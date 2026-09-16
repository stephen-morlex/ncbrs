using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Data;
using NCBRS.Consumer.Models;
using NCBRS.Events;

namespace NCBRS.Consumer.Services;

public enum ProjectionOutcome
{
    /// <summary>Applied for the first time.</summary>
    Applied,

    /// <summary>Seen before. The projection already reflects it.</summary>
    AlreadySeen,

    /// <summary>Not a payload this consumer understands.</summary>
    Ignored,

    /// <summary>
    /// An amendment or annulment for a BRN the projection has not seen yet.
    /// Held, and applied when the registration arrives.
    /// </summary>
    Deferred
}

public record ProjectionResult(ProjectionOutcome Outcome, string? Detail = null);

/// <summary>
/// Builds the reporting projection from the event stream.
///
/// Three things keep this correct under at-least-once delivery, and they
/// answer different questions.
///
/// The projection is idempotent <em>by construction</em>: facts are keyed by
/// BRN and written by upsert, never by incrementing a counter. That is what
/// keeps totals right, and it would keep them right even with no ledger at
/// all -- which matters, because a ledger is one more thing that can be
/// wrong.
///
/// The ledger answers a different question: has this specific event already
/// been acted on? Nothing here needs that yet. WS-E's outbound push to the
/// National ID Authority will, because telling an external system about a
/// birth twice cannot be undone by writing the same row again.
///
/// The pending table answers a third: arrival order. Kafka orders within a
/// partition, not across topics, so a replay can deliver a correction before
/// the registration it corrects. Held rather than dropped, so a rebuild
/// lands on the same numbers as the original run.
/// </summary>
public class BirthRecordProjector(ReadModelDbContext db, ILogger<BirthRecordProjector> logger)
{
    public async Task<ProjectionResult> ApplyAsync(
        string topic,
        Guid? eventId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        // An event with no id can still be projected -- the facts are
        // idempotent regardless -- but it cannot be deduplicated, so it is
        // worth noticing rather than passing over in silence.
        if (eventId is not { } id)
        {
            logger.LogWarning(
                "Event on {Topic} carried no {Header} header; projecting it, but it cannot be deduplicated",
                topic, NCBRS.Kafka.KafkaHeaders.EventId);

            var projected = await ProjectAsync(topic, payload, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return projected;
        }

        var seen = await db.ProcessedEvents
            .FirstOrDefaultAsync(processed => processed.EventId == id, cancellationToken);

        if (seen is not null)
        {
            // Counted rather than ignored: replay is normal, but a rate of it
            // that changes is the first sign something upstream is wrong, and
            // it is invisible unless recorded.
            seen.Deliveries++;
            await db.SaveChangesAsync(cancellationToken);

            return new ProjectionResult(ProjectionOutcome.AlreadySeen,
                $"Event {id} was already applied ({seen.Deliveries} deliveries).");
        }

        var result = await ProjectAsync(topic, payload, cancellationToken);

        if (result.Outcome is ProjectionOutcome.Applied or ProjectionOutcome.Deferred)
        {
            // A deferred event counts as processed: it is safely held in the
            // pending table, so redelivering it would only duplicate the row.
            // An event that could not be read is deliberately not marked, so
            // fixing the reader later does not need the ledger cleared first.
            db.ProcessedEvents.Add(new ProcessedEvent { EventId = id, Topic = topic });
        }

        await db.SaveChangesAsync(cancellationToken);

        return result;
    }

    private async Task<ProjectionResult> ProjectAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken)
    {
        if (topic.EndsWith(".registered", StringComparison.Ordinal))
        {
            return await RegisteredAsync(payload, cancellationToken);
        }

        if (topic.EndsWith(".amended", StringComparison.Ordinal))
        {
            return await AmendedAsync(topic, payload, cancellationToken);
        }

        if (topic.EndsWith(".annulled", StringComparison.Ordinal))
        {
            return await AnnulledAsync(topic, payload, cancellationToken);
        }

        if (topic.EndsWith(".neonatal", StringComparison.Ordinal))
        {
            return await NeonatalOutcomeAsync(payload, cancellationToken);
        }

        if (topic.EndsWith(".maternal", StringComparison.Ordinal))
        {
            return await MaternalOutcomeAsync(payload, cancellationToken);
        }

        if (topic.EndsWith(".sync.audit", StringComparison.Ordinal))
        {
            return await SyncAuditAsync(payload, cancellationToken);
        }

        return new ProjectionResult(ProjectionOutcome.Ignored,
            $"No projection is defined for topic '{topic}'.");
    }

