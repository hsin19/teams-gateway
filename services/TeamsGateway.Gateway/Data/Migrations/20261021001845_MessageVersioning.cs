using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TeamsGateway.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class MessageVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Deleted",
                table: "MessageEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Edited",
                table: "MessageEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "MessageEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "MessageEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // 既有列：未編輯的訊息 version == id，補上去，免得慢更新把它們誤判成「更新」重發
            migrationBuilder.Sql("UPDATE MessageEvents SET Version = CAST(Id AS INTEGER) WHERE Version = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Deleted",
                table: "MessageEvents");

            migrationBuilder.DropColumn(
                name: "Edited",
                table: "MessageEvents");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "MessageEvents");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "MessageEvents");
        }
    }
}
