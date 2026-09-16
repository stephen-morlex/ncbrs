using Microsoft.EntityFrameworkCore;
using NCBRS.Models;

namespace NCBRS.Data;

public class NcbrsDbContext(DbContextOptions<NcbrsDbContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<Facility> Facilities => Set<Facility>();
    public DbSet<Registrar> Registrars => Set<Registrar>();
    public DbSet<BirthRecord> BirthRecords => Set<BirthRecord>();
    public DbSet<MaternalStatistics> MaternalStatistics => Set<MaternalStatistics>();
    public DbSet<NeonatalOutcome> NeonatalOutcomes => Set<NeonatalOutcome>();
    public DbSet<MaternalOutcome> MaternalOutcomes => Set<MaternalOutcome>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<SyncBatch> SyncBatches => Set<SyncBatch>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<DuplicateCandidate> DuplicateCandidates => Set<DuplicateCandidate>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<BirthRecordAmendment> BirthRecordAmendments => Set<BirthRecordAmendment>();
    public DbSet<CertificateRevocation> CertificateRevocations => Set<CertificateRevocation>();
    public DbSet<LateRegistration> LateRegistrations => Set<LateRegistration>();
    public DbSet<RecordAnnulment> RecordAnnulments => Set<RecordAnnulment>();
    public DbSet<AmendmentConflict> AmendmentConflicts => Set<AmendmentConflict>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceAlert> DeviceAlerts => Set<DeviceAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // BRN must be unique across the whole system -- this is the field
        // the offline block-allocation strategy (Facility.BrnBlockStart/End)
        // exists to protect.
        modelBuilder.Entity<BirthRecord>()
            .HasIndex(b => b.Brn)
            .IsUnique();

        // Child/Mother/Father all point at Person -- configure each
        // relationship explicitly so EF doesn't try to guess which FK
        // belongs to which navigation (it can't infer this the way
        // Eloquent's convention-based relationships would).
        modelBuilder.Entity<BirthRecord>()
            .HasOne(b => b.ChildPerson)
            .WithMany()
            .HasForeignKey(b => b.ChildPersonId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<BirthRecord>()
            .HasOne(b => b.MotherPerson)
            .WithMany()
            .HasForeignKey(b => b.MotherPersonId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<BirthRecord>()
            .HasOne(b => b.FatherPerson)
            .WithMany()
            .HasForeignKey(b => b.FatherPersonId)
            .OnDelete(DeleteBehavior.Restrict);

        // One-to-one follow-on outcomes, keyed by the BirthRecord's own PK.
        modelBuilder.Entity<NeonatalOutcome>()
            .HasKey(n => n.BirthRecordId);
        modelBuilder.Entity<NeonatalOutcome>()
            .HasOne(n => n.BirthRecord)
            .WithOne(b => b.NeonatalOutcome)
            .HasForeignKey<NeonatalOutcome>(n => n.BirthRecordId);

        modelBuilder.Entity<MaternalOutcome>()
            .HasKey(m => m.BirthRecordId);
        modelBuilder.Entity<MaternalOutcome>()
            .HasOne(m => m.BirthRecord)
            .WithOne(b => b.MaternalOutcome)
            .HasForeignKey<MaternalOutcome>(m => m.BirthRecordId);

        modelBuilder.Entity<MaternalStatistics>()
            .HasKey(s => s.BirthRecordId);
        modelBuilder.Entity<MaternalStatistics>()
            .HasOne(s => s.BirthRecord)
            .WithOne(b => b.MaternalStatistics)
            .HasForeignKey<MaternalStatistics>(s => s.BirthRecordId);

        modelBuilder.Entity<MaternalStatistics>()
            .HasOne(s => s.RecordedByRegistrar)
            .WithMany()
            .HasForeignKey(s => s.RecordedByRegistrarId)
            .OnDelete(DeleteBehavior.Restrict);

        // Coded classifications, stored readably so the statistics team can
        // query the raw database without a lookup table.
        modelBuilder.Entity<MaternalStatistics>().Property(s => s.MotherEducationLevel).HasConversion<string>();
        modelBuilder.Entity<MaternalStatistics>().Property(s => s.FatherEducationLevel).HasConversion<string>();
        modelBuilder.Entity<MaternalStatistics>().Property(s => s.MotherOccupation).HasConversion<string>();
        modelBuilder.Entity<MaternalStatistics>().Property(s => s.FatherOccupation).HasConversion<string>();

        // One-to-many rather than one-to-one, because an amendment withdraws a
        // certificate instead of deleting it and a replacement is then issued.
        modelBuilder.Entity<Certificate>()
            .HasOne(c => c.BirthRecord)
            .WithMany(b => b.Certificates)
            .HasForeignKey(c => c.BirthRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        // "At most one valid certificate per birth" is the rule that keeps two
        // differently-signed documents from circulating for the same child, so
        // it belongs in the database rather than in whichever code path
        // remembers to check. Withdrawn rows are excluded by the filter, which
        // is what lets the history accumulate underneath it.
        modelBuilder.Entity<Certificate>()
            .HasIndex(c => c.BirthRecordId)
            .IsUnique()
            .HasFilter("\"WithdrawnAtUtc\" IS NULL");

        modelBuilder.Entity<BirthRecordAmendment>()
            .HasOne(amendment => amendment.BirthRecord)
            .WithMany(record => record.Amendments)
            .HasForeignKey(amendment => amendment.BirthRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<BirthRecordAmendment>()
            .HasOne(amendment => amendment.AmendedByRegistrar)
            .WithMany()
            .HasForeignKey(amendment => amendment.AmendedByRegistrarId)
            .OnDelete(DeleteBehavior.Restrict);

        // The amendment history of one record is read in the order it
        // happened -- the question an auditor asks is "what changed, when".
        modelBuilder.Entity<BirthRecordAmendment>()
            .HasIndex(amendment => new { amendment.BirthRecordId, amendment.AmendedAtUtc });

        modelBuilder.Entity<BirthRecordAmendment>()
            .HasIndex(amendment => amendment.TransactionId);

        modelBuilder.Entity<BirthRecordAmendment>()
            .HasOne(amendment => amendment.ReviewedByRegistrar)
            .WithMany()
            .HasForeignKey(amendment => amendment.ReviewedByRegistrarId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<BirthRecordAmendment>()
            .Property(amendment => amendment.Status)
            .HasConversion<string>();

        // A reviewer approves the submission, not a field, so the rows of one
        // request are always fetched together.
        modelBuilder.Entity<BirthRecordAmendment>()
            .HasIndex(amendment => amendment.AmendmentRequestId);

        // The approval queue is read by status.
        modelBuilder.Entity<BirthRecordAmendment>()
            .HasIndex(amendment => amendment.Status);

        modelBuilder.Entity<CertificateRevocation>()
            .HasOne(revocation => revocation.Certificate)
            .WithMany()
            .HasForeignKey(revocation => revocation.CertificateId)
            .OnDelete(DeleteBehavior.Restrict);

        // The lookup a verifier performs, on every certificate presented.
        // Unique as well as indexed: the same serial appearing twice would
        // make the published list contradict itself about when a document
        // stopped being valid.
        modelBuilder.Entity<CertificateRevocation>()
            .HasIndex(revocation => revocation.SerialHash)
            .IsUnique();

        // Delta fetches ask for everything after a timestamp.
        modelBuilder.Entity<CertificateRevocation>()
            .HasIndex(revocation => revocation.RevokedAtUtc);

        modelBuilder.Entity<CertificateRevocation>()
            .Property(revocation => revocation.Reason)
            .HasConversion<string>();

        // One late registration per record: lateness is a property of the
        // registration event, not something that can happen to it twice.
        modelBuilder.Entity<LateRegistration>()
            .HasKey(late => late.LateRegistrationId);

        modelBuilder.Entity<LateRegistration>()
            .HasOne(late => late.BirthRecord)
            .WithOne(record => record.LateRegistration)
            .HasForeignKey<LateRegistration>(late => late.BirthRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LateRegistration>()
            .HasOne(late => late.SubmittedByRegistrar)
            .WithMany()
            .HasForeignKey(late => late.SubmittedByRegistrarId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LateRegistration>()
            .HasOne(late => late.ReviewedByRegistrar)
            .WithMany()
            .HasForeignKey(late => late.ReviewedByRegistrarId)
            .OnDelete(DeleteBehavior.Restrict);

        // The verification queue is read by status, and the evidence mix is
        // a statistic the Ministry reports on -- both stay readable in the
        // raw database as strings rather than integer codes.
        modelBuilder.Entity<LateRegistration>()
            .HasIndex(late => late.Status);

        modelBuilder.Entity<LateRegistration>()
            .Property(late => late.Status)
            .HasConversion<string>();

        modelBuilder.Entity<LateRegistration>()
            .Property(late => late.EvidenceType)
            .HasConversion<string>();

        // One annulment per record: voiding is terminal, so there is never a
        // second one to record.
        modelBuilder.Entity<RecordAnnulment>()
            .HasOne(annulment => annulment.BirthRecord)
            .WithOne(record => record.Annulment)
            .HasForeignKey<RecordAnnulment>(annulment => annulment.BirthRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RecordAnnulment>()
            .HasOne(annulment => annulment.AnnulledByRegistrar)
            .WithMany()
            .HasForeignKey(annulment => annulment.AnnulledByRegistrarId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RecordAnnulment>()
            .Property(annulment => annulment.Reason)
            .HasConversion<string>();

        modelBuilder.Entity<AmendmentConflict>()
            .HasOne(conflict => conflict.BirthRecord)
            .WithMany()
            .HasForeignKey(conflict => conflict.BirthRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AmendmentConflict>()
            .HasOne(conflict => conflict.ReviewedByRegistrar)
            .WithMany()
            .HasForeignKey(conflict => conflict.ReviewedByRegistrarId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AmendmentConflict>()
            .Property(conflict => conflict.Status)
            .HasConversion<string>();

        // The conflict queue is read by status.
        modelBuilder.Entity<AmendmentConflict>()
            .HasIndex(conflict => conflict.Status);

        // The device's own identifier is the key. A surrogate would leave the
        // id a device asserts unconstrained, which is the hole enrolment
        // exists to close.
        modelBuilder.Entity<Device>().HasKey(device => device.DeviceId);

        modelBuilder.Entity<Device>()
            .Property(device => device.Status)
            .HasConversion<string>();

        modelBuilder.Entity<Device>()
            .HasOne(device => device.Facility)
            .WithMany()
            .HasForeignKey(device => device.FacilityId)
            .OnDelete(DeleteBehavior.Restrict);

        // "Which devices belong to this facility, and which have never
        // reported" -- the district's operational view.
        modelBuilder.Entity<Device>()
            .HasIndex(device => new { device.FacilityId, device.LastSeenAtUtc });

        modelBuilder.Entity<DeviceAlert>().Property(alert => alert.Kind).HasConversion<string>();
        modelBuilder.Entity<DeviceAlert>().Property(alert => alert.Status).HasConversion<string>();

        modelBuilder.Entity<DeviceAlert>()
            .HasOne(alert => alert.Facility)
            .WithMany()
            .HasForeignKey(alert => alert.FacilityId)
            .OnDelete(DeleteBehavior.Restrict);

        // The district officer's queue: open alerts for my district.
        modelBuilder.Entity<DeviceAlert>()
            .HasIndex(alert => new { alert.DistrictId, alert.Status });

        // At most one alert per device may be outstanding. Without this the
        // sweep raises the same fact again every time it runs, and a queue of
        // duplicates is a queue nobody reads. Filtered so the history of
        // resolved alerts is unconstrained -- a post that keeps going dark
        // should accumulate a row each time.
        modelBuilder.Entity<DeviceAlert>()
            .HasIndex(alert => alert.DeviceId)
            .IsUnique()
            .HasFilter("\"ResolvedAtUtc\" IS NULL");

        // Every read path filters annulled records out, or reports them as
        // void; this is the column that check runs on.
        modelBuilder.Entity<BirthRecord>()
            .HasIndex(record => record.AnnulledAtUtc);

        // Deliberately NOT unique: this is the audit of what arrived, so a
        // device that retries after a dropped response should appear three
        // times. Uniqueness lives on IdempotencyRecord, which answers the
        // different question of whether the work was already done.
        modelBuilder.Entity<RequestLog>()
            .HasIndex(r => r.TransactionId);

        modelBuilder.Entity<IdempotencyRecord>()
            .HasIndex(i => i.TransactionId)
            .IsUnique();

        // Two FKs to the same table, so EF cannot infer which navigation is
        // which -- and Restrict because a flagged record must never cascade
        // a delete through the registry.
        modelBuilder.Entity<DuplicateCandidate>()
            .HasOne(link => link.BirthRecord)
            .WithMany()
            .HasForeignKey(link => link.BirthRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DuplicateCandidate>()
            .HasOne(link => link.MatchedBirthRecord)
            .WithMany()
            .HasForeignKey(link => link.MatchedBirthRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DuplicateCandidate>()
            .Property(link => link.Status)
            .HasConversion<string>();

        // The review queue is read by status.
        modelBuilder.Entity<DuplicateCandidate>()
            .HasIndex(link => link.Status);

        // Duplicate detection blocks on a narrow date window, so this index
        // is what keeps the scan off a full-table comparison.
        modelBuilder.Entity<BirthRecord>()
            .HasIndex(record => record.DateOfBirth);

        // The relay claims undispatched messages oldest-first; this is the
        // index that keeps that off a full-table scan as the outbox grows.
        modelBuilder.Entity<OutboxMessage>()
            .HasIndex(message => new { message.DispatchedAtUtc, message.CreatedAtUtc });

        // One identity-provider account maps to at most one registrar, so a
        // token can never resolve ambiguously to two people.
        modelBuilder.Entity<Registrar>()
            .HasIndex(r => r.ExternalSubjectId)
            .IsUnique();

        // Purging expired keys scans by completion time.
        modelBuilder.Entity<IdempotencyRecord>()
            .HasIndex(i => i.CompletedAtUtc);

        modelBuilder.Entity<IdempotencyRecord>()
            .Property(i => i.Status)
            .HasConversion<string>();

        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => a.TransactionId);

        // Store enums as readable strings, not integers -- makes the raw
        // database far easier to inspect/report against directly (e.g. for
        // the Ministry's statistics team) without a lookup table.
        modelBuilder.Entity<BirthRecord>().Property(b => b.VitalEventType).HasConversion<string>();
        modelBuilder.Entity<BirthRecord>().Property(b => b.Sex).HasConversion<string>();
        modelBuilder.Entity<BirthRecord>().Property(b => b.Plurality).HasConversion<string>();
        modelBuilder.Entity<BirthRecord>().Property(b => b.Status).HasConversion<string>();
        modelBuilder.Entity<Facility>().Property(f => f.Tier).HasConversion<string>();
        modelBuilder.Entity<Facility>().Property(f => f.ConnectivityProfile).HasConversion<string>();
        modelBuilder.Entity<Registrar>().Property(r => r.Role).HasConversion<string>();
        modelBuilder.Entity<NeonatalOutcome>().Property(n => n.IcdPmTiming).HasConversion<string>();
        modelBuilder.Entity<SyncBatch>().Property(s => s.Status).HasConversion<string>();

        NothingCascadesIntoADelete(modelBuilder);
    }

    /// <summary>
    /// No relationship in the registry deletes anything on its parent's
    /// behalf.
    ///
    /// **This is the register's own rule, finally enforced by the database.**
    /// An annulment keeps the record; a duplicate supersession keeps both; a
    /// revoked certificate keeps its revocation; the audit trail cannot be
    /// rewritten at all. A schema that quietly removes births when a row
    /// upstream goes away contradicts every one of those.
    ///
    /// Most relationships already said <c>Restrict</c> individually, which is
    /// what makes the ones that did not so easy to miss: they were never
    /// decided, they were left to EF's convention, and EF's convention for a
    /// required relationship is <c>Cascade</c>. That left six paths that
    /// destroy legal records —
    ///
    /// <list type="bullet">
    /// <item>deleting a facility deleted every birth registered there;</item>
    /// <item>deleting a facility deleted its registrars, which then cascaded
    /// again into their birth records;</item>
    /// <item>deleting a registrar deleted the births they filed, and the
    /// maternal and neonatal deaths they recorded;</item>
    /// <item>deleting a facility deleted its sync history.</item>
    /// </list>
    ///
    /// Applied as a sweep rather than relationship by relationship, for the
    /// same reason <c>AuditLog.DistrictId</c> is <c>required</c>: naming each
    /// one works only while somebody remembers, and the failure of
    /// remembering is silent. A relationship added next year is safe without
    /// anyone thinking about it, and a genuine need to cascade now has to be
    /// argued for here rather than arrived at by omission.
    ///
    /// The consequence is that removing a facility or a registrar who has
    /// touched the register now fails at the database. That is the intended
    /// outcome: they cannot be removed, because what they did cannot be
    /// unmade. Withdrawing someone's access is a Keycloak act, not a delete.
    /// </summary>
    private static void NothingCascadesIntoADelete(ModelBuilder modelBuilder)
    {
        foreach (var relationship in modelBuilder.Model
                     .GetEntityTypes()
                     .SelectMany(entity => entity.GetForeignKeys())
                     .Where(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.Cascade))
        {
            relationship.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }

    /// <summary>
    /// Refuses any change to an audit row that has already been written.
    ///
    /// The database enforces this too, with triggers (see the
    /// AuditLogImmutability migration), and that is the enforcement that
    /// actually counts -- this one can be bypassed by anything issuing SQL
    /// directly. It is here because it fails in the right place: a developer
    /// who writes <c>audit.Action = "..."</c> gets a message naming the rule
    /// at the line that broke it, rather than a constraint violation from the
    /// provider several layers down.
    ///
    /// Design decision #5: the chain of custody is legal evidence about a
    /// birth record, not a debugging convenience. A corrected audit row is
    /// indistinguishable from a falsified one.
    /// </summary>
    private void GuardAuditLogImmutability()
    {
        var altered = ChangeTracker.Entries<AuditLog>()
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (altered.Count == 0)
        {
            return;
        }

        var entry = altered[0];

        // Original values, not current: the point of the message is to name
        // the row as it stands in the database. Echoing the attempted new
        // value would describe the change rather than what is being
        // overwritten, which is the half that matters.
        var original = entry.OriginalValues;

        throw new InvalidOperationException(
            $"Audit rows are append-only and cannot be {entry.State.ToString().ToLowerInvariant()}. "
            + $"Attempted on AuditLog '{entry.Entity.AuditLogId}' "
            + $"({original[nameof(AuditLog.Action)]} on "
            + $"{original[nameof(AuditLog.EntityType)]} '{original[nameof(AuditLog.EntityId)]}'). "
            + "Record what happened as a new audit row instead.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditLogImmutability();

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAuditLogImmutability();

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