    private async Task<ProjectionResult> RegisteredAsync(string payload, CancellationToken cancellationToken)
    {
        var evt = JsonSerializer.Deserialize<BirthRegisteredEvent>(payload);

        if (evt is null)
        {
            return new ProjectionResult(ProjectionOutcome.Ignored, "Unreadable registration event.");
        }

        var fact = await db.RegistrationFacts
            .FirstOrDefaultAsync(entry => entry.Brn == evt.Brn, cancellationToken);

        if (fact is null)
        {
            fact = new RegistrationFact
            {
                Brn = evt.Brn,
                DistrictId = evt.DistrictId,
                Sex = evt.Sex
            };

            db.RegistrationFacts.Add(fact);
        }

        // Assigned unconditionally, not only on insert. A replay must write
        // the same values rather than skip -- skipping would leave a fact
        // that an earlier partial write had got wrong.
        fact.BirthRecordId = evt.BirthRecordId;
        fact.DistrictId = evt.DistrictId;
        fact.FacilityId = evt.FacilityId;
        fact.DateOfBirth = evt.DateOfBirth;
        fact.Sex = evt.Sex;
        fact.RegisteredAtUtc = evt.EventTimestampUtc;
        fact.FacilityTier = evt.FacilityTier;
        fact.VitalEventType = evt.VitalEventType;
        fact.WithinStatutoryWindow = evt.WithinStatutoryWindow;
        fact.ConfirmedAtUtc = evt.ConfirmedAtUtc;

        var held = await DrainAsync(fact, cancellationToken);

        return new ProjectionResult(ProjectionOutcome.Applied,
            held == 0 ? null : $"Applied {held} held event(s) waiting on BRN '{evt.Brn}'.");
    }

    private async Task<ProjectionResult> AmendedAsync(
        string topic, string payload, CancellationToken cancellationToken)
    {
        var evt = JsonSerializer.Deserialize<BirthRecordAmendedEvent>(payload);

        if (evt is null)
        {
            return new ProjectionResult(ProjectionOutcome.Ignored, "Unreadable amendment event.");
        }

        var fact = await FactAsync(evt.Brn, cancellationToken);

        if (fact is null)
        {
            return Hold(topic, payload, evt.Brn, evt.EventTimestampUtc);
        }

        ApplyAmendment(fact, evt);

        return new ProjectionResult(ProjectionOutcome.Applied);
    }

    private async Task<ProjectionResult> AnnulledAsync(
        string topic, string payload, CancellationToken cancellationToken)
    {
        var evt = JsonSerializer.Deserialize<BirthRecordAnnulledEvent>(payload);

        if (evt is null)
        {
            return new ProjectionResult(ProjectionOutcome.Ignored, "Unreadable annulment event.");
        }

        var fact = await FactAsync(evt.Brn, cancellationToken);

        if (fact is null)
        {
            return Hold(topic, payload, evt.Brn, evt.EventTimestampUtc);
        }

        ApplyAnnulment(fact, evt);

        return new ProjectionResult(ProjectionOutcome.Applied);
    }

    /// <summary>
    /// A death within 28 days. Not held when the registration is missing:
    /// unlike a correction, this describes a second vital event that stands
    /// on its own, and a perinatal death is not something to make invisible
    /// because the birth event has not caught up.
    /// </summary>
    private async Task<ProjectionResult> NeonatalOutcomeAsync(string payload, CancellationToken cancellationToken)
    {
        var evt = JsonSerializer.Deserialize<NeonatalOutcomeRecordedEvent>(payload);

        if (evt is null)
        {
            return new ProjectionResult(ProjectionOutcome.Ignored, "Unreadable neonatal outcome event.");
        }

        var fact = await db.NeonatalOutcomeFacts
            .FirstOrDefaultAsync(entry => entry.Brn == evt.Brn, cancellationToken);

        if (fact is null)
        {
            fact = new NeonatalOutcomeFact
            {
                Brn = evt.Brn,
                IcdPmTiming = evt.IcdPmTiming,
                IcdPmCauseCode = evt.IcdPmCauseCode
            };

            db.NeonatalOutcomeFacts.Add(fact);
        }

        fact.FacilityId = evt.FacilityId;
        fact.DeathDateUtc = evt.DeathDateUtc;
        fact.IcdPmTiming = evt.IcdPmTiming;
        fact.IcdPmCauseCode = evt.IcdPmCauseCode;
        fact.DaysAfterBirth = evt.DaysAfterBirth;
        fact.RecordedAtUtc = evt.EventTimestampUtc;

        return new ProjectionResult(ProjectionOutcome.Applied);
    }

    private async Task<ProjectionResult> MaternalOutcomeAsync(string payload, CancellationToken cancellationToken)
    {
        var evt = JsonSerializer.Deserialize<MaternalOutcomeRecordedEvent>(payload);

        if (evt is null)
        {
            return new ProjectionResult(ProjectionOutcome.Ignored, "Unreadable maternal outcome event.");
        }

        var fact = await db.MaternalOutcomeFacts
            .FirstOrDefaultAsync(entry => entry.Brn == evt.Brn, cancellationToken);

        if (fact is null)
        {
            fact = new MaternalOutcomeFact
            {
                Brn = evt.Brn,
                IcdMmCauseCode = evt.IcdMmCauseCode
            };

            db.MaternalOutcomeFacts.Add(fact);
        }

        fact.FacilityId = evt.FacilityId;
        fact.DeathDateUtc = evt.DeathDateUtc;
        fact.IcdMmCauseCode = evt.IcdMmCauseCode;
        fact.DaysAfterBirth = evt.DaysAfterBirth;
        fact.RecordedAtUtc = evt.EventTimestampUtc;

        return new ProjectionResult(ProjectionOutcome.Applied);
    }

