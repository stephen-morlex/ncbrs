using System.Text.Json;
using System.Text.RegularExpressions;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The realm's sign-in hardening (plan §17 item 30, decision D3). The dev realm
/// is the reference for a production one (RUNBOOK, "Deployment: Keycloak
/// realm"), and the e2e tests and the TLS rehearsal sign in through it, so
/// what is pinned here is what CI actually exercises.
/// </summary>
public class KeycloakRealmHardeningTests
{
    private const string OtpRequired = "otp-required";

    private static readonly JsonElement Realm = LoadRealm();

    /// <summary>
    /// D3: a one-time code for district officers and both Ministry roles. Put
    /// the other way round, which is the way that fails safe: <b>every</b> role
    /// needs one except the two facility roles, whose tablets sign in once and
    /// then work offline for weeks. A role added later needs a code unless
    /// someone decides otherwise here.
    /// </summary>
    [Fact]
    public void EveryRoleButTheFacilityRolesNeedsAOneTimeCode()
    {
        var withCode = Roles()
            .Where(role => role.TryGetProperty("composites", out var composites)
                && composites.GetProperty("realm").EnumerateArray().Any(name => name.GetString() == OtpRequired))
            .Select(role => role.GetProperty("name").GetString()!)
            .Order();
        var allOthers = Roles()
            .Select(role => role.GetProperty("name").GetString()!)
            .Where(name => name is not OtpRequired and not NcbrsRoles.FacilityRegistrar and not NcbrsRoles.CommunityHealthWorker)
            .Order();

        Assert.Equal(allOthers, withCode);
        Assert.Equal(
            new[] { NcbrsRoles.DistrictOfficer, NcbrsRoles.RegistrarGeneral, NcbrsRoles.SystemAdmin }.Order(),
            withCode);
    }

    /// <summary>
    /// Both ways of signing in with a password ask for the code. Binding only
    /// the browser flow would leave the password grant as a way round it on
    /// any client where it is switched on.
    /// </summary>
    [Theory]
    [InlineData("browserFlow", "auth-otp-form")]
    [InlineData("directGrantFlow", "direct-grant-validate-otp")]
    public void TheSignInFlowAsksForTheCodeByRole(string binding, string otpAuthenticator)
    {
        var flow = Flow(Realm.GetProperty(binding).GetString()!);
        var conditional = Descendants(flow)
            .Select(Flow)
            .Single(subflow => Executions(subflow).Any(execution => Authenticator(execution) == otpAuthenticator));

        var executions = Executions(conditional).ToList();
        var condition = Assert.Single(executions, execution => Authenticator(execution) == "conditional-user-role");
        var config = Realm.GetProperty("authenticatorConfig").EnumerateArray()
            .Single(entry => entry.GetProperty("alias").GetString() == condition.GetProperty("authenticatorConfig").GetString())
            .GetProperty("config");
        Assert.Equal(OtpRequired, config.GetProperty("condUserRole").GetString());
        Assert.Equal("false", config.GetProperty("negate").GetString());

        // REQUIRED, not ALTERNATIVE: someone holding the role with no code set
        // up is made to set one up (browser) or refused (password grant).
        var otp = executions.Single(execution => Authenticator(execution) == otpAuthenticator);
        Assert.Equal("REQUIRED", otp.GetProperty("requirement").GetString());
    }

    /// <summary>
    /// Guessing is slowed, never ended for good: a permanent lockout lets
    /// anyone who knows an officer's username lock them out of the registry.
    /// </summary>
    [Fact]
    public void RepeatedWrongPasswordsLockTheAccountForAWhile()
    {
        Assert.True(Realm.GetProperty("bruteForceProtected").GetBoolean());
        Assert.False(Realm.GetProperty("permanentLockout").GetBoolean());
        Assert.InRange(Realm.GetProperty("failureFactor").GetInt32(), 3, 10);
        Assert.True(Realm.GetProperty("maxFailureWaitSeconds").GetInt32() >= 15 * 60);
    }

