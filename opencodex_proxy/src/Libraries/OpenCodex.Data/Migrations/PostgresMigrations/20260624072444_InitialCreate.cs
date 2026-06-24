using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations.PostgresMigrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    KeyPlaintext = table.Column<string>(type: "text", nullable: true),
                    KeyPrefix = table.Column<string>(type: "text", nullable: false),
                    KeySuffix = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<double>(type: "double precision", nullable: false),
                    LastUsedAt = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    BaseUrl = table.Column<string>(type: "text", nullable: false),
                    ApiKey = table.Column<string>(type: "text", nullable: false),
                    AuthMode = table.Column<string>(type: "text", nullable: false),
                    HeadersJson = table.Column<string>(type: "text", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    CompatJson = table.Column<string>(type: "text", nullable: false),
                    ModelsJson = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelPricings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<string>(type: "text", nullable: false),
                    Vendor = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    MatchPattern = table.Column<string>(type: "text", nullable: false),
                    InputPrice = table.Column<double>(type: "double precision", nullable: false),
                    CachedInputPrice = table.Column<double>(type: "double precision", nullable: true),
                    OutputPrice = table.Column<double>(type: "double precision", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPricings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogDetails",
                columns: table => new
                {
                    RequestLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHeaders = table.Column<string>(type: "text", nullable: true),
                    RequestBody = table.Column<string>(type: "text", nullable: true),
                    UpstreamRequestBody = table.Column<string>(type: "text", nullable: true),
                    UpstreamResponseBody = table.Column<string>(type: "text", nullable: true),
                    ResponseBody = table.Column<string>(type: "text", nullable: true),
                    WebSearchJson = table.Column<string>(type: "text", nullable: true),
                    OcrJson = table.Column<string>(type: "text", nullable: true),
                    StreamTimingsJson = table.Column<string>(type: "text", nullable: true),
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogDetails", x => x.RequestLogId);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: true),
                    ProcessingStartedAt = table.Column<double>(type: "double precision", nullable: true),
                    CompletedAt = table.Column<double>(type: "double precision", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: true),
                    Path = table.Column<string>(type: "text", nullable: true),
                    ClientIp = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    UpstreamModel = table.Column<string>(type: "text", nullable: true),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestType = table.Column<string>(type: "text", nullable: false),
                    LifecycleStatus = table.Column<string>(type: "text", nullable: true),
                    ParentRequestLogId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsStream = table.Column<bool>(type: "boolean", nullable: false),
                    TtftMs = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    CachedTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<double>(type: "double precision", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogStreamLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<double>(type: "double precision", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    RawLine = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogStreamLines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TavilyKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ApiKey = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UsageCount = table.Column<int>(type: "integer", nullable: false),
                    UsageLimit = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TavilyKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebSearchSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    KeyUsageLimit = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebSearchSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessApiKeys_KeyHash",
                table: "AccessApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessApiKeys_OwnerUserId_Id",
                table: "AccessApiKeys",
                columns: new[] { "OwnerUserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_OwnerUserId_Position",
                table: "Channels",
                columns: new[] { "OwnerUserId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_OwnerUserId_Priority_Position",
                table: "Channels",
                columns: new[] { "OwnerUserId", "Priority", "Position" });

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

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_ApiKeyId",
                table: "RequestLogs",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_ChannelId",
                table: "RequestLogs",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_CreatedAt",
                table: "RequestLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_LifecycleStatus",
                table: "RequestLogs",
                column: "LifecycleStatus");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_Model",
                table: "RequestLogs",
                column: "Model");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_OwnerUserId_Id",
                table: "RequestLogs",
                columns: new[] { "OwnerUserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_ParentRequestLogId",
                table: "RequestLogs",
                column: "ParentRequestLogId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_Path",
                table: "RequestLogs",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_RequestType",
                table: "RequestLogs",
                column: "RequestType");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_StatusCode",
                table: "RequestLogs",
                column: "StatusCode");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_UpstreamModel",
                table: "RequestLogs",
                column: "UpstreamModel");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogStreamLines_OccurredAt",
                table: "RequestLogStreamLines",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogStreamLines_RequestLogId_Sequence",
                table: "RequestLogStreamLines",
                columns: new[] { "RequestLogId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TavilyKeys_Position",
                table: "TavilyKeys",
                column: "Position");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessApiKeys");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "ModelPricings");

            migrationBuilder.DropTable(
                name: "RequestLogDetails");

            migrationBuilder.DropTable(
                name: "RequestLogs");

            migrationBuilder.DropTable(
                name: "RequestLogStreamLines");

            migrationBuilder.DropTable(
                name: "TavilyKeys");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "WebSearchSettings");
        }
    }
}
