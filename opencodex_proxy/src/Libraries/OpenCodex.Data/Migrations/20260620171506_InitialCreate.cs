using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenCodex.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    OwnerUsername = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: false),
                    AuthMode = table.Column<string>(type: "TEXT", nullable: false),
                    HeadersJson = table.Column<string>(type: "TEXT", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Capacity = table.Column<int>(type: "INTEGER", nullable: false),
                    CompatJson = table.Column<string>(type: "TEXT", nullable: false),
                    ModelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => new { x.OwnerUsername, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ModelPricings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MatchPattern = table.Column<string>(type: "TEXT", nullable: false),
                    InputPrice = table.Column<double>(type: "REAL", nullable: false),
                    CachedInputPrice = table.Column<double>(type: "REAL", nullable: true),
                    OutputPrice = table.Column<double>(type: "REAL", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPricings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: true),
                    ProcessingStartedAt = table.Column<double>(type: "REAL", nullable: true),
                    CompletedAt = table.Column<double>(type: "REAL", nullable: true),
                    Method = table.Column<string>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    ClientIp = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    UpstreamModel = table.Column<string>(type: "TEXT", nullable: true),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: true),
                    RequestType = table.Column<string>(type: "TEXT", nullable: false),
                    LifecycleStatus = table.Column<string>(type: "TEXT", nullable: true),
                    ParentRequestLogId = table.Column<long>(type: "INTEGER", nullable: true),
                    IsStream = table.Column<bool>(type: "INTEGER", nullable: false),
                    TtftMs = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CachedTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    Cost = table.Column<double>(type: "REAL", nullable: false),
                    OwnerUsername = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKeyId = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestLogs_RequestLogs_ParentRequestLogId",
                        column: x => x.ParentRequestLogId,
                        principalTable: "RequestLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TavilyKeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UsageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UsageLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TavilyKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Username);
                });

            migrationBuilder.CreateTable(
                name: "WebSearchSettings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    KeyUsageLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebSearchSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogDetails",
                columns: table => new
                {
                    RequestLogId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestHeaders = table.Column<string>(type: "TEXT", nullable: true),
                    RequestBody = table.Column<string>(type: "TEXT", nullable: true),
                    UpstreamRequestBody = table.Column<string>(type: "TEXT", nullable: true),
                    UpstreamResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                    WebSearchJson = table.Column<string>(type: "TEXT", nullable: true),
                    OcrJson = table.Column<string>(type: "TEXT", nullable: true),
                    StreamTimingsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogDetails", x => x.RequestLogId);
                    table.ForeignKey(
                        name: "FK_RequestLogDetails_RequestLogs_RequestLogId",
                        column: x => x.RequestLogId,
                        principalTable: "RequestLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogStreamLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestLogId = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAt = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    RawLine = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogStreamLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestLogStreamLines_RequestLogs_RequestLogId",
                        column: x => x.RequestLogId,
                        principalTable: "RequestLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccessApiKeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUsername = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", nullable: false),
                    KeyPlaintext = table.Column<string>(type: "TEXT", nullable: true),
                    KeyPrefix = table.Column<string>(type: "TEXT", nullable: false),
                    KeySuffix = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<double>(type: "REAL", nullable: false),
                    LastUsedAt = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessApiKeys_Users_OwnerUsername",
                        column: x => x.OwnerUsername,
                        principalTable: "Users",
                        principalColumn: "Username",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessApiKeys_KeyHash",
                table: "AccessApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessApiKeys_OwnerUsername_Id",
                table: "AccessApiKeys",
                columns: new[] { "OwnerUsername", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_OwnerUsername_Position",
                table: "Channels",
                columns: new[] { "OwnerUsername", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_OwnerUsername_Priority_Position_Id",
                table: "Channels",
                columns: new[] { "OwnerUsername", "Priority", "Position", "Id" });

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
                name: "IX_RequestLogs_OwnerUsername_Id",
                table: "RequestLogs",
                columns: new[] { "OwnerUsername", "Id" });

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
                name: "RequestLogStreamLines");

            migrationBuilder.DropTable(
                name: "TavilyKeys");

            migrationBuilder.DropTable(
                name: "WebSearchSettings");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "RequestLogs");
        }
    }
}
