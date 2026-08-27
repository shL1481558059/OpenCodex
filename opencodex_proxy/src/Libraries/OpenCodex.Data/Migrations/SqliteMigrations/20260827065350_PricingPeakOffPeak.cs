using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class PricingPeakOffPeak : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OffPeakEnabled",
                table: "ModelPricingRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OffPeakTiersJson",
                table: "ModelPricingRules",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<decimal>(
                name: "OffPeakUnitPrice",
                table: "ModelPricingRules",
                type: "TEXT",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "OffPeakWindowsJson",
                table: "ModelPricingPlans",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "ModelPricingPlans",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OffPeakEnabled",
                table: "ModelPricingRules");

            migrationBuilder.DropColumn(
                name: "OffPeakTiersJson",
                table: "ModelPricingRules");

            migrationBuilder.DropColumn(
                name: "OffPeakUnitPrice",
                table: "ModelPricingRules");

            migrationBuilder.DropColumn(
                name: "OffPeakWindowsJson",
                table: "ModelPricingPlans");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "ModelPricingPlans");
        }
    }
}
