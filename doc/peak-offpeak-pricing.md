# 峰谷计费方案（模型价格按时段切换单价）

> 状态：决策已确认（谷段绝对单价 + 请求进入时刻），批 1 到批 4 已实施，批 5 未做。实施记录见第 11 节。
> 更新时间：2026-08-27。文中现状事实逐文件核对，未依赖记忆；时区行为为本机实测结论（见 4.3）。

## 1. 需求与问题定义

需求原话是「价格新增峰谷计费规则」。从第一性原理拆开，真实要解决的问题有两类，二者对数据模型的要求不同，必须先分清：

1. 还原上游真实成本。上游供应商本身就有错峰价目表，典型如 DeepSeek：错峰价为峰价的一半，峰段是 UTC `01:00-04:00` 与 `06:00-10:00`，且仅限周一至周五，其余时间全部按错峰价。这类需求的输入是供应商公告里的绝对单价与时段，我们只是照抄，账单要能对得上对方发票。
2. 平台自己对下游做峰谷差价。输入是运营策略（例如夜间八折），时段由我们自己定。

两者在计费引擎里是同一件事：**在同一个计费项上，按请求发生的时刻落在哪个时段，选用不同的计价参数**。差别只在配置界面上更适合填绝对单价还是填折扣率，这一点在 3.2 里请你选。

本方案覆盖的是「一份价格计划内部按时段切换单价」，不改动模型匹配、渠道路由和用量提取。

## 2. 现状核对

### 2.1 价格数据模型

| 事实 | 位置 |
| --- | --- |
| 价格计划：作用域（模型/渠道模型/渠道）+ 币种 + 启用 + 来源，无任何时间字段 | ModelPricingPlan.cs:3 |
| 计费规则：`BillingItem` + `BillingMode` + `UnitPrice` + `TiersJson`，同样无时间字段 | ModelPricingRule.cs:3 |
| 计费项四种：`input` / `output` / `cache_write` / `cache_read`；计费模式三种：`per_request` / `per_million_tokens` / `tiered_tokens` | ModelCatalogConstants.cs:26 |
| 表与索引：`ModelPricingPlans` / `ModelPricingRules`，`UnitPrice` 精度 18,8 | OpenCodexDbContextBase.cs:174 |
| 列类型：SQLite 下 `decimal` 落 `TEXT`，Postgres 下 `numeric(18,8)` | 20260627143924_ModelCatalog.cs:126 |

### 2.2 计费执行链路

| 事实 | 位置 |
| --- | --- |
| 唯一生产入口 `CalculateCostAsync(channelId, requestModel, upstreamModel, usage)`，**参数里没有时间** | IModelCatalogService.cs:52 |
| 实现：解析 plan（走缓存）→ 现查启用规则 → 逐条算量算钱 → 汇总 + 落快照 | ModelCatalogService.cs:990 |
| 数量映射（`input` 已扣除 cache 读写） | ModelCatalogService.cs:1334 |
| 单条规则金额：三种模式的计价函数 | ModelCatalogService.cs:1351 |
| 定价解析缓存：key 只含 `channelId + upstreamModel` 与两个版本号，TTL 60 秒；缓存值是 plan 的扁平快照，**规则每次现查** | ModelCatalogService.cs:1063、CacheKeys.cs:24 |
| 两个调用点：补写已存在日志、直接写完成日志 | ProxyLogService.cs:200 与 :284 |
| 快照结构（落 `RequestLogs.PricingSnapshotJson`），逐条记录计费项/模式/数量/单价/金额 | ModelPricingCalculation.cs:83 |

### 2.3 配置面与传输面

