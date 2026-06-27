using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class ModelCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CacheReadTokens",
                table: "RequestLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CacheWriteTokens",
                table: "RequestLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CostCurrency",
                table: "RequestLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "PricingModelInfoId",
                table: "RequestLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PricingPlanId",
                table: "RequestLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricingSnapshotJson",
                table: "RequestLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelModelMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestModel = table.Column<string>(type: "TEXT", nullable: false),
                    UpstreamModel = table.Column<string>(type: "TEXT", nullable: false),
                    SupportsImage = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModelInfoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PricingMode = table.Column<string>(type: "TEXT", nullable: false),
                    PricingPlanId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelModelMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelInfos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelKey = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    MatchType = table.Column<string>(type: "TEXT", nullable: false),
                    MatchPattern = table.Column<string>(type: "TEXT", nullable: false),
                    CatalogJson = table.Column<string>(type: "TEXT", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelInfos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelPricingPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelInfoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPricingPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelPricingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PricingPlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BillingItem = table.Column<string>(type: "TEXT", nullable: false),
                    BillingMode = table.Column<string>(type: "TEXT", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 8, nullable: false),
                    TiersJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPricingRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelProviders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_PricingModelInfoId",
                table: "RequestLogs",
                column: "PricingModelInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_PricingPlanId",
                table: "RequestLogs",
                column: "PricingPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelMappings_ChannelId_Position",
                table: "ChannelModelMappings",
                columns: new[] { "ChannelId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelMappings_ChannelId_RequestModel",
                table: "ChannelModelMappings",
                columns: new[] { "ChannelId", "RequestModel" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelMappings_Enabled",
                table: "ChannelModelMappings",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelMappings_ModelInfoId",
                table: "ChannelModelMappings",
                column: "ModelInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelMappings_PricingPlanId",
                table: "ChannelModelMappings",
                column: "PricingPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInfos_ChannelId",
                table: "ModelInfos",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInfos_Enabled",
                table: "ModelInfos",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInfos_MatchPattern",
                table: "ModelInfos",
                column: "MatchPattern");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInfos_MatchType",
                table: "ModelInfos",
                column: "MatchType");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInfos_ProviderId",
                table: "ModelInfos",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelInfos_Scope_ChannelId_ModelKey",
                table: "ModelInfos",
                columns: new[] { "Scope", "ChannelId", "ModelKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelInfos_Scope_ProviderId_ModelKey",
                table: "ModelInfos",
                columns: new[] { "Scope", "ProviderId", "ModelKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricingPlans_ChannelId",
                table: "ModelPricingPlans",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricingPlans_Enabled",
                table: "ModelPricingPlans",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricingPlans_ModelInfoId",
                table: "ModelPricingPlans",
                column: "ModelInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricingRules_BillingItem",
                table: "ModelPricingRules",
                column: "BillingItem");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricingRules_Enabled",
                table: "ModelPricingRules",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPricingRules_PricingPlanId",
                table: "ModelPricingRules",
                column: "PricingPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelProviders_Code",
                table: "ModelProviders",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelProviders_Enabled",
                table: "ModelProviders",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ModelProviders_SortOrder",
                table: "ModelProviders",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelModelMappings");

            migrationBuilder.DropTable(
                name: "ModelInfos");

            migrationBuilder.DropTable(
                name: "ModelPricingPlans");

            migrationBuilder.DropTable(
                name: "ModelPricingRules");

            migrationBuilder.DropTable(
                name: "ModelProviders");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_PricingModelInfoId",
                table: "RequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_PricingPlanId",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "CacheWriteTokens",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "CostCurrency",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "PricingModelInfoId",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "PricingPlanId",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "PricingSnapshotJson",
                table: "RequestLogs");
        }
    }
}
