using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.DataManagement;
using Autodesk.DataManagement.Model;

public partial class APS
{
    public async Task<IEnumerable<HubData>> GetHubs(Tokens tokens)
    {
        var dataManagementClient = new DataManagementClient();
        var hubs = await dataManagementClient.GetHubsAsync(accessToken: tokens.InternalToken);
        return hubs.Data;
    }

    public async Task<IEnumerable<ProjectData>> GetProjects(string hubId, Tokens tokens)
    {
        var dataManagementClient = new DataManagementClient();
        var projects = await dataManagementClient.GetHubProjectsAsync(hubId, accessToken: tokens.InternalToken);
        return projects.Data;
    }

    public async Task<IEnumerable<TopFolderData>> GetTopFolders(string hubId, string projectId, Tokens tokens)
    {
        var dataManagementClient = new DataManagementClient();
        var folders = await dataManagementClient.GetProjectTopFoldersAsync(hubId, projectId, accessToken: tokens.InternalToken);
        return folders.Data;
    }

    // All pages of the folder contents, plus the tip version of each item (by item ID)
    public async Task<(List<IFolderContentsData> Contents, Dictionary<string, VersionData> TipVersions)> GetFolderContents(string projectId, string folderId, Tokens tokens)
    {
        var dataManagementClient = new DataManagementClient();
        var contents = new List<IFolderContentsData>();
        var tipVersions = new Dictionary<string, VersionData>();
        for (var page = 0; ; page++)
        {
            var response = await dataManagementClient.GetFolderContentsAsync(projectId, folderId, pageNumber: page, pageLimit: 200, accessToken: tokens.InternalToken);
            contents.AddRange(response.Data);
            foreach (var version in response.Included ?? [])
            {
                tipVersions[version.Relationships.Item.Data.Id] = version;
            }
            if (response.Links?.Next == null || response.Data.Count == 0)
            {
                break;
            }
        }
        return (contents, tipVersions);
    }

    public async Task<IEnumerable<VersionData>> GetVersions(string projectId, string itemId, Tokens tokens)
    {
        var dataManagementClient = new DataManagementClient();
        var versions = await dataManagementClient.GetItemVersionsAsync(projectId, itemId, accessToken: tokens.InternalToken);
        return versions.Data;
    }
}
