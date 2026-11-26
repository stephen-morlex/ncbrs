using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NCBRS.Controllers;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Who may do what (plan §17 item 29). What these pin: the decisions D1 and D2
/// in the one table both services build their policies from; the web reading
/// that same table; and <b>every endpoint making an explicit decision</b>, so a
/// new one cannot fall through to "any signed-in caller" by being forgotten.
/// The pre-audit sweep (item 24) did that inventory by hand once; this does it
/// on every build.
/// </summary>
public class RolePermissionsTests
{
    private static string[] Holders(string permission) => NcbrsRoles.Permissions[permission];

    /// <summary>Decision D1: community health workers record births, registrars certify them.</summary>
    [Fact]
    public void CommunityHealthWorkersRegisterButDoNotCertify()
    {
        Assert.Contains(NcbrsRoles.CommunityHealthWorker, Holders(NcbrsRoles.CanRegisterBirths));
        Assert.DoesNotContain(NcbrsRoles.CommunityHealthWorker, Holders(NcbrsRoles.CanIssueCertificates));
        Assert.Contains(NcbrsRoles.FacilityRegistrar, Holders(NcbrsRoles.CanIssueCertificates));

        var certificates = typeof(CertificatesController);
        Assert.Equal(NcbrsRoles.CanIssueCertificates, PolicyOn(certificates.GetMethod(nameof(CertificatesController.Issue))!));
        Assert.Equal(NcbrsRoles.CanIssueCertificates, PolicyOn(certificates.GetMethod(nameof(CertificatesController.Reprint))!));
        // Handing over printed paper is not certifying: at a post it is often the CHW who does it.
        Assert.Null(PolicyOn(certificates.GetMethod(nameof(CertificatesController.HandOver))!));
    }

    /// <summary>Decision D2: the system admin runs the system and reads no record.</summary>
    [Fact]
    public void TheSystemAdminRunsTheSystemAndSeesNoRecord()
    {
        foreach (var permission in new[] { NcbrsRoles.CanManageFacilities, NcbrsRoles.CanManageRegistrars, NcbrsRoles.CanEnrolDevices })
        {
            Assert.Contains(NcbrsRoles.SystemAdmin, Holders(permission));
            Assert.DoesNotContain(NcbrsRoles.RegistrarGeneral, Holders(permission));
        }

        var withRecords = NcbrsRoles.Permissions
            .Where(entry => entry.Value.Contains(NcbrsRoles.SystemAdmin))
            .Select(entry => entry.Key)
            .Order();
        Assert.Equal(
            new[] { NcbrsRoles.CanEnrolDevices, NcbrsRoles.CanManageFacilities, NcbrsRoles.CanManageRegistrars }.Order(),
            withRecords);
    }

    /// <summary>Decision D2: the registrar-general holds the records, nationally, and annuls alone.</summary>
    [Fact]
    public void TheRegistrarGeneralHoldsTheRecords()
    {
        foreach (var permission in new[]
        {
            NcbrsRoles.CanReadRecords, NcbrsRoles.CanReviewDuplicates, NcbrsRoles.CanApproveAmendments,
            NcbrsRoles.CanApproveLateRegistrations, NcbrsRoles.CanReadAuditTrail, NcbrsRoles.CanReadReporting,
            NcbrsRoles.CanExportReporting,
        })
        {
            Assert.Contains(NcbrsRoles.RegistrarGeneral, Holders(permission));
        }

        Assert.Equal([NcbrsRoles.RegistrarGeneral], Holders(NcbrsRoles.CanAnnulRegistrations));
    }

    [Fact]
    public void TheRetiredMinistryRoleGrantsNothing()
    {
        Assert.DoesNotContain(NcbrsRoles.Permissions.Values.SelectMany(roles => roles), role => role == "ministry-admin");
        Assert.Null(NcbrsRoles.RealmRoleFor(NCBRS.Models.RegistrarRole.MinistryAdmin));
    }

