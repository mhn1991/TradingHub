using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase7Backup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_records",
                schema: "operations",
                columns: table => new
                {
                    backup_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    postgre_sql_version = table.Column<string>(type: "text", nullable: true),
                    schema_version = table.Column<string>(type: "text", nullable: true),
                    checksum = table.Column<string>(type: "text", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    restore_test_status = table.Column<short>(type: "smallint", nullable: false),
                    restore_tested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    restore_failure_detail = table.Column<string>(type: "text", nullable: true),
                    retention_expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_records", x => x.backup_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_records_retention_expires_at",
                schema: "operations",
                table: "backup_records",
                column: "retention_expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_records",
                schema: "operations");
        }
    }
}
