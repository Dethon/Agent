using System.Net;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.Middleware;

public sealed class DdnsIpAllowlistMiddleware(RequestDelegate next, string ddnsHostname)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var clientIp = GetClientIp(context);

        if (string.IsNullOrEmpty(clientIp) || !await IsIpAllowedAsync(clientIp))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden");
            return;
        }

        await next(context);
    }

    private static string? GetClientIp(HttpContext context)
    {
        return context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
               ?? context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
               ?? context.Connection.RemoteIpAddress?.ToString();
    }

    private async Task<bool> IsIpAllowedAsync(string clientIp)
    {
        try
        {
            var allowedIps = await Dns.GetHostAddressesAsync(ddnsHostname);
            var clientIpParsed = IPAddress.Parse(clientIp);
            return allowedIps.Any(ip => ip.Equals(clientIpParsed));
        }
        catch
        {
            return false;
        }
    }
}