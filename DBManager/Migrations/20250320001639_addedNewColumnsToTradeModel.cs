using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DBManager.Migrations
{
    /// <inheritdoc />
    public partial class addedNewColumnsToTradeModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Price",
                table: "Trades",
                newName: "TakeProfit");

            migrationBuilder.AddColumn<decimal>(
                name: "EntryPrice",
                table: "Trades",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExitPrice",
                table: "Trades",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StopPrice",
                table: "Trades",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntryPrice",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "ExitPrice",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "StopPrice",
                table: "Trades");

            migrationBuilder.RenameColumn(
                name: "TakeProfit",
                table: "Trades",
                newName: "Price");
        }
    }
}
