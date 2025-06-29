using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBManager.Migrations
{
    /// <inheritdoc />
    public partial class AddEndpointTypeToEndpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EndpointType",
                table: "Endpoints",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "Endpoints",
                keyColumn: "Id",
                keyValue: 1,
                column: "EndpointType",
                value: "GetCandles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndpointType",
                table: "Endpoints");
        }
    }
}
