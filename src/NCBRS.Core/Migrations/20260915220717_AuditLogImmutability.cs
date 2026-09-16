using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <summary>
    /// Makes AuditLogs append-only at the database (WS-A2), for the SQLite
    /// development and test provider.
    ///
    /// Design decision #5 says the audit trail is legal evidence about a birth
    /// record, and until now that was enforced by convention: nothing in the
    /// code updates an audit row, so none was updated. Convention is not a
    /// control. A corrected audit row is indistinguishable from a falsified
    /// one, and the whole value of a chain of custody is that it cannot be
    /// quietly rewritten after someone disputes a registration.
    ///
    /// The central tier's version of this control lives in
    /// NCBRS.Migrations.Postgres, where it also has to guard TRUNCATE. This
    /// file deliberately carries no Postgres branch: a migration in the SQLite
    /// history can never run against Postgres, so one here would be a control
    /// that reads as present and has never executed.
    /// </summary>
    public partial class AuditLogImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RAISE(ABORT) rolls the statement back and surfaces the message,
            // so an attempted rewrite fails loudly rather than silently
            // affecting zero rows.
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS audit_logs_no_update
                BEFORE UPDATE ON "AuditLogs"
                BEGIN
                    SELECT RAISE(ABORT, 'AuditLogs is append-only: rows cannot be updated.');
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS audit_logs_no_delete
                BEFORE DELETE ON "AuditLogs"
                BEGIN
                    SELECT RAISE(ABORT, 'AuditLogs is append-only: rows cannot be deleted.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_logs_no_update;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_logs_no_delete;");
        }
    }
}
