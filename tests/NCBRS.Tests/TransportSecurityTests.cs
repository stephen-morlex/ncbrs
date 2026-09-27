using NCBRS.District.Services;
using NCBRS.Middleware;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Security relaxations the local stack needs -- plain-HTTP Keycloak, unsigned
/// device batches -- live in Development settings only, and outside
/// Development the services refuse to start with them.
///
/// Before this, the relaxations sat in the *base* settings, which every
/// environment loads. Production would have fetched token-signing keys over
/// plain HTTP (anyone on the path could mint accepted tokens), and would have
/// accepted unsigned device batches, with the device-signature control that
/// CLAUDE.md says "defaults to on" switched off by the file shipped beside it.
/// </summary>
public class TransportSecurityTests
{
    // --- the defaults are the safe values ---------------------------------------------------

    [Fact]
    public void KeycloakMetadataMustBeHttpsByDefault()
        => Assert.True(new KeycloakOptions().RequireHttpsMetadata);

    [Fact]
    public void RelaxingHttpsIsRefusedOutsideDevelopment()
    {
        var relaxed = new KeycloakOptions { Authority = HttpsRealm, RequireHttpsMetadata = false };

        Assert.NotNull(relaxed.RefusalOutsideDevelopment(isDevelopment: false));
        Assert.Null(relaxed.RefusalOutsideDevelopment(isDevelopment: true));
        Assert.Null(new KeycloakOptions { Authority = HttpsRealm }.RefusalOutsideDevelopment(isDevelopment: false));
    }

    private const string HttpsRealm = "https://id.ncbrs.ss/realms/ncbrs";

    /// <summary>
    /// With the flag on, an http:// authority used to pass this check, and the
    /// service then failed every request — anonymous /health included — with a
    /// 500. Found running the Api in Production against TLS infrastructure; the
    /// shipped default authority was exactly that.
    /// </summary>
    [Theory]
    [InlineData("http://id.ncbrs.ss/realms/ncbrs")]
    [InlineData("id.ncbrs.ss/realms/ncbrs")]
    public void AnAuthorityThatIsNotHttpsIsRefusedOutsideDevelopment(string authority)
    {
        var options = new KeycloakOptions { Authority = authority };

        Assert.Contains("Keycloak:Authority", options.RefusalOutsideDevelopment(isDevelopment: false));
        Assert.Null(options.RefusalOutsideDevelopment(isDevelopment: true));
    }

    [Fact]
    public void TheDefaultAuthorityIsRefusedOutsideDevelopment()
        => Assert.NotNull(new KeycloakOptions().RefusalOutsideDevelopment(isDevelopment: false));

    [Theory]
    [InlineData("http://central.ncbrs.ss", "https://id.ncbrs.ss/realms/ncbrs/protocol/openid-connect/token", "Central:BaseUrl")]
    [InlineData("https://central.ncbrs.ss", "http://id.ncbrs.ss/realms/ncbrs/protocol/openid-connect/token", "Central:TokenEndpoint")]
    public void ADistrictNodeRefusesPlainHttpOutsideDevelopment(string baseUrl, string tokenEndpoint, string named)
    {
        var options = new CentralApiOptions { BaseUrl = baseUrl, TokenEndpoint = tokenEndpoint };

        Assert.Contains(named, options.RefusalOutsideDevelopment(isDevelopment: false));
        Assert.Null(options.RefusalOutsideDevelopment(isDevelopment: true));
    }

    [Fact]
    public void ADistrictNodeOnHttpsStarts()
        => Assert.Null(new CentralApiOptions
        {
            BaseUrl = "https://central.ncbrs.ss",
            TokenEndpoint = "https://id.ncbrs.ss/realms/ncbrs/protocol/openid-connect/token",
        }.RefusalOutsideDevelopment(isDevelopment: false));

    // --- and the shipped base settings do not relax anything -----------------------------------

    /// <summary>
    /// The base file is loaded in every environment, so a relaxation there is
    /// a relaxation in production. Development's own file is the only place
    /// one may live.
    /// </summary>
    [Theory]
    [InlineData("NCBRS.Api")]
    [InlineData("NCBRS.Consumer")]
    public void NoBaseSettingsFileRelaxesHttps(string project)
        => Assert.False(ShippedSettings.SetsFalse(
            ShippedSettings.Read(project, "appsettings.json"), "Keycloak", "RequireHttpsMetadata"));

    /// <summary>
    /// Where Keycloak and the centre live is per deployment, and the shipped
    /// values were a developer's localhost over plain HTTP.
    /// </summary>
    [Theory]
    [InlineData("NCBRS.Api", "Keycloak", "Authority")]
    [InlineData("NCBRS.Consumer", "Keycloak", "Authority")]
    [InlineData("NCBRS.District", "Central", "BaseUrl")]
    [InlineData("NCBRS.District", "Central", "TokenEndpoint")]
    public void TheBaseSettingsNameNoDevelopmentAddress(string project, string section, string key)
        => Assert.False(ShippedSettings.Sets(ShippedSettings.Read(project, "appsettings.json"), section, key, out _));

    [Fact]
    public void TheBaseSettingsDoNotSwitchOffDeviceSignatures()
        => Assert.False(ShippedSettings.SetsFalse(
            ShippedSettings.Read("NCBRS.Api", "appsettings.json"), "DeviceEnrolment", "RequireSignature"));

    /// <summary>
    /// Already refused outside Development by <c>CertificateSigner</c>, so this
    /// is the rule rather than a live hole: a relaxation in the base file is
    /// one guard away from production, and the day that guard changes nobody
    /// looks here.
    /// </summary>
    [Fact]
    public void TheBaseSettingsDoNotAllowAThrowawaySigningKey()
        => Assert.False(ShippedSettings.SetsTrue(
            ShippedSettings.Read("NCBRS.Api", "appsettings.json"), "CertificateSigning", "AllowEphemeralDevelopmentKey"));
}
