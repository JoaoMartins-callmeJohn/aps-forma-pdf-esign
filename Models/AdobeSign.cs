using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public class AdobeAgreement
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Status { get; set; }
    public string Date { get; set; }
    public string ExternalId { get; set; }
}

public class AdobeTokens
{
    public string AccessToken;
    public string RefreshToken;
    public string ApiAccessPoint; // e.g. https://api.na1.adobesign.com/ (depends on the account's shard)
    public DateTime ExpiresAt;
}

// Minimal wrapper around the Adobe Acrobat Sign REST API v6, authenticated with OAuth 2.0 (authorization code).
// See https://secure.adobesign.com/public/docs/restapi/v6
public class AdobeSign
{
    private const string Scopes = "agreement_read:self agreement_write:self agreement_send:self";

    private readonly HttpClient _httpClient = new HttpClient();
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _callbackUri;
    private readonly string _shard;

    // shard: the account's region (na1, na3, eu1, ...), visible in the Acrobat Sign URL, e.g. https://secure.na3.adobesign.com
    public AdobeSign(string clientId, string clientSecret, string callbackUri, string shard)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _callbackUri = callbackUri;
        _shard = shard;
    }

    public string GetAuthorizationURL(string state)
    {
        return $"https://secure.{_shard}.adobesign.com/public/oauth/v2"
            + $"?response_type=code&client_id={Uri.EscapeDataString(_clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(_callbackUri)}"
            + $"&scope={Uri.EscapeDataString(Scopes)}&state={Uri.EscapeDataString(state)}";
    }

    // The callback may include the api_access_point of the user's shard; otherwise derive it from the configured shard
    public async Task<AdobeTokens> GenerateTokens(string code, string apiAccessPoint)
    {
        if (string.IsNullOrEmpty(apiAccessPoint))
        {
            apiAccessPoint = $"https://api.{_shard}.adobesign.com/";
        }
        apiAccessPoint = apiAccessPoint.TrimEnd('/') + "/";
        var json = await PostForm($"{apiAccessPoint}oauth/v2/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["redirect_uri"] = _callbackUri
        });
        return new AdobeTokens
        {
            AccessToken = json.GetProperty("access_token").GetString(),
            RefreshToken = json.GetProperty("refresh_token").GetString(),
            ApiAccessPoint = json.TryGetProperty("api_access_point", out var point) ? point.GetString() : apiAccessPoint,
            ExpiresAt = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32())
        };
    }

    // The refresh response does not include a new refresh token; the existing one stays valid
    public async Task<AdobeTokens> RefreshTokens(AdobeTokens tokens)
    {
        var json = await PostForm($"{tokens.ApiAccessPoint}oauth/v2/refresh", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret
        });
        return new AdobeTokens
        {
            AccessToken = json.GetProperty("access_token").GetString(),
            RefreshToken = tokens.RefreshToken,
            ApiAccessPoint = tokens.ApiAccessPoint,
            ExpiresAt = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32())
        };
    }

    public async Task<string> UploadTransientDocument(AdobeTokens tokens, byte[] content, string fileName)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var form = new MultipartFormDataContent
        {
            { file, "File", fileName },
            { new StringContent(fileName), "File-Name" },
            { new StringContent("application/pdf"), "Mime-Type" }
        };
        using var request = CreateRequest(tokens, HttpMethod.Post, "/transientDocuments");
        request.Content = form;
        var json = await ReadJson(await _httpClient.SendAsync(request));
        return json.GetProperty("transientDocumentId").GetString();
    }

    // externalId tags the agreement so it can be listed later (see ListAgreements).
    // ccEmail gets an email when the agreement is sent and a copy of the signed PDF when it completes.
    public async Task<string> CreateAgreement(AdobeTokens tokens, string transientDocumentId, string name, string signerEmail, string externalId, string ccEmail)
    {
        var addCc = !string.IsNullOrEmpty(ccEmail) && !string.Equals(ccEmail, signerEmail, StringComparison.OrdinalIgnoreCase);
        var payload = new
        {
            fileInfos = new[] { new { transientDocumentId } },
            name,
            participantSetsInfo = new[]
            {
                new { memberInfos = new[] { new { email = signerEmail } }, order = 1, role = "SIGNER" }
            },
            ccs = addCc ? new[] { new { email = ccEmail } } : null,
            signatureType = "ESIGN",
            state = "IN_PROCESS",
            externalId = new { id = externalId }
        };
        using var request = CreateRequest(tokens, HttpMethod.Post, "/agreements");
        request.Content = JsonContent.Create(payload, options: new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        var json = await ReadJson(await _httpClient.SendAsync(request));
        return json.GetProperty("id").GetString();
    }

    public async Task<AdobeAgreement> GetAgreement(AdobeTokens tokens, string agreementId)
    {
        using var request = CreateRequest(tokens, HttpMethod.Get, $"/agreements/{agreementId}");
        var json = await ReadJson(await _httpClient.SendAsync(request));
        return new AdobeAgreement
        {
            Id = agreementId,
            Name = json.GetProperty("name").GetString(),
            Status = json.GetProperty("status").GetString(),
            ExternalId = json.TryGetProperty("externalId", out var externalId) && externalId.TryGetProperty("id", out var id) ? id.GetString() : null
        };
    }

    // Agreements of the connected Adobe user with the given externalId (exact, case-sensitive match).
    // Status here is relative to the caller, e.g. WAITING_FOR_MY_SIGNATURE when the sender is also the signer.
    public async Task<List<AdobeAgreement>> ListAgreements(AdobeTokens tokens, string externalId)
    {
        var agreements = new List<AdobeAgreement>();
        string cursor = null;
        do
        {
            var path = $"/agreements?externalId={Uri.EscapeDataString(externalId)}&pageSize=100"
                + (cursor != null ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
            using var request = CreateRequest(tokens, HttpMethod.Get, path);
            var json = await ReadJson(await _httpClient.SendAsync(request));
            // Adobe omits userAgreementList when nothing matches ({"page":{}})
            if (!json.TryGetProperty("userAgreementList", out var list))
            {
                break;
            }
            foreach (var agreement in list.EnumerateArray())
            {
                agreements.Add(new AdobeAgreement
                {
                    Id = agreement.GetProperty("id").GetString(),
                    Name = agreement.GetProperty("name").GetString(),
                    Status = agreement.GetProperty("status").GetString(),
                    Date = agreement.TryGetProperty("displayDate", out var date) ? date.GetString() : null,
                    ExternalId = externalId
                });
            }
            cursor = json.TryGetProperty("page", out var page) && page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor));
        return agreements;
    }

    // The signed PDF, with the audit report appended
    public async Task<byte[]> GetCombinedDocument(AdobeTokens tokens, string agreementId)
    {
        using var request = CreateRequest(tokens, HttpMethod.Get, $"/agreements/{agreementId}/combinedDocument?attachAuditReport=true");
        var response = await _httpClient.SendAsync(request);
        await EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static HttpRequestMessage CreateRequest(AdobeTokens tokens, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{tokens.ApiAccessPoint.TrimEnd('/')}/api/rest/v6{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return request;
    }

    private async Task<JsonElement> PostForm(string url, Dictionary<string, string> fields)
    {
        return await ReadJson(await _httpClient.PostAsync(url, new FormUrlEncodedContent(fields)));
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        await EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var message = $"Acrobat Sign request failed ({(int)response.StatusCode}): {body}";
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Throttled (e.g. THROTTLING_TOO_FREQUENT_POLLING); the body says how long to wait
                var retryAfter = 60;
                try { retryAfter = JsonDocument.Parse(body).RootElement.GetProperty("retryAfter").GetInt32(); } catch (Exception) { }
                throw new AdobeSignThrottledException(message, retryAfter);
            }
            throw new HttpRequestException(message, null, response.StatusCode);
        }
    }
}

public class AdobeSignThrottledException : HttpRequestException
{
    public int RetryAfter { get; }

    public AdobeSignThrottledException(string message, int retryAfter) : base(message, null, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }
}
