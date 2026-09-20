using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

/// <summary>
/// W4. Reading the audit trail.
///
/// The trail is legally load-bearing — draft 4.3 — and until now it was
/// readable only by someone with database access. That is the wrong shape for
/// something whose purpose is accountability: a control nobody can consult is
/// a control that only works in retrospect, after a dispute has already
/// escalated to whoever holds the credentials.
///
/// **Reading it is itself written to it.** That is not decoration. Since W1
/// the trail records the names people searched for, so trawling it to see
/// what a colleague looked up is exactly the misuse it exists to expose —
/// and `RequestLog`, which records every `/api` call, stores the path and
/// status but **not who made the call**. Without a row here, reading the
/// trail would be the one privileged act it could not account for.
///
/// The recursion is bounded and cheap: oversight reads are rare, and each
/// writes one row.
/// </summary>
[ApiController]
[Route("api/audit")]
[Authorize(Policy = NcbrsRoles.CanReadAuditTrail)]
[Produces("application/json")]
public class AuditController(
    NcbrsDbContext db,
    CurrentRegistrarService currentRegistrar,
    CountyScopeResolver scopes,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// The trail, filtered and scoped.
    ///
    /// Two shapes of question, and they are scoped differently because they
    /// are different questions:
    ///
    /// - **"What happened to this record?"** — given a `brn`, every entry
    ///   against it whoever acted. Gated on the record being in the caller's
    ///   district, which is the same boundary that governs seeing the record
    ///   at all.
    /// - **"What is my district accountable for?"** — without a `brn`,
    ///   entries carrying the caller's district, which names the district
    ///   whose register changed rather than the district of whoever changed
    ///   it. A ministry admin annulling a record here produces a row this
    ///   district can read.
    ///
    /// Rows written before `AuditLog.DistrictId` existed fall back to the
    /// actor's district, which is the best that can be said of them. They can
    /// never be given one — the table is append-only and enforced so at the
    /// database — so for that era an act on this district's records by
    /// someone outside it remains invisible. That gap is closed for
    /// everything written since, and is permanent for what came before.
    /// </summary>
    [HttpGet(Name = "GetAuditTrail")]
    [ProducesResponseType(typeof(Page<AuditEntryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Page<AuditEntryResponse>>> Get(
        [FromQuery] string? brn = null,
        [FromQuery] Guid? actorId = null,
        [FromQuery] string? deviceId = null,
        [FromQuery] string? entityType = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? districtId = null,
        [FromQuery] int limit = PageRequest.DefaultLimit,
        [FromQuery] string? after = null)
    {
        var caller = await currentRegistrar.GetAsync(HttpContext.RequestAborted);

        if (caller is null)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden,
                "No registrar record for this account.",
                string.Empty,
                "This account is not provisioned in the registry, so a read of the trail "
                + "cannot be attributed to anyone."));
        }

        if (from is { } start && to is { } end && end < start)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest,
                "The date range ends before it starts.",
                "to",
                "'to' must not be earlier than 'from'."));
        }

        var scope = await scopes.ResolveAsync(
            User, caller, districtId, HttpContext.RequestAborted);

        if (!scope.IsAllowed)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, scope.Title, scope.Field, scope.Message));
        }

        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(brn))
        {
            var wanted = brn.Trim();

            // The record must be one the caller could already see. Answering
            // otherwise would let the trail disclose what the record itself
            // withholds -- that a given BRN exists in another district.
            if (scope.Scope.DistrictId is { } district)
            {
                var visible = await db.BirthRecords
                    .AnyAsync(record =>
                        (record.Brn == wanted || record.ProvisionalIdentifier == wanted)
                        && record.Facility != null
                        && record.Facility.CountyCode == district,
                        HttpContext.RequestAborted);

                if (!visible)
                {
                    return ApiErrors.Result(ApiErrors.Single(
                        StatusCodes.Status404NotFound,
                        "No such record.",
                        "brn",
                        "No record with that number in your district."));
                }
            }

            query = query.Where(entry => entry.EntityId == wanted);
        }
        else if (scope.Scope.DistrictId is { } district)
        {
            // Rows written since the district column exists carry it, and it
            // is the accurate answer: it names the district whose register
            // changed, not the district of whoever changed it.
            //
            // Rows written before it fall back to the actor's district, which
            // is the best that can be said of them. They can never be given a
            // district retroactively -- AuditLogs is append-only and enforced
            // so at the database -- so the fallback is permanent for that
            // era, and misses acts performed on this district's records by
            // someone outside it.
            var actors = db.Registrars
                .Where(registrar =>
                    registrar.Facility != null && registrar.Facility.CountyCode == district)
                .Select(registrar => registrar.RegistrarId);

            query = query.Where(entry =>
                entry.DistrictId == district
                || (entry.DistrictId == AuditLog.Unknown
                    && entry.UserId != null
                    && actors.Contains(entry.UserId.Value)));
        }

        if (actorId is { } actor)
        {
            query = query.Where(entry => entry.UserId == actor);
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var device = deviceId.Trim();

            query = query.Where(entry => entry.DeviceId == device);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var type = entityType.Trim();

            query = query.Where(entry => entry.EntityType == type);
        }

        if (from is { } start2)
        {
            query = query.Where(entry => entry.TimestampUtc >= start2);
        }

        if (to is { } end2)
        {
            query = query.Where(entry => entry.TimestampUtc <= end2);
        }

        var total = await query.CountAsync(HttpContext.RequestAborted);

        var page = new PageRequest { Limit = limit, After = after };

        // Most recent first: someone opening the trail is almost always
        // asking what just happened.
        var ordered = query
            .OrderByDescending(entry => entry.TimestampUtc)
            .ThenByDescending(entry => entry.AuditLogId);

        if (PageCursor.TryDecode(page.After, out var cursor) && cursor.IsDateTime())
        {
            var at = cursor.AsDateTime();

            ordered = (IOrderedQueryable<AuditLog>)ordered.Where(entry =>
                entry.TimestampUtc < at
                || (entry.TimestampUtc == at && entry.AuditLogId < cursor.Id));
        }

        var fetched = await ordered
            .Take(page.EffectiveLimit + 1)
            .ToListAsync(HttpContext.RequestAborted);

        var result = Page<AuditLog>.From(
            fetched,
            total,
            page.EffectiveLimit,
            entry => PageCursor.For(entry.TimestampUtc, entry.AuditLogId));

        // Actor names resolved in one query rather than per row. A trail that
        // names "0199a1b2-…" is a trail nobody can read, which is the same
        // objection W3 answers for the queues.
        var actorIds = result.Items
            .Where(entry => entry.UserId is not null)
            .Select(entry => entry.UserId!.Value)
            .Distinct()
            .ToList();

        var names = await db.Registrars
            .Where(registrar => actorIds.Contains(registrar.RegistrarId))
            .ToDictionaryAsync(
                registrar => registrar.RegistrarId,
                registrar => registrar.DisplayName,
                HttpContext.RequestAborted);

        WriteReadEntry(caller, scope.Scope, brn, actorId, result.Items.Count, total);

        await db.SaveChangesAsync(HttpContext.RequestAborted);

        return new Page<AuditEntryResponse>(
            result.Items.Select(entry => new AuditEntryResponse(
                entry.AuditLogId,
                entry.EntityType,
                entry.EntityId,
                entry.DistrictId,
                entry.Action,
                entry.UserId,
                entry.UserId is { } id && names.TryGetValue(id, out var name) ? name : null,
                entry.DeviceId,
                entry.TransactionId,
                entry.TimestampUtc)).ToList(),
            result.Total,
            result.NextCursor);
    }

    /// <summary>
    /// Records that the trail was read, and what was asked of it.
    ///
    /// The filter matters as much as the fact: "read the trail" and "read
    /// every entry for this one registrar" are different acts, and only the
    /// second looks like someone checking up on a colleague.
    /// </summary>
    private void WriteReadEntry(
        Registrar caller,
        SearchScope scope,
        string? brn,
        Guid? actorId,
        int returned,
        int total)
    {
        var filters = new List<string>();

        if (!string.IsNullOrWhiteSpace(brn))
        {
            filters.Add($"brn={brn.Trim()}");
        }

        if (actorId is { } actor)
        {
            filters.Add($"actor={actor}");
        }

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = "AuditTrail",
            EntityId = scope.DistrictId ?? AuditLog.Unattributed,
            DistrictId = scope.DistrictId ?? AuditLog.Unattributed,
            Action = $"Read:{(filters.Count > 0 ? string.Join(",", filters) : "all")};"
                     + $"returned={returned};total={total}",
            UserId = caller.RegistrarId,
            DeviceId = "web",
            TransactionId = TransactionContext.Get(HttpContext)?.TransactionId,
            TimestampUtc = clock.GetUtcNow().UtcDateTime
        });
    }
}

/// <summary>
/// One entry, with the actor named.
///
/// <c>ActorName</c> is null where the actor is not a registrar this caller
/// can see, or where the row records something no person did — a sweep, a
/// projection. Null says "not a person here", which is different from a blank
/// name.
/// </summary>
public record AuditEntryResponse(
    Guid AuditLogId,
    string EntityType,
    string EntityId,

    /// <summary>
    /// The district this act belongs to. Empty for rows written before the
    /// column existed, which can never be given one -- see AuditLog.Unknown.
    /// Mostly of interest to the Ministry, who read across districts; a
    /// district officer sees their own on every row that has one.
    /// </summary>
    string DistrictId,
    string Action,
    Guid? ActorId,
    string? ActorName,
    string DeviceId,
    Guid? TransactionId,
    DateTime TimestampUtc);
