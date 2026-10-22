using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamsGateway.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublishedEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishedEvents",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedEvents", x => x.Seq);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublishedEvents_PublishedAt",
                table: "PublishedEvents",
                column: "PublishedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublishedEvents");
        }
    }
}
