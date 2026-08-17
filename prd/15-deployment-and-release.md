# 15. 部署与发布

> 需求前缀：`REQ-REL`  
> 代码基线：`main@3827590`  
> 覆盖：本地开发、Docker SQLite、Docker PostgreSQL + Redis、Tauri 桌面构建、CI 发布、升级和回滚

## 1. 发布目标

发布流程必须保证：

- 构建产物来自可追溯提交和锁定依赖；
- 管理台、后端、数据库迁移和桌面 sidecar 版本一致；
- 发布前经过规定测试门禁；
- 部署不会无备份地破坏现有数据；
- 健康检查和业务冒烟通过后才宣布成功；
- 失败时有可执行回滚方案；
- 桌面安装包具备明确的平台、签名和升级策略；
- 服务端镜像、Compose 和运行配置可复现。

## 2. 版本和产物

### 2.1 版本来源

当前版本信息存在于：

- `src-tauri/tauri.conf.json`；
- `src-tauri/Cargo.toml`；
- Git tag `v*`；
- Docker 镜像标签；
- GitHub Release 名称。

发布 CI 在 tag 构建时仅将 `src-tauri/tauri.conf.json` 的版本同步为 tag 去掉 `v` 后的值；`src-tauri/Cargo.toml` 的 package 版本仍可能保持旧值，服务端也没有统一的公开版本来源。工作流会接受任意 `v*` tag，当前未校验其是否为合法 SemVer；仓库还缺少 `src-tauri/Cargo.lock`，CI 使用浮动 `stable` Rust 工具链，桌面二进制无法仅凭 tag 稳定复现。

`REQ-REL-001`（MUST）：一次正式发布必须有唯一语义版本，并同步到桌面配置、后端版本接口、镜像标签、安装包和发布说明。

### 2.2 产物清单

| 产物 | 当前生成方式 | 目标用途 |
|---|---|---|
| Docker 镜像 | 本地发布脚本调用 Docker buildx 构建并直接推送，当前仅 `linux/amd64` | 服务端运行 |
| 管理台静态资源 | Vite build，复制到 `wwwroot/admin` | 浏览器管理 |
| .NET API | `dotnet publish` | Docker 或 sidecar |
| macOS DMG | GitHub Actions / Tauri | macOS arm64 |
| Windows NSIS | GitHub Actions / Tauri | Windows x64 |
| Linux DEB | GitHub Actions / Tauri | x86_64 Linux/Deepin |
| GitHub Release | tag 触发，草稿 Release | 桌面分发 |

准备脚本还声明部分其他目标，但未进入当前 CI 发布矩阵，不能宣称正式支持。当前桌面发布 workflow 不构建或发布服务端 Docker 镜像，服务端镜像没有由 CI 产生的统一产物、哈希、来源证明和发布记录。

## 3. 本地开发

### 3.1 后端

前置：

- .NET SDK 10.0.300；
- 可写的 `logs/`；
- 有效数据库配置；
- 本地 HTTPS 证书（按开发方式）；
- 可选 Redis。

启动后应验证：

- `/health` 可响应；
- `/setup/status` 可响应；
- 管理台静态资源或 Vite 代理可加载；
- 数据库迁移成功；
- 默认模型目录和价格播种成功。

### 3.2 管理台

- 使用 `npm --prefix frontend ci` 安装锁定依赖；
- 使用 `npm --prefix frontend run dev` 开发；
- `/admin/` 为基础路径；
- 开发入口与后端直接入口不共享 Cookie；
- 浏览器跨入口切换导致登录丢失应在开发文档中说明。

### 3.3 桌面开发

- 根目录和前端依赖均需安装；
- `desktop:prepare` 构建前端、发布 self-contained sidecar 并复制资源；
- `tauri dev` 启动桌面壳；
- Rust toolchain 和平台构建依赖必须满足；
- sidecar、资源目录和桌面设置路径必须可写。

## 4. Docker 构建

### 4.1 当前多阶段结构

1. Node 22 Bookworm 构建管理台；
2. .NET 10 Alpine SDK 恢复并发布 `linux-musl-x64`；
3. 删除 PDB、XML 和 Development 文件；
4. ASP.NET 10 Alpine 作为最终镜像；
5. 安装时区和 ICU；
6. 复制 API、管理台和 `.env.example`；
7. 以 `dotnet OpenCodex.Api.dll` 启动。

