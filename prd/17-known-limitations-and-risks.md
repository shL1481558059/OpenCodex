# 17. 已知限制、风险与开放决策

> 需求前缀：`REQ-RSK`  
> 代码基线：`main@3827590`  
> 说明：本文件记录当前可由源码、测试、配置或文档冲突证明的问题，不代表所有问题均已决定修复方式

## 1. 风险等级

| 等级 | 定义 | 处理要求 |
|---|---|---|
| P0 | 可能造成严重安全事件、数据不可恢复或核心服务不可用 | 正式生产前解决或停止相关能力发布 |
| P1 | 高概率影响权限、数据、兼容性或生产稳定性 | 发布前有修复、隔离或明确接受记录 |
| P2 | 明显影响体验、可维护性、测试或运维效率 | 纳入近期计划并设置监控/文档缓解 |
| P3 | 低影响一致性、文案或优化问题 | 常规迭代处理 |

## 2. 风险总表

| ID | 等级 | 风险 | 当前证据 | 主要影响 |
|---|---|---|---|---|
| RSK-001 | P0 | 生产 Compose 使用示例弱数据库密码 | `docker-compose-pgsql.yml` | 数据泄露/篡改 |
| RSK-002 | P0 | Redis 无认证 | Compose 配置 | 多实例状态被读取或操纵 |
| RSK-003 | P1 | LAN 模式为明文 HTTP | Tauri 和系统设置 | Cookie、Key、管理操作被窃听 |
| RSK-004 | P1 | 访问 Key 明文策略与 README 冲突 | `KeyPlaintext` 与文档 | 凭证泄露和错误安全预期 |
| RSK-005 | P1 | 渠道/Tavily Key明文持久化与导出 | 实体和前端导出 | 上游凭证泄露 |
| RSK-006 | P1 | 登录无统一限流/锁定 | 依赖注册与认证服务 | 密码暴力破解 |
| RSK-007 | P1 | 管理写接口无独立 CSRF Token | Cookie 配置 | 跨站请求风险 |
| RSK-008 | P1 | Images 生产服务实现和 DI 注册缺失 | 控制器依赖/服务注册 | 控制器解析失败、运行时 500 或能力误宣传 |
| RSK-009 | P1 | `/health` 仅浅层存活 | `SystemController` | 故障实例仍被认为健康 |
| RSK-010 | P1 | 自动迁移无备份和回滚契约 | 启动初始化 | 发布导致数据不可恢复 |
| RSK-011 | P1 | 日志正文无保留/配额/归档 | 内容寻址存储 | 磁盘耗尽、合规风险 |
| RSK-012 | P1 | PR/普通 push 无 CI 门禁 | workflow 触发条件 | 缺陷直接进入主分支 |
| RSK-013 | P1 | 桌面 CSP 为空且启用 DevTools | Tauri 配置 | 桌面 Web 安全面扩大 |
| RSK-014 | P1 | 桌面正式签名/公证缺失 | 发布 workflow | 安装信任和供应链风险 |
| RSK-015 | P2 | 管理接口授权依赖手工 Require 调用 | Controllers | 新接口容易漏鉴权 |
| RSK-016 | P2 | 管理员修改他人渠道缓存可能延迟失效 | Config/Route 缓存 | 短时间使用旧配置 |
| RSK-017 | P2 | 模型映射是全局模式切换语义 | Route Service | 配置一个映射后其他模型突然不可用 |
| RSK-018 | P2 | OCR 降级只覆盖部分映射路径 | ImageFallback | 相似图片请求行为不一致 |
| RSK-019 | P2 | capacity DTO 与校验语义冲突 | DTO/Validator | 配置可用性和文档不一致 |
| RSK-020 | P2 | 部分 400/403 可触发故障转移/熔断 | Failover Policy | 客户端错误被误判为渠道故障 |
| RSK-021 | P2 | 管理台无全局会话过期处理 | App API helper | 操作失败但用户不知需重登 |
| RSK-022 | P2 | 无 Router/深链/页面状态持久化 | `App.vue` | 刷新和导航体验差 |
| RSK-023 | P2 | 导入缺少逐条预检和冲突预览 | 前端导入逻辑 | 部分失败和覆盖不可预测 |
| RSK-024 | P2 | 实时流断开和真实新鲜度提示有限 | Dashboard | 旧数据被误认为实时 |
| RSK-025 | P2 | 文档配置与当前源码漂移 | README/DEPLOYMENT | 部署错误 |
| RSK-026 | P2 | 部署脚本无 readiness/冒烟/自动回滚 | update scripts | 部署失败仍显示完成 |
| RSK-027 | P2 | 可变镜像和工具链版本 | Docker/CI/Rust | 构建不可复现 |
| RSK-028 | P2 | 前端测试未接入 npm/CI | package.json/workflow | UI 回归难发现 |
| RSK-029 | P2 | 缺少性能、SLA、RPO/RTO 基线 | 当前文档与代码 | 无法判断生产适用性 |
| RSK-030 | P3 | 模型“删除”实际为停用 | 前端/服务语义 | 用户理解错误 |
| RSK-031 | P1 | `ContentAddressedLogs` 的 Up/Down 都会丢失已有日志正文 | SQLite/PostgreSQL migration | 升级或回退造成不可恢复的数据损失 |
| RSK-032 | P2 | 服务端 Docker 镜像依赖本地工作站直接构建推送 | `update_remote_image.sh`/workflow | 产物来源不可审计、发布不可复现 |
| RSK-033 | P1 | 缺少真实 PostgreSQL、Redis 和迁移恢复测试 | 测试集/CI | 数据或多实例缺陷在生产首次暴露 |
| RSK-034 | P2 | 手工流式与数据提取脚本已偏离当前认证和数据库结构 | `scripts/` | 错误验收结论、采集失败或误操作 |
| RSK-035 | P1 | 请求头、嵌套认证信息和图片/base64 正文会原样进入日志持久化 | `ProxyRequestMetadataFactory`、`ProxyLogServiceTests` | 凭证与业务敏感数据泄露、备份合规风险 |

