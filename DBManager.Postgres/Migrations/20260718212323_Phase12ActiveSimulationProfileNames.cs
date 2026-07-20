using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBManager.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Phase12ActiveSimulationProfileNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_experiment_profiles_name",
                schema: "simulation",
                table: "experiment_profiles");

            migrationBuilder.CreateIndex(
                name: "ix_experiment_profiles_name",
                schema: "simulation",
                table: "experiment_profiles",
                column: "name",
                unique: true,
                filter: "archived_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_experiment_profiles_name",
                schema: "simulation",
                table: "experiment_profiles");

            migrationBuilder.CreateIndex(
                name: "ix_experiment_profiles_name",
                schema: "simulation",
                table: "experiment_profiles",
                column: "name",
                unique: true);
        }
    }
}
