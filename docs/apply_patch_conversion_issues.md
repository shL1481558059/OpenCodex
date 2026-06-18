# apply_patch 转换问题清单

> 场景：客户端用 `responses` 协议入站，上游通道为 `chat` 或 `messages`，代理对 `apply_patch` 工具的双向转换。

## 涉及文件

- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/ProtocolConverter.ApplyPatchTools.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/ProtocolConverter.Tools.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/ProtocolConverter.ResponsesInput.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/ProtocolConverter.ToolNames.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/SseStreamConverter.Chat.cs`
- `opencodex_proxy/src/Libraries/OpenCodex.Core/Protocols/SseStreamConverter.Messages.cs`

## 当前结论

现在的正确方向已经明确：**不要拆分 apply_patch，也不要把它重建成 `exec_command` heredoc**。代理应当把 `apply_patch` 当作普通函数工具透传，保留原始工具名和原始参数形状。

当前实现已经收敛到这个方向：

- 请求侧（responses → chat/messages）：`apply_patch` 工具不再展开成 `apply_patch_*`
- 响应侧（chat/messages → responses）：`apply_patch` / `apply_patch_*` 工具调用都按普通 `function_call` 输出，保留原始 `name` 和 `arguments`
- chat 流式通道不再对 apply_patch 特殊 skip，`response.function_call_arguments.delta` 会像普通工具一样实时发出

## 已确认问题

### P0 — Description 提示词自相矛盾（根因）

位置：`ApplyPatchProxyTools`（`ProtocolConverter.ApplyPatchTools.cs:76`）

5 个 proxy 工具的 description = `{原始 apply_patch 描述} {结构化 JSON 说明}`。原始描述来自 Codex 客户端传入的 apply_patch 工具（"Use the `apply_patch` tool to edit files. This is a FREEFORM tool, so do not wrap the patch in JSON. ..."），包含三条互相打架的指引：

- "Use the `apply_patch` tool"（指向不存在的工具名）
- "FREEFORM tool, so do not wrap the patch in JSON"（但 schema 要求 JSON 对象）
- 追加的 "Create one new file with structured JSON"（与 FREEFORM 直接矛盾）

后果：LLM 可能生成纯文本 patch 而非 JSON → `RebuildApplyPatchGrammar` 解析失败 → 回退到 `ApplyPatchInputFromArguments` 当文本处理 → 字段名 `path`/`hunks`/`content` 对不上 → 产出空 patch 或错误 exec_command。

### P1 — chat/messages 流式行为曾经不一致

位置：`SseStreamConverter.Chat.cs`

旧实现里 chat 通道会在流式增量循环中跳过 apply_patch 的 delta，只在流末补 done 事件；messages 通道没有这层 skip。结果是同类工具在两个上游协议上的行为不一致。

这个问题现在已经清理：chat 通道 apply_patch 会和普通工具一样实时输出 `response.function_call_arguments.delta/done`。

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

因为模型没用 apply_patch_* 工具，代理旧的 `RebuildApplyPatchGrammar` / `ApplyPatchExecCommand` 转换逻辑根本没执行。文件修改是通过 exec_command 路径完成的（模型自己拼了 apply_patch heredoc）。

### 系统层面的循环冲突

- 代理把 apply_patch 拆成 apply_patch_*（但描述矛盾）
- 模型不用 apply_patch_*，改用 exec_command 跑 apply_patch
- codex 客户端检测到 exec_command 里的 apply_patch，警告模型"用 apply_patch 工具"
- 但客户端认识的是 `apply_patch`，而代理发给模型的是 `apply_patch_*`
- 模型收到的工具列表里根本没有叫 `apply_patch` 的工具
- 陷入循环，浪费多轮请求（实测 7 轮，每轮 5-11 秒）

### 流式时序实测

代理日志显示 7 轮请求全是 conversion 路径（entry=responses, channel=messages），TTFT 5-10 秒。前几轮 `first_output_text` 为空（模型在 reasoning），最后轮有文本输出。
## 不拆分方案验证（分支 codex/apply-patch-no-split）

在分支 `codex/apply-patch-no-split` 上实现并验证了"不拆分，直接透传"方案。

### 改动（2 个核心文件，6 行）

- `ProtocolConverter.Tools.cs`：`CanonicalToolsToChat` / `CanonicalToolsToAnthropic` 不再调 `ExpandApplyPatchProxyTools`，直接遍历 `tools`
- `ProtocolConverter.ApplyPatchTools.cs`：`ResponsesToolCallItemFromToolCall` 移除 apply_patch 分流，不再转 exec_command

### 实测结果（火山 glm-5.2 / messages 协议）

模型直接用 `apply_patch` 工具（而非 exec_command 拼 heredoc），codex 客户端无警告循环：

1. `exec_command` → `cat app.py` 读取文件
2. `apply_patch` → 第一次因 context 行不匹配失败
3. `apply_patch` → 修正后成功（`"Success. Updated the following files: M .../app.py"`）
4. `exec_command` → 验证修改结果
5. 最终文本回复

轮数从 7 轮降到 4 轮，无三方循环冲突。

### 测试

11 个 apply_patch 测试断言已更新为透传行为（name 保持原样、arguments 为原始 JSON、无 exec_command/heredoc）。随后又补了：

- chat 流式 apply_patch delta 实时输出验证
- 多轮 apply_patch 历史工具结果透传验证（失败一次、成功一次、继续对话）

当前 `OpenCodex.Api.Tests` 已达 `160 passed`。

## 本轮收尾

本轮已经完成：

1. `ProtocolConverter.ApplyPatchTools.cs` 清理为最小实现，只保留：
   - `ResponsesToolCallItemFromToolCall`
   - `NormalizeApplyPatchArguments`
   - `IsJsonObjectString`
2. 删除 chat 流式里 apply_patch 的特殊 skip，让它和普通工具一样实时流式
3. 补了多轮 apply_patch 历史透传测试，确认：
   - `function_call_output` / `tool_result` 不丢
   - `call_id` / `tool_use_id` 关联正确
   - 参数不会再被改写成 `exec_command` / heredoc