## 3. 安全风险

### 3.1 数据库与 Redis 默认凭据

**风险**：PostgreSQL 示例用户名/密码为 `admin/123456`，Redis 未启用认证。如果 Compose 被直接用于生产或网络暴露，攻击者可能读取用户、渠道、凭证和日志。

**要求**：

- `REQ-RSK-001`（MUST）：生产配置必须从 Secret 注入随机数据库凭据；
- `REQ-RSK-002`（MUST）：Redis 必须启用认证或置于不可被非受信主体访问的隔离网络；
- 数据库与 Redis 端口默认不得映射公网；
- 发布门禁应检测示例密码和空认证。

### 3.2 LAN HTTP

**风险**：桌面 LAN 模式监听 `0.0.0.0`，管理台、Cookie、访问 Key 和上游配置通过明文 HTTP 传输。

**缓解选项**：

1. 将 LAN 标记为仅受信局域网实验能力；
2. 内置本地证书和 HTTPS；
3. 强制用户通过反向代理 TLS；
4. 默认关闭 LAN，并在开启时要求确认。

`REQ-RSK-003`（MUST）：LAN 模式必须展示明确风险和生效地址；正式公网场景必须使用 TLS。

### 3.3 凭证明文

当前冲突：

- README 声称访问 Key只显示一次且数据库只保存哈希；
- 实体仍存在 `KeyPlaintext`；
- 管理台提供列表复制和明文导出倾向；
- 渠道 API Key、Header 和 Tavily Key需要再次编辑/导出。

待决策方案：

| 方案 | 优点 | 代价 |
|---|---|---|
| 只存哈希，创建时显示一次 | 安全边界最清晰 | 无法恢复和明文导出，需要轮换 |
| 应用层加密保存 | 支持恢复/导出 | 需要独立加密主密钥和轮换 |
| 数据库明文保存 | 实现简单 | 高风险，不建议生产 |

`REQ-RSK-004`（MUST）：正式发布前必须选择统一凭证策略，并同步实体、API、管理台、导出和 README。

### 3.4 登录、CSRF 与会话

当前 Cookie 具备 HttpOnly、SameSite=Lax、滑动续期，但：

- 未发现统一登录限流；
- 未发现账户锁定或失败审计；
- 写接口没有独立 CSRF Token；
- LAN HTTP 下 Secure 属性不会生效；
- 前端无全局 401 跳转。

`REQ-RSK-005`（MUST）：正式多人或网络部署应增加登录速率限制、失败审计和 CSRF 防护，并定义会话撤销机制。

### 3.5 日志敏感内容原样持久化

当前日志链路不是“已经完成脱敏的安全视图”：

- `ProxyRequestMetadataFactory` 会复制客户端请求头，Authorization 和 Cookie 可能进入持久化元数据；
- 日志正文测试明确验证嵌套 MCP Authorization token 不被修改；
- 图片/base64、工具参数、原始 SSE 和自定义 Header 也可能进入内容寻址存储；
- 内容分块、压缩和 SHA-256 去重只解决存储与完整性，不提供加密或脱敏；
- 数据库、备份、日志导出和详情读取因此都必须按高敏数据保护。

