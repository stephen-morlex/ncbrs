using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NCBRS.Models;

namespace NCBRS.Data;

/// <summary>
/// Development-only sample data shaped like a real South Sudan deployment: a
/// small fleet of facilities across three states and every tier, the registrars
/// that map to the imported Keycloak realm users, and a handful of enrolled
/// devices — so a fresh dev database exercises scoping, sync, reporting and
/// device-silence out of the box, reproducibly.
///
/// Each facility sits at its true sub-county level: <see cref="Facility.AdministrativeAreaId"/>
/// points at the <b>payam</b> it is in (seeded from the official COD), and the
/// transitional <see cref="Facility.DistrictId"/> carries the <b>county</b>
/// p-code — the level scoping and reporting resolve to by walking up the tree.
///
/// Idempotent, keyed by fixed ids, by registrar subject and by device id.
/// **Never runs in production**: real facilities, registrars and devices are
/// provisioned through their own processes and a production registry starts
/// empty. Only invoked from the Development startup path.
/// </summary>
public static class DevelopmentDataSeeder
{
    private static Guid Fac(int n) => new($"0199c000-0000-7000-8000-0000000f000{n}");

    /// <summary>
    /// The fleet: name, tier, connectivity, the payam p-code it sits in, the
    /// county p-code its <c>DistrictId</c> carries, and the first number of its
    /// 100 000-wide BRN block (blocks never overlap). Connectivity follows the
    /// tier — hospitals on the grid, village posts offline-first — as it does in
    /// the field.
    /// </summary>
    private static readonly FleetMember[] Fleet =
    [
        new(Fac(1), "Juba Teaching Hospital",             FacilityTier.Hospital,         ConnectivityProfile.AlwaysOn,     "SS010105", "SS0101", 100_000),
        new(Fac(2), "Munuki Primary Health Care Centre",  FacilityTier.Clinic,           ConnectivityProfile.Intermittent, "SS010111", "SS0101", 200_000),
        new(Fac(3), "Terekeka County Hospital",           FacilityTier.Hospital,         ConnectivityProfile.Intermittent, "SS010507", "SS0105", 300_000),
        new(Fac(4), "Tali Primary Health Care Unit",      FacilityTier.VillageHealthPost, ConnectivityProfile.OfflineFirst, "SS010506", "SS0105", 400_000),
        new(Fac(5), "Torit State Hospital",               FacilityTier.Hospital,         ConnectivityProfile.AlwaysOn,     "SS020807", "SS0208", 500_000),
        new(Fac(6), "Imurok Primary Health Care Unit",    FacilityTier.VillageHealthPost, ConnectivityProfile.OfflineFirst, "SS020805", "SS0208", 600_000),
        new(Fac(7), "Bor Civil Hospital",                 FacilityTier.Hospital,         ConnectivityProfile.Intermittent, "SS030303", "SS0303", 700_000),
        new(Fac(8), "Makuach Primary Health Care Unit",   FacilityTier.VillageHealthPost, ConnectivityProfile.OfflineFirst, "SS030306", "SS0303", 800_000),
    ];

    // Registrar ids are fixed; the subjects match the imported dev realm so
    // existing dev logins keep resolving. Display names match the realm users.
    private static readonly Guid NurseId = new("0199c000-0000-7000-8000-0000000000a1");
    private static readonly Guid DoctorId = new("0199c000-0000-7000-8000-0000000000a2");
    private static readonly Guid CountyOfficerId = new("0199c000-0000-7000-8000-0000000000a3");
    private static readonly Guid MinistryId = new("0199c000-0000-7000-8000-0000000000a4");