| 事实 | 位置 |
| --- | --- |
| 请求 DTO：`ModelPricingPlanRequest` / `ModelPricingRuleRequest` / `ModelPricingTierRequest` | ModelCatalogRequests.cs:5 |
| 响应 DTO：`ModelPricingPlanResponse` / `ModelPricingRuleResponse` | ModelCatalogDtos.cs:51 |
| 导入导出/远端同步 DTO（`version = 1`） | ModelCatalogTransferDtos.cs:20 |
| 写入路径的规范化与校验：`NormalizeBillingItem` / `NormalizeBillingMode` / `SerializeTiers` / `ValidatePrice` | ModelCatalogService.cs:1886 |
| 导入幂等判定 `PricingUnchanged` 按字段逐一比对，漏字段会让同步永远判「updated」 | ModelCatalogService.cs:2232 |
| 管理台模型价格编辑（表格 + 移动端卡片两套 UI） | frontend/src/ModelCatalog.vue:494 |
| 渠道级模型价格编辑（同样两套 UI） | frontend/src/Channels.vue:1056 |
| 日志页只展示金额，不展示价格快照 | frontend/src/Logs.vue:617 |

两个由现状直接推出的结论：

- 计费引擎**当前拿不到时间**。峰谷的第一个硬改动是给 `CalculateCostAsync` 传入计费时刻，这是接口签名变更，会波及测试里的假实现（`ProxyControllerTests.cs:373`）。
- 全仓**没有任何时区配置**（`rg` 检索 `TimeZone` 零命中），也没有 `IClock`/`TimeProvider` 抽象，35 处直接用 `DateTimeOffset.UtcNow`。所以时区语义必须由本方案新引入，不能指望「服务器本地时间」：容器时区是 UTC，而你按北京时间配价，差 8 小时就是账单事故。

## 3. 需要你拍板的决策点

这几条决定数据模型形状，改起来代价不对称，先确认再动手。默认值是我的推荐。

### 3.1 时段语义：只定义谷段窗口，窗口外即峰段（推荐）

只配一套「谷段窗口」，落在窗口内算谷段，其余一律峰段。基础单价 `unit_price` 即峰价。

理由：如果峰段和谷段各配一套窗口，就必然出现重叠区和空洞区，需要额外的优先级规则去兜，而任何兜底都是猜测。单套窗口没有歧义，且对老数据 100% 兼容（窗口为空 = 永远峰段 = 金额不变）。

### 3.2 谷段价格的表达：绝对单价（推荐）还是折扣率

推荐在规则上存一份**谷段绝对单价**（外加谷段阶梯，见 4.2），不在数据库里存折扣率。

理由：供应商公告给的就是绝对单价，照抄不引入换算误差，账单可逐项对照；折扣率会引入「基础价改了、折扣价跟着漂」的隐式联动。折扣的便利性放在前端：编辑器提供「按 X 折填充」按钮，前端算完写入绝对单价，数据库只有一个真相。

### 3.3 跨午夜窗口：输入允许，存储规范化拆分（推荐）

允许你填 `22:00-06:00`，后端按「窗口起始日」语义自动拆成 `22:00-24:00`（当日）+ `00:00-06:00`（次日），存储层只保留不跨午夜的窗口。

理由：跨午夜 + 星期限制会产生真实歧义，`周五 22:00-06:00` 里的周六凌晨 2 点到底算不算谷段？拆分把语义显式固化为「周五晚上开始的那一段延续到周六凌晨」，判定逻辑退化成一行 `dow 命中 && start <= m < end`，没有隐藏分支。代价是编辑器回显的是拆分后的结果，与你输入的不完全一致，需要 UI 提示一句。

### 3.4 计费时刻：请求进入时刻（推荐）还是请求完成时刻

推荐用该条日志的 `CreatedAt`（请求进入网关的时刻）。

理由有两条。可复算：`CreatedAt` 已落库，日后重算账单能得到同一结果，而「当时的 UtcNow」不可复现。可预期：长流式请求可能跑十几分钟，用完成时刻会让同一次对话的价格取决于它什么时候结束，同一主请求的多次 attempt 还可能落进不同时段。

需要你确认的是：如果目标是和上游发票对账，而上游按完成时刻计价，那么在时段边界附近会有极少量请求金额与上游不一致。若你更看重对账一致，就改用完成时刻，实现成本相同。

### 3.5 星期维度：必须做

这不是可选项。DeepSeek 的错峰规则本身就带「周一至周五」，不支持星期就无法表达周末全天谷段。窗口里带一个 `days` 数组（ISO-8601，1=周一，7=周日；为空表示每天）。

