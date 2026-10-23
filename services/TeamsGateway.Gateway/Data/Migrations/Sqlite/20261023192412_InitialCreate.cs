using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamsGateway.Gateway.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Topic = table.Column<string>(type: "TEXT", nullable: true),
                    ThreadType = table.Column<string>(type: "TEXT", nullable: true),
                    LastMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    Subscribed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncedThrough = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublishedEvents",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedEvents", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "SecureValues",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Protected = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecureValues", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "WebhookSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Secret = table.Column<string>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveredSeq = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookSubscriptions_ApiKeys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    SenderMri = table.Column<string>(type: "TEXT", nullable: true),
                    SenderName = table.Column<string>(type: "TEXT", nullable: true),
                    MessageType = table.Column<string>(type: "TEXT", nullable: true),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    SequenceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Edited = table.Column<bool>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ComposeTime = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageEvents_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageRevisions",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    SupersededAt = table.Column<long>(type: "INTEGER", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageEvents_ConversationId_SequenceId",
                table: "MessageEvents",
                columns: new[] { "ConversationId", "SequenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageEvents_ReceivedAt",
                table: "MessageEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PublishedEvents_PublishedAt",
                table: "PublishedEvents",
                column: "PublishedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSubscriptions_ApiKeyId",
                table: "WebhookSubscriptions",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookSubscriptions_ConversationId",
                table: "WebhookSubscriptions",
                column: "ConversationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageRevisions");

            migrationBuilder.DropTable(
                name: "PublishedEvents");

            migrationBuilder.DropTable(
                name: "SecureValues");

            migrationBuilder.DropTable(
                name: "WebhookSubscriptions");

            migrationBuilder.DropTable(
                name: "MessageEvents");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "Conversations");
        }
    }
}
