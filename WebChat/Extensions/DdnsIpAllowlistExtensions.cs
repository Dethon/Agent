using WebChat.Middleware;

namespace WebChat.Extensions;

public static class DdnsIpAllowlistExtensions
{
    public static IApplicationBuilder UseDdnsIpAllowlist(this IApplicationBuilder app, IConfiguration configuration)
    {
        var ddnsHostname = configuration["AllowedDdnsHost"];

        if (!string.IsNullOrEmpty(ddnsHostname))
        {
            app.UseMiddleware<DdnsIpAllowlistMiddleware>(ddnsHostname);
        }

        return app;
    }
}