# 模型目录同步方案（导入按钮改下拉 + 远端 JSON 同步）

> 状态：决策全部确认（Q1-Q21），可进入实施。同步源尚未搭建，见批 0。
> 更新时间：2026-08-27。文中代码事实均逐文件核对，未依赖记忆。

## 1. 需求与已确认决策

需求：模型信息页「导入」按钮改成下拉；`同步最新模型` 从一份集中维护的远端 JSON 拉取模型目录，地址可配置。

已锁定的决策（作为设计前提，不再讨论）：

| 编号 | 决策 |
| --- | --- |
| Q1 | 权威 JSON 由你手动维护，放服务器静态目录、NGINX 对外提供，不进仓库 |
| Q2 | 需要出厂内置默认地址 |
| Q3 | 增量同步**只新增**：本地已存在的 `model_key` 一律不动 |
| Q4 | 远端已移除、本地仍有的模型不动，以本地为主 |
| Q5 / Q14 | 供应商参与同步，且**只新增**，不改已存在供应商的名称/排序/启用状态 |
| Q6 | 定价属于同步范围 |
| Q7 | 同步地址只放环境变量，管理台不可改、不提供设置入口 |
| Q8 | 不做私有源鉴权、不做定时同步、不做签名校验 |
| Q9 | 远端 JSON 不引入额外元信息 |
| Q11 | 内置默认地址 `https://ocxpmodel.shldev.me/model-catalog.json`（暂定），允许 http |
| Q12 | 下拉再加一项覆盖入口并二次确认，作为远端改价下发的通路（文案定为「覆盖已有模型」，见 3.1） |
| Q13 | 本地已停用的同名模型也算「已有」，增量同步跳过、不复活 |
| Q15 | 保留「预检 → 确认」两步 |
| Q16 | 同步创建的记录 `source = sync` |
| Q17 | 拉取上限 5 MB、超时 60 秒、最多 3 次重定向 |
| Q18 / Q23 | 同步失败写 warning 一行，含地址、失败原因与响应体，**不截断** |
| Q19 | 对话框不展示同步地址 |
| Q22 | 覆盖模式二次确认用勾选框，不要求手输确认词 |
| Q20 / Q24 | 前端 `api/` 死代码本次**不动**，留待你核实后单独处理 |
| Q25 | 同步源尚未就绪，本方案需包含搭建该 JSON 的任务（批 0） |
| Q21-1 | 覆盖模式**不删除**本地有、远端没有的模型 |
| Q21-2 | 覆盖模式**不改写**已存在供应商的名称/排序/启用状态 |
| Q21-3 | 覆盖模式**不启用**本地已停用的同名模型 |

两点由决策直接推出的结构性简化与冲突处理：

- Q7 + Q9 → **不新增数据表、不做 EF 双库迁移、不做同步设置接口、不做 ETag/304**。
- Q12 选 C 后下拉需要三项，与 Q10「只有两项」冲突。按 Q12 为准：`同步最新模型`、`导入本地 json`、`覆盖已有模型`，第三项前加分隔线并用危险色。

## 2. 现状核对

### 2.1 前端

| 事实 | 位置 |
| --- | --- |
| 「导入」按钮，点击触发隐藏 file input | [Pricing.vue](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/Pricing.vue:24) |
| 隐藏 `<input type="file">` | [Pricing.vue](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/Pricing.vue:34) |
| 导入预检/确认对话框，三态 `preview` / `done` / 错误 | [Pricing.vue](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/Pricing.vue:76) |
| `selectCatalogFile` / `handleCatalogFileSelected` / `confirmCatalogImport` | [Pricing.vue](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/Pricing.vue:613) |
| 前端解析与状态机（校验 `type=model_catalog`、`version=1`） | [modelCatalogImportState.js](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/modelCatalogImportState.js:16) |
| 移动端工具栏 `grid`，规则只匹配 `.toolbar-actions` 的**直接子** `.el-button` | [Pricing.vue](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/Pricing.vue:1109) |
| 表格已有「来源」列直接展示 `source` | [Pricing.vue](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/Pricing.vue:166) |
| 模型信息页已是超管专属（菜单 + 路由双 gating） | [App.vue](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/App.vue:165) |
| 页面统一用 `props.api()`；`App.vue` 自带一份 `api()` 实现 | [App.vue](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/App.vue:174) |
| 全仓检索（含 `src-tauri`、测试、构建配置）显示 `frontend/src/api/` 下除 `sseClient.js`（3 处引用）外无引用点，且 `client.js` 的 envelope 解包规则与 `App.vue` 已分叉；按 Q24 本次不动这些文件 | [api/client.js](/persistent/home/shl/w/work/shl/OpenCodex/frontend/src/api/client.js:34) |

