using System;
using System.IO;
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
    public string FileName { get; set; }
    public string ViewName { get; set; }
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

    [HttpPost()]
    public async Task<ActionResult> SendForSignature([FromBody] SignRequest body)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var pdf = string.IsNullOrEmpty(body.DerivativeUrn)
            ? await _aps.GetSourcePdf(body.ProjectId, body.VersionId, tokens)
            : await _aps.GetViewPdf(body.VersionId, body.DerivativeUrn, tokens);
        var name = GetBaseName(body.FileName, body.ViewName);
        var transientDocumentId = await _adobeSign.UploadTransientDocument(pdf, name + ".pdf");
        var agreementId = await _adobeSign.CreateAgreement(transientDocumentId, name, body.SignerEmail, body.ItemId);
        return Ok(new { agreementId });
    }

    [HttpGet("{agreementId}")]
    public async Task<ActionResult> GetStatus(string agreementId)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        return Ok(new { status = await _adobeSign.GetAgreementStatus(agreementId) });
    }

    [HttpPost("{agreementId}/upload")]
    public async Task<ActionResult> UploadSignedPdf(string agreementId, [FromBody] UploadRequest body)
    {
        var tokens = await AuthController.PrepareTokens(Request, Response, _aps);
        if (tokens == null)
        {
            return Unauthorized();
        }
        var status = await _adobeSign.GetAgreementStatus(agreementId);
        if (status != "SIGNED")
        {
            return BadRequest($"Agreement is not signed yet (status: {status}).");
        }
        var pdf = await _adobeSign.GetCombinedDocument(agreementId);
        // Timestamp keeps the name unique, so we always create a new item
        var name = $"{GetBaseName(body.FileName, body.ViewName)} - signed {DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf";
        var itemId = await _aps.UploadSignedPdf(body.ProjectId, body.ItemId, name, pdf, tokens);
        return Ok(new { itemId, name });
    }

    private static string GetBaseName(string fileName, string viewName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrEmpty(viewName) ? baseName : $"{baseName} - {viewName}";
    }
}
