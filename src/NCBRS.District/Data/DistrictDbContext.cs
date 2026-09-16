using Microsoft.EntityFrameworkCore;
using NCBRS.District.Models;

namespace NCBRS.District.Data;

/// <summary>
/// The district node's own store: a queue of batches in transit, and nothing
/// else.
///
/// It deliberately does not carry <c>NcbrsDbContext</c>. A district box
/// holding a copy of the registry would multiply the number of places birth
/// records live — onto hardware sitting in a district office, physically far
/// easier to reach than a data centre — for a node whose job is to pass
/// batches along. Draft 4.2 asks for data minimisation, and the smallest
/// thing that does this job holds batches only until the centre has
/// acknowledged them.
///
/// The tiers still "differ in scale, not model" in the sense that matters:
/// the node forwards the sync contract verbatim and never reinterprets it.
/// What it does not do is duplicate the register.
/// </summary>
public class DistrictDbContext(DbContextOptions<DistrictDbContext> options) : DbContext(options)
{
    public DbSet<ForwardedBatch> ForwardedBatches => Set<ForwardedBatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // One row per submission. A device retrying the same batch while the
        // node is still holding it must not queue it twice -- the same
        // guarantee the centre gives, enforced here as well so it holds at
        // whichever tier the device happened to reach.
        modelBuilder.Entity<ForwardedBatch>()
            .HasIndex(batch => batch.TransactionId)
            .IsUnique();

        // The forwarder claims queued work oldest-first, filtered on when it
        // may next be tried.
        modelBuilder.Entity<ForwardedBatch>()
            .HasIndex(batch => new { batch.Status, batch.NextAttemptAtUtc });

        modelBuilder.Entity<ForwardedBatch>()
            .Property(batch => batch.Status)
            .HasConversion<string>();
    }
}
