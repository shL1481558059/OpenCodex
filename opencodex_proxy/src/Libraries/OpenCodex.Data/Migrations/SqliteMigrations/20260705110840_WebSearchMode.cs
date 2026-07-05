using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.SqliteMigrations
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
                type: "TEXT",
                nullable: false,
                defaultValue: "convert");

            migrationBuilder.Sql(
                """UPDATE "WebSearchSettings" SET "Mode" = CASE WHEN "Enabled" <> 0 THEN 'simulate' ELSE 'convert' END""");

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
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """UPDATE "WebSearchSettings" SET "Enabled" = CASE WHEN "Mode" = 'simulate' THEN 1 ELSE 0 END""");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "WebSearchSettings");
        }
    }
}
