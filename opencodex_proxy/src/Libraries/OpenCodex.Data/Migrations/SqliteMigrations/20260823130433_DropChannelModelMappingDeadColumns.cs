using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class DropChannelModelMappingDeadColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelPricings");

            migrationBuilder.DropIndex(
                name: "IX_ChannelModelMappings_ModelInfoId",
                table: "ChannelModelMappings");

            migrationBuilder.DropIndex(
                name: "IX_ChannelModelMappings_PricingPlanId",
                table: "ChannelModelMappings");

            migrationBuilder.DropColumn(
                name: "ModelInfoId",
                table: "ChannelModelMappings");

            migrationBuilder.DropColumn(
                name: "PricingMode",
                table: "ChannelModelMappings");

            migrationBuilder.DropColumn(
                name: "PricingPlanId",
                table: "ChannelModelMappings");

            migrationBuilder.DropColumn(
                name: "SupportsImage",
                table: "ChannelModelMappings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ModelInfoId",
                table: "ChannelModelMappings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricingMode",
                table: "ChannelModelMappings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "PricingPlanId",
                table: "ChannelModelMappings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsImage",
                table: "ChannelModelMappings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ModelPricings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CachedInputPrice = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    InputPrice = table.Column<double>(type: "REAL", nullable: false),
                    MatchPattern = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    OutputPrice = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPricings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelMappings_ModelInfoId",
                table: "ChannelModelMappings",
                column: "ModelInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelMappings_PricingPlanId",
                table: "ChannelModelMappings",
                column: "PricingPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricings_Enabled",
                table: "ModelPricings",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricings_MatchPattern",
                table: "ModelPricings",
                column: "MatchPattern");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricings_ModelId",
                table: "ModelPricings",
                column: "ModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricings_Vendor",
                table: "ModelPricings",
                column: "Vendor");
        }
    }
}
