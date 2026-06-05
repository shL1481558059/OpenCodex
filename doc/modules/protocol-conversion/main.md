# 协议转换模块

## 模块名称

协议转换。

## 模块职责

在 Responses、Chat Completions 和 Anthropic Messages 三种协议之间转换请求和响应。模块内部通过 canonical 中间结构统一表达消息、工具、工具选择、参数和用量，再输出目标协议格式。

## 输入

- 来源协议请求或响应 JSON。
- 来源协议名称：`responses`、`chat` 或 `messages`。
- 目标协议名称。
- 原始模型名或上游模型名。

## 输出

- 目标协议格式的请求 JSON。
- 入口协议格式的响应 JSON。
- 规范化后的工具调用、工具结果、用量字段。
- 对非法输入抛出的 `BadRequestError`。

## 依赖模块

- `protocols.py`：核心协议转换。
- `patch_semantics.py`：apply_patch 语义事件辅助。
- `errors.py`：请求错误。
- `streaming.py`：流式转换会复用部分协议工具函数。

## 核心逻辑

- 逻辑步骤 1：`convert_request` 深拷贝入参并把 `model` 替换为上游模型。
- 逻辑步骤 2：如果来源协议和目标协议相同，直接返回替换模型后的请求。
- 逻辑步骤 3：如果协议不同，先调用 `to_canonical_request` 把来源请求转成 canonical。
- 逻辑步骤 4：再调用 `from_canonical_request` 把 canonical 转成目标协议请求。
- 逻辑步骤 5：响应转换使用相反方向：先将上游响应转 canonical，再转回入口协议响应。
- 逻辑步骤 6：Responses 输入支持字符串和列表；系统指令会合并到 system message。
- 逻辑步骤 7：Chat 与 Messages 的工具调用会转为 canonical tool，再输出目标协议的 tool 定义。
- 逻辑步骤 8：apply_patch 相关工具会被扩展成更结构化的代理工具，并在响应中重建为 Responses 可识别的工具调用项。
- 逻辑步骤 9：Token 用量字段按协议转换为入口协议期望格式。

## 数据结构 / 数据库表

该模块不直接使用数据库。

### Canonical Request

| 字段 | 类型 | 用途 |
| --- | --- | --- |
| `model` | string | 模型名 |
| `messages` | array | 统一后的消息列表 |
| `tools` | array | 统一后的工具列表 |
| `tool_choice` | any | 工具选择策略 |
| `params` | object | 协议通用参数 |

### Canonical Response

| 字段 | 类型 | 用途 |
| --- | --- | --- |
| `id` | string | 响应 ID |
| `model` | string | 模型名 |
| `messages` | array | 输出消息 |
| `tool_calls` | array | 工具调用 |
| `reasoning` | array/string | 推理内容 |
| `usage` | object | Token 用量 |
| `finish_reason` | string | 结束原因 |

## 外部接口 / API

| 函数 | 参数 | 返回值 | 异常 |
| --- | --- | --- | --- |
| `convert_request` | `payload`, `source_protocol`, `target_protocol`, `upstream_model` | 目标协议请求体 | `BadRequestError` |
| `convert_response` | `payload`, `source_protocol`, `target_protocol`, `original_model` | 入口协议响应体 | `BadRequestError` |
| `to_canonical_request` | `payload`, `protocol` | canonical 请求 | `BadRequestError` |
| `from_canonical_request` | `canonical`, `protocol` | 指定协议请求 | `BadRequestError` |
| `to_canonical_response` | `payload`, `protocol`, `original_model` | canonical 响应 | `BadRequestError` |
| `from_canonical_response` | `canonical`, `protocol` | 指定协议响应 | `BadRequestError` |

## 异常处理

| 异常类型 | 触发条件 | 处理方式 |
| --- | --- | --- |
| `BadRequestError` | 不支持的协议名 | 代理入口返回 400 |
| `BadRequestError` | Responses `input` 既不是字符串也不是列表 | 返回 400 |
| 数据格式不完整 | 历史工具消息缺失、孤儿工具结果、空内容块 | 尽量清理、补齐或跳过，避免上游拒绝 |

## 流程图 / UML

```mermaid
flowchart LR
    A["来源协议 Payload"] --> B["to_canonical_*"]
    B --> C["Canonical 结构"]
    C --> D["from_canonical_*"]
    D --> E["目标协议 Payload"]
```

## 备注

- 协议转换是项目兼容不同上游的关键模块，测试覆盖集中在 `tests/test_protocols.py`。
- 该模块包含大量 apply_patch 兼容逻辑，修改时需要补充细粒度回归测试。
- Responses Plan Mode 标签会触发额外 system instruction 注入，以保证客户端识别计划输出。

