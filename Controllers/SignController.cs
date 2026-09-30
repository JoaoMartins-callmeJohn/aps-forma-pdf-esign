using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

public class SignRequest
{
    public string ProjectId { get; set; }
    public string ItemId { get; set; }
    public string VersionId { get; set; }
    public string DerivativeUrn { get; set; } // null for native PDF items
    public string FileName { get; set; }
    public string ViewName { get; set; }
    public string SignerEmail { get; set; }
}

public class UploadRequest
{
    public string ProjectId { get; set; }
    public string ItemId { get; set; }
}

[ApiController]
[Route("api/[controller]")]
public class SignController : ControllerBase
{
    private readonly APS _aps;
    private readonly AdobeSign _adobeSign;

    public SignController(APS aps, AdobeSign adobeSign)
    {
        _aps = aps;
        _adobeSign = adobeSign;
    }

    // Agreements are tagged in Adobe (externalId) with the Autodesk user and the ACC item,
    // so each user can list their own submissions per document without any local storage
    private static string GetTag(string userId, string itemId)
    {
        return $"{userId}:{itemId}";
    }

    [HttpPost()]
    public async Task<ActionResult> SendForSignature([FromBody] SignRequest body)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        var adobeTokens = await AdobeController.PrepareTokens(Request, Response, _adobeSign);
        if (tokens == null || adobeTokens == null)
        {
            return Unauthorized();
        }
        var profile = await _aps.GetUserProfile(tokens);
        var pdf = string.IsNullOrEmpty(body.DerivativeUrn)
            ? await _aps.GetSourcePdf(body.ProjectId, body.VersionId, tokens)
            : await _aps.GetViewPdf(body.VersionId, body.DerivativeUrn, tokens);
        var name = GetBaseName(body.FileName, body.ViewName);
        var transientDocumentId = await _adobeSign.UploadTransientDocument(adobeTokens, pdf, name + ".pdf");
        // The logged-in Autodesk user is CC'd, so Adobe notifies them on send and on completion
        var agreementId = await _adobeSign.CreateAgreement(adobeTokens, transientDocumentId, name, body.SignerEmail, GetTag(profile.Sub, body.ItemId), profile.Email);
        return Ok(new { agreementId });
    }

    // The logged-in user's submissions for an item, newest first
    [HttpGet()]
    public async Task<ActionResult> ListSubmissions([FromQuery] string itemId)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        var adobeTokens = await AdobeController.PrepareTokens(Request, Response, _adobeSign);
        if (tokens == null || adobeTokens == null)
        {
            return Unauthorized();
        }
        var profile = await _aps.GetUserProfile(tokens);
        try
        {
            var agreements = await _adobeSign.ListAgreements(adobeTokens, GetTag(profile.Sub, itemId));
            return Ok(
                from agreement in agreements
                orderby agreement.Date descending
                select new { agreementId = agreement.Id, name = agreement.Name, status = agreement.Status, date = agreement.Date }
            );
        }
        catch (AdobeSignThrottledException ex)
        {
            // Let the client back off instead of failing
            return StatusCode(429, new { retryAfter = ex.RetryAfter });
        }
    }

    [HttpPost("{agreementId}/upload")]
    public async Task<ActionResult> UploadSignedPdf(string agreementId, [FromBody] UploadRequest body)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        var adobeTokens = await AdobeController.PrepareTokens(Request, Response, _adobeSign);
        if (tokens == null || adobeTokens == null)
        {
            return Unauthorized();
        }
        var profile = await _aps.GetUserProfile(tokens);
        var agreement = await _adobeSign.GetAgreement(adobeTokens, agreementId);
        // Only the user who submitted the agreement for this item may save it
        if (agreement.ExternalId != GetTag(profile.Sub, body.ItemId))
        {
            return StatusCode(403, "This agreement was not submitted by you for this document.");
        }
        if (agreement.Status != "SIGNED")
        {
            return BadRequest($"Agreement is not signed yet (status: {agreement.Status}).");
        }
        var pdf = await _adobeSign.GetCombinedDocument(adobeTokens, agreementId);
        // Timestamp keeps the name unique, so we always create a new item
        var name = $"{agreement.Name} - signed {DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf";
        var itemId = await _aps.UploadSignedPdf(body.ProjectId, body.ItemId, name, pdf, tokens);
        return Ok(new { itemId, name });
    }

    private static string GetBaseName(string fileName, string viewName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrEmpty(viewName) ? baseName : $"{baseName} - {viewName}";
    }
}