### 4.2 构建要求

`REQ-REL-002`（MUST）：Docker 构建必须从干净工作区使用锁文件恢复依赖，并记录 Git commit、构建时间、版本和基础镜像摘要。

`REQ-REL-003`（SHOULD）：生产镜像应使用非 root 用户、只读根文件系统兼容路径、最小 Linux capabilities 和显式资源限制。

当前差距：

- 基础镜像标签可漂移；
- 最终镜像未设置非 root 用户；
- 未生成 SBOM；
- 未执行镜像漏洞扫描；
- Docker Node 22 与 CI Node 24 不一致；
- 只正式构建 `linux/amd64`；
- 服务端镜像构建和推送依赖运行 `update_remote_image.sh` 的本地工作站，缺少独立 CI 发布链。

## 5. Docker SQLite 部署

### 5.1 适用范围

- 单实例；
- 本机或低并发服务；
- 数据文件与应用在同一持久卷；
- 不需要跨实例共享亲和、容量和熔断。

### 5.2 必需持久内容

- SQLite 数据库，包括用户、渠道、配置、请求日志元数据及内容寻址日志正文；
- Data Protection Key；
- 桌面/运行设置（如使用）；
- OCR 缓存（如保留）。

容器 stdout/stderr 当前由 Docker `json-file` 驱动记录并按 Compose 中的 `max-size`/`max-file` 轮转，不属于 SQLite 业务数据。当前代码和 Compose 已没有需要单独挂载的 `OPENCODEX_LOG_PATH` 文件日志配置，不能继续按旧文档把它当作必需持久卷。

### 5.3 部署流程

1. 校验 `.env`；
2. 创建持久卷目录；
3. 备份现有数据库和密钥目录；
4. 拉取固定镜像版本或 digest；
5. 在隔离环境执行迁移兼容检查；
6. 启动容器；
7. 等待 readiness；
8. 执行 `/setup/status`、登录和代理冒烟；
9. 确认卷、日志轮转和备份任务；
10. 记录发布结果和回滚点。

## 6. PostgreSQL + Redis 部署

### 6.1 适用范围

- 生产服务端；
- 更大数据量；
- 多实例或需要共享缓存/运行时协调；
- 需要数据库备份、监控和独立运维。

### 6.2 当前 Compose 组件

- PostgreSQL 17 Alpine；
- Redis 7 Alpine；
- OpenCodex 镜像；
- 持久化 `postgres-data`、`redis-data`、`logs`；
- 容器健康检查；
- `ocxp-network` 网络；
- JSON 日志轮转，默认约 `50m × 5`。

### 6.3 生产要求

- PostgreSQL 凭据必须来自安全 Secret，不得使用 `admin/123456`；
- Redis 必须启用认证或位于严格受控网络；
- 数据库与 Redis 不应直接映射到公网；
- 数据库连接必须支持 TLS 或受控内网；
- 所有 OpenCodex 实例共享同一数据库和 Redis Prefix；
- 自动迁移执行者必须唯一或具备数据库级互斥；
- 部署前执行备份与恢复点验证；
- 多实例切流前校验配置、镜像和迁移版本一致。

`REQ-REL-004`（MUST）：多实例部署不得把流量同时分发到数据源、Redis Prefix、密钥或配置不一致的实例。

## 7. 远程更新流程

当前 `update_remote_image*.sh` 由本地工作站同时承担镜像构建者、镜像发布者和远程部署执行者，大致执行：

1. 构建并推送镜像；
2. 选择 SQLite 或 PostgreSQL Compose；
3. SCP Compose 到远端；
4. SSH 拉取镜像；
5. 删除旧容器；
6. `docker compose up -d --force-recreate`；
7. 打印日志轮转和容器状态。

当前风险：

- 第一次 SCP 前远端目录可能尚未创建；
- 没有部署前数据库备份；
- 没有等待业务 readiness；
- 没有代理 API 冒烟；
- 没有失败自动回滚；
- 使用可变镜像标签；
- 强制重建导致服务中断；
- 输出 `docker ps` 不足以证明发布成功。

`REQ-REL-005`（MUST）：远程发布脚本必须在切换流量前完成目录准备、配置校验、备份、镜像固定、迁移验证和健康冒烟；任何一步失败应停止并保留旧实例。

