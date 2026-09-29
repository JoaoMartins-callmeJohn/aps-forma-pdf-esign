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
        var adobeSignKey = Configuration["ADOBE_SIGN_INTEGRATION_KEY"];
        var adobeSignApiBase = Configuration["ADOBE_SIGN_API_BASE"];
        if (string.IsNullOrEmpty(clientID) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(callbackURL))
        {
            throw new ApplicationException("Missing required environment variables APS_CLIENT_ID, APS_CLIENT_SECRET, or APS_CALLBACK_URL.");
        }
        if (string.IsNullOrEmpty(adobeSignKey))
        {
            throw new ApplicationException("Missing required environment variable ADOBE_SIGN_INTEGRATION_KEY.");
        }
        services.AddSingleton(new APS(clientID, clientSecret, callbackURL));
        services.AddSingleton(new AdobeSign(adobeSignKey, adobeSignApiBase));
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
