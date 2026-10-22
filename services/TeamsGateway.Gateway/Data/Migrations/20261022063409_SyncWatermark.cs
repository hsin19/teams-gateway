using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamsGateway.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncWatermark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SyncedThrough",
                table: "Conversations",
                type: "TEXT",
                nullable: true);

            // 已訂閱的舊對話：水位線先用已存最新一則的時間（舊版判斷缺口的依據），升級後第一次重啟才補得到停機缺口。
            // 沒存過訊息的維持 null = 從沒同步過，回補只存不發
            migrationBuilder.Sql("""
                UPDATE Conversations
                SET SyncedThrough = (SELECT MAX(m.ComposeTime) FROM MessageEvents m WHERE m.ConversationId = Conversations.Id)
                WHERE Subscribed = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncedThrough",
                table: "Conversations");
        }
    }
}