### 2.2 后端

| 事实 | 位置 |
| --- | --- |
| `GET /model-catalog/export`、`POST /model-catalog/import?dryRun=`，均 `RequireSuperadmin()` | [ModelCatalogController.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Presentation/OpenCodex.Api/Controllers/ModelCatalogController.cs:88) |
| 导入语义：供应商按 `code`、模型按全局 `model_key`（均 `OrdinalIgnoreCase`）匹配，存在即更新、不存在即创建 | [ModelCatalogService.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelCatalogService.cs:425) |
| 单事务写入，异常整批回滚，成功后 `BumpPricingVersion()` | [ModelCatalogService.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelCatalogService.cs:589) |
| 价格整体替换：带 `pricing` 删旧建新，`pricing: null` 删除该模型价格 | [ModelCatalogService.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelCatalogService.cs:1358) |
| 更新会写 `model.Enabled = transfer.Enabled`，即远端启用状态会覆盖本地停用 | [ModelCatalogService.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelCatalogService.cs:653) |
| 文档级校验：type/version/重复键/枚举/负价，任一失败拒绝整批 | [ModelCatalogService.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelCatalogService.cs:1924) |
| 写入阶段 `trackedProvidersByCode[providerCode]` 直接索引，**文档缺该 provider 条目会抛 `KeyNotFoundException`**（不在 catch 过滤内 → 500） | [ModelCatalogService.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Libraries/OpenCodex.Core/Services/ModelCatalogService.cs:630) |
| `ModelCatalogSources` 目前只有 `manual`；`UpdateModel` 会把 `Source` 写回 `manual` | [ModelCatalogConstants.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Libraries/OpenCodex.Domain/Domain/ModelCatalogConstants.cs:3) |
| 环境变量统一走 `OpenCodexRuntimeSettingsProvider`（`OpenCodex:*` 优先 `OPENCODEX_*`） | [OpenCodexRuntimeSettingsProvider.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Presentation/OpenCodex.Api/Configuration/OpenCodexRuntimeSettingsProvider.cs:15) |
| 出站 HTTP 统一 `AddHttpClient<接口, 实现>` + `SocketsHttpHandler` | [OpenCodexServiceCollectionExtensions.cs](/persistent/home/shl/w/work/shl/OpenCodex/opencodex_proxy/src/Presentation/OpenCodex.Api/Hosting/OpenCodexServiceCollectionExtensions.cs:106) |
| PRD 已记录导入导出契约，实施后需同步 | [10-admin-console.md](/persistent/home/shl/w/work/shl/OpenCodex/prd/10-admin-console.md:573) |

### 2.3 两个易混点

- `wwwroot/ocxp_codex_official_models.json` 是给 Codex 客户端 `/models` 用的静态能力清单（`CodexOfficialModelCatalogFactory` 读取），与本次同步的模型目录不是同一份数据。
- 工作树有未提交改动（`Pricing.vue`、`SystemSettings.vue`、`ModelCatalogService.cs`），其中模型删除已改为「启用→停用，停用→硬删除」两段式。本方案在其之上叠加，不回退。

## 3. 目标方案

### 3.1 两种同步模式

Q21 三条都选了「以本地为主」，于是两种模式的差异收窄到**一件事**：已存在模型的元数据与价格是否被远端改写。其余行为完全一致。