    /// <summary>
    /// Chain of custody, and the only evidence the centre has that the
    /// offline tier is working. Keyed by batch id, so a redelivered batch
    /// overwrites its own row instead of inflating a district's sync volume.
    /// </summary>
    private async Task<ProjectionResult> SyncAuditAsync(string payload, CancellationToken cancellationToken)
    {
        var evt = JsonSerializer.Deserialize<SyncBatchProcessedEvent>(payload);

        if (evt is null)
        {
            return new ProjectionResult(ProjectionOutcome.Ignored, "Unreadable sync audit event.");
        }

        var fact = await db.SyncBatchFacts
            .FirstOrDefaultAsync(entry => entry.SyncBatchId == evt.SyncBatchId, cancellationToken);

        if (fact is null)
        {
            fact = new SyncBatchFact
            {
                SyncBatchId = evt.SyncBatchId,
                DeviceId = evt.DeviceId,
                DistrictId = evt.DistrictId,
                Status = evt.Status
            };

            db.SyncBatchFacts.Add(fact);
        }

        fact.DeviceId = evt.DeviceId;
        fact.FacilityId = evt.FacilityId;
        fact.DistrictId = evt.DistrictId;
        fact.Submitted = evt.Submitted;
        fact.Registered = evt.Registered;
        fact.Duplicates = evt.Duplicates;
        fact.Rejected = evt.Rejected;
        fact.Status = evt.Status;
        fact.SyncedAtUtc = evt.EventTimestampUtc;

        return new ProjectionResult(ProjectionOutcome.Applied);
    }

    private static void ApplyAmendment(RegistrationFact fact, BirthRecordAmendedEvent evt)
    {
        fact.AmendedAtUtc = evt.EventTimestampUtc;

        foreach (var change in evt.Changes)
        {
            // Only the fields the projection actually holds. The register is
            // the place to read a birth record; this exists to aggregate.
            if (change.Field == "Sex" && change.NewValue is { } sex)
            {
                fact.Sex = sex;
            }
            else if (change.Field == "DateOfBirth"
                     && DateTime.TryParse(change.NewValue, out var dateOfBirth))
            {
                fact.DateOfBirth = dateOfBirth;
            }
        }

        if (evt.CertificateInvalidated)
        {
            fact.CertificateRevoked = true;
        }
    }

    private static void ApplyAnnulment(RegistrationFact fact, BirthRecordAnnulledEvent evt)
    {
        // Voided, not deleted. A district's history should not silently
        // change shape, and every aggregate filters on this instead.
        fact.AnnulledAtUtc = evt.EventTimestampUtc;

        if (evt.CertificateRevoked)
        {
            fact.CertificateRevoked = true;
        }
    }

    /// <summary>
    /// Looks the fact up including one added in this same unit of work, so a
    /// registration and a correction arriving together still connect.
    /// </summary>
    private async Task<RegistrationFact?> FactAsync(string brn, CancellationToken cancellationToken)
        => db.RegistrationFacts.Local.FirstOrDefault(entry => entry.Brn == brn)
           ?? await db.RegistrationFacts.FirstOrDefaultAsync(entry => entry.Brn == brn, cancellationToken);

    private ProjectionResult Hold(string topic, string payload, string brn, DateTime occurredAtUtc)
    {
        db.PendingEvents.Add(new PendingEvent
        {
            Brn = brn,
            Topic = topic,
            Payload = payload,
            OccurredAtUtc = occurredAtUtc
        });

        return new ProjectionResult(ProjectionOutcome.Deferred,
            $"Held an event on '{topic}' for BRN '{brn}', which the projection has not seen registered yet.");
    }

    /// <summary>
    /// Applies anything that was waiting on this registration, oldest first.
    /// </summary>
    private async Task<int> DrainAsync(RegistrationFact fact, CancellationToken cancellationToken)
    {
        var waiting = await db.PendingEvents
            .Where(pending => pending.Brn == fact.Brn)
            .OrderBy(pending => pending.OccurredAtUtc)
            .ThenBy(pending => pending.ReceivedAtUtc)
            .ToListAsync(cancellationToken);

        if (waiting.Count == 0)
        {
            return 0;
        }

        foreach (var pending in waiting)
        {
            if (pending.Topic.EndsWith(".amended", StringComparison.Ordinal))
            {
                if (JsonSerializer.Deserialize<BirthRecordAmendedEvent>(pending.Payload) is { } amended)
                {
                    ApplyAmendment(fact, amended);
                }
            }
            else if (pending.Topic.EndsWith(".annulled", StringComparison.Ordinal))
            {
                if (JsonSerializer.Deserialize<BirthRecordAnnulledEvent>(pending.Payload) is { } annulled)
                {
                    ApplyAnnulment(fact, annulled);
                }
            }
        }

        db.PendingEvents.RemoveRange(waiting);

        logger.LogInformation(
            "Applied {Count} held event(s) on the arrival of BRN {Brn}", waiting.Count, fact.Brn);

        return waiting.Count;
    }
}