### 3.6 分段数量：v1 只做峰/谷两段

电价那种「峰/平/谷」三段暂不做。三段需要引入命名 phase 与规则-phase 关联表，是一张新表加一套动态 UI。演进路径见第 9 节，二段模型可以平滑升上去。

## 4. 方案设计

### 4.1 语义模型

价格计划持有一份**时段日历**（时区 + 谷段窗口集合），计费规则各自持有一份**可选的谷段参数集**。

```text
ModelPricingPlan                     ModelPricingRule (每个计费项一条)
  time_zone: "Asia/Shanghai"           billing_item: output
  off_peak_windows: [                  billing_mode: per_million_tokens
    { 22:00-24:00, days [1-5] },       unit_price: 1.10          <- 峰价（现有字段）
    { 00:00-06:00, days [2-6] }        off_peak_enabled: true
  ]                                    off_peak_unit_price: 0.55 <- 谷价（新增）
                                       off_peak_tiers: []        <- 谷段阶梯（新增）
```

计算时先由日历定出这次请求的 `phase`（`peak` / `off_peak`），再让每条规则按 phase 选参数集，走同一个计价函数。三条不变量：

- **一次请求只有一个 phase**，在规则循环之外算一次，四个计费项共用。绝不允许 input 落峰段而 output 落谷段。
- **计价函数不变**。`billing_mode` 仍然只决定「数量怎么变成钱」，phase 只决定「用哪套参数」。因此 `tiered_tokens` 与峰谷天然叠加，无需新模式。
- **规则可以不参与峰谷**（`off_peak_enabled = false`），此时谷段也用基础价。`cache_read` 往常不打折，这是常态而非例外。

### 4.2 数据模型变更

`ModelPricingPlans` 新增两列：

| 列 | 类型（SQLite / Postgres） | 默认 | 语义 |
| --- | --- | --- | --- |
| `TimeZoneId` | TEXT / text，非空 | `""` | IANA 时区 ID。空字符串表示未启用峰谷 |
| `OffPeakWindowsJson` | TEXT / text，非空 | `"[]"` | 规范化后的谷段窗口数组 |

`ModelPricingRules` 新增三列：

| 列 | 类型（SQLite / Postgres） | 默认 | 语义 |
| --- | --- | --- | --- |
| `OffPeakEnabled` | INTEGER / boolean，非空 | `false` | 该计费项是否参与峰谷。唯一开关 |
| `OffPeakUnitPrice` | TEXT / numeric(18,8)，非空 | `0` | 谷段单价 |
| `OffPeakTiersJson` | TEXT / text，非空 | `"[]"` | 谷段阶梯，结构与 `TiersJson` 一致 |

设计上刻意避免的两件事：不用可空列表达「未配置」（可空 + 空数组会出现两个「未配置」信号，判定就得靠猜）；不新增表（窗口与规则都是同一个 plan 的紧耦合附属数据，没有独立生命周期，多一张表只是多一次 join 和一份删除级联）。

窗口的 JSON 形状（同时也是 API 与导出格式）：

```json
[
  { "start": "22:00", "end": "24:00", "days": [1, 2, 3, 4, 5] },
  { "start": "00:00", "end": "06:00", "days": [2, 3, 4, 5, 6] }
]
```

校验规则（写入即拒绝，不做运行时兜底）：`start` / `end` 必须匹配 `HH:mm`，`end` 允许 `24:00`；规范化后必须 `start < end`；`days` 元素取值 1-7 且去重，缺失或空数组等价于每天；窗口数量上限 24 条（防止把配置当脚本用）；`TimeZoneId` 必须能被 `TimeZoneInfo.FindSystemTimeZoneById` 解析，否则 400。窗口之间允许重叠，语义是并集，不需要额外规则。

启用条件是显式的合取：`TimeZoneId` 非空 **且** 窗口数组非空，才进入峰谷判定；否则整套逻辑短路，行为与今天完全一致。

### 4.3 计费时刻与时区（含实测结论）