## 8. 流量切换

`switch_backend.sh` 可将 Nginx 上游切换到生产、开发或两者轮询。产品化规则：

- 切换前两个后端必须通过相同版本和业务冒烟；
- `both` 只允许在共享数据库、Redis 和关键配置时使用；
- 需要定义连接排空，尤其是长 SSE 请求；
- Nginx 配置测试通过后再 reload；
- 切换后校验 Host 路由、Cookie、代理接口和实时 SSE；
- 记录操作人、时间、目标和回滚命令。

`REQ-REL-006`（MUST）：SSE 长连接发布时必须有连接排空或可接受中断策略，不能假设普通 HTTP 切流对流式请求无影响。

## 9. 数据库迁移发布

### 9.1 当前行为

- 应用启动自动执行 EF 迁移；
- SQLite 和 PostgreSQL 各有独立迁移目录；
- 启动后播种模型目录和价格；
- 迁移失败通常阻止应用正常启动。

当前 `ContentAddressedLogs` 迁移存在已确认的破坏性行为：

- SQLite 与 PostgreSQL 的 `Up` 都先直接删除 `RequestLogDetails` 和 `RequestLogStreamLines`，随后创建内容寻址表，没有把旧请求/响应正文、Header、OCR、Web Search 或 SSE 行转换到新表；
- `Down` 会删除新的内容寻址表，并只重建空的旧表，同样不会恢复日志正文；
- 因此，无论升级还是通过 `Down` 回退，已有日志正文都可能永久丢失；保留的 `RequestLogs` 元数据不能替代正文恢复。

该迁移在有历史日志的环境中不得被视为可逆迁移，也不得在没有数据导出、备份恢复验证或明确数据丢弃审批的情况下随应用启动自动执行。

### 9.2 发布门禁

每个数据库变更必须：

1. 同时生成 SQLite 和 PostgreSQL 迁移；
2. 在生产规模副本上测试迁移耗时和锁；
3. 验证旧版本应用与迁移前后 Schema 的兼容窗口；
4. 提供备份、恢复和回滚策略；
5. 验证内容寻址日志等大表迁移的容量影响；
6. 对旧日志表执行非空数据升级测试，逐字段核对迁移前后正文、流式行和引用完整性；
7. 明确验证 `Down` 的数据影响，不能把“旧表被重新创建”误认为数据已回滚；
8. 不允许仅以单元测试代替真实数据库迁移测试。

## 10. 桌面发布

### 10.1 当前矩阵

| 平台 | Runner | Target | 安装包 | 当前签名状态 |
|---|---|---|---|---|
| macOS arm64 | macOS latest | `aarch64-apple-darwin` | DMG | ad-hoc `-`，无公证证据 |
| Windows x64 | Windows latest | `x86_64-pc-windows-msvc` | NSIS | 未见正式代码签名 |
| Linux x64 | Ubuntu 22.04 | `x86_64-unknown-linux-gnu` | DEB | 不适用/包签名未定义 |

### 10.2 发布要求

- tag 必须与版本一致；
- 前端、sidecar 和 Tauri 壳必须来自同一提交；
- 每个平台至少完成安装、首次启动、初始化、重启后端、托盘退出和卸载冒烟；
- macOS 应签名并 notarize；
- Windows 应使用可信代码签名证书；
- 安装包必须提供 SHA-256 校验；
- 发布说明包含数据库兼容性和已知限制；
- 自动更新是否启用为 `TBD`；
- 正式版应评估关闭 DevTools 并设置 CSP。

`REQ-REL-007`（MUST）：未经正式签名和平台安全验证的安装包必须标记为测试构建，不能作为正式生产发布。

## 11. CI/CD 门禁

当前唯一工作流只在手工触发或 `v*` tag 时运行，validate 仅执行后端测试。正式门禁应至少包括：

1. 后端 restore/build/test；
2. 前端 Node 单测；
3. 前端生产 build；
4. Rust `cargo check/test`；
5. SQLite/PostgreSQL 迁移测试；
6. Docker build 和启动冒烟；
7. 依赖审计、Secret 扫描、SAST；
8. 镜像扫描和 SBOM；
9. 桌面目标构建；
10. 发布产物校验和签名；
11. 普通 PR 和 push 触发质量验证；
12. tag 仅在主分支已通过门禁后创建；
13. tag SemVer 校验以及 Tauri/Cargo/后端版本一致性校验；
14. 服务端 Docker 镜像由 CI 构建、扫描、固定 digest 并发布，而不是依赖本地工作站直接推送。

