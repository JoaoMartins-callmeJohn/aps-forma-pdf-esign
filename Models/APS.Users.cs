using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

public class ProjectUser
{
    public string Name { get; set; }
    public string Email { get; set; }
}

public partial class APS
{
    // Active users of an ACC/BIM 360 project, sorted by name (ACC Admin API, requires account:read).
    // See https://aps.autodesk.com/en/docs/acc/v1/reference/http/admin-projectsprojectId-users-GET/
    public async Task<List<ProjectUser>> GetProjectUsers(string projectId, Tokens tokens)
    {
        // The Admin API expects the project ID without the Data Management "b." prefix
        var id = projectId.StartsWith("b.") ? projectId.Substring(2) : projectId;
        var users = new List<ProjectUser>();
        const int limit = 200;
        for (var offset = 0; ; offset += limit)
        {
            var url = $"https://developer.api.autodesk.com/construction/admin/v1/projects/{id}/users"
                + $"?fields=name,email&filter[status]=active&sort=name&limit={limit}&offset={offset}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.InternalToken);
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Could not list project users ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}", null, response.StatusCode);
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var user in json.RootElement.GetProperty("results").EnumerateArray())
            {
                users.Add(new ProjectUser
                {
                    Name = user.TryGetProperty("name", out var name) ? name.GetString() : null,
                    Email = user.TryGetProperty("email", out var email) ? email.GetString() : null
                });
            }
            if (offset + limit >= json.RootElement.GetProperty("pagination").GetProperty("totalResults").GetInt32())
            {
                break;
            }
        }
        return users;
    }
}