该风险由 `REQ-RSK-006` 的日志数据分类、保留、删除、备份和访问策略统一约束。正式多人或网络部署前还必须定义：安全摘要视图、受保护原始槽位、字段级脱敏规则、原始内容访问审计和静态加密方案。

## 4. 数据与合规风险

### 4.1 日志包含敏感内容

日志可能保存：

- 客户端 IP；
- 用户输入、代码和文档；
- 工具参数和结果；
- 上游响应；
- 图片识别文本；
- Web Search 查询；
- 自定义 Header。

当前缺少：

- 默认保留期；
- 租户级数据删除；
- 单条日志删除；
- 审计读取记录；
- 容量配额和自动清理；
- 合规导出和匿名化。

`REQ-RSK-006`（MUST）：部署前必须确认日志数据分类、保留、删除、备份和访问策略。

### 4.2 自动迁移

应用启动自动迁移简化部署，但在大表、多实例或破坏性迁移时可能：

- 竞争迁移锁；
- 长时间阻塞；
- 部分迁移后启动失败；
- 无法应用回滚；
- 破坏旧版本兼容性。

`REQ-RSK-007`（MUST）：生产数据库迁移必须有唯一执行者、备份、时长评估、兼容窗口和恢复验证。

当前两套 `ContentAddressedLogs` migration 是已确认的具体阻断风险，而不只是一般性迁移担忧：

- SQLite `20260810233458_ContentAddressedLogs` 和 PostgreSQL `20260810233510_ContentAddressedLogs` 的 `Up` 都直接删除 `RequestLogDetails`、`RequestLogStreamLines`，没有向新内容寻址表回填旧正文；
- 两者的 `Down` 都删除新内容表，只创建空的旧表；
- 因此升级会丢失旧日志正文，回退还会丢失升级后写入的新正文，Schema 恢复不等于数据恢复。

`REQ-RSK-014`（MUST）：在替换为可保留数据的迁移或完成明确的数据丢弃审批、导出和恢复验证前，发布门禁必须阻止该迁移进入含历史日志的环境。

### 4.3 内容寻址存储

去重可降低容量，但带来：

- 引用计数/孤立清理复杂度；
- 哈希碰撞虽低但需完整性校验；
- 删除一个租户内容时共享块仍可能被其他内容引用；
- 损坏块影响多个日志；
- 加密去重存在设计取舍。

需要监控块、Manifest、引用、孤立对象和读取校验失败。

## 5. 功能和兼容性风险

### 5.1 Images 能力

`ImagesController` 依赖 `IProxyImagesEndpointService`，但生产代码中没有该接口的实现或 DI 注册；`HttpUpstreamClient` 虽实现 `IImagesUpstreamClient`，生产 DI 也只按 `IUpstreamClient` 和 `IUpstreamModelClient` 注册。当前 Images 端点因此只有控制器契约和 fake 测试证据，真实应用解析控制器时会因依赖缺失而失败。

`REQ-RSK-008`（MUST）：在将 Images 标为正式能力前，必须补齐 `IProxyImagesEndpointService` 生产实现与 DI 注册，并通过真实容器启动、依赖解析、OpenAI/xAI 上游集成和错误路径测试。

### 5.2 模型映射全局语义

只要任一启用渠道存在模型映射，路由就进入显式映射模式。管理员可能只为一个模型添加映射，却导致其他模型不再使用通用渠道。

缓解：

- 管理台保存前显示影响范围；
- 提供“显式映射模式”状态；
- 支持通用兜底的产品决策；
- 测试混合配置。

### 5.3 400/403 故障分类

某些 400 可能是客户端参数问题，403 可能是渠道授权问题。统一故障转移可能：

- 对所有渠道重复发送无效请求；
- 把客户端问题计入渠道熔断；

- 增加延迟和费用。

需要按错误正文、入口校验和渠道策略细化分类。

### 5.4 协议不可等价字段

不同协议对 Reasoning、MCP、工具、JSON Schema、文件、多模态和 finish reason 的表达并非完全等价。风险是静默丢字段或生成上游拒绝的请求。

要求：

- 维护字段和事件支持矩阵；
- 不能等价时明确降级或拒绝；
- 渠道 compat 行为可观测；
- 每个新增字段更新九方向测试。

### 5.5 OCR 降级边界

