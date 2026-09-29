using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

// Minimal wrapper around the Adobe Acrobat Sign REST API v6, authenticated with an Integration Key.
// See https://secure.adobesign.com/public/docs/restapi/v6
public class AdobeSign
{
    private readonly HttpClient _httpClient = new HttpClient();
    private string _apiBase;

    public AdobeSign(string integrationKey, string apiBase)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", integrationKey);
        _apiBase = apiBase?.TrimEnd('/');
    }

    // The API base URL depends on the account's shard (na1, eu1, ...), so resolve it unless configured
    private async Task<string> GetApiBase()
    {
        if (string.IsNullOrEmpty(_apiBase))
        {
            var uris = await _httpClient.GetFromJsonAsync<JsonElement>("https://api.adobesign.com/api/rest/v6/baseUris");
            _apiBase = uris.GetProperty("apiAccessPoint").GetString().TrimEnd('/') + "/api/rest/v6";
        }
        return _apiBase;
    }

    public async Task<string> UploadTransientDocument(byte[] content, string fileName)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var form = new MultipartFormDataContent
        {
            { file, "File", fileName },
            { new StringContent(fileName), "File-Name" },
            { new StringContent("application/pdf"), "Mime-Type" }
        };
        var response = await _httpClient.PostAsync($"{await GetApiBase()}/transientDocuments", form);
        var json = await ReadJson(response);
        return json.GetProperty("transientDocumentId").GetString();
    }

    public async Task<string> CreateAgreement(string transientDocumentId, string name, string signerEmail, string externalId)
    {
        var payload = new
        {
            fileInfos = new[] { new { transientDocumentId } },
            name,
            participantSetsInfo = new[]
            {
                new { memberInfos = new[] { new { email = signerEmail } }, order = 1, role = "SIGNER" }
            },
            signatureType = "ESIGN",
            state = "IN_PROCESS",
            externalId = new { id = externalId }
        };
        var response = await _httpClient.PostAsJsonAsync($"{await GetApiBase()}/agreements", payload);
        var json = await ReadJson(response);
        return json.GetProperty("id").GetString();
    }

    public async Task<string> GetAgreementStatus(string agreementId)
    {
        var response = await _httpClient.GetAsync($"{await GetApiBase()}/agreements/{agreementId}");
        var json = await ReadJson(response);
        return json.GetProperty("status").GetString();
    }

    // The signed PDF, with the audit report appended
    public async Task<byte[]> GetCombinedDocument(string agreementId)
    {
        var response = await _httpClient.GetAsync($"{await GetApiBase()}/agreements/{agreementId}/combinedDocument?attachAuditReport=true");
        await EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync();
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
            throw new HttpRequestException($"Acrobat Sign request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        }
    }
}