## 12. 回滚

### 12.1 应用回滚

- 保留上一稳定镜像 digest；
- 保留上一 Compose 和配置快照；
- 新版本 readiness 或冒烟失败时恢复旧实例；
- 避免清理仍需回滚的旧镜像；
- SSE 连接按已定义策略排空或中断。

### 12.2 数据回滚

- 优先使用向前修复迁移；
- 破坏性迁移前必须备份；
- 必须验证从备份恢复，而不只是生成备份；
- 应用回滚前确认旧版本能读取新 Schema；
- 若不兼容，必须恢复数据库到迁移前恢复点。

### 12.3 桌面回滚

- 保留上一安装包；
- 数据库迁移需要向后兼容或提供用户数据备份；
- 卸载不得默认删除用户数据；
- 自动更新启用后必须支持失败恢复。

`REQ-REL-008`（MUST）：任何包含数据库 Schema 变化的发布都必须同时给出应用回滚兼容性结论和数据恢复步骤。

## 13. 发布验收清单

| 阶段 | 必须证据 |
|---|---|
| 构建 | 版本、commit、锁文件、构建日志、产物哈希 |
| 测试 | 测试矩阵结果和已知跳过项 |
| 安全 | Secret/SAST/依赖/镜像扫描结果 |
| 数据 | 迁移结果、备份位置、恢复抽检 |
| 部署 | readiness、数据库/Redis 状态、业务冒烟 |
| 代理 | models、三协议至少一个请求、流式请求 |
| 管理台 | 登录、渠道、Key、日志关键路径 |
| 桌面 | 安装、首次启动、托盘、重启、卸载 |
| 回滚 | 上一版本和回滚命令已验证 |
| 文档 | 版本说明、配置变化、迁移、风险同步 |

## 14. 需求列表

| 编号 | 级别 | 需求 |
|---|---|---|
| `REQ-REL-009` | MUST | 正式发布使用不可变版本或镜像 digest |
| `REQ-REL-010` | MUST | 部署成功必须由 readiness 和业务冒烟证明 |
| `REQ-REL-011` | MUST | 数据变更发布前有可恢复备份 |
| `REQ-REL-012` | MUST | SQLite/PostgreSQL 双迁移同步交付 |
| `REQ-REL-013` | MUST | PR 级验证覆盖后端、前端和迁移 |
| `REQ-REL-014` | SHOULD | 发布生成 SBOM 和签名校验 |
| `REQ-REL-015` | SHOULD | 服务端支持滚动或蓝绿发布 |
| `REQ-REL-016` | MUST | 桌面正式包有平台签名和安装冒烟 |
| `REQ-REL-017` | MUST | 发布说明列明所有配置变化和兼容性影响 |
| `REQ-REL-018` | MUST | 失败发布不会删除唯一可用的旧版本和数据恢复点 |
| `REQ-REL-019` | MUST | `ContentAddressedLogs` 升级必须迁移并校验旧日志正文，或在明确审批后执行可恢复的数据丢弃方案 |
| `REQ-REL-020` | MUST | 服务端 Docker 镜像由可审计 CI 产出并以不可变 digest 发布，禁止把本地工作站作为唯一发布链 |

## 15. 源码和配置追溯

| 区域 | 文件 |
|---|---|
| Docker 构建 | `Dockerfile` |
| SQLite Compose | `docker-compose-sqlite.yml` |
| PostgreSQL/Redis Compose | `docker-compose-pgsql.yml` |
| 部署说明 | `DEPLOYMENT.md` |
| 生产更新 | `update_remote_image.sh` |
| 开发更新 | `update_remote_image_dev.sh` |
| 流量切换 | `switch_backend.sh` |
| 桌面准备 | `scripts/prepare_tauri_sidecar.mjs` |
| Tauri 配置 | `src-tauri/tauri.conf.json`、`src-tauri/Cargo.toml` |
| CI | `.github/workflows/desktop-release.yml` |
