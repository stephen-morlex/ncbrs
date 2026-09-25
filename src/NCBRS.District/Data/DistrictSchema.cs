using Microsoft.EntityFrameworkCore;

namespace NCBRS.District.Data;

/// <summary>
/// Brings an existing node's store up to the current shape.
///
/// The store is created with <c>EnsureCreated</c>, which does nothing to a
/// database that already exists, so a column added to the model never appears
/// in a node deployed before it. The consumer's read model answers the same
/// problem by refusing to start and asking for a rebuild -- safe there, because
/// it is derived from Kafka and holds nothing of its own. **This store is the
/// opposite**: its rows are births in transit that exist nowhere else yet, so
/// deleting it is not a remedy. Columns are added in place instead.
///
/// Additive only, and only nullable or defaulted columns, so an upgrade can
/// never fail on an existing row and never changes one. A change that is not
/// additive needs a real migration, not an entry here.
/// </summary>
public static class DistrictSchema
{
    private static readonly (string Column, string Definition)[] AddedColumns =
    [
        ("DeviceSignature", "TEXT NULL"),
        ("ConsecutiveTimeouts", "INTEGER NOT NULL DEFAULT 0"),
    ];

    public static void EnsureCurrent(DistrictDbContext db)
    {
        db.Database.EnsureCreated();

        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            connection.Open();
        }

        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var read = connection.CreateCommand())
            {
                read.CommandText = """SELECT "name" FROM pragma_table_info('ForwardedBatches')""";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetString(0));
                }
            }

            foreach (var (column, definition) in AddedColumns)
            {
                if (existing.Contains(column))
                {
                    continue;
                }

                using var add = connection.CreateCommand();
                // Identifiers from the fixed list above, never from input.
                add.CommandText = $"""ALTER TABLE "ForwardedBatches" ADD COLUMN "{column}" {definition}""";
                add.ExecuteNonQuery();
            }
        }
        finally
        {
            if (opened)
            {
                connection.Close();
            }
        }
    }
}