| | 增量同步（`同步最新模型`） | 覆盖已有模型 |
| --- | --- | --- |
| 远端有、本地无的模型 | 创建 + 写价格，`source = sync` | 同左 |
| 远端有、本地已有的模型 | **完全不动** | 改写 `display_name`/`description`/`match_type`/`match_pattern`/`catalog`/`capabilities` + 价格，`source = sync` |
| 已存在模型的启用状态 | 不动 | **不动**（Q21-3） |
| 远端有、本地已有的供应商 | 不动 | **不动**（Q21-2） |
| 远端有、本地无的供应商 | 创建 | 创建 |
| 远端无、本地有的模型 | 不动 | **不动**（Q21-1） |
| 触发门槛 | 预检 → 确认 | 预检 → 危险提示 + 勾选确认 → 确认 |

两条由上述答案自然外推、我据此定稿的规则（若与你本意不符请指出）：

1. **启用状态永远以本地为准，双向都不同步**。Q21-3 只问了「本地停用 + 远端启用」，反向的「本地启用 + 远端停用」按同一原则也不改写，否则同一个字段会出现两套方向不一致的规则。
2. **远端 `pricing: null` 不删除本地价格**。现有导入在这种情况下会删掉该模型的价格（`ReplaceImportedPricing`），但「覆盖」在这套以本地为主的语义里不应包含静默清空价格；因此 `pricing_deleted` 在两种同步模式下恒为 0。

命名建议：既然覆盖模式既不删模型、也不动供应商与启用状态，「强制全量覆盖」这个叫法会让人以为它会清理本地数据。下拉项文案改为 `覆盖已有模型`，危险提示里再说明具体覆盖哪些字段。

### 3.2 数据流

```
管理台「同步最新模型」/「覆盖已有模型」
  → POST /model-catalog/sync?mode=incremental|overwrite&dryRun=true    （超管）
      → 读 OPENCODEX_MODEL_CATALOG_SYNC_URL（空白回落内置默认地址）
      → HttpClient GET（60s 超时、5MB 上限、http/https、最多 3 跳、自动解压、剥 BOM）
      → 反序列化为 ModelCatalogTransferDocument（沿用导入契约）
      → ImportModelCatalog(document, dryRun: true, options)
      → 返回计数 + created_model_keys / skipped_model_keys / overwritten_model_keys
  → 对话框展示差异（覆盖模式另需勾选确认）
  → POST /model-catalog/sync?mode=...&dryRun=false （重新拉取 → 单事务写入）
  → 前端刷新供应商 + 模型列表
```

### 3.3 配置（只读环境变量）

| 项 | 键 | 默认 |
| --- | --- | --- |
| 同步地址 | `OpenCodex:ModelCatalogSyncUrl` / `OPENCODEX_MODEL_CATALOG_SYNC_URL` | `https://ocxpmodel.shldev.me/model-catalog.json` |

- 归一化：`trim`；空白回落内置默认值；非 `http`/`https` 在同步时返回 400，不在启动时崩服务。
- 管理台无修改入口，也不展示地址（Q19）；排障靠 warning 日志里的地址字段。
- 需补文档：`.env.example`、`README.md`、`DEPLOYMENT.md`、`prd/12-configuration.md`。

### 3.4 远端 JSON 契约

就是现有导出文件，字段不增不减：

```json
{
  "type": "model_catalog",
  "version": 1,
  "exported_at": "2026-08-27T02:00:00.0000000+00:00",
  "providers": [{ "code": "openai", "name": "OpenAI", "enabled": true, "sort_order": 10 }],
  "models": [
    {
      "provider_code": "openai",
      "model_key": "gpt-5.6",
      "display_name": "GPT-5.6",
      "match_type": "exact",
      "match_pattern": "gpt-5.6",
      "catalog": {},
      "capabilities": { "supports_image": true, "context_window": 272000 },
      "enabled": true,
      "pricing": {
        "currency": "USD",
        "enabled": true,
        "rules": [{ "billing_item": "input", "billing_mode": "per_million_tokens", "unit_price": 1.25, "enabled": true }]
      }
    }
  ]
}
```

- `version != 1` 拒绝并提示升级程序，不做猜测式兼容；未知字段被 `System.Text.Json` 默认忽略。
- `models: []` 不算错误：增量模式提示「没有新模型」，覆盖模式提示「远端目录为空，未做任何变更」。

### 3.5 实现路径

