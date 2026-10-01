using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

public class FacilityOnboardingOptions
{
    public const string SectionName = "FacilityOnboarding";

    /// <summary>
    /// How many registration numbers a new facility is given: the pool its
    /// devices draw blocks from. The dev seed gives each facility 100,000.
    /// Ranges are allocated aligned to this size, so changing it later still
    /// never overlaps a range already given.
    /// </summary>
    public long BrnRangeSize { get; set; } = 100_000;
}

public enum FacilityOnboardingResult
{
    Created,
    AreaNotFound,
    NotInACounty,
    NameTaken,
}

public sealed record FacilityOnboardingOutcome(FacilityOnboardingResult Result, Facility? Facility = null, string? Detail = null);

/// <summary>
/// Bringing a facility into the registry (pilot readiness §1). Before this,
/// only the Development seed could create one, so a pilot district had no way
/// in at all.
///
/// The two things a facility needs that must never be typed by hand are
/// computed here:
///
/// - <b>Its county</b>, from where it sits in the administrative tree. The flat
///   <see cref="Facility.CountyCode"/> is what every scope filter and report
///   groups by, and it must agree with the tree.
/// - <b>Its range of registration numbers</b>, after every range already
///   given. Devices issue numbers offline from blocks inside this range;
///   two facilities with overlapping ranges would give two children one BRN,
///   discovered years later (decision #2). Ranges are aligned to
///   <see cref="FacilityOnboardingOptions.BrnRangeSize"/>, and a unique index
///   on the range start turns two simultaneous onboardings into a retry.
/// </summary>
public class FacilityOnboardingService(NcbrsDbContext db, CountyLookup counties, FacilityOnboardingOptions options)
{
    private const int Attempts = 3;

    private static readonly AdministrativeLevel[] PlaceableLevels =
    [
        AdministrativeLevel.County,
        AdministrativeLevel.Payam,
        AdministrativeLevel.Block,
        AdministrativeLevel.Boma,
        AdministrativeLevel.Quarter,
        AdministrativeLevel.Village,
    ];

    public async Task<FacilityOnboardingOutcome> CreateAsync(
        CreateFacilityRequest request, Registrar actor, Guid? transactionId, CancellationToken cancellationToken = default)
    {
        var area = await db.AdministrativeAreas.AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.AdministrativeAreaId == request.AdministrativeAreaId, cancellationToken);

        if (area is null)
        {
            return new(FacilityOnboardingResult.AreaNotFound, Detail: "No such administrative area.");
        }

        var county = await counties.ForAreaAsync(area.AdministrativeAreaId, cancellationToken);

        // A facility is placed in a county or below it. Placed at a state, it
        // would have no county, and county is the scope every officer acts in.
        if (!PlaceableLevels.Contains(area.Level) || county is null)
        {
            return new(FacilityOnboardingResult.NotInACounty,
                Detail: $"'{area.Name}' is a {area.Level}. A facility is placed in a county, or a payam, block, boma, quarter or village within one.");
        }

        var name = request.Name.Trim();
        var taken = await db.Facilities.AnyAsync(
            facility => facility.CountyCode == county && facility.Name.ToLower() == name.ToLower(), cancellationToken);

        if (taken)
        {
            return new(FacilityOnboardingResult.NameTaken,
                Detail: $"{county} already has a facility called '{name}'. Two with one name would be told apart by nobody.");
        }

        for (var attempt = 1; ; attempt++)
        {
            var start = await NextRangeStartAsync(cancellationToken);
            var facility = new Facility
            {
                Name = name,
                Tier = request.Tier,
                ConnectivityProfile = request.ConnectivityProfile,
                AdministrativeAreaId = area.AdministrativeAreaId,
                CountyCode = county,
                BrnBlockStart = start,
                BrnBlockEnd = start + options.BrnRangeSize - 1,
                BrnBlockNextAvailable = start,
            };

            db.Facilities.Add(facility);
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Facility),
                EntityId = facility.FacilityId.ToString(),
                Action = $"FacilityCreated:{facility.BrnBlockStart}-{facility.BrnBlockEnd}",
                CountyCode = county,
                UserId = actor.RegistrarId,
                DeviceId = "web",
                TransactionId = transactionId,
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new(FacilityOnboardingResult.Created, facility);
            }
            catch (DbUpdateException) when (attempt < Attempts)
            {
                // Another onboarding took this range first: start again above it.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>The first aligned range above every range already given.</summary>
    private async Task<long> NextRangeStartAsync(CancellationToken cancellationToken)
    {
        var highest = await db.Facilities
            .Where(facility => facility.BrnBlockEnd > 0)
            .MaxAsync(facility => (long?)facility.BrnBlockEnd, cancellationToken) ?? 0;

        var size = options.BrnRangeSize;
        return (highest / size + 1) * size;
    }
}
