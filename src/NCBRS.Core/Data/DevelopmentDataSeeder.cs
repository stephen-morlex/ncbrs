using Microsoft.EntityFrameworkCore;
using NCBRS.Models;

namespace NCBRS.Data;

/// <summary>
/// Development-only sample data: two South Sudan facilities and the registrars
/// that map to the imported Keycloak realm users, so a fresh dev database is
/// usable immediately and reproducibly.
///
/// This replaces the old hand-inserted Zambian dev rows. The facilities are
/// placed in seeded South Sudan counties (their legacy <c>DistrictId</c> now
/// carries the county code during the transition), and the registrars carry
/// the same subject claims the dev realm issues — so existing dev logins keep
/// resolving to a registrar rather than breaking.
///
/// Idempotent, keyed by fixed ids and by subject. **Never runs in production**:
/// real facilities and registrars are provisioned through their own processes,
/// and a production registry starts empty. Only invoked from the Development
/// startup path.
/// </summary>
public static class DevelopmentDataSeeder
{
    private static readonly Guid JubaHospitalId = new("0199c000-0000-7000-8000-000000000001");
    private static readonly Guid TerekekaUnitId = new("0199c000-0000-7000-8000-000000000002");

    public static async Task SeedAsync(NcbrsDbContext db, CancellationToken cancellationToken = default)
    {
        // Areas are seeded first; without the counties there is nowhere to
        // place a facility, so this is a no-op rather than an error.
        var juba = await db.AdministrativeAreas.FirstOrDefaultAsync(a => a.Code == "SS-CE-JUB", cancellationToken);
        var terekeka = await db.AdministrativeAreas.FirstOrDefaultAsync(a => a.Code == "SS-CE-TER", cancellationToken);
        if (juba is null || terekeka is null)
        {
            return;
        }

        if (!await db.Facilities.AnyAsync(f => f.FacilityId == JubaHospitalId, cancellationToken))
        {
            db.Facilities.Add(new Facility
            {
                FacilityId = JubaHospitalId,
                Name = "Juba Teaching Hospital",
                Tier = FacilityTier.Hospital,
                ConnectivityProfile = ConnectivityProfile.AlwaysOn,
                DistrictId = juba.Code,
                AdministrativeAreaId = juba.AdministrativeAreaId,
                BrnBlockStart = 100_000,
                BrnBlockEnd = 199_999,
                BrnBlockNextAvailable = 100_000,
            });
        }

        if (!await db.Facilities.AnyAsync(f => f.FacilityId == TerekekaUnitId, cancellationToken))
        {
            db.Facilities.Add(new Facility
            {
                FacilityId = TerekekaUnitId,
                Name = "Terekeka Primary Health Care Unit",
                Tier = FacilityTier.VillageHealthPost,
                ConnectivityProfile = ConnectivityProfile.OfflineFirst,
                DistrictId = terekeka.Code,
                AdministrativeAreaId = terekeka.AdministrativeAreaId,
                BrnBlockStart = 200_000,
                BrnBlockEnd = 299_999,
                BrnBlockNextAvailable = 200_000,
            });
        }

        // The subjects match the imported dev realm; the display names are
        // South Sudanese. Role enum names are unchanged (renaming
        // DistrictOfficer touches Keycloak realm roles too — a later step).
        await EnsureRegistrar(db, "0199c000-0000-7000-8000-0000000000a1", JubaHospitalId,
            "11111111-1111-4111-8111-111111111111", "Nurse A. Deng", RegistrarRole.FacilityRegistrar, cancellationToken);
        await EnsureRegistrar(db, "0199c000-0000-7000-8000-0000000000a2", JubaHospitalId,
            "22222222-2222-4222-8222-222222222222", "Dr. M. Lado", RegistrarRole.FacilityRegistrar, cancellationToken);
        await EnsureRegistrar(db, "0199c000-0000-7000-8000-0000000000a3", JubaHospitalId,
            "33333333-3333-4333-8333-333333333333", "Grace Deng (County Officer)", RegistrarRole.DistrictOfficer, cancellationToken);
        await EnsureRegistrar(db, "0199c000-0000-7000-8000-0000000000a4", JubaHospitalId,
            "44444444-4444-4444-8444-444444444444", "Naledi Akol (Ministry)", RegistrarRole.MinistryAdmin, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureRegistrar(
        NcbrsDbContext db, string registrarId, Guid facilityId, string subject,
        string displayName, RegistrarRole role, CancellationToken cancellationToken)
    {
        if (await db.Registrars.AnyAsync(r => r.ExternalSubjectId == subject, cancellationToken))
        {
            return;
        }

        db.Registrars.Add(new Registrar
        {
            RegistrarId = new Guid(registrarId),
            FacilityId = facilityId,
            ExternalSubjectId = subject,
            DisplayName = displayName,
            Role = role,
        });
    }
}
