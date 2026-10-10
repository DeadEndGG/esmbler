using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;

namespace Esmbler.Hosting.Auth;

public static class AuthRegistration
{
    /// <summary>JWT bearer authentication accepting only tokens minted for <paramref name="audience"/> (no passthrough).</summary>
    public static IServiceCollection AddEsmblerAuth(this IServiceCollection services, string audience)
    {
        services.AddSingleton<TokenService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService>((o, tokens) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = tokens.Validation(audience);
                o.Events = new JwtBearerEvents
                {
                    // SignalR sends the token in the query string for WebSockets; accept it only on the hub path.
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            ctx.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();
        return services;
    }
}
