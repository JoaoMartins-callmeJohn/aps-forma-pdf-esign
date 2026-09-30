using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// Minimal wrapper around the Docusign eSignature REST API v2.1, authenticated with OAuth 2.0 (confidential authorization code grant).
// See https://developers.docusign.com/docs/esign-rest-api/reference/
public class Docusign : IESignProvider
{
    // Hidden envelope custom field holding the tag
    private const string TagField = "apsTag";
    // No further status changes expected
    private static readonly string[] FinalStatuses = { "completed", "declined", "voided" };

    private readonly HttpClient _httpClient = new HttpClient();
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _callbackUri;
    private readonly string _authServer;

    // authServer: account-d.docusign.com for developer (demo) accounts, account.docusign.com for production
    public Docusign(string clientId, string clientSecret, string callbackUri, string authServer)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _callbackUri = callbackUri;
        _authServer = authServer;
    }

    public string Name => "Docusign";

    // Docusign allows a GET on the same URL at most once every 15 minutes
    // See https://developers.docusign.com/platform/api-guidelines/
    public int PollingInterval => 900;

    public string GetAuthorizationURL(string state)
    {
        return $"https://{_authServer}/oauth/auth"
            + $"?response_type=code&scope=signature&client_id={Uri.EscapeDataString(_clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(_callbackUri)}&state={Uri.EscapeDataString(state)}";
    }

    // The API base URL and account come from the user info of the new token (the user's default account)
    public async Task<ESignTokens> GenerateTokens(string code, IDictionary<string, string> callbackQuery)
    {
        var json = await PostToken(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code
        });
        var tokens = ReadTokens(json);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{_authServer}/oauth/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var userInfo = await ReadJson(await _httpClient.SendAsync(request));
        var accounts = userInfo.GetProperty("accounts").EnumerateArray().ToList();
        var account = accounts.FirstOrDefault(a => a.TryGetProperty("is_default", out var isDefault) && isDefault.ValueKind == JsonValueKind.True);
        if (account.ValueKind == JsonValueKind.Undefined)
        {
            account = accounts.First();
        }
        tokens.AccountId = account.GetProperty("account_id").GetString();
        tokens.BaseUri = account.GetProperty("base_uri").GetString();
        return tokens;
    }

    // Unlike Adobe, the refresh response includes a new refresh token, which replaces the old one
    public async Task<ESignTokens> RefreshTokens(ESignTokens tokens)
    {
        var json = await PostToken(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken
        });
        var refreshed = ReadTokens(json);
        refreshed.RefreshToken ??= tokens.RefreshToken;
        refreshed.AccountId = tokens.AccountId;
        refreshed.BaseUri = tokens.BaseUri;
        return refreshed;
    }

    // Creates and sends the envelope in one call, with the PDF inline. The tag is stored in a hidden custom field.
    // Without signature fields, the signer places them (free-form signing).
    public async Task<string> SendForSignature(ESignTokens tokens, byte[] pdf, string name, string signerEmail, string signerName, string tag, string ccEmail)
    {
        var addCc = !string.IsNullOrEmpty(ccEmail) && !string.Equals(ccEmail, signerEmail, StringComparison.OrdinalIgnoreCase);
        var payload = new
        {
            // Docusign limits the subject to 100 characters
            emailSubject = name.Length > 100 ? name.Substring(0, 100) : name,
            documents = new[]
            {
                new { documentBase64 = Convert.ToBase64String(pdf), name, fileExtension = "pdf", documentId = "1" }
            },
            recipients = new
            {
                signers = new[]
                {
                    new { email = signerEmail, name = string.IsNullOrEmpty(signerName) ? signerEmail : signerName, recipientId = "1", routingOrder = "1" }
                },
                carbonCopies = addCc
                    ? new[] { new { email = ccEmail, name = ccEmail, recipientId = "2", routingOrder = "2" } }
                    : Array.Empty<object>()
            },
            customFields = new
            {
                textCustomFields = new[] { new { name = TagField, value = tag, show = "false", required = "false" } }
            },
            status = "sent"
        };
        using var request = CreateRequest(tokens, HttpMethod.Post, "/envelopes");
        request.Content = JsonContent.Create(payload);
        var json = await ReadJson(await _httpClient.SendAsync(request));
        return json.GetProperty("envelopeId").GetString();
    }

    public async Task<Submission> GetSubmission(ESignTokens tokens, string id)
    {
        using var request = CreateRequest(tokens, HttpMethod.Get, $"/envelopes/{id}?include=custom_fields");
        return CreateSubmission(await ReadJson(await _httpClient.SendAsync(request)));
    }

    // Envelopes of the connected user from the last year with the given tag.
    // from_date is required; it is kept to the day so the URL stays the same between polls.
    public async Task<List<Submission>> ListSubmissions(ESignTokens tokens, string tag)
    {
        var submissions = new List<Submission>();
        var fromDate = DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-dd");
        var start = 0;
        while (true)
        {
            var path = $"/envelopes?from_date={fromDate}&custom_field={Uri.EscapeDataString($"{TagField}={tag}")}"
                + $"&include=custom_fields&count=100&start_position={start}";
            using var request = CreateRequest(tokens, HttpMethod.Get, path);
            var json = await ReadJson(await _httpClient.SendAsync(request));
            // Docusign omits envelopes when nothing matches
            if (!json.TryGetProperty("envelopes", out var list))
            {
                break;
            }
            var count = 0;
            foreach (var envelope in list.EnumerateArray())
            {
                count++;
                var submission = CreateSubmission(envelope);
                // The custom_field filter also allows partial matches, so check the exact tag
                if (submission.Tag == tag)
                {
                    submissions.Add(submission);
                }
            }
            start += count;
            // Docusign returns the counts as strings
            var total = json.TryGetProperty("totalSetSize", out var size) && int.TryParse(size.ToString(), out var value) ? value : 0;
            if (count == 0 || start >= total)
            {
                break;
            }
        }
        return submissions;
    }

    public async Task<byte[]> GetSignedPdf(ESignTokens tokens, string id)
    {
        using var request = CreateRequest(tokens, HttpMethod.Get, $"/envelopes/{id}/documents/combined?certificate=true");
        var response = await _httpClient.SendAsync(request);
        await EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static Submission CreateSubmission(JsonElement envelope)
    {
        var status = envelope.GetProperty("status").GetString();
        string tag = null;
        if (envelope.TryGetProperty("customFields", out var customFields) && customFields.TryGetProperty("textCustomFields", out var fields))
        {
            tag = fields.EnumerateArray()
                .Where(f => f.GetProperty("name").GetString() == TagField)
                .Select(f => f.GetProperty("value").GetString())
                .FirstOrDefault();
        }
        return new Submission
        {
            Id = envelope.GetProperty("envelopeId").GetString(),
            Name = envelope.GetProperty("emailSubject").GetString(),
            Status = status,
            Date = envelope.TryGetProperty("statusChangedDateTime", out var date) ? date.GetString() : null,
            Tag = tag,
            IsSigned = status == "completed",
            IsFinal = FinalStatuses.Contains(status)
        };
    }

    private async Task<JsonElement> PostToken(Dictionary<string, string> fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_authServer}/oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}")));
        request.Content = new FormUrlEncodedContent(fields);
        return await ReadJson(await _httpClient.SendAsync(request));
    }

    private static ESignTokens ReadTokens(JsonElement json)
    {
        return new ESignTokens
        {
            AccessToken = json.GetProperty("access_token").GetString(),
            RefreshToken = json.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            ExpiresAt = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32())
        };
    }

    private static HttpRequestMessage CreateRequest(ESignTokens tokens, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{tokens.BaseUri.TrimEnd('/')}/restapi/v2.1/accounts/{tokens.AccountId}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return request;
    }

    private async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        await EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var message = $"Docusign request failed ({(int)response.StatusCode}, {response.RequestMessage?.RequestUri}): {body}";
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Rate or polling limit; X-RateLimit-Reset is the Unix time when calls are allowed again
                var retryAfter = PollingInterval;
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.First(), out var reset))
                {
                    retryAfter = (int)Math.Max(1, reset - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                throw new ESignThrottledException(message, retryAfter);
            }
            throw new HttpRequestException(message, null, response.StatusCode);
        }
    }
}
