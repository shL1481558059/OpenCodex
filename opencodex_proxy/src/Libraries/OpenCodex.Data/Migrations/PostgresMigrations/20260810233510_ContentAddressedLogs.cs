using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.PostgresMigrations
{
    /// <inheritdoc />
    public partial class ContentAddressedLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestLogDetails");

            migrationBuilder.DropTable(
                name: "RequestLogStreamLines");

            migrationBuilder.AddColumn<string>(
                name: "ConversationKey",
                table: "RequestLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConversationTurnId",
                table: "RequestLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConversationWindowId",
                table: "RequestLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousResponseId",
                table: "RequestLogs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LogContentBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    RawLength = table.Column<long>(type: "bigint", nullable: false),
                    StoredLength = table.Column<int>(type: "integer", nullable: false),
                    Compression = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogContentBlocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogContentManifests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    RawLength = table.Column<long>(type: "bigint", nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    Encoding = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogContentManifests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogContentManifestChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    BlockId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawLength = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogContentManifestChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogContentManifestChunks_LogContentBlocks_BlockId",
                        column: x => x.BlockId,
                        principalTable: "LogContentBlocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LogContentManifestChunks_LogContentManifests_ManifestId",
                        column: x => x.ManifestId,
                        principalTable: "LogContentManifests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogContentRefs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slot = table.Column<short>(type: "smallint", nullable: false),
                    ManifestId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogContentRefs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestLogContentRefs_LogContentManifests_ManifestId",
                        column: x => x.ManifestId,
                        principalTable: "LogContentManifests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestLogContentRefs_RequestLogs_RequestLogId",
                        column: x => x.RequestLogId,
                        principalTable: "RequestLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_ConversationKey",
                table: "RequestLogs",
                column: "ConversationKey");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_ConversationTurnId",
                table: "RequestLogs",
                column: "ConversationTurnId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_ConversationWindowId",
                table: "RequestLogs",
                column: "ConversationWindowId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_PreviousResponseId",
                table: "RequestLogs",
                column: "PreviousResponseId");

            migrationBuilder.CreateIndex(
                name: "IX_LogContentBlocks_Sha256",
                table: "LogContentBlocks",
                column: "Sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogContentManifestChunks_BlockId",
                table: "LogContentManifestChunks",
                column: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_LogContentManifestChunks_ManifestId_Ordinal",
                table: "LogContentManifestChunks",
                columns: new[] { "ManifestId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogContentManifests_Sha256",
                table: "LogContentManifests",
                column: "Sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogContentRefs_ManifestId",
                table: "RequestLogContentRefs",
                column: "ManifestId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogContentRefs_RequestLogId_Slot",
                table: "RequestLogContentRefs",
                columns: new[] { "RequestLogId", "Slot" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LogContentManifestChunks");

            migrationBuilder.DropTable(
                name: "RequestLogContentRefs");

            migrationBuilder.DropTable(
                name: "LogContentBlocks");

            migrationBuilder.DropTable(
                name: "LogContentManifests");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_ConversationKey",
                table: "RequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_ConversationTurnId",
                table: "RequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_ConversationWindowId",
                table: "RequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_PreviousResponseId",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "ConversationKey",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "ConversationTurnId",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "ConversationWindowId",
                table: "RequestLogs");

            migrationBuilder.DropColumn(
                name: "PreviousResponseId",
                table: "RequestLogs");

            migrationBuilder.CreateTable(
                name: "RequestLogDetails",
                columns: table => new
                {
                    RequestLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OcrJson = table.Column<string>(type: "text", nullable: true),
                    RequestBody = table.Column<string>(type: "text", nullable: true),
                    RequestHeaders = table.Column<string>(type: "text", nullable: true),
                    ResponseBody = table.Column<string>(type: "text", nullable: true),
                    StreamTimingsJson = table.Column<string>(type: "text", nullable: true),
                    UpstreamRequestBody = table.Column<string>(type: "text", nullable: true),
                    UpstreamResponseBody = table.Column<string>(type: "text", nullable: true),
                    WebSearchJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogDetails", x => x.RequestLogId);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogStreamLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<double>(type: "double precision", nullable: false),
                    RawLine = table.Column<string>(type: "text", nullable: false),
                    RequestLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogStreamLines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogStreamLines_OccurredAt",
                table: "RequestLogStreamLines",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogStreamLines_RequestLogId_Sequence",
                table: "RequestLogStreamLines",
                columns: new[] { "RequestLogId", "Sequence" },
                unique: true);
        }
    }
}
