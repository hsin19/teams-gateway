using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamsGateway.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class MessageRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageRevisions",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageRevisions", x => new { x.MessageId, x.Version });
                    table.ForeignKey(
                        name: "FK_MessageRevisions_MessageEvents_MessageId",
                        column: x => x.MessageId,
                        principalTable: "MessageEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageRevisions");
        }
    }
}
