using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DBManager.Migrations
{
    /// <inheritdoc />
    public partial class BrokerTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Brokers",
                columns: table => new
                {
                    Name = table.Column<string>(type: "text", nullable: false),
                    APIKey = table.Column<string>(type: "text", nullable: true),
                    SecretKey = table.Column<string>(type: "text", nullable: true),
                    BaseURL = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brokers", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Endpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BrokerName = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    ProtocolType = table.Column<string>(type: "text", nullable: false),
                    ActionType = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Endpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Endpoints_Brokers_BrokerName",
                        column: x => x.BrokerName,
                        principalTable: "Brokers",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Parameters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BrokerName = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    LocatedIn = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: true),
                    Value = table.Column<string>(type: "text", nullable: true),
                    EndpointId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Parameters_Brokers_BrokerName",
                        column: x => x.BrokerName,
                        principalTable: "Brokers",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Parameters_Endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "Endpoints",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Endpoints_BrokerName",
                table: "Endpoints",
                column: "BrokerName");

            migrationBuilder.CreateIndex(
                name: "IX_Parameters_BrokerName_Path_Name",
                table: "Parameters",
                columns: new[] { "BrokerName", "Path", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parameters_EndpointId",
                table: "Parameters",
                column: "EndpointId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Parameters");

            migrationBuilder.DropTable(
                name: "Endpoints");

            migrationBuilder.DropTable(
                name: "Brokers");
        }
    }
}
