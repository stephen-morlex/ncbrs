using System.Data.Common;

namespace NCBRS.Consumer.Data;

/// <summary>
/// Refuses to start on a read model whose shape no longer matches the code.
///
/// The projection is created with <c>EnsureCreated</c> rather than migrations,
/// deliberately: it holds no system of record and is rebuildable from Kafka by
/// design, which is the whole reason it can be thrown away. But
/// <c>EnsureCreated</c> is a no-op against a database that already exists, so
/// a column added or renamed in the model simply never appears in a store
/// created before the change.
///
/// Without this check the service starts cleanly, consumes happily, and fails
/// on the first dashboard query with a provider-level "no such column" — which
/// reads as a broken deployment rather than a read model that needs rebuilding.
/// Worse, the window between those two moments is one where the consumer looks
/// healthy while projecting into a schema nobody has verified.
///
/// The same rule as an unrecognised <c>Database:Provider</c>: refuse at
/// startup and name the remedy, rather than default into a state that looks
/// fine until it matters.
/// </summary>
public static class ReadModelSchema
{
    /// <summary>
    /// Reads one row from every projected table. Any column the model expects
    /// and the database lacks fails here, which is the point — a probe per
    /// table covers every future shape change, not only the one that prompted
    /// this.
    /// </summary>
    public static void EnsureUsable(ReadModelDbContext db)
    {
        try
        {
            _ = db.RegistrationFacts.FirstOrDefault();
            _ = db.NeonatalOutcomeFacts.FirstOrDefault();
            _ = db.MaternalOutcomeFacts.FirstOrDefault();
            _ = db.SyncBatchFacts.FirstOrDefault();
            _ = db.ProcessedEvents.FirstOrDefault();
            _ = db.PendingEvents.FirstOrDefault();
        }
        catch (DbException cause)
        {
            throw new InvalidOperationException(
                "The reporting read model does not match this build. It was created by an "
                + "earlier version and EnsureCreated does not alter an existing database, so a "
                + "column this code expects is missing.\n\n"
                + "Delete the read model store and let the consumer replay the topics from the "
                + "earliest offset. Nothing is lost: this projection is derived from Kafka by "
                + "design and is not the system of record for anything.",
                cause);
        }
    }
}
