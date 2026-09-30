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
    public string SignerName { get; set; } // optional; Docusign falls back to the email
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
    private readonly IESignProvider _provider;

    public SignController(APS aps, IESignProvider provider)
    {
        _aps = aps;
        _provider = provider;
    }

    // Submissions are tagged in the e-sign service with the Autodesk user and the ACC item,
    // so each user can list their own submissions per document without any local storage
    private static string GetTag(string userId, string itemId)
    {
        return $"{userId}:{itemId}";
    }

    [HttpPost()]
    public async Task<ActionResult> SendForSignature([FromBody] SignRequest body)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        var esignTokens = await ESignController.PrepareTokens(Request, Response, _provider);
        if (tokens == null || esignTokens == null)
        {
            return Unauthorized();
        }
        var profile = await _aps.GetUserProfile(tokens);
        var pdf = string.IsNullOrEmpty(body.DerivativeUrn)
            ? await _aps.GetSourcePdf(body.ProjectId, body.VersionId, tokens)
            : await _aps.GetViewPdf(body.VersionId, body.DerivativeUrn, tokens);
        var name = GetBaseName(body.FileName, body.ViewName);
        // The logged-in Autodesk user is CC'd, so they are notified on send and on completion
        var agreementId = await _provider.SendForSignature(esignTokens, pdf, name, body.SignerEmail, body.SignerName, GetTag(profile.Sub, body.ItemId), profile.Email);
        return Ok(new { agreementId });
    }

    // The logged-in user's submissions for an item, newest first
    [HttpGet()]
    public async Task<ActionResult> ListSubmissions([FromQuery] string itemId)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        var esignTokens = await ESignController.PrepareTokens(Request, Response, _provider);
        if (tokens == null || esignTokens == null)
        {
            return Unauthorized();
        }
        var profile = await _aps.GetUserProfile(tokens);
        try
        {
            var submissions = await _provider.ListSubmissions(esignTokens, GetTag(profile.Sub, itemId));
            return Ok(
                from submission in submissions
                orderby submission.Date descending
                select new
                {
                    agreementId = submission.Id,
                    name = submission.Name,
                    status = submission.Status,
                    date = submission.Date,
                    isSigned = submission.IsSigned,
                    isFinal = submission.IsFinal
                }
            );
        }
        catch (ESignThrottledException ex)
        {
            // Let the client back off instead of failing
            return StatusCode(429, new { retryAfter = ex.RetryAfter });
        }
    }

    [HttpPost("{agreementId}/upload")]
    public async Task<ActionResult> UploadSignedPdf(string agreementId, [FromBody] UploadRequest body)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        var esignTokens = await ESignController.PrepareTokens(Request, Response, _provider);
        if (tokens == null || esignTokens == null)
        {
            return Unauthorized();
        }
        var (submission, error) = await GetSignedSubmission(tokens, esignTokens, agreementId, body.ItemId);
        if (error != null)
        {
            return error;
        }
        var pdf = await _provider.GetSignedPdf(esignTokens, agreementId);
        // Timestamp keeps the name unique, so we always create a new item
        var name = $"{submission.Name} - signed {DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf";
        var (itemId, fileName) = await _aps.UploadSignedPdf(body.ProjectId, body.ItemId, name, pdf, tokens);
        return Ok(new { itemId, name = fileName });
    }

    // The signed PDF as a file download
    [HttpGet("{agreementId}/download")]
    public async Task<ActionResult> DownloadSignedPdf(string agreementId, [FromQuery] string itemId)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        var esignTokens = await ESignController.PrepareTokens(Request, Response, _provider);
        if (tokens == null || esignTokens == null)
        {
            return Unauthorized();
        }
        var (submission, error) = await GetSignedSubmission(tokens, esignTokens, agreementId, itemId);
        if (error != null)
        {
            return error;
        }
        var pdf = await _provider.GetSignedPdf(esignTokens, agreementId);
        return File(pdf, "application/pdf", $"{submission.Name} - signed.pdf");
    }

    // Only the user who submitted it for this item may get the signed PDF, and only once it's signed
    private async Task<(Submission, ActionResult)> GetSignedSubmission(Tokens tokens, ESignTokens esignTokens, string agreementId, string itemId)
    {
        var profile = await _aps.GetUserProfile(tokens);
        var submission = await _provider.GetSubmission(esignTokens, agreementId);
        if (submission.Tag != GetTag(profile.Sub, itemId))
        {
            return (null, StatusCode(403, "This agreement was not submitted by you for this document."));
        }
        if (!submission.IsSigned)
        {
            return (null, BadRequest($"Agreement is not signed yet (status: {submission.Status})."));
        }
        return (submission, null);
    }

    private static string GetBaseName(string fileName, string viewName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrEmpty(viewName) ? baseName : $"{baseName} - {viewName}";
    }
}
