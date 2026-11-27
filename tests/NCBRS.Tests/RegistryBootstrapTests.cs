using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Making a new, empty registry usable. Binding an account and creating a
/// facility each need a system admin already bound, so on an empty registry
/// neither could ever happen, and production never loaded the geography a
/// facility is placed in. What these pin: the geography loads every time; the
/// first system admin is bound only from an account that signed in and holds
/// the role; and never once an active system admin exists.
/// </summary>
public class RegistryBootstrapTests : IDisposable
{
    private const string Username = "amina.lado";
    private const string Subject = "kc-amina";

    private readonly TestDatabase _database = TestDatabase.Create(migrated: true);

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static RegistryBootstrap Bootstrap(NcbrsDbContext db)
        => new(db, new FacilityOnboardingService(db, new CountyLookup(db), new FacilityOnboardingOptions()), new CountyLookup(db));

    private static RegistryBootstrapOptions Options(string? username = Username) => new()
    {
        Username = username,
        FacilityName = "Ministry of Health headquarters",
        Area = "SS0101",
        OfficeCode = "MOH",
    };

    private async Task SignedInAsync(string roles = NcbrsRoles.SystemAdmin, string? county = null)
    {
        await using var db = NewDb();
        db.PendingAccounts.Add(new PendingAccount
        {
            Subject = Subject, DisplayName = "Amina Lado", Username = Username, RealmRoles = roles, CountyCode = county,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnEmptyRegistryGetsTheOfficialGeography_AndNobodyIsBoundUnlessNamed()
    {
        await using var db = NewDb();

        var outcome = await Bootstrap(db).RunAsync(Options(username: null));

        Assert.True(outcome.Succeeded, string.Join(" ", outcome.Messages));
        Assert.True(await db.AdministrativeAreas.AnyAsync(area => area.Code == "SS0101"));
        Assert.Empty(await db.Registrars.ToListAsync());
        Assert.Empty(await db.Facilities.ToListAsync());
    }

    [Fact]
    public async Task TheFirstSystemAdminIsBoundAtANewMinistryFacility_AndTheTrailSaysHow()
    {
        await SignedInAsync();
        await using var db = NewDb();

        var outcome = await Bootstrap(db).RunAsync(Options());

        Assert.True(outcome.Succeeded, string.Join(" ", outcome.Messages));
        var admin = await db.Registrars.Include(entry => entry.Facility).SingleAsync();
        Assert.Equal(RegistrarRole.SystemAdmin, admin.Role);
        Assert.Equal(Subject, admin.ExternalSubjectId);
        Assert.Equal("Amina Lado", admin.DisplayName);
        Assert.Equal("Ministry of Health headquarters", admin.Facility!.Name);
        Assert.Equal("SS0101", admin.Facility.CountyCode);
        Assert.Equal("MOH", admin.Facility.OfficeCode);
        Assert.Empty(await db.PendingAccounts.ToListAsync());

        var trail = await db.AuditLogs.OrderBy(row => row.TimestampUtc).ToListAsync();
        Assert.Contains(trail, row => row.Action.StartsWith("FacilityCreated:") && row.UserId == admin.RegistrarId);
        var bound = Assert.Single(trail, row => row.Action == $"SystemAdminBootstrapped:{Username}");
        Assert.Equal(admin.RegistrarId, bound.UserId);
        Assert.Equal(RegistryBootstrap.Channel, bound.DeviceId);
    }

    [Fact]
    public async Task OnceASystemAdminExists_ItIsRefused_AndChangesNothing()
    {
        await SignedInAsync();
        await using (var db = NewDb())
        {
            Assert.True((await Bootstrap(db).RunAsync(Options())).Succeeded);
        }

        await using (var db = NewDb())
        {
            db.PendingAccounts.Add(new PendingAccount
            {
                Subject = "kc-intruder", DisplayName = "Someone Else", Username = "someone.else", RealmRoles = NcbrsRoles.SystemAdmin,
            });
            await db.SaveChangesAsync();
        }

        await using var again = NewDb();
        var outcome = await Bootstrap(again).RunAsync(Options("someone.else"));

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Messages, message => message.Contains("already bound"));
        Assert.Single(await again.Registrars.ToListAsync());
    }

    /// <summary>
    /// The other way a registry is left with nobody able to bind anyone: every
    /// system admin withdrawn. Then it is allowed again.
    /// </summary>
    [Fact]
    public async Task WithEverySystemAdminWithdrawn_ItIsAllowedAgain()
    {
        await using (var db = NewDb())
        {
            var facility = new Facility { Name = "Old HQ", CountyCode = "SS0101", BrnBlockStart = 100_000, BrnBlockEnd = 199_999 };
            db.Facilities.Add(facility);
            db.Registrars.Add(new Registrar
            {
                FacilityId = facility.FacilityId, ExternalSubjectId = "kc-gone", DisplayName = "Gone", Role = RegistrarRole.SystemAdmin,
                WithdrawnAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await SignedInAsync();
        await using var check = NewDb();

        Assert.True((await Bootstrap(check).RunAsync(Options())).Succeeded);
    }

    [Fact]
    public async Task AnAccountThatNeverSignedInIsRefused_SoNoIdIsTyped()
    {
        await using var db = NewDb();

        var outcome = await Bootstrap(db).RunAsync(Options());

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Messages, message => message.Contains("has signed in"));
        Assert.Empty(await db.Registrars.ToListAsync());
    }

    [Fact]
    public async Task AnAccountWithoutTheRoleInKeycloakIsRefused()
    {
        await SignedInAsync(roles: NcbrsRoles.FacilityRegistrar);
        await using var db = NewDb();

        var outcome = await Bootstrap(db).RunAsync(Options());

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Messages, message => message.Contains(NcbrsRoles.SystemAdmin));
        Assert.Empty(await db.Registrars.ToListAsync());
        Assert.Empty(await db.Facilities.ToListAsync());
    }

    [Fact]
    public async Task AnAccountInAnotherCountysGroupIsRefused()
    {
        await SignedInAsync(county: "SS0105");
        await using var db = NewDb();

        var outcome = await Bootstrap(db).RunAsync(Options());

        Assert.False(outcome.Succeeded);
        Assert.Empty(await db.Registrars.ToListAsync());
    }

    [Fact]
    public async Task AnExistingFacilityWithThatNameInTheCountyIsUsed_NotDuplicated()
    {
        await using (var db = NewDb())
        {
            await Bootstrap(db).RunAsync(Options(username: null));
            var county = await db.AdministrativeAreas.SingleAsync(area => area.Code == "SS0101");
            db.Facilities.Add(new Facility
            {
                Name = "Ministry of Health Headquarters", CountyCode = "SS0101", AdministrativeAreaId = county.AdministrativeAreaId,
                BrnBlockStart = 100_000, BrnBlockEnd = 199_999,
            });
            await db.SaveChangesAsync();
        }

        await SignedInAsync();
        await using var check = NewDb();

        Assert.True((await Bootstrap(check).RunAsync(Options())).Succeeded);
        var facility = await check.Facilities.SingleAsync();
        Assert.Equal(facility.FacilityId, (await check.Registrars.SingleAsync()).FacilityId);
    }

    [Fact]
    public async Task AnUnknownAreaIsRefused()
    {
        await SignedInAsync();
        await using var db = NewDb();

        var refused = await Bootstrap(db).RunAsync(new RegistryBootstrapOptions
        {
            Username = Username, FacilityName = "HQ", Area = "SS9999",
        });

        Assert.False(refused.Succeeded);
        Assert.Contains(refused.Messages, message => message.Contains("SS9999"));
    }
}