当前 OCR 主要在命中显式模型映射但不支持图片时触发；无映射的通用渠道可能表现不同。需要决定：

- 是否把图片能力判断统一到所有路径；
- OCR 成本如何统计；
- 图片和识别结果如何保留；
- 缓存是否跨用户共享。

## 6. 运维与发布风险

### 6.1 健康检查

`/health` 仅返回静态 ok，不检查：

- 数据库连接；
- 最新迁移；
- Redis；
- 磁盘空间；
- 内容存储写入；
- 上游可用性。

负载均衡器可能继续向不可服务实例发送请求。

`REQ-RSK-009`（MUST）：增加 readiness，并让部署脚本等待其成功。

### 6.2 部署脚本

风险：

- 首次部署目录不存在；
- 无备份；
- 无不可变镜像；
- 无冒烟；
- 无失败回滚；
- 强制重建造成中断；
- `both` 可能混用不一致环境。

### 6.3 构建供应链

- Docker Node 22、CI Node 24；
- Rust 使用浮动 stable；
- 缺少 `Cargo.lock`；
- 基础镜像标签浮动；
- 无 SBOM/镜像扫描；
- 桌面签名和公证不足。

`REQ-RSK-010`（SHOULD）：锁定工具链和依赖，生成 SBOM、哈希和签名。

### 6.4 服务端镜像发布链和集成证据

当前唯一 GitHub workflow 只发布桌面产物，不构建或推送服务端 Docker 镜像。`update_remote_image.sh` 在操作者本地工作站执行 buildx、直接推送可变镜像标签并立即远程部署，导致构建环境、源码状态、缓存、凭据使用和产物摘要缺少统一审计链。

与此同时，CI 仅执行后端测试；未发现真实 PostgreSQL 迁移矩阵、真实 Redis/多实例共享状态、数据库备份恢复或迁移失败恢复测试。当前发布成功不能证明生产依赖和数据恢复路径可用。

- `REQ-RSK-015`（MUST）：服务端镜像必须由受控 CI 从干净提交构建、扫描、记录 digest/provenance 后发布；
- `REQ-RSK-016`（MUST）：正式发布必须通过真实 PostgreSQL、Redis、多实例和迁移备份恢复集成测试。

## 7. 前端体验风险

### 7.1 会话失效

API helper 对 401 只抛错误，未统一清除会话并跳登录。用户可能连续看到 Toast 而不知道重新登录。

### 7.2 页面无 URL 状态

使用 `activeTab` 而非 Router：

- 无深链；
- 刷新回仪表盘；
- 浏览器前进/后退无效；
- 页面切换销毁筛选和草稿；
- 无未保存变更确认。

### 7.3 表单校验不一致

部分页面依赖后端错误和 Toast，缺少行内必填、长度、密码规则。导入只检查顶层形态，缺少冲突预览和逐条结果。

### 7.4 无障碍

- ECharts 无文本替代；
- 部分可点击 div 缺键盘语义；
- 图标按钮 aria-label 不统一；
- 无 reduced motion；
- 焦点样式不统一。

`REQ-RSK-011`（SHOULD）：管理台应建立全局错误、会话、表单、路由和无障碍规范。

## 8. 文档漂移

已知不一致：

- README 仍出现 `OPENCODEX_DB_PATH`；
- README/部署文档描述已清理的日志等级变量；
- 部分内部/桌面环境变量未文档化；
- `capture_real_sse.sh` 使用旧认证路径；
- 数据提取脚本假设 SQLite 和个人 SSH Key 路径；
- `test_streaming.py` 依赖未声明的 `requests`，流式脚本仍默认使用 `change-me`；
- 历史 `stream_fix_plan.md` 引用已不存在结构；
- 未跟踪 `doc/proxy-conversion/` 基于其他提交，需要重新校验。

`REQ-RSK-012`（MUST）：正式发布文档必须由当前配置源和测试验证，历史方案不得混入现行操作说明。

`REQ-RSK-017`（MUST）：手工测试、数据采集和运维脚本必须声明依赖、使用当前认证/API/Schema，并移除个人路径和示例凭据默认值；否则必须标记停用且不得作为验收证据。

## 9. 开放决策清单