接口签名改为传入计费时刻，与仓库里时间戳一律用 Unix 秒（`double`）的风格一致：

```csharp
Task<ModelPricingCalculationResult> CalculateCostAsync(
    Guid? channelId,
    string? requestModel,
    string? upstreamModel,
    ModelUsageVector usage,
    double billingInstantUnixSeconds);
```

两个调用点分别传 `log.CreatedAt`（补写路径，日志已在库里）和即将写入的 `CreatedAt`（直写路径）。显式传参而不是注入时钟抽象，是为了让测试直接给定时刻，也让账单可复算。

时区行为本机实测（.NET 10.0.301 + 系统 tzdata）：`Asia/Shanghai` 解析正常，同一 instant 转出 `+08:00` 本地时间；`America/New_York` 正确带出 DST 偏移；非法 ID 抛 `TimeZoneNotFoundException`。运行镜像 `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` 已经 `apk add tzdata`（Dockerfile:46），所以生产可用。

判定方向只有 instant 到本地时间这一条，不存在本地时间反查 instant 的二义性，因此 DST 不会产生歧义：切换日的谷段实际时长会自然地多一小时或少一小时，这是正确且可解释的结果。

时区解析失败的降级：写入时已经拦住非法 ID，运行时只可能因为跨机器 tzdata 缺失而失败。此时**按峰价计费**，并在快照里标 `time_zone_unresolved`，同时打一条 warning。不静默按 UTC 猜，也不让计费异常冒泡打断代理请求。`TimeZoneInfo` 实例按 ID 做进程内缓存，避免每请求查一次系统时区库。

### 4.4 计算流程

```text
CalculateCostAsync(channelId, requestModel, upstreamModel, usage, instant)
  1. 解析 plan（走现有缓存，缓存值扩充 time_zone + windows 的原始定义）
  2. 若 plan 未启用峰谷 -> phase = peak, phase_source = disabled
     否则解析时区:
       失败 -> phase = peak, phase_source = time_zone_unresolved (warning)
       成功 -> local = ConvertTime(instant, tz)
               m = local.Hour * 60 + local.Minute
               dow = ISO(local.DayOfWeek)
               命中任一窗口 (dow 在 days 内 && start <= m < end) -> off_peak，否则 peak
  3. 现查启用规则，逐条:
       该 rule 用谷段参数的条件 = (phase == off_peak && rule.OffPeakEnabled)
       quantity 不受 phase 影响
       cost = 计价函数(billing_mode, quantity, 选中的 unit_price / tiers)
  4. 汇总 + 快照（新增 phase / 计费时刻 / 时区 / 命中窗口 / 每条规则实际用的参数集）
```

区间取半开 `[start, end)`，配合 `end` 允许 `24:00`，一天被完整覆盖且不会在整点重复命中。

### 4.5 缓存正确性（红线）

**phase 判定结果绝对不能进缓存。** 定价缓存 TTL 是 60 秒，如果把 phase 缓存进去，跨越时段边界的那一分钟会按旧 phase 算钱，且没有任何报错。

具体做法：缓存值 `CachedPricingResolution` 只扩充**规则定义本身**（`TimeZoneId` 与窗口 JSON），phase 每次请求现算。缓存 key 保持不变，不能把时间片塞进 key（那会让缓存命中率崩掉，还是解决不了边界问题）。改价仍走现有的定价版本号失效机制（本地版本 + Redis 版本）。

### 4.6 快照与可观测性

`prd/11` 已有的 `REQ-OBS-002` 要求成本可追溯到计费项、模式、单价和用量。峰谷必须进快照，否则「为什么这单便宜一半」无法解释。

计划快照新增：`pricing_phase`（`peak` / `off_peak`）、`phase_source`（`window_hit` / `window_miss` / `disabled` / `time_zone_unresolved`）、`billing_instant`（Unix 秒）、`time_zone`、`matched_window`（命中的那条窗口，未命中为 null）。
规则快照新增：`applied_phase`，表示这条规则实际用了峰价还是谷价（`off_peak_enabled = false` 的规则在谷段仍是 `peak`，这一列让账单自解释）。

