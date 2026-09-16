using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations.Postgres
{
    /// <summary>
    /// Makes AuditLogs append-only at the database (WS-A2), for the central
    /// tier.
    ///
    /// The SQLite set carries the same control as `RAISE(ABORT)` triggers.
    /// This is the version that matters: it is the engine a real registry
    /// runs on.
    ///
    /// **The trigger is half the control.** It stops anything issuing SQL
    /// through the connection. It does not stop someone holding the
    /// application's credentials from being *granted* the verb in the first
    /// place, which is what a REVOKE is for. That runs as a deployment step,
    /// because only the deployment knows what the application role is called:
    ///
    ///     REVOKE UPDATE, DELETE, TRUNCATE ON "AuditLogs" FROM &lt;app_role&gt;;
    ///
    /// Neither stops a database owner, who can drop a trigger or restore a
    /// grant. That is not closable here — it is why audit data must also be
    /// shipped off the box it is written on, to storage that only appends
    /// (WS-A6).
    /// </summary>
    public partial class AuditLogImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ncbrs_audit_logs_append_only()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditLogs is append-only: % is not permitted on this table.', TG_OP;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER audit_logs_append_only
                BEFORE UPDATE OR DELETE ON "AuditLogs"
                FOR EACH ROW EXECUTE FUNCTION ncbrs_audit_logs_append_only();
                """);

            // TRUNCATE bypasses row-level triggers entirely, so it needs a
            // statement-level one of its own. Without this the table can be
            // emptied by a single command while every row-level guard above
            // stands — and emptying it is the shape a cover-up actually takes.
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_logs_no_truncate
                BEFORE TRUNCATE ON "AuditLogs"
                FOR EACH STATEMENT EXECUTE FUNCTION ncbrs_audit_logs_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS audit_logs_no_truncate ON "AuditLogs";""");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS audit_logs_append_only ON "AuditLogs";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS ncbrs_audit_logs_append_only();");
        }
    }
}
