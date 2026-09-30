using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.DataManagement;
using Autodesk.DataManagement.Model;
using Autodesk.Oss;

public partial class APS
{
    // Cookies are handled manually (CloudFront signed cookies for derivative downloads)
    private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler { UseCookies = false });

    // URL-safe base64 of a version ID, without padding (the Model Derivative URN)
    public static string ToUrn(string id)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Downloads the PDF derivative of a 2D view (the "pdf-page" child of the viewed node).
    // See https://aps.autodesk.com/blog/download-your-revit-2d-views-pdfs
    public async Task<byte[]> GetViewPdf(string versionId, string derivativeUrn, Tokens tokens)
    {
        var url = $"https://developer.api.autodesk.com/modelderivative/v2/designdata/{ToUrn(versionId)}/manifest/{Uri.EscapeDataString(derivativeUrn)}/signedcookies";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.InternalToken);
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var downloadUrl = json.RootElement.GetProperty("url").GetString();

        // The CloudFront-* cookies authorize the download from the CDN
        var cookies = response.Headers.GetValues("Set-Cookie")
            .Select(header => header.Split(';')[0])
            .Where(cookie => cookie.StartsWith("CloudFront-"));
        using var download = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        download.Headers.Add("Cookie", string.Join("; ", cookies));
        using var downloadResponse = await _httpClient.SendAsync(download);
        downloadResponse.EnsureSuccessStatusCode();
        return await downloadResponse.Content.ReadAsByteArrayAsync();
    }

    // Downloads the source file of a version (used for native PDF items)
    public async Task<byte[]> GetSourcePdf(string projectId, string versionId, Tokens tokens)
    {
        var dataManagementClient = new DataManagementClient();
        var version = await dataManagementClient.GetVersionAsync(projectId, versionId, accessToken: tokens.InternalToken);
        var (bucketKey, objectKey) = ParseStorageId(version.Data.Relationships.Storage.Data.Id);
        var ossClient = new OssClient();
        using var stream = await ossClient.DownloadObjectAsync(bucketKey, objectKey, accessToken: tokens.InternalToken);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    // Uploads a PDF as a new item in the same folder as the given item.
    // Returns the item ID and the file name actually used.
    public async Task<(string ItemId, string FileName)> UploadSignedPdf(string projectId, string itemId, string fileName, byte[] content, Tokens tokens)
    {
        fileName = GetValidFileName(fileName);
        var dataManagementClient = new DataManagementClient();
        var parent = await dataManagementClient.GetItemParentFolderAsync(projectId, itemId, accessToken: tokens.InternalToken);
        var folderId = parent.Data.Id;

        var storage = await dataManagementClient.CreateStorageAsync(projectId, new StoragePayload
        {
            Jsonapi = new JsonApiVersion { VarVersion = JsonApiVersionValue._10 },
            Data = new StoragePayloadData
            {
                Type = TypeObject.Objects,
                Attributes = new StoragePayloadDataAttributes { Name = fileName },
                Relationships = new StoragePayloadDataRelationships
                {
                    Target = new StoragePayloadDataRelationshipsTarget
                    {
                        Data = new StoragePayloadDataRelationshipsTargetData { Type = TypeFolderItemsForStorage.Folders, Id = folderId }
                    }
                }
            }
        }, accessToken: tokens.InternalToken);

        var (bucketKey, objectKey) = ParseStorageId(storage.Data.Id);
        var ossClient = new OssClient();
        using (var stream = new MemoryStream(content))
        {
            await ossClient.UploadObjectAsync(bucketKey, objectKey, stream, accessToken: tokens.InternalToken);
        }

        var item = await dataManagementClient.CreateItemAsync(projectId, new ItemPayload
        {
            Jsonapi = new JsonApiVersion { VarVersion = JsonApiVersionValue._10 },
            Data = new ItemPayloadData
            {
                Type = TypeItem.Items,
                Attributes = new ItemPayloadDataAttributes
                {
                    DisplayName = fileName,
                    Extension = new ItemPayloadDataAttributesExtension { Type = "items:autodesk.bim360:File", VarVersion = "1.0" }
                },
                Relationships = new ItemPayloadDataRelationships
                {
                    Tip = new ItemPayloadDataRelationshipsTip
                    {
                        Data = new ItemPayloadDataRelationshipsTipData { Type = TypeVersion.Versions, Id = "1" }
                    },
                    Parent = new ItemPayloadDataRelationshipsParent
                    {
                        Data = new ItemPayloadDataRelationshipsParentData { Type = TypeFolder.Folders, Id = folderId }
                    }
                }
            },
            Included =
            [
                new ItemPayloadIncluded
                {
                    Type = TypeVersion.Versions,
                    Id = "1",
                    Attributes = new ItemPayloadIncludedAttributes
                    {
                        Name = fileName,
                        Extension = new ItemPayloadIncludedAttributesExtension { Type = "versions:autodesk.bim360:File", VarVersion = "1.0" }
                    },
                    Relationships = new ItemPayloadIncludedRelationships
                    {
                        Storage = new ItemPayloadIncludedRelationshipsStorage
                        {
                            Data = new ItemPayloadIncludedRelationshipsStorageData { Type = TypeObject.Objects, Id = storage.Data.Id }
                        }
                    }
                }
            ]
        }, accessToken: tokens.InternalToken);
        return (item.Data.Id, fileName);
    }

    // ACC rejects file names with \ / : * ? " < > | or control characters ("Invalid characters in file name"),
    // which view and sheet names often contain
    private static string GetValidFileName(string fileName)
    {
        var chars = fileName.Select(c => "\\/:*?\"<>|".Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    // "urn:adsk.objects:os.object:{bucketKey}/{objectKey}"
    private static (string, string) ParseStorageId(string storageId)
    {
        var path = storageId.Substring(storageId.LastIndexOf(':') + 1);
        var slash = path.IndexOf('/');
        return (path.Substring(0, slash), path.Substring(slash + 1));
    }
}
