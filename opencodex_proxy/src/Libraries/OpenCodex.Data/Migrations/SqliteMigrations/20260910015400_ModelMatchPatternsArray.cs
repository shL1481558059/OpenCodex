using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class ModelMatchPatternsArray : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchPatternsJson",
                table: "ModelInfos",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "MatchPatternsJson",
                table: "ChannelModelInfos",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchPatternsJson",
                table: "ModelInfos");

            migrationBuilder.DropColumn(
                name: "MatchPatternsJson",
                table: "ChannelModelInfos");
        }
    }
}
