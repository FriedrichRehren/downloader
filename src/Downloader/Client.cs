using System.Security.Cryptography;

namespace Downloader;

/// <summary>
/// Jobs belong to the browser that started them. There is no login, so an
/// unguessable cookie is what keeps one visitor from seeing, downloading or
/// cancelling another visitor's jobs.
/// </summary>
public static class Client
{
    public const string Cookie = "downloader-client";

    /// <summary>Returns the caller's client ID, issuing a new cookie if it has none.</summary>
    public static string Identify(HttpContext context)
    {
        if (context.Request.Cookies[Cookie] is { Length: 32 } existing && existing.All(char.IsAsciiHexDigitLower))
        {
            return existing;
        }

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        context.Response.Cookies.Append(Cookie, id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            // TLS ends at Traefik, so the request arrives here as plain HTTP.
            Secure = context.Request.IsHttps || context.Request.Headers["X-Forwarded-Proto"] == "https",
            MaxAge = TimeSpan.FromDays(365),
        });
        return id;
    }
}
