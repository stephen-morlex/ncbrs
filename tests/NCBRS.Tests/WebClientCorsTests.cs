using NCBRS.Web;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Which browser origins may call the Api and the Consumer. The class said
/// "no wildcard" but nothing checked it, and a lone <c>"*"</c> makes ASP.NET
/// allow every site (verified live). The localhost origins also sat in the
/// base settings, which production loads.
/// </summary>
public class WebClientCorsTests
{
    private static string? Refusal(bool isDevelopment, params string[] origins)
        => new WebClientCorsOptions { AllowedOrigins = origins }.Refusal(isDevelopment);

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.ncbrs.ss")]
    public void AWildcardIsRefusedEvenInDevelopment(string origin)
    {
        Assert.Contains("Wildcards are refused", Refusal(isDevelopment: true, origin));
        Assert.Contains("Wildcards are refused", Refusal(isDevelopment: false, origin));
    }

    /// <summary>A browser's Origin header has no path, so these never match.</summary>
    [Theory]
    [InlineData("https://registry.ncbrs.ss/")]
    [InlineData("https://registry.ncbrs.ss/app")]
    [InlineData("registry.ncbrs.ss")]
    [InlineData("ftp://registry.ncbrs.ss")]
    public void AnythingButABareOriginIsRefused(string origin)
        => Assert.Contains("not a bare origin", Refusal(isDevelopment: true, origin));

    [Fact]
    public void PlainHttpIsRefusedOutsideDevelopment()
    {
        Assert.Contains("HTTPS", Refusal(isDevelopment: false, "https://registry.ncbrs.ss", "http://registry.ncbrs.ss"));
        Assert.Null(Refusal(isDevelopment: true, "http://localhost:5173", "http://127.0.0.1:5173"));
    }

    [Fact]
    public void HttpsOriginsAndAnEmptyListStart()
    {
        Assert.Null(Refusal(isDevelopment: false, "https://registry.ncbrs.ss", "https://registry.ncbrs.ss:8443"));

        // No browser may call the service: right for a deployment with no web
        // front end, or one serving the site from the same origin.
        Assert.Null(Refusal(isDevelopment: false));
    }

    [Theory]
    [InlineData("NCBRS.Api")]
    [InlineData("NCBRS.Consumer")]
    public void TheBaseSettingsAllowNoOrigin(string project)
        => Assert.False(ShippedSettings.Sets(
            ShippedSettings.Read(project, "appsettings.json"), "WebClientCors", "AllowedOrigins", out _));

    /// <summary>The shipped Development origins must pass the check they are subject to.</summary>
    [Theory]
    [InlineData("NCBRS.Api")]
    [InlineData("NCBRS.Consumer")]
    public void TheDevelopmentOriginsAreValid(string project)
    {
        Assert.True(ShippedSettings.Sets(
            ShippedSettings.Read(project, "appsettings.Development.json"), "WebClientCors", "AllowedOrigins", out var origins));

        string[] list = [.. origins.EnumerateArray().Select(origin => origin.GetString()!)];
        Assert.NotEmpty(list);
        Assert.Null(Refusal(isDevelopment: true, list));
    }
}