给 `ImportModelCatalog` 加一个 `ModelCatalogImportOptions` 重载（`class`，不用 `record`），既有无参调用行为不变：

| 选项 | 增量同步 | 覆盖已有模型 | 导入本地 json（现状保持） |
| --- | --- | --- | --- |
| `SkipExistingModels` | true | false | false |
| `SkipExistingProviders` | true | true | false |
| `PreserveLocalEnabled` | true | true | false |
| `KeepLocalPricingWhenRemoteNull` | true | true | false |
| `Source` | `sync` | `sync` | `manual` |

规则由真正写库的那层保证，而不是靠调用方先把文档裁剪一遍 —— 「永不覆盖本地」是这次的核心约束，藏在调用方的数据变形里迟早会被绕过。

另一种「同步服务先过滤文档、不动核心服务」的写法要放弃：它会撞上 2.2 里那条 `KeyNotFoundException` 缺陷 —— 一旦把本地已存在的供应商从文档里删掉，其名下新模型在写入阶段直接 500。

顺带修掉该缺陷：`trackedProvidersByCode[providerCode]` 改 `TryGetValue`，缺失时返回 400 可读错误。这条同时保护现有「导入本地 json」路径。

`SkipExistingProviders=true` 时仍需把已存在供应商放进 `trackedProvidersByCode`（从数据库实体取），否则新模型挂不上供应商 —— 这是实现时最容易踩的一处。

### 3.6 接口契约

| 方法 | 路由 | 权限 | 说明 |
| --- | --- | --- | --- |
| POST | `/model-catalog/sync?mode=incremental\|overwrite&dryRun=true\|false` | superadmin | 拉取远端 JSON，按模式预检或写入 |

`mode` 缺省 `incremental`；非法值返回 400（不静默降级）。响应在现有 `ModelCatalogImportResult` 上追加字段，不改既有字段语义：

```json
{
  "dry_run": true,
  "mode": "incremental",
  "providers": { "created": 1, "updated": 0, "unchanged": 4 },
  "models": { "created": 3, "updated": 0, "unchanged": 0 },
  "skipped": 22,
  "created_model_keys": ["gpt-5.6-sol", "gemini-3.5-pro", "grok-5"],
  "skipped_model_keys": ["gpt-5.6", "claude-4.6-sonnet"],
  "overwritten_model_keys": [],
  "pricing_deleted": 0,
  "error_count": 0,
  "errors": []
}
```

断言点：增量模式下 `models.updated`、`overwritten_model_keys` 恒为空/0；两种同步模式下 `providers.updated` 与 `pricing_deleted` 都恒为 0。

失败一律 400 + `errors` 且不写库：地址非法、DNS/连接/超时失败、状态码非 2xx、响应超 5 MB、JSON 非法、文档校验失败。

### 3.7 拉取硬化

- 独立 `AddHttpClient<IModelCatalogSyncClient, ModelCatalogSyncClient>`，超时 60 秒（Q17），不复用上游代理那条无限超时 client。
- 响应体上限 5 MB：先看 `Content-Length`，流式读取时再兜底计数，超限立即中止。
- 允许 `http` 与 `https`（Q11），拒绝 `file://`、`data:` 等；最多 3 次重定向。
- 开启自动解压（NGINX 常开 gzip）；不校验 `Content-Type`（静态目录可能返回 `text/plain`）；解析前剥掉 UTF-8 BOM，否则 `System.Text.Json` 会直接报错。
- 允许内网与回环地址（自建源常见），因此该路由必须保持 superadmin-only；PRD 风险章需写明这是超管触发的出站请求。
- 正式写入前重新拉取一次，避免预览与写入之间源站已变；按新结果写入并返回实际计数。
- 失败写一行 warning：地址、HTTP 状态、失败原因、**响应体原样不截断**（Q23）。`ModelCatalogSyncService` 需注入 `ILogger`（`ModelCatalogService` 现在完全没用 logger）。单条日志最大可达 5 MB，可接受的依据是 docker-compose 已配 `json-file` 轮转（`max-size` 默认 50m、`max-file` 默认 5），不会无界增长。

### 3.8 前端交互

