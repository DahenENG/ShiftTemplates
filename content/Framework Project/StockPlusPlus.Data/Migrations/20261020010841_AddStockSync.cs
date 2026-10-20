using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StockPlusPlus.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockSyncInbox",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Feed = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ExternalCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockSyncInbox", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "SyncedStocks",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Feed = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ExternalCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncedStocks", x => x.ID);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockSyncInbox_Feed_Status_ID",
                table: "StockSyncInbox",
                columns: new[] { "Feed", "Status", "ID" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncedStocks_Feed_ExternalCode",
                table: "SyncedStocks",
                columns: new[] { "Feed", "ExternalCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockSyncInbox");

            migrationBuilder.DropTable(
                name: "SyncedStocks");
        }
    }
}
