using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.PostgresMigrations
{
    /// <inheritdoc />
    public partial class ChannelModelInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ModelInfoId",
                table: "ModelPricingPlans",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ChannelModelInfoId",
                table: "ModelPricingPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelModelInfos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpstreamModel = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelKey = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    MatchType = table.Column<string>(type: "text", nullable: false),
                    MatchPattern = table.Column<string>(type: "text", nullable: false),
                    CatalogJson = table.Column<string>(type: "text", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelModelInfos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricingPlans_ChannelModelInfoId",
                table: "ModelPricingPlans",
                column: "ChannelModelInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelInfos_ChannelId_UpstreamModel",
                table: "ChannelModelInfos",
                columns: new[] { "ChannelId", "UpstreamModel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelInfos_Enabled",
                table: "ChannelModelInfos",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelInfos_MatchPattern",
                table: "ChannelModelInfos",
                column: "MatchPattern");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelInfos_MatchType",
                table: "ChannelModelInfos",
                column: "MatchType");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelInfos_ProviderId",
                table: "ChannelModelInfos",
                column: "ProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelModelInfos");

            migrationBuilder.DropIndex(
                name: "IX_ModelPricingPlans_ChannelModelInfoId",
                table: "ModelPricingPlans");

            migrationBuilder.DropColumn(
                name: "ChannelModelInfoId",
                table: "ModelPricingPlans");

            migrationBuilder.AlterColumn<Guid>(
                name: "ModelInfoId",
                table: "ModelPricingPlans",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
