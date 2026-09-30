using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

// Adobe Acrobat Sign OAuth, mirroring AuthController (tokens are kept in cookies)
[ApiController]
[Route("api/[controller]")]
public class AdobeController : ControllerBase
{
    private readonly AdobeSign _adobeSign;

    public AdobeController(AdobeSign adobeSign)
    {
        _adobeSign = adobeSign;
    }

    public static async Task<AdobeTokens> PrepareTokens(HttpRequest request, HttpResponse response, AdobeSign adobeSign)
    {
        if (!request.Cookies.ContainsKey("adobe_refresh_token"))
        {
            return null;
        }
        var tokens = new AdobeTokens
        {
            AccessToken = request.Cookies["adobe_access_token"],
            RefreshToken = request.Cookies["adobe_refresh_token"],
            ApiAccessPoint = request.Cookies["adobe_api_access_point"],
            ExpiresAt = DateTime.Parse(request.Cookies["adobe_expires_at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
        if (tokens.ExpiresAt < DateTime.UtcNow)
        {
            tokens = await adobeSign.RefreshTokens(tokens);
            SetTokenCookies(response, tokens);
        }
        return tokens;
    }

    private static void SetTokenCookies(HttpResponse response, AdobeTokens tokens)
    {
        response.Cookies.Append("adobe_access_token", tokens.AccessToken);
        response.Cookies.Append("adobe_refresh_token", tokens.RefreshToken);
        response.Cookies.Append("adobe_api_access_point", tokens.ApiAccessPoint);
        response.Cookies.Append("adobe_expires_at", tokens.ExpiresAt.ToString("o"));
    }

    [HttpGet("login")]
    public ActionResult Login()
    {
        // Random state, checked in the callback to prevent CSRF
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append("adobe_state", state);
        return Redirect(_adobeSign.GetAuthorizationURL(state));
    }

    [HttpGet("logout")]
    public ActionResult Logout()
    {
        Response.Cookies.Delete("adobe_access_token");
        Response.Cookies.Delete("adobe_refresh_token");
        Response.Cookies.Delete("adobe_api_access_point");
        Response.Cookies.Delete("adobe_expires_at");
        return Redirect("/");
    }

    [HttpGet("callback")]
    public async Task<ActionResult> Callback(string code, string state, string api_access_point, string error, string error_description)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return BadRequest($"Adobe Sign authorization failed: {error} {error_description}");
        }
        if (string.IsNullOrEmpty(state) || state != Request.Cookies["adobe_state"])
        {
            return BadRequest("Invalid OAuth state.");
        }
        Response.Cookies.Delete("adobe_state");
        var tokens = await _adobeSign.GenerateTokens(code, api_access_point);
        SetTokenCookies(Response, tokens);
        return Redirect("/");
    }

    [HttpGet("status")]
    public async Task<ActionResult> GetStatus()
    {
        try
        {
            var tokens = await PrepareTokens(Request, Response, _adobeSign);
            return Ok(new { connected = tokens != null });
        }
        catch (Exception)
        {
            // e.g. the refresh token expired; the user has to connect again
            return Ok(new { connected = false });
        }
    }
}
