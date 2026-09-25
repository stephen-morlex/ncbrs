using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Devices;
using NCBRS.Middleware;
using NCBRS.Models;

namespace NCBRS.Services;

/// <summary>
/// The device-channel rule, applied at every write that names a device.
///
/// Registration, correction, certificate issue and reprint, the maternal
/// questionnaire, both outcomes and a BRN block grant all carry a
/// <c>deviceId</c>, and each writes it into the audit trail against a legal
/// record. Guarding only registration and sync left that id taken on trust
/// everywhere else: a stolen token could correct a child's name, or draw a
/// facility's BRN range, as any device it cared to name -- including a revoked
/// one. The rule is the same everywhere because the property is: the trail
/// must say which device acted, and only the device can prove that.
///
/// One gate rather than the check pasted into each endpoint, so that the
/// refusal (its audit row, its survival past the rollback, its status code)
/// cannot drift between them.
/// </summary>
public class DeviceChannelGate(
    NcbrsDbContext db,
    DeviceEnrolmentService devices,
    RefusalAudit refusals,
    CountyLookup counties)
{
    /// <summary>
    /// Refuses unless the request came through a channel entitled to act as
    /// <paramref name="deviceId"/> at <paramref name="facilityId"/>. Returns
    /// the 403 to answer with, or null to proceed -- having marked a device
    /// that proved itself as seen.
    /// </summary>
    public async Task<ObjectResult?> RefuseUnlessPermittedAsync(
        HttpContext http,
        Registrar registrar,
        string? deviceId,
        Guid facilityId,
        string entityType,
        string entityId)
    {
        var cancellationToken = http.RequestAborted;

        var channel = await devices.CheckChannelAsync(
            http.User,
            deviceId,
            facilityId,
            await http.Request.ReadRawAsync(cancellationToken),
            http.Request.Headers[DeviceSignature.HeaderName],
            cancellationToken);

        if (channel.Accepted)
        {
            // A device that has just proved itself has been seen, exactly as
            // on sync -- which also resolves a silence alert raised against it.
            // Saved here rather than left for the act to commit: the device
            // reached the centre whether or not what it asked for succeeds.
            if (channel.Device is not null)
            {
                await devices.MarkSeenAsync(channel.Device, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            return null;
        }

        // Audited though nothing is written: acting as a device from the wrong
        // channel, or without its key, is what a stolen token looks like.
        // Recorded as a refusal so it survives the rollback of the request it
        // refused.
        refusals.Record(new AuditLog
        {
            EntityType = entityType,
            EntityId = entityId,
            CountyCode = await counties.ForFacilityAsync(facilityId, cancellationToken),
            Action = $"DeviceRefused:{channel.Outcome}",
            UserId = registrar.RegistrarId,
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? "unspecified" : deviceId,
            TransactionId = TransactionContext.Get(http)?.TransactionId
        });

        await db.SaveChangesAsync(cancellationToken);

        return ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status403Forbidden, "Device not permitted.", "data.deviceId", channel.Detail));
    }

    /// <summary>
    /// The same rule for an act on an existing record, where the facility a
    /// device must belong to is the record's, not one the body names. A number
    /// that resolves to no record proceeds, so the endpoint answers the 404 it
    /// would have: there is nothing to act on, and a refusal would claim a
    /// record the register does not hold.
    /// </summary>
    public async Task<ObjectResult?> RefuseUnlessPermittedForRecordAsync(
        HttpContext http,
        Registrar registrar,
        string? deviceId,
        string brn)
    {
        var facilityId = await db.BirthRecords
            .Where(record => record.Brn == brn || record.ProvisionalIdentifier == brn)
            .Select(record => (Guid?)record.FacilityId)
            .FirstOrDefaultAsync(http.RequestAborted);

        return facilityId is null
            ? null
            : await RefuseUnlessPermittedAsync(http, registrar, deviceId, facilityId.Value, nameof(BirthRecord), brn);
    }
}
