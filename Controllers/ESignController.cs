using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

// OAuth with the configured e-sign provider, mirroring AuthController (tokens are kept in cookies)
[ApiController]
[Route("api/[controller]")]
public class ESignController : ControllerBase
{
    private readonly IESignProvider _provider;

    public ESignController(IESignProvider provider)
    {
        _provider = provider;
    }

    public static async Task<ESignTokens> PrepareTokens(HttpRequest request, HttpResponse response, IESignProvider provider)
    {
        // Tokens from another provider (e.g. after switching ESIGN_PROVIDER) mean "not connected"
        if (!request.Cookies.ContainsKey("esign_refresh_token") || request.Cookies["esign_provider"] != provider.Name)
        {
            return null;
        }
        var tokens = new ESignTokens
        {
            AccessToken = request.Cookies["esign_access_token"],
            RefreshToken = request.Cookies["esign_refresh_token"],
            BaseUri = request.Cookies["esign_base_uri"],
            AccountId = request.Cookies["esign_account_id"],
            ExpiresAt = DateTime.Parse(request.Cookies["esign_expires_at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
        if (tokens.ExpiresAt < DateTime.UtcNow)
        {
            // Docusign also returns a new refresh token here, so all cookies are rewritten
            tokens = await provider.RefreshTokens(tokens);
            SetTokenCookies(response, provider, tokens);
        }
        return tokens;
    }

    private static void SetTokenCookies(HttpResponse response, IESignProvider provider, ESignTokens tokens)
    {
        response.Cookies.Append("esign_provider", provider.Name);
        response.Cookies.Append("esign_access_token", tokens.AccessToken);
        response.Cookies.Append("esign_refresh_token", tokens.RefreshToken);
        response.Cookies.Append("esign_base_uri", tokens.BaseUri);
        response.Cookies.Append("esign_account_id", tokens.AccountId ?? "");
        response.Cookies.Append("esign_expires_at", tokens.ExpiresAt.ToString("o"));
    }

    [HttpGet("login")]
    public ActionResult Login()
    {
        // Random state, checked in the callback to prevent CSRF
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append("esign_state", state);
        return Redirect(_provider.GetAuthorizationURL(state));
    }

    [HttpGet("logout")]
    public ActionResult Logout()
    {
        Response.Cookies.Delete("esign_provider");
        Response.Cookies.Delete("esign_access_token");
        Response.Cookies.Delete("esign_refresh_token");
        Response.Cookies.Delete("esign_base_uri");
        Response.Cookies.Delete("esign_account_id");
        Response.Cookies.Delete("esign_expires_at");
        return Redirect("/");
    }

    [HttpGet("callback")]
    public async Task<ActionResult> Callback(string code, string state, string error, string error_description)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return BadRequest($"{_provider.Name} authorization failed: {error} {error_description}");
        }
        if (string.IsNullOrEmpty(state) || state != Request.Cookies["esign_state"])
        {
            return BadRequest("Invalid OAuth state.");
        }
        Response.Cookies.Delete("esign_state");
        var query = Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString());
        var tokens = await _provider.GenerateTokens(code, query);
        SetTokenCookies(Response, _provider, tokens);
        return Redirect("/");
    }

    [HttpGet("status")]
    public async Task<ActionResult> GetStatus()
    {
        bool connected;
        try
        {
            connected = await PrepareTokens(Request, Response, _provider) != null;
        }
        catch (Exception)
        {
            // e.g. the refresh token expired; the user has to connect again
            connected = false;
        }
        return Ok(new { connected, provider = _provider.Name, pollingInterval = _provider.PollingInterval });
    }
}
