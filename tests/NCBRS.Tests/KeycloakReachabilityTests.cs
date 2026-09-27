using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NCBRS.Middleware;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// An unreachable identity provider made every signed-in request fail with
/// 401 while the service logged nothing at the shipped levels. Found running
/// the Api in Production against Keycloak over HTTPS without the CA trusted.
/// </summary>
public class KeycloakReachabilityTests
{
    /// <summary>The shape Microsoft.IdentityModel throws when metadata cannot be fetched.</summary>
    private static Exception ConfigurationUnavailable()
        => new InvalidOperationException(
            "IDX20803: Unable to obtain configuration from: '[PII of type 'System.String' is hidden.]'.",
            new IOException("IDX20804: Unable to retrieve document from: '[PII is hidden]'.",
                new HttpRequestException("The SSL connection could not be established, see inner exception.")));

    [Fact]
    public void AMetadataFetchFailureIsTheProviderBeingUnreachable()
    {
        Assert.True(KeycloakReachability.IsProviderUnreachable(ConfigurationUnavailable()));

        // However deeply the library wraps it.
        Assert.True(KeycloakReachability.IsProviderUnreachable(new AggregateException(ConfigurationUnavailable())));
    }

    /// <summary>
    /// What .NET 10 actually reports when the metadata fetch fails, captured
    /// from the Api in Production against Keycloak over HTTPS with the CA not
    /// trusted: the handler goes on without configuration, so there is no
    /// issuer, and no keys, to validate against.
    /// </summary>
    [Fact]
    public void NoIssuerOrKeysToValidateAgainstIsTheProviderBeingUnreachable()
    {
        Assert.True(KeycloakReachability.IsProviderUnreachable(new SecurityTokenInvalidIssuerException(
            "IDX10204: Unable to validate issuer. validationParameters.ValidIssuer is null or whitespace AND "
            + "validationParameters.ValidIssuers is null or empty.")));
        Assert.True(KeycloakReachability.IsProviderUnreachable(new SecurityTokenSignatureKeyNotFoundException(
            "IDX10500: Signature validation failed. No security keys were provided to validate the signature.")));
    }

    /// <summary>A caller's bad token is the caller's problem and stays quiet.</summary>
    [Fact]
    public void ABadTokenIsNot()
    {
        Assert.False(KeycloakReachability.IsProviderUnreachable(new SecurityTokenExpiredException("IDX10223: Lifetime validation failed.")));
        Assert.False(KeycloakReachability.IsProviderUnreachable(new SecurityTokenInvalidSignatureException("IDX10511: Signature validation failed.")));
        Assert.False(KeycloakReachability.IsProviderUnreachable(new SecurityTokenInvalidIssuerException("IDX10205: Issuer validation failed.")));
        Assert.False(KeycloakReachability.IsProviderUnreachable(null));
    }

    /// <summary>
    /// Every request fails while it lasts: one error line a minute, not one
    /// per request.
    /// </summary>
    [Fact]
    public void ItIsReportedAsAnErrorAtMostOnceAMinute()
    {
        var logger = new CapturingLogger();

        for (var i = 0; i < 50; i++)
        {
            KeycloakReachability.Report(logger, ConfigurationUnavailable(), "https://id.ncbrs.ss/realms/ncbrs");
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("https://id.ncbrs.ss/realms/ncbrs", entry.Message);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
