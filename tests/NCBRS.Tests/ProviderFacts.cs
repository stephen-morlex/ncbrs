using NCBRS.Data;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// A fact that only means something against Postgres -- roles and grants, WAL
/// archiving, anything SQLite does not have. Skipped with a reason elsewhere,
/// never passed silently: a green test on SQLite would claim a proof it never
/// made.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (TestDatabase.Provider != DatabaseProvider.Postgres)
        {
            Skip = "Postgres only. Run with NCBRS_TEST_PROVIDER=Postgres, as CI's Postgres job does.";
        }
    }
}

/// <summary>The other half: a fact about what SQLite lacks, skipped on Postgres.</summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (TestDatabase.Provider != DatabaseProvider.Sqlite)
        {
            Skip = "SQLite only. Its Postgres counterpart runs in CI's Postgres job.";
        }
    }
}
