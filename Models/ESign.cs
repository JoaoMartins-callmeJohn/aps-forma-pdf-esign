using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

public class ESignTokens
{
    public string AccessToken;
    public string RefreshToken;
    public string BaseUri;   // API base URL of the user's account (Adobe api_access_point, Docusign base_uri)
    public string AccountId; // Docusign only
    public DateTime ExpiresAt;
}

// An agreement (Adobe) or envelope (Docusign) sent from the app
public class Submission
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Status { get; set; } // provider's own status, shown as is
    public string Date { get; set; }
    public string Tag { get; set; }
    public bool IsSigned { get; set; } // the signed PDF can be downloaded
    public bool IsFinal { get; set; }  // no further status changes expected
}

// An e-sign service the app sends PDFs to, authenticated with OAuth 2.0 (authorization code)
public interface IESignProvider
{
    string Name { get; }
    // Seconds between status checks the provider tolerates
    int PollingInterval { get; }

    string GetAuthorizationURL(string state);
    // callbackQuery: all query parameters of the OAuth callback, since some providers add their own
    Task<ESignTokens> GenerateTokens(string code, IDictionary<string, string> callbackQuery);
    Task<ESignTokens> RefreshTokens(ESignTokens tokens);

    // tag is stored on the submission so it can be listed later (see ListSubmissions).
    // ccEmail gets an email when it is sent and a copy of the signed PDF when it completes.
    Task<string> SendForSignature(ESignTokens tokens, byte[] pdf, string name, string signerEmail, string signerName, string tag, string ccEmail);
    // Submissions of the connected user with the given tag (exact match)
    Task<List<Submission>> ListSubmissions(ESignTokens tokens, string tag);
    Task<Submission> GetSubmission(ESignTokens tokens, string id);
    // The signed PDF, with the audit report / certificate appended
    Task<byte[]> GetSignedPdf(ESignTokens tokens, string id);
}

public class ESignThrottledException : HttpRequestException
{
    public int RetryAfter { get; }

    public ESignThrottledException(string message, int retryAfter) : base(message, null, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }
}