```vue
<el-dropdown trigger="click" @command="handleCatalogImportCommand">
  <el-button :icon="Upload" :loading="catalogImporting || catalogSyncing">
    导入<el-icon class="el-icon--right"><ArrowDown /></el-icon>
  </el-button>
  <template #dropdown>
    <el-dropdown-menu>
      <el-dropdown-item command="sync" :icon="RefreshRight">同步最新模型</el-dropdown-item>
      <el-dropdown-item command="file" :icon="Upload">导入本地 json</el-dropdown-item>
      <el-dropdown-item command="overwrite" :icon="Warning" divided>覆盖已有模型</el-dropdown-item>
    </el-dropdown-menu>
  </template>
</el-dropdown>
```

- 复用同一个确认对话框，用 `origin: "file" | "sync" | "overwrite"` 区分标题与摘要区。
- 增量模式摘要：将新增的 `model_key` 列表（超 20 条折叠计数）+ 已存在跳过数量。
- 覆盖模式摘要：红色 `el-alert` 说明「将按远端改写 N 个已存在模型的名称、匹配规则、能力与价格，本地修改不可恢复；启用状态、供应商信息、本地独有模型不受影响」，并提供「先导出当前目录」按钮（复用现有导出流程留快照），必须勾选「我已了解本地修改将被覆盖」才能点确认。
- 拉取可能耗时最多 60 秒：对话框先进入 `loading` 态并显示「正在拉取远端目录…」，期间禁用整个下拉，防重复触发。
- 远端无新模型时（增量模式）不弹对话框，直接 `ElMessage.info("没有新模型，已是最新")`。
- 移动端 CSS 必须补 `.pricing-page .toolbar-actions > :deep(.el-dropdown)` 撑满、内部按钮 `width: 100%`：现有规则只匹配直接子 `.el-button`（第 1109 行），包一层 `el-dropdown` 后两列网格会错位。

### 3.9 前端 api 层现状（Q24：本次不动）

按 Q24 本次不删任何文件，也不把 `Pricing.vue` 改成走 `api/modelCatalog.js`；新增的同步调用继续用 `props.api()`，与页面既有写法一致。

留档一份检索结论，供你后续核实：`rg` 全仓（含 `src-tauri`、后端测试、构建配置）检索 `api/apiKeys|channels|client|logs|modelCatalog|monitor|session|stats|systemSettings|users|webSearch` 的 import 点，命中数为 0；唯一被引用的是 `api/sseClient.js`（`Channels.vue:1512`、`Dashboard.vue:333`、`Logs.vue:726`）。另外 `api/client.js:34` 的 envelope 解包是「见到 `ErrorCode`/`ErrorMsg` 就解包」，而实际在跑的 `App.vue:174` 要求同时存在布尔 `succeeded` 字段 —— 两份实现行为不同，日后若要复活 api 层需先统一这一点。

## 4. 维护端发布流程

1. 在基准实例（现网 `ocxp-dev`）把模型目录调到期望状态，点「导出」拿到 `model-catalog-*.json`。
2. 复核后重命名为 `model-catalog.json`，上传到服务器静态目录，由 NGINX 提供在 `https://ocxpmodel.shldev.me/model-catalog.json`；文件不入仓库（Q1）。
3. 各实例管理员点「同步最新模型」补齐新模型；需要把改价下发到存量模型时，走「覆盖已有模型」。
4. 该文件含定价、不含任何上游凭证。若不希望定价公开，需在 NGINX 侧限制来源（当前方案不带鉴权头，Q8）。

## 5. 分批实施计划

### 批 0：搭建同步源（Q25，前置且需你授权）

同步源 `https://ocxpmodel.shldev.me/model-catalog.json` 目前不存在，代码可以先做（拉取失败按普通网络错误提示），但上线前必须把它建起来。步骤：

