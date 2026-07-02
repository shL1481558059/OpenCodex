using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenCodex.Data;

#nullable disable

namespace OpenCodex.Data.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    [DbContext(typeof(OpenCodexSqliteDbContext))]
    [Migration("20260702000000_ChannelCircuitBreakDuration")]
    public partial class ChannelCircuitBreakDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CircuitBreakDurationSeconds",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CircuitBreakDurationSeconds",
                table: "Channels");
        }
    }
}
