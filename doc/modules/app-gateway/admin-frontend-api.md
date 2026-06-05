# 管理台前端字段/API 对照

## 模块名称

管理台前端字段/API 对照。

## 模块职责

记录当前 Vue 管理台页面与 Flask 后端管理 API 的实际对应关系，便于修改页面字段、接口参数或后端返回结构时同步检查。

## 输入

- 前端组件：`frontend/src/App.vue`、`Dashboard.vue`、`Channels.vue`、`AccessKeys.vue`、`Users.vue`、`WebSearch.vue`、`Logs.vue`、`Login.vue`。
- 后端接口：`opencodex_proxy/app.py` 中 `/admin/api/*` 路由。

## 输出

- 页面到 API 的调用表。
- 前端字段到后端字段的映射。
- 当前实现差异和维护注意事项。

## 依赖模块

- 应用网关与代理入口。
- 认证与访问控制。
- 配置管理与路由。
- Web Search 模拟。
- 持久化、日志与统计。

## 页面与 API 总览

| 页面/组件 | 主要 API | 说明 |
| --- | --- | --- |
| `App.vue` | `GET /admin/api/session`, `POST /admin/api/logout` | 检查会话、退出登录、根据角色控制菜单 |
| `Login.vue` | `POST /admin/api/login` | 管理台登录 |
| `Dashboard.vue` | `GET /admin/api/stats` | 仪表盘统计图表 |
| `Channels.vue` | `GET /admin/api/config`, `POST /admin/api/config`, `POST /admin/api/test-channel` | 渠道列表、编辑、测试 |
| `AccessKeys.vue` | `GET/POST/PATCH/DELETE /admin/api/api-keys` | 代理访问 API Key 管理 |
| `Users.vue` | `GET/POST/PATCH/DELETE /admin/api/users` | 用户管理，超级管理员可见 |
| `WebSearch.vue` | `GET/POST /admin/api/web-search`, `POST /admin/api/web-search/test-key` | Web Search Key 配置与测试 |
| `Logs.vue` | `GET /admin/api/logs`, `GET /admin/api/logs/<id>`, `GET /admin/api/log-filter-options` | 请求日志列表、详情、筛选项 |

## 登录与会话

| 前端字段/行为 | 后端接口 | 请求字段 | 响应字段 |
| --- | --- | --- | --- |
| 登录表单 | `POST /admin/api/login` | `username`, `password` | `authenticated`, `user.username`, `user.role`, `user.enabled` |
| 初始化会话 | `GET /admin/api/session` | 无 | `authenticated`, `user` |
| 退出登录 | `POST /admin/api/logout` | 空 JSON 字符串 | `authenticated=false` |
| 菜单权限 | 无直接接口 | 使用 `user.role` | `users`、`web-search` 仅超级管理员显示 |

## 仪表盘

### 请求

`Dashboard.vue` 调用：

```http
GET /admin/api/stats?range=1h
GET /admin/api/stats?range=custom&start=...&end=...
```

| 前端字段 | 后端参数 | 说明 |
| --- | --- | --- |
| `range` | `range` | `1h`、`6h`、`24h`、`7d`、`30d`、`custom` |
| `customRange[0]` | `start` | 前端毫秒时间戳转秒 |
| `customRange[1]` | `end` | 前端毫秒时间戳转秒 |

### 响应字段

| 后端字段 | 前端用途 |
| --- | --- |
| `range` | 当前时间范围 |
| `start`, `end` | 时间范围展示与图表标签 |
| `granularity_minutes` | RPM/TPM 计算参考 |
| `currency_rate` | 人民币和美元换算 |
| `summary.request_count` | 总请求数 |
| `summary.success_count` | 成功请求数 |
| `summary.recent_1h_request_count` | 近 1 小时请求 |
| `summary.input_tokens`, `cached_tokens`, `output_tokens`, `total_tokens` | Token 卡片和趋势 |
| `summary.cost`, `recent_1h_cost` | 成本卡片 |
| `summary.rpm`, `summary.tpm` | RPM/TPM 卡片 |
| `points[]` | 消费、Token、TTFT、缓存命中、RPM 折线图 |
| `model_distribution[]` | 模型分布饼图 |

## 渠道配置

### 列表与保存

| 前端行为 | 后端接口 | 请求/响应 |
| --- | --- | --- |
| 加载渠道 | `GET /admin/api/config` | 返回 `channels` |
| 保存渠道 | `POST /admin/api/config` | 请求 `{channels: [...]}` |
| 导入配置 | 前端读取本地 JSON 后调用 `POST /admin/api/config` | 当前前端为覆盖保存 |
| 导出配置 | 前端本地生成 JSON | 未调用后端 `GET /admin/api/config/export` |

