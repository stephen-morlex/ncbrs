using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// Searching the register by name and date, for a registrar helping a family
/// who lost their certificate and has a name and an approximate date rather
/// than a number.
///
/// **A name search across the national register is a surveillance
/// capability**, and the whole design of this class follows from saying so
/// out loud. Exact-BRN lookup is a different act and stays unrestricted: a
/// family holding a certificate with that number printed on it is not
/// searching, they are presenting an identifier they already hold.
///
/// Three controls, none of which can be bolted on afterwards:
///
/// 1. **Scope comes from the token, never from a parameter.** A district id
///    in a query string is a district id a caller can change. The caller's
///    district is resolved from their registrar record, and a request naming
///    a different one is refused rather than quietly narrowed -- a caller who
///    thinks they searched Juba and actually searched their own district
///    reads an empty result as "no such child".
/// 2. **`ministry-admin` is exempt**, because national oversight is their
///    function. Nobody else is, including a district officer: overseeing
///    several facilities is not overseeing several districts.
/// 3. **Every search is audited**, criteria and result count included. An
///    audit that recorded only "a search happened" would not distinguish a
///    registrar helping one family from someone enumerating a district.
///
/// A registrar helping a family who moved districts will hit this, and that
/// is the cost of the decision rather than a bug in it. The remedy is a
/// referral to the Ministry, not a quiet widening of the scope later.
/// </summary>
public class RecordSearchService(NcbrsDbContext db, TimeProvider clock)
{
    /// <summary>
    /// The shortest name fragment worth searching on. One letter matches a
    /// large fraction of a district, which is enumeration wearing a search's
    /// clothes.
    /// </summary>
    public const int MinimumNameLength = 2;

    /// <summary>
    /// Runs the search within the caller's scope and writes the audit row.
    ///
    /// The audit row is added to the same change tracker as nothing else, so
    /// the caller must save. That is deliberate: it keeps the write in the
    /// controller's transaction and makes "searched but not recorded"
    /// impossible to reach by forgetting a call here.
    /// </summary>
    public async Task<Page<BirthRecordSearchHit>> SearchAsync(
        RecordSearchCriteria criteria,
        SearchScope scope,
        PageRequest page,
        AuditContext audit,
        CancellationToken cancellationToken = default)
    {
        var query = db.BirthRecords
            .AsNoTracking()
            .Include(record => record.ChildPerson)
            .Include(record => record.Facility)
            .AsQueryable();

        // The scope first, so nothing below can widen it. A district officer
        // whose own facility list is empty still gets their district, because
        // the scope is the district and not the facilities they happen to
        // have.
        if (scope.DistrictId is { } districtId)
        {
            query = query.Where(record =>
                record.Facility != null && record.Facility.DistrictId == districtId);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Name))
        {
            var name = criteria.Name.Trim();

            // Case-insensitive contains. Note this is a scan; at national
            // volume the district filter is what keeps it survivable, which
            // is one more reason the scope is not optional.
            query = query.Where(record =>
                record.ChildPerson != null
                && EF.Functions.Like(record.ChildPerson.FullName, $"%{name}%"));
        }

        if (criteria.BornFrom is { } from)
        {
            query = query.Where(record => record.DateOfBirth >= from);
        }

        if (criteria.BornTo is { } to)
        {
            query = query.Where(record => record.DateOfBirth <= to);
        }

        if (criteria.FacilityId is { } facilityId)
        {
            query = query.Where(record => record.FacilityId == facilityId);
        }

        if (criteria.Status is { } status)
        {
            query = query.Where(record => record.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);

        // Newest birth first: a registrar helping a family is far more often
        // looking for a recent registration than an old one.
        var ordered = query
            .OrderByDescending(record => record.DateOfBirth)
            .ThenByDescending(record => record.BirthRecordId);

        var afterCursor = PageCursor.TryDecode(page.After, out var cursor) ? cursor : (PageCursor?)null;

        if (afterCursor is { } position && position.IsDateTime())
        {
            var at = position.AsDateTime();

            ordered = (IOrderedQueryable<BirthRecord>)ordered.Where(record =>
                record.DateOfBirth < at
                || (record.DateOfBirth == at && record.BirthRecordId < position.Id));
        }

        // One more than asked for, so the cursor is only issued when there is
        // genuinely a next page.
        var fetched = await ordered
            .Take(page.EffectiveLimit + 1)
            .ToListAsync(cancellationToken);

        var result = Page<BirthRecord>.From(
            fetched,
            total,
            page.EffectiveLimit,
            record => PageCursor.For(record.DateOfBirth, record.BirthRecordId));

        WriteAuditEntry(criteria, scope, result.Items.Count, total, audit);

        return new Page<BirthRecordSearchHit>(
            result.Items.Select(ToHit).ToList(),
            result.Total,
            result.NextCursor);
    }

