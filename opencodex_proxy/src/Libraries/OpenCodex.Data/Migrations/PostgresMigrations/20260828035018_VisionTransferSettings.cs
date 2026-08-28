using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.PostgresMigrations
{
    /// <inheritdoc />
    public partial class VisionTransferSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VisionTransferSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrimaryChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrimaryModel = table.Column<string>(type: "text", nullable: false),
                    FallbackChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    FallbackModel = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisionTransferSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisionTransferSettings_OwnerUserId",
                table: "VisionTransferSettings",
                column: "OwnerUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisionTransferSettings");
        }
    }
}