### 渠道字段

| 前端字段 | 后端字段 | 类型/格式 | 说明 |
| --- | --- | --- | --- |
| `channelDraft.id` | `id` | string | 新增后可编辑，编辑时禁用 |
| `channelDraft.name` | `name` | string | 显示名称 |
| `channelDraft.type` | `type` | `responses`/`chat`/`messages` | 上游协议 |
| `channelDraft.baseurl` | `baseurl` | URL string | 必须为 http(s) |
| `channelDraft.apikey` | `apikey` | string | 上游 Key |
| `channelDraft.auth_mode` | `auth_mode` | `config`/`none` | 鉴权模式 |
| `headersText` | `headers` | JSON object | 自定义 headers |
| `timeout_seconds` | `timeout_seconds` | positive integer | 超时秒数 |
| `retry_count` | `retry_count` | non-negative integer | 重试次数 |
| `enabled` | `enabled` | boolean | 是否启用 |
| `models[].model` | `models[].model` | string | 客户端请求模型 |
| `models[].upstream_model` | `models[].upstream_model` | string | 上游模型 |

### compat 字段

| 前端字段 | 后端字段 | 前端输入格式 | 后端格式 |
| --- | --- | --- | --- |
| `compatTexts.rename_params` | `compat.rename_params` | 每行 `from=to` | object |
| `compatTexts.drop_params` | `compat.drop_params` | 每行一个参数 | array |
| `compatTexts.force_params` | `compat.force_params` | 每行 `key=value`，value 尝试 JSON parse | object |
| `compatTexts.default_params` | `compat.default_params` | 每行 `key=value`，value 尝试 JSON parse | object |
| `compatTexts.unsupported_params` | `compat.unsupported_params` | 每行一个参数 | array |
| `compatDraft.fallback_thinking_on_tool_use` | `compat.fallback_thinking_on_tool_use` | switch | boolean，false 时前端会省略字段 |

### 渠道测试

前端调用：

```http
POST /admin/api/test-channel
```

请求体为扁平结构，既包含渠道字段，也包含测试 payload 字段：

| 字段 | 说明 |
| --- | --- |
| 渠道字段 | `id`, `name`, `type`, `baseurl`, `apikey`, `auth_mode`, `headers`, `timeout_seconds`, `retry_count`, `compat`, `models`, `enabled` |
| `model` | 测试模型，后端会用模型映射得到上游模型 |
| `input` | 测试提示词 |
| `max_output_tokens` | 测试输出上限 |

后端会根据渠道类型构造：

- Chat：`messages=[{role:"user",content:input}]`, `max_tokens=max_output_tokens`
- Messages：`messages=[{role:"user",content:input}]`, `max_tokens=max_output_tokens`
- Responses：`input=input`, `max_output_tokens=max_output_tokens`

### 当前实现差异

后端存在模型发现接口：

```http
POST /admin/api/channels/discover-models
POST /admin/api/discover-models
```

但 `Channels.vue` 中“发现模型”当前实际调用的是：

```http
POST /admin/api/test-channel
```

并尝试读取 `data.models`。而 `test-channel` 的正常响应字段是 `ok`、`duration_ms`、`model`、`upstream_model`、`compat`、`response`，不返回 `models`。这属于当前前后端实现差异，后续若修复应改为调用模型发现接口或调整后端响应。

## API Key 管理

| 前端行为 | 后端接口 | 请求字段 | 响应字段 |
| --- | --- | --- | --- |
| 加载 Key | `GET /admin/api/api-keys` | 无 | `keys[]` |
| 创建 Key | `POST /admin/api/api-keys` | `name`，超级管理员可传 `owner_username` | `key`，创建时含明文 |
| 启停 Key | `PATCH /admin/api/api-keys/<id>` | `enabled` | `key` |
| 删除 Key | `DELETE /admin/api/api-keys/<id>` | 无 | `deleted` |

列表使用字段：

| 后端字段 | 前端用途 |
| --- | --- |
| `owner_username` | 超级管理员可见的归属用户 |
| `name` | Key 名称 |
| `masked_key` | 列表展示 |
| `key` | 可复制明文，旧 Key 可能为空 |
| `last_used_at` | 最近使用时间 |
| `enabled` | 启用状态 |

## 用户管理

