using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NCBRS.Client.Auth;
using NCBRS.Client.Network;
using Xunit;
using static NCBRS.Client.Tests.CentralClientTests;

namespace NCBRS.Client.Tests;

/// <summary>
/// A tablet's sign-in held as an offline token: obtained once, traded for
/// access tokens each window, rotated on every use, and told apart — "sign in
/// again" from "no network". The live proof against Keycloak is the TLS rehearsal.
/// </summary>
public class OfflineTokenSessionTests
{
    private static readonly OidcEndpoints Realm = OidcEndpoints.ForKeycloakRealm(new Uri("https://id.ncbrs.ss/realms/ncbrs"));
    private static readonly Uri Redirect = new("ss.gov.ncbrs.tablet:/signed-in");

    /// <summary>An unsigned token with the given typ; the session only reads the claim.</summary>
    private static string Jwt(string typ, int serial = 0)
        => $"e30.{PkceChallenge.Base64Url(Encoding.UTF8.GetBytes($$"""{"typ":"{{typ}}","n":{{serial}}}"""))}.sig";

    private sealed class FakeKeycloak : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Forms { get; } = [];

        public int Issued { get; private set; }

        public bool Revoked { get; set; }

        public Exception? Down { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down is not null)
            {
                throw Down;
            }

            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(pair => pair.Split('='))
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            Forms.Add(form);

            if (request.RequestUri!.AbsolutePath.EndsWith("/revoke"))
            {
                Revoked = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (Revoked)
            {
                return Json("""{"error":"invalid_grant","error_description":"Offline session not active"}""", HttpStatusCode.BadRequest);
            }

            // Every answer carries a new offline token, as Keycloak's does.
            Issued++;
            return Json($$"""{"access_token":"access-{{Issued}}","expires_in":900,"refresh_token":"{{Jwt("Offline", Issued)}}"}""");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static (OfflineTokenSession Session, List<string?> Saved, FakeKeycloak Keycloak) Session(string? offlineToken = null)
    {
        var keycloak = new FakeKeycloak();
        var saved = new List<string?>();
        var session = new OfflineTokenSession(new HttpClient(keycloak), Realm, offlineToken, (token, _) =>
        {
            saved.Add(token);
            return Task.CompletedTask;
        });
        return (session, saved, keycloak);
    }

    // --- signing in -------------------------------------------------------------------------

    [Fact]
    public void ThePkceChallengeIsTheHashOfTheVerifier()
    {
        var pkce = PkceChallenge.Create();

        Assert.InRange(pkce.Verifier.Length, 43, 128);
        Assert.Equal(PkceChallenge.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier))), pkce.Challenge);
        Assert.NotEqual(PkceChallenge.Create().Verifier, pkce.Verifier);
    }

    [Fact]
    public void TheBrowserIsSentToAskForAnOfflineTokenWithPkce()
    {
        var (session, _, _) = Session();
        var pkce = PkceChallenge.Create();

        var url = session.AuthorizationUrl(pkce, Redirect).AbsoluteUri;

        Assert.StartsWith("https://id.ncbrs.ss/realms/ncbrs/protocol/openid-connect/auth?", url);
        Assert.Contains("scope=openid%20offline_access", url);
        Assert.Contains($"code_challenge={pkce.Challenge}", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains($"state={pkce.State}", url);
        Assert.Contains("client_id=ncbrs-device", url);
    }

    [Fact]
    public async Task RedeemingTheCodeKeepsTheOfflineToken()
    {
        var (session, saved, keycloak) = Session();
        var pkce = PkceChallenge.Create();

        var result = await session.RedeemAsync("the-code", pkce, Redirect);

        Assert.True(result.SignedIn);
        Assert.True(session.SignedIn);
        var form = Assert.Single(keycloak.Forms);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(pkce.Verifier, form["code_verifier"]);
        Assert.Equal(Redirect.AbsoluteUri, form["redirect_uri"]);
        Assert.Equal("Offline", OfflineTokenSession.TokenType(Assert.Single(saved)!));
    }

    /// <summary>
    /// An account that cannot hold an offline token gets an ordinary one. A
    /// tablet keeping that would stop syncing within the hour, silently.
    /// </summary>
    [Fact]
    public async Task AnOrdinarySessionIsRefusedForATablet()
    {
        var (session, saved, _) = Session();
        using var ordinary = JsonDocument.Parse($$"""{"access_token":"a","expires_in":900,"refresh_token":"{{Jwt("Refresh")}}"}""");

        var result = await session.AcceptAsync(ordinary.RootElement);

        Assert.False(result.SignedIn);
        Assert.Contains("registrar", result.Problem);
        Assert.False(session.SignedIn);
        Assert.Empty(saved);
    }

    // --- each connectivity window -------------------------------------------------------------

    /// <summary>
    /// Keycloak rotates the offline token on every use. The new one is saved at
    /// once: a device keeping the old one would end up holding a dead sign-in.
    /// </summary>
    [Fact]
    public async Task EachRenewalSavesTheRotatedOfflineToken()
    {
        var (session, saved, keycloak) = Session(offlineToken: Jwt("Offline"));

        var access = await session.GetAccessTokenAsync();

        Assert.Equal("access-1", access);
        Assert.Equal("refresh_token", keycloak.Forms[0]["grant_type"]);
        Assert.Equal(Jwt("Offline"), keycloak.Forms[0]["refresh_token"]);
        Assert.Equal(Jwt("Offline", 1), Assert.Single(saved));
    }

    [Fact]
    public async Task AnAccessTokenIsReusedUntilItNearlyExpires()
    {
        var (session, _, keycloak) = Session(offlineToken: Jwt("Offline"));

        await session.GetAccessTokenAsync();
        await session.GetAccessTokenAsync();

        Assert.Single(keycloak.Forms);
    }

    /// <summary>
    /// A lapsed or revoked offline session: the token is dropped and the
    /// network layer reports it as "sign in again".
    /// </summary>
    [Fact]
    public async Task AnEndedSignInMeansSignInAgain()
    {
        var (session, saved, keycloak) = Session(offlineToken: Jwt("Offline"));
        keycloak.Revoked = true;
        var (facility, _) = Facility_();
        facility.RegisterBirth(Birth());
        var central = new CentralClient(new HttpClient(new FakeNetwork()), new CentralEndpoints(new Uri("https://registry.ncbrs.ss/")), session.AccessToken);

        var result = await central.UploadAsync(facility.BuildSignedUpload());

        Assert.Equal(CentralOutcome.Unauthorized, result.Outcome);
        Assert.False(session.SignedIn);
        Assert.Null(Assert.Single(saved));
        Assert.Contains("Sign in again", session.LastProblem);
    }

    /// <summary>
    /// No answer from Keycloak is the link, not the sign-in. Telling a
    /// registrar at a village post to sign in again because the mast is down
    /// would send them to fix the wrong thing — and the sign-in is kept.
    /// </summary>
    [Fact]
    public async Task NoAnswerFromKeycloakIsUnreachableNotSignInAgain()
    {
        var (session, saved, keycloak) = Session(offlineToken: Jwt("Offline"));
        keycloak.Down = new HttpRequestException("No route to host");
        var (facility, _) = Facility_();
        facility.RegisterBirth(Birth());
        var central = new CentralClient(new HttpClient(new FakeNetwork()), new CentralEndpoints(new Uri("https://registry.ncbrs.ss/")), session.AccessToken);

        var result = await central.UploadAsync(facility.BuildSignedUpload());

        Assert.Equal(CentralOutcome.Unreachable, result.Outcome);
        Assert.True(session.SignedIn);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task SigningOutRevokesTheOfflineTokenAtKeycloak()
    {
        var (session, saved, keycloak) = Session(offlineToken: Jwt("Offline"));

        await session.SignOutAsync();

        var form = Assert.Single(keycloak.Forms);
        Assert.Equal(Jwt("Offline"), form["token"]);
        Assert.True(keycloak.Revoked);
        Assert.False(session.SignedIn);
        Assert.Null(Assert.Single(saved));
    }

    [Fact]
    public async Task SigningOutOfflineStillForgetsTheSignInHere()
    {
        var (session, saved, keycloak) = Session(offlineToken: Jwt("Offline"));
        keycloak.Down = new HttpRequestException("No route to host");

        await session.SignOutAsync();

        Assert.False(session.SignedIn);
        Assert.Null(Assert.Single(saved));
    }
}