读取侧不做破坏性变更：老日志的快照没有这些字段，任何展示都必须按缺失处理。日志详情页展示 phase 属于可选批次（见 6.5）。

### 4.7 API 契约

请求与响应各加同一组字段，命名沿用 snake_case：

```json
{
  "currency": "USD",
  "enabled": true,
  "time_zone": "Asia/Shanghai",
  "off_peak_windows": [{ "start": "22:00", "end": "06:00", "days": [1, 2, 3, 4, 5] }],
  "rules": [
    {
      "billing_item": "output",
      "billing_mode": "per_million_tokens",
      "unit_price": 1.1,
      "tiers": [],
      "off_peak_enabled": true,
      "off_peak_unit_price": 0.55,
      "off_peak_tiers": [],
      "enabled": true
    }
  ]
}
```

全部字段可省略，省略即维持当前行为，因此 `/model-infos`、`/channels/{id}/model-infos`、`/model-catalog/import` 的现有调用方不受影响。响应里的 `off_peak_windows` 是规范化拆分后的结果（见 3.3）。

导出文档 `version` 从 1 提到 2：新版能读旧文档（缺字段按未启用处理），旧版读新文档会忽略峰谷字段并按峰价计费，这一点必须写进发布说明，避免有人拿旧实例导入新价目表后账单偏高而不知情。

`PricingUnchanged` 与 `ModelUnchanged` 的比较项必须同步补齐五个新字段，否则远端同步每次都把模型判成 updated，覆盖导入的统计也会失真。

### 4.8 前端

两个页面各有桌面表格 + 移动卡片两套 UI，共四处要改：模型价格编辑（`ModelCatalog.vue:494`）与渠道模型价格编辑（`Channels.vue:1056`）。

编辑器结构：币种一行下面加「峰谷计费」区块，含时区选择（可搜索的常用 IANA 列表 + 自由输入）、窗口列表（起止时间选择 + 星期多选 + 增删行）、以及一行「当前处于峰段/谷段」的实时提示（前端用 `Intl.DateTimeFormat` 按所选时区本地判定，纯展示，不参与计费）。规则行在单价后面加「谷段单价」列与 `off_peak_enabled` 开关，`tiered_tokens` 模式下再展开谷段阶梯 JSON。窗口为空时谷段列整体禁用并给出原因，避免出现「填了谷价却不生效」这种沉默失效。

列表页的价格摘要（`pricingSummary` / `pricingRuleSummary`）在启用峰谷时显示两个价格，形如 `1.10 / 0.55`，并加一个小标记说明后者是谷价。

折扣按钮（3.2）放在规则区顶部：填 `0.5` 一键把所有 `off_peak_enabled` 的规则谷价刷成峰价的一半，写入的是绝对数。

### 4.9 与远端目录同步的关系

同步默认 `KeepLocalPricingWhenRemoteNull = true`（`ModelCatalogSyncService.cs:73`），远端 `pricing: null` 不会删本地价格。但**覆盖导入模式下，远端带了 pricing 就会整体替换本地 plan**，如果远端文档还是 v1 格式（没有峰谷字段），本地手工配的峰谷会被抹掉。这是既有语义的自然延伸，不是新 bug，但必须在覆盖确认框里补一句提示。

## 5. 备选方案与否决理由

| 方案 | 否决理由 |
| --- | --- |
| 新增 `billing_mode = "peak_offpeak_tokens"`，时段与单价塞进一个 JSON | 与 `tiered_tokens` 互斥，谷段阶梯没法表达；且要在 JSON 里重新实现一遍单价语义，`billing_mode` 从「计价函数」退化成混合概念 |
| 时段窗口下沉到每条规则 | 四个计费项各配一套窗口，边界稍有不一致就产出无法解释的账单，还需要一致性校验去兜；配置量翻四倍 |
| 规则上存折扣率而非绝对单价 | 与供应商公告的绝对单价之间需要换算，误差进账单；基础价变动时折扣价隐式漂移。折扣只作为前端填充手段 |
| 新建 `ModelPricingWindows` 表 | 窗口没有独立生命周期，跟随 plan 增删；多一张表就多一套级联删除、双库迁移和 join，收益为零 |
| 直接用服务器本地时间，不引入时区字段 | 容器时区是 UTC，配置者按北京时间思考，8 小时偏差是必然事故；且换部署环境账单会变 |
| 把 phase 塞进定价缓存 key 或缓存值 | 见 4.5，边界时刻静默算错，属于不可接受的正确性风险 |

