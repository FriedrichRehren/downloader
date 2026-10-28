using Microsoft.AspNetCore.Http;

namespace Downloader.Tests;

public class ClientTests
{
    [Fact]
    public void IssuesCookieWhenMissing()
    {
        var context = new DefaultHttpContext();

        var id = Client.Identify(context);

        Assert.Matches("^[0-9a-f]{32}$", id);
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith($"{Client.Cookie}={id};", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.DoesNotContain("secure", cookie);
    }

    [Fact]
    public void ReusesValidCookie()
    {
        var existing = new string('a', 32);
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{Client.Cookie}={existing}";

        Assert.Equal(existing, Client.Identify(context));
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ReplacesInvalidCookie(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{Client.Cookie}={value}";

        Assert.NotEqual(value, Client.Identify(context));
        Assert.Equal(1, context.Response.Headers.SetCookie.Count);
    }

    [Fact]
    public void MarksCookieSecureBehindTls()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        Client.Identify(context);

        Assert.Contains("secure", context.Response.Headers.SetCookie.ToString());
    }
}