    /// <summary>
    /// What a search result may show.
    ///
    /// Deliberately less than the record: enough to recognise the right child
    /// and open them, and no more. Someone who has found the record can fetch
    /// it by BRN, which is audited in its own right; a result list that
    /// carried parents' names would spread them across every search that
    /// happened to match.
    /// </summary>
    private static BirthRecordSearchHit ToHit(BirthRecord record) => new(
        record.Brn,
        record.ChildPerson?.FullName ?? string.Empty,
        record.DateOfBirth,
        record.Sex,
        record.Status,
        record.Facility?.Name ?? string.Empty,
        record.Facility?.DistrictId ?? string.Empty,
        record.ProvisionalIdentifier);

    /// <summary>
    /// Records what was searched for, not merely that a search occurred.
    ///
    /// The criteria are the point. A trail showing twelve searches tells
    /// nobody anything; a trail showing twelve searches for the same surname
    /// across a district is the pattern the control exists to make visible.
    ///
    /// Note this writes the searched name into the audit table, which means
    /// the trail now holds names of people who may not be in the register at
    /// all. That is a real cost, accepted knowingly: an audit that cannot say
    /// what was looked for cannot distinguish use from misuse.
    /// </summary>
    private void WriteAuditEntry(
        RecordSearchCriteria criteria,
        SearchScope scope,
        int returned,
        int total,
        AuditContext audit)
    {
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = "BirthRecordSearch",

            // The register, scoped. Keeps "every search in this district"
            // answerable without overloading the column that everywhere else
            // holds a record's own identifier.
            EntityId = scope.DistrictId ?? AuditLog.Unattributed,
            DistrictId = scope.DistrictId ?? AuditLog.Unattributed,

            Action = $"Search:{criteria.Describe()};returned={returned};total={total}",
            UserId = audit.RegistrarId,
            DeviceId = audit.DeviceId,
            TransactionId = audit.TransactionId,
            TimestampUtc = clock.GetUtcNow().UtcDateTime
        });
    }
}

/// <summary>
/// Who is searching and how the write is attributed. Passed in rather than
/// read from an HttpContext here, so the service stays testable and has no
/// opinion about the web layer.
/// </summary>
public readonly record struct AuditContext(Guid? RegistrarId, string DeviceId, Guid? TransactionId);

/// <summary>
/// The district a search is confined to, or null for the Ministry, who are
/// the only callers entitled to the whole register.
/// </summary>
public readonly record struct SearchScope(string? DistrictId)
{
    public static SearchScope National => new((string?)null);

    public static SearchScope District(string districtId) => new(districtId);
}

public record RecordSearchCriteria
{
    public string? Name { get; init; }

    public DateTime? BornFrom { get; init; }

    public DateTime? BornTo { get; init; }

    public Guid? FacilityId { get; init; }

    public RecordStatus? Status { get; init; }

    /// <summary>
    /// Whether this narrows the register at all.
    ///
    /// A search with no name and no date range is not a search, it is a bulk
    /// read of a district, and it must not be reachable by leaving the form
    /// blank. A facility or a status alone is still a bulk read -- "every
    /// birth at this clinic" -- so neither counts.
    /// </summary>
    public bool IsSpecific =>
        (Name is not null && Name.Trim().Length >= RecordSearchService.MinimumNameLength)
        || BornFrom is not null
        || BornTo is not null;

    /// <summary>
    /// A stable, readable rendering for the audit trail. Ordered fixed rather
    /// than by what was supplied, so two identical searches audit identically
    /// and a trail can be grouped.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Name))
        {
            parts.Add($"name={Name.Trim()}");
        }

        if (BornFrom is { } from)
        {
            parts.Add($"from={from:yyyy-MM-dd}");
        }

        if (BornTo is { } to)
        {
            parts.Add($"to={to:yyyy-MM-dd}");
        }

        if (FacilityId is { } facilityId)
        {
            parts.Add($"facility={facilityId}");
        }

        if (Status is { } status)
        {
            parts.Add($"status={status}");
        }

        return parts.Count > 0 ? string.Join(",", parts) : "none";
    }
}

/// <summary>
/// One result. See <see cref="RecordSearchService.ToHit"/> for why it carries
/// this much and no more.
/// </summary>
public record BirthRecordSearchHit(
    string Brn,
    string ChildFullName,
    DateTime DateOfBirth,
    Sex Sex,
    RecordStatus Status,
    string FacilityName,
    string DistrictId,
    string? ProvisionalIdentifier);
