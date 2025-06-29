using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBManager.Migrations
{
    /// <inheritdoc />
    public partial class AddSeedDataForBinance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Brokers",
                columns: new[] { "Name", "APIKey", "BaseURL", "SecretKey" },
                values: new object[] { "BINANCE", null, "https://api.binance.com/api/v3/", null });

            migrationBuilder.InsertData(
                table: "Endpoints",
                columns: new[] { "Id", "ActionType", "BrokerName", "Path", "ProtocolType" },
                values: new object[] { 1, "GET", "BINANCE", "klines", "REST" });

            migrationBuilder.InsertData(
                table: "Parameters",
                columns: new[] { "Id", "BrokerName", "EndpointId", "LocatedIn", "Name", "Path", "Type", "Value" },
                values: new object[] { 1, "BINANCE", null, "query", "symbol", "klines", "string", null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Endpoints",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Parameters",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Brokers",
                keyColumn: "Name",
                keyValue: "BINANCE");
        }
    }
}