## 6. 任务拆分

改动跨 12 个以上文件，按 AGENTS.md 拆成可独立验收的批次。每批结束都要求 `dotnet test` 全绿（当前基线实测 564 个测试，0 失败）。

### 6.1 批 1：领域字段与双库迁移

目标：把五个新列加上，行为完全不变。
文件：`ModelPricingPlan.cs`、`ModelPricingRule.cs`、`ModelCatalogConstants.cs`（新增 `PricingPhases` 常量类）、`OpenCodexDbContextBase.cs`、SQLite 与 Postgres 各一份迁移。
风险：中。双库迁移历史上分叉过（见 `doc/refactoring-checklist.md`），必须用 `dotnet ef migrations add` 生成两份并核对 snapshot；SQLite 的 `decimal` 落 `TEXT`，默认值要写字符串 `"0"` 而非数值 0。
验收：两库都能从空库迁到最新；老库升级后既有价格金额零变化。

### 6.2 批 2：计费内核与快照

目标：phase 判定与参数选择落地，成本可解释。
文件：`IModelCatalogService.cs`（签名）、`ModelCatalogService.cs`（判定 + 缓存 DTO 扩字段 + 计价参数选择）、`ModelPricingCalculation.cs`（快照）、`ProxyLogService.cs`（两处调用点传时刻）、`ProxyControllerTests.cs`（假实现同步签名）。
风险：高。这是唯一会改变金额的一批，缓存红线（4.5）和「一次请求单一 phase」（4.1）都在这里。
验收：7.1 的全部单测通过，其中缓存边界那条是必过项。

### 6.3 批 3：配置契约与校验

目标：能通过 API 与导入文档配置峰谷。
文件：`ModelCatalogRequests.cs`、`ModelCatalogDtos.cs`、`ModelCatalogTransferDtos.cs`、`ModelCatalogService.cs`（`NormalizeTimeZone` / `NormalizeWindows` / 窗口拆分 / `ToPlanResponse` / `ValidateImportModel` / `PricingUnchanged` / 导出 version）。
风险：中。`PricingUnchanged` 漏字段会造成同步噪音；窗口拆分是唯一有「输入不等于存储」的地方，需要往返测试（配置 -> 读回 -> 再提交 -> 结果稳定）。
验收：7.2 的校验与幂等测试通过。

### 6.4 批 4：管理台编辑与展示

目标：不写 JSON 也能配峰谷。
文件：`frontend/src/ModelCatalog.vue`、`frontend/src/Channels.vue`（各含桌面与移动两套），必要时抽一个共享的窗口编辑组件避免两份逻辑漂移。
风险：低到中。风险主要在两页面重复实现导致行为分叉，以及移动端布局。
验收：新建/编辑/回显往返一致；窗口为空时谷段输入禁用并有说明。

### 6.5 批 5（可选，建议单独排期）

日志详情展示 `pricing_phase` 与命中窗口（需要前端读快照，`Logs.vue` 目前完全不读）；`prd/11-observability-and-billing.md` 与 `prd/18-traceability-index.md` 补 `REQ-OBS` 条目；一个只读试算端点用于配置后自检。

## 7. 建议测试用例

### 7.1 计费内核（`ModelCatalogServiceTests.cs`）