| ID | 决策 | 推荐方向 | 截止点 |
|---|---|---|---|
| TBD-RSK-01 | 访问 Key 是否只存哈希 | 推荐只存哈希，创建时一次展示 | 安全正式版前 |
| TBD-RSK-02 | 渠道/Tavily Key如何加密 | 应用层加密 + 独立主密钥 | 多人生产前 |
| TBD-RSK-03 | LAN 是否正式支持 | 保留但明确受信网络，后续 TLS | 桌面正式版前 |
| TBD-RSK-04 | Images 是否正式能力 | 通过 DI/集成测试后决定 | PRD 发布前 |
| TBD-RSK-05 | capacity 是否允许不限 | 推荐明确 `0/空=不限` 或强制正数二选一 | 配置稳定版前 |
| TBD-RSK-06 | 400/403 是否故障转移 | 按错误分类细化 | 路由 SLA 前 |
| TBD-RSK-07 | 日志保留期和容量 | 按部署形态设置默认与上限 | 生产前 |
| TBD-RSK-08 | OCR 成本和缓存隔离 | 计入主请求或独立列示 | 计费发布前 |
| TBD-RSK-09 | Web Search Tavily 成本 | 与模型成本分列 | 计费发布前 |
| TBD-RSK-10 | 自动迁移执行方式 | 生产推荐独立迁移 Job | 多实例前 |
| TBD-RSK-11 | 正式 SLA/RPO/RTO | 按 SQLite/Postgres 分层 | 对外发布前 |
| TBD-RSK-12 | 桌面自动更新 | 签名成熟后启用 | 正式分发前 |

## 10. 风险接受流程

任何 P0/P1 风险若不在发布前修复，必须留下：

1. 风险描述和影响范围；
2. 负责人；
3. 临时缓解措施；
4. 监控或检测方式；
5. 用户可见说明；
6. 到期时间；
7. 回滚或停用开关；
8. 审批记录。

`REQ-RSK-013`（MUST）：没有书面接受记录的 P0/P1 风险不得被默认视为可接受。

## 11. 风险验收标准

| 编号 | 验收 |
|---|---|
| AC-RSK-01 | CI 能阻止示例数据库密码进入生产配置 |
| AC-RSK-02 | LAN 开启时展示风险，HTTPS 部署通过验证 |
| AC-RSK-03 | Key 存储/展示/导出策略在代码和文档中一致 |
| AC-RSK-04 | 登录限流和 CSRF 负向测试通过 |
| AC-RSK-05 | readiness 能检测数据库故障和迁移失败 |
| AC-RSK-06 | 日志保留和容量清理在大数据量测试中生效 |
| AC-RSK-07 | Images 生产依赖启动和真实上游测试通过或功能明确下线 |
| AC-RSK-08 | 部署失败可以自动/手工恢复旧版本和数据 |
| AC-RSK-09 | 普通 PR 执行后端、前端、迁移和安全门禁 |
| AC-RSK-10 | 所有开放决策有负责人、状态和截止点 |
| AC-RSK-11 | 非空旧库执行 `ContentAddressedLogs` Up/Down 后的数据影响已逐字段验证，且无未审批的数据丢失 |
| AC-RSK-12 | 服务端镜像可从 commit 追溯到 CI 构建记录、扫描结果、digest 和部署版本 |
| AC-RSK-13 | 真实 PostgreSQL、Redis、多实例和备份恢复集成测试进入发布门禁 |
| AC-RSK-14 | 流式/采集脚本通过当前认证和数据库 Schema 的可重复运行验证 |

## 12. 证据索引

| 风险领域 | 主要证据 |
|---|---|
| 默认凭据 | `docker-compose-pgsql.yml` |
| Cookie/CSRF | `OpenCodexServiceCollectionExtensions.cs` |
| Key 明文 | `AccessApiKey.cs`、`ApiKeyService.cs`、`AccessKeys.vue` |
| LAN | `DesktopSystemSettingsStore.cs`、`src-tauri/src/lib.rs` |
| Images | `ImagesController.cs`、服务注册、Images 测试 |
| 健康检查 | `SystemController.cs` |
| 自动迁移 | `OpenCodexDatabaseInitializer.cs` |
| 破坏性日志迁移 | SQLite/PostgreSQL `ContentAddressedLogs` migrations |
| 日志存储 | `LogContentCodec.cs`、`LogContentStore.cs` |
| CI/发布 | `.github/workflows/desktop-release.yml`、`update_remote_image.sh` |
| 集成测试缺口 | 测试项目、`frontend/package.json`、发布 workflow |
| Tauri 安全 | `src-tauri/tauri.conf.json`、`Cargo.toml` |
| 文档/脚本漂移 | `README.md`、`DEPLOYMENT.md`、`scripts/capture_real_sse.sh`、`scripts/extract_sse_test_data.sh`、`scripts/test_streaming.py` |
