using System;
using Autodesk.Authentication.Model;
using System.Collections.Generic;

public class Tokens
{
    public string InternalToken;
    public string PublicToken;
    public string RefreshToken;
    public DateTime ExpiresAt;
}

public partial class APS
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _callbackUri;
    // data:write and data:create are needed to upload the signed PDF back to the project,
    // account:read to list the project users (signer dropdown)
    private readonly List<Scopes> InternalTokenScopes = [Scopes.DataRead, Scopes.DataWrite, Scopes.DataCreate, Scopes.ViewablesRead, Scopes.AccountRead];
    private readonly List<Scopes> PublicTokenScopes = [Scopes.ViewablesRead];

    public APS(string clientId, string clientSecret, string callbackUri)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _callbackUri = callbackUri;
    }
}
