# OpenCodex Python to .NET 10 Migration Progress

## Current Goal

Convert the existing Python backend into a .NET 10 project while preserving behavior.

## Latest Session State

Status: active; not fully complete.

Current .NET project location:

- Solution: `opencodex_proxy/OpenCodex.sln`
- SDK pin: `opencodex_proxy/global.json`
- API project: `opencodex_proxy/src/OpenCodex.Api/OpenCodex.Api.csproj`
- Test project: `opencodex_proxy/tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj`
- Session memory: `opencodex_proxy/MIGRATION_PROGRESS.tmp.md`

Latest completed work:

- Continued enforcing the codebase rule that non-essential C# data carriers should prefer `class` over `record` by converting additional non-value-semantic types:
  - `AdminAuthenticatedUser`
  - `AdminSessionUser`
  - `WebSearchProviderKey`
  - `WebSearchProviderResult`
  - `WebSearchSummary`
  - `AdminSessionResponse`
  - `AdminSessionUserResponse`
  - `AdminLoginErrorResponse`
  - `UsersResponse`
  - `UserResponsePayload`
  - `DeleteUserResponse`
  - `UserResponse`
  - `ApiKeysResponse`
  - `ApiKeyResponsePayload`
  - `AccessApiKeyResponse`
  - `DeleteApiKeyResponse`
  - `ConfigResponse`
  - `ChannelResponse`
  - `ConfigImportResponse`
  - `ConfigExportResponse`
  - `DiscoverModelsResponse`
  - `DiscoverModelsErrorResponse`
  - `TestChannelResponse`
  - `TestChannelErrorResponse`
  - `WebSearchConfigResponse`
  - `TavilyKeyResponse`
  - `WebSearchTestKeyResponse`
  - `WebSearchProviderResultResponse`
  - `WebSearchSummaryResponse`
  - `WebSearchTestKeyResponsePayload`
  - `AdminErrorResponse`
  - `AdminLoginRequest`
  - `WebSearchTestKeyRequest`
  - `LogsPageResponse`
  - `LogEventResponse`
  - `LogDetailResponse`
  - `StatsResponse`
  - `StatsSummaryResponse`
  - `StatsPointResponse`
  - `StatsModelDistributionResponse`
  - `ProxyEndpointContext`
  - `ProxyStreamContext`
  - `ProxyNonStreamContext`
  - `ProxyRequestLogContext`
  - `ProxyLogContext`
  - `ProxyRequestState`
  - `AdminConfigImportResult`
  - `AdminChannelTestResult`
  - `AdminDiscoverModelsResult`
  - `AdminUiFileResult`
  - `AdminUiHtmlResult`
  - `AdminUserCreateCommand`
  - `AdminUserUpdateCommand`
  - `AdminApiKeyCreateCommand`
  - `AdminApiKeyUpdateCommand`
  - `ProxyEndpointResult`
  - `ProxyNonStreamResult`
  - `WebSearchSimulationResult`
- Updated the affected constructors and call sites to keep the build green after the normalization pass.
- Verified the solution still builds successfully after the additional normalization pass:
  - `dotnet build OpenCodex.sln --no-restore -warnaserror`
- Verified the full .NET solution still passes all tests after the normalization pass:
  - `dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"`

- Enforced the codebase rule that non-essential C# data carriers should prefer `class` over `record` by converting several foundational non-value-semantic types:
  - `OpenCodexRuntimeSettings`
  - `ProxyRequestMetadata`
  - `OpenCodexSettings`
  - `RouteResult`
  - `ErrorItem`
  - `PageResult<T>`
- Updated `OpenCodexRuntimeSettingsProvider` to use the new positional constructor after the record-to-class conversion.
- Verified the solution still builds successfully after the normalization pass:
  - `dotnet build OpenCodex.sln --no-restore -warnaserror`

- Added a Web Search request policy boundary:
  - `WebSearchRequestPolicy`
  - `WebSearchRequestPolicyTests`
- Moved Web Search tool declaration detection, `max_tool_calls` coercion, and `web_search` argument parsing out of `WebSearchSimulator` into the pure policy helper.
- Added a Web Search tool-call parser boundary:
  - `WebSearchToolCallParser`
  - `WebSearchToolCall`
  - `WebSearchToolCallParserTests`
- Moved Chat `tool_calls` and Messages `tool_use` extraction out of `WebSearchSimulator`, preserving extracted call id, index, name, serialized arguments, and raw payload copy behavior.
- Added a scoped Web Search payload helper:
  - `WebSearchPayload`
- Consolidated Web Search-only JSON serialization, `JsonElement` conversion, deep-copy, dictionary/list coercion, string/int coercion, and payload lookup helpers across `WebSearchSimulator`, `WebSearchRequestPolicy`, and `WebSearchToolCallParser`.
- Kept this helper internal to the Web Search service boundary instead of making it a broad project utility.
- Added a Web Search response payload boundary:
  - `WebSearchResponsePayload`
- Moved Responses `web_search_call` item construction, web-search function-call replacement/prepending, source annotation injection, SSE `response.completed` output injection, and SSE line parsing out of `WebSearchSimulator`.
- Kept stream sequence-number and output-index calculation in `WebSearchSimulator`, because those remain stream orchestration details.
- Added `WebSearchSimulatorTests.RunAsyncReplacesOnlyWebSearchFunctionCallsWhenOtherToolCallsRemain` to preserve the mixed-tool-call response shape where local Web Search calls are replaced by `web_search_call` items while other function calls remain client-visible.
- Added a Web Search continuation request boundary:
  - `WebSearchContinuationRequest`
- Moved Chat/Messages follow-up request construction for completed Web Search tool calls out of `WebSearchSimulator`.
- Expanded `WebSearchSimulatorTests.RunAsyncReservesTavilyKeyThroughRepository` to verify the second upstream Chat request includes the assistant tool-call message and matching tool result message.
- Added a Web Search simulation log boundary:
  - `WebSearchSimulationLog`
- Moved Web Search call detail, upstream call copy, and upstream call summary construction out of `WebSearchSimulator`.
- Expanded `WebSearchSimulatorTests.RunAsyncReservesTavilyKeyThroughRepository` to verify `Details["upstream_call_summary"]` for the initial tool-call round and final answer round.
- Preserved Python-compatible Web Search parser error messages:
  - `web_search arguments must be an object`
  - `web_search arguments must be valid JSON`
  - `web_search only supports the query argument`
  - `web_search query is required`
- Preserved Web Search default/limit behavior, including boolean `max_tool_calls` falling back to 5 and negative values clamping to 0.
- Verified Web Search policy, tool-call parser, response payload helper, simulator, and proxy stream/non-stream service coverage with 32 passing filtered tests.
- Verified the full .NET solution with 287 passing tests.
- Ran `git diff --check` from `opencodex_proxy/`; no whitespace errors were reported.
- Ran a direct trailing-whitespace scan over the touched Web Search and migration-memory files; no matches were reported.
- Confirmed the migrated .NET project is isolated under `opencodex_proxy/`; continue all .NET migration work from that folder.
- Moved the temporary migration/session memory file from repository root to `opencodex_proxy/MIGRATION_PROGRESS.tmp.md`.
- Updated current README/docs references so migration history points to `opencodex_proxy/MIGRATION_PROGRESS.tmp.md`.
- Added a shared admin API controller base:
  - `AdminApiControllerBase`
  - centralizes admin query parsing, JSON body reading, session refresh, superadmin checks, elapsed-time conversion, and standard admin `{ "error": ... }` helpers.
- Split admin observability endpoints into a dedicated controller:
  - `AdminObservabilityController`
  - `GET /admin/api/logs`
  - `GET /admin/api/log-filter-options`
  - `GET /admin/api/logs/{logId}`
  - `GET /admin/api/stats`
- Removed the stale observability and duplicated base helper methods from `AdminDataController`, so it no longer references removed `_adminSession`, `_bodyReader`, or log/stat helper state.
- Split admin user management endpoints into a dedicated controller:
  - `AdminUsersController`
  - `GET /admin/api/users`
  - `POST /admin/api/users`
  - `PATCH /admin/api/users/{username}`
  - `DELETE /admin/api/users/{username}`
- Split admin API key management endpoints into a dedicated controller:
  - `AdminApiKeysController`
  - `GET /admin/api/api-keys`
  - `POST /admin/api/api-keys`
  - `PATCH /admin/api/api-keys/{keyId}`
  - `DELETE /admin/api/api-keys/{keyId}`
- Removed user and API key dependencies, actions, and response mappers from `AdminDataController`, preserving existing admin route paths and JSON response shapes.
- Split admin channel config endpoints into a dedicated controller:
  - `AdminConfigController`
  - `GET /admin/api/config`
  - `GET /admin/api/config/export`
  - `POST /admin/api/config/import`
  - `POST /admin/api/config`
- Split admin channel diagnostics endpoints into a dedicated controller:
  - `AdminChannelDiagnosticsController`
  - `POST /admin/api/channels/discover-models`
  - `POST /admin/api/discover-models`
  - `POST /admin/api/channels/test`
  - `POST /admin/api/test-channel`
- Split admin Web Search management endpoints into a dedicated controller:
  - `AdminWebSearchController`
  - `GET /admin/api/web-search`
  - `POST /admin/api/web-search`
  - `POST /admin/api/web-search/test-key`
- Deleted `AdminDataController`; its former routes are now handled by single-responsibility admin controllers while preserving route compatibility and JSON response shapes.
- Added an admin observability Service boundary:
  - `IAdminObservabilityService` / `AdminObservabilityService`
  - `IAdminObservabilityRepository` / `AdminObservabilityRepository`
  - `AdminObservabilityErrorCodes`
  - `AdminObservabilityServiceTests`
- Moved `/admin/api/logs`, `/admin/api/log-filter-options`, `/admin/api/logs/{logId}`, and `/admin/api/stats` read flow out of `AdminDataController` and into `IAdminObservabilityService`, preserving current JSON response shapes and owner scoping.
- Added an admin Web Search management Service boundary:
  - `IAdminWebSearchService` / `AdminWebSearchService`
  - `IAdminWebSearchRepository` / `AdminWebSearchRepository`
  - `AdminWebSearchErrorCodes`
  - `AdminWebSearchServiceTests`
- Moved `/admin/api/web-search` read/save and `/admin/api/web-search/test-key` key reservation out of `AdminDataController`, preserving existing response JSON and error messages.
- Added an admin channel config Service boundary:
  - `IAdminConfigService` / `AdminConfigService`
  - `IAdminConfigRepository` / `AdminConfigRepository`
  - `AdminConfigErrorCodes`
  - `AdminConfigImportResult`
  - `AdminConfigServiceTests`
- Moved `/admin/api/config`, `/admin/api/config/export`, `/admin/api/config/import`, and `/admin/api/config` save flow out of direct database access in `AdminDataController`, preserving channel JSON shape, import skip behavior, validation messages, and regular-user owner scoping.
- Added a proxy access Service boundary:
  - `IProxyAccessService` / `ProxyAccessService`
  - `IProxyAccessRepository` / `ProxyAccessRepository`
  - `ProxyAccessServiceTests`
- Moved Bearer API key parsing and access-key authentication out of `ProxyController`, preserving the existing `valid bearer api key required` 401 behavior.
- Added a proxy request runtime context Service boundary:
  - `IProxyRequestService` / `ProxyRequestService`
  - `ProxyRequestState`
  - `ProxyRequestServiceTests`
- Moved request id generation, runtime owner/default timeout reads, Authorization header access-key authentication, and JSON body reading out of `ProxyController`, preserving the existing proxy authentication and request body semantics.
- `ProxyController` now depends on `IProxyRequestService` instead of directly injecting runtime settings, proxy access, and request body reading services.
- Added a proxy request logging Service boundary:
  - `IProxyLogService` / `ProxyLogService`
  - `IProxyLogRepository` / `ProxyLogRepository`
  - `ProxyRequestLogContext`
  - `ProxyLogContext`
  - `ProxyLogServiceTests`
- Moved proxy usage extraction, cost calculation, and request-log persistence out of `ProxyController`, preserving request log field names, JSON serialization shape, and token/cost behavior.
- Moved proxy request HTTP metadata mapping into `ProxyLogService`:
  - `ProxyController` now passes `ProxyLogContext`, `HttpRequest`, and client IP to `IProxyLogService`.
  - `ProxyLogService` now owns method/path/client_ip extraction and request header redaction for log persistence.
  - Added `ProxyLogServiceTests.WriteLogBuildsHttpMetadataAndRedactsAuthorization`.
  - Preserved Authorization redaction shape and persisted request log JSON fields.
- Added a proxy endpoint orchestration Service boundary:
  - `IProxyEndpointService` / `ProxyEndpointService`
  - `ProxyEndpointContext`
  - `ProxyEndpointResult`
  - `ProxyEndpointServiceTests`
- Moved proxy request start, access-key authentication, JSON body reading, route selection, protocol request conversion, stream/non-stream dispatch, streaming compatibility rejection, and early proxy failure logging out of `ProxyController`.
- `ProxyController` now only exposes the three `/v1/*` routes and maps `IProxyEndpointService` results to `EmptyResult` or `StatusCode(...)`.
- Moved streaming conversion capability checks into the protocol layer:
  - Added `ProtocolConverter.SupportsStreamingConversion`.
  - `ProxyController` now delegates streaming compatibility checks to `ProtocolConverter`.
  - Added `ProtocolConverterTests.SupportsStreamingConversionMatchesMigratedSsePaths`.
  - Preserved the current migrated SSE matrix: same-protocol streams, Responses-to-Chat streams, and Responses-to-Messages streams.
- Added a proxy SSE response writer boundary:
  - `ProxyStreamResponseWriter`
  - `ProxyStreamResponseWriterTests`
- Moved SSE response header setup, async line writing, response flushing, and TTFT measurement out of `ProxyController`, preserving the current stream output and TTFT rules:
  - passthrough streams count the first non-empty line for TTFT.
  - converted/Web Search streams use `SseStreamConverter.CountsForTtft`.
- Added a proxy streaming orchestration Service boundary:
  - `IProxyStreamService` / `ProxyStreamService`
  - `ProxyStreamContext`
  - `ProxyStreamServiceTests`
- Moved stream-mode upstream invocation, converted SSE orchestration, Web Search stream simulation wiring, stream log context creation, and stream duration/error logging out of `ProxyController`, preserving existing `/v1/*` SSE output, TTFT rules, reconstructed log payloads, and route compatibility.
- `ProxyController` now delegates stream-mode requests to `IProxyStreamService` and only maps the service completion back to `EmptyResult`.
- Added a proxy non-streaming orchestration Service boundary:
  - `IProxyNonStreamService` / `ProxyNonStreamService`
  - `ProxyNonStreamContext`
  - `ProxyNonStreamResult`
  - `ProxyNonStreamServiceTests`
- Moved non-stream upstream invocation, Web Search simulation wiring, protocol response conversion, proxy-compatible error response mapping, and non-stream log context creation out of `ProxyController`, preserving `/v1/*` response shapes, Web Search log details, and upstream error behavior.
- `ProxyController` now delegates non-stream requests to `IProxyNonStreamService` and maps the returned status/payload with `StatusCode(...)`.
- Cleaned up `ProxyController` post-extraction state so early proxy failures log explicit `null` upstream/response/Web Search details instead of carrying unused local variables.
- Added a proxy route selection Service boundary:
  - `IProxyRouteService` / `ProxyRouteService`
  - `IProxyRouteRepository` / `ProxyRouteRepository`
  - `ProxyRouteServiceTests`
- Moved channel loading, channel-to-config mapping, environment-variable expansion, and `ChannelRouter.ChooseChannel` orchestration out of `ProxyController`, preserving owner-scoped route selection, model mapping, disabled-channel errors, and env expansion behavior.
- Added a proxy Web Search runtime repository boundary:
  - `IProxyWebSearchRepository` / `ProxyWebSearchRepository`
  - `WebSearchSimulatorTests`
- Moved Web Search enabled-state reads and Tavily key reservation out of `WebSearchSimulator` direct database access, so the simulator now depends on `IProxyWebSearchRepository` and `ProxyController` no longer passes `dbPath` through Web Search simulation calls.
- Added a Web Search simulation interface boundary:
  - `IWebSearchSimulator`
- `ProxyNonStreamService` and `ProxyStreamService` now depend on `IWebSearchSimulator` instead of the concrete `WebSearchSimulator`; DI maps `IWebSearchSimulator` to `WebSearchSimulator`.
- Updated proxy service tests to use lightweight `IWebSearchSimulator` fakes while keeping `WebSearchSimulatorTests` focused on concrete simulation behavior.
- Added an admin session validation Service boundary:
  - `IAdminSessionService` / `AdminSessionService`
  - `IAdminSessionRepository` / `AdminSessionRepository`
  - `AdminSessionUser`
  - `AdminSessionServiceTests`
- Moved current-session user refresh and enabled-user validation out of `AdminSession` direct database access, preserving `admin authentication required` 401 and `superadmin required` 403 behavior while keeping `AdminSession` focused on session storage.
- Added an admin channel diagnostics Service boundary:
  - `IAdminChannelDiagnosticsService` / `AdminChannelDiagnosticsService`
  - `AdminDiscoverModelsResult`
  - `AdminChannelTestResult`
  - `AdminChannelDiagnosticsServiceTests`
- Moved `/admin/api/channels/discover-models`, `/admin/api/discover-models`, `/admin/api/channels/test`, and `/admin/api/test-channel` channel draft parsing, config validation, model discovery, compat handling, protocol conversion, and upstream diagnostic calls out of `AdminDataController`, preserving response JSON and error handling.
- Expanded the admin Web Search management Service boundary:
  - `AdminWebSearchTestResult`
  - `IAdminWebSearchService.TestKeyAsync`
  - additional `AdminWebSearchServiceTests`
- Moved `/admin/api/web-search/test-key` Tavily test execution out of `AdminDataController`, so the controller no longer directly depends on `IWebSearchClient`; it now only parses the key id/query and maps `AdminWebSearchService` output back to the existing JSON shape.
- Kept the migrated .NET project grouped under `opencodex_proxy/`.
- Confirmed the repository root has no remaining `.sln`, `.csproj`, or `.cs` files outside `opencodex_proxy/`; the .NET project is already isolated in that folder.
- Added an admin UI static asset Service boundary:
  - `IAdminUiService` / `AdminUiService`
  - `AdminUiFileResult`
  - `AdminUiServiceTests`
- Moved admin SPA static directory resolution, safe asset path resolution, SPA index fallback, file existence checks, and content-type detection out of `AdminUiController`; the controller now only maps the service result to `PhysicalFile`, login fallback HTML, or redirects.
- Added a shared request body reading Service boundary:
  - `IRequestBodyReader` / `RequestBodyReader`
  - `RequestBodyReaderTests`
- Moved repeated JSON/form request body parsing out of `AdminAuthController`, `AdminDataController`, and `ProxyController`; the controllers now delegate JSON object and form-or-JSON parsing to `IRequestBodyReader`.
- Preserved existing request body semantics, including `null` for invalid/non-object JSON in API endpoints, empty login body fallback for invalid JSON, nested object/list parsing, and small JSON integer values staying `int` instead of being widened to `long` for config validation compatibility.
- Added standardized result DTO foundations under `opencodex_proxy/src/OpenCodex.Api/DTOs/Results/`.
- Added `ApiControllerBase` and moved controllers to inherit from it.
- Centralized startup/DI/pipeline wiring under `opencodex_proxy/src/OpenCodex.Api/Infrastructure/`.
- Added request trace handling with `X-Request-Id`; incoming request IDs now set `HttpContext.TraceIdentifier`, and responses get the same header before response start or as a fallback after the next middleware returns.
- Updated global error handling so existing proxy-compatible `ProxyException` responses keep the OpenAI/Python-compatible `{"error": ...}` shape, while unexpected non-proxy exceptions return a standardized `ApiResult` with traceId.
- Fixed `ProxyErrorTests.NonProxyExceptionsReturnStableApiResultWithTraceId` after the directory move/normalization edits.
- Added an admin authentication Service boundary:
  - `IAdminAuthService` / `AdminAuthService`
  - `IAdminUserRepository` / `AdminUserRepository`
  - `AdminAuthenticatedUser`
  - `AdminAuthServiceTests`
- Moved API and HTML admin login flows from direct database authentication in controllers to `IAdminAuthService`.
- Added a runtime settings provider:
  - `OpenCodexRuntimeSettings`
  - `IOpenCodexRuntimeSettingsProvider`
  - `OpenCodexRuntimeSettingsProvider`
- Consolidated `DbPath`, `AdminUsername`, and `DefaultTimeout` reads in `AdminDataController` and `ProxyController` through `IOpenCodexRuntimeSettingsProvider`.
- Reduced `AdminSession` back toward session-only responsibilities by removing admin bootstrap/config-key parsing from it.
- Added direct runtime settings provider tests and removed the existing `xUnit2031` analyzer warning in `ProtocolConverterTests`.
- Added an admin user management Service boundary:
  - `IAdminUserService` / `AdminUserService`
  - `AdminUserErrorCodes`
  - expanded `IAdminUserRepository` / `AdminUserRepository`
  - `AdminUserServiceTests`
- Moved `/admin/api/users` list/create/update/delete business flow out of `AdminDataController` and into `IAdminUserService`, while keeping the existing admin API response JSON and HTTP status behavior unchanged.
- Added an admin API key management Service boundary:
  - `IAdminApiKeyService` / `AdminApiKeyService`
  - `IAdminApiKeyRepository` / `AdminApiKeyRepository`
  - `AdminApiKeyErrorCodes`
  - `AdminApiKeyServiceTests`
- Moved `/admin/api/api-keys` list/create/update/delete business flow out of `AdminDataController` and into `IAdminApiKeyService`, preserving superadmin vs regular-user owner scoping and the existing JSON response shapes.

Compatibility boundary:

- Do not broadly wrap `/v1/responses`, `/v1/chat/completions`, `/v1/messages`, or existing admin API success payloads in `ApiResult` yet; clients and frontend tests depend on their current shapes.
- `ApiResult` is currently infrastructure for unexpected errors and future normalized endpoints.

Latest verification:

```bash
cd opencodex_proxy
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
```

Result: passed with 261 tests, 0 failed, 0 skipped.

Latest code normalization verification:

```bash
cd opencodex_proxy
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
```

Result: .NET tests passed with 261 tests; `git diff --check` passed.

## Completed

- Installed .NET SDK 10.0.300 into `/Users/w/.dotnet`.
- Verified .NET 10 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet --info`
- Created the first .NET 10 ASP.NET Core API skeleton.
- Kept the existing Python backend untouched in this first migration unit.
- Completed Unit 3 infrastructure and verification baseline:
  - Added `global.json` pinning SDK `10.0.300`.
  - Added .NET build output ignore rules for `bin/`, `obj/`, `TestResults/`, and `*.trx`.
  - Added `tests/OpenCodex.Api.Tests`.
  - Converted the skeleton API to ASP.NET Core controllers.
  - Added Development Swagger support via Swashbuckle.
  - Added smoke tests for `GET /`, `GET /health`, and `/swagger/v1/swagger.json`.
- Verified Unit 3 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 3 passed, 0 failed, 0 skipped.
- Completed Settings module migration as a pure, additive .NET unit:
  - Added `OpenCodexSettings`.
  - Added `OpenCodexSettingsLoader`.
  - Added `OpenCodexSettingsException`.
  - Preserved Python-compatible defaults, admin password validation, log level validation, log view level validation, positive integer parsing, `.env` loading, and environment-variable override behavior.
  - Added Settings parity tests.
- Verified Settings migration with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 18 passed, 0 failed, 0 skipped.
- Completed Error Model and API Error Responses migration:
  - Added .NET `ProxyException`, `BadRequestException`, `RoutingException`, and `UpstreamException`.
  - Added `ProxyErrorMiddleware`.
  - Wired `ProxyErrorMiddleware` into the ASP.NET Core pipeline before controllers.
  - Preserved Python-compatible error JSON shape:
    `{"error":{"message":"...","type":"..."}}`
  - Preserved `UpstreamException` optional `channel_id` and `upstream` fields.
  - Preserved Python-style instance status-code override behavior for proxy errors.
  - Added error response parity tests.
- Verified Error Model migration with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 24 passed, 0 failed, 0 skipped.
- Completed Config Validation and Channel Routing migration as a pure, additive .NET unit:
  - Added config exception, constants, dynamic config value helpers, env expansion, normalizer, and validator.
  - Added channel router and route result.
  - Preserved Python-compatible validation messages for core `config.py` cases.
  - Preserved model mapping normalization and routing semantics from `routing.py`.
  - Added parity tests derived from `tests/test_config.py`.
- Verified Config/Routing migration with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 66 passed, 0 failed, 0 skipped.
- Completed Protocol Conversion, Slice 1 as a pure, additive .NET unit:
  - Added `ProtocolConverter` with Python-compatible `ConvertRequest` and `ConvertResponse` entrypoints.
  - Preserved same-protocol request/response deep-copy behavior and model rewriting.
  - Added baseline request conversion for Responses, Chat Completions, and Messages.
  - Added baseline response conversion for Responses, Chat Completions, and Messages.
  - Preserved core Python mappings for:
    - Responses string/list input to Chat messages.
    - Responses instructions to Chat system messages.
    - Responses function tools to Chat tools.
    - `max_output_tokens` to `max_tokens`.
    - Chat response text/tool calls/reasoning to Responses output.
    - Chat `finish_reason == "length"` to Responses `incomplete_details.reason == "max_output_tokens"`.
    - Chat annotations `summary` to Responses annotation `snippet`.
    - Messages response usage to Chat usage.
  - Added protocol conversion parity tests derived from `tests/test_protocols.py`.
- Verified Protocol Conversion, Slice 1 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 74 passed, 0 failed, 0 skipped.
- Completed Protocol Conversion, Slice 2 as a pure, additive .NET unit:
  - Added Responses native tool wrappers for Chat targets:
    - `web_search`
    - `local_shell`
    - `apply_patch` proxy tools
  - Added Responses tool call/output history replay into Chat assistant/tool messages.
  - Added reasoning item folding into adjacent assistant tool-call messages.
  - Added orphan tool-output removal and placeholder outputs for missing tool results.
  - Added namespace tool flattening for Chat and reconstruction for Responses.
  - Added apply_patch raw input wrapping into `{ "patch": "..." }` for Chat tool calls.
  - Added parity tests derived from the higher-risk `tests/test_protocols.py` tool/history cases.
- Verified Protocol Conversion, Slice 2 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 80 passed, 0 failed, 0 skipped.
- Completed SQLite Persistence, Slice 1 as a pure, additive .NET unit:
  - Added `Microsoft.Data.Sqlite`.
  - Added `OpenCodexDatabase` and `ChannelRecord`.
  - Preserved SQLite schema initialization for request logs, request log details, channels, users, access API keys, web search settings, and Tavily keys.
  - Preserved idempotent initialization for the migrated slice.
  - Preserved channel replacement/read semantics, owner scoping, JSON fields, defaults, ordering, and legacy channel primary-key migration.
  - Added persistence parity tests derived from `tests/test_db.py`.
- Verified SQLite Persistence, Slice 1 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 85 passed, 0 failed, 0 skipped.
- Completed SQLite Persistence, Slice 2 as a pure, additive .NET unit:
  - Added `UserRecord`, `AccessApiKeyRecord`, and authenticated access-key record shapes.
  - Preserved Python-compatible PBKDF2 password hash format:
    `pbkdf2_sha256$200000$<salt>$<digest>`.
  - Preserved `ensure_superadmin`, user create/list/get/authenticate/enable/password reset/delete primitives.
  - Preserved access API key generation, SHA-256 hashing, plaintext copy field, prefix/suffix masking, listing, enable/delete, and authentication behavior.
  - Preserved disabled user/key rejection and delete-user cleanup of owned API keys and channels.
  - Added parity tests derived from `tests/test_db.py`.
- Verified SQLite Persistence, Slice 2 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 92 passed, 0 failed, 0 skipped.
- Completed SQLite Persistence, Slice 3 as a pure, additive .NET unit:
  - Added `WebSearchConfigRecord` and `TavilyKeyRecord`.
  - Preserved Web Search config read/replace behavior.
  - Preserved Tavily provider normalization, enabled flags, key ordering, default/per-key usage limits, and usage counts.
  - Preserved usage-count retention when the key/provider stays the same, and reset when the key string changes.
  - Preserved `reserve_tavily_key` and `reserve_tavily_key_by_id` behavior, including usage-count increment on reservation and exhausted-key rejection.
  - Added parity tests derived from `tests/test_db.py`.
- Verified SQLite Persistence, Slice 3 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 105 passed, 0 failed, 0 skipped.
- Completed SQLite Persistence, Slice 4 as a pure, additive .NET unit:
  - Added `UsageRecord` and `RequestLogRecord`.
  - Preserved `extract_usage` behavior for Responses, Chat Completions, and Messages.
  - Preserved known model cost calculation and fuzzy model matching from `pricing.json`.
  - Added synchronous request-log metadata/detail insertion.
  - Added basic request-log list and by-id detail readback.
  - Preserved owner/API-key fields, request status derivation, and `web_search_json` detail storage.
  - Added parity tests derived from `tests/test_db.py`.
- Verified SQLite Persistence, Slice 4 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln`
  Result: 109 passed, 0 failed, 0 skipped.
- Completed SQLite Persistence, Slice 5 as a pure, additive .NET unit:
  - Added request-log page/event records.
  - Preserved `read_logs_page` total/page/page_size/events behavior.
  - Preserved common text filters, integer filters, request-status filters, and created_at range filters.
  - Added `read_log_filter_options` and `read_log_filter_option` equivalents with Python-compatible distinct option behavior.
  - Kept paginated events metadata-only while preserving full request/response detail fields in `ReadLogById`.
  - Added parity tests derived from `tests/test_db.py`.
- Verified SQLite Persistence, Slice 5 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore`
  Result: 114 passed, 0 failed, 0 skipped.
- Completed Unit 16 Admin API Controllers, Slice 1:
  - Added thin controller endpoints for request logs, log filter options, log detail, and dashboard stats.
  - Preserved Python-compatible snake_case response shape for the migrated admin data.
  - Added controller tests for paging, filter options, detail 404, and stats shape.
- Verified Unit 16 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore`
  Result: 131 passed, 0 failed, 0 skipped.
- Completed Unit 17 Admin API Controllers, Slice 2:
  - Added thin controller endpoints for users, access API keys, channels/config, and Web Search config.
  - Preserved Python-compatible status codes and `{"error": "..."}` error bodies for this admin slice.
  - Preserved admin-facing snake_case JSON fields for users, API keys, channels, and Web Search keys.
  - Kept authentication/session migration out of scope; this slice exposes the migrated persistence surface from a superadmin/default-admin perspective.
  - Added controller tests for users, API keys, config save/read, and Web Search save/read.
- Verified Unit 17 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj --logger "trx;LogFileName=unit17.trx" --results-directory TestResults`
  Result: 135 passed, 0 failed, 0 skipped.
- Completed Unit 18 Admin Authentication and Session Slice:
  - Added ASP.NET Core session support.
  - Added `/admin/api/session`, `/admin/api/login`, and `/admin/api/logout`.
  - Added configured superadmin bootstrapping from `OpenCodex:AdminUsername` / `OpenCodex:AdminPassword` and existing env-style fallbacks.
  - Added session-backed `RequireUser` / `RequireSuperadmin` behavior for admin controllers.
  - Scoped logs, log-filter options, log detail, stats, config, and access API keys for regular users.
  - Kept users and Web Search management superadmin-only.
  - Added tests for login/session/logout, invalid login, unauthenticated admin access, superadmin endpoints, regular-user forbidden behavior, API-key scoping, config owner forcing, and log/stat scoping.
- Verified Unit 18 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj --logger "console;verbosity=minimal"`
  Result: 142 passed, 0 failed, 0 skipped.
- Completed Unit 19 Proxy Entrypoints and Upstream HTTP Slice 1:
  - Added default `IUpstreamClient` HTTP implementation with Python-compatible upstream URL joining, auth/header behavior, timeout handling, retry handling, HTTP error mapping, and invalid JSON handling for non-streaming JSON requests.
  - Added `ProxyController` for non-streaming `POST /v1/responses`, `POST /v1/chat/completions`, and `POST /v1/messages`.
  - Enforced bearer access API-key authentication for proxy traffic through migrated SQLite access-key records.
  - Routed requests by authenticated owner username, preserving regular-user channel isolation without falling back to admin channels.
  - Wired proxy payloads through the migrated `ProtocolConverter` and `ChannelRouter`.
  - Added synchronous request logging for proxy attempts, including redacted request headers, request/upstream/response bodies, owner/API-key, channel, usage, cost, status, and error details.
  - Kept streaming/SSE and Web Search simulation out of this slice.
  - Added controller tests for missing bearer auth, Responses-to-Chat conversion/logging, owner-scoped routing, upstream errors, and no-admin-fallback routing failures.
- Verified Unit 19 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  Result: 147 passed, 0 failed, 0 skipped. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.
- Completed Unit 20 Streaming/SSE Proxy Slice 1:
  - Extended `IUpstreamClient` with `StreamJsonAsync` and added streaming HTTP support with `HttpCompletionOption.ResponseHeadersRead`.
  - Added minimal `SseStreamConverter` for Chat Completions SSE text chunks to Responses SSE events, including final reconstructed upstream/converted response bodies.
  - Updated `ProxyController` so `stream=true` no longer returns the Unit 19 placeholder error for supported paths.
  - Added same-protocol SSE passthrough for `/v1/chat/completions` and other same-protocol routes.
  - Added Responses-to-Chat streaming conversion for text output, TTFT recording, stream response headers, and post-stream request logging.
  - Kept Messages streaming, tool-call streaming details, Web Search streaming simulation, and advanced patch semantic previews out of this first streaming slice.
  - Added controller tests for same-protocol Chat SSE passthrough and Responses-to-Chat stream conversion/log reconstruction.
- Verified Unit 20 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  Result: 149 passed, 0 failed, 0 skipped. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.
- Completed Unit 21 Streaming/SSE Proxy Slice 2:
  - Extended Chat Completions SSE to Responses SSE conversion with streamed `tool_calls` argument accumulation.
  - Added Responses SSE events for tool calls:
    - `response.output_item.added`
    - `response.function_call_arguments.delta`
    - `response.function_call_arguments.done`
    - `response.output_item.done`
  - Reconstructed streamed Chat upstream responses with `message.tool_calls` for request-log persistence and converted Responses response bodies.
  - Added minimal Messages SSE to Responses SSE conversion for:
    - `message_start`
    - `content_block_start`
    - `content_block_delta` text deltas
    - `message_delta` usage/stop_reason
    - `message_stop`
  - Reconstructed streamed Messages upstream responses with text content and usage for request-log persistence.
  - Updated `ProxyController` streaming conversion support for Responses-to-Messages channels.
  - Added proxy controller tests for Chat tool-call streaming/log reconstruction and Messages text streaming/log reconstruction.
  - Kept reasoning stream deltas, annotations, Messages tool_use streaming, Web Search streaming simulation, and patch semantic previews out of this slice.
- Verified Unit 21 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln && DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  Result: 151 passed, 0 failed, 0 skipped. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.
- Completed Unit 22 Web Search Proxy Simulation Slice:
  - Added `IWebSearchClient` and default Tavily HTTP client.
  - Added `WebSearchSimulator` for non-streaming Responses proxy requests that declare `{"type":"web_search"}`.
  - Preserved Python-compatible superadmin-only local Web Search simulation guard.
  - Preserved Tavily key reservation/usage-count behavior through existing SQLite persistence.
  - Implemented non-streaming multi-round Web Search flow:
    - First upstream call can return `web_search` tool calls.
    - Simulator reserves a Tavily key, executes search, appends a tool result to the upstream request, and calls upstream again for the final answer.
    - If no key is available, feeds back a `搜索不可用` tool result and asks upstream for a final answer.
  - Added Responses output `web_search_call` items and source URL annotations for successful search results.
  - Added `web_search_json` request-log persistence for simulated Web Search calls.
  - Added controller tests for successful Web Search simulation, no-key fallback, and regular-user declaration not triggering local simulation.
  - Kept streaming Web Search SSE injection, multiple continued stream searches, admin `/admin/api/web-search/test-key`, and upstream model discovery out of this slice.
- Verified Unit 22 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  Result: 154 passed, 0 failed, 0 skipped. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.
- Completed Unit 23 Web Search Streaming SSE Slice:
  - Extended Chat-to-Responses SSE conversion with controllable `response.created` emission, output/sequence offsets, and suppression of selected tool names.
  - Added streamed Responses Web Search simulation for superadmin requests routed to Chat channels.
  - Suppressed upstream `web_search` function-call events from the client stream and synthesized Responses `web_search_call` output items while Tavily search runs.
  - Fed Web Search tool results back into a second streamed upstream Chat request and streamed the final answer to the client.
  - Preserved streamed request logging for TTFT, final upstream request/response reconstruction, response body reconstruction, and `web_search_json`.
  - Added tests for streamed Web Search execution and for the declared-but-not-called Web Search fallback to normal streaming.
  - Kept streamed Messages `tool_use` Web Search, multi-round continued streamed searches, and patch semantic previews out of this slice.
- Verified Unit 23 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln && DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  Result: 156 passed, 0 failed, 0 skipped. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.
- Completed Unit 24 Admin Web Search Test-Key and Model Discovery Slice:
  - Added admin endpoint parity for `POST /admin/api/web-search/test-key`.
  - Reused `IWebSearchClient` for Tavily validation and preserved Python-compatible disabled-key test behavior via `ReserveTavilyKeyById`.
  - Preserved usage-count increment, usage-limit rejection, response `key`, `result`, and refreshed `config` payloads.
  - Added `IUpstreamModelClient` and implemented upstream model discovery through `HttpUpstreamClient` GET `/models`.
  - Added `POST /admin/api/channels/discover-models` and compatibility alias `POST /admin/api/discover-models`.
  - Preserved draft channel normalization, environment expansion, validation, unique model-id extraction, and 502 upstream-error response shape.
  - Added controller tests for disabled Web Search key testing, usage-limit rejection, model discovery success, and model discovery upstream error mapping.
- Verified Unit 24 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln && DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  Result: 160 passed, 0 failed, 0 skipped. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.
- Completed Unit 25 Admin Channel Test and Config Import/Export Slice:
  - Added `GET /admin/api/config/export`.
  - Added `POST /admin/api/config/import`.
  - Added `POST /admin/api/channels/test` and compatibility alias `POST /admin/api/test-channel`.
  - Preserved Python-compatible config export payload shape, attachment filename, full channel API keys, and exclusion of Web Search config.
  - Preserved config import append-without-overwrite behavior, skipped id reporting, validation rejection, and owner-aware save path.
  - Preserved channel test model mapping rewrite, same-protocol request/response conversion, compat rule application, and upstream error response body with HTTP 200 `{ "ok": false }`.
  - Fixed admin controller list handling so typed in-memory lists produced by `ConfigToJson` are treated as JSON lists during import.
  - Added controller parity tests for export, import, invalid import, channel test success, and channel test upstream error.
- Verified Unit 25 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  Result: 165 passed, 0 failed, 0 skipped. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.
- Completed Unit 26 Admin Static Frontend and Route Compatibility Slice:
  - Added `AdminUiController`.
  - Changed `GET /` to redirect to `/admin`, matching the Python Flask entrypoint behavior.
  - Added `GET /admin` fallback behavior:
    - serve built SPA `index.html` when available;
    - otherwise show login HTML for unauthenticated sessions;
    - show the "admin frontend not built" fallback for authenticated sessions when no SPA build exists.
  - Added `POST /admin` form-login compatibility for the legacy admin shell.
  - Added `GET /admin/{**assetPath}` to serve built SPA assets, return SPA index for client-side routes, or redirect to `/admin` when no SPA build exists.
  - Added `POST /admin/logout` compatibility alias alongside `POST /admin/api/logout`.
  - Added deterministic smoke tests using `OpenCodex:AdminStaticPath` so tests do not depend on local frontend build artifacts.
- Verified Unit 26 with:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  Result: 168 passed, 0 failed, 0 skipped. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.
- Completed Unit 27 Dockerfile and Deployment Runtime Slice:
  - Switched `Dockerfile` final runtime from Python 3.12 to `mcr.microsoft.com/dotnet/aspnet:10.0`.
  - Kept the existing Node/Vite admin frontend build stage.
  - Added a .NET SDK build stage that restores and publishes `src/OpenCodex.Api`.
  - Copied published .NET output into `/app` and built admin SPA assets into `/app/admin-static`.
  - Set container defaults for `OPENCODEX_HOST=0.0.0.0`, `OPENCODEX_PORT=8000`, and `OPENCODEX_ADMIN_STATIC_PATH=/app/admin-static`.
  - Updated `.dockerignore` to exclude .NET build outputs, local test results, and local archives while keeping .NET source/project files in the Docker build context.
  - Updated `Program.cs` so the .NET API listens on existing `OPENCODEX_HOST` / `OPENCODEX_PORT` settings.
- Verified Unit 27 with:
  - `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet publish src/OpenCodex.Api/OpenCodex.Api.csproj --configuration Release --output /tmp/opencodex-publish`
  - `npm run build --prefix frontend`
  - `docker build --progress=plain -t opencodex-dotnet-migration:test .`
  - container smoke using `opencodex-dotnet-migration:test`, `.env.example`, and a temporary `/app/logs` volume:
    - `GET /health` returned `{"status":"ok"}`
    - `GET /admin` returned the built Vue admin SPA index from `/app/admin-static/index.html`.
  Result: all verification steps passed.
- Completed Unit 28 Final Runtime Parity Audit and Cleanup Slice:
  - Audited Python Flask routes against the migrated ASP.NET Core controller/static/proxy routes; the public Python app entrypoints are now represented in the .NET project.
  - Added `.env` default loading parity through `DotEnvDefaults`, so local `.env` values are loaded by the runtime without overriding existing ASP.NET Core configuration or real environment variables.
  - Updated top-level runtime/deployment documentation from Python/Flask wording to .NET 10 / ASP.NET Core wording:
    - `README.md`
    - `DEPLOYMENT.md`
    - `doc/main.md`
    - `doc/deployment/main.md`
  - Kept Python source and Python tests in place as reference material for final acceptance and any remaining deep parity checks.
- Verified Unit 28 with:
  - `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln`
  - `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"`
  - `docker build --progress=plain -t opencodex-dotnet-migration:test .`
  - container smoke using `opencodex-dotnet-migration:test`, `.env.example`, and a temporary `/app/logs` volume:
    - `GET /health` returned `{"status":"ok"}`
    - `GET /admin` returned the built Vue admin SPA index from `/app/admin-static/index.html`.
  Result: 169 passed, 0 failed, 0 skipped; Docker build passed; container smoke passed.
- Completed Unit 29 Admin Static Asset Decoupling Slice:
  - Changed the Vue admin build output from the Python package path `opencodex_proxy/static/admin` to `frontend/dist/admin`.
  - Updated the .NET admin UI fallback lookup so local `dotnet run` serves `frontend/dist/admin` when `OPENCODEX_ADMIN_STATIC_PATH` is not set.
  - Updated Docker to copy `/app/frontend/dist/admin` from the frontend build stage into `/app/admin-static`.
  - Updated `.gitignore` and `.dockerignore` so the new frontend build output and the legacy generated Python static path stay out of Git and Docker contexts.
  - Updated README/deployment docs and the app gateway module note to describe the new static asset location.
  - Kept Python source and tests in place as reference material; this slice only removed the runtime asset coupling to the Python package directory.
- Completed Unit 30 Python Runtime Removal Decision Audit and Top-Level Docs Slice:
  - Audited remaining `python` / `Flask` / `opencodex_proxy` references across runtime files, deployment scripts, docs, and tests.
  - Confirmed Docker and remote image deployment now run the .NET 10 API; Python runtime files are no longer part of the container runtime path.
  - Updated `doc/project-overview/main.md` from a Flask/Python current-runtime overview to a .NET 10 / ASP.NET Core current-runtime overview.
  - Updated `doc/main.md` module index to show current .NET source files alongside Python reference source files.
  - Updated `doc/appendix/main.md` to separate current .NET source/tests from Python reference source/tests and record the 2026-06-06 runtime-doc update.
  - Updated selected deep module entry text so `app-gateway`, `admin-frontend-api`, and `auth-access-control` no longer describe Flask as the current backend.
  - Kept Python source, Python tests, and `requirements.txt` in place as reference artifacts; final deletion/archive remains a separate acceptance decision.
- Completed Unit 31 Core Admin-to-Proxy End-to-End Verification Slice:
  - Added a .NET test that exercises the migrated core workflow through public HTTP endpoints and a fake upstream:
    - admin login through `/admin/api/login`;
    - access API key creation through `/admin/api/api-keys`;
    - channel save through `/admin/api/config`;
    - proxy request through `/v1/responses` using the created bearer key;
    - upstream request model rewrite to the configured upstream model;
    - request log listing through `/admin/api/logs`;
    - request log detail through `/admin/api/logs/{id}`.
  - Verified the migrated .NET runtime can connect the admin surface, proxy surface, protocol conversion, channel routing, access-key auth, upstream call abstraction, usage extraction, and SQLite log persistence in one closed loop.
  - Kept the upstream fake local to the test; no live provider credentials or external network are required.
- Completed Unit 32 Final Python Runtime Removal Slice:
  - Removed the obsolete Python runtime directory `opencodex_proxy/`.
  - Removed the obsolete Python dependency file `requirements.txt`.
  - Removed the old Python test files under `tests/test_*.py`.
  - Cleaned Python-specific ignore rules from `.gitignore` and `.dockerignore`.
  - Updated top-level and module documentation so the current worktree is documented as .NET-only.
  - Verified the .NET-only worktree with restore, full .NET tests, frontend build, Docker build, and container smoke.
- Completed Unit 33 .NET Project Directory Consolidation:
  - Moved the .NET solution, `global.json`, API source, and .NET tests under `opencodex_proxy/`.
  - Updated Docker and current documentation to use `opencodex_proxy/OpenCodex.sln` and `opencodex_proxy/src/OpenCodex.Api`.
  - Verified restore, full .NET tests, frontend build, Docker build, container smoke, and local `dotnet run` smoke from the new project location.

## Current .NET Project

- Project directory: `opencodex_proxy/`
- Solution: `opencodex_proxy/OpenCodex.sln`
- API project: `opencodex_proxy/src/OpenCodex.Api/OpenCodex.Api.csproj`
- Test project: `opencodex_proxy/tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj`
- Target framework: `net10.0`
- API style: ASP.NET Core controllers.
- Swagger: enabled in Development via Swashbuckle.
- Runtime config: `.env` is loaded as defaults; existing configuration and real environment variables keep priority.
- Container runtime: `mcr.microsoft.com/dotnet/aspnet:10.0`, running `dotnet OpenCodex.Api.dll`.
- Admin SPA assets:
  - local build output: `frontend/dist/admin`
  - container runtime path: `/app/admin-static`
  - override: `OPENCODEX_ADMIN_STATIC_PATH`
- Documentation status:
  - top-level docs now describe .NET 10 / ASP.NET Core as the current runtime.
  - .NET implementation files are grouped under `opencodex_proxy/`.
  - Python runtime files and Python tests have been removed from the current worktree.
  - Historical Python behavior is traceable through Git history, this migration memory file, and migrated .NET regression tests.
- End-to-end verification:
  - `AdminConfiguredAccessKeyCanCallProxyAndViewPersistedLog` covers admin login, API key creation, channel save, `/v1/responses`, fake upstream, usage/log persistence, log list, and log detail.
- Current routes:
  - `GET /`
  - `GET /health`
  - `GET /swagger/v1/swagger.json` in Development
  - `GET|POST /admin`
  - `GET /admin/{asset_path}`
  - `POST /admin/logout`
  - `GET /admin/api/session`
  - `POST /admin/api/login`
  - `POST /admin/api/logout`
  - `GET /admin/api/logs`
  - `GET /admin/api/log-filter-options`
  - `GET /admin/api/logs/{id}`
  - `GET /admin/api/stats`
  - `GET|POST /admin/api/users`
  - `PATCH|DELETE /admin/api/users/{username}`
  - `GET|POST /admin/api/api-keys`
  - `PATCH|DELETE /admin/api/api-keys/{id}`
  - `GET|POST /admin/api/config`
  - `GET /admin/api/config/export`
  - `POST /admin/api/config/import`
  - `GET|POST /admin/api/web-search`
  - `POST /admin/api/web-search/test-key`
  - `POST /admin/api/channels/discover-models`
  - `POST /admin/api/discover-models`
  - `POST /admin/api/channels/test`
  - `POST /admin/api/test-channel`
  - `POST /v1/responses`
  - `POST /v1/chat/completions`
  - `POST /v1/messages`

## Pending Migration Units

1. Optional live provider E2E: run one real upstream request with a configured channel and generated access API key.
2. Optional polish: review deep module docs for wording quality now that the runtime is .NET-only.

## Next Recommended Unit

### Optional: Live Provider End-to-End Verification

Goal: run one real upstream request with a configured channel and generated access API key to validate live provider DNS, TLS, credentials, and provider-specific behavior outside the fake-upstream test harness.

Suggested scope:

- Start the .NET API locally or in Docker with a temporary logs directory.
- Create or reuse a configured channel through `/admin`.
- Create a temporary access API key through `/admin/api/api-keys`.
- Send one minimal `/v1/responses` request.
- Confirm response, request log, and log detail.

Risks:

- Live upstream credentials/network behavior still require an optional external E2E.
- Avoid committing real provider credentials or generated access keys.

## Completed Migration Unit

### Unit 33: .NET Project Directory Consolidation

Status: completed.

Goal: place the migrated .NET project under `opencodex_proxy/` as its own subproject while keeping the repository root for Docker, deployment docs, frontend, and scripts.

Moved files/directories:

- `OpenCodex.sln` -> `opencodex_proxy/OpenCodex.sln`
- `global.json` -> `opencodex_proxy/global.json`
- `src/` -> `opencodex_proxy/src/`
- `tests/` -> `opencodex_proxy/tests/`

Implemented files:

- `Dockerfile`
- `README.md`
- `DEPLOYMENT.md`
- `doc/main.md`
- `doc/project-overview/main.md`
- `doc/appendix/main.md`
- `doc/deployment/main.md`
- `doc/modules/*`
- `opencodex_proxy/src/OpenCodex.Api/Controllers/AdminUiController.cs`
- `MIGRATION_PROGRESS.tmp.md`

Behavior to preserve:

- Root-level `npm run build` still builds the Vue admin SPA to `frontend/dist/admin`.
- Root-level Docker build still publishes the .NET API and copies admin SPA assets into `/app/admin-static`.
- Local run from the repository root uses:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet run --project opencodex_proxy/src/OpenCodex.Api`
- Full .NET tests use:
  `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test opencodex_proxy/OpenCodex.sln`

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore opencodex_proxy/OpenCodex.sln
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test opencodex_proxy/OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
npm run build
docker build --progress=plain -t opencodex-dotnet-migration:test .
```

Container smoke:

```bash
docker run -d --rm --name opencodex-dotnet-migration-smoke -p 18080:8000 --env-file .env.example -v "$TMP_LOGS:/app/logs" opencodex-dotnet-migration:test
curl -fsS http://127.0.0.1:18080/health
curl -fsS http://127.0.0.1:18080/admin
```

Local run smoke:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet run --project opencodex_proxy/src/OpenCodex.Api --no-launch-profile
curl -fsS http://127.0.0.1:18081/health
curl -fsS http://127.0.0.1:18081/admin
```

Result: restore passed; full solution test passed with 170 passed, 0 failed, 0 skipped; frontend build passed; Docker build passed; container smoke passed; local `dotnet run` smoke passed. Existing warning remains: `xUnit2031` in `opencodex_proxy/tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`.

### Unit 32: Final Python Runtime Removal Slice

Status: completed.

Goal: remove obsolete Python runtime, dependency, and Python test paths from the current worktree after the .NET 10 runtime reached route, admin, proxy, streaming, Web Search, Docker, and core admin-to-proxy E2E coverage.

Removed files/directories:

- `requirements.txt`
- `opencodex_proxy/`
- `tests/test_app.py`
- `tests/test_config.py`
- `tests/test_db.py`
- `tests/test_protocols.py`
- `tests/test_reasoning_cache.py`
- `tests/test_upstream.py`

Implemented files:

- `.gitignore`
- `.dockerignore`
- `README.md`
- `doc/main.md`
- `doc/project-overview/main.md`
- `doc/appendix/main.md`
- `doc/system-flow/main.md`
- `doc/modules/app-gateway/main.md`
- `doc/modules/app-gateway/admin-frontend-api.md`
- `doc/modules/auth-access-control/main.md`
- `doc/modules/config-routing/main.md`
- `doc/modules/protocol-conversion/main.md`
- `doc/modules/protocol-conversion/apply-patch.md`
- `doc/modules/upstream-streaming/main.md`
- `doc/modules/web-search/main.md`
- `doc/modules/persistence-observability/main.md`
- `doc/modules/compat-reasoning-cache/main.md`
- `MIGRATION_PROGRESS.tmp.md`

Behavior preserved:

- Current runtime remains `.NET 10` / ASP.NET Core.
- Docker runtime remains `mcr.microsoft.com/dotnet/aspnet:10.0`, running `dotnet OpenCodex.Api.dll`.
- Admin SPA build output remains `frontend/dist/admin`, copied to `/app/admin-static` in Docker.
- Historical Python behavior remains traceable through Git history, this migration memory file, and migrated .NET regression tests.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
npm run build
docker build --progress=plain -t opencodex-dotnet-migration:test .
```

Container smoke:

```bash
docker run -d --rm --name opencodex-dotnet-migration-smoke -p 18080:8000 --env-file .env.example -v "$TMP_LOGS:/app/logs" opencodex-dotnet-migration:test
curl -fsS http://127.0.0.1:18080/health
curl -fsS http://127.0.0.1:18080/admin
```

Result: restore passed; full solution test passed with 170 passed, 0 failed, 0 skipped; frontend build passed; Docker build passed; container smoke passed. Existing warning remains: `xUnit2031` in `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`.

### Unit 31: Core Admin-to-Proxy End-to-End Verification Slice

Status: completed.

Goal: prove the migrated .NET backend can execute the core product workflow end to end without relying on the Python runtime.

Source reference:

- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`
- `src/OpenCodex.Api/Controllers/AdminAuthController.cs`
- `src/OpenCodex.Api/Controllers/AdminDataController.cs`
- `src/OpenCodex.Api/Controllers/ProxyController.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `src/OpenCodex.Api/Routing/ChannelRouter.cs`

Implemented files:

- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Behavior verified:

- Admin login establishes an authenticated session.
- Admin API creates an `ocx_...` access API key.
- Admin API saves a Chat channel with model mapping.
- Proxy `/v1/responses` accepts the generated bearer key.
- Channel routing selects the saved admin channel.
- Protocol conversion rewrites the Responses request into a Chat upstream request and maps `client-model` to `upstream-model`.
- Fake upstream response converts back into a Responses payload with the client-facing model.
- Request log metadata and detail are persisted to SQLite and visible through admin log APIs.
- Usage fields from the fake upstream response are extracted into the log event.

Known gaps:

- This is an in-process E2E with fake upstream; it does not prove live provider credentials, DNS, TLS, or remote deployment.
- Python source/tests remain available as reference and have not been deleted or archived.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj --filter "FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
```

Result: `AdminDataControllerTests` passed with 25 passed, 0 failed, 0 skipped. Full solution test passed with 170 passed, 0 failed, 0 skipped. Existing warning remains: `xUnit2031` in `ProtocolConverterTests.cs`.

### Unit 30: Python Runtime Removal Decision Audit and Top-Level Docs Slice

Status: completed.

Goal: make the repository documentation reflect that .NET 10 is the current runtime while preserving Python as explicit migration reference material until final deletion/archive is accepted.

Source reference:

- `doc/project-overview/main.md`
- `doc/main.md`
- `doc/appendix/main.md`
- `doc/modules/app-gateway/main.md`
- `doc/modules/app-gateway/admin-frontend-api.md`
- `doc/modules/auth-access-control/main.md`
- `scripts/update_remote_image.sh`
- `Dockerfile`
- `requirements.txt`
- `opencodex_proxy/`
- `tests/test_*.py`

Implemented files:

- `doc/project-overview/main.md`
- `doc/main.md`
- `doc/appendix/main.md`
- `doc/modules/app-gateway/main.md`
- `doc/modules/app-gateway/admin-frontend-api.md`
- `doc/modules/auth-access-control/main.md`
- `MIGRATION_PROGRESS.tmp.md`

Behavior preserved:

- No runtime code was changed in this slice.
- Docker continues to run `dotnet OpenCodex.Api.dll`.
- Remote image deployment script continues to build and push the repository Docker image and recreate the configured service.
- Python source/tests remain available for parity disputes and final acceptance review.

Decision audit:

- Safe-to-remove candidates after explicit acceptance:
  - `opencodex_proxy/`
  - Python-focused `tests/test_*.py`
  - `requirements.txt`
  - Python-specific module documentation sections that no longer add reference value.
- Keep for now:
  - Python source/tests, because they remain the clearest behavior reference for unported internals.
  - Deep module docs that intentionally preserve Python implementation details until final acceptance.
- Current runtime artifacts:
  - `OpenCodex.sln`
  - `global.json`
  - `src/OpenCodex.Api/`
  - `tests/OpenCodex.Api.Tests/`
  - `Dockerfile`
  - `frontend/`

Known gaps:

- Final delete/archive of Python runtime files has not been performed.
- Some deep module docs still include Python implementation details by design.
- No live upstream provider E2E was run in this slice.

Verification commands:

```bash
rg -n '轻量 Python|Flask 后端|Flask 应用|Flask Session|python-dotenv|Python 标准库|Python `logging`|Python `unittest`|当前.*Flask|当前.*Python 后端|python -m opencodex_proxy|opencodex_proxy\.app:create_app' doc README.md DEPLOYMENT.md
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
```

Result: documentation residual search completed; remaining Python/Flask mentions are in explicit old/reference or migration-history context. `dotnet restore` passed; `dotnet test` passed with 169 passed, 0 failed, 0 skipped. Existing warning remains: `xUnit2031` in `ProtocolConverterTests.cs`.

### Unit 29: Admin Static Asset Decoupling Slice

Status: completed.

Goal: remove the last practical runtime asset dependency on the Python package directory while keeping the Python backend available as behavior reference.

Source reference:

- `frontend/vite.config.js`
- `Dockerfile`
- `src/OpenCodex.Api/Controllers/AdminUiController.cs`
- `doc/modules/app-gateway/main.md`

Implemented files:

- `frontend/vite.config.js`
- `Dockerfile`
- `.gitignore`
- `.dockerignore`
- `src/OpenCodex.Api/Controllers/AdminUiController.cs`
- `README.md`
- `DEPLOYMENT.md`
- `doc/deployment/main.md`
- `doc/modules/app-gateway/main.md`
- `MIGRATION_PROGRESS.tmp.md`

Behavior preserved:

- `npm run build` still builds the Vue admin SPA with base path `/admin/`.
- Local .NET runtime can serve the built SPA from `frontend/dist/admin` by default.
- Docker runtime still serves the built SPA from `/app/admin-static` via `OPENCODEX_ADMIN_STATIC_PATH`.
- The legacy `opencodex_proxy/static/admin` generated directory remains ignored so old local artifacts are not accidentally committed or copied into Docker contexts.

Known gaps:

- Python source/tests remain in the repository as reference material.
- Final removal/archive decision for Python runtime files is still pending.

Verification commands:

```bash
npm run build
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
docker build --progress=plain -t opencodex-dotnet-migration:test .
```

Container smoke:

```bash
docker run -d --rm --name opencodex-dotnet-migration-smoke -p 18080:8000 --env-file .env.example -v "$TMP_LOGS:/app/logs" opencodex-dotnet-migration:test
curl -fsS http://127.0.0.1:18080/health
curl -fsS http://127.0.0.1:18080/admin
```

Local smoke: `dotnet run` without `OPENCODEX_ADMIN_STATIC_PATH` served `/Users/w/shL/work/shl/OpenCodex/frontend/dist/admin/index.html` for `GET /admin`.

Result: `npm run build` passed; 169 tests passed; local `/health` and `/admin` smoke passed; Docker build passed; container `/health` returned `{"status":"ok"}`; container `/admin` returned the built Vue admin SPA index.

### Unit 28: Final Runtime Parity Audit and Cleanup Slice

Status: completed.

Goal: audit runtime parity after the Docker switch, close the `.env` runtime gap, and refresh top-level docs so the repository points at the .NET 10 runtime.

Source reference:

- `opencodex_proxy/app.py`
- `opencodex_proxy/settings.py`
- `README.md`
- `DEPLOYMENT.md`
- `doc/main.md`
- `doc/deployment/main.md`

Implemented files:

- `src/OpenCodex.Api/Configuration/DotEnvDefaults.cs`
- `src/OpenCodex.Api/Program.cs`
- `tests/OpenCodex.Api.Tests/Configuration/OpenCodexSettingsTests.cs`
- `README.md`
- `DEPLOYMENT.md`
- `doc/main.md`
- `doc/deployment/main.md`
- `MIGRATION_PROGRESS.tmp.md`

Behavior preserved:

- Python Flask public routes are represented by .NET controllers, admin static serving, and proxy routes.
- `.env` is read by the runtime as default configuration.
- Existing configuration sources and real environment variables take priority over `.env` defaults.
- Top-level docs now direct local, Docker, and deployment usage to `.NET 10` / ASP.NET Core.

Known gaps:

- Python source and Python tests are still present as reference material and have not been removed.
- Deep module docs under `doc/modules/*` still primarily describe Python internals.
- Some Python tests target Flask internals and are not directly runnable against the .NET runtime without adaptation.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
docker build --progress=plain -t opencodex-dotnet-migration:test .
```

Container smoke:

```bash
docker run -d --rm --name opencodex-dotnet-migration-smoke -p 18080:8000 --env-file .env.example -v "$TMP_LOGS:/app/logs" opencodex-dotnet-migration:test
curl -fsS http://127.0.0.1:18080/health
curl -fsS http://127.0.0.1:18080/admin
```

Result: 169 tests passed; Docker build passed; container `/health` returned `{"status":"ok"}`; container `/admin` returned the built Vue admin SPA index.

## Completed Migration Unit

### Unit 27: Dockerfile and Deployment Runtime Slice

Status: completed.

Goal: switch the container runtime from Python to the migrated .NET 10 API while preserving the existing Vue admin frontend build.

Source reference:

- `Dockerfile`
- `.dockerignore`
- `frontend/vite.config.js`
- `scripts/update_remote_image.sh`
- `.env.example`

Implemented files:

- `Dockerfile`
- `.dockerignore`
- `src/OpenCodex.Api/Program.cs`
- `MIGRATION_PROGRESS.tmp.md`

Behavior preserved:

- Docker builds the Vue admin frontend with the existing `npm run build` flow.
- Docker publishes the .NET 10 API project and runs `dotnet OpenCodex.Api.dll`.
- Runtime image exposes port `8000` and defaults to `OPENCODEX_HOST=0.0.0.0`, `OPENCODEX_PORT=8000`.
- Built admin SPA assets are served from `/app/admin-static` via `OPENCODEX_ADMIN_STATIC_PATH`.
- Existing `.env.example` remains copied into the image for reference.
- Existing remote update script can continue to build/push the repository image and replace the compose service image without changing the service name assumptions.

Known gaps:

- README/DEPLOYMENT prose still describes some local Python/Flask commands and needs final doc cleanup.
- Python source is still present as reference during final audit.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet publish src/OpenCodex.Api/OpenCodex.Api.csproj --configuration Release --output /tmp/opencodex-publish
npm run build --prefix frontend
docker build --progress=plain -t opencodex-dotnet-migration:test .
```

Container smoke:

```bash
docker run -d --rm --name opencodex-dotnet-migration-smoke -p 18080:8000 --env-file .env.example -v "$TMP_LOGS:/app/logs" opencodex-dotnet-migration:test
curl -fsS http://127.0.0.1:18080/health
curl -fsS http://127.0.0.1:18080/admin
```

Result: Docker build passed; container `/health` returned `{"status":"ok"}`; container `/admin` returned the built Vue admin SPA index.

## Completed Migration Unit

### Unit 26: Admin Static Frontend and Route Compatibility Slice

Status: completed.

Goal: close the remaining simple Flask app route compatibility around `/`, `/admin`, and `/admin/logout` before deployment packaging work.

Source reference:

- `opencodex_proxy/app.py`
- `opencodex_proxy/templates/login.html`
- `opencodex_proxy/templates/admin.html`
- `frontend/vite.config.js`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminUiController.cs`
- `src/OpenCodex.Api/Controllers/AdminAuthController.cs`
- `src/OpenCodex.Api/Controllers/SystemController.cs`
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`

Behavior preserved:

- `GET /` redirects to `/admin`.
- `GET /admin` serves built SPA index when `index.html` is present under the admin static directory.
- `GET /admin` falls back to a login form when the SPA is missing and the user is unauthenticated.
- `POST /admin` accepts form login, bootstraps the configured superadmin, sets the session, and redirects to `/admin`.
- Invalid form login returns HTML containing `用户名或密码错误`.
- Authenticated `/admin` without a built SPA returns the "frontend not built" fallback shell.
- `GET /admin/{asset_path}` serves built assets with content types, returns SPA index for client-side routes, and redirects to `/admin` when no SPA build exists.
- `POST /admin/logout` clears the session and returns the same JSON shape as `/admin/api/logout`.

Known gaps:

- Dockerfile and deployment scripts still target the Python runtime.
- Full end-to-end container runtime verification is still pending.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
```

Result: passed with 168 tests. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.

## Completed Migration Unit

### Unit 25: Admin Channel Test and Config Import/Export Slice

Status: completed.

Goal: continue closing admin parity gaps by migrating channel test endpoints and config import/export flows.

Source reference:

- `opencodex_proxy/app.py`
- `opencodex_proxy/compat.py`
- `tests/test_app.py`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminDataController.cs`
- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`

Behavior preserved:

- `GET /admin/api/config/export` requires admin auth and returns only `channels` as `application/json`.
- Config export sets attachment filename `opencodex-channels-config.json`, includes full channel `apikey`, and excludes Web Search/Tavily config.
- `POST /admin/api/config/import` appends non-duplicate channel ids and skips existing `(owner_username, id)` pairs without overwriting existing channels.
- Import response includes `config`, `imported`, `skipped`, and `skipped_ids`.
- Invalid import candidates return HTTP 400 without replacing persisted channels.
- `POST /admin/api/channels/test` and `POST /admin/api/test-channel` accept draft channel plus payload bodies.
- Channel test applies model mapping rewrite to the upstream request and restores the original model in the returned response.
- Channel test applies migrated compat rules before the upstream call.
- Upstream errors in channel tests return HTTP 200 with `ok: false`, `status_code`, `duration_ms`, `error`, and upstream `body`.

Known gaps:

- Static admin frontend hosting under `/admin/*` is still pending.
- Dockerfile and deployment scripts still target the Python runtime.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
```

Result: passed with 165 tests. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.

## Completed Migration Unit

### Unit 24: Admin Web Search Test-Key and Model Discovery Slice

Status: completed.

Goal: continue closing admin/proxy parity gaps by migrating Web Search key validation and upstream model discovery endpoints.

Source reference:

- `opencodex_proxy/app.py`
- `opencodex_proxy/upstream.py`
- `tests/test_app.py`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminDataController.cs`
- `src/OpenCodex.Api/Program.cs`
- `src/OpenCodex.Api/Services/IUpstreamClient.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.cs`
- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`

Behavior preserved:

- `POST /admin/api/web-search/test-key` requires superadmin auth.
- Test-key requests parse `id`, default blank `query` to `OpenAI`, reserve the selected Tavily key by id, and increment usage count.
- Disabled Tavily keys can still be tested by id, while exhausted keys return `400` with the usage-limit message.
- Test-key success responses include `ok`, `duration_ms`, `key`, provider `result`, and refreshed Web Search `config`.
- `POST /admin/api/channels/discover-models` and `POST /admin/api/discover-models` accept either `{ "channel": {...} }` or flat draft channel fields.
- Draft channels are normalized, environment-expanded, and validated before upstream calls.
- Upstream model discovery calls GET `/models`, extracts unique ids from `data[].id`, and returns `models`, `raw`, and `duration_ms`.
- Upstream model discovery errors return HTTP 502 with `error`, upstream `status_code`, `duration_ms`, and `body`.

Known gaps:

- Channel test endpoints are still pending.
- Config import/export endpoints are still pending.
- Real frontend static hosting under `/admin/*` is still pending.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
```

Result: passed with 160 tests. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.

## Completed Migration Unit

### Unit 23: Web Search Streaming SSE Slice

Status: completed.

Goal: extend the Unit 22 non-streaming Web Search simulation to streamed Responses requests while preserving SSE output order and final request logging.

Source reference:

- `opencodex_proxy/app.py`
- `opencodex_proxy/streaming.py`
- `opencodex_proxy/web_search.py`
- `tests/test_app.py`

Implemented files:

- `src/OpenCodex.Api/Controllers/ProxyController.cs`
- `src/OpenCodex.Api/Protocols/SseStreamConverter.cs`
- `src/OpenCodex.Api/Services/WebSearchSimulator.cs`
- `tests/OpenCodex.Api.Tests/ProxyControllerTests.cs`

Behavior preserved:

- Streamed Responses requests that declare `{"type":"web_search"}` can run local Web Search simulation for superadmin-owned Chat routes.
- Upstream streamed Chat `web_search` function-call chunks are reconstructed for internal tool execution but suppressed from the client-facing Responses SSE stream.
- Client stream receives synthesized `web_search_call` `response.output_item.added` and `response.output_item.done` events.
- Tavily search uses the migrated `IWebSearchClient` and SQLite key reservation/usage-count behavior.
- Search results are appended as Chat `tool` messages before the second streamed upstream request.
- Final upstream streamed answer is converted back to Responses SSE without duplicating `response.created`.
- Streamed request logs preserve TTFT, final upstream request/response bodies, final response body, token usage, and `web_search_json`.
- Declared-but-not-called Web Search tools fall back to normal streamed conversion without executing Tavily or writing `web_search_json`.

Known gaps:

- Streamed Messages `tool_use` Web Search simulation is still pending.
- Multi-round continued Web Search during a streamed final response is still pending.
- Cumulative token usage across all Web Search stream iterations is not migrated in this slice; the logged usage follows the final upstream response.
- Patch semantic preview streaming remains pending.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore OpenCodex.sln
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
```

Result: passed with 156 tests. Existing warning: `xUnit2031` in `ProtocolConverterTests.cs`.

## Completed Migration Unit

### Unit 3: Infrastructure and Verification Baseline

Status: completed.

Goal: make the .NET migration sustainable before moving real backend logic. Each later migration unit should have a clear build/test signal and should be easy to resume from this file.

Proposed files:

- `global.json`: pin the SDK version to `10.0.300`.
- `.gitignore`: ignore .NET build outputs such as `bin/` and `obj/`.
- `tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj`: create the .NET test project.
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`: verify the current controller routes and Swagger document.
- `MIGRATION_PROGRESS.tmp.md`: record Unit 3 completion and the next migration target.

Risks:

- This unit modifies more than three files, so it should stay as a separate approved migration unit.
- `global.json` only selects the SDK when the .NET 10 SDK is reachable; local commands still need `/Users/w/.dotnet` first in `PATH` unless the shell profile is updated.
- Smoke tests only prove the new .NET skeleton works. They do not prove parity with the Python backend.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 3 smoke tests.

## Completed Migration Unit

### Unit 17: Admin API Controllers, Slice 2

Status: completed.

Goal: continue wiring migrated persistence primitives into ASP.NET Core controllers before implementing upstream proxy calls.

Source reference:

- `opencodex_proxy/app.py`
- `tests/test_app.py`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminDataController.cs`
- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`

Behavior preserved:

- `GET /admin/api/users` returns `{"users": [...]}` with Python-compatible user fields.
- `POST /admin/api/users` creates normal users and returns `201 {"user": ...}`.
- `PATCH /admin/api/users/{username}` updates enabled/password fields and returns Python-compatible 400/404 error bodies.
- `DELETE /admin/api/users/{username}` returns `{"deleted": true, "user": ...}`.
- `GET /admin/api/api-keys` supports optional `owner_username` filtering and returns plaintext copy fields when stored.
- `POST /admin/api/api-keys` creates access keys and returns `201 {"key": ...}`.
- `PATCH|DELETE /admin/api/api-keys/{id}` updates enabled state or deletes keys with Python-compatible not-found bodies.
- `GET|POST /admin/api/config` reads and replaces persisted channels with snake_case channel fields.
- `GET|POST /admin/api/web-search` reads and replaces Web Search config, including `default_key_usage_limit`, per-key usage counts, and per-key usage limits.

Known gaps:

- Admin authentication/session handling is not migrated yet; Unit 17 intentionally uses a thin superadmin/default-admin perspective.
- Regular-user scoping for config/API-key/log endpoints is not migrated yet.
- `/admin/api/config/export`, `/admin/api/config/import`, and `/admin/api/web-search/test-key` are still pending.
- Static admin frontend hosting is not wired to ASP.NET Core yet.
- Proxy `/v1/*` endpoints and upstream calls are not migrated yet.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj --logger "trx;LogFileName=unit17.trx" --results-directory TestResults
```

Result: passed with 135 tests.

## Completed Migration Unit

### Unit 18: Admin Authentication and Session Slice

Status: completed.

Goal: migrate enough of the Python admin auth/session boundary to make the already-wired admin endpoints safe and parity-testable before proxy traffic is exposed.

Source reference:

- `opencodex_proxy/app.py`
- `tests/test_app.py`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminSession.cs`
- `src/OpenCodex.Api/Controllers/AdminAuthController.cs`
- `src/OpenCodex.Api/Controllers/AdminDataController.cs`
- `src/OpenCodex.Api/Program.cs`
- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`

Behavior preserved:

- `POST /admin/api/login`, `POST /admin/api/logout`, and current-user/session status responses.
- Superadmin bootstrapping from `OPENCODEX_ADMIN_USERNAME` / `OPENCODEX_ADMIN_PASSWORD`.
- Superadmin-only access for user and Web Search management.
- Regular-user scoping for config/API-key/log/stat endpoints where Python already scopes by owner.

Known gaps:

- Static admin frontend hosting is not wired to ASP.NET Core yet.
- `/admin/api/config/export`, `/admin/api/config/import`, and `/admin/api/web-search/test-key` are still pending.
- Proxy `/v1/*` endpoints and upstream calls are not migrated yet.
- Session secret/key-ring deployment hardening is still pending for production packaging.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj --logger "console;verbosity=minimal"
```

Result: passed with 142 tests.

## Recommended Next Unit

### Unit 21: Streaming/SSE Proxy Slice 2

Goal: extend the first streaming proxy slice beyond text-only Chat streams to cover Messages streaming and function/tool-call streaming parity.

Source reference:

- `opencodex_proxy/app.py`
- `opencodex_proxy/upstream.py`
- `opencodex_proxy/streaming.py`
- `opencodex_proxy/protocols.py`
- `tests/test_app.py`
- `tests/test_upstream.py`

Expected .NET files:

- `src/OpenCodex.Api/Controllers/ProxyController.cs`
- `src/OpenCodex.Api/Services/*`
- `src/OpenCodex.Api/Protocols/*`
- `src/OpenCodex.Api/Program.cs`
- `tests/OpenCodex.Api.Tests/*`
- `MIGRATION_PROGRESS.tmp.md`

Behavior to preserve:

- Messages upstream SSE converts to Responses SSE where Python currently supports it.
- Chat tool-call SSE chunks convert to Responses function-call events and reconstruct upstream/converted response bodies.
- Same-protocol passthrough behavior from Unit 20 remains unchanged.
- Streaming request logs preserve `is_stream`, `ttft_ms`, token usage, and reconstructed bodies for tool/message streams.
- Unsupported streaming conversions fail before the SSE response starts and are logged as normal failed requests.

Risks:

- Function-call argument chunks may arrive split across arbitrary JSON boundaries.
- Messages streaming has different usage and stop-reason semantics from Chat.
- Keep Web Search simulation out of Unit 21 unless streaming compatibility depends on it.

## Completed Migration Unit

### Unit 16: Admin API Controllers, Slice 1

Status: completed.

Goal: begin wiring migrated persistence primitives into ASP.NET Core controllers before implementing upstream proxy calls.

Source reference:

- `opencodex_proxy/app.py`
- `tests/test_app.py`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminDataController.cs`
- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`

Behavior preserved:

- `GET /admin/api/logs` returns paginated `events`, `total`, `page`, and `page_size` in Python-compatible snake_case.
- Log list events omit large detail fields.
- `GET /admin/api/log-filter-options` returns a single option set by `field` and `q`.
- `GET /admin/api/logs/{id}` returns full detail fields and a Python-compatible 404 body for missing logs.
- `GET /admin/api/stats` returns dashboard stats with snake_case summary, point, and model distribution fields.
- Controller DB path is configurable through `OpenCodex:DbPath` / `OPENCODEX_DB_PATH` for tests and future runtime wiring.

Known gaps:

- Admin authentication/session handling is not migrated yet.
- Users, API keys, channels, config, and web-search admin endpoints are not wired to controllers yet.
- Proxy `/v1/*` endpoints and upstream calls are not migrated yet.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore
```

Result: passed with 131 tests.

## Completed Migration Unit

### Unit 15: SQLite Persistence, Slice 7

Status: completed.

Goal: continue migrating `db.py` by adding the async request-log writer primitive before wiring proxy controllers.

Source reference:

- `opencodex_proxy/db.py`
- `tests/test_db.py`

Implemented files:

- `src/OpenCodex.Api/Persistence/AsyncRequestLogWriter.cs`
- `tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs`

Behavior preserved:

- Construction initializes the SQLite database.
- Queued request-log writes run on a background worker.
- `Start` is idempotent.
- Records can be queued before `Start` and are flushed once the worker starts.
- `Stop` sends a sentinel and flushes queued records.
- Default owner username is applied through the existing request-log insertion logic.
- Background write failures are swallowed to match Python `AsyncDBWriter`.

Known gaps:

- The writer is not wired into ASP.NET Core DI or proxy request handling yet.
- No controller/admin API wiring was added in this slice.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore
```

Result: passed with 127 tests.

## Completed Migration Unit

### Unit 14: SQLite Persistence, Slice 6

Status: completed.

Goal: continue migrating `db.py` by adding dashboard stats primitives before wiring admin/proxy controllers.

Source reference:

- `opencodex_proxy/db.py`
- `tests/test_app.py`

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs`

Behavior preserved:

- `read_stats` range resolution for supported ranges and custom ranges.
- Empty stats response shape for missing databases.
- Custom-range bucket point generation.
- Summary totals, success counts, recent 1h counts/tokens/cost, RPM, and TPM.
- Model distribution aggregation.
- Owner scoping for stats reads.
- Python-compatible `USD_CNY_RATE` usage through `OpenCodexSettings.UsdCnyRate`.

Known gaps:

- No controller/admin API wiring was added in this slice.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore
```

Result: passed with 123 tests.

## Completed Migration Unit

### Unit 13: SQLite Persistence, Slice 5

Status: completed.

Goal: continue migrating `db.py` by adding request-log pagination and filter-option primitives before wiring admin/proxy controllers.

Source reference:

- `opencodex_proxy/db.py`
- `tests/test_db.py`

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs`

Behavior preserved:

- `read_logs_page` behavior for total, page, page_size, and metadata-only events.
- Text filters for `request_id`, `model`, `upstream_model`, `channel_id`, `owner_username`, `path`, `client_ip`, and `error`.
- Integer filters for `status_code`, `is_stream`, and `api_key_id`.
- Derived `request_status` filter semantics for `success` and `failed`.
- Created timestamp range filters via `created_from` and `created_to`.
- `read_log_filter_options` and `read_log_filter_option` distinct option behavior, including fixed `request_statuses`.
- `ReadLogById` can now apply log filters while preserving full detail fields.

Known gaps:

- Async DB writer is not migrated yet.
- No controller/admin API wiring was added in this slice.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore
```

Result: passed with 114 tests.

## Completed Migration Unit

### Unit 12: SQLite Persistence, Slice 4

Status: completed.

Goal: continue migrating `db.py` by adding request-log, usage extraction, and cost calculation primitives before wiring proxy/controllers.

Source reference:

- `opencodex_proxy/db.py`
- `opencodex_proxy/pricing.json`
- `tests/test_db.py`

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs`

Behavior preserved:

- `extract_usage` for Responses, Chat Completions, and Messages protocols.
- `calculate_cost` for the known model pricing table and fuzzy model matching.
- Request log metadata/detail insertion.
- Basic newest-first request log list readback.
- Request log by-id detail readback.
- Owner/API-key fields and `web_search_json` detail storage.
- Request status derivation from status code and error text.

Known gaps:

- Async DB writer is not migrated yet.
- Request log pagination/filter options are not migrated yet.
- Dashboard stats are not migrated yet.
- No controller/admin API wiring was added in this slice.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 109 tests.

## Completed Migration Unit

### Unit 11: SQLite Persistence, Slice 3

Status: completed.

Goal: continue migrating `db.py` by adding Web Search configuration and Tavily key reservation primitives before wiring admin/proxy controllers.

Source reference:

- `opencodex_proxy/db.py`
- `tests/test_db.py`

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs`

Behavior preserved:

- `read_web_search_config` and `replace_web_search_config` behavior.
- Tavily key ordering, enabled flags, usage counts, default/per-key usage limits, and provider validation.
- `reserve_tavily_key` and `reserve_tavily_key_by_id` behavior, including usage-count increment on reservation.
- Usage-count retention when key/provider stays the same.
- Usage-count reset when the key string changes.
- Provider case normalization to `tavily`.

Known gaps:

- Request logging, detail migration, log filters, stats, usage extraction, and cost calculation are not migrated yet.
- No controller/admin API wiring was added in this slice.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 105 tests.

## Completed Migration Unit

### Unit 10: SQLite Persistence, Slice 2

Status: completed.

Goal: continue migrating `db.py` by adding the user and access API key persistence primitives before wiring admin/proxy controllers.

Source reference:

- `opencodex_proxy/db.py`
- `tests/test_db.py`

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs`

Behavior preserved:

- Python-compatible PBKDF2 password hash format and verification.
- `ensure_superadmin` as environment-authoritative bootstrap/update behavior.
- User create/list/get/authenticate/enable/password reset/delete primitives.
- Access API key creation with `ocx_` prefix, SHA-256 hash, plaintext copy field, prefix/suffix, and mask.
- Access key listing, enable/disable, delete, and authentication.
- Disabled user/access-key rejection.
- Delete-user cleanup of owned access keys and channels.

Known gaps:

- Web Search configuration and Tavily key reservation are not migrated yet.
- Request logging, detail migration, log filters, stats, and cost extraction are not migrated yet.
- No controller/admin API wiring was added in this slice.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 92 tests.

Risks:

- Access key generation uses .NET URL-safe base64 without padding to match Python `secrets.token_urlsafe(32)` shape closely; exact random output length is shape-tested, not deterministic.
- Keep controller/admin API wiring out of this slice unless the persistence surface is already stable.

## Completed Migration Unit

### Unit 9: SQLite Persistence, Slice 1

Status: completed.

Goal: begin migrating `db.py` in small slices, starting with schema initialization and channel persistence that can be tested without admin/proxy controller wiring.

Source reference:

- `opencodex_proxy/db.py`
- `tests/test_db.py`

Implemented files:

- `src/OpenCodex.Api/OpenCodex.Api.csproj`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs`

Behavior preserved:

- SQLite schema initialization for `request_logs`, `request_log_details`, `channels`, `users`, `access_api_keys`, `web_search_settings`, and `tavily_keys`.
- Request-log and channel indexes needed by the migrated schema slice.
- Idempotent `Initialize`.
- Channel read/replace ordering and owner scoping.
- Channel defaults for `name`, `apikey`, `auth_mode`, `headers`, `timeout_seconds`, `retry_count`, `compat`, `models`, and `enabled`.
- Channel JSON field parsing with safe fallbacks.
- Legacy channel migration for `models_json`, `retry_count`, invalid `auth_mode`, `owner_username`, and primary key `(owner_username, id)`.

Known gaps:

- Full request log migration/detail extraction is not migrated yet.
- User/password/access-key behavior is not migrated yet.
- Web Search configuration and Tavily key reservation are not migrated yet.
- Async DB writer, log filters, stats, and cost extraction are not migrated yet.
- No controller/admin API wiring was added in this slice.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 85 tests.

## Completed Migration Unit

### Unit 8: Protocol Conversion, Slice 2

Status: completed.

Goal: continue migrating `protocols.py` by covering the high-value tool/history compatibility paths that were intentionally left out of Slice 1.

Source reference:

- `opencodex_proxy/protocols.py`
- `tests/test_protocols.py`

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`

Behavior preserved:

- Responses native tools to Chat function wrappers, especially `web_search`, `local_shell`, and `apply_patch`.
- Responses tool call/output history replay into Chat assistant/tool messages.
- Reasoning item folding into adjacent assistant tool-call messages.
- Orphan tool-output removal and placeholder outputs for missing tool results.
- Namespace tool flattening and reconstruction.
- Basic apply_patch raw input wrapping for Chat tool calls.

Known gaps:

- Full apply_patch proxy response reconstruction is not migrated yet.
- Streaming/SSE protocol conversion is not migrated yet.
- Plan-mode system instruction injection remains to be migrated in a later protocol compatibility slice.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 80 tests.

## Completed Migration Unit

### Unit 7: Protocol Conversion, Slice 1

Status: completed.

Goal: begin migrating `protocols.py` in small slices. This unit covered the lowest-risk synchronous JSON request/response conversions before apply_patch compatibility and streaming.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`

Behavior preserved:

- `ConvertRequest` deep-copies input, rewrites `model` to the upstream model, and returns directly for same-protocol requests.
- `ConvertResponse` deep-copies same-protocol payloads and rewrites `model` back to the original model when provided.
- Responses input string and `input_text` blocks convert to Chat user messages.
- Responses `instructions` convert to a Chat system message.
- Responses function tools convert to Chat function tools.
- Responses `max_output_tokens` converts to Chat `max_tokens`.
- Chat response text, function tool calls, reasoning content, length status, annotations, and usage convert to Responses response shape.
- Messages text response and usage convert to Chat response shape.

Known gaps:

- Native Responses tools beyond simple function tools are not fully migrated yet.
- Namespace tools and advanced tool-name flattening are not fully migrated yet.
- `apply_patch` proxy-tool conversion is not migrated yet.
- Reasoning/tool-call history repair is not migrated yet.
- Streaming/SSE protocol conversion is not migrated yet.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 74 tests.

## Completed Migration Unit

### Unit 6: Config Validation and Channel Routing

Status: completed.

Goal: migrate the pure logic from Python `config.py` and `routing.py` before moving database-backed config storage or proxy endpoints.

Implemented files:

- `src/OpenCodex.Api/Config/ConfigException.cs`
- `src/OpenCodex.Api/Config/OpenCodexConfig.cs`
- `src/OpenCodex.Api/Config/ConfigValue.cs`
- `src/OpenCodex.Api/Config/ConfigEnvironmentExpander.cs`
- `src/OpenCodex.Api/Config/ConfigNormalizer.cs`
- `src/OpenCodex.Api/Config/ConfigValidator.cs`
- `src/OpenCodex.Api/Routing/RouteResult.cs`
- `src/OpenCodex.Api/Routing/ChannelRouter.cs`
- `tests/OpenCodex.Api.Tests/Config/ConfigTests.cs`

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 66 tests.

## Completed Migration Unit

### Unit 5: Error Model and API Error Responses

Status: completed.

Goal: migrate the Python `errors.py` behavior and the Flask error-handler response shape into .NET before moving config, routing, or proxy endpoints.

Implemented files:

- `src/OpenCodex.Api/Errors/ProxyException.cs`
- `src/OpenCodex.Api/Errors/BadRequestException.cs`
- `src/OpenCodex.Api/Errors/RoutingException.cs`
- `src/OpenCodex.Api/Errors/UpstreamException.cs`
- `src/OpenCodex.Api/Errors/ProxyErrorMiddleware.cs`
- `tests/OpenCodex.Api.Tests/Errors/ProxyErrorTests.cs`

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 24 tests.

## Completed Migration Unit

### Unit 4: Settings Module

Status: completed.

Goal: migrate the Python `settings.py` behavior into .NET before touching proxy routing or database code.

Source reference:

- `opencodex_proxy/settings.py`
- Existing environment variable examples in `.env.example`, `README.md`, and `DEPLOYMENT.md`

Expected .NET files:

- `src/OpenCodex.Api/Configuration/OpenCodexSettings.cs`
- `src/OpenCodex.Api/Configuration/OpenCodexSettingsLoader.cs`
- `tests/OpenCodex.Api.Tests/Configuration/OpenCodexSettingsTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Behavior to preserve:

- Environment variable names and default values.
- Required `OPENCODEX_ADMIN_PASSWORD` validation.
- Positive integer parsing for port, default timeout, and related numeric settings.
- Log view level and logging-related defaults.

Risks:

- Settings are used by app startup, DB paths, logging, and admin bootstrap; keep this unit pure and testable before wiring it into runtime startup.
- Do not remove Python settings yet. This migration remains additive until parity is proven.

Verification command:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln
```

Result: passed with 18 tests.

## Recommended Migration Order

1. Protocol conversion: migrate in smaller slices, starting with basic request/response conversion before apply_patch compatibility.
2. SQLite and auth: schema initialization, migrations, users, access API keys, channel ownership, and log visibility.
3. Proxy entrypoints and upstream calls: `/v1/responses`, `/v1/chat/completions`, `/v1/messages`, retries, and model discovery.
4. SSE and streaming conversion.
5. Web Search simulation and Tavily key handling.
6. Admin API, static admin frontend hosting, Dockerfile, deployment scripts, and final parity verification.

## Verification Commands

Use the user-local .NET 10 SDK:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln
```

## Notes

- The default shell `dotnet` still resolves to system SDK 9.0.305 unless `/Users/w/.dotnet` is placed first in `PATH`.
- This file is temporary session memory. Decide later whether to keep it tracked, move it under docs, or add it to `.gitignore`.

## Current Project Location

Status: confirmed.

Decision:

- The .NET solution, source code, tests, checklist, and temporary migration memory are kept under `opencodex_proxy/`.
- Future .NET migration work should run from `/Users/w/shL/work/shl/OpenCodex/opencodex_proxy`.
- Avoid creating new .NET project files at the repository root unless the user explicitly changes this decision.

Current verification:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Services/WebSearch*.cs tests/OpenCodex.Api.Tests/Services/WebSearch*.cs
```

Result:

- Full .NET test suite passed: 287 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in the touched Web Search and migration memory files.

Recent Web Search cleanup already verified:

- Split request policy, tool-call parsing, payload helpers, response payload shaping, continuation request construction, simulation logging, and result DTOs out of `WebSearchSimulator`.
- `WebSearchSimulator` is now closer to orchestration-only, but still contains stream orchestration and SSE state handling that can be normalized later.

## Completed Code Normalization Unit

### Web Search Stream Event State

Status: completed.

Goal:

- Continue reducing `WebSearchSimulator` complexity without changing OpenAI-compatible streaming response shapes.
- Isolate mechanical SSE stream state handling from the Web Search orchestration flow.

Implemented files:

- `src/OpenCodex.Api/Services/WebSearchStreamEventState.cs`
- `src/OpenCodex.Api/Services/WebSearchSimulator.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearchSimulatorTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `WebSearchStreamEventState` to own:
  - next `sequence_number` calculation from already converted SSE events
  - next `output_index` calculation from already converted SSE payloads
  - `response.output_item.added` generation for injected `web_search_call` items
  - `response.output_item.done` generation for injected `web_search_call` items
- Removed the stream-state helper methods from `WebSearchSimulator`.
- Added a focused stream test that runs through `RunChatStreamAsync` and verifies:
  - sequence numbers continue monotonically after injected Web Search events
  - injected Web Search output index starts at the next available index
  - the final assistant message starts after the injected Web Search item

Bug found during the unit:

- Compile error: `WebSearchStreamEventState` initially had a `NextOutputIndex` property and private static method with the same name.
- Minimal reproduction: targeted `dotnet test` compile step failed with CS0102.
- Fix: renamed the private method to `CalculateNextOutputIndex`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Services/WebSearch*.cs tests/OpenCodex.Api.Tests/Services/WebSearch*.cs
```

Result:

- Targeted Web Search/proxy tests passed: 19 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 288 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched Web Search and migration memory files.

Remaining risks:

- `WebSearchSimulator` still owns non-streaming iteration control and streaming orchestration. It is smaller, but not fully reduced to orchestration-only.
- `SseStreamConverter`, `ProtocolConverter`, and `OpenCodexDatabase` remain the largest normalization targets.

## Completed Code Normalization Unit

### Protocol Converter Tool Conversion Partial

Status: completed.

Goal:

- Reduce `ProtocolConverter` file size and responsibility concentration without changing public conversion behavior.
- Isolate pure tool declaration conversion logic from the main request/response conversion flow.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.Tools.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Converted `ProtocolConverter` to a `partial` static class.
- Moved tool declaration helpers into `ProtocolConverter.Tools.cs`, including:
  - Responses, Chat, and Messages/Anthropic tool declarations to canonical tool format
  - canonical tools back to Responses, Chat, and Messages/Anthropic declarations
  - namespace tool flattening and reconstruction
  - native Responses tool wrapping
  - `web_search` schema wrapping
  - `apply_patch` proxy tool schema expansion
  - tool choice normalization for Chat
  - apply_patch argument normalization
- Left `ConvertRequest` and `ConvertResponse` public entrypoints unchanged.
- Added `ResponsesToolsConvertToMessagesInputSchemas` to cover the Messages/Anthropic tool declaration outlet, including function tools, `web_search`, and expanded `apply_patch` proxy tools.

Bug found during the unit:

- Compile error: `ToolChoiceToChat` was duplicated after the first partial split.
- Minimal reproduction: targeted `ProtocolConverterTests` compile step failed with CS0111.
- Fix: removed the stale copy from `ProtocolConverter.cs` and kept the tool-related implementation in `ProtocolConverter.Tools.cs`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Protocols/ProtocolConverter*.cs tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs
```

Result:

- Protocol converter tests passed: 23 passed, 0 failed, 0 skipped.
- Protocol/proxy targeted tests passed: 37 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 289 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched protocol and migration memory files.

Remaining risks:

- `ProtocolConverter.cs` remains large at about 1741 lines after moving about 626 lines of tool conversion logic into `ProtocolConverter.Tools.cs`.
- Remaining high-value split points are content block conversion, tool history normalization, usage/token mapping, and response shaping.
- `SseStreamConverter` and `OpenCodexDatabase` remain major normalization targets.

## Completed Code Normalization Unit

### Protocol Converter Content Conversion Partial

Status: completed.

Goal:

- Continue reducing `ProtocolConverter` size and responsibility concentration without changing public conversion behavior.
- Isolate content block conversion and content stringification helpers from the main request/response conversion flow.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.Content.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.Content.cs` as another `partial` segment.
- Moved content conversion helpers into the content partial, including:
  - Responses content blocks to Chat content
  - Chat content blocks to Responses content blocks
  - Messages/Anthropic content blocks to Chat content
  - Chat content to Messages/Anthropic content blocks
  - empty chat content detection
  - `StringifyContent`
- Kept `ConvertRequest` and `ConvertResponse` public entrypoints unchanged.
- Added content conversion boundary tests:
  - `ChatMixedContentBlocksConvertToResponsesInputBlocks`
  - `MessagesToolResultContentConvertsToChatTextBlock`

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Protocols/ProtocolConverter*.cs tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs
```

Result:

- Protocol converter tests passed: 25 passed, 0 failed, 0 skipped.
- Protocol/proxy targeted tests passed: 39 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 291 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched protocol and migration memory files.

Remaining risks:

- `ProtocolConverter.cs` remains large at about 1504 lines after moving content and tool conversion helpers into partial files.
- Remaining high-value split points are tool history normalization, usage/token mapping, response shaping, and low-level JSON/object helpers.
- `SseStreamConverter` and `OpenCodexDatabase` remain major normalization targets.

## Completed Code Normalization Unit

### Protocol Converter Usage Mapping Partial

Status: completed.

Goal:

- Continue reducing `ProtocolConverter` size and responsibility concentration without changing public conversion behavior.
- Isolate token usage normalization and protocol-specific usage output mapping from the main request/response conversion flow.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.Usage.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.Usage.cs` as another `partial` segment.
- Moved usage/token helpers into the usage partial, including:
  - Responses usage to canonical usage
  - Chat usage to canonical usage
  - Messages/Anthropic usage to canonical usage
  - canonical usage to Responses usage
  - canonical usage to Chat usage
  - canonical usage to Messages/Anthropic usage
- Kept `ConvertRequest` and `ConvertResponse` public entrypoints unchanged.
- Added `ChatResponseUsageWithoutTotalTokensFillsResponsesTotalTokens` to cover `total_tokens` fallback from `prompt_tokens + completion_tokens`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Protocols/ProtocolConverter*.cs tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs
```

Result:

- Protocol converter tests passed: 26 passed, 0 failed, 0 skipped.
- Protocol/proxy targeted tests passed: 40 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 292 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched protocol and migration memory files.

Remaining risks:

- `ProtocolConverter.cs` remains large at about 1449 lines after moving usage, content, and tool conversion helpers into partial files.
- Remaining high-value split points are tool history normalization, response shaping, annotations/reasoning helpers, and low-level JSON/object helpers.
- `SseStreamConverter` and `OpenCodexDatabase` remain major normalization targets.

## Completed Code Normalization Unit

### Protocol Converter Reasoning and Annotation Partial

Status: completed.

Goal:

- Continue reducing `ProtocolConverter` size and responsibility concentration without changing public conversion behavior.
- Isolate reasoning, annotation, and Responses metadata text helpers from the main request/response conversion flow.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.Reasoning.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.Reasoning.cs` as another `partial` segment.
- Moved reasoning/annotation helpers into the reasoning partial, including:
  - reasoning content append/merge helper
  - Responses metadata item text rendering
  - Responses reasoning text extraction
  - Responses reasoning item construction
  - annotation normalization
- Kept `ConvertRequest` and `ConvertResponse` public entrypoints unchanged.
- Added `ResponsesReasoningTextPrefersEncryptedContentThenSummaryThenContent` to cover reasoning text priority through the public request conversion path.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Protocols/ProtocolConverter*.cs tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs
```

Result:

- Protocol converter tests passed: 27 passed, 0 failed, 0 skipped.
- Protocol/proxy targeted tests passed: 41 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 293 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched protocol and migration memory files.

Remaining risks:

- `ProtocolConverter.cs` remains large at about 1354 lines after moving reasoning, usage, content, and tool conversion helpers into partial files.
- Remaining high-value split points are tool history normalization, response shaping, and low-level JSON/object helpers.
- `SseStreamConverter` and `OpenCodexDatabase` remain major normalization targets.

## Completed Code Normalization Unit

### Protocol Converter Tool History Partial

Status: completed.

Goal:

- Continue reducing `ProtocolConverter` size and responsibility concentration without changing public conversion behavior.
- Isolate Responses-to-Chat tool history normalization from the main request/response conversion flow.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.ToolHistory.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.ToolHistory.cs` as another `partial` segment.
- Moved tool history normalization helpers into the tool-history partial, including:
  - `NormalizeChatToolHistory`
  - reasoning fold into adjacent tool-call messages
  - consecutive assistant tool-call message merging
  - orphan tool output removal
  - missing tool output placeholder insertion
  - assistant/tool-call shape predicates
- Kept `ConvertRequest` and `ConvertResponse` public entrypoints unchanged.
- Added `ResponsesToolHistoryMergesToolCallsAcrossReasoning` to cover the case where a Responses reasoning item appears between tool calls that should still merge into one Chat assistant tool-call message.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Protocols/ProtocolConverter*.cs tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs
```

Result:

- Protocol converter tests passed: 28 passed, 0 failed, 0 skipped.
- Protocol/proxy targeted tests passed: 42 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 294 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched protocol and migration memory files.

Remaining risks:

- `ProtocolConverter.cs` remains large at about 1137 lines after moving tool history, reasoning, usage, content, and tool conversion helpers into partial files.
- Remaining high-value split points are response shaping and low-level JSON/object helpers.
- `SseStreamConverter` and `OpenCodexDatabase` remain major normalization targets.

## Completed Code Normalization Unit

### Protocol Converter Values Partial

Status: completed.

Goal:

- Continue reducing `ProtocolConverter` size and responsibility concentration without changing public conversion behavior.
- Isolate low-level JSON/object/list/value coercion helpers from the main request/response conversion flow.
- Preserve request conversion behavior for runtime JSON payloads that arrive as `JsonElement` values.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.Values.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.Values.cs` as another `partial` segment.
- Moved low-level value helpers into the values partial, including:
  - JSON serialization/parsing helpers
  - dictionary and list coercion helpers
  - deep-copy and JSON normalization helpers
  - `JsonElement` and `JsonDocument` conversion helpers
  - truthiness, integer coercion, timestamp, and ID helpers
- Kept `ConvertRequest` and `ConvertResponse` public entrypoints unchanged.
- Added `ResponsesRequestNormalizesJsonElementValues` to cover runtime `JsonElement` payloads through the public request conversion path.
- Fixed a reproduced boundary where `JsonElement` integer numbers in `int` range were value-equal but boxed as `long`; they now normalize to `int` when safe, matching regular dictionary payload behavior.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Protocols/ProtocolConverter*.cs tests/OpenCodex.Api.Tests/Protocols/ProtocolConverterTests.cs
```

Result:

- Protocol converter tests passed: 29 passed, 0 failed, 0 skipped.
- Protocol/proxy targeted tests passed: 43 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched protocol and migration memory files.

Current size snapshot:

- `ProtocolConverter.cs`: 873 lines.
- `ProtocolConverter.Values.cs`: 273 lines.
- `SseStreamConverter.cs`: 974 lines.
- `OpenCodexDatabase.cs`: 3383 lines.

Remaining risks:

- `ProtocolConverter.cs` is much smaller than before, but still large enough that response shaping could be split in a later unit.
- `SseStreamConverter` and `OpenCodexDatabase` remain the largest normalization targets.
- `OpenCodexDatabase` is the next highest-risk area because it concentrates persistence, schema, seed/config, and query/write behavior in one file.

## Completed Code Normalization Unit

### OpenCodex Database Records Split

Status: completed.

Goal:

- Start reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Move public persistence record DTOs out of the implementation-heavy database file.
- Keep record names, namespace, constructor shapes, and public visibility unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Records.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Records.cs`.
- Moved the public persistence record types into the new records file:
  - `ChannelRecord`
  - `UserRecord`
  - `AccessApiKeyRecord`
  - `AccessApiKeyUserRecord`
  - `AuthenticatedAccessApiKeyRecord`
  - `TavilyKeyRecord`
  - `WebSearchConfigRecord`
  - `UsageRecord`
  - `RequestLogRecord`
  - `RequestLogEventRecord`
  - `RequestLogPageRecord`
  - `StatsPointRecord`
  - `StatsSummaryRecord`
  - `ModelDistributionRecord`
  - `StatsRecord`
- Left SQL schema, migrations, queries, readers, and business behavior untouched.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 3213 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` is still the largest implementation file and still mixes schema, migrations, users, API keys, logs, stats, web search, channels, and low-level helpers.
- Next low-risk split candidate is schema/migration constants and initialization helpers, but that touches SQL definition placement and should be handled as its own focused unit.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Schema Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Move database schema SQL constants out of the main implementation file.
- Keep initialization, migrations, queries, and SQL text behavior unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Schema.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Schema.cs`.
- Changed `OpenCodexDatabase` to a `partial` static class.
- Moved schema-only constants and schema composition into the schema partial:
  - `RequestLogsSchema`
  - `RequestLogsIndexesSchema`
  - `RequestLogDetailsSchema`
  - `ChannelsSchema`
  - `UsersSchema`
  - `WebSearchSchema`
  - `Schema`
- Left `Initialize`, migration helpers, SQL query methods, readers, and business behavior untouched.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 3083 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains the largest implementation file and still mixes migrations, users, API keys, logs, stats, web search, channels, and low-level helpers.
- Next low-risk split candidate is migration-specific helpers, but that should be handled as its own focused unit because it is close to schema evolution behavior.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Migrations Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate initialization-time schema migration helpers from normal read/write persistence logic.
- Keep migration SQL, migration ordering, and `Initialize` behavior unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Migrations.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Migrations.cs`.
- Moved migration-only private helpers into the migrations partial:
  - `MigrateRequestLogs`
  - `MigrateChannels`
  - `MigrateWebSearch`
  - `RebuildChannelsWithOwnerPrimaryKey`
  - `AddColumnIfMissing`
  - `ColumnNames`
  - `ChannelPrimaryKey`
- Kept `Initialize` in the main database file so the startup flow remains easy to find.
- Kept normal read/write helper methods in the main file when they are used outside initialization, including:
  - `ReadExistingChannelCreatedTimes`
  - `ReadCurrentWebSearchDefaultUsageLimit`
  - `ReadExistingTavilyKeys`
- Left SQL text, migration order, queries, readers, and business behavior untouched.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 2906 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains the largest implementation file and still mixes users, API keys, logs, stats, web search, channels, readers, and low-level helpers.
- Next low-risk split candidate is reader/mapper helpers, because that moves object materialization without changing SQL or persistence behavior.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Readers Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate `SqliteDataReader` to persistence record mapping helpers from query and write logic.
- Keep SQL, public API, record shapes, and business behavior unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Readers.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Readers.cs`.
- Moved reader/mapper-only private helpers into the readers partial:
  - `ReadChannel`
  - `ReadUser`
  - `ReadAccessApiKey`
  - `ReadTavilyKey`
  - `ReadRequestLog`
  - `ReadRequestLogEvent`
- Kept query construction, SQL execution, pagination/filter helpers, and nullable reader primitives in the main database file.
- Left SQL text, public methods, repository behavior, and record shapes untouched.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 2780 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains a large implementation file and still mixes users, API keys, logs, stats, web search, channels, query helpers, value helpers, and cryptographic/key helpers.
- Next useful split candidates are log query/stat helpers or low-level value/reader primitives, each as its own focused unit.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Values Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate low-level JSON, optional value, conversion, nullable reader, and SQLite parameter helper methods.
- Keep security-sensitive password/API-key functions in the main database file for this unit.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Values.cs`.
- Moved low-level helper members into the values partial, including:
  - `JsonOptions`
  - timestamp parsing and formatting helpers
  - owner/nullable SQLite parameter helpers
  - nullable/int/double reader helpers
  - optional dictionary value helpers
  - token usage value helpers
  - request status helper
  - numeric conversion helpers
  - dictionary/list coercion helpers
  - required/default string and timeout/retry helpers
  - web search provider/key normalization helpers
  - required integer and positive ID parsers
  - falsy/username normalization helpers
  - JSON parse/serialize/normalization helpers
  - `UnixTimeSeconds`
  - `ExecuteNonQuery`
- Kept `HashPassword`, `VerifyPassword`, `GenerateAccessApiKey`, `HashAccessApiKey`, and `ToLowerHex` in the main database file.
- Left SQL text, public methods, repositories, record shapes, and business behavior untouched.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 2143 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains a large implementation file and still mixes users, API keys, logs, stats, web search, channels, query helpers, and cryptographic/key helpers.
- Next useful split candidates are log query/stat helpers or security/key helpers, each as its own focused unit.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Security Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate password hashing and access API key security helpers from database workflow methods.
- Keep password hash format, access key prefix, hash algorithms, public methods, and call sites unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Security.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Security.cs`.
- Moved security-related constants and methods into the security partial:
  - `AccessKeyPrefix`
  - `PasswordHashIterations`
  - `HashPassword`
  - `VerifyPassword`
  - `GenerateAccessApiKey`
  - `HashAccessApiKey`
  - `ToLowerHex`
- Removed security-only `using` directives from the main database implementation file.
- Left user creation, login, access key persistence, authentication queries, SQL text, record shapes, and public method behavior untouched.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 2081 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains a large implementation file and still mixes users, API keys, logs, stats, web search, channels, and query helpers.
- Next useful split candidates are log query/stat helpers, each as its own focused unit.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Pricing Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate model pricing constants and cost calculation from database workflow methods.
- Keep public `CalculateCost` behavior, model matching, tier selection, and pricing values unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Pricing.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Pricing.cs`.
- Moved pricing-only private records and table into the pricing partial:
  - `PricingTier`
  - `PricingEntry`
  - `PricingEntries`
- Moved public `CalculateCost` into the pricing partial while keeping the method signature unchanged.
- Left usage extraction, request log writing, SQL text, stats, repositories, record shapes, and business behavior untouched.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 1995 lines.
- `OpenCodexDatabase.Pricing.cs`: 90 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains large and still mixes users, API keys, logs, stats, web search, channels, and query helpers.
- Next useful split candidates are log query/stat helpers or user/access-key workflow helpers, each as its own focused unit.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Current Project Location Constraint

Status: active.

Requirement:

- The .NET migration project must live under `opencodex_proxy/`.
- Continue using `/Users/w/shL/work/shl/OpenCodex/opencodex_proxy` as the .NET project root.
- Keep `OpenCodex.sln`, `global.json`, `src/`, `tests/`, and migration memory files inside `opencodex_proxy/`.
- Do not place .NET project files at the repository root unless the user explicitly changes this requirement.

Current verification:

- `OpenCodex.sln` is under `opencodex_proxy/`.
- `global.json` is under `opencodex_proxy/`.
- `src/OpenCodex.Api/OpenCodex.Api.csproj` is under `opencodex_proxy/`.
- `tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj` is under `opencodex_proxy/`.

## Completed Code Normalization Unit

### OpenCodex Database Observability Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate log filter, log query, and stats helper code from the main database workflow file.
- Keep public observability entry points and `/admin/api/...` response behavior unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Observability.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Observability.cs`.
- Moved log/stat private constants and helper records into the observability partial:
  - `RequestLogMetadataColumns`
  - `RequestLogDetailColumns`
  - `TextLogFilterFields`
  - `IntegerLogFilterFields`
  - `RequestStatusValues`
  - `LogFilterFields`
  - `StatsRangeGranularity`
  - `StatsRangeHours`
  - `SqlQueryParameter`
  - `LogWhereClause`
  - `ResolvedStatsRange`
- Moved log/stat helper methods into the observability partial:
  - `LogMetadataSelectColumns`
  - `LogDetailSelectColumns`
  - `BuildLogWhereClause`
  - `EmptyLogFilterOptions`
  - `DistinctTextValues`
  - `DistinctIntValues`
  - `AppendWhereCondition`
  - `AddParameters`
  - `IsEmptyLogFilterValue`
  - `ParseLogPage`
  - `ParseLogPageSize`
  - `EmptyStatsResponse`
  - `EmptyStatsSummary`
  - `ReadStatsSummary`
  - `CreateStatsSummaryCommand`
  - `ReadModelDistribution`
  - `ResolveStatsRange`
  - `StatsGranularityForSeconds`
- Left public observability entry points in `OpenCodexDatabase.cs`:
  - `ReadLogs`
  - `ReadLogsPage`
  - `ReadLogById`
  - `ReadLogFilterOptions`
  - `ReadLogFilterOption`
  - `ReadStats`
- Left SQL behavior, result record shapes, owner scoping, pagination defaults, stats range handling, and compatibility payload shape unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 1485 lines.
- `OpenCodexDatabase.Observability.cs`: 517 lines.
- `OpenCodexDatabase.Pricing.cs`: 90 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains large and still mixes users, API keys, request log write flow, web search, channels, and proxy route helpers.
- Next useful split candidates are user workflow, access API key workflow, web search workflow, or channel workflow partials.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Users Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate user management workflow methods from the main database implementation file.
- Keep user validation, SQL text, transaction behavior, exception messages, protected-user checks, and public method signatures unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Users.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Users.cs`.
- Moved user role constants and public user workflow methods into the users partial:
  - `UserRoles`
  - `EnsureSuperadmin`
  - `CreateUser`
  - `ListUsers`
  - `GetUser`
  - `AuthenticateUser`
  - `SetUserEnabled`
  - `ResetUserPassword`
  - `DeleteUser`
- Left password hashing, shared readers, schema, migrations, access API key workflows, request logs, web search, channels, and public behavior unchanged.
- Kept `DeleteUser` transaction flow and associated `access_api_keys` / `channels` cleanup intact.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 1150 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.Observability.cs`: 517 lines.
- `OpenCodexDatabase.Pricing.cs`: 90 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains large and still mixes access API key workflows, request log write flow, web search, channels, and proxy route helpers.
- Next useful split candidates are access API key workflow, request log write workflow, web search workflow, or channel workflow partials.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Access API Keys Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate access API key workflow methods from the main database implementation file.
- Keep key generation, hashing, owner scoping, SQL text, transaction behavior, exception messages, and public method signatures unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.AccessApiKeys.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.AccessApiKeys.cs`.
- Moved public access API key workflow methods into the access API keys partial:
  - `CreateAccessApiKey`
  - `ListAccessApiKeys`
  - `GetAccessApiKey`
  - `SetAccessApiKeyEnabled`
  - `DeleteAccessApiKey`
  - `AuthenticateAccessApiKey`
- Left user lookup, password/key hashing, shared readers, schema, migrations, request logs, web search, channels, and public behavior unchanged.
- Kept `AuthenticateAccessApiKey` transaction flow and `last_used_at` update behavior intact.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 888 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.Observability.cs`: 517 lines.
- `OpenCodexDatabase.Pricing.cs`: 90 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains large and still mixes request log write flow, public log/stat read entry points, web search, channels, and shared connection helpers.
- Next useful split candidates are web search workflow, channel workflow, request log write workflow, or public observability entry points.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Web Search Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate web search settings, Tavily key configuration, and key reservation workflow from the main database implementation file.
- Keep provider validation, default usage limit behavior, SQL text, transaction behavior, usage-count updates, exception messages, and public method signatures unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.WebSearch.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.WebSearch.cs`.
- Moved web search constants and provider set into the web search partial:
  - `DefaultWebSearchKeyUsageLimit`
  - `WebSearchProviders`
- Moved public web search workflow methods into the web search partial:
  - `ReadWebSearchConfig`
  - `ReplaceWebSearchConfig`
  - `ReserveTavilyKey`
  - `ReserveTavilyKeyById`
- Moved Tavily helper record and methods into the web search partial:
  - `ExistingTavilyKey`
  - `ReserveTavilyKey`
  - `ReadCurrentWebSearchDefaultUsageLimit`
  - `ReadExistingTavilyKeys`
- Left shared readers, value parsing helpers, schema, migrations, request logs, channels, and public behavior unchanged.
- Kept `ReplaceWebSearchConfig` transaction flow and `ReserveTavilyKey` usage-count update behavior intact.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 573 lines.
- `OpenCodexDatabase.WebSearch.cs`: 322 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.Observability.cs`: 517 lines.
- `OpenCodexDatabase.Pricing.cs`: 90 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` remains medium-sized and still mixes request log write flow, public log/stat read entry points, channel workflow, and shared connection helpers.
- Next useful split candidates are channel workflow, request log write workflow, public observability entry points, or moving connection helpers into infrastructure partial.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Channels Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate channel read/replace workflow from the main database implementation file.
- Keep owner scoping, default timeout/retry behavior, SQL text, transaction behavior, created-at preservation, and public method signatures unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Channels.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Channels.cs`.
- Moved channel workflow methods into the channels partial:
  - `ReadChannels`
  - `ReplaceChannels`
- Moved channel-specific helper into the channels partial:
  - `ReadExistingChannelCreatedTimes`
- Moved `DefaultRetryCount` into the channels partial because it is consumed by channel retry parsing.
- Left shared value parsing, readers, schema, migrations, request logs, stats, web search, and public behavior unchanged.
- Kept `ReplaceChannels` transaction flow and existing channel `created_at` preservation intact.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 419 lines.
- `OpenCodexDatabase.Channels.cs`: 160 lines.
- `OpenCodexDatabase.WebSearch.cs`: 322 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.Observability.cs`: 517 lines.
- `OpenCodexDatabase.Pricing.cs`: 90 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` still contains initialization, request usage extraction, request log writing, public log/stat read entry points, and shared connection helpers.
- Next useful split candidates are request log write workflow, public observability entry points, or moving connection helpers into infrastructure partial.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Request Logs Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Isolate request usage extraction and request log write workflow from the main database implementation file.
- Keep usage parsing, request log SQL, default owner fallback, transaction behavior, nullable field handling, and public method signatures unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.RequestLogs.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.RequestLogs.cs`.
- Moved request usage/log writing methods into the request logs partial:
  - `ExtractUsage`
  - `WriteRequestLog`
- Left shared nullable/value helpers in `OpenCodexDatabase.Values.cs` because they are used by multiple persistence workflows.
- Left public log/stat read entry points, schema, migrations, readers, observability helpers, and public behavior unchanged.
- Kept `WriteRequestLog` metadata/detail insert transaction and rollback behavior intact.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 305 lines.
- `OpenCodexDatabase.RequestLogs.cs`: 118 lines.
- `OpenCodexDatabase.Channels.cs`: 160 lines.
- `OpenCodexDatabase.WebSearch.cs`: 322 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.Observability.cs`: 517 lines.
- `OpenCodexDatabase.Pricing.cs`: 90 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.cs` still contains initialization, public log/stat read entry points, and shared connection helpers.
- Next useful split candidates are public observability entry points or moving connection helpers into infrastructure partial.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.

## Completed Code Normalization Unit

### OpenCodex Database Observability Public Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.cs` size without changing persistence behavior.
- Move public log/stat read entry points into an observability-focused partial.
- Keep log filtering, pagination, detail loading, owner scoping, stats buckets, response records, and public method signatures unchanged.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Observability.Public.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Observability.Public.cs`.
- Moved public observability entry points out of `OpenCodexDatabase.cs`:
  - `ReadLogs`
  - `ReadLogsPage`
  - `ReadLogById`
  - `ReadLogFilterOptions`
  - `ReadLogFilterOption`
  - `ReadStats`
- Left private observability helpers in `OpenCodexDatabase.Observability.cs`.
- Left request log writes, schema, migrations, readers, value helpers, repositories, services, and public behavior unchanged.
- Reduced `OpenCodexDatabase.cs` to initialization and shared connection helper responsibilities.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Persistence/OpenCodexDatabase*.cs tests/OpenCodex.Api.Tests/Persistence/OpenCodexDatabaseTests.cs
```

Result:

- Persistence database tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched persistence and migration memory files.

Current size snapshot:

- `OpenCodexDatabase.cs`: 40 lines.
- `OpenCodexDatabase.Observability.Public.cs`: 270 lines.
- `OpenCodexDatabase.Observability.cs`: 517 lines.
- `OpenCodexDatabase.RequestLogs.cs`: 118 lines.
- `OpenCodexDatabase.Channels.cs`: 160 lines.
- `OpenCodexDatabase.WebSearch.cs`: 322 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.Pricing.cs`: 90 lines.
- `OpenCodexDatabase.Security.cs`: 69 lines.
- `OpenCodexDatabase.Values.cs`: 643 lines.
- `OpenCodexDatabase.Readers.cs`: 132 lines.
- `OpenCodexDatabase.Migrations.cs`: 183 lines.
- `OpenCodexDatabase.Schema.cs`: 134 lines.
- `OpenCodexDatabase.Records.cs`: 171 lines.
- `OpenCodexDatabaseTests.cs`: 1367 lines.

Remaining risks:

- `OpenCodexDatabase.Values.cs` and `OpenCodexDatabase.Observability.cs` remain large helper partials, but the root database file is now thin.
- `SseStreamConverter.cs` remains another large normalization target at about 974 lines.
- Broader checklist compliance still needs continued review outside persistence, especially large protocol/streaming components and API documentation/validation coverage.

## Completed Code Normalization Unit

### SseStreamConverter Parsing Partial

Status: completed.

Goal:

- Continue reducing `SseStreamConverter.cs` size without changing streaming conversion behavior.
- Isolate SSE parsing, TTFT line classification, JSON conversion, and low-level dictionary/list helpers from the main stream conversion implementation.
- Keep Chat Completions and Messages stream conversion logic untouched.

Implemented files:

- `src/OpenCodex.Api/Protocols/SseStreamConverter.Parsing.cs`
- `src/OpenCodex.Api/Protocols/SseStreamConverter.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `SseStreamConverter.Parsing.cs`.
- Changed `SseStreamConverter` to a `partial` class.
- Moved parsing/helper methods into the parsing partial:
  - `ParseEvents`
  - `CountsForTtft`
  - `ParseData`
  - `ChatUsageToResponsesUsage`
  - `MessagesUsageToResponsesUsage`
  - `ParseJsonObject`
  - `FromJsonElement`
  - `TryAsObject`
  - `TryAsList`
  - `GetValue`
  - `StringValue`
  - `ToInt`
- Left `ChatToResponsesEvents`, `MessagesToResponsesEvents`, stream event ordering, upstream response reconstruction, and public behavior unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyStreamResponseWriterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Protocols/SseStreamConverter*.cs tests/OpenCodex.Api.Tests/ProxyControllerTests.cs tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/WebSearchSimulatorTests.cs tests/OpenCodex.Api.Tests/Services/ProxyStreamResponseWriterTests.cs
```

Result:

- Stream-related targeted tests passed: 23 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched SSE converter and migration memory files.

Current size snapshot:

- `SseStreamConverter.cs`: 793 lines.
- `SseStreamConverter.Parsing.cs`: 188 lines.
- `ProxyControllerTests.cs`: 954 lines.
- `ProxyStreamServiceTests.cs`: 193 lines.
- `WebSearchSimulatorTests.cs`: 540 lines.
- `ProxyStreamResponseWriterTests.cs`: 70 lines.

Remaining risks:

- `SseStreamConverter.cs` remains large and still contains both Chat Completions and Messages stream conversion workflows.
- Good next split candidates are a Chat stream partial and a Messages stream partial, provided the conversion-local helper classes remain shared safely.
- Broader checklist compliance still needs continued review outside persistence, especially API documentation examples, validation edge cases, and large service classes.

## Completed Code Normalization Unit

### SseStreamConverter Chat And Messages Partials

Status: completed.

Goal:

- Continue reducing `SseStreamConverter.cs` size without changing streaming conversion behavior.
- Separate Chat Completions stream conversion and Messages stream conversion into focused partial files.
- Keep shared stream records/state, `JsonOptions`, SSE parsing helpers, event payloads, event order, and upstream response reconstruction unchanged.

Implemented files:

- `src/OpenCodex.Api/Protocols/SseStreamConverter.Chat.cs`
- `src/OpenCodex.Api/Protocols/SseStreamConverter.Messages.cs`
- `src/OpenCodex.Api/Protocols/SseStreamConverter.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `SseStreamConverter.Chat.cs`.
- Added `SseStreamConverter.Messages.cs`.
- Moved Chat Completions stream conversion methods into the chat partial:
  - `ChatToResponsesEvents` simple overload
  - `ChatToResponsesEvents` extended overload
- Moved Messages stream conversion method into the messages partial:
  - `MessagesToResponsesEvents`
- Reduced `SseStreamConverter.cs` to shared stream result/event/state types and `JsonOptions`.
- Left `SseStreamConverter.Parsing.cs`, parsing helpers, TTFT classification, stream event ordering, and public behavior unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyStreamResponseWriterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check
rg -n "[ \t]+$" MIGRATION_PROGRESS.tmp.md src/OpenCodex.Api/Protocols/SseStreamConverter*.cs tests/OpenCodex.Api.Tests/ProxyControllerTests.cs tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/WebSearchSimulatorTests.cs tests/OpenCodex.Api.Tests/Services/ProxyStreamResponseWriterTests.cs
```

Result:

- Stream-related targeted tests passed: 23 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed.
- Trailing whitespace scan found no matches in touched SSE converter and migration memory files.

Current size snapshot:

- `SseStreamConverter.cs`: 35 lines.
- `SseStreamConverter.Chat.cs`: 464 lines.
- `SseStreamConverter.Messages.cs`: 305 lines.
- `SseStreamConverter.Parsing.cs`: 188 lines.
- `ProxyControllerTests.cs`: 954 lines.
- `ProxyStreamServiceTests.cs`: 193 lines.
- `WebSearchSimulatorTests.cs`: 540 lines.
- `ProxyStreamResponseWriterTests.cs`: 70 lines.

Remaining risks:

- `SseStreamConverter.Chat.cs` is still medium-large because Chat tool-call streaming has several tightly coupled states.
- `ProtocolConverter.cs`, `ProtocolConverter.Tools.cs`, `OpenCodexDatabase.Values.cs`, `OpenCodexDatabase.Observability.cs`, `HttpUpstreamClient.cs`, and `WebSearchSimulator.cs` remain useful future normalization targets.
- Broader checklist compliance still needs continued review outside file-size normalization, especially API documentation examples, validation edge cases, and service/integration boundaries.

## Completed Code Normalization Unit

### ProtocolConverter Request And Response Partials

Status: completed.

Goal:

- Continue reducing the main protocol conversion file size without changing protocol compatibility behavior.
- Keep public conversion entrypoints, protocol constants, shared JSON settings, and compatibility boundaries in `ProtocolConverter.cs`.
- Separate request canonicalization and response canonicalization workflows into focused partial files.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.Requests.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.Responses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.Requests.cs`.
- Added `ProtocolConverter.Responses.cs`.
- Moved request conversion methods into the request partial:
  - `ToCanonicalRequest`
  - `FromCanonicalRequest`
  - `ResponsesRequestToCanonical`
  - `ChatRequestToCanonical`
  - `MessagesRequestToCanonical`
  - `CanonicalToResponsesRequest`
  - `CanonicalToChatRequest`
  - `CanonicalToMessagesRequest`
  - `ResponsesInputItemToMessages`
  - `MessagesToResponsesInput`
  - `CopyCommonRequestParams`
  - `MergeSystemMessages`
- Moved response conversion methods into the response partial:
  - `ToCanonicalResponse`
  - `FromCanonicalResponse`
  - `ResponsesResponseToCanonical`
  - `ChatResponseToCanonical`
  - `MessagesResponseToCanonical`
  - `CanonicalToResponsesResponse`
  - `CanonicalToChatResponse`
  - `CanonicalToMessagesResponse`
- Reduced `ProtocolConverter.cs` to protocol constants, shared JSON options, tool type sets, and public conversion entrypoints.
- Left DTO shapes, route behavior, tool conversion behavior, usage conversion behavior, and protocol compatibility behavior unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.cs opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.Requests.cs opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.Responses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Protocols/ProtocolConverter.cs src/OpenCodex.Api/Protocols/ProtocolConverter.Requests.cs src/OpenCodex.Api/Protocols/ProtocolConverter.Responses.cs
```

Result:

- ProtocolConverter targeted tests passed: 29 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched protocol converter files.
- Trailing whitespace scan found no matches in touched protocol converter files.

Current size snapshot:

- `ProtocolConverter.cs`: 88 lines.
- `ProtocolConverter.Requests.cs`: 483 lines.
- `ProtocolConverter.Responses.cs`: 311 lines.
- `ProtocolConverter.Tools.cs`: 626 lines.
- `ProtocolConverter.Values.cs`: 273 lines.
- `ProtocolConverter.Content.cs`: 241 lines.
- `ProtocolConverter.ToolHistory.cs`: 221 lines.
- `ProtocolConverter.Reasoning.cs`: 99 lines.
- `ProtocolConverter.Usage.cs`: 59 lines.

Remaining risks:

- `ProtocolConverter.Tools.cs` remains large and is the next obvious protocol converter normalization target.
- `ProtocolConverter.Requests.cs` is still medium-large because request canonicalization includes Responses input history reconstruction and common request parameter copying.
- Broader checklist compliance still needs continued review beyond protocol converter file-size normalization, especially Swagger examples, validation edge cases, and service/integration boundaries.

## Completed Code Normalization Unit

### ProtocolConverter Apply Patch Tool Partial

Status: completed.

Goal:

- Continue reducing `ProtocolConverter.Tools.cs` without changing tool conversion behavior.
- Separate apply-patch proxy tool expansion, schema generation, and argument normalization from generic tool protocol conversion.
- Keep native tool wrapping, namespace name handling, canonical tool conversion, and tool choice conversion in `ProtocolConverter.Tools.cs`.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.Tools.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.ApplyPatchTools.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.ApplyPatchTools.cs`.
- Moved apply-patch focused helpers into the new partial:
  - `ExpandApplyPatchProxyTools`
  - `IsApplyPatchCanonicalTool`
  - `ApplyPatchProxyTools`
  - `ApplyPatchSingleOpSchema`
  - `ApplyPatchBatchSchema`
  - `ApplyPatchHunksSchema`
  - `NormalizeApplyPatchArguments`
  - `IsJsonObjectString`
- Left `IsApplyPatchName` in `ProtocolConverter.Tools.cs` because it is shared by generic tool detection and apply-patch normalization.
- Left tool names, JSON schemas, canonical/native tool shapes, namespace parsing, and request/response conversion behavior unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.Tools.cs opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.ApplyPatchTools.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Protocols/ProtocolConverter.Tools.cs src/OpenCodex.Api/Protocols/ProtocolConverter.ApplyPatchTools.cs
```

Result:

- ProtocolConverter targeted tests passed: 29 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched protocol converter tool files.
- Trailing whitespace scan found no matches in touched protocol converter tool files.

Current size snapshot:

- `ProtocolConverter.Requests.cs`: 483 lines.
- `ProtocolConverter.Tools.cs`: 428 lines.
- `ProtocolConverter.Responses.cs`: 311 lines.
- `ProtocolConverter.Values.cs`: 273 lines.
- `ProtocolConverter.Content.cs`: 241 lines.
- `ProtocolConverter.ToolHistory.cs`: 221 lines.
- `ProtocolConverter.ApplyPatchTools.cs`: 202 lines.
- `ProtocolConverter.Reasoning.cs`: 99 lines.
- `ProtocolConverter.cs`: 88 lines.
- `ProtocolConverter.Usage.cs`: 59 lines.

Remaining risks:

- `ProtocolConverter.Requests.cs` and `ProtocolConverter.Tools.cs` remain the largest protocol converter files.
- Apply-patch proxy schemas are covered through current protocol converter regression tests, but unusual malformed schema combinations could still benefit from focused regression cases.
- Broader checklist compliance still needs continued review beyond protocol converter file-size normalization, especially Swagger examples, validation edge cases, and service/integration boundaries.

## Completed Code Normalization Unit

### ProtocolConverter Responses Input Partial

Status: completed.

Goal:

- Continue reducing `ProtocolConverter.Requests.cs` without changing request conversion behavior.
- Separate Responses input/history reconstruction helpers from protocol request orchestration.
- Keep `ToCanonicalRequest`, `FromCanonicalRequest`, protocol-specific request converters, and common request parameter copying in `ProtocolConverter.Requests.cs`.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.Requests.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.ResponsesInput.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.ResponsesInput.cs`.
- Moved Responses input/history helpers into the new partial:
  - `ResponsesInputItemToMessages`
  - `MessagesToResponsesInput`
  - `MergeSystemMessages`
- Left `CopyCommonRequestParams` in `ProtocolConverter.Requests.cs` because it is shared by Responses, Chat, and Messages request canonicalization.
- Left protocol request conversion behavior, model handling, max token aliasing, tool conversion calls, and content conversion behavior unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.Requests.cs opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.ResponsesInput.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Protocols/ProtocolConverter.Requests.cs src/OpenCodex.Api/Protocols/ProtocolConverter.ResponsesInput.cs
```

Result:

- ProtocolConverter targeted tests passed: 29 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched protocol converter request files.
- Trailing whitespace scan found no matches in touched protocol converter request files.

Current size snapshot:

- `ProtocolConverter.Tools.cs`: 428 lines.
- `ProtocolConverter.Responses.cs`: 311 lines.
- `ProtocolConverter.Requests.cs`: 282 lines.
- `ProtocolConverter.Values.cs`: 273 lines.
- `ProtocolConverter.Content.cs`: 241 lines.
- `ProtocolConverter.ToolHistory.cs`: 221 lines.
- `ProtocolConverter.ResponsesInput.cs`: 206 lines.
- `ProtocolConverter.ApplyPatchTools.cs`: 202 lines.
- `ProtocolConverter.Reasoning.cs`: 99 lines.
- `ProtocolConverter.cs`: 88 lines.
- `ProtocolConverter.Usage.cs`: 59 lines.

Remaining risks:

- `ProtocolConverter.Tools.cs` remains the largest protocol converter file and still contains several related but separable responsibilities, especially namespace/native tool conversion.
- `ProtocolConverter.Responses.cs` and `ProtocolConverter.Requests.cs` are now medium-sized and may not need further immediate splitting unless checklist review finds clearer domain boundaries.
- Broader checklist compliance still needs continued review beyond protocol converter file-size normalization, especially Swagger examples, validation edge cases, and service/integration boundaries.

## Completed Code Normalization Unit

### ProtocolConverter Tool Names Partial

Status: completed.

Goal:

- Continue reducing `ProtocolConverter.Tools.cs` without changing tool conversion behavior.
- Separate tool name normalization, namespace parsing, Responses function-call name shaping, and shared apply-patch name detection into a focused partial.
- Keep generic tool protocol conversion and tool-choice conversion in `ProtocolConverter.Tools.cs`.

Implemented files:

- `src/OpenCodex.Api/Protocols/ProtocolConverter.Tools.cs`
- `src/OpenCodex.Api/Protocols/ProtocolConverter.ToolNames.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProtocolConverter.ToolNames.cs`.
- Moved tool name and namespace helpers into the new partial:
  - `NamespaceNameToChat`
  - `NamespaceCallParts`
  - `SplitFlatNamespaceName`
  - `ResponsesFunctionCallNameFields`
  - `IsApplyPatchName`
- Left `ToolChoiceToChat` in `ProtocolConverter.Tools.cs` because it is a tool-choice semantic conversion helper rather than a tool-name helper.
- Left namespace parsing behavior, flat namespace heuristics, apply-patch name matching, and Responses function-call name output unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProtocolConverterTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.Tools.cs opencodex_proxy/src/OpenCodex.Api/Protocols/ProtocolConverter.ToolNames.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Protocols/ProtocolConverter.Tools.cs src/OpenCodex.Api/Protocols/ProtocolConverter.ToolNames.cs
```

Result:

- ProtocolConverter targeted tests passed: 29 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched protocol converter tool-name files.
- Trailing whitespace scan found no matches in touched protocol converter tool-name files.

Current size snapshot:

- `ProtocolConverter.Tools.cs`: 343 lines.
- `ProtocolConverter.Responses.cs`: 311 lines.
- `ProtocolConverter.Requests.cs`: 282 lines.
- `ProtocolConverter.Values.cs`: 273 lines.
- `ProtocolConverter.Content.cs`: 241 lines.
- `ProtocolConverter.ToolHistory.cs`: 221 lines.
- `ProtocolConverter.ResponsesInput.cs`: 206 lines.
- `ProtocolConverter.ApplyPatchTools.cs`: 202 lines.
- `ProtocolConverter.Reasoning.cs`: 99 lines.
- `ProtocolConverter.ToolNames.cs`: 89 lines.
- `ProtocolConverter.cs`: 88 lines.
- `ProtocolConverter.Usage.cs`: 59 lines.

Remaining risks:

- Protocol converter files are now moderately sized, but `ProtocolConverter.Tools.cs` still combines canonical conversion and native tool wrapping.
- Focused regression coverage for namespace heuristics could be broader, especially names with multiple `__` segments or legacy dotted namespaces.
- Broader checklist compliance still needs continued review beyond protocol converter file-size normalization, especially Swagger examples, validation edge cases, and service/integration boundaries.

## Completed Code Normalization Unit

### OpenCodexDatabase JSON Value Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.Values.cs` without changing persistence behavior.
- Separate JSON parsing, serialization, and JsonElement normalization helpers from general database value conversion helpers.
- Keep reader helpers, nullable SQL parameter helpers, optional dictionary conversion helpers, numeric parsing, timestamp conversion, and business value normalization in `OpenCodexDatabase.Values.cs`.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Json.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Json.cs`.
- Moved JSON-focused helpers into the new partial:
  - `JsonOptions`
  - `ParseJsonObject`
  - `ParseJsonList`
  - `FromJsonElement`
  - `JsonDumps`
  - `NormalizeJsonValue`
- Left database reader/parameter conversion helpers and general optional/numeric value conversion helpers in `OpenCodexDatabase.Values.cs`.
- Left JSON serializer options, invalid JSON fallback behavior, array/object conversion behavior, and unsafe relaxed JSON escaping unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.Json.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs src/OpenCodex.Api/Persistence/OpenCodexDatabase.Json.cs
```

Result:

- Database targeted tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched database value files.
- Trailing whitespace scan found no matches in touched database value files.

Current size snapshot:

- `OpenCodexDatabase.Observability.cs`: 517 lines.
- `OpenCodexDatabase.Values.cs`: 513 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.WebSearch.cs`: 322 lines.
- `OpenCodexDatabase.Observability.Public.cs`: 270 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Json.cs`: 136 lines.

Remaining risks:

- `OpenCodexDatabase.Values.cs` remains large because it still combines reader helpers, SQL parameter helpers, optional dictionary conversion, numeric parsing, timestamp conversion, and business value normalization.
- `OpenCodexDatabase.Observability.cs` is now the largest persistence file and may be a better next normalization target if it has separable filter/statistics helpers.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially repository boundaries, query service boundaries, validation edge cases, and Swagger examples.

## Completed Code Normalization Unit

### OpenCodexDatabase Observability Filter Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.Observability.cs` without changing observability query behavior.
- Separate request-log column metadata, log filter construction, filter option queries, SQL parameter binding, and log pagination parsing from stats aggregation helpers.
- Keep stats range resolution, stats summary queries, and model distribution queries in `OpenCodexDatabase.Observability.cs`.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Observability.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Observability.Filters.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Observability.Filters.cs`.
- Moved observability filter/log-list helpers into the new partial:
  - `RequestLogMetadataColumns`
  - `RequestLogDetailColumns`
  - `TextLogFilterFields`
  - `IntegerLogFilterFields`
  - `RequestStatusValues`
  - `LogFilterFields`
  - `SqlQueryParameter`
  - `LogWhereClause`
  - `LogMetadataSelectColumns`
  - `LogDetailSelectColumns`
  - `BuildLogWhereClause`
  - `EmptyLogFilterOptions`
  - `DistinctTextValues`
  - `DistinctIntValues`
  - `AppendWhereCondition`
  - `AddParameters`
  - `IsEmptyLogFilterValue`
  - `ParseLogPage`
  - `ParseLogPageSize`
- Kept stats-specific records and helpers in `OpenCodexDatabase.Observability.cs`.
- Restored `Microsoft.Data.Sqlite` using in `OpenCodexDatabase.Observability.cs` after the split because stats helpers still create SQLite commands directly.
- Left SQL text, filter semantics, request status classification, filter option limits, and pagination bounds unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.Observability.cs opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.Observability.Filters.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Persistence/OpenCodexDatabase.Observability.cs src/OpenCodex.Api/Persistence/OpenCodexDatabase.Observability.Filters.cs
```

Result:

- Database targeted tests passed after restoring the stats-file SQLite using: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched observability files.
- Trailing whitespace scan found no matches in touched observability files.

Current size snapshot:

- `OpenCodexDatabase.Values.cs`: 513 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.WebSearch.cs`: 322 lines.
- `OpenCodexDatabase.Observability.Filters.cs`: 283 lines.
- `OpenCodexDatabase.Observability.Public.cs`: 270 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Observability.cs`: 239 lines.

Remaining risks:

- `OpenCodexDatabase.Values.cs` is again the largest persistence file and still has several separable helper groups.
- Log filter behavior is covered by database tests, but focused filter-builder edge cases could still be expanded, especially combined text/int/status/date filters.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially repository/query-service boundaries, validation edge cases, and Swagger examples.

## Completed Code Normalization Unit

### OpenCodexDatabase SQLite Helper Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.Values.cs` without changing persistence behavior.
- Separate SQLite command, reader, nullable SQL parameter, and non-query execution helpers from general value parsing and business value normalization.
- Remove the direct `Microsoft.Data.Sqlite` dependency from `OpenCodexDatabase.Values.cs`.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Sqlite.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Sqlite.cs`.
- Moved SQLite-focused helpers into the new partial:
  - `AddOwnerParameter`
  - `ReadInt`
  - `ReadDouble`
  - `ReadNullableDouble`
  - `GetNullableString`
  - `GetNullableDouble`
  - `GetNullableInt`
  - `GetNullableLong`
  - `AddNullableInt64`
  - `AddNullableInt32`
  - `AddNullableDouble`
  - `AddNullableString`
  - `ExecuteNonQuery`
- Left optional dictionary conversion, numeric conversion, timestamp conversion, Python-falsy compatibility, and business-specific normalization in `OpenCodexDatabase.Values.cs`.
- Left reader null/default behavior, SQL parameter names, `DBNull.Value` handling, and non-query execution behavior unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.Sqlite.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs src/OpenCodex.Api/Persistence/OpenCodexDatabase.Sqlite.cs
```

Result:

- Database targeted tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched database SQLite helper files.
- Trailing whitespace scan found no matches in touched database SQLite helper files.

Current size snapshot:

- `OpenCodexDatabase.Values.cs`: 436 lines.
- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.WebSearch.cs`: 322 lines.
- `OpenCodexDatabase.Observability.Filters.cs`: 283 lines.
- `OpenCodexDatabase.Observability.Public.cs`: 270 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Observability.cs`: 240 lines.
- `OpenCodexDatabase.Sqlite.cs`: 83 lines.

Remaining risks:

- `OpenCodexDatabase.Values.cs` remains the largest persistence helper file and still combines optional dictionary conversion, numeric parsing, timestamp conversion, and business value normalization.
- SQLite helper behavior is exercised through database tests, but there are no dedicated unit tests for each helper's null/default conversion behavior.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially repository/query-service boundaries, validation edge cases, and Swagger examples.

## Completed Code Normalization Unit

### OpenCodexDatabase Conversion Helper Partial

Status: completed.

Goal:

- Continue reducing `OpenCodexDatabase.Values.cs` without changing persistence behavior.
- Separate generic dictionary, optional value, numeric, and collection conversion helpers from timestamp and business-specific value normalization.
- Leave business compatibility helpers in `OpenCodexDatabase.Values.cs`, including Python-falsy behavior, channel timeout/retry values, web-search provider/key normalization, request status classification, and required integer validation.

Implemented files:

- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Conversions.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodexDatabase.Conversions.cs`.
- Moved generic conversion helpers into the new partial:
  - `GetOptionalValue`
  - `OptionalNullableString`
  - `OptionalInt`
  - `OptionalDouble`
  - `OptionalNullableInt`
  - `OptionalNullableLong`
  - `OptionalNullableDouble`
  - `ToInt`
  - `TryConvertInt64`
  - `TryConvertInt32`
  - `TryConvertDouble`
  - `TryAsObject`
  - `TryAsList`
  - `RequiredString`
  - `OptionalString`
- Left timestamp helpers, cached token extraction, request status classification, timeout/retry values, web-search normalization, positive/non-negative integer parsing, username normalization, and `IsPythonFalsy` in `OpenCodexDatabase.Values.cs`.
- Left conversion fallback/default behavior and exception handling unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~OpenCodexDatabaseTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.Conversions.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Persistence/OpenCodexDatabase.Values.cs src/OpenCodex.Api/Persistence/OpenCodexDatabase.Conversions.cs
```

Result:

- Database targeted tests passed: 47 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched database conversion files.
- Trailing whitespace scan found no matches in touched database conversion files.

Current size snapshot:

- `OpenCodexDatabase.Users.cs`: 341 lines.
- `OpenCodexDatabase.WebSearch.cs`: 322 lines.
- `OpenCodexDatabase.Observability.Filters.cs`: 283 lines.
- `OpenCodexDatabase.Observability.Public.cs`: 270 lines.
- `OpenCodexDatabase.AccessApiKeys.cs`: 266 lines.
- `OpenCodexDatabase.Conversions.cs`: 241 lines.
- `OpenCodexDatabase.Observability.cs`: 240 lines.
- `OpenCodexDatabase.Values.cs`: 200 lines.

Remaining risks:

- Persistence files are now moderately sized, but `OpenCodexDatabase.Users.cs` and `OpenCodexDatabase.WebSearch.cs` remain useful future normalization candidates if clearer repository/service boundaries are introduced.
- Generic conversion helper behavior is covered indirectly through database tests, but there are no dedicated unit tests for every fallback/default conversion edge case.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially repository/query-service boundaries, validation edge cases, and Swagger examples.

## Completed Code Normalization Unit

### HttpUpstreamClient Request Helper Partial

Status: completed.

Goal:

- Start reducing `HttpUpstreamClient.cs` without changing upstream HTTP behavior.
- Separate outbound request construction, channel header construction, upstream URL joining, JSON request serialization options, and channel value helpers from send/retry/response workflows.
- Keep POST, model-list, and streaming send/retry flows in `HttpUpstreamClient.cs`.

Implemented files:

- `src/OpenCodex.Api/Services/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Requests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Changed `HttpUpstreamClient` to a `partial` class.
- Added `HttpUpstreamClient.Requests.cs`.
- Moved request-focused helpers into the new partial:
  - `JsonOptions`
  - `BuildRequest`
  - `BuildGetRequest`
  - `BuildHeaders`
  - `JoinUrl`
  - `GetValue`
  - `StringValue`
  - `TimeoutValue`
  - `RetryCountValue`
- Left `PostJsonAsync`, `ListModelsAsync`, `StreamJsonAsync`, response JSON parsing, upstream HTTP error decoding, retry delay behavior, and JSON response element conversion in `HttpUpstreamClient.cs`.
- Left request body serialization, default headers, custom header merging, Anthropic `x-api-key`/version behavior, authorization behavior, URL `/v1` joining, timeout defaults, and retry count defaults unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.cs opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.Requests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/HttpUpstreamClient.cs src/OpenCodex.Api/Services/HttpUpstreamClient.Requests.cs
```

Result:

- Upstream-related targeted tests passed: 51 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched upstream client files.
- Trailing whitespace scan found no matches in touched upstream client files.

Current size snapshot:

- `WebSearchSimulator.cs`: 442 lines.
- `HttpUpstreamClient.cs`: 426 lines.
- `AdminChannelDiagnosticsService.cs`: 350 lines.
- `HttpUpstreamClient.Requests.cs`: 126 lines.

Remaining risks:

- `HttpUpstreamClient.cs` remains large because send/retry workflows, response parsing, error decoding, and retry delay behavior are still together.
- There are no direct unit tests for `HttpUpstreamClient` request header/url construction; current coverage is mostly indirect through higher-level fake upstream tests.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially service/integration boundaries, validation edge cases, and Swagger examples.

## Completed Code Normalization Unit

### HttpUpstreamClient Response Helper Partial

Status: completed.

Goal:

- Continue reducing `HttpUpstreamClient.cs` without changing upstream HTTP behavior.
- Separate upstream response JSON parsing, upstream HTTP error body decoding, and JSON element conversion from send/retry workflows.
- Keep POST, model-list, streaming send/retry loops, stream reading, and retry delay behavior in `HttpUpstreamClient.cs`.

Implemented files:

- `src/OpenCodex.Api/Services/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Responses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `HttpUpstreamClient.Responses.cs`.
- Moved response-focused helpers into the new partial:
  - `ReadJsonObject`
  - `ThrowHttpError`
  - `DecodeBody`
  - `FromJsonElement`
- Left invalid JSON error message/status, upstream HTTP error message/status/body, plain-text body truncation, and JSON number conversion behavior unchanged.
- Left retry delay behavior in `HttpUpstreamClient.cs` with the send/retry loops because it is still part of the retry workflow.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.cs opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.Responses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/HttpUpstreamClient.cs src/OpenCodex.Api/Services/HttpUpstreamClient.Responses.cs
```

Result:

- Upstream-related targeted tests passed: 51 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched upstream client response files.
- Trailing whitespace scan found no matches in touched upstream client response files.

Current size snapshot:

- `WebSearchSimulator.cs`: 442 lines.
- `AdminChannelDiagnosticsService.cs`: 350 lines.
- `HttpUpstreamClient.cs`: 341 lines.
- `HttpUpstreamClient.Requests.cs`: 126 lines.
- `HttpUpstreamClient.Responses.cs`: 91 lines.

Remaining risks:

- `HttpUpstreamClient.cs` still combines non-streaming, model-list, streaming, timeout, retry, and stream-reading workflows.
- Upstream response parsing/error decoding is covered indirectly by higher-level tests; there are still no direct unit tests for invalid JSON, non-object JSON, large plain-text error body truncation, or integer/long JSON number conversion.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially service/integration boundaries, validation edge cases, and Swagger examples.

## Completed Code Normalization Unit

### WebSearchSimulator Non-Streaming Partial

Status: completed.

Goal:

- Start reducing `WebSearchSimulator.cs` without changing web search simulation behavior.
- Separate the non-streaming `RunAsync` workflow from constructor/config checks, streaming workflow, and upstream error wrapping.
- Keep `CanSimulate`, `RunChatStreamAsync`, and `PostUpstream` in `WebSearchSimulator.cs`.

Implemented files:

- `src/OpenCodex.Api/Services/WebSearchSimulator.cs`
- `src/OpenCodex.Api/Services/WebSearchSimulator.NonStream.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Changed `WebSearchSimulator` to a `partial` class.
- Added `WebSearchSimulator.NonStream.cs`.
- Moved the non-streaming `RunAsync` workflow into the new partial.
- Left request copy behavior, `stream = false`, web search call extraction, Tavily key reservation, provider search invocation, continuation request creation, source annotation injection, iteration guard behavior, and upstream exception conversion unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/WebSearchSimulator.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchSimulator.NonStream.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/WebSearchSimulator.cs src/OpenCodex.Api/Services/WebSearchSimulator.NonStream.cs
```

Result:

- WebSearch/proxy targeted tests passed: 22 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched web search simulator files.
- Trailing whitespace scan found no matches in touched web search simulator files.

Current size snapshot:

- `HttpUpstreamClient.cs`: 341 lines.
- `AdminChannelDiagnosticsService.cs`: 350 lines.
- `WebSearchSimulator.cs`: 252 lines.
- `WebSearchSimulator.NonStream.cs`: 197 lines.

Remaining risks:

- `WebSearchSimulator.cs` still contains the full streaming workflow and upstream error wrapping.
- `RunAsync` coverage is still mostly behavioral through existing simulator/proxy tests; there are no new direct tests for every iteration guard branch in this structural-only unit.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially validation edge cases, Swagger examples, dependency boundaries, and repository/query boundaries.

## Completed Code Normalization Unit

### WebSearchSimulator Streaming Partial

Status: completed.

Goal:

- Continue reducing `WebSearchSimulator.cs` without changing streaming web search simulation behavior.
- Separate the streaming `RunChatStreamAsync` workflow from constructor/config checks and upstream error wrapping.
- Leave `CanSimulate` and `PostUpstream` in the main partial.

Implemented files:

- `src/OpenCodex.Api/Services/WebSearchSimulator.cs`
- `src/OpenCodex.Api/Services/WebSearchSimulator.Streaming.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `WebSearchSimulator.Streaming.cs`.
- Moved the streaming `RunChatStreamAsync` workflow into the new partial.
- Removed the now-unused `WebSearchPayload` static using from `WebSearchSimulator.cs`.
- Left initial stream conversion, skipped web_search tool output, prefix event replay, sequence/output index continuation, injected web_search SSE events, final upstream stream call, completed event injection, result metadata, and response payload construction unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/WebSearchSimulator.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchSimulator.Streaming.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/WebSearchSimulator.cs src/OpenCodex.Api/Services/WebSearchSimulator.Streaming.cs
```

Result:

- WebSearch/proxy targeted tests passed: 22 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched streaming web search simulator files.
- Trailing whitespace scan found no matches in touched streaming web search simulator files.

Current size snapshot:

- `AdminChannelDiagnosticsService.cs`: 350 lines.
- `HttpUpstreamClient.cs`: 341 lines.
- `WebSearchSimulator.Streaming.cs`: 201 lines.
- `WebSearchSimulator.NonStream.cs`: 197 lines.
- `WebSearchSimulator.cs`: 57 lines.

Remaining risks:

- Streaming edge coverage is still concentrated in existing simulator/proxy tests; no new tests were added for parse errors, provider failures, Tavily key exhaustion, or missing final upstream response in stream mode.
- `PostUpstream` remains in the main partial because it is shared by non-streaming `RunAsync`; if more upstream-wrapper behavior is added, it may need its own helper partial.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially validation edge cases, Swagger examples, dependency boundaries, and repository/query boundaries.

## Completed Code Normalization Unit

### HttpUpstreamClient Streaming Partial

Status: completed.

Goal:

- Continue reducing `HttpUpstreamClient.cs` without changing upstream streaming behavior.
- Separate the streaming upstream send/read workflow from non-streaming POST, model-list, request construction, response parsing, and retry delay helper logic.
- Keep shared endpoint/status retry definitions, constructor, non-streaming workflows, and `DelayBeforeRetry` in the main partial.

Implemented files:

- `src/OpenCodex.Api/Services/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Streaming.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `HttpUpstreamClient.Streaming.cs`.
- Moved `StreamJsonAsync` into the new partial.
- Removed `System.Runtime.CompilerServices` and `System.Text` using directives from `HttpUpstreamClient.cs` because they are now only needed by the streaming partial.
- Left streaming endpoint selection, request building, timeout handling, retry count handling, `ResponseHeadersRead`, retryable status behavior, timeout/HTTP exception mapping, response disposal, UTF-8 stream reading, and line newline preservation unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.cs opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.Streaming.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/HttpUpstreamClient.cs src/OpenCodex.Api/Services/HttpUpstreamClient.Streaming.cs
```

Result:

- Upstream-related targeted tests passed: 51 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched upstream streaming files.
- Trailing whitespace scan found no matches in touched upstream streaming files.

Current size snapshot:

- `AdminChannelDiagnosticsService.cs`: 350 lines.
- `HttpUpstreamClient.cs`: 236 lines.
- `HttpUpstreamClient.Requests.cs`: 126 lines.
- `HttpUpstreamClient.Streaming.cs`: 111 lines.
- `HttpUpstreamClient.Responses.cs`: 91 lines.

Remaining risks:

- `HttpUpstreamClient.cs` still combines non-streaming POST, model-list, and shared retry delay behavior; duplication between POST and model-list retry loops remains.
- Streaming upstream behavior is covered indirectly through proxy/web-search tests; there are still no direct unit tests for low-level `HttpUpstreamClient` retry-after delay, stream timeout, failed final fallback, or response disposal behavior.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially validation edge cases, Swagger examples, dependency boundaries, and repository/query boundaries.

## Completed Code Normalization Unit

### AdminChannelDiagnosticsService Value Helper Partial

Status: completed.

Goal:

- Start reducing `AdminChannelDiagnosticsService.cs` without changing admin diagnostics behavior.
- Separate low-level dictionary/list/object/value cloning helpers from the admin channel diagnostics workflows.
- Keep discover-models and test-channel business flow in the main service partial.

Implemented files:

- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs`
- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Values.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Changed `AdminChannelDiagnosticsService` to a `partial` class.
- Added `AdminChannelDiagnosticsService.Values.cs`.
- Moved value helpers into the new partial:
  - `GetValue`
  - `StringValue`
  - `ListValue`
  - `ObjectValue`
  - `ToInt`
  - `CloneObject`
  - `CloneJsonValue`
- Left channel draft normalization, config expansion/validation, flat payload construction, model mapping, compat application, model ID extraction, and upstream calls unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminChannelDiagnosticsServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Values.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Values.cs
```

Result:

- Admin diagnostics targeted tests passed: 4 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 51 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched admin diagnostics files.
- Trailing whitespace scan found no matches in touched admin diagnostics files.

Current size snapshot:

- `AdminChannelDiagnosticsService.cs`: 294 lines.
- `HttpUpstreamClient.cs`: 236 lines.
- `AdminChannelDiagnosticsService.Values.cs`: 61 lines.

Remaining risks:

- `AdminChannelDiagnosticsService.cs` still combines discover-models and test-channel workflows with channel drafting, flat payload construction, model mapping, compat application, and model ID extraction.
- Current tests cover primary behavior but do not exhaustively cover value helper edge cases such as non-list enumerable inputs, nested clone aliasing, numeric overflow, or unusual scalar values.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially validation edge cases, Swagger examples, dependency boundaries, and repository/query boundaries.

## Completed Code Normalization Unit

### AdminChannelDiagnosticsService Channel Draft Partial

Status: completed.

Goal:

- Continue reducing `AdminChannelDiagnosticsService.cs` without changing admin diagnostics behavior.
- Separate draft channel construction, inline/flat test body parsing, and flat payload construction from the main discover/test application flow.
- Keep discover-models, test-channel orchestration, model mapping, compat application, model extraction, timeout access, and value helpers in their existing partials.

Implemented files:

- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs`
- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.ChannelDraft.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `AdminChannelDiagnosticsService.ChannelDraft.cs`.
- Moved channel draft/test body helpers into the new partial:
  - `ChannelKeys`
  - `DraftChannelFromBody`
  - `ParseTestChannelBody`
  - `BuildPayloadFromFlat`
- Moved config/protocol usings needed by these helpers to the new partial.
- Restored `OpenCodex.Api.Configuration` using in `AdminChannelDiagnosticsService.cs` after the first targeted test compile exposed that the main partial still needs `IOpenCodexRuntimeSettingsProvider`.
- Left channel key filtering, config normalization, environment expansion, channel validation, payload cloning, default flat input text, max token conversion, chat/messages/responses flat payload shapes, and error messages unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminChannelDiagnosticsServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.ChannelDraft.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.ChannelDraft.cs
```

Result:

- Initial targeted compile failed because `AdminChannelDiagnosticsService.cs` still needed `OpenCodex.Api.Configuration` for `IOpenCodexRuntimeSettingsProvider`; restored that using.
- Admin diagnostics targeted tests passed after fix: 4 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 51 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched admin diagnostics channel draft files.
- Trailing whitespace scan found no matches in touched admin diagnostics channel draft files.

Current size snapshot:

- `HttpUpstreamClient.cs`: 236 lines.
- `AdminChannelDiagnosticsService.cs`: 175 lines.
- `AdminChannelDiagnosticsService.ChannelDraft.cs`: 125 lines.
- `AdminChannelDiagnosticsService.Values.cs`: 61 lines.

Remaining risks:

- `AdminChannelDiagnosticsService.cs` still contains model mapping, compat application, model ID extraction, and timeout access in the main partial; compat logic could be a future focused split.
- Current tests cover primary inline-channel and flat responses flows, but not flat chat/messages payload defaults, empty input defaulting, env-expanded channel edge cases, or malformed expanded config shapes.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially validation edge cases, Swagger examples, dependency boundaries, and repository/query boundaries.

## Completed Code Normalization Unit

### AdminChannelDiagnosticsService Compat Partial

Status: completed.

Goal:

- Continue reducing `AdminChannelDiagnosticsService.cs` without changing admin channel test behavior.
- Separate upstream compatibility parameter handling from the main admin diagnostics orchestration.
- Keep discover-models/test-channel orchestration, model mapping, model ID extraction, timeout access, channel drafting, and value helpers in their existing partials.

Implemented files:

- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs`
- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Compat.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `AdminChannelDiagnosticsService.Compat.cs`.
- Moved `ApplyCompat` into the new partial.
- Removed the `OpenCodex.Api.Errors` using from `AdminChannelDiagnosticsService.cs` because `BadRequestException` is now only used by the compat partial.
- Left compat processing order and behavior unchanged:
  - `default_params`
  - `rename_params`
  - `drop_params`
  - `force_params`
  - `unsupported_params`
- Left compat detail strings, clone semantics, unsupported parameter sorting, and error message unchanged.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminChannelDiagnosticsServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Compat.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Compat.cs
```

Result:

- Admin diagnostics targeted tests passed: 4 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 51 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 295 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched admin diagnostics compat files.
- Trailing whitespace scan found no matches in touched admin diagnostics compat files.

Current size snapshot:

- `HttpUpstreamClient.cs`: 236 lines.
- `AdminChannelDiagnosticsService.ChannelDraft.cs`: 125 lines.
- `AdminChannelDiagnosticsService.cs`: 113 lines.
- `AdminChannelDiagnosticsService.Compat.cs`: 67 lines.
- `AdminChannelDiagnosticsService.Values.cs`: 61 lines.

Remaining risks:

- Compat behavior still has only primary-path coverage through `AdminChannelDiagnosticsServiceTests`; unsupported parameter errors and duplicate rename target behavior are not covered by dedicated tests.
- `AdminChannelDiagnosticsService.cs` still contains model mapping and model ID extraction helpers; they are small enough to leave for now, but could move to a model partial if more model diagnostics logic grows.
- Broader checklist compliance still needs continued review beyond file-size normalization, especially validation edge cases, Swagger examples, dependency boundaries, and repository/query boundaries.

## Completed Test Coverage Unit

### AdminChannelDiagnosticsService Boundary Tests

Status: completed.

Goal:

- Improve checklist coverage for admin diagnostics validation and boundary cases.
- Add focused tests for flat channel+payload request generation and compat business failure behavior.
- Avoid production behavior changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/AdminChannelDiagnosticsServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `TestChannelBuildsFlatChatPayloadWithDefaultInput`.
  - Verifies flat chat test requests default missing input to `ping`.
  - Verifies default `max_tokens` is `256`.
  - Verifies generated chat payload contains a single user message.
- Added `TestChannelBuildsFlatMessagesPayload`.
  - Verifies flat messages test requests use the messages protocol shape.
  - Verifies string `max_output_tokens` values are converted to integer `max_tokens`.
- Added `TestChannelRejectsUnsupportedCompatParameters`.
  - Verifies compat `unsupported_params` produces a `BadRequestException`.
  - Verifies unsupported parameter names are sorted in the error message.
- Added a small `AssertList` helper for payload message assertions.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminChannelDiagnosticsServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/AdminChannelDiagnosticsServiceTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/AdminChannelDiagnosticsServiceTests.cs
```

Result:

- Admin diagnostics targeted tests passed: 7 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 54 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 298 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched admin diagnostics test file.
- Trailing whitespace scan found no matches in touched admin diagnostics test file.

Remaining risks:

- Compat rename behavior when the target key already exists is still not covered by a dedicated test.
- Empty/invalid compat entries such as blank unsupported parameter names are still not covered.
- Broader checklist compliance still needs continued review beyond this test unit, especially Swagger examples, dependency boundaries, repository/query boundaries, and low-level upstream HTTP client edge tests.

## Completed Test Coverage Unit

### AdminChannelDiagnosticsService Compat Edge Tests

Status: completed.

Goal:

- Continue improving checklist coverage for admin diagnostics compat validation.
- Cover rename conflict behavior and non-triggering unsupported parameter entries.
- Avoid production behavior changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/AdminChannelDiagnosticsServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `TestChannelRenameCompatDoesNotOverwriteExistingTarget`.
  - Verifies `rename_params` removes the source key even when the target key already exists.
  - Verifies the existing target value is preserved rather than overwritten.
  - Verifies the compat detail still records the rename operation.
- Added `TestChannelIgnoresUnsupportedCompatEntriesThatAreBlankOrAbsent`.
  - Verifies blank unsupported parameter names are ignored.
  - Verifies unsupported names that are absent from the request are ignored.
  - Verifies unrelated request parameters remain in the upstream payload.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminChannelDiagnosticsServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/AdminChannelDiagnosticsServiceTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/AdminChannelDiagnosticsServiceTests.cs
```

Result:

- Admin diagnostics targeted tests passed: 9 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 56 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 300 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched admin diagnostics test file.
- Trailing whitespace scan found no matches in touched admin diagnostics test file.

Remaining risks:

- Compat nested clone behavior and default/force interactions with mutable nested objects are still not covered directly.
- Channel draft tests still do not cover environment-expanded values or malformed expanded config shapes.
- Broader checklist compliance still needs continued review beyond this test unit, especially Swagger examples, dependency boundaries, repository/query boundaries, and low-level upstream HTTP client edge tests.

## Completed Test Coverage Unit

### HttpUpstreamClient Response Boundary Tests

Status: completed.

Goal:

- Add direct low-level tests for `HttpUpstreamClient`, which previously had coverage mostly through proxy/admin/web-search services.
- Improve checklist coverage for external integration response parsing and upstream error conversion.
- Avoid production behavior changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `HttpUpstreamClientTests`.
- Added `PostJsonAsyncReturnsParsedJsonObject`.
  - Verifies successful upstream JSON object parsing.
  - Verifies POST request method and `/v1/responses` URL joining.
- Added `PostJsonAsyncRejectsInvalidJsonObject`.
  - Verifies non-object JSON response bodies are rejected as invalid upstream JSON.
  - Verifies status code, channel ID, and empty upstream body details.
- Added `PostJsonAsyncDecodesHttpErrorJsonBody`.
  - Verifies non-success upstream HTTP status is converted to `UpstreamException`.
  - Verifies JSON error body decoding into structured dictionaries.
- Added a small stub `HttpMessageHandler` for deterministic low-level HTTP client tests.

Verification notes:

- First targeted compile failed because the new test file needed `Microsoft.AspNetCore.Http` for `StatusCodes`; added the missing using.
- The first success-path assertion expected `int` for a JSON number, but runtime conversion surfaced a numeric type assertion mismatch; changed the assertion to compare via `Convert.ToInt64` so the test validates numeric semantics rather than implementation storage type.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
```

Result:

- Http upstream client targeted tests passed: 3 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 59 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 303 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the new upstream client test file.
- Trailing whitespace scan found no matches in the new upstream client test file.

Remaining risks:

- Low-level streaming tests are still missing for `StreamJsonAsync`.
- Retry behavior, retry-after delay handling, timeout mapping, and `HttpRequestException` mapping still need direct low-level tests.
- Plain-text upstream error body truncation and model-list `/models` behavior are not directly covered yet.
- Broader checklist compliance still needs continued review beyond this test unit, especially Swagger examples, dependency boundaries, repository/query boundaries, and low-level upstream streaming/retry tests.

## Completed Test Coverage Unit

### HttpUpstreamClient Models And Plain Text Error Tests

Status: completed.

Goal:

- Continue direct low-level `HttpUpstreamClient` coverage without introducing real retry/delay waits.
- Cover model-list endpoint construction and plain-text upstream error body truncation.
- Avoid production behavior changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ListModelsAsyncUsesModelsEndpoint`.
  - Verifies `ListModelsAsync` issues a GET request.
  - Verifies base URL joining produces `/v1/models`.
  - Verifies successful JSON response parsing for a model list payload.
- Added `PostJsonAsyncTruncatesPlainTextHttpErrorBody`.
  - Verifies plain-text upstream HTTP error bodies are kept as strings.
  - Verifies bodies longer than 2000 characters are truncated to 2000 characters.
  - Verifies the upstream HTTP status and message are preserved in `UpstreamException`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
```

Result:

- Http upstream client targeted tests passed: 5 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 61 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 305 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched upstream client test file.
- Trailing whitespace scan found no matches in the touched upstream client test file.

Remaining risks:

- Low-level streaming tests are still missing for `StreamJsonAsync`.
- Retry behavior, retry-after delay handling, timeout mapping, and `HttpRequestException` mapping still need direct low-level tests.
- Header construction and Anthropic `messages` auth/version behavior are still covered mostly indirectly.
- Broader checklist compliance still needs continued review beyond this test unit, especially Swagger examples, dependency boundaries, repository/query boundaries, and low-level upstream streaming/retry tests.

## Completed Test Coverage Unit

### HttpUpstreamClient Streaming Boundary Tests

Status: completed.

Goal:

- Add direct low-level tests for `HttpUpstreamClient.StreamJsonAsync`.
- Cover streaming line reading and stream-mode upstream error conversion without introducing real retry/delay waits.
- Avoid production behavior changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `StreamJsonAsyncReadsLinesWithTrailingNewlines`.
  - Verifies successful stream responses are read line by line.
  - Verifies each yielded line preserves the trailing newline added by `StreamJsonAsync`.
  - Verifies POST request method and `/v1/responses` URL joining for streaming requests.
- Added `StreamJsonAsyncDecodesHttpErrorJsonBody`.
  - Verifies non-success stream responses are converted to `UpstreamException`.
  - Verifies JSON upstream error body decoding in stream mode.
  - Verifies channel ID and HTTP status are preserved.
- Added a small `ReadStream` helper for collecting `IAsyncEnumerable<string>` lines in low-level tests.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
```

Result:

- Http upstream client targeted tests passed: 7 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 63 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 307 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched upstream client test file.
- Trailing whitespace scan found no matches in the touched upstream client test file.

Remaining risks:

- Retry behavior, retry-after delay handling, timeout mapping, and `HttpRequestException` mapping still need direct low-level tests.
- Streaming response disposal is covered indirectly by the code path but not asserted directly.
- Header construction and Anthropic `messages` auth/version behavior are still covered mostly indirectly.
- Broader checklist compliance still needs continued review beyond this test unit, especially Swagger examples, dependency boundaries, repository/query boundaries, and low-level upstream retry/timeout tests.

## Completed Test Coverage Unit

### HttpUpstreamClient Network Error And Messages Header Tests

Status: completed.

Goal:

- Continue low-level `HttpUpstreamClient` coverage for external integration failure and protocol-specific header construction.
- Cover immediate network failure mapping and Anthropic/messages request headers without introducing real retry/delay waits.
- Avoid production behavior changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `PostJsonAsyncMapsHttpRequestException`.
  - Verifies `HttpRequestException` is converted to `UpstreamException`.
  - Verifies message, 502 status code, channel ID, and absent upstream body.
- Added `PostJsonAsyncBuildsMessagesHeaders`.
  - Verifies `messages` protocol posts to `/v1/messages`.
  - Verifies config API key is sent as `x-api-key`.
  - Verifies default `anthropic-version` is added.
  - Verifies bearer `authorization` is not sent for `messages`.
- Generalized the test `Channel` helper to accept a protocol type.
- Reused the existing stub `HttpMessageHandler`; no production code changes.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
```

Result:

- Http upstream client targeted tests passed: 9 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 65 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 309 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched upstream client test file.
- Trailing whitespace scan found no matches in the touched upstream client test file.

Remaining risks:

- Retry behavior, retry-after delay handling, and timeout mapping still need direct low-level tests.
- Custom header override behavior and non-config auth modes need direct low-level tests.
- Streaming response disposal is covered indirectly by the code path but not asserted directly.
- Broader checklist compliance still needs continued review beyond this test unit, especially Swagger examples, dependency boundaries, repository/query boundaries, and low-level upstream retry/timeout tests.

## Completed Test Coverage Unit

### HttpUpstreamClient Authorization Precedence Test

Status: completed.

Goal:

- Continue direct low-level `HttpUpstreamClient` coverage for request header construction.
- Lock down the current chat authorization precedence behavior.
- Avoid production behavior changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `PostJsonAsyncConfigAuthOverridesCustomAuthorizationHeader`.
  - Verifies ordinary `chat` requests with `auth_mode=config` use the configured API key as `authorization`.
  - Verifies a custom `authorization` channel header does not override config auth under the current implementation.
  - Documents current behavior explicitly in a direct low-level test.

Verification notes:

- During planning, confirmed the implementation currently loads custom headers first and then overwrites ordinary-protocol `authorization` when `auth_mode=config`.
- This test locks down current behavior. If custom authorization should take precedence in the future, that should be handled as an intentional behavior change.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
```

Result:

- Http upstream client targeted tests passed: 12 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 68 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 312 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched upstream client test file.
- Trailing whitespace scan found no matches in the touched upstream client test file.

Remaining risks:

- Retry behavior, retry-after delay handling, and timeout mapping still need direct low-level tests.
- Header behavior when custom headers are used together with `messages` and non-config auth modes may still need more edge tests.
- Streaming response disposal is covered indirectly by the code path but not asserted directly.
- Broader checklist compliance still needs continued review beyond this test unit, especially Swagger examples, dependency boundaries, repository/query boundaries, and low-level upstream retry/timeout tests.

## Completed Test Coverage Unit

### HttpUpstreamClient Header And Auth Mode Tests

Status: completed.

Goal:

- Continue direct low-level `HttpUpstreamClient` coverage for request header construction.
- Cover custom Anthropic version headers and non-config auth mode behavior without introducing retry/delay waits.
- Avoid production behavior changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `PostJsonAsyncKeepsCustomAnthropicVersionHeader`.
  - Verifies custom `anthropic-version` channel headers are preserved for `messages`.
  - Verifies `x-api-key` is still added from the configured API key.
- Added `PostJsonAsyncOmitsAuthorizationWhenAuthModeIsNotConfig`.
  - Verifies `chat` requests with `auth_mode` set to a non-config value do not receive bearer `authorization`.
  - Verifies no `x-api-key` is added for non-messages protocols.
  - Verifies chat endpoint URL joining still targets `/v1/chat/completions`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
```

Result:

- Http upstream client targeted tests passed: 11 passed, 0 failed, 0 skipped.
- Upstream/admin related targeted tests passed: 67 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 311 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched upstream client test file.
- Trailing whitespace scan found no matches in the touched upstream client test file.

Remaining risks:

- Retry behavior, retry-after delay handling, and timeout mapping still need direct low-level tests.
- Custom header override behavior for ordinary chat authorization is still not directly tested.
- Streaming response disposal is covered indirectly by the code path but not asserted directly.
- Broader checklist compliance still needs continued review beyond this test unit, especially Swagger examples, dependency boundaries, repository/query boundaries, and low-level upstream retry/timeout tests.

## Completed Swagger Coverage Unit

### Swagger Critical Controller Routes Smoke Test

Status: completed.

Goal:

- Continue checklist item "十五、接口文档" by proving Swagger is not only enabled but also exposes key Controller routes.
- Keep this unit scoped to test coverage only; no production routing or Swagger configuration changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Strengthened `SwaggerDocumentIsAvailableInDevelopment`.
  - Parses `/swagger/v1/swagger.json` as `JsonElement`.
  - Verifies the document has an `openapi` property.
  - Verifies `paths` includes key proxy routes:
    - `POST /v1/responses`
    - `POST /v1/chat/completions`
    - `POST /v1/messages`
  - Verifies `paths` includes key admin diagnostics routes:
    - `POST /admin/api/channels/test`
    - `POST /admin/api/channels/discover-models`
  - Verifies `paths` includes `GET /health`.
- Added a small `AssertSwaggerPath` helper to keep route/method assertions readable.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~SmokeTests.SwaggerDocumentIsAvailableInDevelopment" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Targeted Swagger smoke test passed: 1 passed, 0 failed, 0 skipped.
- Full smoke test class passed: 6 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched smoke test file.
- Trailing whitespace scan found no matches in the touched smoke test file.

Remaining risks:

- Swagger currently verifies route visibility but not request parameter descriptions, response examples, error code descriptions, pagination/idempotency rules, or field-level descriptions.
- Compatibility routes such as `/admin/api/test-channel` and `/admin/api/discover-models` are not asserted in this smoke unit.
- This unit does not verify that Swagger is hidden outside Development.

## Completed Swagger Boundary Unit

### Swagger Compatibility Routes And Environment Visibility

Status: completed.

Goal:

- Continue checklist item "十五、接口文档" with a small boundary-focused test unit.
- Address the previous uncovered Swagger edges:
  - Admin diagnostics compatibility routes should be visible in Swagger.
  - Swagger should not be exposed outside Development.
- Keep the change aligned with `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md`:
  - Controller remains a thin HTTP entry.
  - Swagger wiring remains infrastructure/startup concern.
  - This unit changes tests only and introduces no production layering changes.

Implemented files:

- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Extended `SwaggerDocumentIsAvailableInDevelopment`.
  - Verifies `POST /admin/api/test-channel`.
  - Verifies `POST /admin/api/discover-models`.
- Added `SwaggerDocumentIsHiddenOutsideDevelopment`.
  - Creates a test host with `environment = Production`.
  - Verifies `/swagger/v1/swagger.json` returns `404 NotFound`.
- Updated the smoke test `CreateClient` helper to accept an environment name while preserving the default `Development` behavior for existing tests.
- Re-read architecture/class-library guidance before selecting the next larger unit:
  - `PROJECT_ARCHITECTURE.md`
  - `CLASS_LIBRARY_GUIDE.md`

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~SmokeTests.SwaggerDocumentIsAvailableInDevelopment|FullyQualifiedName~SmokeTests.SwaggerDocumentIsHiddenOutsideDevelopment" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Targeted Swagger boundary tests passed: 2 passed, 0 failed, 0 skipped.
- Full smoke test class passed: 7 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched smoke test file.
- Trailing whitespace scan found no matches in the touched smoke test file.

Remaining risks:

- Swagger still lacks explicit request parameter descriptions, response examples, error code descriptions, pagination/idempotency rules, and important field descriptions.
- Full production-like hosting behavior is only verified through `WebApplicationFactory`, not a deployed container.
- The broader architecture goal still needs dedicated source-code review against the documented API/Application/Domain/DataAccess/Infrastructure/ExternalIntegrations boundaries.

## Completed Architecture Layering Unit

### AdminObservability Controller Response DTO Extraction

Status: completed.

Goal:

- Continue `NEW_PROJECT_CHECKLIST.md` architecture/API design items by applying the documented layering rules from:
  - `PROJECT_ARCHITECTURE.md`
  - `CLASS_LIBRARY_GUIDE.md`
- Reduce `AdminObservabilityController` from a response-shaping controller into a thinner HTTP entry point.
- Keep all admin observability JSON fields and routes backward compatible.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminObservabilityController.cs`
- `src/OpenCodex.Api/DTOs/AdminObservability/AdminObservabilityResponses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added explicit admin observability response DTOs under `DTOs/AdminObservability`.
  - `LogsPageResponse`
  - `LogEventResponse`
  - `LogDetailResponse`
  - `StatsResponse`
  - `StatsSummaryResponse`
  - `StatsPointResponse`
  - `StatsModelDistributionResponse`
- Added `JsonPropertyName` annotations to preserve existing snake_case admin API contracts.
- Added `From(...)` factory methods to centralize mapping from persistence records to API response DTOs.
- Removed large response dictionary construction methods from `AdminObservabilityController`.
- Removed the controller's direct `OpenCodex.Api.Persistence` using.
- Reduced `AdminObservabilityController` from 231 lines to 121 lines.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminObservabilityServiceTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminObservabilityController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminObservability/AdminObservabilityResponses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminObservabilityController.cs src/OpenCodex.Api/DTOs/AdminObservability/AdminObservabilityResponses.cs
```

Result:

- Admin data controller integration tests passed: 25 passed, 0 failed, 0 skipped.
- Admin observability service and smoke tests passed: 12 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 313 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.

Remaining risks:

- Mapping currently lives in API DTO static factories and still depends on persistence record types; a future Application layer split may move these mappings into an application-facing DTO/model boundary.
- `LogFilters` and query default handling still live in the controller; they may be acceptable lightweight HTTP concerns, but a later pass can centralize query DTO binding if needed.
- Other controllers still need the same architecture review, especially larger admin controllers and any endpoint that shapes large response dictionaries inline.

## Completed Architecture Layering Unit

### AdminWebSearch Controller Response DTO Extraction

Status: completed.

Goal:

- Continue applying `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md` to keep controllers thin and response contracts explicit.
- Remove inline response dictionary construction from `AdminWebSearchController`.
- Preserve the existing admin web-search API JSON contract exactly, including compatibility details around `usage_limit` and `key_usage_limit`.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminWebSearchController.cs`
- `src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added explicit web-search response DTOs under `DTOs/AdminWebSearch`.
  - `WebSearchConfigResponse`
  - `TavilyKeyResponse`
  - `WebSearchTestKeyResponse`
  - `WebSearchProviderResultResponse`
  - `WebSearchSummaryResponse`
  - `WebSearchTestKeyResponsePayload`
- Added `JsonPropertyName` annotations to preserve existing snake_case response fields.
- Moved response mapping from controller private dictionary helpers into DTO `From(...)` factory methods.
- Removed controller direct dependency on `OpenCodex.Api.Persistence`.
- Reduced `AdminWebSearchController` from 155 lines to 93 lines.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests.WebSearch|FullyQualifiedName~AdminWebSearchServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminWebSearchController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminWebSearchController.cs src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs
```

Result:

- Web-search targeted tests passed: 10 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 313 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.

Remaining risks:

- DTO factory methods still depend on persistence/service record types; a future Application/Contracts split may move these mapping boundaries.
- Request parsing for `id` and `query` remains in the controller as lightweight HTTP input handling; a future pass can introduce request DTOs if API validation policy becomes stricter.
- `AdminConfigController` and `AdminApiKeysController` still contain inline response dictionary shaping and should be reviewed next.

## Completed Architecture Layering Unit

### AdminApiKeys Controller Response DTO Extraction

Status: completed.

Goal:

- Continue controller-thinning work against `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md`.
- Remove inline API-key response dictionary construction from `AdminApiKeysController`.
- Preserve existing admin API-key response fields, including plaintext `key`, `masked_key`, key prefix/suffix, timestamps, and delete result shape.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminApiKeysController.cs`
- `src/OpenCodex.Api/DTOs/AdminApiKeys/AdminApiKeyResponses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added explicit API-key response DTOs under `DTOs/AdminApiKeys`.
  - `ApiKeysResponse`
  - `ApiKeyResponsePayload`
  - `AccessApiKeyResponse`
  - `DeleteApiKeyResponse`
- Added `JsonPropertyName` annotations to preserve existing snake_case response fields.
- Moved response mapping from controller private dictionary helper into DTO `From(...)` factory methods.
- Removed controller direct dependency on `OpenCodex.Api.Persistence`.
- Reduced `AdminApiKeysController` from 126 lines to 96 lines.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests.ApiKeys|FullyQualifiedName~AdminDataControllerTests.RegularUserApiKeyManagementIsScopedToSelf|FullyQualifiedName~AdminApiKeyServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminApiKeysController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminApiKeys/AdminApiKeyResponses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminApiKeysController.cs src/OpenCodex.Api/DTOs/AdminApiKeys/AdminApiKeyResponses.cs
```

Result:

- API-key targeted tests passed: 7 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 313 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.

Remaining risks:

- API-key response DTOs still map directly from persistence records; if Application/Contracts projects are split later, this mapping boundary may move.
- API key request bodies are still raw dictionaries handled by service methods; request DTO validation remains a future API design/validation pass.
- `AdminConfigController` still contains inline config/channel response dictionary shaping and is the next natural controller-thinning target.

## Completed Architecture Layering Unit

### AdminConfig Controller Response DTO Extraction

Status: completed.

Goal:

- Continue controller-thinning work against `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md`.
- Remove inline config/channel response dictionary construction from `AdminConfigController`.
- Preserve existing admin config JSON contracts and export behavior:
  - channel fields stay `baseurl`, `apikey`, `auth_mode`, `timeout_seconds`, `retry_count`, etc.
  - config export remains a JSON document with only top-level `channels`.
  - export keeps indented JSON and a trailing newline.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminConfigController.cs`
- `src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added explicit config response DTOs under `DTOs/AdminConfig`.
  - `ConfigResponse`
  - `ChannelResponse`
  - `ConfigImportResponse`
- Added `JsonPropertyName` annotations to preserve existing response and export field names.
- Moved response mapping from controller private dictionary helpers into DTO `From(...)` factory methods.
- Updated `/admin/api/config/export` to serialize `ConfigResponse` instead of a dictionary while preserving `WriteIndented = true` and trailing newline.
- Removed controller direct dependency on `OpenCodex.Api.Persistence`.
- Reduced `AdminConfigController` from 121 lines to 84 lines.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests.Config|FullyQualifiedName~AdminConfigServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminConfigController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminConfigController.cs src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs
```

Result:

- Config targeted tests passed: 10 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 313 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.

Remaining risks:

- Config response DTOs still map directly from persistence records; if Application/Contracts projects are split later, this mapping boundary may move.
- Config request/import bodies remain raw dictionaries because existing config normalization/validation is dictionary-based; request DTO validation remains a future API design pass.
- `AdminUsersController` still contains inline response dictionary shaping and should be reviewed next.

## Completed Architecture Layering Unit

### AdminUsers Controller Response DTO Extraction

Status: completed.

Goal:

- Continue controller-thinning work against `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md`.
- Remove inline user response dictionary construction from `AdminUsersController`.
- Preserve existing admin users response fields and shapes:
  - list response keeps top-level `users`.
  - create/update response keeps top-level `user`.
  - delete response keeps `deleted` plus deleted `user`.
  - user fields remain `username`, `role`, `enabled`, `created_at`, and `updated_at`.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminUsersController.cs`
- `src/OpenCodex.Api/DTOs/AdminUsers/AdminUserResponses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added explicit users response DTOs under `DTOs/AdminUsers`.
  - `UsersResponse`
  - `UserResponsePayload`
  - `DeleteUserResponse`
  - `UserResponse`
- Added `JsonPropertyName` annotations to preserve existing snake_case response fields.
- Moved response mapping from controller private dictionary helper into DTO `From(...)` factory methods.
- Removed controller direct dependency on `OpenCodex.Api.Persistence`.
- Reduced `AdminUsersController` from 107 lines to 82 lines.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests.Users|FullyQualifiedName~AdminDataControllerTests.RegularUserCannotUseSuperadminEndpoints|FullyQualifiedName~AdminUserServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminUsersController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminUsers/AdminUserResponses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminUsersController.cs src/OpenCodex.Api/DTOs/AdminUsers/AdminUserResponses.cs
```

Result:

- Users targeted tests passed: 7 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 313 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.

Remaining risks:

- User response DTOs still map directly from persistence records; if Application/Contracts projects are split later, this mapping boundary may move.
- User create/update request bodies remain raw dictionaries handled by service methods; request DTO validation remains a future API design pass.
- `AdminUiController` still owns fallback HTML generation inside the controller and should be reviewed against the thin-controller rule.

## Completed Architecture Layering Unit

### AdminUi Fallback HTML Moved To Service

Status: completed.

Goal:

- Continue controller-thinning work against `PROJECT_ARCHITECTURE.md`.
- Move admin login/fallback HTML generation out of `AdminUiController`.
- Keep the controller responsible for HTTP flow only: serving SPA files, reading form values, setting session, redirecting, and returning service-provided HTML.
- Preserve existing `/admin` fallback/login behavior and content type.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminUiController.cs`
- `src/OpenCodex.Api/Services/IAdminUiService.cs`
- `src/OpenCodex.Api/Services/AdminUiService.cs`
- `tests/OpenCodex.Api.Tests/Services/AdminUiServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `AdminUiHtmlResult` service result record.
- Added `IAdminUiService.GetLoginPage(...)` and `IAdminUiService.GetAdminFallbackPage()`.
- Moved login HTML, fallback HTML, and HTML encoding from controller into `AdminUiService`.
- Kept `AdminUiService` independent of MVC `ContentResult`; controller converts `AdminUiHtmlResult` to `Content(...)`.
- Added service tests for:
  - encoded login error HTML.
  - missing-frontend fallback HTML.
- Reduced `AdminUiController` from 129 lines to 74 lines.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminUiServiceTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminUiController.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminUiService.cs opencodex_proxy/src/OpenCodex.Api/Services/IAdminUiService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/AdminUiServiceTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminUiController.cs src/OpenCodex.Api/Services/AdminUiService.cs src/OpenCodex.Api/Services/IAdminUiService.cs tests/OpenCodex.Api.Tests/Services/AdminUiServiceTests.cs
```

Result:

- Admin UI service and smoke tests passed: 16 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 315 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source/test files.
- Trailing whitespace scan found no matches in touched source/test files.

Remaining risks:

- `AdminUiService` now owns both static asset lookup and fallback HTML generation; if HTML grows, a dedicated renderer/template component may be cleaner.
- `/admin` form parsing still lives in `AdminUiController`; this is currently lightweight HTTP input handling, but request DTO/form binding could be introduced later.
- Other controllers should still be reviewed for request parsing and raw dictionary request bodies.

## Completed Architecture Layering Unit

### AdminAuth Session Response DTO Extraction

Status: completed.

Goal:

- Continue controller response-shape cleanup against `PROJECT_ARCHITECTURE.md`.
- Remove inline session/login/logout response dictionaries from `AdminAuthController`.
- Keep session helper focused on session state rather than response serialization.
- Preserve existing auth/session JSON shapes:
  - `authenticated`
  - nullable `user`
  - `user.username`, `user.role`, `user.enabled`
  - login failure `{ "error": "..." }`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminAuthController.cs`
- `src/OpenCodex.Api/Controllers/AdminSession.cs`
- `src/OpenCodex.Api/DTOs/AdminAuth/AdminAuthResponses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added explicit auth/session response DTOs under `DTOs/AdminAuth`.
  - `AdminSessionResponse`
  - `AdminSessionUserResponse`
  - `AdminLoginErrorResponse`
- Replaced inline dictionaries in:
  - `GET /admin/api/session`
  - `POST /admin/api/login`
  - `POST /admin/logout`
  - `POST /admin/api/logout`
- Removed `AdminSession.UserToJson`; `AdminSession` now only handles session read/write/role checks.
- Reduced `AdminAuthController` from 81 lines to 68 lines.
- Reduced `AdminSession` from 57 lines to 42 lines.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests.AdminApiLogin|FullyQualifiedName~AdminApiLoginRejectsInvalidCredentials|FullyQualifiedName~AdminAuthServiceTests|FullyQualifiedName~SmokeTests.AdminFormLoginRedirectsAndLogoutAliasClearsSession" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminAuthController.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminSession.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminAuth/AdminAuthResponses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminAuthController.cs src/OpenCodex.Api/Controllers/AdminSession.cs src/OpenCodex.Api/DTOs/AdminAuth/AdminAuthResponses.cs
```

Result:

- Auth/session targeted tests passed: 6 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 315 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.

Remaining risks:

- Auth request body parsing remains raw dictionary-based in `AdminAuthController`; a future request DTO/form binding pass can tighten validation.
- `AdminChannelDiagnosticsController` still contains inline diagnostic response dictionaries and should be reviewed next.
- Shared admin error responses still use dictionary helpers in `AdminApiControllerBase`; those may be reasonable cross-cutting helpers, but can be revisited when standardizing admin API result shapes.

## Completed Architecture Layering Unit

### AdminChannelDiagnostics Response DTO Extraction

Status: completed.

Goal:

- Continue controller response-shape cleanup against `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md`.
- Remove inline diagnostic response dictionaries from `AdminChannelDiagnosticsController`.
- Keep diagnostic response DTOs in the API layer because they are HTTP-facing admin payloads, not reusable service/domain contracts.
- Preserve existing admin diagnostic JSON shapes:
  - discover models success: `models`, `raw`, `duration_ms`
  - discover models upstream failure: `error`, `status_code`, `duration_ms`, `body`
  - test channel success: `ok`, `duration_ms`, `model`, `upstream_model`, `compat`, `response`
  - test channel failure: `ok`, `status_code`, `duration_ms`, `error`, `body`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs`
- `src/OpenCodex.Api/DTOs/AdminChannelDiagnostics/AdminChannelDiagnosticsResponses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added explicit admin channel diagnostics response DTOs under `DTOs/AdminChannelDiagnostics`.
  - `DiscoverModelsResponse`
  - `DiscoverModelsErrorResponse`
  - `TestChannelResponse`
  - `TestChannelErrorResponse`
- Replaced inline response dictionaries in:
  - `POST /admin/api/channels/discover-models`
  - `POST /admin/api/discover-models`
  - `POST /admin/api/channels/test`
  - `POST /admin/api/test-channel`
- Preserved snake_case fields with `JsonPropertyName`.
- Kept `BadRequestError` helper usage unchanged for request-shape and config validation failures.
- Kept admin diagnostic success/error payloads unwrapped to preserve compatibility.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests.DiscoverModels|FullyQualifiedName~AdminDataControllerTests.ChannelTest|FullyQualifiedName~AdminChannelDiagnosticsServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminChannelDiagnostics/AdminChannelDiagnosticsResponses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs src/OpenCodex.Api/DTOs/AdminChannelDiagnostics/AdminChannelDiagnosticsResponses.cs
```

Result:

- Diagnostics targeted tests passed: 13 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 315 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.

Remaining risks:

- Request bodies for diagnostic endpoints still use raw dictionaries because they mirror flexible admin channel drafts; a future request DTO pass should decide whether this flexibility is intentional.
- `AdminApiControllerBase.BadRequestError` and `NotFoundError` still return small dictionary payloads; this may remain acceptable as shared compatibility helpers, but should be revisited when standardizing admin error shapes.
- `AdminChannelDiagnosticsController` still owns timing measurement; this is lightweight HTTP diagnostic metadata, but a future observability pass could centralize it if more endpoints need the same pattern.

## Completed Architecture Layering Unit

### Shared Admin Error Response DTO Extraction

Status: completed.

Goal:

- Continue API response-shape cleanup against `API_CODE_STYLE.md` and `API_RESULT_GUIDE.md`.
- Replace shared admin `{ "error": "..." }` dictionary helpers with an explicit DTO.
- Keep admin compatibility payloads unchanged as top-level string `error` values.
- Remove the remaining local `NotFound(new Dictionary<string, string> ...)` in `AdminObservabilityController`.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminApiControllerBase.cs`
- `src/OpenCodex.Api/Controllers/AdminObservabilityController.cs`
- `src/OpenCodex.Api/DTOs/Admin/AdminErrorResponses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added shared `AdminErrorResponse` under `DTOs/Admin`.
- Updated `AdminApiControllerBase.BadRequestError(...)` and `NotFoundError(...)` to return `AdminErrorResponse`.
- Updated log detail 404 in `AdminObservabilityController` to use the shared `NotFoundError(...)` helper.
- Verified no `new Dictionary<string, string>` / `NotFound(new Dictionary...)` / `BadRequest(new Dictionary...)` response construction remains in controllers.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminApiControllerBase.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminObservabilityController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/Admin/AdminErrorResponses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminApiControllerBase.cs src/OpenCodex.Api/Controllers/AdminObservabilityController.cs src/OpenCodex.Api/DTOs/Admin/AdminErrorResponses.cs
rg -n "new Dictionary<string, string>|NotFound\(new Dictionary|BadRequest\(new Dictionary" src/OpenCodex.Api/Controllers
```

Result:

- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 315 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.
- Controller dictionary-error scan found no remaining matches.

Remaining risks:

- Admin error responses intentionally remain compatibility-shaped as `{ "error": "..." }`, not `ApiResult` and not proxy-style `error.message`.
- `AdminLoginErrorResponse` still exists separately under `DTOs/AdminAuth`; it has the same JSON shape but is auth-specific. It could be unified later if the project decides shared admin errors should cover login failures too.
- Some service-layer dictionaries remain because they model flexible upstream/provider payloads rather than HTTP error wrappers.

## Completed Architecture Layering Unit

### AdminConfig Export Formatting Moved To API DTO

Status: completed.

Goal:

- Continue controller-thinning work against `PROJECT_ARCHITECTURE.md`, `API_CODE_STYLE.md`, and `CLASS_LIBRARY_GUIDE.md`.
- Remove hand-written JSON serialization from `AdminConfigController`.
- Keep business service boundaries clean: `AdminConfigService` still returns business data and does not depend on API DTOs or JSON export formatting.
- Preserve the existing export compatibility surface:
  - content type `application/json`
  - attachment filename `opencodex-channels-config.json`
  - formatted JSON
  - trailing newline
  - top-level `channels` only

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminConfigController.cs`
- `src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs`
- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ConfigExportResponse` in the API DTO layer.
- Centralized export payload generation, content type, and filename in `ConfigExportResponse`.
- Removed direct `JsonSerializer.Serialize(...)` usage from `AdminConfigController`.
- Kept `AdminConfigController.ExportConfig()` responsible only for user scope, response header, and returning content.
- Extended `ConfigExportIncludesFullApiKeyAndOnlyChannels` to assert formatted JSON and trailing newline.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests.Config|FullyQualifiedName~AdminConfigServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminConfigController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs opencodex_proxy/tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminConfigController.cs src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs
rg -n "JsonSerializer.Serialize|WriteIndented" src/OpenCodex.Api/Controllers src/OpenCodex.Api/DTOs/AdminConfig tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs
```

Result:

- Config targeted tests passed: 10 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 315 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source/test files.
- Trailing whitespace scan found no matches in touched source/test files.
- Serialization scan confirmed config export JSON formatting no longer lives in controllers.

Remaining risks:

- `ConfigExportResponse` is an API-layer DTO with formatting behavior. This keeps service boundaries clean, but if more export formats are added, a dedicated API formatter component may be clearer.
- The export endpoint still returns raw file content, not `ApiResult`, by design for download compatibility.
- Config save/import request bodies remain raw dictionaries; a later request DTO pass should decide whether to preserve that flexibility or tighten validation.

## Completed Architecture Layering Unit

### AdminObservability Log Filter Query Extraction

Status: completed.

Goal:

- Continue controller-thinning work against `API_CODE_STYLE.md`.
- Move log filter key ownership and query-to-filter construction out of `AdminObservabilityController`.
- Keep the transformation in the API layer because it is HTTP query parsing, not business logic.
- Preserve existing log list and filter option behavior:
  - known filter keys are forwarded
  - empty filter values are ignored
  - filter option endpoint excludes the currently selected field
  - service layer still applies owner scoping

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminObservabilityController.cs`
- `src/OpenCodex.Api/DTOs/AdminObservability/LogFilterQuery.cs`
- `tests/OpenCodex.Api.Tests/DTOs/AdminObservability/LogFilterQueryTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `LogFilterQuery.FromQuery(...)` in the API DTO layer.
- Moved log filter key list out of `AdminObservabilityController`.
- Removed `AdminObservabilityController.LogFilters(...)`.
- Updated logs and log-filter-options actions to pass `Request.Query` through `LogFilterQuery`.
- Added a DTO-level test for allowed keys, empty values, unknown keys, and excluded field behavior.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~LogFilterQueryTests|FullyQualifiedName~AdminDataControllerTests.Log|FullyQualifiedName~AdminObservabilityServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminObservabilityController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminObservability/LogFilterQuery.cs opencodex_proxy/tests/OpenCodex.Api.Tests/DTOs/AdminObservability/LogFilterQueryTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminObservabilityController.cs src/OpenCodex.Api/DTOs/AdminObservability/LogFilterQuery.cs tests/OpenCodex.Api.Tests/DTOs/AdminObservability/LogFilterQueryTests.cs
rg -n "LogFilterKeys|Dictionary<string, object\?>|new Dictionary<string, object\?>|JsonSerializer.Serialize|new Dictionary<string, string>" src/OpenCodex.Api/Controllers/AdminObservabilityController.cs src/OpenCodex.Api/Controllers
```

Result:

- Observability targeted tests passed: 9 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 316 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source/test files.
- Trailing whitespace scan found no matches in touched source/test files.
- Controller scan confirmed `AdminObservabilityController` no longer owns log filter keys or dictionary construction.

Remaining risks:

- `LogFilterQuery` currently returns a dictionary because `IAdminObservabilityService` and repository filters still use dictionary-based flexible filters.
- If log filtering becomes a stable contract, the next stronger step is a typed filter model in Application/DataAccess rather than a dictionary.
- Other controllers still contain small request helper methods (`AdminAuthController.StringValue`, `AdminWebSearchController.StringValue/GetValue`) and should be reviewed next.

## Completed Architecture Layering Unit

### Admin Auth And Web Search Request Parsing DTO Extraction

Status: completed.

Goal:

- Continue controller-thinning work against `API_CODE_STYLE.md`.
- Remove remaining controller-local request parsing helpers from `AdminAuthController` and `AdminWebSearchController`.
- Keep parsing behavior in the API DTO layer because it converts raw HTTP body dictionaries into request-specific values.
- Preserve existing compatibility behavior:
  - login string values are trimmed
  - login primitive values such as numbers and booleans are converted to strings
  - login missing/complex values become empty strings
  - web-search test-key invalid id returns `id is required`
  - web-search test-key blank query defaults to `OpenAI`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminAuthController.cs`
- `src/OpenCodex.Api/Controllers/AdminWebSearchController.cs`
- `src/OpenCodex.Api/DTOs/AdminAuth/AdminLoginRequest.cs`
- `src/OpenCodex.Api/DTOs/AdminWebSearch/WebSearchTestKeyRequest.cs`
- `tests/OpenCodex.Api.Tests/DTOs/AdminAuth/AdminLoginRequestTests.cs`
- `tests/OpenCodex.Api.Tests/DTOs/AdminWebSearch/WebSearchTestKeyRequestTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `AdminLoginRequest.From(...)`.
- Added `WebSearchTestKeyRequest.From(...)`.
- Removed `AdminAuthController.StringValue(...)`.
- Removed `AdminWebSearchController.GetValue(...)` and `StringValue(...)`.
- Added DTO-level tests for parsing compatibility and default behavior.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminLoginRequestTests|FullyQualifiedName~WebSearchTestKeyRequestTests|FullyQualifiedName~AdminDataControllerTests.AdminApiLogin|FullyQualifiedName~AdminDataControllerTests.WebSearchTestKey|FullyQualifiedName~AdminAuthServiceTests|FullyQualifiedName~AdminWebSearchServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminAuthController.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminWebSearchController.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminAuth/AdminLoginRequest.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminWebSearch/WebSearchTestKeyRequest.cs opencodex_proxy/tests/OpenCodex.Api.Tests/DTOs/AdminAuth/AdminLoginRequestTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/DTOs/AdminWebSearch/WebSearchTestKeyRequestTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminAuthController.cs src/OpenCodex.Api/Controllers/AdminWebSearchController.cs src/OpenCodex.Api/DTOs/AdminAuth/AdminLoginRequest.cs src/OpenCodex.Api/DTOs/AdminWebSearch/WebSearchTestKeyRequest.cs tests/OpenCodex.Api.Tests/DTOs/AdminAuth/AdminLoginRequestTests.cs tests/OpenCodex.Api.Tests/DTOs/AdminWebSearch/WebSearchTestKeyRequestTests.cs
rg -n "private static object\? GetValue|private static string StringValue|StringValue\(|GetValue\(" src/OpenCodex.Api/Controllers
```

Result:

- Auth/web-search targeted tests passed: 21 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 323 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source/test files.
- Trailing whitespace scan found no matches in touched source/test files.
- Controller scan found no controller-local `StringValue` / `GetValue` helper methods remaining.

Remaining risks:

- `AdminLoginRequest` and `WebSearchTestKeyRequest` still parse from raw dictionaries because `IRequestBodyReader` currently returns flexible dictionary bodies.
- Save endpoints for config, users, API keys, and web-search still pass raw dictionaries into services; request DTO standardization remains a larger follow-up.
- `AdminApiControllerBase.QueryValue(...)` remains as a lightweight shared query accessor for simple parameters.

## Completed Architecture Layering Unit

### Shared JSON Dictionary Value Helper Extraction

Status: completed.

Goal:

- Continue service-layer cleanup against `CLASS_LIBRARY_GUIDE.md` and `API_CODE_STYLE.md`.
- Remove duplicated JSON-like dictionary value helpers from admin services.
- Keep dynamic dictionary payload boundaries intact for flexible config/channel/upstream data.
- Place the helper in `Infrastructure` as a reusable technical utility, not in controllers or domain logic.

Implemented files:

- `src/OpenCodex.Api/Infrastructure/JsonDictionaryValue.cs`
- `src/OpenCodex.Api/Services/AdminUserService.cs`
- `src/OpenCodex.Api/Services/AdminApiKeyService.cs`
- `src/OpenCodex.Api/Services/AdminConfigService.cs`
- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs`
- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.ChannelDraft.cs`
- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Compat.cs`
- `src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Values.cs`
- `tests/OpenCodex.Api.Tests/Infrastructure/JsonDictionaryValueTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added shared `JsonDictionaryValue` helper for:
  - raw value lookup
  - trimmed string value lookup
  - list value lookup
  - object value lookup with caller-provided clone behavior
- Replaced duplicated `Value` / `GetValue` / `StringValue` / `ListValue` / `ObjectValue` helpers in:
  - `AdminUserService`
  - `AdminApiKeyService`
  - `AdminConfigService`
  - `AdminChannelDiagnosticsService` partial files
- Kept diagnostic-specific `ToInt`, clone, and protocol behavior local to `AdminChannelDiagnosticsService`.
- Added infrastructure helper tests for missing values, trimming, list handling, scalar fallback, object cloning, and missing object fallback.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~JsonDictionaryValueTests|FullyQualifiedName~AdminUserServiceTests|FullyQualifiedName~AdminApiKeyServiceTests|FullyQualifiedName~AdminConfigServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Infrastructure/JsonDictionaryValue.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminUserService.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminApiKeyService.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminConfigService.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.ChannelDraft.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Compat.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Values.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Infrastructure/JsonDictionaryValueTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Infrastructure/JsonDictionaryValue.cs src/OpenCodex.Api/Services/AdminUserService.cs src/OpenCodex.Api/Services/AdminApiKeyService.cs src/OpenCodex.Api/Services/AdminConfigService.cs src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.cs src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.ChannelDraft.cs src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Compat.cs src/OpenCodex.Api/Services/AdminChannelDiagnosticsService.Values.cs tests/OpenCodex.Api.Tests/Infrastructure/JsonDictionaryValueTests.cs
rg -n "private static object\? (GetValue|Value)|private static string StringValue|private static List<object\?> ListValue|private static Dictionary<string, object\?> ObjectValue" src/OpenCodex.Api/Services/AdminUserService.cs src/OpenCodex.Api/Services/AdminApiKeyService.cs src/OpenCodex.Api/Services/AdminConfigService.cs src/OpenCodex.Api/Services/AdminChannelDiagnosticsService*.cs
```

Result:

- Targeted service/helper tests passed: 28 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 326 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source/test files.
- Trailing whitespace scan found no matches in touched source/test files.
- Targeted duplicate-helper scan found no remaining local value helper methods in the touched admin services.

Remaining risks:

- Other protocol-heavy services still contain their own value helpers; some are intentionally protocol-specific and should be reviewed separately before consolidation.
- `JsonDictionaryValue.Object(...)` delegates cloning to callers to avoid imposing one clone policy globally.
- This cleanup reduces duplication but does not make config/channel/upstream payloads strongly typed.

## Completed Architecture Layering Unit

### Proxy And Upstream Trimmed Dictionary Value Helper Reuse

Status: completed.

Goal:

- Continue service-layer cleanup against `CLASS_LIBRARY_GUIDE.md` and `API_CODE_STYLE.md`.
- Reuse the shared `JsonDictionaryValue` helper for proxy/upstream services that already had the same trimmed string / raw value semantics.
- Avoid changing web-search payload helpers because those intentionally keep non-trimmed string behavior.
- Preserve existing proxy/upstream behavior:
  - request model and channel metadata are trimmed strings
  - upstream base URL, auth mode, API key, channel type, and channel id are trimmed strings
  - timeout/retry values still use existing numeric fallback rules
  - stream flag checks still use explicit payload `TryGetValue`

Implemented files:

- `src/OpenCodex.Api/Services/ProxyEndpointService.cs`
- `src/OpenCodex.Api/Services/ProxyLogService.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Streaming.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Requests.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Responses.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Replaced proxy endpoint request/channel string extraction with `JsonDictionaryValue.String(...)`.
- Replaced proxy log upstream response model extraction with `JsonDictionaryValue.String(...)`.
- Replaced HTTP upstream channel value extraction with `JsonDictionaryValue.String(...)` and `JsonDictionaryValue.Get(...)`.
- Removed local `GetValue` / `StringValue` helper methods from `HttpUpstreamClient.Requests`.
- Left `IWebSearchClient` and `WebSearchPayload` helpers untouched because their no-trim behavior is protocol-specific.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyLogServiceTests|FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/ProxyEndpointService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyLogService.cs opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.cs opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.Streaming.cs opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.Requests.cs opencodex_proxy/src/OpenCodex.Api/Services/HttpUpstreamClient.Responses.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/ProxyEndpointService.cs src/OpenCodex.Api/Services/ProxyLogService.cs src/OpenCodex.Api/Services/HttpUpstreamClient.cs src/OpenCodex.Api/Services/HttpUpstreamClient.Streaming.cs src/OpenCodex.Api/Services/HttpUpstreamClient.Requests.cs src/OpenCodex.Api/Services/HttpUpstreamClient.Responses.cs
rg -n "private static object\? GetValue|private static string StringValue|StringValue\(|GetValue\(" src/OpenCodex.Api/Services/ProxyEndpointService.cs src/OpenCodex.Api/Services/ProxyLogService.cs src/OpenCodex.Api/Services/HttpUpstreamClient*.cs
```

Result:

- Proxy/upstream targeted tests passed: 21 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 326 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source files.
- Trailing whitespace scan found no matches in touched source files.
- Targeted helper scan found no local `GetValue` / `StringValue` helper methods remaining in the touched proxy/upstream services.

Remaining risks:

- `IWebSearchClient` and `WebSearchPayload` still own value helpers because they intentionally preserve non-trimmed strings and broader read-only dictionary/list conversion behavior.
- This pass reduces duplicate helper code but does not strongly type proxy/upstream channel dictionaries.
- Existing protocol tests cover key behavior, but there are no dedicated tests for whitespace trimming in upstream channel config values.

## Completed Architecture Layering Unit

### Upstream Channel Config Trimming Regression Coverage

Status: completed.

Goal:

- Strengthen test coverage after proxy/upstream services started reusing `JsonDictionaryValue`.
- Explicitly lock down whitespace trimming behavior for upstream channel config values.
- Preserve existing HTTP upstream behavior:
  - `type` with surrounding whitespace still routes to the correct endpoint
  - `baseurl` with surrounding/trailing whitespace still builds the correct URL
  - `auth_mode` and `apikey` with surrounding whitespace still produce the expected authorization header
  - `id` with surrounding whitespace is trimmed in upstream exceptions

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `PostJsonAsyncTrimsChannelConfigValues`.
- The test sends a channel with whitespace around `id`, `type`, `baseurl`, `auth_mode`, and `apikey`.
- The test verifies:
  - request URI is `https://upstream.test/v1/chat/completions`
  - authorization header is `Bearer secret`
  - exception channel id is `primary`

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~HttpUpstreamClientTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs
```

Result:

- HttpUpstreamClient targeted tests passed: 13 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 327 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched test file.
- Trailing whitespace scan found no matches in touched test file.

Remaining risks:

- This is a regression coverage pass only; no production code changed in this unit.
- Streaming upstream requests share the same channel extraction helpers, but the new test only exercises non-streaming POST.
- Header dictionary values supplied through `headers` are still not trimmed, preserving the existing custom-header behavior.

## Completed Architecture Layering Unit

### Tavily Web Search Payload Helper Reuse

Status: completed.

Goal:

- Continue web-search service cleanup against `CLASS_LIBRARY_GUIDE.md` and `API_CODE_STYLE.md`.
- Remove duplicate JSON element and dictionary helper methods from `TavilyWebSearchClient`.
- Reuse `WebSearchPayload` because it already owns web-search payload semantics.
- Preserve protocol-specific behavior:
  - Tavily summary strings are not trimmed
  - non-object result items are ignored
  - HTTP error JSON bodies are decoded into raw payload dictionaries
  - request payload and authorization header remain unchanged

Implemented files:

- `src/OpenCodex.Api/Services/IWebSearchClient.cs`
- `tests/OpenCodex.Api.Tests/Services/TavilyWebSearchClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Replaced Tavily client-local `FromJsonElement`, `TryAsObject`, `TryAsList`, `GetValue`, and `StringValue` helpers with `WebSearchPayload`.
- Kept `TavilyWebSearchClient` focused on HTTP request/response handling and summary mapping.
- Added `TavilyWebSearchClientTests`.
- Covered:
  - request URL and authorization header
  - Tavily request JSON payload
  - successful summary extraction
  - non-trimmed answer/title/url/content values
  - HTTP error raw JSON decoding

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~TavilyWebSearchClientTests|FullyQualifiedName~AdminWebSearchServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~WebSearchRequestPolicyTests|FullyQualifiedName~WebSearchToolCallParserTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/IWebSearchClient.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/TavilyWebSearchClientTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/IWebSearchClient.cs tests/OpenCodex.Api.Tests/Services/TavilyWebSearchClientTests.cs
rg -n "private static object\? GetValue|private static string StringValue|private static bool TryAsObject|private static bool TryAsList|private static object\? FromJsonElement" src/OpenCodex.Api/Services/IWebSearchClient.cs
```

Result:

- Web-search targeted tests passed: 39 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 329 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched source/test files.
- Trailing whitespace scan found no matches in touched source/test files.
- Tavily client duplicate-helper scan found no remaining local JSON/dictionary helper methods.

Remaining risks:

- `WebSearchPayload` is still an internal shared service helper rather than a separate class library utility.
- Tavily client now depends on `WebSearchPayload` behavior; this is intentional because both belong to web-search payload handling.
- Timeout and request-error branches in `TavilyWebSearchClient` remain covered indirectly by existing simulator/service tests, not by the new direct Tavily client tests.

## Completed Architecture Layering Unit

### Admin API Key Service Command Boundary

Status: completed.

Goal:

- Continue service boundary cleanup against `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md`.
- Stop passing raw HTTP request-body dictionaries into `AdminApiKeyService`.
- Give the API key service its own input contracts so API DTO/body parsing stays in the API layer and service rules stay in the service layer.
- Preserve admin API compatibility:
  - routes remain unchanged
  - response payload shapes remain unchanged
  - non-superadmin create still forces `owner_username` to the current user
  - superadmin blank owner still falls back to the current user
  - PATCH `enabled` keeps the previous `is true` behavior

Implemented files:

- `src/OpenCodex.Api/Services/IAdminApiKeyService.cs`
- `src/OpenCodex.Api/Services/AdminApiKeyService.cs`
- `src/OpenCodex.Api/Controllers/AdminApiKeysController.cs`
- `tests/OpenCodex.Api.Tests/Services/AdminApiKeyServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `AdminApiKeyCreateCommand` and `AdminApiKeyUpdateCommand` in the service namespace.
- Updated `IAdminApiKeyService.CreateKey` and `UpdateKey` to accept service commands instead of raw body dictionaries.
- Moved API key body field extraction to `AdminApiKeysController`.
- Kept string trimming at the service boundary so non-HTTP callers get the same normalized behavior.
- Added service tests covering command trimming and blank-owner fallback.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminApiKeyServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
rg -n "CreateKey\(|UpdateKey\(|IReadOnlyDictionary<string, object\?> body" src/OpenCodex.Api tests/OpenCodex.Api.Tests
rg -n "[ \t]+$" src/OpenCodex.Api/Services/IAdminApiKeyService.cs src/OpenCodex.Api/Services/AdminApiKeyService.cs src/OpenCodex.Api/Controllers/AdminApiKeysController.cs tests/OpenCodex.Api.Tests/Services/AdminApiKeyServiceTests.cs
```

Result:

- Admin API key service tests passed: 7 passed, 0 failed, 0 skipped.
- Admin data controller and smoke tests passed: 32 passed, 0 failed, 0 skipped.
- API key service scan shows no raw body dictionary parameters remain on `CreateKey` / `UpdateKey`.
- Trailing whitespace scan found no matches in touched source/test files.

Remaining risks:

- `AdminUsersService` still accepts raw request-body dictionaries and should be the next similar service-boundary cleanup candidate.
- `AdminConfigService`, `AdminChannelDiagnosticsService`, and `AdminWebSearchService` still accept dictionaries by design for dynamic compatibility-heavy payloads; do not command-ify them without a narrower compatibility plan.
- There is no new controller-only test for `enabled` string/non-bool values; existing admin data tests cover the main create/list/disable/delete flow.

## Completed Architecture Layering Unit

### Admin Users Service Command Boundary

Status: completed.

Goal:

- Continue service boundary cleanup against `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md`.
- Stop passing raw HTTP request-body dictionaries into `AdminUserService`.
- Use service-level commands for user create/update so API body parsing stays in the Controller and service business semantics stay in the Service layer.
- Preserve admin API compatibility:
  - routes remain unchanged
  - response payload shapes remain unchanged
  - create `enabled` keeps the previous behavior where only explicit `false` disables the user
  - PATCH `enabled` only updates enabled state when the field is present
  - PATCH `password` only resets password when the field is present
  - environment superadmin password reset remains blocked

Implemented files:

- `src/OpenCodex.Api/Services/IAdminUserService.cs`
- `src/OpenCodex.Api/Services/AdminUserService.cs`
- `src/OpenCodex.Api/Controllers/AdminUsersController.cs`
- `tests/OpenCodex.Api.Tests/Services/AdminUserServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `AdminUserCreateCommand` and `AdminUserUpdateCommand` in the service namespace.
- Updated `IAdminUserService.CreateUser` and `UpdateUser` to accept service commands instead of raw body dictionaries.
- Moved admin user body field extraction to `AdminUsersController`.
- Kept string trimming at the service boundary for create username/password and update password.
- Used nullable command fields to preserve PATCH field-presence semantics.
- Added service tests for update enabled, password trim, and omitted field behavior.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminUserServiceTests" --logger "console;verbosity=minimal"
rg -n "CreateUser\(new Dictionary|UpdateUser\([^\n]*new Dictionary|IReadOnlyDictionary<string, object\?> body" src/OpenCodex.Api/Services src/OpenCodex.Api/Controllers tests/OpenCodex.Api.Tests/Services/AdminUserServiceTests.cs
```

Result:

- Admin user service tests passed: 7 passed, 0 failed, 0 skipped.
- Users service/controller scan shows no raw body dictionary parameters remain on `CreateUser` / `UpdateUser`.
- Remaining raw body dictionaries are in dynamic compatibility-heavy services: config, channel diagnostics, and web-search.

Remaining risks:

- There is no new controller-only test for non-bool `enabled`; admin data controller tests cover the normal user management flow.
- `AdminUserCreateCommand` and `AdminUserUpdateCommand` are still inside the API project service namespace; if/when Application class libraries are split out, these service contracts should move with the service layer.
- Config, diagnostics, and web-search should not be command-ified casually because their payloads intentionally mirror flexible Python-compatible JSON bodies.

## Next Task Memory

User instruction:

- Next task should specifically inspect the code class-library structure and architecture layering documents.
- Start from `CLASS_LIBRARY_GUIDE.md`, `PROJECT_ARCHITECTURE.md`, and `NEW_PROJECT_CHECKLIST.md`.
- Do not continue blindly migrating business modules before checking whether the current single-project layout should be reshaped or documented against the class-library/layering rules.

Suggested next unit:

- Perform a class-library/layering audit of `opencodex_proxy/src/OpenCodex.Api`.
- Compare current folders and dependencies against the documented layers:
  - API / Controllers / DTOs
  - Services / Application-style use cases
  - Persistence / DataAccess
  - Infrastructure
  - External integrations
  - Domain-like models/constants
- Produce a concrete next-step plan in `MIGRATION_PROGRESS.tmp.md` before code changes.

## Completed Architecture Layering Unit

### Class Library and Layering Audit

Status: completed.

Goal:

- Follow the user instruction to inspect code class-library structure and architecture layering documents before continuing feature/module migration.
- Use `CLASS_LIBRARY_GUIDE.md`, `PROJECT_ARCHITECTURE.md`, and `NEW_PROJECT_CHECKLIST.md` as the decision baseline.
- Produce a concrete next-step plan before further source-code changes.
- Avoid blindly splitting projects before current folder/namespace responsibilities are clear.

Evidence inspected:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`
- `src/OpenCodex.Api/OpenCodex.Api.csproj`
- `tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj`
- `src/OpenCodex.Api/Program.cs`
- `src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs`
- Folder and file inventory under `src/OpenCodex.Api`
- Namespace/using scans across Controller, DTO, Service, Persistence, Config, Protocol, Routing, Infrastructure, and Errors layers

Current project shape:

- Solution currently has one Web API project and one test project:
  - `src/OpenCodex.Api/OpenCodex.Api.csproj`
  - `tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj`
- `OpenCodex.Api` is `Microsoft.NET.Sdk.Web`, `net10.0`, nullable enabled, implicit usings enabled.
- Main package dependencies are:
  - `Microsoft.Data.Sqlite`
  - `Swashbuckle.AspNetCore`
- Current source-folder counts:
  - `Services`: 77 files
  - `Persistence`: 40 files
  - `Protocols`: 16 files
  - `DTOs`: 15 files
  - `Controllers`: 13 files
  - `Config`: 6 files
  - `Configuration`: 6 files
  - `Infrastructure`: 5 files
  - `Errors`: 5 files
  - `Routing`: 2 files

Positive findings:

- Startup is thin: `Program.cs` delegates configuration, service registration, and middleware setup to infrastructure extensions.
- DI registration is centralized in `OpenCodexServiceCollectionExtensions`.
- Controllers mostly depend on DTOs and Services rather than repositories.
- Persistence does not depend on Controllers.
- SQLite-specific code is localized under `Persistence/OpenCodexDatabase.*`.
- External SDK types are not broadly leaked; current external access is mostly plain `HttpClient`.
- Recent command-boundary work reduced raw HTTP-body leakage into `AdminApiKeyService` and `AdminUserService`.

Layering gaps found:

- Shared business/read-model records are currently declared in `Persistence/OpenCodexDatabase.Records.cs`.
  - Examples: `ChannelRecord`, `UserRecord`, `AccessApiKeyRecord`, `AuthenticatedAccessApiKeyRecord`, `TavilyKeyRecord`, `WebSearchConfigRecord`, `RequestLogRecord`, `StatsRecord`.
  - These types are used by Services, DTOs, repositories, tests, and external/web-search flows.
  - This makes Application/API layers depend on a `Persistence` namespace for non-persistence concepts.
  - This is the strongest next cleanup candidate because it directly contradicts the class-library guide's DTO/Entity/Service contract placement rules.
- `ServiceResult` is currently under `DTOs/Results` but is used by Services and service interfaces.
  - `ApiResult` belongs in API/DTO land.
  - `ServiceResult` belongs closer to Application/Services, or later to an Application/Abstractions library.
  - Current placement causes business services to depend on a DTO namespace.
- External integration implementations live in `Services`.
  - `HttpUpstreamClient` and `TavilyWebSearchClient` are concrete external clients.
  - Per `PROJECT_ARCHITECTURE.md`, these should eventually move toward an ExternalIntegrations boundary.
  - Their interfaces may remain as service/application abstractions until a real class library split happens.
- Some proxy services are intentionally Host/API-coupled.
  - Examples use `HttpRequest`, `HttpResponse`, `StatusCodes`, or response streaming helpers.
  - This is acceptable while they are API-app-specific orchestration, but they should not be moved into a pure Application class library unchanged.
- `Config` is a mixed boundary.
  - It contains config parsing/normalization/routing-compatible value logic.
  - It is used by Persistence, Services, Routing, and Admin diagnostics.
  - It needs separate review before moving because it may represent domain-level config behavior, not just infrastructure.

Decision:

- Do not immediately create multiple class library projects.
- First make folder/namespace responsibilities explicit inside the current project.
- After the internal boundaries are clean and verified, consider physical project split.
- This avoids project-reference churn while the migration is still preserving Python compatibility behavior.

Recommended next code units:

1. Move shared record types out of `Persistence`.
   - Create a Domain-style location such as `src/OpenCodex.Api/Domain`.
   - Move records from `Persistence/OpenCodexDatabase.Records.cs` into that layer.
   - Update Services, DTOs, Persistence, and tests to depend on the Domain namespace instead of Persistence for these records.
   - Keep SQLite readers/writers in Persistence.
   - Verification: full test suite, plus a scan that DTOs/Services no longer import `OpenCodex.Api.Persistence` only to access record types.
2. Move `ServiceResult` out of `DTOs/Results`.
   - Keep `ApiResult` in DTOs/API response territory.
   - Move `ServiceResult` and service error result helpers toward Services/Application-style results.
   - Update service interfaces and tests.
   - Verification: service tests, admin/smoke tests, full test suite.
3. Separate ExternalIntegrations folder boundary.
   - Move `HttpUpstreamClient` and `TavilyWebSearchClient` implementations out of Services.
   - Keep `IUpstreamClient`, `IUpstreamModelClient`, and `IWebSearchClient` near service/application abstractions unless a dedicated Abstractions project is introduced.
   - Update DI registration.
   - Verification: upstream/web-search tests, admin channel diagnostics tests, smoke tests, full test suite.
4. Review Host-coupled proxy services.
   - Mark or isolate types that legitimately depend on `HttpRequest`/`HttpResponse`.
   - Do not move them into pure Application until their HTTP dependencies are abstracted.
5. Only after the previous units, decide whether to split projects:
   - `OpenCodex.Domain`
   - `OpenCodex.Application`
   - `OpenCodex.DataAccess`
   - `OpenCodex.Infrastructure`
   - `OpenCodex.ExternalIntegrations`
   - `OpenCodex.Api` as Host/API

Suggested immediate next unit:

- Move shared record types from `Persistence/OpenCodexDatabase.Records.cs` to a Domain-style folder/namespace.
- This is the highest-value next step because it reduces false `Persistence` dependencies across DTOs and Services without changing runtime behavior.

Verification commands used for this audit:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet sln OpenCodex.sln list
find src/OpenCodex.Api -maxdepth 3 -type f -name '*.cs' | sort
rg -n "using OpenCodex\.Api\.(Controllers|DTOs|Services|Persistence|Infrastructure|Configuration|Config|Routing|Protocols|Errors)" src/OpenCodex.Api/Persistence src/OpenCodex.Api/Config src/OpenCodex.Api/Configuration src/OpenCodex.Api/Routing src/OpenCodex.Api/Protocols src/OpenCodex.Api/Services src/OpenCodex.Api/Controllers
rg -n "IActionResult|ActionResult|HttpContext|HttpRequest|HttpResponse|ControllerBase|StatusCodes|Microsoft\.AspNetCore" src/OpenCodex.Api/Services src/OpenCodex.Api/Persistence src/OpenCodex.Api/Config src/OpenCodex.Api/Protocols src/OpenCodex.Api/Routing
rg -n "using OpenCodex\.Api\.Persistence" src/OpenCodex.Api/DTOs src/OpenCodex.Api/Services src/OpenCodex.Api/Controllers
rg -n "using OpenCodex\.Api\.DTOs\.Results" src/OpenCodex.Api/Services src/OpenCodex.Api/Persistence src/OpenCodex.Api/Controllers
```

Remaining risks:

- This audit is documentation/planning only; it does not yet change source-code boundaries.
- Moving shared records will touch many files because current tests and services instantiate persistence-namespaced records directly.
- Choosing `Domain` as the target folder is a pragmatic single-project step; if a later multi-project split uses different names, namespaces may need another mechanical update.

## Completed Architecture Layering Unit

### Domain Record Boundary Extraction

Status: completed.

Goal:

- Implement the highest-priority follow-up from the class-library/layering audit.
- Move shared business/read-model records out of the DataAccess/Persistence namespace.
- Make DTOs and Services depend on a Domain-style namespace for shared records instead of depending on `OpenCodex.Api.Persistence`.
- Preserve runtime behavior, database access, route compatibility, and response shapes.

Implemented files:

- `src/OpenCodex.Api/Domain/Records.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.Records.cs` deleted
- DTO response files under:
  - `src/OpenCodex.Api/DTOs/AdminApiKeys`
  - `src/OpenCodex.Api/DTOs/AdminUsers`
  - `src/OpenCodex.Api/DTOs/AdminConfig`
  - `src/OpenCodex.Api/DTOs/AdminWebSearch`
  - `src/OpenCodex.Api/DTOs/AdminObservability`
- Service interfaces/results/implementations that use shared records
- Persistence interfaces/implementations and `OpenCodexDatabase.*` partials that read/write shared records
- Tests that instantiate shared records
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodex.Api.Domain` namespace with the existing shared records:
  - `ChannelRecord`
  - `UserRecord`
  - `AccessApiKeyRecord`
  - `AccessApiKeyUserRecord`
  - `AuthenticatedAccessApiKeyRecord`
  - `TavilyKeyRecord`
  - `WebSearchConfigRecord`
  - `UsageRecord`
  - `RequestLogRecord`
  - `RequestLogEventRecord`
  - `RequestLogPageRecord`
  - `StatsPointRecord`
  - `StatsSummaryRecord`
  - `ModelDistributionRecord`
  - `StatsRecord`
- Removed the old `Persistence/OpenCodexDatabase.Records.cs` definition file.
- Updated DTOs to use `OpenCodex.Api.Domain` instead of `OpenCodex.Api.Persistence`.
- Updated Services to use Domain records while keeping real repository dependencies on Persistence.
- Updated Persistence to depend on Domain for returned/read records.
- Updated tests to import Domain records explicitly.
- Removed an obsolete `OpenCodex.Api.Persistence` using from `ProxyRequestService`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-restore --logger "console;verbosity=minimal"
rg -n "using OpenCodex\.Api\.Persistence" src/OpenCodex.Api/DTOs
rg -n "public sealed record (ChannelRecord|UserRecord|AccessApiKeyRecord|AccessApiKeyUserRecord|AuthenticatedAccessApiKeyRecord|TavilyKeyRecord|WebSearchConfigRecord|UsageRecord|RequestLogRecord|RequestLogEventRecord|RequestLogPageRecord|StatsPointRecord|StatsSummaryRecord|ModelDistributionRecord|StatsRecord)" src/OpenCodex.Api/Persistence src/OpenCodex.Api/Domain
for f in $(rg -l "using OpenCodex\.Api\.Persistence" src/OpenCodex.Api/Services | sort); do echo "---" $f; rg -n "I[A-Za-z]+Repository|Repository" $f; done
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- DTO scan shows no remaining `using OpenCodex.Api.Persistence`.
- Shared record definitions now appear only under `src/OpenCodex.Api/Domain/Records.cs`.
- Services that still import `OpenCodex.Api.Persistence` do so for real repository dependencies, not only for shared records.

Remaining risks:

- This is a namespace/folder boundary cleanup inside the current single Web API project, not a physical class-library split yet.
- Tests and services now depend on `OpenCodex.Api.Domain`; a later physical `OpenCodex.Domain` project split will require a project-reference step.
- Some Domain records are read models rather than rich domain entities; this is acceptable for the current migration step but may later split into Domain vs Application read models.

Suggested next unit:

- Move `ServiceResult` out of `DTOs/Results` into a Services/Application-style result boundary.
- Keep `ApiResult` in DTO/API response territory.
- Update service interfaces and implementations to depend on the service result namespace.

## Completed Architecture Layering Unit

### Service Result Boundary Extraction

Status: completed.

Goal:

- Implement the next class-library/layering cleanup after Domain record extraction.
- Move `ServiceResult` out of API DTO result territory.
- Keep `ApiResult` in DTO/API response territory.
- Preserve service behavior, API response shapes, error codes, and tests.

Implemented files:

- `src/OpenCodex.Api/Services/Results/ServiceResult.cs`
- `src/OpenCodex.Api/Services/Results/ErrorItem.cs`
- `src/OpenCodex.Api/DTOs/Results/ServiceResult.cs` deleted
- `src/OpenCodex.Api/DTOs/Results/ErrorItem.cs` deleted
- `src/OpenCodex.Api/DTOs/Results/ApiResult.cs`
- Service interfaces and implementations that return `ServiceResult`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `OpenCodex.Api.Services.Results` namespace.
- Moved `ServiceResult` and `ServiceResult<T>` into the service result namespace.
- Moved `ErrorItem` into the service result namespace because it is shared by `ServiceResult` and `ApiResult`.
- Kept `ApiResult` and `PageResult` under `DTOs/Results`.
- Updated service interfaces and implementations to use `OpenCodex.Api.Services.Results`.
- Updated `ApiResult` to reference `OpenCodex.Api.Services.Results.ErrorItem`.
- Removed DTO-layer `ServiceResult` and `ErrorItem` files.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --logger "console;verbosity=minimal"
find src/OpenCodex.Api/DTOs/Results -maxdepth 1 -type f -name '*.cs' -print | sort
find src/OpenCodex.Api/Services/Results -maxdepth 1 -type f -name '*.cs' -print | sort
rg -n "using OpenCodex\.Api\.DTOs\.Results" src/OpenCodex.Api/Services
rg -n "public class ServiceResult|public sealed class ServiceResult" src/OpenCodex.Api
rg -n "[ \t]+$" src/OpenCodex.Api/Services/Results src/OpenCodex.Api/DTOs/Results src/OpenCodex.Api/Services MIGRATION_PROGRESS.tmp.md
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/Results opencodex_proxy/src/OpenCodex.Api/DTOs/Results opencodex_proxy/src/OpenCodex.Api/Services opencodex_proxy/MIGRATION_PROGRESS.tmp.md
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `DTOs/Results` now contains only `ApiResult.cs` and `PageResult.cs`.
- `Services/Results` contains `ServiceResult.cs` and `ErrorItem.cs`.
- No service files import `OpenCodex.Api.DTOs.Results`.
- `ServiceResult` is defined only under `src/OpenCodex.Api/Services/Results/ServiceResult.cs`.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ErrorItem` is now in the service result namespace while still used by `ApiResult`; this is acceptable for the current single-project boundary but could later move to an Abstractions/SharedKernel package if needed.
- This is still folder/namespace cleanup, not a physical Application class-library split.
- Tests do not directly assert namespace boundaries; the structural scans are the evidence for this unit.

Suggested next unit:

- Create an `ExternalIntegrations` folder/namespace boundary.
- Move concrete external clients out of Services:
  - `HttpUpstreamClient*.cs`
  - `TavilyWebSearchClient`
- Keep `IUpstreamClient`, `IUpstreamModelClient`, and `IWebSearchClient` as service/application abstractions for now.
- Update DI registration and run upstream/web-search/channel diagnostics tests plus full suite.

## Next Task Memory

Status: completed for the ExternalIntegrations boundary pre-read.

User reminder:

- Before starting the next implementation task, carefully read the code class-library guide and architecture layering documents.
- Treat the class-library and layer-boundary documentation as the decision source for the next migration unit.
- Next task focus: continue architecture layering work, likely starting from the `ExternalIntegrations` boundary described above, but first verify it against:
  - `CLASS_LIBRARY_GUIDE.md`
  - `PROJECT_ARCHITECTURE.md`
  - `NEW_PROJECT_CHECKLIST.md`
  - related repository/code-style documents if needed

Working rule for next task:

- Do not start moving code only from memory.
- Re-open and compare the relevant documents with the current source tree before proposing the next unit.
- If the next unit touches more than 3 files, split it into smaller approved units with goals, files, and risks.

## Completed Architecture Layering Unit

### External Integration Client Boundary

Status: completed.

Goal:

- Follow `CLASS_LIBRARY_GUIDE.md`, `PROJECT_ARCHITECTURE.md`, and `NEW_PROJECT_CHECKLIST.md`.
- Move concrete external system client implementations out of the service/application folder.
- Keep service-layer abstractions as the business-facing contracts.
- Preserve API behavior, upstream request behavior, Tavily response mapping, DI registration, and tests.

Implemented files:

- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Requests.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Responses.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Streaming.cs`
- `src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs`
- `src/OpenCodex.Api/Services/IWebSearchClient.cs`
- `src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs`
- `tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs`
- `tests/OpenCodex.Api.Tests/Services/TavilyWebSearchClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Removed files:

- `src/OpenCodex.Api/Services/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Requests.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Responses.cs`
- `src/OpenCodex.Api/Services/HttpUpstreamClient.Streaming.cs`

Changes:

- Added `OpenCodex.Api.ExternalIntegrations` namespace.
- Moved `HttpUpstreamClient` partial implementation files into `ExternalIntegrations`.
- Moved `TavilyWebSearchClient` out of `Services/IWebSearchClient.cs` into `ExternalIntegrations`.
- Kept `IUpstreamClient`, `IUpstreamModelClient`, `IWebSearchClient`, `WebSearchProviderResult`, and `WebSearchSummary` in `OpenCodex.Api.Services`.
- Updated DI composition to register external integration implementations through service abstractions.
- Updated tests that directly construct external clients to import `OpenCodex.Api.ExternalIntegrations`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~TavilyWebSearchClientTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~AdminWebSearchServiceTests|FullyQualifiedName~WebSearchSimulatorTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "class (HttpUpstreamClient|TavilyWebSearchClient)|namespace OpenCodex\.Api\.ExternalIntegrations|namespace OpenCodex\.Api\.Services" src/OpenCodex.Api/Services src/OpenCodex.Api/ExternalIntegrations
rg -n "TavilyWebSearchClient|HttpUpstreamClient" src/OpenCodex.Api tests/OpenCodex.Api.Tests
rg -n "[ \t]+$" src/OpenCodex.Api/ExternalIntegrations src/OpenCodex.Api/Services/IWebSearchClient.cs src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs tests/OpenCodex.Api.Tests/Services/TavilyWebSearchClientTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations opencodex_proxy/src/OpenCodex.Api/Services/IWebSearchClient.cs opencodex_proxy/src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/HttpUpstreamClientTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/TavilyWebSearchClientTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Upstream/web-search/channel diagnostics focused tests passed: 36 passed, 0 failed, 0 skipped.
- Controller/smoke focused tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `HttpUpstreamClient` and `TavilyWebSearchClient` are now defined under `src/OpenCodex.Api/ExternalIntegrations`.
- `Services/IWebSearchClient.cs` now contains only the web-search client abstraction and provider result records.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- This is still a folder/namespace boundary inside a single API project, not a physical `OpenCodex.ExternalIntegrations` class-library split.
- Service abstractions still live in `Services`; a future physical split may move stable contracts into an Abstractions/Application project.
- Integration clients still reuse some service-layer helper models such as `WebSearchPayload`; later cleanup may split web-search contracts/helpers more finely.

Suggested next unit:

- Audit remaining `Services` files for mixed responsibilities after the external client move.
- Candidate focus: split web-search simulation/application helpers from transport-specific payload helpers, or continue toward physical class-library extraction only after the current folder boundaries are clean.
- Re-read `CLASS_LIBRARY_GUIDE.md`, `PROJECT_ARCHITECTURE.md`, and `NEW_PROJECT_CHECKLIST.md` before choosing the exact next unit.

## Completed Architecture Layering Unit

### Web Search Payload Infrastructure Helper Boundary

Status: completed.

Goal:

- Continue the folder/namespace layering cleanup from `NEW_PROJECT_CHECKLIST.md`.
- Remove the `ExternalIntegrations` dependency on a `Services`-local helper implementation.
- Treat JSON dictionary payload manipulation as infrastructure helper behavior rather than application service behavior.
- Preserve web-search simulation behavior, Tavily response mapping, proxy compatibility, and API response shapes.

Implemented files:

- `src/OpenCodex.Api/Infrastructure/WebSearchPayload.cs`
- `src/OpenCodex.Api/Services/WebSearchPayload.cs` deleted
- `src/OpenCodex.Api/Services/WebSearchContinuationRequest.cs`
- `src/OpenCodex.Api/Services/WebSearchRequestPolicy.cs`
- `src/OpenCodex.Api/Services/WebSearchResponsePayload.cs`
- `src/OpenCodex.Api/Services/WebSearchSimulationLog.cs`
- `src/OpenCodex.Api/Services/WebSearchSimulator.NonStream.cs`
- `src/OpenCodex.Api/Services/WebSearchSimulator.Streaming.cs`
- `src/OpenCodex.Api/Services/WebSearchStreamEventState.cs`
- `src/OpenCodex.Api/Services/WebSearchToolCallParser.cs`
- `src/OpenCodex.Api/Services/WebSearchToolResult.cs`
- `src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `WebSearchPayload` from `OpenCodex.Api.Services` to `OpenCodex.Api.Infrastructure`.
- Kept the helper `internal` and preserved the existing helper name to avoid widening the change.
- Updated web-search service helpers to use `OpenCodex.Api.Infrastructure.WebSearchPayload`.
- Updated `TavilyWebSearchClient` to use the infrastructure helper rather than a service-layer helper.
- Confirmed no `using static OpenCodex.Api.Services.WebSearchPayload` references remain.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~WebSearchRequestPolicyTests|FullyQualifiedName~WebSearchToolCallParserTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~TavilyWebSearchClientTests|FullyQualifiedName~AdminWebSearchServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "using static OpenCodex\.Api\.Services\.WebSearchPayload" src/OpenCodex.Api tests/OpenCodex.Api.Tests
rg -n "WebSearchPayload" src/OpenCodex.Api/Infrastructure src/OpenCodex.Api/Services src/OpenCodex.Api/ExternalIntegrations
rg -n "[ \t]+$" src/OpenCodex.Api/Infrastructure/WebSearchPayload.cs src/OpenCodex.Api/Services/WebSearchContinuationRequest.cs src/OpenCodex.Api/Services/WebSearchRequestPolicy.cs src/OpenCodex.Api/Services/WebSearchResponsePayload.cs src/OpenCodex.Api/Services/WebSearchSimulationLog.cs src/OpenCodex.Api/Services/WebSearchSimulator.NonStream.cs src/OpenCodex.Api/Services/WebSearchSimulator.Streaming.cs src/OpenCodex.Api/Services/WebSearchStreamEventState.cs src/OpenCodex.Api/Services/WebSearchToolCallParser.cs src/OpenCodex.Api/Services/WebSearchToolResult.cs src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Infrastructure/WebSearchPayload.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchPayload.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchContinuationRequest.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchRequestPolicy.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchResponsePayload.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchSimulationLog.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchSimulator.NonStream.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchSimulator.Streaming.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchStreamEventState.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchToolCallParser.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearchToolResult.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Web-search/helper focused tests passed: 42 passed, 0 failed, 0 skipped.
- Proxy/AdminData/Smoke focused tests passed: 46 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `WebSearchPayload` is now defined only under `src/OpenCodex.Api/Infrastructure/WebSearchPayload.cs`.
- No `OpenCodex.Api.Services.WebSearchPayload` static imports remain.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- The helper is still named `WebSearchPayload`, even though it is now in Infrastructure; a future cleanup could rename it if it becomes broadly reused.
- `ExternalIntegrations` still references `OpenCodex.Api.Services` for stable contracts such as `IWebSearchClient`, `WebSearchProviderResult`, and `WebSearchSummary`; this is acceptable in the current single-project folder split but should be revisited during a physical Abstractions/Application split.
- Tests cover behavior, not strict namespace architecture; structural `rg` scans are the evidence for this boundary.

Suggested next unit:

- Continue auditing `Services` for framework-facing types such as `HttpRequest` / `HttpResponse` in proxy service contracts.
- Candidate: move request/response stream writer or proxy HTTP-facing helpers toward API/Infrastructure boundaries, but first re-check `PROJECT_ARCHITECTURE.md` and avoid breaking proxy route compatibility.

## Completed Architecture Layering Unit

### Proxy Stream Response Writer Infrastructure Boundary

Status: completed.

Goal:

- Continue reducing framework-facing helper responsibilities inside `Services`.
- Move pure SSE HTTP response writing behavior into `Infrastructure`.
- Keep `ProxyStreamService` as the stream orchestration service and preserve proxy streaming behavior.
- Preserve response headers, line flushing, TTFT measurement, logging behavior, and route compatibility.

Implemented files:

- `src/OpenCodex.Api/Infrastructure/ProxyStreamResponseWriter.cs`
- `src/OpenCodex.Api/Services/ProxyStreamResponseWriter.cs` deleted
- `src/OpenCodex.Api/Services/ProxyStreamService.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyStreamResponseWriterTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `ProxyStreamResponseWriter` from `OpenCodex.Api.Services` to `OpenCodex.Api.Infrastructure`.
- Updated `ProxyStreamService` to import the infrastructure writer.
- Updated `ProxyStreamResponseWriterTests` to target the infrastructure namespace.
- Kept the public helper API and behavior unchanged:
  - `PrepareSse(HttpResponse response)`
  - `WriteLinesAsync(HttpResponse response, IAsyncEnumerable<string> lines, Func<string, bool> countsForTtft, Func<int> elapsedMilliseconds, CancellationToken cancellationToken = default)`

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyStreamResponseWriterTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "ProxyStreamResponseWriter" src/OpenCodex.Api/Services src/OpenCodex.Api/Infrastructure tests/OpenCodex.Api.Tests
rg -n "[ \t]+$" src/OpenCodex.Api/Infrastructure/ProxyStreamResponseWriter.cs src/OpenCodex.Api/Services/ProxyStreamService.cs tests/OpenCodex.Api.Tests/Services/ProxyStreamResponseWriterTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Infrastructure/ProxyStreamResponseWriter.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyStreamResponseWriter.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyStreamService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyStreamResponseWriterTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Stream proxy focused tests passed: 18 passed, 0 failed, 0 skipped.
- AdminData/Smoke focused tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `ProxyStreamResponseWriter` is now defined only under `src/OpenCodex.Api/Infrastructure/ProxyStreamResponseWriter.cs`.
- `ProxyStreamService` now calls the infrastructure writer.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ProxyStreamService` and proxy contexts still carry `HttpRequest` / `HttpResponse`; this unit only moved the pure response writer helper.
- Tests verify response headers, line writing, TTFT calculation, and stream service paths, but do not assert architecture rules directly.

Suggested next unit:

- Continue the HTTP-facing service contract audit.
- Candidate: inspect `RequestBodyReader`, `IProxyRequestService`, `IProxyLogService`, and proxy context records to decide whether a DTO/context split can reduce direct framework coupling without disrupting proxy behavior.

## Completed Architecture Layering Unit

### Request Body Reader Infrastructure Boundary

Status: completed.

Goal:

- Continue reducing HTTP framework read responsibilities inside `Services`.
- Move request-body reading and form/JSON conversion into `Infrastructure`.
- Preserve admin controller body parsing, proxy request body parsing, JSON number conversion behavior, and DI registration.

Implemented files:

- `src/OpenCodex.Api/Infrastructure/IRequestBodyReader.cs`
- `src/OpenCodex.Api/Infrastructure/RequestBodyReader.cs`
- `src/OpenCodex.Api/Services/IRequestBodyReader.cs` deleted
- `src/OpenCodex.Api/Services/RequestBodyReader.cs` deleted
- `src/OpenCodex.Api/Services/ProxyRequestService.cs`
- `src/OpenCodex.Api/Controllers/AdminApiControllerBase.cs`
- `src/OpenCodex.Api/Controllers/AdminAuthController.cs`
- `src/OpenCodex.Api/Controllers/AdminConfigController.cs`
- `src/OpenCodex.Api/Controllers/AdminWebSearchController.cs`
- `src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs`
- `tests/OpenCodex.Api.Tests/Services/RequestBodyReaderTests.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `IRequestBodyReader` and `RequestBodyReader` from `OpenCodex.Api.Services` to `OpenCodex.Api.Infrastructure`.
- Updated admin controllers that accept `IRequestBodyReader` to import the infrastructure namespace.
- Updated `ProxyRequestService` to depend on the infrastructure reader abstraction.
- Updated request-body and proxy-request tests to target the infrastructure namespace.
- Kept DI registration unchanged in behavior: `IRequestBodyReader` still resolves to `RequestBodyReader`.
- Kept request parsing behavior unchanged:
  - JSON objects return dictionaries.
  - invalid or non-object JSON returns null.
  - form bodies become string-valued dictionaries.
  - small integers remain `int`, large integers become `long`, fractions become `double`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~RequestBodyReaderTests|FullyQualifiedName~ProxyRequestServiceTests|FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~AdminWebSearchServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "IRequestBodyReader|class RequestBodyReader|using OpenCodex\.Api\.Infrastructure" src/OpenCodex.Api/Services src/OpenCodex.Api/Infrastructure src/OpenCodex.Api/Controllers tests/OpenCodex.Api.Tests/Services/RequestBodyReaderTests.cs tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Infrastructure/IRequestBodyReader.cs src/OpenCodex.Api/Infrastructure/RequestBodyReader.cs src/OpenCodex.Api/Services/ProxyRequestService.cs src/OpenCodex.Api/Controllers/AdminApiControllerBase.cs src/OpenCodex.Api/Controllers/AdminAuthController.cs src/OpenCodex.Api/Controllers/AdminConfigController.cs src/OpenCodex.Api/Controllers/AdminWebSearchController.cs src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs tests/OpenCodex.Api.Tests/Services/RequestBodyReaderTests.cs tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Infrastructure/IRequestBodyReader.cs opencodex_proxy/src/OpenCodex.Api/Infrastructure/RequestBodyReader.cs opencodex_proxy/src/OpenCodex.Api/Services/IRequestBodyReader.cs opencodex_proxy/src/OpenCodex.Api/Services/RequestBodyReader.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyRequestService.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminApiControllerBase.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminAuthController.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminConfigController.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminWebSearchController.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/RequestBodyReaderTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Request-body/proxy/admin focused tests passed: 63 passed, 0 failed, 0 skipped.
- Smoke tests passed: 7 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `IRequestBodyReader` and `RequestBodyReader` are now defined only under `src/OpenCodex.Api/Infrastructure`.
- No `Services` definitions for request body reader remain.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- `IRequestBodyReader` still exposes `HttpRequest`, so it remains an ASP.NET-facing abstraction by design; this unit only moved it out of application service territory.
- Some service contracts still use `HttpRequest` / `HttpResponse`, especially proxy request/log/context boundaries.
- Tests verify behavior and DI-adjacent usage through controller/service paths, not strict architecture rules.

Suggested next unit:

- Continue the HTTP-facing service contract audit.
- Candidate: reduce `IProxyLogService.WriteLog(ProxyLogContext, HttpRequest, clientIp)` framework coupling by moving request metadata extraction out of the logging service or introducing an explicit metadata DTO.

## Completed Architecture Layering Unit

### Proxy Request Metadata Logging Boundary

Status: completed.

Goal:

- Reduce `IProxyLogService` framework coupling.
- Stop passing `HttpRequest` directly into the proxy logging service contract.
- Move request metadata extraction and Authorization header redaction into an infrastructure DTO.
- Preserve request log fields, header redaction behavior, proxy logging behavior, and persisted observability output.

Implemented files:

- `src/OpenCodex.Api/Infrastructure/ProxyRequestMetadata.cs`
- `src/OpenCodex.Api/Services/IProxyLogService.cs`
- `src/OpenCodex.Api/Services/ProxyLogService.cs`
- `src/OpenCodex.Api/Services/ProxyEndpointService.cs`
- `src/OpenCodex.Api/Services/ProxyNonStreamService.cs`
- `src/OpenCodex.Api/Services/ProxyStreamService.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyLogServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyNonStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProxyRequestMetadata` in `OpenCodex.Api.Infrastructure`.
- Moved request method/path/clientIp/header extraction into `ProxyRequestMetadata.FromHttpRequest`.
- Moved Authorization redaction out of `ProxyLogService` into `ProxyRequestMetadata`.
- Changed `IProxyLogService.WriteLog` from:
  - `WriteLog(ProxyLogContext context, HttpRequest request, string? clientIp)`
  to:
  - `WriteLog(ProxyLogContext context, ProxyRequestMetadata request)`
- Updated `ProxyEndpointService`, `ProxyNonStreamService`, and `ProxyStreamService` to pass metadata.
- Updated proxy service tests and fake log services to assert metadata values rather than raw `HttpRequest` object identity.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyLogServiceTests|FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "WriteLog\(ProxyLogContext context, HttpRequest request, string\? clientIp\)|IHeaderDictionary|RedactedHeaders\(|Redact\(|ProxyRequestMetadata|WriteLog\(ProxyLogContext context" src/OpenCodex.Api tests/OpenCodex.Api.Tests/Services
rg -n "[ \t]+$" src/OpenCodex.Api/Infrastructure/ProxyRequestMetadata.cs src/OpenCodex.Api/Services/IProxyLogService.cs src/OpenCodex.Api/Services/ProxyLogService.cs src/OpenCodex.Api/Services/ProxyEndpointService.cs src/OpenCodex.Api/Services/ProxyNonStreamService.cs src/OpenCodex.Api/Services/ProxyStreamService.cs tests/OpenCodex.Api.Tests/Services/ProxyLogServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyNonStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Infrastructure/ProxyRequestMetadata.cs opencodex_proxy/src/OpenCodex.Api/Services/IProxyLogService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyLogService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyEndpointService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyNonStreamService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyStreamService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyLogServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyNonStreamServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Proxy logging/service focused tests passed: 23 passed, 0 failed, 0 skipped.
- AdminData/Smoke focused tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- No old `WriteLog(ProxyLogContext, HttpRequest, clientIp)` signatures remain.
- `ProxyLogService` no longer imports `Microsoft.AspNetCore.Http` or reads request headers directly.
- Authorization header redaction is preserved through `ProxyRequestMetadata.FromHttpRequest`.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ProxyRequestMetadata` still uses `HttpRequest` and `IHeaderDictionary` in its factory; this is intentional infrastructure-level ASP.NET coupling.
- `ProxyNonStreamContext`, `ProxyStreamContext`, and `ProxyEndpointContext` still carry `HttpRequest` / `HttpResponse`.
- Tests verify metadata extraction and logged fields, but not strict architecture rules beyond structural scans.

Suggested next unit:

- Continue reducing proxy context framework coupling.
- Candidate: introduce reusable request metadata on `ProxyEndpointContext` or move method/path/clientIp extraction earlier, then decide whether non-stream/stream contexts still need `HttpRequest`.

## Next Task Memory

Before starting the next implementation unit, explicitly review the architecture and class-library documents:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- Relevant checklist items in `NEW_PROJECT_CHECKLIST.md`:
  - architecture layering
  - class library split
  - public APIs
  - project references
  - dependency direction

Next task intent:

- Continue the .NET migration with architecture layering as the primary constraint.
- Do not only move code by filename or namespace; first compare the current code against the documented class-library and layering rules.
- Prefer small units that preserve existing behavior and compatibility routes.

Candidate next implementation unit:

- Reduce proxy context ASP.NET framework coupling.
- Replace `ProxyNonStreamContext` and `ProxyStreamContext` log-only `HttpRequest` / `ClientIp` dependencies with `ProxyRequestMetadata`.
- Keep `ProxyEndpointContext` as the API boundary for now.
- Keep `ProxyStreamContext.HttpResponse`, because stream writing still needs the response object.
- Update focused proxy service tests before broader verification.

## Completed Architecture Layering Unit

### Proxy Context Request Metadata Boundary

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Continue aligning the proxy implementation with documented layering rules.
- Keep `HttpRequest` / `HttpResponse` at the API boundary where they are needed.
- Stop passing log-only `HttpRequest` / `ClientIp` dependencies into non-stream and stream proxy service contexts.
- Preserve request log method/path/clientIp/header behavior and streaming response behavior.

Implemented files:

- `src/OpenCodex.Api/Services/ProxyNonStreamContext.cs`
- `src/OpenCodex.Api/Services/ProxyStreamContext.cs`
- `src/OpenCodex.Api/Services/ProxyEndpointService.cs`
- `src/OpenCodex.Api/Services/ProxyNonStreamService.cs`
- `src/OpenCodex.Api/Services/ProxyStreamService.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyNonStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Replaced `ProxyNonStreamContext.HttpRequest Request` and `ClientIp` with `ProxyRequestMetadata RequestMetadata`.
- Replaced `ProxyStreamContext.HttpRequest Request` and `ClientIp` with `ProxyRequestMetadata RequestMetadata`.
- Kept `ProxyStreamContext.HttpResponse Response`, because `ProxyStreamService` still writes SSE responses.
- Moved request metadata creation to `ProxyEndpointService`, using `ProxyRequestMetadata.FromHttpRequest(context.Request, context.ClientIp)` once at the endpoint boundary.
- Updated `ProxyNonStreamService` and `ProxyStreamService` to write logs with `context.RequestMetadata`.
- Updated focused tests to assert metadata method/path/clientIp rather than raw request object identity.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~ProxyLogServiceTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "HttpRequest Request|string\? ClientIp|ProxyRequestMetadata.FromHttpRequest\(context.Request, context.ClientIp\)|context\.RequestMetadata" src/OpenCodex.Api/Services tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyNonStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/ProxyNonStreamContext.cs src/OpenCodex.Api/Services/ProxyStreamContext.cs src/OpenCodex.Api/Services/ProxyEndpointService.cs src/OpenCodex.Api/Services/ProxyNonStreamService.cs src/OpenCodex.Api/Services/ProxyStreamService.cs tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyNonStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/ProxyNonStreamContext.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyStreamContext.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyEndpointService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyNonStreamService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyStreamService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyNonStreamServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyStreamServiceTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Proxy focused tests passed: 23 passed, 0 failed, 0 skipped.
- AdminData/Smoke focused tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `ProxyRequestMetadata.FromHttpRequest(context.Request, context.ClientIp)` remains only in `ProxyEndpointService`, the current endpoint boundary.
- `ProxyNonStreamService` and `ProxyStreamService` now log through `context.RequestMetadata`.
- No `HttpRequest Request` / `ClientIp` fields remain in `ProxyNonStreamContext` or `ProxyStreamContext`.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ProxyEndpointContext` still carries `HttpRequest`, `HttpResponse`, and `ClientIp`; this is intentional for now because it is the API boundary and request body/authentication still need `HttpRequest`.
- `ProxyRequestMetadata` still uses ASP.NET types in its factory; this remains infrastructure-level framework coupling.
- No strict architecture analyzer is present, so architecture compliance is verified by targeted review, structural search, and tests.

Suggested next unit:

- Continue applying `PROJECT_ARCHITECTURE.md` and `CLASS_LIBRARY_GUIDE.md`.
- Candidate: audit remaining `Services` types for infrastructure or external-integration responsibilities that should move to `Infrastructure` / `ExternalIntegrations`, while keeping compatibility behavior unchanged.

## Completed Architecture Layering Unit

### Admin UI Infrastructure Implementation Boundary

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Continue reducing infrastructure responsibilities inside `Services`.
- Treat the admin UI static-file resolver as an infrastructure implementation because it reads configuration, hosting environment, content types, and the file system.
- Keep the `IAdminUiService` contract and result records in `Services` so controllers and DI still depend on the existing service contract.
- Preserve admin UI routes, SPA fallback behavior, login HTML, content type detection, and path traversal protection.

Implemented files:

- `src/OpenCodex.Api/Infrastructure/AdminUiService.cs`
- `tests/OpenCodex.Api.Tests/Services/AdminUiServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `AdminUiService` implementation from `Services` to `Infrastructure`.
- Changed the implementation namespace to `OpenCodex.Api.Infrastructure`.
- Added `using OpenCodex.Api.Services` to the implementation so it continues implementing `IAdminUiService`.
- Updated `AdminUiServiceTests` to reference the infrastructure implementation.
- Kept `IAdminUiService`, `AdminUiFileResult`, and `AdminUiHtmlResult` in `Services` as the public service contract.
- Kept DI registration unchanged semantically: `IAdminUiService` still resolves to `AdminUiService`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminUiServiceTests|FullyQualifiedName~AdminUiControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "class AdminUiService|namespace OpenCodex.Api.Infrastructure|namespace OpenCodex.Api.Services|AddScoped<IAdminUiService, AdminUiService>|using OpenCodex.Api.Infrastructure" src/OpenCodex.Api/Infrastructure/AdminUiService.cs src/OpenCodex.Api/Services/IAdminUiService.cs src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs tests/OpenCodex.Api.Tests/Services/AdminUiServiceTests.cs
find src/OpenCodex.Api/Services -maxdepth 1 -name 'AdminUiService.cs' -print
rg -n "[ \t]+$" src/OpenCodex.Api/Infrastructure/AdminUiService.cs tests/OpenCodex.Api.Tests/Services/AdminUiServiceTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Infrastructure/AdminUiService.cs opencodex_proxy/src/OpenCodex.Api/Services/AdminUiService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/AdminUiServiceTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Admin UI focused tests passed: 16 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `AdminUiService` now lives in `OpenCodex.Api.Infrastructure`.
- No `AdminUiService.cs` remains directly under `src/OpenCodex.Api/Services`.
- `IAdminUiService` remains under `OpenCodex.Api.Services`.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- Tests are still located under `tests/OpenCodex.Api.Tests/Services/AdminUiServiceTests.cs`; this is a test organization mismatch only, not runtime layering.
- `IAdminUiService` still exposes file path result records; acceptable for current controller contract, but future class-library extraction may want API-specific response DTOs or a narrower asset abstraction.
- No architecture analyzer enforces that infrastructure implementations stay out of `Services`.

Suggested next unit:

- Continue the Services audit.
- Candidate: review `IProxyRequestService` / `ProxyRequestService`, because they still accept `HttpRequest` for authentication and body reading; decide whether this is acceptable API-boundary orchestration or should be split into smaller request parsing/authentication abstractions.

## Completed Architecture Layering Unit

### Proxy Request Service Contract Narrowing

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Continue reducing Web framework types in service contracts.
- Stop exposing `HttpRequest` through `IProxyRequestService`.
- Keep `HttpRequest` handling inside `ProxyEndpointService`, the current API boundary.
- Preserve bearer authentication behavior, JSON body reading behavior, request logging, and proxy route compatibility.

Implemented files:

- `src/OpenCodex.Api/Services/IProxyRequestService.cs`
- `src/OpenCodex.Api/Services/ProxyRequestService.cs`
- `src/OpenCodex.Api/Services/ProxyEndpointService.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Changed `IProxyRequestService.AuthenticateAccessKey` from accepting `HttpRequest` to accepting `string? authorizationHeader`.
- Removed `IProxyRequestService.ReadBodyAsync`; it was only forwarding `HttpRequest` to `IRequestBodyReader`.
- Removed `HttpRequest` and `IRequestBodyReader` dependencies from `ProxyRequestService`.
- Injected `IRequestBodyReader` into `ProxyEndpointService`, where request-body reading belongs at the current endpoint boundary.
- Moved Authorization header extraction into `ProxyEndpointService`.
- Updated endpoint tests with a fake `IRequestBodyReader` to assert request-body delegation and cancellation token forwarding.
- Updated request service tests to assert authorization-string forwarding directly.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyRequestServiceTests|FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~RequestBodyReaderTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "AuthenticateAccessKey\(HttpRequest|ReadBodyAsync\(|using Microsoft.AspNetCore.Http" src/OpenCodex.Api/Services/IProxyRequestService.cs src/OpenCodex.Api/Services/ProxyRequestService.cs tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs src/OpenCodex.Api/Services/ProxyEndpointService.cs
rg -n "IRequestBodyReader|ReadJsonObjectAsync\(|AuthorizationHeader\(|AuthenticateAccessKey\(" src/OpenCodex.Api/Services/ProxyEndpointService.cs src/OpenCodex.Api/Services/ProxyRequestService.cs src/OpenCodex.Api/Services/IProxyRequestService.cs tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Services/IProxyRequestService.cs src/OpenCodex.Api/Services/ProxyRequestService.cs src/OpenCodex.Api/Services/ProxyEndpointService.cs tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/IProxyRequestService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyRequestService.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyEndpointService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyRequestServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyEndpointServiceTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Proxy request/endpoint focused tests passed: 31 passed, 0 failed, 0 skipped.
- AdminData/Smoke focused tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 332 passed, 0 failed, 0 skipped.
- Full test count decreased from 333 to 332 because the removed `ReadBodyAsync` method no longer exists; body-reader delegation is now covered in `ProxyEndpointServiceTests`.
- `IProxyRequestService` and `ProxyRequestService` no longer import or expose `HttpRequest`.
- `ProxyEndpointService` is now the only touched service boundary extracting Authorization from `HttpRequest` and reading JSON body through `IRequestBodyReader`.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ProxyEndpointService` still uses `HttpRequest` directly; this is intentional because `ProxyEndpointContext` is the current API boundary.
- `ProxyEndpointService` now owns more orchestration detail. If it grows further, a future unit may introduce a dedicated endpoint request adapter in `Infrastructure`.
- No architecture analyzer prevents future service contracts from accepting Web framework types.

Suggested next unit:

- Continue Services audit for low-level technical concerns.
- Candidate: inspect `ProxyLogService`, because it serializes payloads and writes repository records; decide whether this is acceptable service orchestration or should be split into a log-record mapper/helper.

## Completed Architecture Layering Unit

### Proxy Log Write Record Boundary

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Continue reducing persistence details inside `Services`.
- Stop having `ProxyLogService` hand-write database column names.
- Introduce a typed request-log write boundary between service orchestration and persistence mapping.
- Preserve request-log JSON fields, usage/cost calculation behavior, persisted database schema, and admin/proxy observability output.

Implemented files:

- `src/OpenCodex.Api/Domain/Records.cs`
- `src/OpenCodex.Api/Persistence/IProxyLogRepository.cs`
- `src/OpenCodex.Api/Persistence/ProxyLogRepository.cs`
- `src/OpenCodex.Api/Services/ProxyLogService.cs`
- `tests/OpenCodex.Api.Tests/Services/ProxyLogServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Persistence/ProxyLogRepositoryTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `RequestLogWriteRecord` as a typed Domain record for request-log writes.
- Changed `IProxyLogRepository.WriteRequestLog` to accept `RequestLogWriteRecord`.
- Updated `ProxyLogService` to build a typed `RequestLogWriteRecord` instead of a database column dictionary.
- Moved database column-name mapping into `ProxyLogRepository`.
- Kept `OpenCodexDatabase.WriteRequestLog` unchanged so existing persistence/database tests and lower-level dictionary behavior remain stable.
- Updated `ProxyLogServiceTests` to assert typed record properties.
- Added `ProxyLogRepositoryTests` to verify the typed record is mapped and persisted to the existing database columns correctly.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyLogServiceTests|FullyQualifiedName~ProxyLogRepositoryTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "\[\"request_id\"\]|\[\"created_at\"\]|\[\"request_headers\"\]|Dictionary<string, object\?>\(StringComparer.Ordinal\)|RequestLogWriteRecord|WriteRequestLog\(" src/OpenCodex.Api/Services/ProxyLogService.cs src/OpenCodex.Api/Persistence/ProxyLogRepository.cs src/OpenCodex.Api/Persistence/IProxyLogRepository.cs src/OpenCodex.Api/Domain/Records.cs tests/OpenCodex.Api.Tests/Services/ProxyLogServiceTests.cs tests/OpenCodex.Api.Tests/Persistence/ProxyLogRepositoryTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Domain/Records.cs src/OpenCodex.Api/Persistence/IProxyLogRepository.cs src/OpenCodex.Api/Persistence/ProxyLogRepository.cs src/OpenCodex.Api/Services/ProxyLogService.cs tests/OpenCodex.Api.Tests/Services/ProxyLogServiceTests.cs tests/OpenCodex.Api.Tests/Persistence/ProxyLogRepositoryTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Domain/Records.cs opencodex_proxy/src/OpenCodex.Api/Persistence/IProxyLogRepository.cs opencodex_proxy/src/OpenCodex.Api/Persistence/ProxyLogRepository.cs opencodex_proxy/src/OpenCodex.Api/Services/ProxyLogService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/ProxyLogServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/ProxyLogRepositoryTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Proxy log focused and observability compatibility tests passed: 50 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- Database column-name mapping for request-log writes now lives in `ProxyLogRepository`.
- `ProxyLogService` still computes usage/cost and JSON strings, but no longer builds persistence column dictionaries.
- Existing `OpenCodexDatabase.WriteRequestLog` dictionary API remains unchanged.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ProxyLogService` still performs JSON serialization. This is acceptable for the current small unit, but a future unit could extract a request-log serialization helper if needed.
- `RequestLogWriteRecord` is currently in `Domain`; if the project later splits into separate class libraries, this may become a Contracts/Application write model instead of a core domain concept.
- `OpenCodexDatabase.WriteRequestLog` still accepts a dictionary for lower-level tests and async writer compatibility.

Suggested next unit:

- Continue the Services audit for data-transfer/result types.
- Candidate: review whether `ProxyRequestLogContext`, `ProxyLogContext`, and proxy service context records should remain in `Services` or be grouped into a dedicated `Services/Proxy` namespace/folder to reduce the flat Services surface.

## Completed Architecture Layering Unit

### Proxy Services Folder Grouping

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Reduce the flat `Services` surface.
- Group proxy-specific service interfaces, implementations, contexts, and result records into a dedicated folder.
- Preserve public type names, namespaces, DI registrations, controller usage, and tests.

Implemented files:

- `src/OpenCodex.Api/Services/Proxy/IProxyAccessService.cs`
- `src/OpenCodex.Api/Services/Proxy/IProxyEndpointService.cs`
- `src/OpenCodex.Api/Services/Proxy/IProxyLogService.cs`
- `src/OpenCodex.Api/Services/Proxy/IProxyNonStreamService.cs`
- `src/OpenCodex.Api/Services/Proxy/IProxyRequestService.cs`
- `src/OpenCodex.Api/Services/Proxy/IProxyRouteService.cs`
- `src/OpenCodex.Api/Services/Proxy/IProxyStreamService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyAccessService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointResult.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyLogContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyLogService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyNonStreamContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyNonStreamResult.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyNonStreamService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyRequestLogContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyRequestService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyRequestState.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyRouteService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved all root-level `Proxy*` and `IProxy*` service files into `src/OpenCodex.Api/Services/Proxy/`.
- Kept namespace `OpenCodex.Api.Services` unchanged to avoid unnecessary public API churn.
- Kept DI registrations and all consuming code unchanged.
- Did not move web-search service files in this unit; they need a separate boundary decision because they are related to tool simulation rather than generic proxy endpoint orchestration.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyAccessServiceTests|FullyQualifiedName~ProxyRouteServiceTests|FullyQualifiedName~ProxyRequestServiceTests|FullyQualifiedName~ProxyLogServiceTests|FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
find src/OpenCodex.Api/Services -maxdepth 1 -type f \( -name 'Proxy*' -o -name 'IProxy*' \) -print | sort
find src/OpenCodex.Api/Services/Proxy -maxdepth 1 -type f | wc -l
rg -n "namespace OpenCodex.Api.Services" src/OpenCodex.Api/Services/Proxy | wc -l
rg -n "[ \t]+$" src/OpenCodex.Api/Services/Proxy
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services opencodex_proxy/tests/OpenCodex.Api.Tests/Services
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Proxy focused tests passed: 34 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- No root-level `Proxy*` / `IProxy*` files remain directly under `src/OpenCodex.Api/Services`.
- `src/OpenCodex.Api/Services/Proxy` contains 22 files.
- All 22 moved files still use namespace `OpenCodex.Api.Services`.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- This unit improves physical organization but does not yet introduce a `OpenCodex.Api.Services.Proxy` namespace.
- Tests still live under `tests/OpenCodex.Api.Tests/Services`, not `tests/.../Services/Proxy`.
- Web-search service files are still flat under `Services`; they should be reviewed separately before moving because their role spans proxy orchestration, external web search, and response shaping.

Suggested next unit:

- Continue Services directory grouping.
- Candidate: review `WebSearch*` service files and decide whether they should be grouped under `Services/WebSearch` or partly moved to `Infrastructure` / `ExternalIntegrations`.

## Completed Architecture Layering Unit

### WebSearch Services Folder Grouping

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Reduce the remaining flat `Services` surface.
- Group web-search simulation, request policy, response shaping, stream state, and provider contract files into a dedicated module folder.
- Preserve public type names, namespaces, DI registrations, controller usage, and tests.

Implemented files:

- `src/OpenCodex.Api/Services/WebSearch/IWebSearchClient.cs`
- `src/OpenCodex.Api/Services/WebSearch/IWebSearchSimulator.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchContinuationRequest.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchRequestPolicy.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchResponsePayload.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationLog.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationResult.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationUpstreamException.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchStreamEventState.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchStreamResult.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchToolCallParser.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchRequestPolicyTests.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchSimulatorTests.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchToolCallParserTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved all root-level `WebSearch*` service files and `IWebSearch*` service contracts into `src/OpenCodex.Api/Services/WebSearch/`.
- Moved direct WebSearch service tests into `tests/OpenCodex.Api.Tests/Services/WebSearch/`.
- Kept namespace `OpenCodex.Api.Services` unchanged to avoid public API churn.
- Kept test namespace `OpenCodex.Api.Tests.Services` unchanged.
- Did not move `AdminWebSearchServiceTests`; that test belongs to the admin service module.
- Did not move `TavilyWebSearchClientTests`; that test belongs to the external integration client.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~WebSearchRequestPolicyTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~WebSearchToolCallParserTests|FullyQualifiedName~AdminWebSearchServiceTests|FullyQualifiedName~TavilyWebSearchClientTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
find src/OpenCodex.Api/Services -maxdepth 1 -type f \( -name 'WebSearch*' -o -name 'IWebSearch*' \) -print | sort
find tests/OpenCodex.Api.Tests/Services -maxdepth 1 -type f -name 'WebSearch*Tests.cs' -print | sort
find src/OpenCodex.Api/Services/WebSearch -maxdepth 1 -type f | wc -l
find tests/OpenCodex.Api.Tests/Services/WebSearch -maxdepth 1 -type f | wc -l
rg -n "[ \t]+$" src/OpenCodex.Api/Services/WebSearch tests/OpenCodex.Api.Tests/Services/WebSearch
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services opencodex_proxy/tests/OpenCodex.Api.Tests/Services
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- WebSearch/Admin/Proxy focused tests passed: 78 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- No root-level `WebSearch*` / `IWebSearch*` files remain directly under `src/OpenCodex.Api/Services`.
- No root-level `WebSearch*Tests.cs` files remain directly under `tests/OpenCodex.Api.Tests/Services`.
- `src/OpenCodex.Api/Services/WebSearch` contains 15 files.
- `tests/OpenCodex.Api.Tests/Services/WebSearch` contains 3 files.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- This unit improves physical organization but does not introduce a `OpenCodex.Api.Services.WebSearch` namespace.
- `IWebSearchClient` is a provider contract consumed by `ExternalIntegrations/TavilyWebSearchClient`; it remains in Services for now, but future class-library extraction may move provider contracts to an Abstractions/Contracts layer.
- Some web-search helpers still depend on `Infrastructure.WebSearchPayload`; this is acceptable for the current grouping unit but should be revisited if helper placement changes.

Suggested next unit:

- Continue Services directory grouping and contract audit.
- Candidate: review admin service files and decide whether admin modules should be grouped under `Services/Admin`, while preserving namespaces and controller compatibility.

## Completed Architecture Layering Unit

### Admin Services Folder Grouping

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Reduce the remaining flat `Services` surface.
- Group admin service contracts, implementations, command/result records, and error codes into a dedicated module folder.
- Preserve public type names, namespaces, DI registrations, controller usage, and tests.

Implemented files:

- `src/OpenCodex.Api/Services/Admin/AdminApiKeyErrorCodes.cs`
- `src/OpenCodex.Api/Services/Admin/AdminApiKeyService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminAuthService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.ChannelDraft.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.Compat.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.Values.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelTestResult.cs`
- `src/OpenCodex.Api/Services/Admin/AdminConfigErrorCodes.cs`
- `src/OpenCodex.Api/Services/Admin/AdminConfigImportResult.cs`
- `src/OpenCodex.Api/Services/Admin/AdminConfigService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminDiscoverModelsResult.cs`
- `src/OpenCodex.Api/Services/Admin/AdminObservabilityErrorCodes.cs`
- `src/OpenCodex.Api/Services/Admin/AdminObservabilityService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminSessionService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminSessionUser.cs`
- `src/OpenCodex.Api/Services/Admin/AdminUserErrorCodes.cs`
- `src/OpenCodex.Api/Services/Admin/AdminUserService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminWebSearchErrorCodes.cs`
- `src/OpenCodex.Api/Services/Admin/AdminWebSearchService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminWebSearchTestResult.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminApiKeyService.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminAuthService.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminChannelDiagnosticsService.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminConfigService.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminObservabilityService.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminSessionService.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminUiService.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminUserService.cs`
- `src/OpenCodex.Api/Services/Admin/IAdminWebSearchService.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminApiKeyServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminAuthServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminChannelDiagnosticsServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminConfigServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminObservabilityServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminSessionServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminUserServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminWebSearchServiceTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved all root-level `Admin*` service files and `IAdmin*` service contracts into `src/OpenCodex.Api/Services/Admin/`.
- Moved Admin service tests into `tests/OpenCodex.Api.Tests/Services/Admin/`.
- Kept namespace `OpenCodex.Api.Services` unchanged to avoid public API churn.
- Kept test namespace `OpenCodex.Api.Tests.Services` unchanged.
- Kept `IUpstreamClient.cs` at the Services root because it is not an Admin contract.
- Left `Services/Proxy`, `Services/WebSearch`, and `Services/Results` untouched.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminApiKeyServiceTests|FullyQualifiedName~AdminAuthServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~AdminConfigServiceTests|FullyQualifiedName~AdminObservabilityServiceTests|FullyQualifiedName~AdminSessionServiceTests|FullyQualifiedName~AdminUiServiceTests|FullyQualifiedName~AdminUserServiceTests|FullyQualifiedName~AdminWebSearchServiceTests|FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
find src/OpenCodex.Api/Services -maxdepth 1 -type f \( -name 'Admin*' -o -name 'IAdmin*' \) -print | sort
find tests/OpenCodex.Api.Tests/Services -maxdepth 1 -type f -name 'Admin*ServiceTests.cs' -print | sort
find src/OpenCodex.Api/Services/Admin -maxdepth 1 -type f | wc -l
find tests/OpenCodex.Api.Tests/Services/Admin -maxdepth 1 -type f | wc -l
find src/OpenCodex.Api/Services -maxdepth 1 -type f | sed 's#src/OpenCodex.Api/Services/##' | sort
rg -n "[ \t]+$" src/OpenCodex.Api/Services/Admin tests/OpenCodex.Api.Tests/Services/Admin
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services opencodex_proxy/tests/OpenCodex.Api.Tests/Services
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Admin focused tests passed: 91 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- No root-level `Admin*` / `IAdmin*` files remain directly under `src/OpenCodex.Api/Services`.
- No root-level `Admin*ServiceTests.cs` files remain directly under `tests/OpenCodex.Api.Tests/Services`.
- `src/OpenCodex.Api/Services/Admin` contains 30 files.
- `tests/OpenCodex.Api.Tests/Services/Admin` contains 9 files.
- `src/OpenCodex.Api/Services` root now contains only `IUpstreamClient.cs`.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- This unit improves physical organization but does not introduce a `OpenCodex.Api.Services.Admin` namespace.
- `IAdminUiService` is grouped with Admin contracts, while `AdminUiService` implementation remains in Infrastructure; this is intentional after the earlier infrastructure boundary unit.
- `IUpstreamClient.cs` remains at Services root and should be reviewed next because it is an external integration contract rather than a service module implementation.

Suggested next unit:

- Review `IUpstreamClient.cs` placement.
- Candidate: move upstream client contracts into `Services/Proxy` or an Abstractions-like folder while preserving `HttpUpstreamClient` in `ExternalIntegrations`.

## Completed Architecture Layering Unit

### Upstream Client Contract Folder Grouping

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Remove the last root-level file from `src/OpenCodex.Api/Services`.
- Group upstream model/provider client contracts separately from Admin, Proxy, WebSearch, and Results modules.
- Keep `HttpUpstreamClient` implementation in `ExternalIntegrations`.
- Align the upstream HTTP client test location with the external integration implementation.
- Preserve public type names, namespaces, DI registrations, and all caller behavior.

Implemented files:

- `src/OpenCodex.Api/Services/Upstream/IUpstreamClient.cs`
- `tests/OpenCodex.Api.Tests/ExternalIntegrations/HttpUpstreamClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `IUpstreamClient` and `IUpstreamModelClient` contracts into `src/OpenCodex.Api/Services/Upstream/`.
- Kept namespace `OpenCodex.Api.Services` unchanged to avoid public API churn.
- Moved `HttpUpstreamClientTests` from `tests/OpenCodex.Api.Tests/Services/` to `tests/OpenCodex.Api.Tests/ExternalIntegrations/`.
- Updated `HttpUpstreamClientTests` namespace to `OpenCodex.Api.Tests.ExternalIntegrations`.
- Kept `HttpUpstreamClient` implementation under `src/OpenCodex.Api/ExternalIntegrations`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
find src/OpenCodex.Api/Services -maxdepth 1 -type f -print | sort
find tests/OpenCodex.Api.Tests/Services -maxdepth 1 -type f -name 'HttpUpstreamClientTests.cs' -print | sort
find tests/OpenCodex.Api.Tests/ExternalIntegrations -maxdepth 1 -type f -print | sort
find src/OpenCodex.Api/Services/Upstream -maxdepth 1 -type f | wc -l
rg -n "namespace OpenCodex.Api.Services" src/OpenCodex.Api/Services/Upstream | wc -l
rg -n "namespace OpenCodex.Api.Tests.ExternalIntegrations" tests/OpenCodex.Api.Tests/ExternalIntegrations | wc -l
rg -n "[ \t]+$" src/OpenCodex.Api/Services/Upstream tests/OpenCodex.Api.Tests/ExternalIntegrations/HttpUpstreamClientTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services opencodex_proxy/tests/OpenCodex.Api.Tests/Services opencodex_proxy/tests/OpenCodex.Api.Tests/ExternalIntegrations
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Upstream/Admin/Proxy/WebSearch focused tests passed: 44 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `src/OpenCodex.Api/Services` root now has no files.
- `src/OpenCodex.Api/Services/Upstream` contains 1 file.
- `HttpUpstreamClientTests` now lives under `tests/OpenCodex.Api.Tests/ExternalIntegrations`.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- `IUpstreamClient` and `IUpstreamModelClient` remain in namespace `OpenCodex.Api.Services`; this is intentional for low-risk physical grouping.
- These contracts may later belong in an Abstractions/Contracts class library if the project is split beyond the current single API project.
- `tests/OpenCodex.Api.Tests/Services` still contains proxy and infrastructure-adjacent tests at root; those can be grouped in later units.

Suggested next unit:

- Continue test directory organization.
- Candidate: group remaining proxy tests under `tests/OpenCodex.Api.Tests/Services/Proxy` and infrastructure-adjacent tests under `tests/OpenCodex.Api.Tests/Infrastructure`.

## Completed Architecture Layering Unit

### Proxy, Infrastructure, and ExternalIntegration Test Folder Grouping

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Remove remaining root-level files from `tests/OpenCodex.Api.Tests/Services`.
- Align proxy service tests with `src/OpenCodex.Api/Services/Proxy`.
- Align infrastructure-adjacent tests with `src/OpenCodex.Api/Infrastructure`.
- Align external integration client tests with `src/OpenCodex.Api/ExternalIntegrations`.
- Preserve test behavior and coverage.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyAccessServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyLogServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyNonStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyRequestServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyRouteServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Infrastructure/ProxyStreamResponseWriterTests.cs`
- `tests/OpenCodex.Api.Tests/Infrastructure/RequestBodyReaderTests.cs`
- `tests/OpenCodex.Api.Tests/ExternalIntegrations/TavilyWebSearchClientTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved proxy service tests into `tests/OpenCodex.Api.Tests/Services/Proxy/`.
- Moved `RequestBodyReaderTests` and `ProxyStreamResponseWriterTests` into `tests/OpenCodex.Api.Tests/Infrastructure/`.
- Moved `TavilyWebSearchClientTests` into `tests/OpenCodex.Api.Tests/ExternalIntegrations/`.
- Updated namespaces for moved proxy tests to `OpenCodex.Api.Tests.Services.Proxy`.
- Updated namespaces for moved infrastructure tests to `OpenCodex.Api.Tests.Infrastructure`.
- Updated `TavilyWebSearchClientTests` namespace to `OpenCodex.Api.Tests.ExternalIntegrations`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyAccessServiceTests|FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyLogServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyRequestServiceTests|FullyQualifiedName~ProxyRouteServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyStreamResponseWriterTests|FullyQualifiedName~RequestBodyReaderTests|FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~TavilyWebSearchClientTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
find tests/OpenCodex.Api.Tests/Services -maxdepth 1 -type f -print | sort
find tests/OpenCodex.Api.Tests/Services/Proxy -maxdepth 1 -type f | wc -l
find tests/OpenCodex.Api.Tests/Infrastructure -maxdepth 1 -type f | wc -l
find tests/OpenCodex.Api.Tests/ExternalIntegrations -maxdepth 1 -type f | wc -l
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/Proxy tests/OpenCodex.Api.Tests/Infrastructure/ProxyStreamResponseWriterTests.cs tests/OpenCodex.Api.Tests/Infrastructure/RequestBodyReaderTests.cs tests/OpenCodex.Api.Tests/ExternalIntegrations/HttpUpstreamClientTests.cs tests/OpenCodex.Api.Tests/ExternalIntegrations/TavilyWebSearchClientTests.cs
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services opencodex_proxy/tests/OpenCodex.Api.Tests/Infrastructure opencodex_proxy/tests/OpenCodex.Api.Tests/ExternalIntegrations
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Moved-test focused suite passed: 43 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `tests/OpenCodex.Api.Tests/Services` root now has no files.
- `tests/OpenCodex.Api.Tests/Services/Proxy` contains 7 files.
- `tests/OpenCodex.Api.Tests/Infrastructure` contains 3 root-level files.
- `tests/OpenCodex.Api.Tests/ExternalIntegrations` contains 2 root-level files.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- Admin and WebSearch test files are physically grouped but still use the older `OpenCodex.Api.Tests.Services` namespace.
- Production namespaces still mostly remain flat by design for low-risk physical grouping.
- No automated test-folder convention check exists.

Suggested next unit:

- Continue test namespace alignment.
- Candidate: update Admin and WebSearch test namespaces to `OpenCodex.Api.Tests.Services.Admin` and `OpenCodex.Api.Tests.Services.WebSearch`, then run focused and full tests.

## Completed Architecture Layering Unit

### Admin and WebSearch Test Namespace Alignment

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Align test namespaces with the already-grouped Admin and WebSearch test folders.
- Preserve test names, test behavior, production code, and public APIs.

Implemented files:

- `tests/OpenCodex.Api.Tests/Services/Admin/AdminApiKeyServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminAuthServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminChannelDiagnosticsServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminConfigServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminObservabilityServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminSessionServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminUserServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminWebSearchServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchRequestPolicyTests.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchSimulatorTests.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchToolCallParserTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Changed Admin service test namespace from `OpenCodex.Api.Tests.Services` to `OpenCodex.Api.Tests.Services.Admin`.
- Changed WebSearch service test namespace from `OpenCodex.Api.Tests.Services` to `OpenCodex.Api.Tests.Services.WebSearch`.
- Did not change production code.
- Did not change test class names or test method names.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminApiKeyServiceTests|FullyQualifiedName~AdminAuthServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~AdminConfigServiceTests|FullyQualifiedName~AdminObservabilityServiceTests|FullyQualifiedName~AdminSessionServiceTests|FullyQualifiedName~AdminUiServiceTests|FullyQualifiedName~AdminUserServiceTests|FullyQualifiedName~AdminWebSearchServiceTests|FullyQualifiedName~WebSearchRequestPolicyTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~WebSearchToolCallParserTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "^namespace OpenCodex.Api.Tests.Services;" tests/OpenCodex.Api.Tests/Services/Admin tests/OpenCodex.Api.Tests/Services/WebSearch
rg -n "^namespace OpenCodex.Api.Tests.Services.Admin|^namespace OpenCodex.Api.Tests.Services.WebSearch" tests/OpenCodex.Api.Tests/Services/Admin tests/OpenCodex.Api.Tests/Services/WebSearch | wc -l
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Services/Admin tests/OpenCodex.Api.Tests/Services/WebSearch
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Admin opencodex_proxy/tests/OpenCodex.Api.Tests/Services/WebSearch
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Admin/WebSearch focused tests passed: 89 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- No old `OpenCodex.Api.Tests.Services` namespace remains in Admin or WebSearch test folders.
- 12 Admin/WebSearch test files now use folder-aligned namespaces.
- `git diff --check` passed.
- Trailing whitespace scan found no matches.

Remaining risks:

- Production namespaces remain intentionally flat for now, despite physical folder grouping.
- Some other test folders may still have historical namespace choices; only Admin and WebSearch were aligned in this unit.
- No automated namespace/folder convention check exists.

Suggested next unit:

- Audit production namespaces versus folders.
- Candidate: decide whether to introduce sub-namespaces for low-risk internal-only helpers first, or keep flat namespaces until actual class-library split.

## Next Task Memory

Status: pending.

User instruction:

- Before the next implementation unit, pay special attention to the code class-library guide and architecture layering guide.
- The next task should be driven by these documents, not only by local folder cleanup.

Documents to review first:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`

Recommended next unit:

- Audit the current .NET production code against the class-library and architecture layering documents.
- Focus on whether each type belongs to API, Services/Application, Domain, Infrastructure, ExternalIntegrations, or Persistence.
- Then choose one small, low-risk migration unit based on the audit.

Candidate implementation direction:

- Start with production namespace/folder consistency and class-library split readiness.
- Prefer internal-only helpers or clearly misplaced infrastructure/external integration types first.
- Avoid changing public service contracts, controller routes, response shapes, or compatibility behavior unless the audit proves it is necessary.

Risks to keep in mind:

- Flat production namespaces may currently be intentional to reduce migration risk.
- Public WebSearch/Admin/Proxy contracts are used by tests and DI registration, so namespace or project-boundary changes can cascade.
- `/v1/responses`, `/v1/chat/completions`, `/v1/messages`, and admin success payload compatibility must remain unchanged.

Suggested first verification after the next unit:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
```

## Completed Architecture Layering Unit

### WebSearch Internal Helper Namespace Alignment

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Move WebSearch internal implementation helpers into a folder-aligned sub-namespace.
- Keep public WebSearch service contracts, controller routes, response shapes, and compatibility behavior unchanged.
- Make the current code closer to the class-library guide rule that internal implementation should stay narrow and module-local.

Implemented files:

- `src/OpenCodex.Api/Services/WebSearch/WebSearchContinuationRequest.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchResponsePayload.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationLog.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchStreamEventState.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Changed five internal WebSearch helper namespaces from `OpenCodex.Api.Services` to `OpenCodex.Api.Services.WebSearch`:
  - `WebSearchContinuationRequest`
  - `WebSearchResponsePayload`
  - `WebSearchSimulationLog`
  - `WebSearchStreamEventState`
  - `WebSearchToolResult`
- Added explicit `using OpenCodex.Api.Services.WebSearch;` to the WebSearch simulator partial files that use those helpers.
- Left public WebSearch types in `OpenCodex.Api.Services` for compatibility and lower migration risk.
- Did not change API routes, DTOs, DI registrations, public service interfaces, or payload compatibility behavior.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~WebSearchRequestPolicyTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~WebSearchToolCallParserTests|FullyQualifiedName~ProxyControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "^namespace OpenCodex.Api.Services.WebSearch|using OpenCodex.Api.Services.WebSearch" src/OpenCodex.Api/Services/WebSearch
rg -n "[ \t]+$" src/OpenCodex.Api/Services/WebSearch/WebSearchContinuationRequest.cs src/OpenCodex.Api/Services/WebSearch/WebSearchResponsePayload.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationLog.cs src/OpenCodex.Api/Services/WebSearch/WebSearchStreamEventState.cs src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchContinuationRequest.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchResponsePayload.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationLog.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchStreamEventState.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused WebSearch/Proxy tests passed: 44 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- Five internal helpers now use `OpenCodex.Api.Services.WebSearch`.
- Three WebSearch simulator partial files now explicitly import `OpenCodex.Api.Services.WebSearch`.
- `git diff --check` passed for touched WebSearch files.
- Trailing whitespace scan found no matches.

Remaining risks:

- Production public namespaces still mostly remain flat by design for compatibility.
- This unit improves module-local internal helper organization, but it is not a full class-library split.
- No automated rule currently enforces namespace/folder consistency.

Suggested next unit:

- Continue architecture-layering audit from the class-library guide.
- Candidate: evaluate whether `IUpstreamClient` should remain under `Services/Upstream` with flat namespace for compatibility or move toward an Abstractions-style boundary.
- Alternative candidate: align another internal-only implementation cluster before touching public contracts.

## Completed Architecture Layering Unit

### Upstream Client Contract Abstractions Boundary

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Move stable upstream client contracts out of the Services implementation namespace and into an Abstractions-style boundary.
- Preserve interface signatures, DI registrations, HTTP implementation behavior, tests, routes, and response compatibility.
- Make Services depend on upstream contracts while `ExternalIntegrations.HttpUpstreamClient` implements those contracts.

Implemented files:

- `src/OpenCodex.Api/Abstractions/IUpstreamClient.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyNonStreamService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.cs`
- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`
- `tests/OpenCodex.Api.Tests/ProxyControllerTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminChannelDiagnosticsServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyNonStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchSimulatorTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `IUpstreamClient` and `IUpstreamModelClient` from `src/OpenCodex.Api/Services/Upstream/IUpstreamClient.cs` to `src/OpenCodex.Api/Abstractions/IUpstreamClient.cs`.
- Changed their namespace from `OpenCodex.Api.Services` to `OpenCodex.Api.Abstractions`.
- Updated `HttpUpstreamClient` to implement the contracts from `OpenCodex.Api.Abstractions`.
- Updated DI registration to register `IUpstreamClient` / `IUpstreamModelClient` from `OpenCodex.Api.Abstractions`.
- Updated Admin, Proxy, and WebSearch services that depend on upstream clients to import `OpenCodex.Api.Abstractions`.
- Updated tests and fake upstream clients to import the new contract namespace.
- Left interface method signatures unchanged.
- Did not change controller routes, payload conversion, upstream request behavior, response shapes, or compatibility paths.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
find src/OpenCodex.Api/Services/Upstream -type f -maxdepth 1 | sort
rg -n "using OpenCodex.Api.Abstractions;|namespace OpenCodex.Api.Abstractions" src/OpenCodex.Api tests/OpenCodex.Api.Tests
rg -n "[ \t]+$" src/OpenCodex.Api/Abstractions/IUpstreamClient.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.cs src/OpenCodex.Api/Services/Proxy/ProxyNonStreamService.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.cs tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs tests/OpenCodex.Api.Tests/ProxyControllerTests.cs tests/OpenCodex.Api.Tests/Services/Admin/AdminChannelDiagnosticsServiceTests.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyNonStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchSimulatorTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Abstractions/IUpstreamClient.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs opencodex_proxy/src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyNonStreamService.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.cs opencodex_proxy/tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/ProxyControllerTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Admin/AdminChannelDiagnosticsServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyNonStreamServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchSimulatorTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused upstream/Admin/Proxy/WebSearch/controller tests passed: 69 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- `Services/Upstream` no longer contains source files.
- `IUpstreamClient` and `IUpstreamModelClient` now live in `OpenCodex.Api.Abstractions`.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- This is still an in-project Abstractions boundary, not a separate class library project yet.
- Several other service contracts remain in `OpenCodex.Api.Services`; only upstream integration contracts were moved in this unit.
- Tests cover DI replacement and behavior paths, but there is no automated architectural dependency rule yet.

Suggested next unit:

- Continue class-library split readiness by auditing remaining cross-layer contracts.
- Candidate: decide whether `IWebSearchClient` should remain under Services for now or move to an Abstractions boundary because `TavilyWebSearchClient` in ExternalIntegrations implements it.
- Alternative: add an architectural namespace/dependency smoke test before moving more public contracts.

## Completed Architecture Layering Unit

### WebSearch Client Contract Abstractions Boundary

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Move the external WebSearch provider client contract out of Services and into an Abstractions-style boundary.
- Avoid making Abstractions depend on full domain/database records.
- Preserve admin/proxy/WebSearch behavior, DI registration, routes, and response compatibility.

Implemented files:

- `src/OpenCodex.Api/Abstractions/IWebSearchClient.cs`
- `src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs`
- `src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs`
- `src/OpenCodex.Api/Services/Admin/AdminWebSearchService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminWebSearchTestResult.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs`
- `src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs`
- `tests/OpenCodex.Api.Tests/ExternalIntegrations/TavilyWebSearchClientTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminWebSearchServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchSimulatorTests.cs`
- `tests/OpenCodex.Api.Tests/ProxyControllerTests.cs`
- `tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `IWebSearchClient`, `WebSearchProviderResult`, and `WebSearchSummary` from `OpenCodex.Api.Services` to `OpenCodex.Api.Abstractions`.
- Added `WebSearchProviderKey` as a lightweight abstraction contract with only `Provider` and `Key`.
- Changed `IWebSearchClient.SearchAsync` to accept `WebSearchProviderKey` instead of full `TavilyKeyRecord`.
- Updated `TavilyWebSearchClient` to depend on `OpenCodex.Api.Abstractions` and no longer depend on `OpenCodex.Api.Services` or `OpenCodex.Api.Domain`.
- Updated Admin/WebSearch service call sites to map `TavilyKeyRecord` to `WebSearchProviderKey` at the boundary.
- Updated DTOs and internal WebSearch helpers to import provider result models from Abstractions.
- Updated tests and fake WebSearch clients to use `WebSearchProviderKey` and assert provider/key values explicitly.
- Left `TavilyKeyRecord` in Domain and persistence/service logic for configuration, quotas, usage count, and logging metadata.
- Did not change public routes, admin payload shapes, proxy response compatibility, or external Tavily request payload.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~TavilyWebSearchClientTests|FullyQualifiedName~AdminWebSearchServiceTests|FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
test -e src/OpenCodex.Api/Services/WebSearch/IWebSearchClient.cs; printf '%s\n' $?
rg -n "using OpenCodex.Api.Services;" src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs src/OpenCodex.Api/Abstractions/IWebSearchClient.cs tests/OpenCodex.Api.Tests/ExternalIntegrations/TavilyWebSearchClientTests.cs
rg -n "[ \t]+$" src/OpenCodex.Api/Abstractions/IWebSearchClient.cs src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs src/OpenCodex.Api/Services/Admin/AdminWebSearchService.cs src/OpenCodex.Api/Services/Admin/AdminWebSearchTestResult.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs tests/OpenCodex.Api.Tests/ExternalIntegrations/TavilyWebSearchClientTests.cs tests/OpenCodex.Api.Tests/Services/Admin/AdminWebSearchServiceTests.cs tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchSimulatorTests.cs tests/OpenCodex.Api.Tests/ProxyControllerTests.cs tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Abstractions/IWebSearchClient.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs opencodex_proxy/src/OpenCodex.Api/Infrastructure/OpenCodexServiceCollectionExtensions.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminWebSearchService.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminWebSearchTestResult.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs opencodex_proxy/src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs opencodex_proxy/tests/OpenCodex.Api.Tests/ExternalIntegrations/TavilyWebSearchClientTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Admin/AdminWebSearchServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/WebSearch/WebSearchSimulatorTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/ProxyControllerTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/AdminDataControllerTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused Tavily/AdminWebSearch/WebSearch/Proxy/Admin controller tests passed: 53 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 333 passed, 0 failed, 0 skipped.
- Old `Services/WebSearch/IWebSearchClient.cs` path no longer exists.
- `TavilyWebSearchClient` no longer imports `OpenCodex.Api.Services`.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- This is still an in-project Abstractions boundary, not a separate class library project.
- `WebSearchProviderResult` and `WebSearchSummary` are now shared abstraction models; future changes should be treated as public contract changes.
- No automated architectural dependency rule currently prevents ExternalIntegrations from re-importing Services.

Suggested next unit:

- Add an architecture dependency smoke test before moving more public contracts.
- Candidate checks:
  - `ExternalIntegrations` should not reference `OpenCodex.Api.Services`.
  - `Abstractions` should not reference `OpenCodex.Api.Services`, `OpenCodex.Api.Persistence`, or concrete external integrations.
  - `Domain` should not reference API/HTTP/Persistence/Infrastructure namespaces.

## Completed Architecture Layering Unit

### Architecture Dependency Smoke Tests

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Add automated regression coverage for the most important architecture dependency rules introduced during the recent boundary cleanup.
- Keep the test lightweight and source-based, without adding new NuGet dependencies.
- Avoid changing runtime code or public API behavior.

Implemented files:

- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ArchitectureDependencyTests`.
- Added a smoke test that fails if `src/OpenCodex.Api/ExternalIntegrations` references `OpenCodex.Api.Services`.
- Added a smoke test that fails if `src/OpenCodex.Api/Abstractions` references Services, Persistence, ExternalIntegrations, Infrastructure, ASP.NET Core, or Microsoft.Extensions namespaces.
- Added a smoke test that fails if `src/OpenCodex.Api/Domain` references Services, Persistence, ExternalIntegrations, Infrastructure, Controllers, DTOs, ASP.NET Core, or Microsoft.Extensions namespaces.
- Implemented repository-root discovery by walking upward from `AppContext.BaseDirectory` until `src/OpenCodex.Api` is found.
- Did not modify production code.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Focused architecture dependency tests passed: 3 passed, 0 failed, 0 skipped.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 336 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the new architecture test file.
- Trailing whitespace scan found no matches.

Remaining risks:

- This is a source-text smoke test, not a full Roslyn dependency analyzer.
- It checks namespace strings, so it will not catch every possible type-level dependency.
- It intentionally covers only the most important current boundaries; other layers still need broader rules later.

Suggested next unit:

- Expand architecture dependency coverage in small increments.
- Candidate: add checks for `Infrastructure` not referencing Controllers/DTOs and for `Persistence` not referencing Controllers/DTOs/Services implementations where practical.
- Alternative: continue moving remaining stable contracts into Abstractions only after test coverage protects the direction.

## Completed Architecture Layering Unit

### Persistence and Infrastructure Architecture Smoke Tests

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Extend architecture regression coverage without over-constraining current intentional composition/root infrastructure code.
- Protect Persistence from depending on upper layers.
- Protect Infrastructure from referencing API presentation DTO/controller layers.

Implemented files:

- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `PersistenceDoesNotReferenceOuterLayers`.
- The Persistence check forbids references to Controllers, DTOs, Services, Infrastructure, ExternalIntegrations, ASP.NET Core, and Microsoft.Extensions namespaces.
- Added `InfrastructureDoesNotReferenceApiPresentationLayers`.
- The Infrastructure check forbids references to Controllers and DTOs.
- Kept Infrastructure allowed to reference Services/Persistence for now because `OpenCodexServiceCollectionExtensions` is currently the in-project composition root and `AdminUiService` implements a Services contract.
- Did not modify production code.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Focused architecture dependency tests passed: 5 passed, 0 failed, 0 skipped.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 338 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the architecture test file.
- Trailing whitespace scan found no matches.

Remaining risks:

- Architecture checks are still text-based smoke tests.
- Infrastructure still contains composition-root registrations and an implementation of `IAdminUiService`, so broader Infrastructure restrictions need a separate refactor first.
- Persistence is now protected from several upper-layer references, but not yet split into a separate project.

Suggested next unit:

- Decide whether to move composition-root DI registration out of `Infrastructure` into a more API/Host-specific namespace before tightening Infrastructure dependency rules.
- Alternative: continue adding focused architecture checks for Controllers and DTO boundaries.

## Completed Architecture Layering Unit

### Hosting Boundary and Infrastructure Rule Tightening

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Move API/Host composition and startup extensions out of Infrastructure.
- Keep Infrastructure focused on technical helpers/middleware rather than DI composition over Services/Persistence/ExternalIntegrations.
- Tighten the Infrastructure architecture smoke test after the move.

Implemented files:

- `src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs`
- `src/OpenCodex.Api/Hosting/OpenCodexApplicationBuilderExtensions.cs`
- `src/OpenCodex.Api/Hosting/OpenCodexHostBuilderExtensions.cs`
- `src/OpenCodex.Api/Program.cs`
- `src/OpenCodex.Api/Services/Admin/AdminUiService.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `OpenCodexServiceCollectionExtensions`, `OpenCodexApplicationBuilderExtensions`, and `OpenCodexHostBuilderExtensions` from `OpenCodex.Api.Infrastructure` to `OpenCodex.Api.Hosting`.
- Updated `Program.cs` to import `OpenCodex.Api.Hosting`.
- Kept Hosting as the composition boundary that can reference Abstractions, Configuration, ExternalIntegrations, Infrastructure, Persistence, and Services.
- Moved `AdminUiService` from Infrastructure to `Services/Admin` because it implements `IAdminUiService`.
- Updated `AdminUiServiceTests` to use the Services namespace only.
- Tightened `InfrastructureDoesNotReferenceUpperOrConcreteOuterLayers` to forbid Infrastructure references to Controllers, DTOs, Services, Persistence, and ExternalIntegrations.
- Did not change public routes, DI registrations, middleware order, static admin UI behavior, or response compatibility.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminUiServiceTests|FullyQualifiedName~SmokeTests|FullyQualifiedName~AdminUiController|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "OpenCodex.Api.Services|OpenCodex.Api.Persistence|OpenCodex.Api.ExternalIntegrations|OpenCodex.Api.Controllers|OpenCodex.Api.DTOs" src/OpenCodex.Api/Infrastructure
rg -n "[ \t]+$" src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs src/OpenCodex.Api/Hosting/OpenCodexApplicationBuilderExtensions.cs src/OpenCodex.Api/Hosting/OpenCodexHostBuilderExtensions.cs src/OpenCodex.Api/Program.cs src/OpenCodex.Api/Services/Admin/AdminUiService.cs tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs opencodex_proxy/src/OpenCodex.Api/Hosting/OpenCodexApplicationBuilderExtensions.cs opencodex_proxy/src/OpenCodex.Api/Hosting/OpenCodexHostBuilderExtensions.cs opencodex_proxy/src/OpenCodex.Api/Program.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminUiService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Focused architecture dependency tests passed: 5 passed, 0 failed, 0 skipped.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused Admin UI / smoke / architecture tests passed: 21 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 338 passed, 0 failed, 0 skipped.
- Infrastructure residual scan found no references to Services, Persistence, ExternalIntegrations, Controllers, or DTOs.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- `Hosting` is still inside the API project, not a separate Host project.
- Architecture checks are still source-text smoke tests.
- `Infrastructure` still contains ASP.NET middleware and request/body helpers; further separation into pure infrastructure and web infrastructure may be considered later.

Suggested next unit:

- Add a Hosting-specific architecture smoke test that allows composition references but prevents Domain/Persistence/Abstractions from referencing Hosting.
- Alternative: continue tightening service implementation boundaries now that Hosting owns DI composition.

## Pending Next Task Reminder

Status: pending.

User instruction:

- Before the next implementation unit, carefully review the code class-library guidance and architecture layering documentation.
- Treat `CLASS_LIBRARY_GUIDE.md`, `PROJECT_ARCHITECTURE.md`, and `NEW_PROJECT_CHECKLIST.md` as the starting constraints for deciding the next unit.
- Do not migrate or move code only for mechanical cleanup; first confirm the change improves the intended layer boundary.

Recommended next unit:

- Add a Hosting-specific architecture smoke test that allows Hosting to act as the composition root, while preventing lower layers such as Abstractions, Domain, Persistence, Infrastructure, ExternalIntegrations, and Services from referencing `OpenCodex.Api.Hosting`.

Risks to check before coding:

- The current architecture tests are source-text smoke tests, not compiler-enforced project boundaries.
- Hosting currently lives inside the API project, so namespace rules must be precise and should not accidentally block `Program.cs`.
- If more than three files need changes, split the work into smaller approved units and record each completed unit here.

## Completed Architecture Layering Unit

### Hosting Reverse Dependency Rule

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`

Goal:

- Make the Hosting/composition-root boundary explicit in the architecture smoke tests.
- Allow `Program.cs` and Hosting extensions to use `OpenCodex.Api.Hosting`, while preventing lower layers from referencing Hosting.

Implemented files:

- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `LowerLayersDoNotReferenceHosting`.
- The new rule checks that Abstractions, Domain, Persistence, Infrastructure, ExternalIntegrations, and Services do not reference `OpenCodex.Api.Hosting`.
- Kept `Program.cs` outside the forbidden scan so the API entry point can continue to use Hosting as the composition root.
- Did not change runtime behavior, public routes, DI registrations, middleware order, or compatibility payloads.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Focused architecture dependency tests passed: 6 passed, 0 failed, 0 skipped.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 339 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched architecture test file.
- Trailing whitespace scan found no matches.

Remaining risks:

- Architecture checks are still source-text smoke tests rather than project-level compiler boundaries.
- Hosting still lives inside `OpenCodex.Api`, so the namespace rule protects direction but does not enforce a separate deployable host project.
- Controllers and DTO boundary rules can still be tightened in later units.

Suggested next unit:

- Review Controller/DTO boundary against `API_CODE_STYLE.md` and `PROJECT_ARCHITECTURE.md`, then add focused architecture tests that prevent Services, Domain, Persistence, Infrastructure, and ExternalIntegrations from referencing Controllers or API DTOs where not already covered.
- Alternative: audit Service layer for remaining direct framework/config/request dependencies and move stable contracts toward Abstractions or Domain only when it improves the layer boundary.

## Completed Architecture Layering Unit

### API Surface Reverse Dependency Rule

Status: completed.

Documents reviewed before implementation:

- `API_CODE_STYLE.md`
- `API_RESULT_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`

Goal:

- Make the Controller/DTO API-surface boundary explicit in architecture tests.
- Protect business, domain, persistence, infrastructure, external integration, and abstraction layers from depending on Controllers or API DTO namespaces.

Pre-change audit:

- Searched lower-layer directories for `OpenCodex.Api.Controllers` and `OpenCodex.Api.DTOs`.
- Found no references from Abstractions, Domain, Persistence, Infrastructure, ExternalIntegrations, or Services.
- Existing DTO references are limited to Controllers, DTO declarations, DTO-specific tests, architecture tests, and `Errors/ProxyErrorMiddleware`.
- `Errors/ProxyErrorMiddleware` remains outside this unit's scan because it is an API error/response boundary, not a lower business or data layer.

Implemented files:

- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `LowerLayersDoNotReferenceApiSurface`.
- The new rule checks that Abstractions, Domain, Persistence, Infrastructure, ExternalIntegrations, and Services do not reference `OpenCodex.Api.Controllers` or `OpenCodex.Api.DTOs`.
- Did not change runtime behavior, public routes, DTO contracts, DI registrations, middleware order, Swagger setup, or compatibility payloads.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Focused architecture dependency tests passed: 7 passed, 0 failed, 0 skipped.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 340 passed, 0 failed, 0 skipped.
- `git diff --check` passed for the touched architecture test file.
- Trailing whitespace scan found no matches.

Remaining risks:

- Architecture checks are still source-text smoke tests, so they do not replace future multi-project reference boundaries.
- API error middleware still depends on `DTOs.Results`; this is deliberate for now but should be revisited if `Errors` is later reclassified under Infrastructure.
- The new test protects namespace references, not semantic leakage through duplicated DTO-shaped records.

Suggested next unit:

- Audit Service layer for direct `Microsoft.AspNetCore` and `HttpContext` dependencies, then move any remaining request-specific inputs behind typed metadata or API-layer parameters.
- Alternative: tighten Controller thinness by adding targeted tests around representative Controllers and ServiceResult-to-ApiResult conversion paths.

## Completed Architecture Layering Unit

### Admin Services ASP.NET Dependency Reduction

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `API_CODE_STYLE.md`

Goal:

- Reduce framework-specific dependencies inside Admin services.
- Keep Admin UI file/path logic in the service layer, while avoiding direct `Microsoft.AspNetCore.*` references there.
- Add an architecture smoke test that prevents Admin services from reintroducing ASP.NET HTTP/controller types.

Pre-change audit:

- Searched `src/OpenCodex.Api/Services` for ASP.NET and HTTP-specific references.
- Found remaining framework references in two areas:
  - `Services/Admin/AdminUiService.cs` used `Microsoft.AspNetCore.StaticFiles` and `IWebHostEnvironment`.
  - `Services/Proxy/*` still uses `HttpRequest` / `HttpResponse` for endpoint and streaming orchestration.
- This unit intentionally handled only Admin services; Proxy endpoint orchestration remains a separate, higher-risk boundary unit.

Implemented files:

- `src/OpenCodex.Api/Services/Admin/AdminUiService.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Replaced `FileExtensionContentTypeProvider` with a local `ContentTypeFor` mapping for frontend/admin static assets.
- Changed `AdminUiService` from `IWebHostEnvironment` to the more general `IHostEnvironment`, since only `ContentRootPath` is needed.
- Added SVG content-type coverage to `AdminUiServiceTests`.
- Updated the Admin UI test fake environment to implement `IHostEnvironment`.
- Added `AdminServicesDoNotReferenceAspNetCore` architecture smoke test.
- Did not change public routes, admin auth flow, SPA fallback behavior, static asset path safety, or compatibility payloads.

Verification commands:

```bash
rg -n "Microsoft\.AspNetCore|HttpContext|HttpRequest|HttpResponse|IActionResult|ActionResult" src/OpenCodex.Api/Services/Admin
rg -n "IWebHostEnvironment|FileExtensionContentTypeProvider|Microsoft\.AspNetCore\.StaticFiles" src/OpenCodex.Api/Services/Admin tests/OpenCodex.Api.Tests/Services/Admin
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~AdminUiServiceTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Services/Admin/AdminUiService.cs tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminUiService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Admin services ASP.NET dependency scans found no matches.
- Focused Admin UI and architecture tests passed: 18 passed, 0 failed, 0 skipped.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 342 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- Local content-type mapping is intentionally smaller than ASP.NET's `FileExtensionContentTypeProvider`; it covers current admin frontend asset types but may need extension if new asset types are introduced.
- Proxy services still use `HttpRequest` / `HttpResponse` in endpoint and stream orchestration.
- The new Admin architecture test does not apply to all Services because Proxy endpoint streaming still needs a separate design step.

Suggested next unit:

- Audit `Services/Proxy` endpoint orchestration and decide whether `ProxyEndpointService`, `ProxyEndpointContext`, and `ProxyStreamContext` should move toward an API/endpoint boundary or be refactored to accept typed request metadata/body/response writer abstractions.
- Alternative: first add a focused characterization test around `ProxyEndpointService` before moving any HTTP-specific boundary.

## Completed Architecture Layering Unit

### Proxy Stream Writer Boundary

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `API_CODE_STYLE.md`

Goal:

- Reduce HTTP response coupling inside `ProxyStreamService`.
- Keep SSE header/body writing in Infrastructure while letting the stream service depend on a small output port.
- Preserve streaming behavior, TTFT measurement, request logging, route compatibility, and public `/v1/...` endpoints.

Pre-change audit:

- `ProxyEndpointService` built `ProxyStreamContext` with `HttpResponse`.
- `ProxyStreamContext` exposed `HttpResponse` directly to the service layer.
- `ProxyStreamService` called `ProxyStreamResponseWriter.PrepareSse(context.Response)` and `WriteLinesAsync(context.Response, ...)`.
- Existing tests covered stream body output, SSE headers, TTFT, and proxy logging.

Implemented files:

- `src/OpenCodex.Api/Abstractions/IProxyStreamWriter.cs`
- `src/OpenCodex.Api/Infrastructure/ProxyStreamResponseWriter.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `IProxyStreamWriter` in Abstractions.
- Changed `ProxyStreamContext` to carry `IProxyStreamWriter` instead of `HttpResponse`.
- Changed `ProxyStreamService` to call `context.StreamWriter.PrepareSse()` and `context.StreamWriter.WriteLinesAsync(...)`.
- Changed `ProxyStreamService` log status code from ASP.NET `StatusCodes.Status200OK` to literal `200`, removing that framework dependency from the stream service.
- Converted `ProxyStreamResponseWriter` from static-only helper to an Infrastructure adapter implementing `IProxyStreamWriter`, while preserving existing static methods for compatibility and tests.
- Updated `ProxyEndpointService` to create `new ProxyStreamResponseWriter(context.Response)` at the endpoint orchestration boundary.
- Updated stream service tests to use a fake `IProxyStreamWriter` instead of `DefaultHttpContext.Response`.
- Added `ProxyStreamServiceBoundaryDoesNotReferenceHttpResponse` architecture smoke test for `ProxyStreamService.cs` and `ProxyStreamContext.cs`.
- Did not change public routes, protocol conversion, upstream streaming behavior, SSE headers, response body line writing, or compatibility payloads.

Verification commands:

```bash
rg -n "HttpResponse|Microsoft\.AspNetCore|StatusCodes" src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyStreamResponseWriterTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
rg -n "Microsoft\.AspNetCore|OpenCodex\.Api\.Services|OpenCodex\.Api\.Infrastructure|OpenCodex\.Api\.Persistence|OpenCodex\.Api\.ExternalIntegrations" src/OpenCodex.Api/Abstractions
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Abstractions/IProxyStreamWriter.cs src/OpenCodex.Api/Infrastructure/ProxyStreamResponseWriter.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Abstractions/IProxyStreamWriter.cs opencodex_proxy/src/OpenCodex.Api/Infrastructure/ProxyStreamResponseWriter.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Stream service boundary scan found no `HttpResponse`, `Microsoft.AspNetCore`, or `StatusCodes` references.
- Focused proxy stream/endpoint/writer/architecture tests passed: 16 passed, 0 failed, 0 skipped.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Abstractions dependency scan found no concrete or ASP.NET references.
- Full .NET test suite passed: 343 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ProxyEndpointService` and `ProxyEndpointContext` still use `HttpRequest` / `HttpResponse` because request reading, authorization header extraction, metadata construction, and adapter creation remain at that endpoint orchestration boundary.
- `IRequestBodyReader` and `ProxyRequestMetadata.FromHttpRequest` still expose HTTP-specific APIs from Infrastructure.
- The new architecture rule protects only `ProxyStreamService.cs` and `ProxyStreamContext.cs`, not the whole Proxy service folder.

Suggested next unit:

- Refactor `ProxyEndpointContext` / `ProxyEndpointService` to accept typed request inputs where practical, or explicitly move endpoint orchestration into a more API-specific boundary if it should remain HTTP-aware.
- Alternative: move `ProxyRequestMetadata` to Abstractions or Services only after removing its `FromHttpRequest` factory from the portable record.

## Completed Architecture Layering Unit

### Proxy Endpoint Typed Request Boundary

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `API_CODE_STYLE.md`

Goal:

- Remove direct HTTP request/response dependencies from `ProxyEndpointService` and `ProxyEndpointContext`.
- Move HTTP body reading, Authorization header extraction, request metadata construction, and response writer adapter creation to the Controller/API boundary.
- Keep proxy authentication, route choice, protocol conversion, streaming/non-streaming branching, and logging behavior inside the service layer.

Pre-change audit:

- `ProxyEndpointContext` carried `HttpRequest`, `HttpResponse`, and `ClientIp`.
- `ProxyEndpointService` injected `IRequestBodyReader`, read `HttpRequest`, extracted Authorization header, called `ProxyRequestMetadata.FromHttpRequest`, and created `ProxyStreamResponseWriter(context.Response)`.
- Existing service/controller tests covered non-stream context construction, stream context construction, bad request logging, bearer access key behavior, streaming SSE, and proxy logs.

Implemented files:

- `src/OpenCodex.Api/Controllers/ProxyController.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Changed `ProxyEndpointContext` to contain typed service inputs:
  - `Payload`
  - `AuthorizationHeader`
  - `RequestMetadata`
  - `StreamWriter`
  - `CancellationToken`
- Updated `ProxyController` to:
  - inject `IRequestBodyReader`
  - read JSON request body
  - extract Authorization header
  - construct `ProxyRequestMetadata` from the current request
  - construct `ProxyStreamResponseWriter` from the current response
- Removed `IRequestBodyReader` from `ProxyEndpointService`.
- Removed direct `HttpRequest`, `HttpResponse`, `AuthorizationHeader(HttpRequest)`, and `ProxyRequestMetadata.FromHttpRequest(...)` usage from `ProxyEndpointService`.
- Replaced remaining `StatusCodes.Status200OK` usage in `ProxyEndpointService` with literal `200`.
- Updated `ProxyEndpointServiceTests` to construct typed contexts directly and use a fake `IProxyStreamWriter`.
- Extended architecture smoke test to protect `ProxyEndpointService.cs`, `ProxyEndpointContext.cs`, `ProxyStreamService.cs`, and `ProxyStreamContext.cs` from `HttpRequest`, `HttpResponse`, `Microsoft.AspNetCore`, `StatusCodes`, `IRequestBodyReader`, and `ProxyRequestMetadata.FromHttpRequest`.
- Did not change public routes, protocol conversion, access key validation semantics, error payload compatibility, streaming SSE behavior, or log persistence.

Verification commands:

```bash
rg -n "IRequestBodyReader|HttpRequest|HttpResponse|Microsoft\.AspNetCore|StatusCodes|ProxyRequestMetadata\.FromHttpRequest" src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --filter "FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/ProxyController.cs src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/ProxyController.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Proxy endpoint/stream service boundary scan found no forbidden HTTP/framework references.
- Focused proxy endpoint/controller/architecture tests passed: 26 passed, 0 failed, 0 skipped.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 343 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ProxyRequestMetadata` still lives in Infrastructure and still has an HTTP factory (`FromHttpRequest`); Controller now calls it, but the portable metadata record and HTTP factory are still coupled.
- `IRequestBodyReader` remains an Infrastructure HTTP helper used by Controllers.
- The proxy service layer still depends on Infrastructure helpers such as `JsonDictionaryValue` and `ProxyRequestMetadata`.

Suggested next unit:

- Split `ProxyRequestMetadata` into a portable record and an HTTP factory/mapper so Services can depend on the record without depending on Infrastructure.
- Alternative: audit Services usage of Infrastructure helpers (`JsonDictionaryValue`, `WebSearchPayload`, request metadata) and decide which should move to Abstractions, Domain, or a narrower shared utility namespace.

## Completed Architecture Layering Unit

### Proxy Request Metadata Factory Split

Status: completed.

Documents reviewed before implementation:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `API_CODE_STYLE.md`

Goal:

- Split proxy request metadata into a portable service-layer contract and an HTTP-specific Infrastructure factory.
- Keep Services depending on metadata data, not on the HTTP request mapping implementation.
- Preserve request method/path/client IP/header logging and Authorization header redaction behavior.

Pre-change audit:

- `ProxyRequestMetadata` lived in Infrastructure and contained both the metadata record and `FromHttpRequest(HttpRequest, ...)`.
- Proxy service contexts and log service used `ProxyRequestMetadata` as data.
- Controller and some service tests used `ProxyRequestMetadata.FromHttpRequest`.
- Header redaction behavior was tested indirectly through `ProxyLogServiceTests`.

Implemented files:

- `src/OpenCodex.Api/Abstractions/ProxyRequestMetadata.cs`
- `src/OpenCodex.Api/Infrastructure/ProxyRequestMetadataFactory.cs`
- `src/OpenCodex.Api/Controllers/ProxyController.cs`
- `src/OpenCodex.Api/Services/Proxy/IProxyLogService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyNonStreamContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyLogService.cs`
- `tests/OpenCodex.Api.Tests/Infrastructure/ProxyRequestMetadataFactoryTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyLogServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyNonStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved portable `ProxyRequestMetadata` record into `OpenCodex.Api.Abstractions`.
- Replaced the old Infrastructure record/factory combo with `ProxyRequestMetadataFactory`.
- Updated `ProxyController` to call `ProxyRequestMetadataFactory.FromHttpRequest`.
- Updated proxy service contexts and `IProxyLogService` to use the Abstractions metadata record.
- Updated `ProxyLogServiceTests` to verify log service consumption of metadata directly.
- Added `ProxyRequestMetadataFactoryTests` to verify HTTP method/path/client IP/header extraction and Authorization redaction.
- Updated non-stream/endpoint/stream service tests to construct `ProxyRequestMetadata` directly from Abstractions.
- Updated architecture smoke test to forbid `ProxyRequestMetadataFactory` in endpoint/stream service boundaries.
- Did not change public routes, response payloads, log table shape, header redaction format, streaming behavior, or protocol conversion.

Verification commands:

```bash
rg -n "ProxyRequestMetadata\.FromHttpRequest|ProxyRequestMetadataFactory|Microsoft\.AspNetCore|HttpRequest|HttpResponse|StatusCodes|IRequestBodyReader" src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs src/OpenCodex.Api/Services/Proxy/ProxyNonStreamContext.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamService.cs src/OpenCodex.Api/Services/Proxy/IProxyLogService.cs
rg -n "OpenCodex\.Api\.Services|OpenCodex\.Api\.Infrastructure|OpenCodex\.Api\.Persistence|OpenCodex\.Api\.ExternalIntegrations|Microsoft\.AspNetCore|Microsoft\.Extensions" src/OpenCodex.Api/Abstractions
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyRequestMetadataFactoryTests|FullyQualifiedName~ProxyLogServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyStreamServiceTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Abstractions/ProxyRequestMetadata.cs src/OpenCodex.Api/Infrastructure/ProxyRequestMetadataFactory.cs src/OpenCodex.Api/Controllers/ProxyController.cs src/OpenCodex.Api/Services/Proxy/IProxyLogService.cs src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs src/OpenCodex.Api/Services/Proxy/ProxyNonStreamContext.cs src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs src/OpenCodex.Api/Services/Proxy/ProxyLogService.cs tests/OpenCodex.Api.Tests/Infrastructure/ProxyRequestMetadataFactoryTests.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyLogServiceTests.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyNonStreamServiceTests.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Abstractions/ProxyRequestMetadata.cs opencodex_proxy/src/OpenCodex.Api/Infrastructure/ProxyRequestMetadataFactory.cs opencodex_proxy/src/OpenCodex.Api/Controllers/ProxyController.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/IProxyLogService.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyEndpointContext.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyNonStreamContext.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyStreamContext.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyLogService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Infrastructure/ProxyRequestMetadataFactoryTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyLogServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyNonStreamServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyEndpointServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Services/Proxy/ProxyStreamServiceTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Proxy endpoint/stream/log metadata boundary scan found no forbidden HTTP/factory references.
- Abstractions scan found no concrete layer or ASP.NET/Microsoft.Extensions references.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused metadata/proxy/architecture tests passed: 34 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 345 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- `ProxyEndpointService` and other services still depend on Infrastructure helpers such as `JsonDictionaryValue`.
- Web search services still use `WebSearchPayload` helpers from Infrastructure.
- `ProxyAccessService` still uses ASP.NET `StatusCodes` for exception status codes; this is separate from request metadata and should be handled deliberately.

Suggested next unit:

- Audit `JsonDictionaryValue` and `WebSearchPayload` usage to decide whether these are generic JSON/domain payload helpers that should move out of Infrastructure.
- Alternative: handle `ProxyAccessService` status code dependency by introducing service-level error/status constants, then add a focused architecture rule.

## Completed Architecture Layering Unit

### JsonDictionaryValue Abstractions Boundary

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`

Goal:

- Move the lightweight dynamic JSON dictionary accessor out of Infrastructure.
- Let Controllers, Services, and ExternalIntegrations depend on a stable shared helper without treating it as infrastructure implementation.
- Keep behavior and public helper API unchanged.

Pre-change audit:

- `JsonDictionaryValue` lived in Infrastructure but was used by Controllers, Admin services, Proxy services, and `HttpUpstreamClient`.
- The helper only performs dictionary lookup, trimming string conversion, list extraction, and nested object extraction.
- It has no dependency on web framework, configuration, logging, persistence, external SDKs, or DI.
- `WebSearchPayload` is heavier and WebSearch-specific, so it was intentionally left for a separate unit.

Implemented files:

- `src/OpenCodex.Api/Abstractions/JsonDictionaryValue.cs`
- `tests/OpenCodex.Api.Tests/Abstractions/JsonDictionaryValueTests.cs`
- `src/OpenCodex.Api/Services/Admin/AdminConfigService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.ChannelDraft.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.Compat.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs`
- `src/OpenCodex.Api/Controllers/AdminUsersController.cs`
- `src/OpenCodex.Api/Controllers/AdminApiKeysController.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Streaming.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Requests.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Responses.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `JsonDictionaryValue` from `OpenCodex.Api.Infrastructure` to `OpenCodex.Api.Abstractions`.
- Moved `JsonDictionaryValueTests` from Infrastructure tests to Abstractions tests.
- Added `OpenCodex.Api.Abstractions` imports for affected Controllers, Services, and ExternalIntegrations.
- Added `InfrastructureDoesNotOwnJsonDictionaryValue` architecture test to prevent the helper file from returning to Infrastructure.
- Did not change helper method names, return types, dictionary semantics, or behavior.

Verification commands:

```bash
rg -n "JsonDictionaryValue" src/OpenCodex.Api tests/OpenCodex.Api.Tests
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~JsonDictionaryValueTests|FullyQualifiedName~AdminConfigServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~ProxyEndpointServiceTests|FullyQualifiedName~ProxyLogServiceTests|FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Abstractions/JsonDictionaryValue.cs tests/OpenCodex.Api.Tests/Abstractions/JsonDictionaryValueTests.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs src/OpenCodex.Api/Services/Admin/AdminConfigService.cs src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.ChannelDraft.cs src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.Compat.cs src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs src/OpenCodex.Api/Controllers/AdminUsersController.cs src/OpenCodex.Api/Controllers/AdminApiKeysController.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Streaming.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Requests.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Responses.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Abstractions/JsonDictionaryValue.cs opencodex_proxy/src/OpenCodex.Api/Infrastructure/JsonDictionaryValue.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Abstractions/JsonDictionaryValueTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Infrastructure/JsonDictionaryValueTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminConfigService.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.ChannelDraft.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.Compat.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminUsersController.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminApiKeysController.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Streaming.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Requests.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Responses.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused helper/admin/proxy/upstream/architecture tests passed: 46 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 345 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- `WebSearchPayload` still lives in Infrastructure even though WebSearch services use it heavily.
- Some service files still import Infrastructure for other helpers such as `ConfigValue`, `ProxyRequestMetadataFactory`, or `WebSearchPayload`.
- Moving too many generic helpers into Abstractions could turn Abstractions into a broad utility bucket, so future moves should stay narrow.

Suggested next unit:

- Audit and move `WebSearchPayload` into `Services/WebSearch` if it is truly WebSearch-specific, or into Abstractions only if it proves broadly shared.
- Alternative: handle `ProxyAccessService` status code dependency by introducing service-level status constants and a focused architecture rule.

## Next Task Memory

Status: queued.

User reminder:

- Before the next implementation task, carefully review the code class-library guidance and architecture layering documents.
- The next unit should be driven by `CLASS_LIBRARY_GUIDE.md` and `PROJECT_ARCHITECTURE.md`, not only by local compile errors or namespace convenience.
- Pay attention to class-library boundaries, dependency direction, and layer ownership before moving types.
- If a type is shared across Services and ExternalIntegrations, do not place it in a layer that would create reverse dependencies.

Recommended next unit:

- Continue the in-progress `WebSearchPayload` boundary cleanup only after re-reading:
  - `opencodex_proxy/CLASS_LIBRARY_GUIDE.md`
  - `opencodex_proxy/PROJECT_ARCHITECTURE.md`
- Confirm the final placement decision against the documented layering rules, then finish the remaining references and architecture test.

Risk note:

- `Abstractions` should not become a generic utility bucket; only move helpers there when they are stable contracts or cross-layer primitives with no framework/infrastructure dependency.

## Completed Architecture Layering Unit

### WebSearchPayload Abstractions Boundary

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`

Goal:

- Finish the in-progress `WebSearchPayload` boundary cleanup.
- Keep WebSearch service helpers and Tavily external integration on a stable shared payload helper.
- Avoid placing the helper in `Services/WebSearch`, because `ExternalIntegrations/TavilyWebSearchClient` also needs it and ExternalIntegrations must not depend on Services.
- Avoid placing the helper in Infrastructure, because it is protocol/payload conversion logic rather than infrastructure implementation.

Layering decision:

- `CLASS_LIBRARY_GUIDE.md` says Abstractions may contain lightweight contract models, enums/constants, result types, and small stable shared primitives.
- `CLASS_LIBRARY_GUIDE.md` also warns not to introduce a broad dependency just to reuse a small helper.
- `PROJECT_ARCHITECTURE.md` says lower layers must not reverse-depend on upper layers and ExternalIntegrations should convert external results into internal result objects.
- `WebSearchPayload` has no dependency on ASP.NET Core, DI, configuration, persistence, logging, external SDKs, or concrete infrastructure.
- Current placement is therefore `OpenCodex.Api.Abstractions`, with a guard test to prevent it from drifting back into Infrastructure.

Implemented files:

- `src/OpenCodex.Api/Abstractions/WebSearchPayload.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchContinuationRequest.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchRequestPolicy.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchResponsePayload.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationLog.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchStreamEventState.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchToolCallParser.cs`
- `src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs`
- `src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Moved `WebSearchPayload` out of Infrastructure into `OpenCodex.Api.Abstractions`.
- Updated WebSearch service helpers to import `OpenCodex.Api.Abstractions.WebSearchPayload`.
- Removed stale Infrastructure imports from `WebSearchToolResult` and `TavilyWebSearchClient`.
- Added `InfrastructureDoesNotOwnWebSearchPayload` architecture test.
- Confirmed `WebSearchPayload.cs` exists only under `src/OpenCodex.Api/Abstractions`.
- Confirmed no `OpenCodex.Api.Infrastructure.WebSearchPayload` or `using static OpenCodex.Api.Infrastructure.WebSearchPayload` references remain.
- Did not change payload conversion behavior, SSE line injection behavior, Tavily response parsing behavior, or public routes.

Verification commands:

```bash
rg -n "OpenCodex\.Api\.Infrastructure\.WebSearchPayload|using static OpenCodex\.Api\.Infrastructure\.WebSearchPayload" opencodex_proxy/src/OpenCodex.Api opencodex_proxy/tests/OpenCodex.Api.Tests
find opencodex_proxy/src/OpenCodex.Api -name 'WebSearchPayload.cs' -print
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~WebSearchSimulatorTests|FullyQualifiedName~WebSearchRequestPolicyTests|FullyQualifiedName~WebSearchToolCallParserTests|FullyQualifiedName~TavilyWebSearchClientTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Abstractions/WebSearchPayload.cs src/OpenCodex.Api/Services/WebSearch/WebSearchContinuationRequest.cs src/OpenCodex.Api/Services/WebSearch/WebSearchRequestPolicy.cs src/OpenCodex.Api/Services/WebSearch/WebSearchResponsePayload.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationLog.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs src/OpenCodex.Api/Services/WebSearch/WebSearchStreamEventState.cs src/OpenCodex.Api/Services/WebSearch/WebSearchToolCallParser.cs src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Abstractions/WebSearchPayload.cs opencodex_proxy/src/OpenCodex.Api/Infrastructure/WebSearchPayload.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchContinuationRequest.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchRequestPolicy.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchResponsePayload.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulationLog.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.NonStream.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchSimulator.Streaming.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchStreamEventState.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchToolCallParser.cs opencodex_proxy/src/OpenCodex.Api/Services/WebSearch/WebSearchToolResult.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/TavilyWebSearchClient.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused WebSearch/Tavily/proxy/architecture tests passed: 57 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 347 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- `Abstractions` now contains both `JsonDictionaryValue` and `WebSearchPayload`; future shared helpers should still be judged individually to avoid creating a generic utility bucket.
- Some Services/Admin and ExternalIntegrations files still import Infrastructure for other types such as `ConfigValue` or request/response infrastructure helpers.
- The repository currently keeps all .NET layers inside one API project, so architecture tests are the practical guard until real class-library projects are split.

Suggested next unit:

- Audit remaining service-layer `using OpenCodex.Api.Infrastructure` imports and classify them by ownership:
  - pure shared primitive -> Abstractions or Domain only if stable and cross-layer;
  - infrastructure implementation -> keep in Infrastructure and move call boundary upward;
  - request/HTTP-specific adapter -> keep near Controller/Infrastructure and pass typed context into Services.
- Good next candidate: inspect `ConfigValue` usage in Admin services and decide whether it is service/domain config semantics or infrastructure parsing.

## Completed Architecture Layering Unit

### Service and External Integration HTTP Status Boundary

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`

Goal:

- Remove ASP.NET Core `StatusCodes` coupling from Services and ExternalIntegrations.
- Preserve the exact HTTP status values returned by proxy/admin/upstream exceptions.
- Keep Controller, Middleware, and Infrastructure free to use ASP.NET HTTP primitives where they belong.

Pre-change audit:

- `ConfigValue` was inspected first and found to already live in `OpenCodex.Api.Config`, not Infrastructure.
- Remaining service/external integration framework leakage was mainly `StatusCodes` usage, plus several stale `using OpenCodex.Api.Infrastructure` imports.
- `HttpUpstreamClient` used `StatusCodes` for upstream timeout/bad-gateway errors.
- `AdminSessionService`, `ProxyAccessService`, and `ProxyNonStreamService` used `StatusCodes` for service-layer status values.

Layering decision:

- HTTP status numbers are part of project error semantics when carried by `ProxyException`.
- Services and ExternalIntegrations should not reference ASP.NET Core just to use numeric constants.
- A project-owned status constant type belongs in `OpenCodex.Api.Errors`, because it supports exception/error expression and has no framework dependency.
- The type is named `ProxyHttpStatus` rather than `ProxyStatusCodes` so architecture text scans can still forbid ASP.NET `StatusCodes` safely.

Implemented files:

- `src/OpenCodex.Api/Errors/ProxyHttpStatus.cs`
- `src/OpenCodex.Api/Errors/ProxyException.cs`
- `src/OpenCodex.Api/Errors/BadRequestException.cs`
- `src/OpenCodex.Api/Errors/RoutingException.cs`
- `src/OpenCodex.Api/Errors/UpstreamException.cs`
- `src/OpenCodex.Api/Services/Admin/AdminSessionService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyAccessService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyNonStreamService.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Responses.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Streaming.cs`
- `src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Requests.cs`
- `src/OpenCodex.Api/Services/Admin/AdminConfigService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.ChannelDraft.cs`
- `src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.Compat.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs`
- `src/OpenCodex.Api/Services/Proxy/ProxyLogService.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProxyHttpStatus` constants for 200, 400, 401, 403, 500, 502, and 504.
- Replaced service-layer `StatusCodes` usage with `ProxyHttpStatus`.
- Replaced `HttpUpstreamClient` timeout/bad-gateway `StatusCodes` usage with `ProxyHttpStatus`.
- Updated default status values in `ProxyException`, `BadRequestException`, `RoutingException`, and `UpstreamException`.
- Removed stale Infrastructure imports from Services and `HttpUpstreamClient` partial files where no infrastructure type was used.
- Added architecture guard `ServicesAndExternalIntegrationsDoNotReferenceAspNetCoreStatusCodes`.
- Did not change exception payload shapes, route behavior, status code values, or middleware behavior.

Verification commands:

```bash
rg -n "StatusCodes|Microsoft\.AspNetCore|using OpenCodex\.Api\.Infrastructure;" opencodex_proxy/src/OpenCodex.Api/Services opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture
rg -n "ProxyStatusCodes|ProxyHttpStatus|StatusCodes" opencodex_proxy/src/OpenCodex.Api/Errors opencodex_proxy/src/OpenCodex.Api/Services opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyErrorTests|FullyQualifiedName~HttpUpstreamClientTests|FullyQualifiedName~AdminSessionServiceTests|FullyQualifiedName~ProxyAccessServiceTests|FullyQualifiedName~ProxyNonStreamServiceTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Errors/ProxyHttpStatus.cs src/OpenCodex.Api/Errors/ProxyException.cs src/OpenCodex.Api/Errors/BadRequestException.cs src/OpenCodex.Api/Errors/RoutingException.cs src/OpenCodex.Api/Errors/UpstreamException.cs src/OpenCodex.Api/Services/Admin/AdminSessionService.cs src/OpenCodex.Api/Services/Proxy/ProxyAccessService.cs src/OpenCodex.Api/Services/Proxy/ProxyNonStreamService.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Responses.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Streaming.cs src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Requests.cs src/OpenCodex.Api/Services/Admin/AdminConfigService.cs src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.cs src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.ChannelDraft.cs src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.Compat.cs src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs src/OpenCodex.Api/Services/Proxy/ProxyLogService.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Errors/ProxyHttpStatus.cs opencodex_proxy/src/OpenCodex.Api/Errors/ProxyStatusCodes.cs opencodex_proxy/src/OpenCodex.Api/Errors/ProxyException.cs opencodex_proxy/src/OpenCodex.Api/Errors/BadRequestException.cs opencodex_proxy/src/OpenCodex.Api/Errors/RoutingException.cs opencodex_proxy/src/OpenCodex.Api/Errors/UpstreamException.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminSessionService.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyAccessService.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyNonStreamService.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Responses.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Streaming.cs opencodex_proxy/src/OpenCodex.Api/ExternalIntegrations/HttpUpstreamClient.Requests.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminConfigService.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.ChannelDraft.cs opencodex_proxy/src/OpenCodex.Api/Services/Admin/AdminChannelDiagnosticsService.Compat.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyEndpointService.cs opencodex_proxy/src/OpenCodex.Api/Services/Proxy/ProxyLogService.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused error/upstream/session/access/non-stream/architecture tests passed: 45 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 348 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- Tests still use ASP.NET `StatusCodes` in several places; this is acceptable for integration/controller/infrastructure assertions, but service tests could later assert against `ProxyHttpStatus` to mirror service-layer rules.
- `ProxyErrorMiddleware`, Controllers, and Infrastructure still use ASP.NET primitives by design.
- `ProxyHttpStatus` is intentionally numeric and small; do not grow it into an HTTP abstraction layer.

Suggested next unit:

- Audit remaining HTTP/framework references in DTOs and API-surface query models, especially `DTOs/AdminObservability/LogFilterQuery.cs`.
- Alternatively, inspect whether `Errors` should have a focused architecture rule that allows ASP.NET only in middleware but keeps exception classes framework-neutral.

## Completed Architecture Layering Unit

### DTO Query Framework Boundary

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`

Goal:

- Remove ASP.NET Core query collection dependency from the DTO layer.
- Keep HTTP request/query adaptation in the Controller/API surface.
- Preserve admin observability filter behavior: allowed keys only, empty values ignored, unknown keys ignored, and excluded key omitted.

Checklist alignment:

- `NEW_PROJECT_CHECKLIST.md` calls out DTO placement, Controller-to-Service boundaries, API design, and clear layering.
- DTOs should express API payload/filter shape, not depend on ASP.NET request abstractions.
- Controller remains the correct place to translate `Request.Query` into plain values.

Implemented files:

- `src/OpenCodex.Api/DTOs/AdminObservability/LogFilterQuery.cs`
- `src/OpenCodex.Api/Controllers/AdminObservabilityController.cs`
- `tests/OpenCodex.Api.Tests/DTOs/AdminObservability/LogFilterQueryTests.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Changed `LogFilterQuery.FromQuery` to accept `IEnumerable<KeyValuePair<string, string?>>` instead of `IQueryCollection`.
- Added `AdminObservabilityController.QueryValues()` to adapt `Request.Query` at the API layer.
- Updated `LogFilterQueryTests` to use a plain dictionary instead of ASP.NET `QueryCollection`.
- Added `DtosDoNotReferenceAspNetCore` architecture guard.
- Confirmed no ASP.NET/Core HTTP references remain under `src/OpenCodex.Api/DTOs`.
- Did not change allowed filter keys, excluded-key behavior, or empty/unknown filter handling.

Verification commands:

```bash
rg -n "Microsoft\.AspNetCore|IQueryCollection|HttpContext|HttpRequest|HttpResponse|IActionResult|ActionResult" opencodex_proxy/src/OpenCodex.Api/DTOs
rg -n "LogFilterQuery\.FromQuery|QueryValues\(" opencodex_proxy/src/OpenCodex.Api opencodex_proxy/tests/OpenCodex.Api.Tests
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~LogFilterQueryTests|FullyQualifiedName~AdminObservabilityServiceTests|FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~ProxyControllerTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/DTOs/AdminObservability/LogFilterQuery.cs src/OpenCodex.Api/Controllers/AdminObservabilityController.cs tests/OpenCodex.Api.Tests/DTOs/AdminObservability/LogFilterQueryTests.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/DTOs/AdminObservability/LogFilterQuery.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminObservabilityController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/DTOs/AdminObservability/LogFilterQueryTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused DTO/observability/controller/architecture tests passed: 58 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 349 passed, 0 failed, 0 skipped.
- DTO ASP.NET reference scan found no matches.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- `AdminObservabilityController.QueryValues()` currently flattens multi-value query parameters with `values.ToString()`, matching prior `IQueryCollection` behavior.
- Other API surface DTOs may still need review for validation attributes, examples, and Swagger documentation coverage.
- Controller still owns query parsing, which is intentional; avoid moving `Request.Query` access into Services or DTO helpers.

Suggested next unit:

- Add/strengthen an architecture rule that exception classes under `Errors` remain framework-neutral while `ProxyErrorMiddleware` is allowed to depend on ASP.NET.
- Alternatively, audit Swagger/API documentation coverage for DTO request/response examples against `API_CODE_STYLE.md` and `API_RESULT_GUIDE.md`.

## Completed Architecture Guard Unit

### Framework-Neutral Proxy Exception Types

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`

Goal:

- Guard the previous status-code boundary work.
- Keep proxy exception and status value classes framework-neutral.
- Continue allowing `ProxyErrorMiddleware` to use ASP.NET Core types because it is request pipeline code.

Pre-change audit:

- `Errors/ProxyErrorMiddleware.cs` is the only source file in `Errors` that references `HttpContext`, `RequestDelegate`, `ILogger`, or ASP.NET `StatusCodes`.
- `BadRequestException`, `ProxyException`, `ProxyHttpStatus`, `RoutingException`, and `UpstreamException` no longer reference ASP.NET Core.
- No business behavior needed to change; this unit only adds an architecture guard.

Implemented files:

- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProxyExceptionTypesDoNotReferenceAspNetCore`.
- The guard checks only:
  - `BadRequestException.cs`
  - `ProxyException.cs`
  - `ProxyHttpStatus.cs`
  - `RoutingException.cs`
  - `UpstreamException.cs`
- The guard forbids `Microsoft.AspNetCore`, `StatusCodes`, HTTP request/response types, middleware delegate/logger types, and MVC action-result types in those exception files.
- It intentionally does not include `ProxyErrorMiddleware.cs`.

Verification commands:

```bash
rg -n "Microsoft\.AspNetCore|StatusCodes|HttpContext|HttpRequest|HttpResponse|RequestDelegate|ILogger|IActionResult|ActionResult" opencodex_proxy/src/OpenCodex.Api/Errors/BadRequestException.cs opencodex_proxy/src/OpenCodex.Api/Errors/ProxyException.cs opencodex_proxy/src/OpenCodex.Api/Errors/ProxyHttpStatus.cs opencodex_proxy/src/OpenCodex.Api/Errors/RoutingException.cs opencodex_proxy/src/OpenCodex.Api/Errors/UpstreamException.cs
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyErrorTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Exception-file ASP.NET reference scan found no matches.
- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused error/architecture tests passed: 21 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 350 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched file.
- Trailing whitespace scan found no matches.

Remaining risks:

- This is an architecture guard only; it does not improve Swagger examples or API documentation coverage.
- Architecture tests use text scanning, so type names should be chosen to avoid accidental substring conflicts.
- Middleware remains in the same `Errors` folder as exceptions; if the project is later split into class libraries, middleware may belong under Infrastructure or Hosting.

Suggested next unit:

- Review `API_CODE_STYLE.md` and `API_RESULT_GUIDE.md`, then audit Swagger/API documentation response examples and unified result wrapping exceptions.
- Alternatively, inspect whether middleware should move from `Errors` to Infrastructure/Hosting after class-library boundaries are further refined.

## Completed API Documentation Unit

### Swagger Basic Metadata

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `API_CODE_STYLE.md`
- `API_RESULT_GUIDE.md`

Goal:

- Improve the currently minimal Swagger/OpenAPI setup without changing existing route or response compatibility.
- Add basic API title/version/description metadata required by the API documentation checklist.
- Keep `/v1/*` proxy response shapes and existing admin payload shapes unchanged.

Pre-change audit:

- Swagger was enabled in Development through Swashbuckle.
- Existing smoke tests only checked that `/swagger/v1/swagger.json` was available and contained key routes.
- `AddSwaggerGen()` had no explicit document metadata.
- `API_CODE_STYLE.md` calls out interface documentation coverage for request method/route, request parameters/examples, response structure/examples, errors, and pagination.
- This unit only addresses the basic document metadata slice; examples and error descriptions remain future work.

Implemented files:

- `src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs`
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Configured `AddSwaggerGen` with `SwaggerDoc("v1", new OpenApiInfo { ... })`.
- Set Swagger title to `OpenCodex Proxy API`.
- Set Swagger version to `v1`.
- Set description to `Admin, observability, and OpenAI-compatible proxy endpoints.`
- Updated smoke test to assert `info.title`, `info.version`, and description content.
- Used the .NET 10/OpenAPI package namespace `Microsoft.OpenApi`, not the older `Microsoft.OpenApi.Models` namespace.
- Did not change Swagger environment exposure: Development only.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs tests/OpenCodex.Api.Tests/SmokeTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Smoke tests passed: 7 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 350 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- Swagger still lacks rich request/response examples and explicit error-code descriptions.
- The OpenAPI document still reflects current compatibility payloads, including unwrapped `/v1/*` proxy responses and legacy admin success payloads.
- If response wrapping is normalized later, Swagger tests and compatibility tests must be updated together.

Suggested next unit:

- Add a focused OpenAPI test for representative response schemas or examples without forcing runtime response wrapping.
- Alternatively, audit admin API response payload DTO names/shapes against `API_CODE_STYLE.md` before adding examples.

## Completed API Documentation Unit

### Admin Observability OpenAPI Response Schemas

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `API_CODE_STYLE.md`
- `API_RESULT_GUIDE.md`

Goal:

- Improve representative Swagger response documentation without changing runtime JSON payloads.
- Cover admin observability endpoints because they include list, detail, stats, and not-found responses.
- Preserve existing compatibility response shapes, including unwrapped admin payloads.

Pre-change audit:

- `AdminObservabilityController` returned concrete DTOs but had no explicit OpenAPI response metadata.
- `SmokeTests.SwaggerDocumentIsAvailableInDevelopment` checked route presence but not representative response schemas.
- `API_CODE_STYLE.md` requires interface docs to cover response structure and examples/errors; this unit addresses response schema metadata only.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminObservabilityController.cs`
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProducesResponseType` metadata for:
  - `GET /admin/api/logs` -> `LogsPageResponse` 200.
  - `GET /admin/api/log-filter-options` -> `IReadOnlyList<string>` 200.
  - `GET /admin/api/logs/{logId}` -> `LogDetailResponse` 200 and `AdminErrorResponse` 404.
  - `GET /admin/api/stats` -> `StatsResponse` 200.
- Added Swagger smoke-test assertions for representative response schema `$ref`s:
  - `LogsPageResponse`
  - `LogDetailResponse`
  - `AdminErrorResponse`
  - `StatsResponse`
- Did not change controller action bodies, service calls, status codes, or JSON field names.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminObservabilityServiceTests|FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~LogFilterQueryTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminObservabilityController.cs tests/OpenCodex.Api.Tests/SmokeTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminObservabilityController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Smoke tests passed: 7 passed, 0 failed, 0 skipped.
- Focused observability/controller/query tests passed: 31 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 350 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.
- Trailing whitespace scan found no matches.

Remaining risks:

- OpenAPI still lacks concrete example payloads for observability endpoints.
- Other admin controllers still need response schema metadata.
- `/v1/*` proxy endpoints remain intentionally broad/unwrapped for compatibility and should be documented separately.

Suggested next unit:

- Apply the same response-schema metadata pattern to one more admin controller with create/update/delete flows, likely `AdminUsersController` or `AdminApiKeysController`.
- Alternatively, add examples for the observability schemas after deciding example format and maintenance cost.

## Next Task Memory

### Architecture and Class Library Layering Review

Status: queued by user.

User reminder:

- Before the next implementation unit, pay attention to the code class libraries and architecture layering documents.
- The next task should prioritize this architecture/class-library layering review and record progress here.

Documents to read first:

- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `REPOSITORY_GUIDE.md`
- `AUTOFAC_GUIDE.md`
- `NEW_PROJECT_CHECKLIST.md`

Proposed next-task boundary:

- Audit current project references, namespaces, DTO placement, services, repositories, infrastructure integrations, and dependency injection registration against the architecture/layering documents.
- Identify violations where lower layers depend on API/web framework details, where Infrastructure owns business DTOs/contracts, or where responsibilities are placed in the wrong class library.
- Fix in small units if code changes exceed 3 files, listing the goal, involved files, and risks before each unit.

Compatibility guardrails:

- Preserve `/v1/responses`, `/v1/chat/completions`, `/v1/messages`, `/admin/api/...` route and response compatibility.
- Do not wrap `/v1/*` compatibility payloads in `ApiResult`.
- Keep `ProxyException` response shape as `{"error":{"message":"...","type":"..."}}`.

## Completed Architecture Layering Unit

### Admin UI Static Directory Configuration Boundary

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- Existing architecture guard tests in `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`

Goal:

- Continue code normalization from the architecture/class-library layering documents.
- Remove direct configuration-key and host-environment access from `AdminUiService`.
- Keep admin SPA/static asset behavior unchanged.
- Add a guard test so Service-layer code does not drift back into direct host configuration reads.

Pre-change audit:

- The project currently uses one `.csproj` with layered directories/namespaces rather than separate class-library projects.
- Existing architecture tests already guard many lower-layer dependencies.
- `AdminUiService` directly depended on `IConfiguration` and `IHostEnvironment`, read `OpenCodex:AdminStaticPath` / `OPENCODEX_ADMIN_STATIC_PATH`, and used `ContentRootPath`.
- This conflicted with `CLASS_LIBRARY_GUIDE.md` guidance that Application/Service code should not directly read configuration keys and should receive configuration through a clearer abstraction.

Implemented files:

- `src/OpenCodex.Api/Configuration/IAdminStaticDirectoryProvider.cs`
- `src/OpenCodex.Api/Configuration/AdminStaticDirectoryProvider.cs`
- `src/OpenCodex.Api/Services/Admin/AdminUiService.cs`
- `src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs`
- `tests/OpenCodex.Api.Tests/Configuration/AdminStaticDirectoryProviderTests.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `IAdminStaticDirectoryProvider` as the Service-layer-facing abstraction for admin static directory resolution.
- Added `AdminStaticDirectoryProvider` in Configuration to own:
  - `OpenCodex:AdminStaticPath`
  - `OPENCODEX_ADMIN_STATIC_PATH`
  - old root layout fallback
  - packaged root layout fallback
- Changed `AdminUiService` to depend on `IAdminStaticDirectoryProvider` instead of `IConfiguration` / `IHostEnvironment`.
- Registered `IAdminStaticDirectoryProvider` in the DI setup.
- Moved static directory resolution coverage from `AdminUiServiceTests` into focused `AdminStaticDirectoryProviderTests`.
- Kept `AdminUiServiceTests` focused on SPA index serving, asset content types, traversal safety, and HTML output.
- Added `ServicesDoNotReadHostConfigurationDirectly` architecture test to block direct Service-layer use of:
  - `IConfiguration`
  - `IHostEnvironment`
  - `IWebHostEnvironment`
  - `IOptions`
  - direct configuration section/key access markers
  - host environment path/name markers

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminUiServiceTests|FullyQualifiedName~AdminStaticDirectoryProviderTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "IConfiguration|IHostEnvironment|IWebHostEnvironment|IOptions|Configuration\[|GetSection\(|ContentRootPath|EnvironmentName" src/OpenCodex.Api/Services -g "*.cs"
rg -n "[ \t]+$" src/OpenCodex.Api/Configuration/AdminStaticDirectoryProvider.cs src/OpenCodex.Api/Configuration/IAdminStaticDirectoryProvider.cs src/OpenCodex.Api/Services/Admin/AdminUiService.cs src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs tests/OpenCodex.Api.Tests/Configuration/AdminStaticDirectoryProviderTests.cs tests/OpenCodex.Api.Tests/Services/Admin/AdminUiServiceTests.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Architecture tests passed: 15 passed, 0 failed, 0 skipped.
- Focused Admin UI/config tests passed: 13 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 354 passed, 0 failed, 0 skipped.
- Service-layer configuration/host scan found no matches.
- Trailing whitespace scan found no matches.

Remaining risks:

- This improves the single-project layered architecture but does not split the code into separate class-library projects.
- `Configuration` still uses framework configuration/host abstractions intentionally because it is the boundary that resolves host settings.
- There may be other Service-layer boundaries to audit beyond direct configuration access, especially DTO dependencies and protocol/config responsibilities.
- Admin UI HTML is still produced by the service; this was left unchanged to avoid mixing a behavior move into this configuration-boundary unit.

Suggested next unit:

- Continue architecture/class-library review with DTO-to-Service dependencies. Current audit found API DTO response files that reference `OpenCodex.Api.Services`; decide whether those are acceptable API mapping dependencies or should be normalized via dedicated API mapper/factory methods.
- Alternatively, continue the previously queued Swagger metadata unit for `AdminUsersController` after the layering audit is complete enough.

## Completed Architecture Layering Unit

### AdminAuth DTO Service Dependency Boundary

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- Existing architecture guard tests in `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`

Goal:

- Continue DTO placement and API-layer conversion normalization from the architecture/class-library documents.
- Remove the direct dependency from `DTOs/AdminAuth` to `OpenCodex.Api.Services`.
- Keep session/login/logout response JSON unchanged.
- Add a focused guard test before expanding the pattern to other DTO modules.

Pre-change audit:

- `AdminAuthResponses.cs` imported `OpenCodex.Api.Services` only to map `AdminSessionUser` into response DTOs.
- `PROJECT_ARCHITECTURE.md` says API layer converts business results to API results.
- `CLASS_LIBRARY_GUIDE.md` places API Request/Response DTOs in API/Contracts, while Service-internal DTOs belong in Application.
- Moving this mapping into `AdminAuthController` is a small, low-risk step because the DTO receives only primitive response fields and no behavior changes are required.

Implemented files:

- `src/OpenCodex.Api/DTOs/AdminAuth/AdminAuthResponses.cs`
- `src/OpenCodex.Api/Controllers/AdminAuthController.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Removed `using OpenCodex.Api.Services` from `AdminAuthResponses.cs`.
- Changed `AdminSessionResponse.From` to accept `username`, `role`, and `enabled` values instead of `AdminSessionUser`.
- Removed `AdminSessionUserResponse.From(AdminSessionUser)`.
- Added a private `SessionResponse(AdminSessionUser?)` mapper in `AdminAuthController`, keeping service/session type mapping at the API boundary.
- Added `AdminAuthDtosDoNotReferenceServices` architecture guard test.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
rg -n "using OpenCodex\.Api\.Services|OpenCodex\.Api\.Services" src/OpenCodex.Api/DTOs/AdminAuth -g "*.cs"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminAuth|FullyQualifiedName~AdminSession|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/DTOs/AdminAuth/AdminAuthResponses.cs src/OpenCodex.Api/Controllers/AdminAuthController.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/DTOs/AdminAuth/AdminAuthResponses.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminAuthController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- AdminAuth DTO Service-reference scan found no matches.
- Architecture tests passed: 16 passed, 0 failed, 0 skipped.
- Focused AdminAuth/AdminSession/Smoke tests passed: 19 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 355 passed, 0 failed, 0 skipped.
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- Other DTO modules still reference `OpenCodex.Api.Services`:
  - `DTOs/AdminChannelDiagnostics`
  - `DTOs/AdminConfig`
  - `DTOs/AdminWebSearch`
  - `DTOs/Results` via `Services.Results`
- `ApiResult` / `ServiceResult` mapping may be intentional unified-result plumbing and should be evaluated separately from admin payload DTOs.
- This unit intentionally did not add broad `DTOsDoNotReferenceServices` because multiple known modules still need separate, safer migrations.

Suggested next unit:

- Apply the same DTO boundary pattern to `AdminChannelDiagnostics` or `AdminConfig`, choosing the smaller module first and preserving response JSON.
- Alternatively, decide whether `ApiResult` should keep referencing `ServiceResult` or move conversion into an API result mapper.

## Completed Architecture Layering Unit

### AdminChannelDiagnostics DTO Service Dependency Boundary

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- Existing architecture guard tests in `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`

Goal:

- Continue DTO-to-Service dependency normalization in API response DTO modules.
- Remove the direct dependency from `DTOs/AdminChannelDiagnostics` to `OpenCodex.Api.Services`.
- Keep channel diagnostics response JSON unchanged.
- Add a module-level architecture guard before broadening to all DTOs.

Pre-change audit:

- `AdminChannelDiagnosticsResponses.cs` imported `OpenCodex.Api.Services` only for:
  - `AdminDiscoverModelsResult`
  - `AdminChannelTestResult`
- The DTOs used those service result types only in static `From` methods.
- `PROJECT_ARCHITECTURE.md` places business-result-to-API-result conversion at the API layer.
- `CLASS_LIBRARY_GUIDE.md` keeps API DTOs in API/Contracts and Service-internal DTOs in Application/Services.

Implemented files:

- `src/OpenCodex.Api/DTOs/AdminChannelDiagnostics/AdminChannelDiagnosticsResponses.cs`
- `src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Removed `using OpenCodex.Api.Services` from `AdminChannelDiagnosticsResponses.cs`.
- Changed `DiscoverModelsResponse.From` to accept `models`, `raw`, and `durationMs` instead of `AdminDiscoverModelsResult`.
- Changed `TestChannelResponse.From` to accept `model`, `upstreamModel`, `compat`, `response`, and `durationMs` instead of `AdminChannelTestResult`.
- Updated `AdminChannelDiagnosticsController` to pass primitive/contract fields from service results into response DTOs.
- Added `AdminChannelDiagnosticsDtosDoNotReferenceServices` architecture guard test.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
rg -n "using OpenCodex\.Api\.Services|OpenCodex\.Api\.Services" src/OpenCodex.Api/DTOs/AdminChannelDiagnostics -g "*.cs"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminChannelDiagnostics|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "using OpenCodex\.Api\.Services|OpenCodex\.Api\.Services" src/OpenCodex.Api/DTOs -g "*.cs"
rg -n "[ \t]+$" src/OpenCodex.Api/DTOs/AdminChannelDiagnostics/AdminChannelDiagnosticsResponses.cs src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/DTOs/AdminChannelDiagnostics/AdminChannelDiagnosticsResponses.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminChannelDiagnosticsController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- AdminChannelDiagnostics DTO Service-reference scan found no matches.
- Architecture tests passed: 17 passed, 0 failed, 0 skipped.
- Focused AdminChannelDiagnostics/Smoke tests passed: 17 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 356 passed, 0 failed, 0 skipped.
- Remaining DTO-to-Service scan now only reports:
  - `DTOs/AdminConfig/AdminConfigResponses.cs`
  - `DTOs/AdminWebSearch/AdminWebSearchResponses.cs`
  - `DTOs/Results/ApiResult.cs`
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- `AdminConfig` and `AdminWebSearch` DTO modules still directly reference Service types.
- `ApiResult` still references `Services.Results`; this may be intentional result plumbing but should be evaluated before adding a broad DTO guard.
- This unit preserved response shape but did not add explicit JSON snapshot tests for diagnostics payloads.

Suggested next unit:

- Apply the same DTO boundary pattern to `AdminConfig`, because it has one service result dependency (`AdminConfigImportResult`) plus existing domain mapping.
- Then handle `AdminWebSearch`, which has a larger response graph and may need more careful mapper extraction.

## Completed Architecture Layering Unit

### AdminConfig DTO Service Dependency Boundary

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- Existing architecture guard tests in `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`

Goal:

- Continue DTO-to-Service dependency normalization in API response DTO modules.
- Remove the direct dependency from `DTOs/AdminConfig` to `OpenCodex.Api.Services`.
- Keep config read/export/import/save response JSON unchanged.
- Add a module-level architecture guard before broadening to all DTOs.

Pre-change audit:

- `AdminConfigResponses.cs` imported `OpenCodex.Api.Services` only for `AdminConfigImportResult`.
- `ConfigImportResponse.From` used that service result type only to map `Config`, `Imported`, `Skipped`, and `SkippedIds`.
- `PROJECT_ARCHITECTURE.md` places business-result-to-API-result conversion at the API layer.
- `CLASS_LIBRARY_GUIDE.md` keeps API DTOs in API/Contracts and Service-internal DTOs in Application/Services.

Implemented files:

- `src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs`
- `src/OpenCodex.Api/Controllers/AdminConfigController.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Removed `using OpenCodex.Api.Services` from `AdminConfigResponses.cs`.
- Changed `ConfigImportResponse.From` to accept `config`, `imported`, `skipped`, and `skippedIds` fields instead of `AdminConfigImportResult`.
- Updated `AdminConfigController` to pass fields from `result.Data` into `ConfigImportResponse.From`.
- Added `AdminConfigDtosDoNotReferenceServices` architecture guard test.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
rg -n "using OpenCodex\.Api\.Services|OpenCodex\.Api\.Services" src/OpenCodex.Api/DTOs/AdminConfig -g "*.cs"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminConfig|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "using OpenCodex\.Api\.Services|OpenCodex\.Api\.Services" src/OpenCodex.Api/DTOs -g "*.cs"
rg -n "[ \t]+$" src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs src/OpenCodex.Api/Controllers/AdminConfigController.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/DTOs/AdminConfig/AdminConfigResponses.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminConfigController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- AdminConfig DTO Service-reference scan found no matches.
- Architecture tests passed: 18 passed, 0 failed, 0 skipped.
- Focused AdminConfig/Smoke tests passed: 15 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 357 passed, 0 failed, 0 skipped.
- Remaining DTO-to-Service scan now only reports:
  - `DTOs/AdminWebSearch/AdminWebSearchResponses.cs`
  - `DTOs/Results/ApiResult.cs`
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- `AdminWebSearch` DTO module still directly references Service result types and has a larger nested response graph.
- `ApiResult` still references `Services.Results`; this may be intentional unified result plumbing but should be evaluated before adding a broad DTO guard.
- Domain record mapping remains in config response DTOs, which appears acceptable for API response projection but can be revisited if DTOs are moved to a separate Contracts class library later.

Suggested next unit:

- Apply the DTO boundary pattern to `AdminWebSearch`, likely by moving `AdminWebSearchTestResult` mapping into `AdminWebSearchController` first.
- After `AdminWebSearch` is clean, evaluate whether `DTOs/Results/ApiResult` should keep depending on `Services.Results` or use a dedicated API result mapper.

## Completed Architecture Layering Unit

### AdminWebSearch DTO Service Dependency Boundary

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- Existing architecture guard tests in `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`

Goal:

- Finish admin payload DTO-to-Service dependency normalization.
- Remove the direct dependency from `DTOs/AdminWebSearch` to `OpenCodex.Api.Services`.
- Keep web-search config and test-key response JSON unchanged.
- Add a module-level architecture guard before evaluating the remaining `ApiResult` / `ServiceResult` dependency.

Pre-change audit:

- `AdminWebSearchResponses.cs` imported `OpenCodex.Api.Services` only for `AdminWebSearchTestResult`.
- Nested test-key response types already depended on `Abstractions` and `Domain` records:
  - `TavilyKeyRecord`
  - `WebSearchProviderResult`
  - `WebSearchSummary`
  - `WebSearchConfigRecord`
- The only Service-owned type in the DTO module was the top-level `AdminWebSearchTestResult` wrapper.
- Moving that wrapper mapping into `AdminWebSearchController` keeps API result conversion at the API boundary.

Implemented files:

- `src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs`
- `src/OpenCodex.Api/Controllers/AdminWebSearchController.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Removed `using OpenCodex.Api.Services` from `AdminWebSearchResponses.cs`.
- Changed `WebSearchTestKeyResponsePayload.From` to accept `key`, `result`, `config`, and `durationMs` instead of `AdminWebSearchTestResult`.
- Updated `AdminWebSearchController` to pass fields from `test.Data` into `WebSearchTestKeyResponsePayload.From`.
- Added `AdminWebSearchDtosDoNotReferenceServices` architecture guard test.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
rg -n "using OpenCodex\.Api\.Services|OpenCodex\.Api\.Services" src/OpenCodex.Api/DTOs/AdminWebSearch -g "*.cs"
rg -n "using OpenCodex\.Api\.Services|OpenCodex\.Api\.Services" src/OpenCodex.Api/DTOs -g "*.cs"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminWebSearch|FullyQualifiedName~WebSearchTestKeyRequest|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs src/OpenCodex.Api/Controllers/AdminWebSearchController.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/DTOs/AdminWebSearch/AdminWebSearchResponses.cs opencodex_proxy/src/OpenCodex.Api/Controllers/AdminWebSearchController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- AdminWebSearch DTO Service-reference scan found no matches.
- Broad DTO-to-Service scan now only reports `DTOs/Results/ApiResult.cs`.
- Architecture tests passed: 19 passed, 0 failed, 0 skipped.
- Focused AdminWebSearch/WebSearch request/Smoke tests passed: 20 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 358 passed, 0 failed, 0 skipped.
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- `DTOs/Results/ApiResult.cs` still references `Services.Results`.
- AdminWebSearch DTOs still map Domain and Abstractions records directly. This is currently acceptable for API projection, but may need mapper extraction if DTOs later move into a separate Contracts class library.
- No dedicated JSON snapshot test was added for the test-key response; existing integration/smoke coverage passed.

Suggested next unit:

- Evaluate the remaining `ApiResult` / `ServiceResult` dependency and decide whether to:
  - keep it as intentional unified-result plumbing, or
  - move conversion into an API result mapper so all DTOs stop referencing Services.
- Once resolved, add a broad DTO architecture guard if appropriate.

## Completed Architecture Layering Unit

### ErrorItem Abstraction and Broad DTO Service Guard

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `PROJECT_ARCHITECTURE.md`
- `CLASS_LIBRARY_GUIDE.md`
- Existing architecture guard tests in `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`

Goal:

- Resolve the last DTO-to-Service dependency.
- Keep `ApiResult` and `ServiceResult` compatible while avoiding DTO-layer references to Services.
- Put the shared error item contract in a neutral lower-level boundary.
- Add a broad guard so future DTO modules cannot reference `OpenCodex.Api.Services`.

Pre-change audit:

- Broad DTO-to-Service scan only reported `DTOs/Results/ApiResult.cs`.
- `ApiResult` did not depend on `ServiceResult`; it only reused `Services.Results.ErrorItem`.
- `ErrorItem` is a simple shared error contract used by both API results and service results.
- `CLASS_LIBRARY_GUIDE.md` allows Abstractions to contain lightweight contract models and base error/result types.

Implemented files:

- `src/OpenCodex.Api/Abstractions/ErrorItem.cs`
- `src/OpenCodex.Api/Services/Results/ErrorItem.cs`
- `src/OpenCodex.Api/Services/Results/ServiceResult.cs`
- `src/OpenCodex.Api/DTOs/Results/ApiResult.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ErrorItem` under `OpenCodex.Api.Abstractions`.
- Removed the old `Services.Results.ErrorItem` file.
- Updated `ServiceResult` to use `OpenCodex.Api.Abstractions.ErrorItem`.
- Updated `ApiResult` to use `OpenCodex.Api.Abstractions.ErrorItem`.
- Added broad `DtosDoNotReferenceServices` architecture guard over `src/OpenCodex.Api/DTOs`.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
rg -n "using OpenCodex\.Api\.Services|OpenCodex\.Api\.Services" src/OpenCodex.Api/DTOs -g "*.cs"
find src/OpenCodex.Api -name "ErrorItem.cs" -print
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ProxyErrorTests|FullyQualifiedName~AdminAuth|FullyQualifiedName~AdminUser|FullyQualifiedName~AdminApiKey|FullyQualifiedName~AdminConfig|FullyQualifiedName~AdminObservability|FullyQualifiedName~AdminWebSearch|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Abstractions/ErrorItem.cs src/OpenCodex.Api/DTOs/Results/ApiResult.cs src/OpenCodex.Api/Services/Results/ServiceResult.cs tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Abstractions/ErrorItem.cs opencodex_proxy/src/OpenCodex.Api/Services/Results/ErrorItem.cs opencodex_proxy/src/OpenCodex.Api/DTOs/Results/ApiResult.cs opencodex_proxy/src/OpenCodex.Api/Services/Results/ServiceResult.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Broad DTO-to-Service scan found no matches.
- `ErrorItem.cs` now exists only under `src/OpenCodex.Api/Abstractions`.
- Architecture tests passed: 20 passed, 0 failed, 0 skipped.
- Focused result/admin/smoke tests passed: 62 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 359 passed, 0 failed, 0 skipped.
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- `ServiceResult` remains in the Services layer, which is acceptable for service-owned results but should not be referenced from DTOs.
- `ErrorItem` is now part of Abstractions, so future changes to its shape should be treated as shared contract changes.
- This completed the DTO-to-Service dependency cleanup, but other checklist areas still need audit, such as Swagger metadata coverage and controller response documentation.

Suggested next unit:

- Resume OpenAPI response metadata work, starting with `AdminUsersController` or `AdminApiKeysController`.
- Alternatively, run another architecture audit for remaining checklist areas: repository boundaries, DI registration grouping, or service-to-protocol/config dependencies.

## Completed API Documentation Unit

### AdminUsers OpenAPI Response Schemas

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `API_CODE_STYLE.md`
- `API_RESULT_GUIDE.md`
- Existing Swagger smoke coverage in `tests/OpenCodex.Api.Tests/SmokeTests.cs`

Goal:

- Continue interface documentation normalization from the checklist.
- Add explicit OpenAPI response schema metadata for admin user management endpoints.
- Preserve admin runtime payload shapes and route compatibility.
- Keep admin success payloads unwrapped, matching current compatibility guardrails.

Pre-change audit:

- `AdminUsersController` returned concrete DTOs but did not declare `ProducesResponseType` metadata.
- Existing Swagger smoke coverage asserted route presence and observability response schemas, but not user-management schemas.
- Runtime responses were already shaped as:
  - `UsersResponse`
  - `UserResponsePayload`
  - `DeleteUserResponse`
  - `AdminErrorResponse`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminUsersController.cs`
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProducesResponseType` metadata for:
  - `GET /admin/api/users` -> `UsersResponse` 200.
  - `POST /admin/api/users` -> `UserResponsePayload` 201 and `AdminErrorResponse` 400.
  - `PATCH /admin/api/users/{username}` -> `UserResponsePayload` 200, `AdminErrorResponse` 400, and `AdminErrorResponse` 404.
  - `DELETE /admin/api/users/{username}` -> `DeleteUserResponse` 200, `AdminErrorResponse` 400, and `AdminErrorResponse` 404.
- Extended `SmokeTests.SwaggerDocumentIsAvailableInDevelopment` with schema `$ref` assertions for all of the above.
- Did not change controller bodies, service calls, status codes, JSON fields, or route aliases.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminUser|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminUsersController.cs tests/OpenCodex.Api.Tests/SmokeTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminUsersController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Swagger smoke tests passed: 7 passed, 0 failed, 0 skipped.
- Focused AdminUser/AdminData tests passed: 33 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 359 passed, 0 failed, 0 skipped.
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- OpenAPI still lacks example payloads for user-management endpoints.
- Other admin controllers still need response schema metadata, especially `AdminApiKeysController`.
- `/v1/*` proxy endpoints remain intentionally broad/unwrapped for compatibility and should be documented separately.

Suggested next unit:

- Apply the same response-schema metadata pattern to `AdminApiKeysController`.
- Then consider examples/error-code documentation once all admin controllers have baseline schemas.

## Completed API Documentation Unit

### AdminApiKeys OpenAPI Response Schemas

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `API_CODE_STYLE.md`
- `API_RESULT_GUIDE.md`
- Existing Swagger smoke coverage in `tests/OpenCodex.Api.Tests/SmokeTests.cs`

Goal:

- Continue interface documentation normalization from the checklist.
- Add explicit OpenAPI response schema metadata for admin API key management endpoints.
- Preserve admin runtime payload shapes and route compatibility.
- Keep admin success payloads unwrapped, matching current compatibility guardrails.

Pre-change audit:

- `AdminApiKeysController` returned concrete DTOs but did not declare `ProducesResponseType` metadata.
- Existing Swagger smoke coverage covered observability and users schemas, but not API key-management schemas.
- Runtime responses were already shaped as:
  - `ApiKeysResponse`
  - `ApiKeyResponsePayload`
  - `DeleteApiKeyResponse`
  - `AdminErrorResponse`

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminApiKeysController.cs`
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProducesResponseType` metadata for:
  - `GET /admin/api/api-keys` -> `ApiKeysResponse` 200.
  - `POST /admin/api/api-keys` -> `ApiKeyResponsePayload` 201 and `AdminErrorResponse` 400.
  - `PATCH /admin/api/api-keys/{keyId}` -> `ApiKeyResponsePayload` 200, `AdminErrorResponse` 400, and `AdminErrorResponse` 404.
  - `DELETE /admin/api/api-keys/{keyId}` -> `DeleteApiKeyResponse` 200, `AdminErrorResponse` 400, and `AdminErrorResponse` 404.
- Extended `SmokeTests.SwaggerDocumentIsAvailableInDevelopment` with schema `$ref` assertions for all of the above.
- Confirmed Swagger normalizes `{keyId:long}` route constraint to `/admin/api/api-keys/{keyId}`.
- Did not change controller bodies, service calls, status codes, JSON fields, or route aliases.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminApiKey|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminApiKeysController.cs tests/OpenCodex.Api.Tests/SmokeTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminApiKeysController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Swagger smoke tests passed: 7 passed, 0 failed, 0 skipped.
- Focused AdminApiKey/AdminData tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 359 passed, 0 failed, 0 skipped.
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- OpenAPI still lacks example payloads for API key-management endpoints.
- Other admin controllers still need baseline response schema metadata, such as auth, config, web search, and channel diagnostics.
- `/v1/*` proxy endpoints remain intentionally broad/unwrapped for compatibility and should be documented separately.

Suggested next unit:

- Continue baseline response schema metadata with `AdminAuthController` or `AdminConfigController`.
- After baseline schemas are in place, add examples/error-code documentation in a separate unit.

## Completed API Documentation Unit

### AdminAuth OpenAPI Response Schemas

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `API_CODE_STYLE.md`
- `API_RESULT_GUIDE.md`
- Existing Swagger smoke coverage in `tests/OpenCodex.Api.Tests/SmokeTests.cs`

Goal:

- Continue baseline response schema metadata for admin controllers.
- Add explicit OpenAPI response schema metadata for admin session/login/logout endpoints.
- Preserve login/session runtime behavior, cookie behavior, route aliases, and JSON payload shapes.
- Keep admin success payloads unwrapped, matching current compatibility guardrails.

Pre-change audit:

- `AdminAuthController` returned concrete DTOs but did not declare `ProducesResponseType` metadata.
- Login failure uses `AdminLoginErrorResponse`, not the shared `AdminErrorResponse`.
- Logout is exposed through both `/admin/logout` and `/admin/api/logout`.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminAuthController.cs`
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProducesResponseType` metadata for:
  - `GET /admin/api/session` -> `AdminSessionResponse` 200.
  - `POST /admin/api/login` -> `AdminSessionResponse` 200 and `AdminLoginErrorResponse` 401.
  - `POST /admin/api/logout` -> `AdminSessionResponse` 200.
  - `POST /admin/logout` -> `AdminSessionResponse` 200.
- Extended `SmokeTests.SwaggerDocumentIsAvailableInDevelopment` with schema `$ref` assertions for all of the above.
- Did not change controller bodies, authentication logic, session cookie handling, status codes, JSON fields, or route aliases.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminAuth|FullyQualifiedName~AdminSession|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminAuthController.cs tests/OpenCodex.Api.Tests/SmokeTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminAuthController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Swagger smoke tests passed: 7 passed, 0 failed, 0 skipped.
- Focused AdminAuth/AdminSession/AdminData tests passed: 37 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 359 passed, 0 failed, 0 skipped.
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- OpenAPI still lacks example payloads for auth endpoints.
- Other admin controllers still need baseline response schema metadata, such as config, web search, and channel diagnostics.
- Login request body metadata is not yet documented beyond the existing generated schema behavior.

Suggested next unit:

- Continue baseline response schema metadata with `AdminConfigController`.
- After baseline schemas are in place, add examples/error-code documentation in a separate unit.

## Completed API Documentation Unit

### AdminConfig OpenAPI Response Schemas

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `API_CODE_STYLE.md`
- `API_RESULT_GUIDE.md`
- Existing Swagger smoke coverage in `tests/OpenCodex.Api.Tests/SmokeTests.cs`

Goal:

- Continue baseline response schema metadata for admin controllers.
- Add explicit OpenAPI response schema metadata for config read/export/import/save endpoints.
- Preserve config runtime behavior, exported file content, headers, status codes, and JSON payload shapes.
- Keep admin success payloads unwrapped, matching current compatibility guardrails.

Pre-change audit:

- `AdminConfigController` returned concrete DTOs but did not declare `ProducesResponseType` metadata.
- `GET /admin/api/config/export` returns `Content(export.Payload, "application/json")` with a `Content-Disposition` header, not an object result.
- The export payload is still JSON serialized from `ConfigResponse`, so documenting the 200 response as `ConfigResponse` matches the exported content structure.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminConfigController.cs`
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProducesResponseType` metadata for:
  - `GET /admin/api/config` -> `ConfigResponse` 200.
  - `GET /admin/api/config/export` -> `ConfigResponse` 200.
  - `POST /admin/api/config/import` -> `ConfigImportResponse` 200 and `AdminErrorResponse` 400.
  - `POST /admin/api/config` -> `ConfigResponse` 200 and `AdminErrorResponse` 400.
- Extended `SmokeTests.SwaggerDocumentIsAvailableInDevelopment` with schema `$ref` assertions for all of the above.
- Did not change controller bodies, import/export logic, content-disposition behavior, status codes, JSON fields, or routes.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminConfig|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminConfigController.cs tests/OpenCodex.Api.Tests/SmokeTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminConfigController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Swagger smoke tests passed: 7 passed, 0 failed, 0 skipped.
- Focused AdminConfig/AdminData tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 359 passed, 0 failed, 0 skipped.
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- OpenAPI still lacks example payloads for config endpoints.
- Export response is documented as `ConfigResponse` because its content is JSON matching that shape, but the runtime still returns a raw `ContentResult` with a download header.
- Other admin controllers still need baseline response schema metadata, such as web search and channel diagnostics.

Suggested next unit:

- Continue baseline response schema metadata with `AdminWebSearchController`.
- Then handle channel diagnostics response schemas for both canonical and compatibility routes.

## Completed API Documentation Unit

### AdminWebSearch OpenAPI Response Schemas

Status: completed.

Documents reviewed before implementation:

- `NEW_PROJECT_CHECKLIST.md`
- `API_CODE_STYLE.md`
- `API_RESULT_GUIDE.md`
- Existing Swagger smoke coverage in `tests/OpenCodex.Api.Tests/SmokeTests.cs`

Goal:

- Continue baseline response schema metadata for admin controllers.
- Add explicit OpenAPI response schema metadata for web-search config and test-key endpoints.
- Preserve web-search config save/test logic, status codes, route compatibility, and JSON payload shapes.
- Keep admin success payloads unwrapped, matching current compatibility guardrails.

Pre-change audit:

- `AdminWebSearchController` returned concrete DTOs but did not declare `ProducesResponseType` metadata.
- `GET` and save responses use `WebSearchConfigResponse`.
- `test-key` success uses `WebSearchTestKeyResponsePayload`.
- Bad request paths use `AdminErrorResponse`.

Implemented files:

- `src/OpenCodex.Api/Controllers/AdminWebSearchController.cs`
- `tests/OpenCodex.Api.Tests/SmokeTests.cs`
- `MIGRATION_PROGRESS.tmp.md`

Changes:

- Added `ProducesResponseType` metadata for:
  - `GET /admin/api/web-search` -> `WebSearchConfigResponse` 200.
  - `POST /admin/api/web-search` -> `WebSearchConfigResponse` 200 and `AdminErrorResponse` 400.
  - `POST /admin/api/web-search/test-key` -> `WebSearchTestKeyResponsePayload` 200 and `AdminErrorResponse` 400.
- Extended `SmokeTests.SwaggerDocumentIsAvailableInDevelopment` with schema `$ref` assertions for all of the above.
- Did not change controller bodies, web-search logic, status codes, JSON fields, or routes.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminWebSearch|FullyQualifiedName~WebSearchTestKeyRequest|FullyQualifiedName~AdminDataControllerTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n "[ \t]+$" src/OpenCodex.Api/Controllers/AdminWebSearchController.cs tests/OpenCodex.Api.Tests/SmokeTests.cs
git diff --check -- opencodex_proxy/src/OpenCodex.Api/Controllers/AdminWebSearchController.cs opencodex_proxy/tests/OpenCodex.Api.Tests/SmokeTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Swagger smoke tests passed: 7 passed, 0 failed, 0 skipped.
- Focused AdminWebSearch/WebSearchTestKeyRequest/AdminData tests passed: 38 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 359 passed, 0 failed, 0 skipped.
- Trailing whitespace scan found no matches.
- `git diff --check` passed for touched files.

Remaining risks:

- OpenAPI still lacks example payloads for web-search endpoints.
- Channel diagnostics endpoints still need explicit response schema metadata for canonical and compatibility routes.
- `/v1/*` proxy endpoints remain intentionally broad/unwrapped for compatibility and should be documented separately.

Suggested next unit:

- Add baseline response schema metadata to `AdminChannelDiagnosticsController` for:
  - `/admin/api/channels/discover-models`
  - `/admin/api/discover-models`
  - `/admin/api/channels/test`
  - `/admin/api/test-channel`
- After baseline schemas are in place, add examples/error-code documentation in a separate unit.

## User-Directed Next Task Memory

Status: queued by user.

User instruction:

- Pay attention to the code class-library structure and architecture layering documents.
- Make this the next task.
- Record this instruction in the temporary migration memory.

Next task priority:

- Before continuing ordinary module migration or Swagger/API metadata cleanup, review the class-library and architecture layering guidance first.
- Use these documents as the decision baseline:
  - `CLASS_LIBRARY_GUIDE.md`
  - `PROJECT_ARCHITECTURE.md`
  - `NEW_PROJECT_CHECKLIST.md`
  - `API_CODE_STYLE.md`
  - `API_RESULT_GUIDE.md`
  - `REPOSITORY_GUIDE.md`
  - `AUTOFAC_GUIDE.md`
- Inspect the current .NET code layout under `src/OpenCodex.Api` and compare it with the documented intended boundaries.
- Decide the smallest useful next implementation unit from that audit, rather than continuing mechanically with previously queued endpoint/documentation work.

Important constraints for the next task:

- Do not blindly migrate more Python behavior before checking whether the current .NET class-library/folder layering is still appropriate.
- Preserve existing compatibility boundaries:
  - `/v1/responses`
  - `/v1/chat/completions`
  - `/v1/messages`
  - `/admin/api/...` compatibility routes
- Keep admin success payloads unwrapped unless a later explicit design decision changes that.
- Keep `ProxyException` error JSON compatible.
- If the audit implies changes across more than three files, split the work into smaller units and ask for approval before editing.

Suggested first step when resuming:

- Read the architecture/class-library documents listed above.
- Review existing architecture tests in `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`.
- Run targeted `rg` scans for remaining cross-layer dependencies.
- Propose a small, bounded architecture/layering cleanup unit for user approval before modifying code.

## Completed Architecture Layering Unit

### Runtime Settings Contracts Moved To Abstractions

Status: completed.

Documents reviewed before implementation:

- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`
- `NEW_PROJECT_CHECKLIST.md`
- Existing architecture guard tests in `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`

Goal:

- Continue class-library/layering normalization before ordinary feature migration.
- Prevent Service and Persistence layers from depending on the concrete `Configuration` layer.
- Keep configuration reading and host-environment logic in `Configuration` implementations.
- Move stable service-facing configuration contracts into `Abstractions`.

Implemented files:

- `src/OpenCodex.Api/Abstractions/IAdminStaticDirectoryProvider.cs`
- `src/OpenCodex.Api/Abstractions/IOpenCodexRuntimeSettingsProvider.cs`
- `src/OpenCodex.Api/Abstractions/OpenCodexRuntimeSettings.cs`
- `src/OpenCodex.Api/Domain/PricingDefaults.cs`
- `src/OpenCodex.Api/Configuration/AdminStaticDirectoryProvider.cs`
- `src/OpenCodex.Api/Configuration/OpenCodexRuntimeSettingsProvider.cs`
- `src/OpenCodex.Api/Configuration/OpenCodexSettings.cs`
- Service, Persistence, and affected test files that consumed the moved contracts
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`

Changes:

- Moved service-facing runtime settings/static-directory contracts from `Configuration` to `Abstractions`.
- Left concrete configuration providers in `Configuration`.
- Updated Service and Persistence code to depend on `OpenCodex.Api.Abstractions`.
- Moved the USD/CNY pricing default from `OpenCodexSettings` to `Domain/PricingDefaults`.
- Added architecture guard tests:
  - Services and Persistence must not reference `OpenCodex.Api.Configuration`.
  - Configuration must not own the moved service-facing contract files.
  - Core `Config`, `Protocols`, and `Routing` folders must not reference outer layers or ASP.NET/Microsoft.Extensions.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~OpenCodexSettingsTests|FullyQualifiedName~AdminStaticDirectoryProviderTests|FullyQualifiedName~AdminAuthServiceTests|FullyQualifiedName~AdminConfigServiceTests|FullyQualifiedName~AdminUserServiceTests|FullyQualifiedName~AdminUiServiceTests|FullyQualifiedName~AdminChannelDiagnosticsServiceTests|FullyQualifiedName~ProxyRequestServiceTests|FullyQualifiedName~ProxyLogRepositoryTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api opencodex_proxy/tests/OpenCodex.Api.Tests
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused architecture tests passed: 23 passed, 0 failed, 0 skipped.
- Focused configuration/service/persistence tests passed: 59 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 362 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched .NET source/test paths.

Remaining risks:

- The repository currently still uses one `.csproj` with folders/namespaces as layer boundaries; architecture tests protect the boundary but do not enforce project-reference boundaries.
- Persistence still uses several dedicated repositories over static `OpenCodexDatabase` methods; it has not yet been reshaped into the user's preferred generic repository pattern.
- Configuration and persistence responsibilities around database path lookup may need a broader repository/unit-of-work design pass.

## User-Directed Next Task Memory

Status: queued by user.

User instruction:

- Repository should be generic.
- The entity should specify the repository.
- Use `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Core` as the reference implementation.
- The class-library layering should also follow that reference.

Next task priority:

- Before changing repositories, inspect the referenced `Ylg.Core` class-library layout and repository pattern.
- Compare it with OpenCodex's current `Domain`, `Persistence`, `Services`, `Abstractions`, and `Configuration` layout.
- Propose the smallest useful repository/layering migration unit, because a full generic repository reshaping will likely touch many files and should be staged.
- Preserve current API behavior, route compatibility, and existing test coverage while changing persistence internals.

## Completed Repository Layering Unit

### Generic Repository Skeleton And User Entity Session Read

Status: completed.

Reference reviewed:

- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Core/Ylg.Core.csproj`
- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Core.Base/Data/IRepository.cs`
- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Data/EfRepository.cs`
- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Core.Domain/Domain/BaseEntity.cs`
- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Web.Framework/Infrastructure/DependencyRegistrar.cs`
- `REPOSITORY_GUIDE.md`
- `CLASS_LIBRARY_GUIDE.md`
- `PROJECT_ARCHITECTURE.md`

Reference conclusions:

- `Ylg.Core` services depend on `IRepository<TEntity>` where the entity type chooses the repository.
- `Ylg.Data` registers an open generic repository implementation:
  - `RegisterGeneric(typeof(EfRepository<>)).As(typeof(IRepository<>))`
- Domain entities inherit from `BaseEntity<TKey>`.
- Dedicated service logic is still service-owned; the generic repository provides common entity access.
- The OpenCodex codebase cannot directly use existing `*Record` types as persistence entities because many records are read models or service/API result shapes rather than full table rows.

Goal:

- Start moving OpenCodex toward the user-requested generic repository pattern.
- Avoid a large, risky rewrite of all SQLite persistence code.
- Introduce the generic repository seam with one low-risk entity read path first.
- Preserve admin session behavior and API compatibility.

Implemented files:

- `src/OpenCodex.Api/Domain/BaseEntity.cs`
- `src/OpenCodex.Api/Domain/UserEntity.cs`
- `src/OpenCodex.Api/Persistence/IRepository.cs`
- `src/OpenCodex.Api/Persistence/SqliteRepository.cs`
- `src/OpenCodex.Api/Persistence/SqliteEntityMap.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `src/OpenCodex.Api/Services/Admin/AdminSessionService.cs`
- `src/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminSessionServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs`
- `tests/OpenCodex.Api.Tests/Architecture/ArchitectureDependencyTests.cs`
- Removed `src/OpenCodex.Api/Persistence/IAdminSessionRepository.cs`
- Removed `src/OpenCodex.Api/Persistence/AdminSessionRepository.cs`

Changes:

- Added `BaseEntity` / `BaseEntity<TKey>` in the Domain layer.
- Added `UserEntity` as a real persistence entity for the `users` table, including `PasswordHash`.
- Added `IRepository<TEntity>` as the generic repository contract.
- Added `SqliteRepository<TEntity>` as the open generic SQLite-backed implementation.
- Added `SqliteEntityMap<TEntity>` with the first entity map for `UserEntity`.
- Registered open generic repository DI:
  - `IRepository<>` -> `SqliteRepository<>`
- Changed `AdminSessionService` to depend on `IRepository<UserEntity>` instead of `IAdminSessionRepository`.
- Removed the session-specific repository and interface.
- Added architecture guard that prevents the removed session-specific repository from returning.
- Added persistence tests proving `SqliteRepository<UserEntity>` loads users from the real SQLite database.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~ArchitectureDependencyTests|FullyQualifiedName~AdminSessionServiceTests|FullyQualifiedName~SqliteRepositoryTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api opencodex_proxy/tests/OpenCodex.Api.Tests
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused architecture/session/generic repository tests passed: 32 passed, 0 failed, 0 skipped.
- Focused AdminData/Smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 365 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched .NET source/test paths.

Remaining risks:

- Only `UserEntity` and the admin session read path use the new generic repository so far.
- The generic SQLite repository currently supports only mapped entities; unsupported entity types throw `NotSupportedException`.
- Existing dedicated repositories still wrap `OpenCodexDatabase` static methods.
- `UserRecord` remains a service-facing read model, while `UserEntity` is the persistence entity. This split is intentional and should continue for other tables.

Suggested next unit:

- Continue repository migration with users:
  - inject `IRepository<UserEntity>` into `AdminUserRepository` for `GetUser` / `ListUsers` read paths, or
  - add explicit query methods to the generic repository if a table needs list/order support.
- Do not migrate password creation/reset/delete in the same unit unless a transaction/write strategy is first defined.
- After user reads are stable, introduce entity maps for:
  - `AccessApiKeyEntity`
  - `ChannelEntity`
  - `TavilyKeyEntity`
- Longer-term direction:
  - keep persistence entities in Domain or a dedicated persistence entity namespace according to the final class-library split;
  - keep API/service read models separate from full persistence entities;
  - gradually reduce empty or thin dedicated repositories, but keep specialized repositories where queries are genuinely business-specific or transactional.

## Completed Repository Layering Unit

### AdminUser Read Paths Use Generic User Repository

Status: completed.

Reference baseline:

- User request: repository should be generic and selected by entity.
- `Ylg.Core` / `Ylg.Data` reference pattern:
  - service/data code uses `IRepository<TEntity>`;
  - DI registers the open generic implementation once;
  - entity type is the repository selector.
- `REPOSITORY_GUIDE.md` warns against overusing dedicated repositories for simple entity access.

Goal:

- Continue the generic repository migration without changing user write/transaction behavior yet.
- Move `AdminUserRepository.GetUser` and `AdminUserRepository.ListUsers` onto `IRepository<UserEntity>`.
- Preserve current list ordering and response model shape.

Implemented files:

- `src/OpenCodex.Api/Persistence/IRepository.cs`
- `src/OpenCodex.Api/Persistence/SqliteRepository.cs`
- `src/OpenCodex.Api/Persistence/SqliteEntityMap.cs`
- `src/OpenCodex.Api/Persistence/AdminUserRepository.cs`
- `tests/OpenCodex.Api.Tests/Services/Admin/AdminSessionServiceTests.cs`
- `tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs`
- `tests/OpenCodex.Api.Tests/Persistence/AdminUserRepositoryTests.cs`

Changes:

- Extended `IRepository<TEntity>` with `ListAll()`.
- Implemented `SqliteRepository<TEntity>.ListAll()`.
- Added `UserEntity` list mapping with the same ordering as the previous SQL:
  - `role ASC, username ASC`
- Updated `AdminUserRepository` to inject `IRepository<UserEntity>`.
- Changed `AdminUserRepository.ListUsers()` to map `UserEntity` to `UserRecord`.
- Changed `AdminUserRepository.GetUser()` to use `IRepository<UserEntity>.GetById()`.
- Left user writes/authentication on the existing `OpenCodexDatabase` paths:
  - `EnsureSuperadmin`
  - `AuthenticateUser`
  - `CreateUser`
  - `SetUserEnabled`
  - `ResetUserPassword`
  - `DeleteUser`
- Updated fake generic repository tests for the new `ListAll()` contract.
- Added `AdminUserRepositoryTests` for real SQLite-backed user read paths.

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminUserRepositoryTests|FullyQualifiedName~SqliteRepositoryTests|FullyQualifiedName~AdminSessionServiceTests|FullyQualifiedName~AdminUserServiceTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~SmokeTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
git diff --check -- opencodex_proxy/src/OpenCodex.Api opencodex_proxy/tests/OpenCodex.Api.Tests
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused repository/service/architecture tests passed: 42 passed, 0 failed, 0 skipped.
- Focused AdminData/Smoke tests passed: 32 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 368 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched .NET source/test paths.

Remaining risks:

- `ListAll()` is intentionally small and entity-mapped; it is not a general `IQueryable` exposure.
- `AdminUserRepository` still owns user write/authentication behavior through `OpenCodexDatabase`.
- Generic repository write methods and transaction/unit-of-work policy are not defined yet.
- Unsupported generic repository entity types still throw `NotSupportedException`.

Suggested next unit:

- Define the write-side strategy before moving user mutations:
  - either add explicit generic repository write methods and a unit-of-work boundary, or
  - keep writes in specialized repositories when they include password hashing, protected superadmin checks, or cascading deletes.
- A safe next migration unit is `AccessApiKeyEntity` read paths because API key listing/getting is table-shaped but still has plaintext/hash masking concerns.
- Another safe next unit is `ChannelEntity` read paths for routing/admin config, but channel replacement should remain specialized until batch replace transaction behavior is designed.

## Next Architecture Constraint

Status: pending.

User direction:

- Repository should be generic.
- Repository selection should be driven by the entity type.
- Use `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Core` as the reference for class-library layering.
- Continue checking the surrounding class-library architecture documents before the next migration unit.

Reference observations:

- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Core.Base/Data/IRepository.cs` defines a generic `IRepository<TEntity>` constrained to domain entities.
- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Data/EfRepository.cs` implements the generic repository.
- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Web.Framework/Infrastructure/DependencyRegistrar.cs` registers the open generic implementation:
  - `RegisterGeneric(typeof(EfRepository<>)).As(typeof(IRepository<>))`
- `/Users/w/shL/work/Ylg/Ylg.WebPortal/Libraries/Ylg.Core.Domain/Domain/BaseEntity.cs` is the domain entity base model.

Next-task rule:

- Do not add new one-off repositories for simple entity reads.
- Prefer `IRepository<TEntity>` plus a real persistence entity and explicit entity mapping.
- Keep specialized repositories/query services only for behavior that is not simple entity access, such as scoped queries, authentication side effects, batch replacement, cascading deletes, password hashing, key masking, or transaction boundaries.
- When moving more persistence code, first decide whether the operation is:
  - generic entity CRUD/read access,
  - a specialized query,
  - or a domain/service transaction.
- Class-library splitting should continue toward the reference direction:
  - domain entities and constants in a domain layer,
  - stable contracts in abstractions/base layer,
  - repository implementation and SQLite mapping in data/persistence layer,
  - HTTP controllers and Swagger/API concerns in API layer.

## Completed Repository Layering Unit

### Access API Key Entity Read Mapping

Status: completed.

Context:

- User direction: repository should be generic and selected by entity.
- User added a long-term C# rule:
  - default to `class`;
  - do not use `record` unless value semantics, immutable snapshots, `with`, or pattern matching benefits are specifically needed.
- This rule was added to `/Users/w/shL/work/shl/OpenCodex/AGENTS.md`.

Goal:

- Continue the generic repository migration with API key persistence reads.
- Preserve existing admin API key behavior and Python-compatible response shape.
- Avoid widening `IRepository<TEntity>` into a generic filtering/query builder.

Implemented files:

- `/Users/w/shL/work/shl/OpenCodex/AGENTS.md`
- `src/OpenCodex.Api/Domain/BaseEntity.cs`
- `src/OpenCodex.Api/Domain/UserEntity.cs`
- `src/OpenCodex.Api/Domain/AccessApiKeyEntity.cs`
- `src/OpenCodex.Api/Persistence/SqliteEntityMap.cs`
- `src/OpenCodex.Api/Persistence/AdminApiKeyRepository.cs`
- `tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs`
- `tests/OpenCodex.Api.Tests/Persistence/AdminApiKeyRepositoryTests.cs`

Changes:

- Added `AccessApiKeyEntity` as a real persistence entity for `access_api_keys`.
- Added SQLite generic repository mapping for:
  - `IRepository<AccessApiKeyEntity>.GetById(...)`
  - `IRepository<AccessApiKeyEntity>.ListAll()`
- Preserved full admin list ordering:
  - `owner_username ASC, id DESC`
- Updated `AdminApiKeyRepository.ListAccessApiKeys(null/blank)` to use `IRepository<AccessApiKeyEntity>`.
- Kept owner-scoped API key listing on the existing specialized query path because owner filtering is an authorization/business scope concern.
- Kept create, enable/disable, delete, and proxy authentication on existing specialized paths because they include validation, side effects, or transaction behavior.
- Converted the generic entity base and persistence entities from `record` to `class`:
  - `BaseEntity`
  - `BaseEntity<TKey>`
  - `UserEntity`
  - `AccessApiKeyEntity`
- Did not rewrite existing DTO/read-model records in this unit; that would be a broad style migration and not necessary for the API key repository step.

Tests added/updated:

- `SqliteRepositoryTests.GetByIdLoadsAccessApiKeyEntity`
- `SqliteRepositoryTests.ListAllLoadsAccessApiKeysInAdminListOrder`
- `AdminApiKeyRepositoryTests.ListAccessApiKeysWithoutOwnerUsesGenericRepository`
- `AdminApiKeyRepositoryTests.ListAccessApiKeysWithOwnerPreservesOwnerScopedQuery`

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --filter "FullyQualifiedName~AdminApiKeyRepositoryTests|FullyQualifiedName~SqliteRepositoryTests|FullyQualifiedName~AdminApiKeyServiceTests|FullyQualifiedName~AdminDataControllerTests|FullyQualifiedName~ArchitectureDependencyTests" --logger "console;verbosity=minimal"
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
rg -n '[ \t]+$' AGENTS.md opencodex_proxy/src/OpenCodex.Api/Domain/BaseEntity.cs opencodex_proxy/src/OpenCodex.Api/Domain/UserEntity.cs opencodex_proxy/src/OpenCodex.Api/Domain/AccessApiKeyEntity.cs opencodex_proxy/src/OpenCodex.Api/Persistence/AdminApiKeyRepository.cs opencodex_proxy/src/OpenCodex.Api/Persistence/SqliteEntityMap.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/AdminApiKeyRepositoryTests.cs
git diff --check -- AGENTS.md opencodex_proxy/src/OpenCodex.Api/Domain/BaseEntity.cs opencodex_proxy/src/OpenCodex.Api/Domain/UserEntity.cs opencodex_proxy/src/OpenCodex.Api/Domain/AccessApiKeyEntity.cs opencodex_proxy/src/OpenCodex.Api/Persistence/AdminApiKeyRepository.cs opencodex_proxy/src/OpenCodex.Api/Persistence/SqliteEntityMap.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/AdminApiKeyRepositoryTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Focused API key/repository/admin/architecture tests passed: 63 passed, 0 failed, 0 skipped.
- Full .NET test suite passed: 372 passed, 0 failed, 0 skipped.
- Trailing whitespace scan passed for touched files.
- `git diff --check` passed for touched files.

Remaining risks:

- `IRepository<TEntity>` still only supports mapped entity types; unsupported entities throw `NotSupportedException`.
- API key owner-scoped reads still use the existing specialized SQL path by design.
- API key create/update/delete/authentication still use static `OpenCodexDatabase` methods.
- Existing DTO/read-model records remain; only generic repository entities and base entity were aligned with the new class-first rule.

Suggested next unit:

- Decide query boundary for owner-scoped lists before moving more scoped reads:
  - keep scoped queries in specialized repositories/query services, or
  - introduce explicit query objects without leaking database column names into `IRepository<TEntity>`.
- A safe next migration target is `ChannelEntity` read mapping, with batch replace and owner-scoped config behavior kept specialized until transaction boundaries are designed.

## Completed Repository Layering Unit

### Channel Entity Read Mapping For Global Config Reads

Status: completed.

Context:

- Explorer audit and local code review agreed on the same boundary:
  - `ChannelEntity` can move to generic repository for simple global reads;
  - owner-scoped reads and batch replace must stay specialized;
  - no extra query/filter API should be added to `IRepository<TEntity>`.
- The repository and class-library rules remain aligned with `Ylg.Core`:
  - entity type chooses the repository;
  - Domain owns entities;
  - Persistence owns SQLite mapping;
  - Service owns scope/transaction decisions.

Goal:

- Move the generic repository one step further by adding a real channel persistence entity.
- Keep `AdminConfigRepository.ReadChannels(null/blank)` on the generic repository path.
- Preserve owner-scoped config reads and `ReplaceChannels(...)` behavior on the existing specialized path.

Implemented files:

- `src/OpenCodex.Api/Domain/ChannelEntity.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs`
- `src/OpenCodex.Api/Persistence/SqliteEntityMap.cs`
- `src/OpenCodex.Api/Persistence/AdminConfigRepository.cs`
- `tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs`
- `tests/OpenCodex.Api.Tests/Persistence/AdminConfigRepositoryTests.cs`

Changes:

- Added `ChannelEntity` as a real persistence entity with a composite identity `(OwnerUsername, Id)`.
- Added repository-read helpers for channel JSON fields in `OpenCodexDatabase`.
- Added SQLite generic repository mapping for:
  - `IRepository<ChannelEntity>.GetById(...)`
  - `IRepository<ChannelEntity>.ListAll()`
- Preserved admin config global list ordering:
  - `owner_username ASC, position ASC, id ASC`
- Updated `AdminConfigRepository.ReadChannels(null/blank)` to use `IRepository<ChannelEntity>`.
- Kept owner-scoped channel reads on the existing specialized SQL path.
- Kept `ReplaceChannels(...)` unchanged because it is a delete-plus-insert transaction with owner scoping and created-at preservation.

Tests added/updated:

- `SqliteRepositoryTests.GetByIdLoadsChannelEntity`
- `SqliteRepositoryTests.ListAllLoadsChannelsInAdminConfigOrder`
- `AdminConfigRepositoryTests.ReadChannelsWithoutOwnerUsesGenericRepository`
- `AdminConfigRepositoryTests.ReadChannelsWithOwnerPreservesOwnerScopedQuery`

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
git diff --check -- AGENTS.md opencodex_proxy/src/OpenCodex.Api/Domain/BaseEntity.cs opencodex_proxy/src/OpenCodex.Api/Domain/UserEntity.cs opencodex_proxy/src/OpenCodex.Api/Domain/AccessApiKeyEntity.cs opencodex_proxy/src/OpenCodex.Api/Domain/ChannelEntity.cs opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.cs opencodex_proxy/src/OpenCodex.Api/Persistence/SqliteEntityMap.cs opencodex_proxy/src/OpenCodex.Api/Persistence/AdminConfigRepository.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/AdminApiKeyRepositoryTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/AdminConfigRepositoryTests.cs opencodex_proxy/MIGRATION_PROGRESS.tmp.md
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 376 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.

Remaining risks:

- `ChannelEntity` is only used for simple global reads; owner-scoped reads still use specialized SQL.
- `ReplaceChannels(...)` remains transaction-heavy and should stay out of generic CRUD until a unit-of-work boundary is explicitly designed.
- `ChannelRecord` still exists as the API/read model; this unit only added the persistence entity needed for generic repository use.

Suggested next unit:

- Choose the next simple persistence entity read path only after deciding the query boundary for owner-scoped reads.
- A sensible follow-up is either `WebSearchConfig` read mapping or another purely table-shaped entity read, but only if it does not require a new query abstraction.

## Completed Repository Layering Unit

### Web Search Settings And Tavily Key Read Mapping

Status: completed.

Context:

- User direction remains the same:
  - repository should be generic;
  - entity type should choose the repository;
  - do not add new one-off repositories for simple entity reads;
  - avoid `record` unless value semantics are specifically needed.
- Explorer audit and local review agreed that Web Search read paths are suitable for the next generic repository step because they are table-shaped reads, while reserve/update/write paths remain transaction-heavy and specialized.

Goal:

- Move Web Search read paths onto generic repository entities.
- Keep `ReplaceWebSearchConfig(...)` and `ReserveTavilyKey(...)` on the existing specialized persistence paths.
- Preserve current default values and response shape.

Implemented files:

- `src/OpenCodex.Api/Domain/WebSearchSettingsEntity.cs`
- `src/OpenCodex.Api/Domain/TavilyKeyEntity.cs`
- `src/OpenCodex.Api/Persistence/OpenCodexDatabase.WebSearch.cs`
- `src/OpenCodex.Api/Persistence/SqliteEntityMap.cs`
- `src/OpenCodex.Api/Persistence/AdminWebSearchRepository.cs`
- `src/OpenCodex.Api/Persistence/ProxyWebSearchRepository.cs`
- `tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs`
- `tests/OpenCodex.Api.Tests/Persistence/AdminWebSearchRepositoryTests.cs`
- `tests/OpenCodex.Api.Tests/Persistence/ProxyWebSearchRepositoryTests.cs`

Changes:

- Added `WebSearchSettingsEntity` and `TavilyKeyEntity` as real persistence entities.
- Added SQLite generic repository mapping for:
  - `IRepository<WebSearchSettingsEntity>.GetById(...)`
  - `IRepository<WebSearchSettingsEntity>.ListAll()`
  - `IRepository<TavilyKeyEntity>.GetById(...)`
  - `IRepository<TavilyKeyEntity>.ListAll()`
- Updated `AdminWebSearchRepository.ReadWebSearchConfig()` to read `web_search_settings` and `tavily_keys` through generic repositories.
- Updated `ProxyWebSearchRepository.ReadWebSearchConfig()` to read through generic repositories as well.
- Kept `ReplaceWebSearchConfig(...)` and `ReserveTavilyKey(...)` on the existing `OpenCodexDatabase` methods because they are transaction-heavy, state-changing operations.
- Preserved default Web Search provider list and default usage-limit behavior.

Tests added/updated:

- `SqliteRepositoryTests.GetByIdLoadsWebSearchSettingsEntity`
- `SqliteRepositoryTests.ListAllLoadsTavilyKeysInAdminOrder`
- `AdminWebSearchRepositoryTests.ReadWebSearchConfigUsesGenericRepositoryForSettingsAndKeys`
- `AdminWebSearchRepositoryTests.ReadWebSearchConfigDefaultsWhenSettingsRowIsMissing`
- `ProxyWebSearchRepositoryTests.ReadWebSearchConfigUsesGenericRepositoryForSettingsAndKeys`

Verification commands:

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build OpenCodex.sln --no-restore -warnaserror
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test OpenCodex.sln --no-build --logger "console;verbosity=minimal"
git diff --check -- AGENTS.md opencodex_proxy/MIGRATION_PROGRESS.tmp.md opencodex_proxy/src/OpenCodex.Api/Domain/WebSearchSettingsEntity.cs opencodex_proxy/src/OpenCodex.Api/Domain/TavilyKeyEntity.cs opencodex_proxy/src/OpenCodex.Api/Persistence/OpenCodexDatabase.WebSearch.cs opencodex_proxy/src/OpenCodex.Api/Persistence/SqliteEntityMap.cs opencodex_proxy/src/OpenCodex.Api/Persistence/AdminWebSearchRepository.cs opencodex_proxy/src/OpenCodex.Api/Persistence/ProxyWebSearchRepository.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/SqliteRepositoryTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/AdminWebSearchRepositoryTests.cs opencodex_proxy/tests/OpenCodex.Api.Tests/Persistence/ProxyWebSearchRepositoryTests.cs
```

Result:

- Build with warnings as errors passed: 0 warnings, 0 errors.
- Full .NET test suite passed: 376 passed, 0 failed, 0 skipped.
- `git diff --check` passed for touched files.

Remaining risks:

- `ReserveTavilyKey(...)` and `ReplaceWebSearchConfig(...)` remain specialized by design.
- `WebSearchConfigRecord` still acts as the read model; the new entities only cover persistence reads.
- Provider defaults are still sourced from `OpenCodexDatabase` constants, not from a table row.

Suggested next unit:

- Continue with another purely table-shaped read path only after checking whether it needs a new query abstraction.
- If the next candidate is write-heavy or scope-heavy, keep it specialized rather than forcing it into generic CRUD.
