using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class ChannelModelRequestKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChannelModelInfos_ChannelId_UpstreamModel",
                table: "ChannelModelInfos");

            migrationBuilder.AddColumn<string>(
                name: "RequestModel",
                table: "ChannelModelInfos",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                "UPDATE \"ChannelModelInfos\" SET \"RequestModel\" = \"UpstreamModel\";");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelInfos_ChannelId_RequestModel",
                table: "ChannelModelInfos",
                columns: new[] { "ChannelId", "RequestModel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelInfos_ChannelId_UpstreamModel",
                table: "ChannelModelInfos",
                columns: new[] { "ChannelId", "UpstreamModel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChannelModelInfos_ChannelId_RequestModel",
                table: "ChannelModelInfos");

            migrationBuilder.DropIndex(
                name: "IX_ChannelModelInfos_ChannelId_UpstreamModel",
                table: "ChannelModelInfos");

            migrationBuilder.DropColumn(
                name: "RequestModel",
                table: "ChannelModelInfos");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelModelInfos_ChannelId_UpstreamModel",
                table: "ChannelModelInfos",
                columns: new[] { "ChannelId", "UpstreamModel" },
                unique: true);
        }
    }
}
