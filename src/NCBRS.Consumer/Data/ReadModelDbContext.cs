using Microsoft.EntityFrameworkCore;
using NCBRS.Consumer.Models;

namespace NCBRS.Consumer.Data;

/// <summary>
/// The reporting projection, in its own store.
///
/// Separate from the registry by design, not convenience. Draft 6.4.1: the
/// registration API remains the single writer to the database, and Kafka
/// events are an outbound notification of what already happened, never the
/// primary record. A consumer writing back into the system of record would
/// invert that and make the register partly a function of its own event
/// stream.
///
/// In production this belongs on the reporting replica (draft 6.4, plan F1)
/// so dashboards never touch the transactional database. SQLite here is the
/// dev stand-in; nothing about the projection assumes it.
/// </summary>
public class ReadModelDbContext(DbContextOptions<ReadModelDbContext> options) : DbContext(options)
{
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<PendingEvent> PendingEvents => Set<PendingEvent>();
    public DbSet<RegistrationFact> RegistrationFacts => Set<RegistrationFact>();
    public DbSet<NeonatalOutcomeFact> NeonatalOutcomeFacts => Set<NeonatalOutcomeFact>();
    public DbSet<MaternalOutcomeFact> MaternalOutcomeFacts => Set<MaternalOutcomeFact>();
    public DbSet<SyncBatchFact> SyncBatchFacts => Set<SyncBatchFact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedEvent>().HasKey(processed => processed.EventId);

        // Keyed by BRN, which is what makes the projection idempotent: the
        // same event applied twice writes the same row rather than adding a
        // second one.
        modelBuilder.Entity<RegistrationFact>().HasKey(fact => fact.Brn);

        // Every aggregate is grouped by district and filtered on annulment,
        // so this is the index they all run on.
        modelBuilder.Entity<RegistrationFact>()
            .HasIndex(fact => new { fact.CountyCode, fact.AnnulledAtUtc });

        modelBuilder.Entity<RegistrationFact>()
            .HasIndex(fact => fact.PublishedAtUtc);

        // Every registration checks this for waiting events, so it is on the
        // hot path and wants an index even though the table is normally
        // empty.
        modelBuilder.Entity<PendingEvent>()
            .HasIndex(pending => new { pending.Brn, pending.OccurredAtUtc });

        // Outcomes are keyed by the BRN they belong to, so an outcome
        // recorded twice overwrites rather than counting a second death.
        modelBuilder.Entity<NeonatalOutcomeFact>().HasKey(fact => fact.Brn);
        modelBuilder.Entity<MaternalOutcomeFact>().HasKey(fact => fact.Brn);

        modelBuilder.Entity<SyncBatchFact>().HasKey(fact => fact.SyncBatchId);

        // "Which devices have gone quiet" runs over exactly these two.
        modelBuilder.Entity<SyncBatchFact>()
            .HasIndex(fact => new { fact.DeviceId, fact.SyncedAtUtc });

        modelBuilder.Entity<SyncBatchFact>()
            .HasIndex(fact => new { fact.CountyCode, fact.SyncedAtUtc });
    }
}