    /// <summary>
    /// The web builds its policies from <c>web/src/auth/permissions.json</c>.
    /// When the table changes, write the file from it (the message carries the
    /// expected content), exactly as the OpenAPI contract is regenerated.
    /// </summary>
    [Fact]
    public void TheWebReadsTheSameTable()
    {
        var path = Path.Combine(RepositoryRoot(), "web", "src", "auth", "permissions.json");
        var web = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path))!;

        var expected = JsonSerializer.Serialize(NcbrsRoles.Permissions, new JsonSerializerOptions { WriteIndented = true });
        Assert.True(
            web.Count == NcbrsRoles.Permissions.Count
            && NcbrsRoles.Permissions.All(entry => web.TryGetValue(entry.Key, out var roles) && roles.SequenceEqual(entry.Value)),
            $"web/src/auth/permissions.json is out of date with NcbrsRoles.Permissions. Expected:\n{expected}");
    }

    // --- every endpoint decides ----------------------------------------------------------------

    /// <summary>
    /// Endpoints deliberately open to any signed-in caller, by name. Adding an
    /// endpoint without a policy fails <see cref="EveryEndpointMakesAnExplicitDecision"/>
    /// until it is either given one or listed here, with a reason.
    /// </summary>
    private static readonly Dictionary<string, string> AnySignedInCaller = new()
    {
        ["AdministrativeAreasController.List"] = "Reference geography; names no person.",
        ["BirthRecordsController.GetRegistrationRules"] = "The statutory window and form rules; names no person.",
        ["DeviceCredentialsController.SetPin"] = "A person setting their own PIN, for the tablets; it names nobody else.",
        ["FacilitiesController.List"] = "Every role needs the facility list; it names no person.",
        ["FacilitiesController.Get"] = "As the list.",
        ["RegistrarsController.Me"] = "Every account asks who it is to the registry after sign-in.",
        ["RegistrarsController.Get"] = "Resolving a colleague by id; shows no HR detail.",
    };

    [Fact]
    public void EveryEndpointMakesAnExplicitDecision()
    {
        var undecided = Endpoints()
            .Where(endpoint => endpoint.Policies.Count == 0 && !endpoint.Anonymous && !AnySignedInCaller.ContainsKey(endpoint.Name))
            .Select(endpoint => endpoint.Name)
            .ToList();

        Assert.True(undecided.Count == 0,
            "These endpoints are open to any signed-in caller without saying so. Give each a policy, or list it in "
            + $"AnySignedInCaller with the reason: {string.Join(", ", undecided)}");
    }

    [Fact]
    public void EveryPolicyNamedIsInTheTable()
    {
        var unknown = Endpoints()
            .SelectMany(endpoint => endpoint.Policies.Select(policy => (endpoint.Name, policy)))
            .Where(pair => !NcbrsRoles.Permissions.ContainsKey(pair.policy))
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void TheSignedInAllowanceListsOnlyRealEndpoints()
    {
        var names = Endpoints().Select(endpoint => endpoint.Name).ToHashSet();

        Assert.Empty(AnySignedInCaller.Keys.Where(name => !names.Contains(name)));
    }

    private sealed record Endpoint(string Name, IReadOnlyList<string> Policies, bool Anonymous);

    private static IEnumerable<Endpoint> Endpoints()
        => typeof(AdministrativeAreasController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(method => new Endpoint(
                    $"{type.Name}.{method.Name}",
                    [.. method.GetCustomAttributes<AuthorizeAttribute>().Concat(type.GetCustomAttributes<AuthorizeAttribute>())
                        .Select(attribute => attribute.Policy)
                        .OfType<string>()],
                    method.GetCustomAttributes<AllowAnonymousAttribute>().Any() || type.GetCustomAttributes<AllowAnonymousAttribute>().Any())));

    private static string? PolicyOn(MethodInfo method) => method.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NCBRS.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
