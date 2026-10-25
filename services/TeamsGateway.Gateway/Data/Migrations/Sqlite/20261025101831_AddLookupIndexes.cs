using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamsGateway.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PublishedEvents_MessageId",
                table: "PublishedEvents",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageEvents_SenderMri_ReceivedAt",
                table: "MessageEvents",
                columns: new[] { "SenderMri", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublishedEvents_MessageId",
                table: "PublishedEvents");

            migrationBuilder.DropIndex(
                name: "IX_MessageEvents_SenderMri_ReceivedAt",
                table: "MessageEvents");
        }
    }
}
