using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable

namespace OpenCodex.Data.Migrations.PostgresMigrations
{
    
    [DbContext(typeof(OpenCodexPostgresDbContext))]
    [Migration("20260702000000_ChannelCircuitBreakDuration")]
    /// <inheritdoc />
    public partial class ChannelCircuitBreakDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CircuitBreakDurationSeconds",
                table: "Channels",
                type: "integer",
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
