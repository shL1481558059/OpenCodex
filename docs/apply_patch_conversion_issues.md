# apply_patch 转换问题清单

> 场景：客户端用 `responses` 协议入站，上游通道为 `chat` 或 `messages`，代理对 `apply_patch` 工具的双向转换。

## 涉及文件

- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/ProtocolConverter.ApplyPatchTools.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/ProtocolConverter.Tools.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/ProtocolConverter.ResponsesInput.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/ProtocolConverter.ToolNames.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/SseStreamConverter.Chat.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/SseStreamConverter.Messages.cs`

## 转换链路概览

### 请求侧（responses → chat/messages）

1 个 `apply_patch` 工具（`native_type=apply_patch`，或 `name=apply_patch` 且 `type=custom`）在 `CanonicalToolsToChat` / `CanonicalToolsToAnthropic` 中通过 `ExpandApplyPatchProxyTools` 展开成 5 个带结构化 JSON schema 的函数工具：

| proxy 工具名 | schema 要求 |
|---|---|
| `apply_patch_add_file` | `path`, `content` |
| `apply_patch_delete_file` | `path` |
| `apply_patch_update_file` | `path`, `hunks[]`（`op: context/add/remove/eof` + `text`），可选 `move_to` |
| `apply_patch_replace_file` | `path`, `content` |
| `apply_patch_batch` | `operations[]`，每项 `{type, path, ...}` |

### 响应侧（chat/messages → responses）

最终都走 `ResponsesApplyPatchItemFromToolCall`：

1. `ApplyPatchInputFromToolCall` 判断是否是 `apply_patch_*` 前缀
2. `RebuildApplyPatchGrammar` 把结构化 JSON 重建为 apply_patch 文本语法（`*** Begin Patch` / `*** Add File:` / `@@` + `+/-/ ` 行 / `*** End Patch`）
3. `ApplyPatchExecCommand` 包成 heredoc：`apply_patch <<'OPENCODEX_PATCH'\n...\nOPENCODEX_PATCH`
4. emit 一个 `function_call` item，**name 改写为 `exec_command`**，arguments 是 `{"cmd": "apply_patch <<'...'..."}`

## 问题清单

### P0 — Description 提示词自相矛盾（根因）

位置：`ApplyPatchProxyTools`（`ProtocolConverter.ApplyPatchTools.cs:76`）

5 个 proxy 工具的 description = `{原始 apply_patch 描述} {结构化 JSON 说明}`。原始描述来自 Codex 客户端传入的 apply_patch 工具（"Use the `apply_patch` tool to edit files. This is a FREEFORM tool, so do not wrap the patch in JSON. ..."），包含三条互相打架的指引：

- "Use the `apply_patch` tool"（指向不存在的工具名）
- "FREEFORM tool, so do not wrap the patch in JSON"（但 schema 要求 JSON 对象）
- 追加的 "Create one new file with structured JSON"（与 FREEFORM 直接矛盾）

后果：LLM 可能生成纯文本 patch 而非 JSON → `RebuildApplyPatchGrammar` 解析失败 → 回退到 `ApplyPatchInputFromArguments` 当文本处理 → 字段名 `path`/`hunks`/`content` 对不上 → 产出空 patch 或错误 exec_command。

### P1 — messages 通道流式 apply_patch 不一致

位置：`SseStreamConverter.Messages.cs`（`content_block_start` / `input_json_delta` 分支）

chat 通道在流式增量循环里 `IsApplyPatchToolName` skip（`SseStreamConverter.Chat.cs:362`），apply_patch 只在流末发一次 done（name=exec_command）。messages 通道**没有**这个 skip，导致同一工具调用的：

- `output_item.added` name = `apply_patch_update_file`
- `function_call_arguments.delta` = 原始 JSON 片段
- `output_item.done` name = `exec_command`，arguments = apply_patch 文本

added 和 done 的 name/参数都不一致，客户端按 added 匹配 done 或按 delta 累加参数都会错乱。现有测试 `ChatToResponsesEvents_ApplyPatchProxy_EmitsExecCommand`（`ProxyCompatibilityTests.cs:1529`）断言 chat 通道 `DoesNotContain delta` 和 `DoesNotContain apply_patch_update_file`，但 messages 通道没有对应测试覆盖。

### P2 — `PrefixedContentLines` 用 `\n` split，`\r\n` 残留 `\r`

位置：`PrefixedContentLines`（`ProtocolConverter.ApplyPatchTools.cs:386`）

`content.Split('\n')` 后每行末尾可能带 `\r`，生成的 patch 行变成 `+line\r`。add 行的 `+line\r` 会在写入文件时引入 `\r` 污染，或与 remove 行匹配失败。

### P3 — 两个名称识别函数语义分歧

- `IsApplyPatchToolName`（`ProtocolConverter.ApplyPatchTools.cs:9`）：匹配 `apply_patch` / `apply_patch_*` 前缀 / `*/apply_patch` 后缀（用于流式 skip 和 `ResponsesToolCallItemFromToolCall` 分流）
- `IsApplyPatchName`（`ProtocolConverter.ToolNames.cs:81`）：只匹配 `apply_patch` / `*/apply_patch`（用于 canonical 识别）

`apply_patch_add_file` 在前者命中、后者不命中。`IsApplyPatchCanonicalTool` 用的是 `IsApplyPatchName`，所以 proxy 工具名不会被误判为 canonical；但两函数并存容易在后续维护里踩坑。

### P4 — `ApplyPatchPath` 空路径静默丢弃

位置：`ApplyPatchOperationLines`（`ProtocolConverter.ApplyPatchTools.cs:320`）→ `ApplyPatchPath`（`ProtocolConverter.ApplyPatchTools.cs:377`）

path 为空或含换行时返回空字符串，整个 operation 的行被丢弃，不报错。batch 里某个 op path 缺失会被静默吞掉，最终 exec_command 产出不完整 patch，apply_patch 执行时才暴露。

## 待定夺

1. **P0 description**：完全丢弃原始 description 用自洽描述，还是保留中性前缀（如 "Apply file edits."）+ 自洽说明？倾向完全丢弃。

2. **P4 空路径**：抛 `BadRequestException` 还是静默跳过 + 警告日志？抛异常更安全但可能让正常请求因模型偶发错误 path 而失败；静默跳过更宽容但会产出残缺 patch。

3. **P3 名称识别**：要不要动？当前分流看起来是有意的（canonical 识别严格、流式 skip 宽松），合并可能引入回归。

## 实测验证（火山渠道 / glm-5.2 / messages 协议）

通过本地代理 + codex CLI 端到端测试，抓取代理发给火山的实际请求体和历史消息，发现：

### P0 已确认：描述矛盾被原样发给模型

火山收到的 5 个 apply_patch_* 工具描述全部是：
> "Use the `apply_patch` tool to edit files. This is a FREEFORM tool, so do not wrap the patch in JSON. Create one new file..."

工具名是 `apply_patch_add_file`，描述却指向 `apply_patch` 工具，且 FREEFORM 与 schema 要求的 JSON 直接矛盾。

### 关键发现：模型绕过 apply_patch_* 工具，直接用 exec_command

在 P0 描述误导下，火山 glm-5.2 **没有调用任何 apply_patch_* 工具**，而是直接用 `exec_command` 执行 `apply_patch <<'OPENCODEX_PATCH'...` heredoc。

实际历史消息序列：
1. `assistant tool_use exec_command` → `cat app.py` 读取文件
2. `user tool_result` → 文件内容
3. `assistant tool_use exec_command` → `apply_patch <<'OPENCODEX_PATCH'...` 直接拼 heredoc 修改文件
4. `user tool_result` → **`[tool output missing]`**（工具结果丢失）
5. `user text` → **codex 客户端注入警告**：`"Warning: apply_patch was requested via exec_command. Use the apply_patch tool instead of exec_command."`

### 拆分-重建链路实际未被触发

因为模型没用 apply_patch_* 工具，代理的 `RebuildApplyPatchGrammar` / `ApplyPatchExecCommand` 转换逻辑根本没执行。文件修改是通过 exec_command 路径完成的（模型自己拼了 apply_patch heredoc）。

### 系统层面的循环冲突

- 代理把 apply_patch 拆成 apply_patch_*（但描述矛盾）
- 模型不用 apply_patch_*，改用 exec_command 跑 apply_patch
- codex 客户端检测到 exec_command 里的 apply_patch，警告模型"用 apply_patch 工具"
- 但客户端认识的是 `apply_patch`，而代理发给模型的是 `apply_patch_*`
- 模型收到的工具列表里根本没有叫 `apply_patch` 的工具
- 陷入循环，浪费多轮请求（实测 7 轮，每轮 5-11 秒）

### 流式时序实测

代理日志显示 7 轮请求全是 conversion 路径（entry=responses, channel=messages），TTFT 5-10 秒。前几轮 `first_output_text` 为空（模型在 reasoning），最后轮有文本输出。
