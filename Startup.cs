using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers();
        var clientID = Configuration["APS_CLIENT_ID"];
        var clientSecret = Configuration["APS_CLIENT_SECRET"];
        var callbackURL = Configuration["APS_CALLBACK_URL"];
        if (string.IsNullOrEmpty(clientID) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(callbackURL))
        {
            throw new ApplicationException("Missing required environment variables APS_CLIENT_ID, APS_CLIENT_SECRET, or APS_CALLBACK_URL.");
        }
        services.AddSingleton(new APS(clientID, clientSecret, callbackURL));
        services.AddSingleton(CreateESignProvider());
    }

    // The e-sign service is chosen per deployment with ESIGN_PROVIDER (adobe or docusign)
    private IESignProvider CreateESignProvider()
    {
        var provider = (Configuration["ESIGN_PROVIDER"] ?? "adobe").ToLowerInvariant();
        var esignCallbackURL = Configuration["ESIGN_CALLBACK_URL"];
        switch (provider)
        {
            case "adobe":
                var adobeClientID = Configuration["ADOBE_SIGN_CLIENT_ID"];
                var adobeClientSecret = Configuration["ADOBE_SIGN_CLIENT_SECRET"];
                var adobeShard = Configuration["ADOBE_SIGN_SHARD"] ?? "na1";
                if (string.IsNullOrEmpty(adobeClientID) || string.IsNullOrEmpty(adobeClientSecret) || string.IsNullOrEmpty(esignCallbackURL))
                {
                    throw new ApplicationException("Missing required environment variables ADOBE_SIGN_CLIENT_ID, ADOBE_SIGN_CLIENT_SECRET, or ESIGN_CALLBACK_URL.");
                }
                return new AdobeSign(adobeClientID, adobeClientSecret, esignCallbackURL, adobeShard);
            case "docusign":
                var docusignClientID = Configuration["DOCUSIGN_CLIENT_ID"];
                var docusignClientSecret = Configuration["DOCUSIGN_CLIENT_SECRET"];
                var docusignAuthServer = Configuration["DOCUSIGN_AUTH_SERVER"] ?? "account-d.docusign.com";
                if (string.IsNullOrEmpty(docusignClientID) || string.IsNullOrEmpty(docusignClientSecret) || string.IsNullOrEmpty(esignCallbackURL))
                {
                    throw new ApplicationException("Missing required environment variables DOCUSIGN_CLIENT_ID, DOCUSIGN_CLIENT_SECRET, or ESIGN_CALLBACK_URL.");
                }
                return new Docusign(docusignClientID, docusignClientSecret, esignCallbackURL, docusignAuthServer);
            default:
                throw new ApplicationException($"Unknown ESIGN_PROVIDER '{provider}'. Use adobe or docusign.");
        }
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }
}