| 前端行为 | 后端接口 | 请求字段 | 响应字段 |
| --- | --- | --- | --- |
| 加载用户 | `GET /admin/api/users` | 无 | `users[]` |
| 创建用户 | `POST /admin/api/users` | `username`, `password`, `enabled` | `user` |
| 启停用户 | `PATCH /admin/api/users/<username>` | `enabled` | `user` |
| 重置密码 | `PATCH /admin/api/users/<username>` | `password` | `user` |
| 删除用户 | `DELETE /admin/api/users/<username>` | 无 | `deleted`, `user` |

前端限制：

- 超级管理员用户不能在前端重置密码。
- 当前登录用户不能在前端删除自己。
- 用户管理菜单仅超级管理员可见。

## Web Search 配置

| 前端行为 | 后端接口 | 请求字段 | 响应字段 |
| --- | --- | --- | --- |
| 加载配置 | `GET /admin/api/web-search` | 无 | `enabled`, `providers`, `default_key_usage_limit`, `keys` |
| 保存配置 | `POST /admin/api/web-search` | `enabled`, `keys[]` | 保存后的配置 |
| 测试 Key | `POST /admin/api/web-search/test-key` | `id` | `ok`, `duration_ms`, `key`, `result`, `config` |

Key 字段：

| 前端字段 | 后端字段 | 说明 |
| --- | --- | --- |
| `id` | `id` | 已保存 Key 的数据库 ID，新 Key 为 null |
| `provider` | `provider` | 当前为 `tavily` |
| `key` | `key` / `api_key` | 前端归一化为 `key`，提交时使用 `key` |
| `enabled` | `enabled` | 是否启用 |
| `usage_count` | `usage_count` | 已用次数 |
| `usage_limit` | `usage_limit` | 单 Key 使用上限 |
| `key_usage_limit` | 兼容字段 | 前端展示和归一化时兼容 |

## 日志页面

### 列表查询

`Logs.vue` 调用：

```http
GET /admin/api/logs?page=1&page_size=20&...
```

筛选字段：

| 前端筛选 | 后端参数 | 说明 |
| --- | --- | --- |
| 请求 ID | `request_id` | 自动补全 |
| 模型 | `model` | 自动补全 |
| 渠道 | `channel_id` | 远程选项 |
| 路径 | `path` | 远程选项 |
| 状态 | `request_status` | `success` 或 `failed` |
| 状态码 | `status_code` | 远程选项 |
| 用户 | `owner_username` | 仅超级管理员 |
| Key ID | `api_key_id` | 远程选项 |

列表字段：

| 后端字段 | 前端列 |
| --- | --- |
| `created_at` | 时间 |
| `request_id` | 请求 |
| `request_status` | 状态 |
| `owner_username` | 用户 |
| `api_key_id` | Key ID |
| `model` | 模型 |
| `channel_id` | 渠道 |
| `status_code` | 状态码 |
| `duration_ms` | 耗时 |
| `ttft_ms` | TTFT |
| `input_tokens`, `cached_tokens`, `output_tokens` | Token |
| `cost` | 成本 |

### 筛选候选项

```http
GET /admin/api/log-filter-options?field=model&q=...
```

前端字段到响应数组：

| `field` | 响应字段 |
| --- | --- |
| `request_id` | `request_ids` |
| `model` | `models` |
| `channel_id` | `channel_ids` |
| `owner_username` | `owner_usernames` |
| `api_key_id` | `api_key_ids` |
| `path` | `paths` |
| `status_code` | `status_codes` |

### 日志详情

```http
GET /admin/api/logs/<id>
```

前端展示 tabs：

| 后端字段 | 前端标签 |
| --- | --- |
| `request_headers` | 请求头 |
| `request_body` | 原始请求 |
| `upstream_request_body` | 转换后请求 |
| `upstream_response_body` | 转换前响应 |
| `response_body` | 转换后响应 |
| `web_search_json` | Web Search |

## 异常处理

| 场景 | 前端处理 | 后端处理 |
| --- | --- | --- |
| API 返回非 2xx | `App.vue` 的 `api` helper 抛出 `Error` | 返回 JSON error 或文本 |
| JSON 输入非法 | 前端在 headers/compat/Web Search Key 处先校验 | 后端再次校验并返回 400 |
| 普通用户访问超级管理员页面 | 菜单隐藏并切回 dashboard | 后端仍会返回 403 |
| 日志详情并发切换 | 使用 request token 忽略过期响应 | 后端无特殊状态 |

## 备注

- 前端 API helper 默认带 `Content-Type: application/json`。
- 管理台导入配置当前是前端覆盖保存，不使用后端导入合并接口 `/admin/api/config/import`。
- 成本展示中日志页使用固定 `7.3` 换算美元，仪表盘使用后端返回的 `currency_rate`。
- 若修改后端字段名，应同步检查对应 Vue 组件中的字段读取和格式化函数。

