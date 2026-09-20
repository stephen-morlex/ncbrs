using Microsoft.EntityFrameworkCore;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Data;

/// <summary>
/// Development-only demo births, so a fresh dev environment shows a living
/// dashboard, DHIS2 export and device history instead of an empty one.
///
/// Deliberately goes through the <b>real</b> registration path
/// (<see cref="BirthRegistrationService.RegisterAsync"/>): each birth draws a
/// BRN from its facility's block, is reconciled and confirmed, stages a
/// <c>BirthRegisteredEvent</c> in the outbox and writes its audit rows exactly
/// as a real registration does — so what the dashboard projects is produced by
/// the same code the product runs, never hand-assembled facts that could drift.
///
/// Idempotent by the simplest possible check: it does nothing once any birth
/// exists. **Never runs outside Development** (its only caller is the
/// Development startup block); a production registry starts and stays empty
/// until real births are filed.
/// </summary>
public static class DemoBirthSeeder
{
    // Deterministic, so a rebuilt dev database is identical run to run.
    private static readonly string[] FemaleGiven =
        ["Ayen", "Nyandeng", "Aluel", "Achol", "Adut", "Awut", "Nyakuoth", "Ajok", "Nyibol", "Poni"];
    private static readonly string[] MaleGiven =
        ["Deng", "Garang", "Chol", "Wani", "Majok", "Mabior", "Kuol", "Lado", "Dut", "Bol"];
    private static readonly string[] Surnames =
        ["Deng", "Wani", "Lado", "Kenyi", "Majok", "Malith", "Akol", "Lueth", "Gatwech", "Ladu"];

    // A registrar name per facility that lacks a seeded one (the realm-backed
    // registrars already staff Juba Teaching and Torit).
    private static readonly string[] RegistrarGiven =
        ["Sarah", "James", "Rebecca", "Peter", "Grace", "Simon", "Mary", "John"];

    private const int HospitalBirths = 12;
    private const int ClinicBirths = 8;
    private const int VillagePostBirths = 5;

    public static async Task SeedAsync(
        NcbrsDbContext db, BirthRegistrationService registration, CancellationToken cancellationToken = default)
    {
        if (await db.BirthRecords.AnyAsync(cancellationToken))
        {
            return;
        }

        var facilities = await db.Facilities.OrderBy(f => f.Name).ToListAsync(cancellationToken);
        var random = new Random(20260920);
        var registrarIndex = 0;

        foreach (var facility in facilities)
        {
            var given = RegistrarGiven[registrarIndex % RegistrarGiven.Length];
            var surname = Surnames[registrarIndex % Surnames.Length];
            registrarIndex++;
            var registrar = await EnsureRegistrarAsync(db, facility, $"{given} {surname}", cancellationToken);

            var count = facility.Tier switch
            {
                FacilityTier.Hospital => HospitalBirths,
                FacilityTier.Clinic => ClinicBirths,
                _ => VillagePostBirths,
            };

            // Reserve the numbers up front so each drawn BRN falls below
            // BrnBlockNextAvailable and reconciles to Confirmed — the same
            // arithmetic BrnReconciler applies. Without this the block looks
            // ungranted and every demo record would sit Provisional.
            var firstBrn = facility.BrnBlockNextAvailable;
            facility.BrnBlockNextAvailable = firstBrn + count;
            await db.SaveChangesAsync(cancellationToken);

            for (var i = 0; i < count; i++)
            {
                var request = BuildBirth(facility.FacilityId, firstBrn + i, random);
                var result = await registration.RegisterAsync(request, registrar, Guid.NewGuid(), cancellationToken);

                if (result.Outcome != RegistrationOutcome.Registered)
                {
                    // A demo birth that the real path rejects is a bug in this
                    // seeder, not something to paper over — surface it loudly.
                    throw new InvalidOperationException(
                        $"Demo birth for {facility.Name} was not registered: {result.Outcome} — {result.Detail}");
                }
            }
        }
    }

    private static RegisterBirthRequest BuildBirth(Guid facilityId, long brn, Random random)
    {
        var now = DateTime.UtcNow;

        // One birth in eight is late, to give the timeliness indicators a real
        // (not all-100%) share and the late-registration queue something to
        // hold. The rest register within days of the birth, as most do.
        var isLate = random.Next(8) == 0;

        DateTime dateOfBirth;
        DateTime registeredAt;
        if (isLate)
        {
            dateOfBirth = now.AddDays(-random.Next(140, 260)).Date;
            registeredAt = now.AddDays(-random.Next(1, 12));
        }
        else
        {
            dateOfBirth = now.AddDays(-random.Next(1, 175)).Date;
            // Registered a few days after the birth, never after now.
            var delay = random.Next(1, 15);
            registeredAt = dateOfBirth.AddDays(delay) > now ? now : dateOfBirth.AddDays(delay);
        }

        var sex = random.Next(20) switch
        {
            0 => Sex.Undetermined,
            var n when n % 2 == 0 => Sex.Male,
            _ => Sex.Female,
        };

        var surname = Surnames[random.Next(Surnames.Length)];
        var childGiven = sex == Sex.Female ? FemaleGiven[random.Next(FemaleGiven.Length)] : MaleGiven[random.Next(MaleGiven.Length)];

        return new RegisterBirthRequest
        {
            Brn = brn.ToString(),
            FacilityId = facilityId,
            ChildFullName = $"{childGiven} {surname}",
            DateOfBirth = dateOfBirth,
            Sex = sex,
            BirthWeightGrams = 2500 + random.Next(0, 1700),
            GestationalAgeWeeks = 37 + random.Next(0, 5),
            Plurality = BirthPlurality.Singleton,
            BirthOrder = 1 + random.Next(0, 4),
            MotherFullName = $"{FemaleGiven[random.Next(FemaleGiven.Length)]} {surname}",
            FatherFullName = $"{MaleGiven[random.Next(MaleGiven.Length)]} {surname}",
            DeviceId = "SEED",
            RegisteredAtUtc = registeredAt,
            LateRegistration = isLate
                ? new LateRegistrationDetails
                {
                    EvidenceType = LateRegistrationEvidenceType.AntenatalOrDeliveryCard,
                    EvidenceReference = $"ANC-{brn}",
                    DeclarantName = $"{FemaleGiven[random.Next(FemaleGiven.Length)]} {surname}",
                    DeclarantRelationship = "Mother",
                }
                : null,
        };
    }

    private static async Task<Registrar> EnsureRegistrarAsync(
        NcbrsDbContext db, Facility facility, string displayName, CancellationToken cancellationToken)
    {
        var existing = await db.Registrars
            .FirstOrDefaultAsync(r => r.FacilityId == facility.FacilityId && r.Role == RegistrarRole.FacilityRegistrar, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // A data-only registrar: real staff who register through the facility's
        // device but have no admin-console login seeded. Its subject is derived
        // from the facility id so it is stable across reseeds.
        var registrar = new Registrar
        {
            FacilityId = facility.FacilityId,
            ExternalSubjectId = $"seed-registrar-{facility.FacilityId}",
            DisplayName = displayName,
            Role = RegistrarRole.FacilityRegistrar,
        };
        db.Registrars.Add(registrar);
        await db.SaveChangesAsync(cancellationToken);
        return registrar;
    }
}