1. **DNS**：把 `ocxpmodel.shldev.me` 解析到部署服务器公网 IP（your-cloud-provider your-aws-region）。这一步在你的 DNS 服务商侧，我无法代做。
2. **静态目录**：服务器上建 `/www/wwwroot/ocxpmodel`，与现有 `/www/wwwroot/ocxp` 同级，权限只读对外。
3. **NGINX 站点**：新增 server block，`server_name ocxpmodel.shldev.me`，`root /www/wwwroot/ocxpmodel`，只放行 `GET`，`location = /model-catalog.json` 显式设置 `default_type application/json`、`gzip on`、`add_header Cache-Control "public, max-age=300"`。
4. **证书**：签发 `ocxpmodel.shldev.me` 证书（或复用已有通配符证书）。未就绪期间方案允许 http（Q11），但建议一次做齐。
5. **首版 JSON**：从现网 `ocxp-dev` 管理台点「导出」，复核后上传为 `/www/wwwroot/ocxpmodel/model-catalog.json`。
6. **连通性验证**：宿主 `curl -I https://ocxpmodel.shldev.me/model-catalog.json`，容器内 `docker exec ocxp-dev curl -sI https://ocxpmodel.shldev.me/model-catalog.json`，两者都要 200。容器内不通通常是 DNS 解析或出网策略问题，必须在这一步暴露而不是等同步报错。

改动服务器 NGINX 与新增站点属于影响线上的操作，我不会自行执行；你确认后我再按上述步骤操作，或你自己做完告诉我地址已就绪。

### 批 1：后端导入模式扩展 + 缺陷修复

- 目标：`ImportModelCatalog` 支持 3.5 表格里的 5 个选项，落地 `skipped` / `created_model_keys` / `skipped_model_keys` / `overwritten_model_keys`；`KeyNotFoundException` 变 400。
- 文件：`ModelCatalogService.cs`、`IModelCatalogService.cs`、`ModelCatalogTransferDtos.cs`、`ModelCatalogConstants.cs`。
- 风险：低-中。用重载扩展接口，既有调用点与测试不动；结果 DTO 只加字段。
- 验收：新增 xunit 用例 + 既有 539 个测试全绿。

### 批 2：拉取与同步接口

- 目标：`POST /model-catalog/sync` 两种模式可用。
- 文件：`OpenCodex.Core/Services/ModelCatalogSyncService.cs`（新）、`OpenCodex.CoreBase/Services/IModelCatalogSyncService.cs`（新）、`ModelCatalogController.cs`、`OpenCodexServiceCollectionExtensions.cs`、`OpenCodexRuntimeSettings(.Provider)`。
- 风险：中。新增出站请求面，靠 superadmin + scheme 白名单 + 5 MB 限长收敛；不塞进已 2224 行的 `ModelCatalogService`。
- 验收：xunit（stub `HttpMessageHandler`）覆盖第 6 章 1-12 项；`RouteTests` 断言新路由存在且非超管 403。

### 批 3：前端下拉与同步流程

- 目标：按钮变下拉、三项可用、覆盖模式有危险确认、移动端不塌。
- 文件：`frontend/src/Pricing.vue`、`frontend/src/modelCatalogImportState.js`、`frontend/src/modelCatalogImportState.test.js`。
- 风险：低-中。需在工作树未提交的 `Pricing.vue` 改动之上叠加；移动端 CSS 选择器易漏（已定位 1109 行）。
- 验收：`node --test frontend/src` 全绿；桌面/移动视口手工走完 5 条路径（有新模型、无新模型、覆盖已有模型、地址不可达、远端 JSON 非法）。

### 批 4：文档

- 文件：`prd/10-admin-console.md`（14.4 增同步小节）、`prd/12-configuration.md`（环境变量行）、`prd/17-known-limitations-and-risks.md`（出站拉取风险 + 覆盖模式不可恢复 + 失败日志含完整响应体）、`.env.example`、`README.md`、`DEPLOYMENT.md`（同步源站点与发布流程）。
- 风险：低。

## 6. 测试计划

后端（xunit，沿用 `ModelCatalogServiceTests` 的 SQLite 临时库夹具 + stub `HttpMessageHandler`）：

