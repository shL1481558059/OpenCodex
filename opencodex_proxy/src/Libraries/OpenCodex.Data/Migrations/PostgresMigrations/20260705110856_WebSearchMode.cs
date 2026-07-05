using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.PostgresMigrations
{
    /// <inheritdoc />
    public partial class WebSearchMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "WebSearchSettings",
                type: "text",
                nullable: false,
                defaultValue: "convert");

            migrationBuilder.Sql(
                """UPDATE "WebSearchSettings" SET "Mode" = CASE WHEN "Enabled" THEN 'simulate' ELSE 'convert' END""");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "WebSearchSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "WebSearchSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """UPDATE "WebSearchSettings" SET "Enabled" = CASE WHEN "Mode" = 'simulate' THEN TRUE ELSE FALSE END""");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "WebSearchSettings");
        }
    }
}