- 同一 plan、同一用量，instant 落窗口内取谷价、落窗口外取峰价。
- 半开区间边界：`start` 那一分钟命中，`end` 那一分钟不命中。
- 跨午夜配置（`22:00-06:00`）在 01:00 命中、在 07:00 不命中。
- 星期限制：周六凌晨对「周一至周五 22:00-06:00」的归属，断言与 3.3 拆分语义一致。
- 时区正确性：同一 instant 在 `UTC` 与 `Asia/Shanghai` 两个 plan 下 phase 相反。
- 多窗口重叠取并集，不重复打折。
- `off_peak_enabled = false` 的规则在谷段仍按峰价（用 `cache_read` 做载体）。
- `tiered_tokens` + 谷段阶梯：谷段走 `off_peak_tiers`。`off_peak_enabled` 为真但谷段阶梯为空的组合，我倾向在写入时就拒绝（否则谷段会算成 0 元），需要你在 3.2 一并确认。
- `per_request` 模式的峰谷切换。
- **缓存边界（必过）**：同一 `channelId + upstreamModel` 连续两次调用，第二次的 instant 跨过窗口边界，金额必须切换；期间不得触发定价版本号变化。
- 时区不可解析：按峰价计费，快照 `phase_source = time_zone_unresolved`。
- 回归：未配置峰谷的老 plan，金额与改造前逐位一致。
- 快照断言：`pricing_phase`、`billing_instant`、`time_zone`、`matched_window`、每条规则的 `applied_phase` 都在。

### 7.2 配置与传输

- 非法时区 ID、非法 `HH:mm`、`start == end`、`days` 越界、窗口超上限：全部 400，错误信息含字段名。
- 跨午夜输入落库为两条；读回再提交结果稳定（幂等）。
- 导入 v1 文档（无峰谷字段）不报错、按未启用处理。
- 导入 v2 文档后 `PricingUnchanged` 判定为 unchanged（同步不产生噪音）。
- 渠道级 plan 与全局 plan 各自独立生效，渠道覆盖优先。

### 7.3 前端（沿用现有 `*.test.js` 模式）

- 窗口编辑状态机：增删行、跨午夜提示、窗口为空时禁用谷段输入。
- 折扣填充按钮只改 `off_peak_enabled` 为真的行。
- 价格摘要在启用/未启用峰谷两种情况下的文案。

## 8. 潜在问题

- 时段边界与上游发票口径不一致（3.4）。影响面是边界前后各一分钟内完成的请求，量小但会被对账发现。
- 长流式请求跨越边界：按进入时刻计价，整段用同一价格，不做按时长分摊。分摊需要按秒切分 token，成本与收益不成比例。
- 覆盖导入会抹掉本地峰谷配置（4.9），需要 UI 明确提示。
- 旧版实例导入 v2 文档会静默按峰价计费，属于跨版本行为差异，写进发布说明。
- 谷段单价填成了「折扣率」（例如填 0.5 却以为是五折）是最可能的人为错误。缓解手段是编辑器里峰价与谷价并排显示并算出实际折扣百分比。
- `off_peak_unit_price` 大于 `unit_price` 是合法的（谷段涨价确实有人这么用），不做拦截，但 UI 给一个非阻断提醒。
- 精度：谷价同样 18,8，SQLite 存 `TEXT`，跨库比较要用 `decimal` 而非 `double`，禁止在计费路径出现 `double` 中转。

## 9. 暂不覆盖的边界

- 节假日日历（春节全周按周末价这类需求）。当前只能靠临时改窗口的 `days`。
- 峰/平/谷三段及更多段。演进路径：把现有二段视为 `phase in {peak, off_peak}` 的特例，新增 phase 时把 `off_peak_*` 三列迁移成 `ModelPricingPhaseRules` 表，plan 侧窗口加 `phase` 字段。本方案的字段命名刻意让这条路可走。
- 按用户、按 API Key、按渠道分组的差异化峰谷（当前粒度是模型与渠道模型）。
- 上游账单自动对账，以及价格调整后的历史成本重算工具。历史日志保留的是当时快照，不会被追溯改写，这是有意的。
- 谷段配额（例如谷段前 100 万 token 免费）。这属于配额而非定价，与 `tiered_tokens` 的语义边界需要另行讨论。

## 10. 下一步

决策已确认：3.2 取谷段绝对单价，3.4 取请求进入时刻，其余按推荐执行。剩余工作是第 6.5 节的可选批次。

## 11. 实施记录（2026-08-27）