    [Fact]
    public void PasswordsAreLongAndNotTheUsername()
    {
        var policy = Realm.GetProperty("passwordPolicy").GetString()!;

        var length = Regex.Match(policy, @"\blength\((\d+)\)");
        Assert.True(length.Success && int.Parse(length.Groups[1].Value) >= 12, policy);
        Assert.Contains("notUsername", policy);
        Assert.Contains("passwordHistory", policy);
    }

    /// <summary>
    /// The realm import applies the password policy to a plain-text password,
    /// so the fixtures' "password" is imported pre-hashed. A plain one makes
    /// Keycloak refuse to start, which is what the first attempt here did.
    /// </summary>
    [Fact]
    public void FixturePasswordsAreImportedHashed()
    {
        foreach (var credential in Users().SelectMany(Credentials).Where(c => c.GetProperty("type").GetString() == "password"))
        {
            Assert.False(credential.TryGetProperty("value", out _), "A plain-text fixture password fails the realm's own policy at import.");
            Assert.True(credential.TryGetProperty("secretData", out _));
        }
    }

    /// <summary>
    /// Every fixture account that needs a code has one set up, or the e2e
    /// sign-in would land on the authenticator set-up page instead of the app.
    /// </summary>
    [Fact]
    public void EveryFixtureThatNeedsACodeHasOne()
    {
        var needingCode = Roles()
            .Where(role => role.TryGetProperty("composites", out var composites)
                && composites.GetProperty("realm").EnumerateArray().Any(name => name.GetString() == OtpRequired))
            .Select(role => role.GetProperty("name").GetString()!)
            .ToHashSet();

        var people = Users().Where(user => !user.TryGetProperty("serviceAccountClientId", out _));
        foreach (var user in people.Where(user => user.GetProperty("realmRoles").EnumerateArray().Any(role => needingCode.Contains(role.GetString()!))))
        {
            Assert.Contains(Credentials(user), credential => credential.GetProperty("type").GetString() == "otp");
        }

        foreach (var user in people.Where(user => !user.GetProperty("realmRoles").EnumerateArray().Any(role => needingCode.Contains(role.GetString()!))))
        {
            Assert.DoesNotContain(Credentials(user), credential => credential.GetProperty("type").GetString() == "otp");
        }
    }

    // --- reading the realm ----------------------------------------------------------------------

    private static IEnumerable<JsonElement> Roles() => Realm.GetProperty("roles").GetProperty("realm").EnumerateArray();

    private static IEnumerable<JsonElement> Users() => Realm.GetProperty("users").EnumerateArray();

    private static IEnumerable<JsonElement> Credentials(JsonElement user)
        => user.TryGetProperty("credentials", out var credentials) ? credentials.EnumerateArray() : [];

    private static JsonElement Flow(string alias)
        => Realm.GetProperty("authenticationFlows").EnumerateArray().Single(flow => flow.GetProperty("alias").GetString() == alias);

    private static IEnumerable<JsonElement> Executions(JsonElement flow) => flow.GetProperty("authenticationExecutions").EnumerateArray();

    private static string? Authenticator(JsonElement execution)
        => execution.TryGetProperty("authenticator", out var authenticator) ? authenticator.GetString() : null;

    /// <summary>The aliases of every subflow below a flow, at any depth.</summary>
    private static IEnumerable<string> Descendants(JsonElement flow)
        => Executions(flow)
            .Where(execution => execution.GetProperty("authenticatorFlow").GetBoolean())
            .Select(execution => execution.GetProperty("flowAlias").GetString()!)
            .SelectMany(alias => Descendants(Flow(alias)).Prepend(alias));

    private static JsonElement LoadRealm()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NCBRS.slnx")))
        {
            directory = directory.Parent;
        }

        using var realm = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "keycloak", "ncbrs-realm.json")));
        return realm.RootElement.Clone();
    }
}