1. 增量模式、远端全是新模型 → `dryRun` 计数正确且库无变化；`dryRun=false` 后模型与价格落库、`source = sync`。
2. 增量模式、模型本地已存在 → 计入 `skipped`，字段/价格/`enabled`/`source` 全部不变。
3. 增量模式、本地该模型已停用 → 依然 `skipped`，不被复活（Q13）。
4. 增量模式、供应商本地已存在 → 名称/排序/启用状态不变（Q14）。
5. 增量模式、供应商与其名下模型都是新的 → 一并创建。
6. 覆盖模式、模型本地已存在 → 名称/匹配/能力/价格被改写，`source = sync`，`overwritten_model_keys` 命中。
7. 覆盖模式、本地停用远端启用 → **仍保持停用**（Q21-3）；反向的本地启用远端停用 → 仍保持启用。
8. 覆盖模式、供应商本地已存在 → 名称/排序/启用状态不变（Q21-2）。
9. 覆盖模式、远端该模型 `pricing: null` → 本地价格保留，`pricing_deleted` 为 0。
10. 两种模式都不会删除远端缺失的本地模型（Q21-1 / Q4）。
11. 对照组：走「导入本地 json」的同一份文档仍是旧语义（更新字段、回写 `Enabled`、`pricing: null` 会删价格、`source = manual`），确认新选项没有污染既有路径。
12. 地址非 http/https、`mode` 非法 → 400。
13. 404、超时、响应超 5 MB、非法 JSON、`version=2`、带 BOM 的合法 JSON（应成功）→ 行为符合预期，失败时库无变化。
14. 写入过程抛异常 → 事务回滚，供应商与模型均无残留。
15. 非超管调用 → 403。
16. 回归：导入文件缺某个 provider 条目 → 400 可读报错（原为 500）。

前端（`node:test`）：状态机 `origin=sync|overwrite` 分支、无新模型分支不进 `preview`、覆盖模式未勾选时确认按钮禁用、错误分支保留 `errors[0]` 且可 `reset`。

手工：本地 `python3 -m http.server` 托一份 JSON 当源，跑通增量与覆盖两条路径；再把 JSON 改坏一次确认不落库；移动端视口检查下拉按钮布局。

## 7. 已知边界与暂不覆盖

- 覆盖模式**不可撤销**：本地对同名模型的名称、描述、匹配规则、能力、价格与 `source` 被远端取代（启用状态、供应商、本地独有模型不受影响）。对话框提供「先导出当前目录」作为唯一兜底。
- 增量模式下远端对存量模型的改价不会下发，必须显式走覆盖模式。
- 两种模式都不清理远端已删除的模型（Q21-1 / Q4）。
- 覆盖模式也不会清空价格：远端 `pricing: null` 时保留本地价格，因此「远端有意把某模型改为不计费」这件事同步不下来，只能人工处理。
- 若远端把同一 `model_key` 挪到别的供应商：增量模式跳过；覆盖模式会改写该模型的 `ProviderId`（若目标供应商本地不存在则新建），但不会修改任何已存在供应商自身的字段。
- 无同步历史、无上次同步时间（Q7 不建表的直接后果）；排障只能看 warning 日志。
- 失败日志按 Q23 不截断，源站返回大体积错误页时单条日志可达 5 MB；靠 docker `json-file` 轮转（`max-size` 50m、`max-file` 5）兜底。
- 不做定时同步、私有源鉴权、签名校验（Q8）。
- 不同步渠道级覆盖（`ChannelModelInfo`），与 PRD 14.4 现有边界一致。
- 多实例并发同步无分布式锁；单事务保证不出半写状态，但两个超管同时点会各自完成一次写入尝试。
- 60 秒拉取期间管理员关页面：预检阶段无副作用；确认阶段若事务已提交则变更生效，前端看不到结果，刷新即可见。
- 不改 `wwwroot/ocxp_codex_official_models.json`（客户端能力清单是另一条链路）。

## 8. 实施前的最后两处推定

决策已全部落定，无待答问题。下列两条是我从 Q21 的答案外推出来的，实现将按此进行，如与你本意不符请指出：

1. 启用状态在两种同步模式下都以本地为准，双向都不改写。
2. 远端 `pricing: null` 时保留本地价格，同步永不删除价格。

另外把下拉第三项文案从「强制全量覆盖」改为「覆盖已有模型」，因为它既不删本地独有模型、也不动供应商与启用状态，旧名字会让人预期一次破坏性更大的操作。