    public static async Task SeedAsync(NcbrsDbContext db, CancellationToken cancellationToken = default)
    {
        // Areas are seeded first. Every facility is placed in a payam; without
        // the geography there is nowhere to place them, so this is a no-op
        // rather than an error.
        var payamCodes = Fleet.Select(m => m.PayamCode).Distinct().ToArray();
        var payams = await db.AdministrativeAreas
            .Where(a => payamCodes.Contains(a.Code))
            .ToDictionaryAsync(a => a.Code, a => a.AdministrativeAreaId, cancellationToken);

        if (payams.Count < payamCodes.Length)
        {
            return;
        }

        foreach (var member in Fleet)
        {
            if (await db.Facilities.AnyAsync(f => f.FacilityId == member.FacilityId, cancellationToken))
            {
                continue;
            }

            db.Facilities.Add(new Facility
            {
                FacilityId = member.FacilityId,
                Name = member.Name,
                Tier = member.Tier,
                ConnectivityProfile = member.Connectivity,
                DistrictId = member.CountyCode,
                AdministrativeAreaId = payams[member.PayamCode],
                BrnBlockStart = member.BlockStart,
                BrnBlockEnd = member.BlockStart + 99_999,
                BrnBlockNextAvailable = member.BlockStart,
            });
        }

        // A facility registrar and a doctor at hospitals; the county officer's
        // county (resolved from their facility) is Juba, where most of the dev
        // data sits; the ministry admin is national but must belong to a
        // facility row, so it is placed at the capital's teaching hospital.
        await EnsureRegistrar(db, NurseId, Fac(1),
            "11111111-1111-4111-8111-111111111111", "Alice Lado", RegistrarRole.FacilityRegistrar, cancellationToken);
        await EnsureRegistrar(db, DoctorId, Fac(5),
            "22222222-2222-4222-8222-222222222222", "Moses Kenyi", RegistrarRole.FacilityRegistrar, cancellationToken);
        await EnsureRegistrar(db, CountyOfficerId, Fac(1),
            "33333333-3333-4333-8333-333333333333", "Nyandeng Wani", RegistrarRole.DistrictOfficer, cancellationToken);
        await EnsureRegistrar(db, MinistryId, Fac(1),
            "44444444-4444-4444-8444-444444444444", "Aluel Lako", RegistrarRole.MinistryAdmin, cancellationToken);

        // Enrolled devices, so sync and device-silence have something to show.
        // The last-seen times deliberately span the states a district queue
        // sorts by: a healthy grid terminal, an offline-first post reporting
        // within its generous window, one long silent, and one that has never
        // reported (the deployment that failed at handover).
        var now = DateTime.UtcNow;
        await EnsureDevice(db, "TERMINAL-JUBA-01", Fac(1), "Juba Teaching, registration desk",
            enrolledAtUtc: now.AddDays(-120), lastSeenAtUtc: now.AddHours(-3), cancellationToken);
        await EnsureDevice(db, "TABLET-TALI-01", Fac(4), "Tali post, tablet 1",
            enrolledAtUtc: now.AddDays(-90), lastSeenAtUtc: now.AddDays(-9), cancellationToken);
        await EnsureDevice(db, "TABLET-MAKUACH-01", Fac(8), "Makuach post, tablet 1",
            enrolledAtUtc: now.AddDays(-75), lastSeenAtUtc: now.AddDays(-30), cancellationToken);
        await EnsureDevice(db, "TABLET-IMUROK-01", Fac(6), "Imurok post, tablet 1 (never reported)",
            enrolledAtUtc: now.AddDays(-40), lastSeenAtUtc: null, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureRegistrar(
        NcbrsDbContext db, Guid registrarId, Guid facilityId, string subject,
        string displayName, RegistrarRole role, CancellationToken cancellationToken)
    {
        if (await db.Registrars.AnyAsync(r => r.ExternalSubjectId == subject, cancellationToken))
        {
            return;
        }

        db.Registrars.Add(new Registrar
        {
            RegistrarId = registrarId,
            FacilityId = facilityId,
            ExternalSubjectId = subject,
            DisplayName = displayName,
            Role = role,
        });
    }

    private static async Task EnsureDevice(
        NcbrsDbContext db, string deviceId, Guid facilityId, string label,
        DateTime enrolledAtUtc, DateTime? lastSeenAtUtc, CancellationToken cancellationToken)
    {
        if (await db.Devices.AnyAsync(d => d.DeviceId == deviceId, cancellationToken))
        {
            return;
        }

        // The centre only ever stores the public half; the private key is
        // discarded here, exactly as a real device would keep it. Dev sets
        // RequireSignature false, so no signing key is needed to sync.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        db.Devices.Add(new Device
        {
            DeviceId = deviceId,
            FacilityId = facilityId,
            Status = DeviceStatus.Enrolled,
            PublicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem(),
            Label = label,
            EnrolledAtUtc = enrolledAtUtc,
            EnrolledByRegistrarId = CountyOfficerId,
            LastSeenAtUtc = lastSeenAtUtc,
        });
    }

    private sealed record FleetMember(
        Guid FacilityId, string Name, FacilityTier Tier, ConnectivityProfile Connectivity,
        string PayamCode, string CountyCode, int BlockStart);
}