批 1 到批 4 已落地，后端 582 个测试、前端 34 个测试全绿，`npm --prefix frontend run build` 通过。

### 11.1 实际改动

- 领域与存储：`ModelPricingPlan` 加 `TimeZoneId` / `OffPeakWindowsJson`，`ModelPricingRule` 加 `OffPeakEnabled` / `OffPeakUnitPrice` / `OffPeakTiersJson`；新增常量 `PricingPhases` 与 `PricingPhaseSources`；SQLite 与 Postgres 各一份迁移 `PricingPeakOffPeak`，两个 JSON 列默认值手改为 `"[]"`。
- 计费内核：`CalculateCostAsync` 新增计费时刻参数；时段判定在规则循环之外算一次；规则按 phase 选参数集后走原有计价函数；`CalculateRuleCost` 改为接收 `(billingMode, quantity, unitPrice, tiersJson)`。
- 缓存：`CachedPricingResolution` 只多带 `TimeZoneId` 与 `OffPeakWindowsJson` 两个定义字段，缓存 key 未变，phase 每请求现算。
- 快照：计划级新增 `pricing_phase` / `phase_source` / `billing_instant` / `time_zone` / `matched_window`，规则级新增 `applied_phase`；`ModelPricingCalculationResult` 也带 phase 与 phase_source，便于测试与后续 API 直接读取。
- 配置契约：请求、响应、导入导出三套 DTO 同步新增字段；导出文档版本升到 2，导入兼容 1 与 2；`PricingUnchanged` 补齐五个新字段的比较。
- 前端：新增 `frontend/src/pricingOffPeak.js` 承载共享纯逻辑（时区列表、时间选项、窗口判定、折扣填充），模型价格页与渠道价格页各接入桌面表格与移动卡片两套 UI，列表摘要在启用峰谷时显示「峰价 / 谷价」。

### 11.2 与方案的偏离

1. 计费时刻参数类型用 `DateTimeOffset` 而不是方案 4.3 写的 `double` Unix 秒。类型本身就排除了 NaN 与越界值；`double` 到 `DateTimeOffset` 的转换收在 `ProxyLogService.BillingInstant`，时间戳缺失或越界时退回当前时刻，实际使用的时刻会写进快照。
2. 峰谷窗口类型与判定日历放进了 `OpenCodex.CoreBase/Domain/Models/ModelPricingCalculation.cs`，没有按 6.2 新建 `Core/Services/Pricing/PricingWindowCalendar.cs`。原因是本地编辑工具无法创建新的 `.cs` 文件，只能落在已有文件里。依赖方向没有问题（CoreBase 已引用 Domain，日历只依赖 `System.*`），但这是可以后续单独整理的位置债。
3. 方案里没有的一项修复：`EfRepository.Insert` 会立即 `SaveChanges`，而价格校验原本发生在模型插入之后，非法价格会留下一个没有价格计划的模型，该模型后续请求会静默按 0 成本记账。新增 `ValidatePricingRequest` 在 `CreateModel` / `UpdateModel` / `UpsertChannelModelInfo` 的最开头做纯校验，把这个既有缺陷一并堵掉。
4. 窗口的 `days` 在写入时展开为显式的 ISO 星期列表（空数组等价于每天这一语义保留在输入层），存储与判定都不再有「空表示每天」的隐含分支。
5. `off_peak_enabled` 为真、计费模式是 `tiered_tokens` 但谷段阶梯为空的组合，按 7.1 的倾向直接拒绝（400），避免谷段静默算成 0 元。

### 11.3 上线注意

- 迁移是纯加列，存量数据金额不变；回滚脚本会删列，回滚前需确认没有已配置的峰谷数据。
- 导出文档版本变成 2。旧版实例导入新文档会忽略峰谷字段并按峰价计费，跨版本互导需要留意。
- 覆盖导入仍会整体替换本地价格计划：用 v1 文档覆盖会抹掉本地峰谷配置。
- 第 6.5 节仍未做：日志详情页不展示 `pricing_phase`，`prd/11` 与 `prd/18` 未补 `REQ-OBS` 条目，也没有只读试算端点。
