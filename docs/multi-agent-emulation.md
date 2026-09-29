# 服务端多代理模拟

## 启用方式与影响范围

Codex 的 `base_url` 保持 `https://你的服务器/v1`，`wire_api` 保持 `responses`。在管理台模型信息中开启 `capabilities.v2_agent_simulation`，服务端按客户端请求的模型名称解析全局模型信息并读取该能力。该开关默认关闭，不需要数据库迁移，也不是某个渠道的局部设置。

未开启的模型继续使用原处理逻辑。开启后，Responses 请求进入服务端多代理运行器；HTTP 请求可使用 `multi_agent.enabled: false` 交回普通 Responses 管线。Chat Completions、Messages 等入口不因此变成多代理入口。改变开关后建议新建任务，避免进行中的任务切换编排方式。

- `POST /v1/responses` 支持 JSON 和 SSE，客户端工具结果在后续请求回传。
- `GET /v1/responses` 支持 WebSocket 升级，使用 `response.create` 创建响应、`response.inject` 投递工具结果；每连接同时仅允许一个活动响应。
- 模型列表沿用 `GET /v1/models`。

## 运行职责

服务端提供 `ocxp_ma_spawn_agent`、`ocxp_ma_send_message`、`ocxp_ma_followup_task`、`ocxp_ma_wait_agent`、`ocxp_ma_interrupt_agent`、`ocxp_ma_list_agents`，保存各代理的上下文、任务、消息与调用关系，由根代理汇总实际产出。

本地命令、文件和其他客户端工具仍由 Codex 执行。服务端为对外工具调用分配独立 `call_id`，将回传结果交给所属代理。Chat 上游将 custom tool 包装为单字段 `input` 时，新运行器解开包装后交给客户端。

每次模型调用创建独立依赖注入作用域，复用原代理流式管线的路由、认证、转换、日志与计量。转换后的文本、推理及工具参数增量到达后，运行器立即转发，统一分配代理归属、item ID、output index 和事件序号；无需等待整轮模型结束。异步回调保留回压，终结响应用于保存完整结果，不重复播放已发出的 delta。客户端工具只有参数完整并登记调用后才发出可执行的 done；内部协作函数不会作为客户端 function call 泄漏。

没有修改已有非流式转换器；复用流式路径也避开了真实验收遇到的 Cline 非流式 `data` 外壳造成空 output 的问题。缺少终结事件或成功终结却无输出时明确报错。custom tool 只解开转换器生成的包装，原始 JSON 工具内容保留。

## 会话与状态

运行按 API key 和会话标识共同隔离，`previous_response_id` 在同一范围内查询。会话标识依次取自 `client_metadata.session_id`、`client_metadata.thread_id`、`session-id` 请求头、`X-OpenCodex-Multi-Agent-Session` 请求头、`prompt_cache_key`。

HTTP 未提供标识时自动生成，并通过 `X-OpenCodex-Multi-Agent-Session` 响应头返回，后续请求须复用。WebSocket 客户端需要断线续接时应主动提供标识；连接内沿用首次创建响应的会话。

一个会话固定模型；改变模型或开始独立任务时使用新会话。续接请求可动态更新 tools、instructions 和 input 中的 system/developer 指令快照，省略的配置沿用已有值，显式空工具集用于清空客户端工具。更新从下一轮模型请求生效，已发出的模型请求保留启动时快照，已登记的工具调用仍可接收结果。同一运行串行接收请求，内部代理模型调用按并发额度调度。

- `store: false`：新运行状态仅保存在当前进程内存，不生成多代理 JSON 快照。原有请求日志仍遵循原日志策略，该参数不代表关闭全部日志。重启后应新建任务。
- `store: true` 或未关闭保存：默认写入 `logs/multi-agent-runs` 的 JSON 快照，通过 `MultiAgent:StateDirectory` 或 `MultiAgent__StateDirectory` 修改目录。
- 已保存运行的恢复要求保留目录，复用 API key 与会话标识。重启不主动调用模型，下一次请求才恢复；重启时正在进行的模型回合可能重新执行。
- 后续请求改用 `store: false` 不会删除既有快照。需要整个任务仅在内存中保存时，从新任务首请求开始使用该选项。
- 新状态格式显式保存任务回合边界。旧快照缺少精确回合记录时，不根据 user 消息、工具输出或 mailbox 猜测回合；需要严格 fork N 的任务建议重新开始。

本次实现范围为单实例，不包含多实例协调或 Redis 状态方案。部署时保持一个实例负责这些运行，并保留需要续接的状态目录。

## 预算、上下文与协作约定

`multi_agent.max_concurrent_subagents` 默认 3，必须为正整数。`MultiAgent:MaxModelTurns` 默认 128，可通过 `MultiAgent__MaxModelTurns` 配置。预算针对整个运行的模型调用总数，子代理及摘要调用均计入，并非每个代理各有 128 次。长任务由操作者明确调整预算。

每个代理按上一轮 `input_tokens` 判断上下文大小；达到配置阈值（默认 64000，可用 `MultiAgent:CompactThresholdTokens` 或请求 `context_management.compact_threshold` 调整）时，在下一次推理前请求独立摘要，再继续任务。摘要保留任务、约束、完成工作、重要结果、代理路径和待办，摘要请求复用原代理管线并计入用量。这是本地上下文管理，不等同于官方私有压缩实现，也不提供无服务器状态的完整历史恢复。

委派具体、独立的任务，修改文件前划分归属，共享资源由根代理协调。明确子代理执行当前委派任务，继承历史仅作背景，避免重复执行根代理编排要求。首次真实验收曾观察到子代理重复创建后代；补充当前任务与继承背景说明后，复验按一层委派完成。任务边界通过明确的提示和协作约定管理。

`followup_task` 使用显式队列保存后续任务，按顺序执行，不把多次请求覆盖成最后一条。`send_message` 只投递消息，不替空闲代理启动新任务。中断按任务代次取消执行，旧代次的迟到结果和 delta 不得进入新任务。

`fork_turns: N` 严格继承最近 N 个已结束任务回合，当前未结束任务不计入，工具往返和 mailbox 通知不单独计为回合。`all` 继承可用历史，`none` 只继承适用的基础指令，再接收新的委派任务。历史不足 N 个时继承已有的已结束回合。

子代理失败或输出不完整时，保留该代理的失败状态并向父代理报告，兄弟代理继续工作。根代理最终回答需等待已接受的子任务结束并汇总报告；根代理提前给出的回答不将仍有子任务的整个运行标记完成。HTTP 因客户端工具暂停而结束本次 response，与整个 run 完成是不同状态。

根代理失败以 `response.failed` 结束响应，输出截断以 `response.incomplete` 结束，已开始的部分消息以 incomplete item 收尾，未完成的工具参数不会发布为可执行调用。WebSocket 注入成功返回 `response.inject.created`；完成后注入返回 `response_already_completed`，未知响应返回 `response_not_found`，失败事件保留原 input。非法注入结构返回通用 `error` 并关闭连接；已有响应活动时再次 create 返回 `response_in_progress` 错误，保留当前运行及其注入处理，不因该错误丢失已接收注入的 ACK。排查先查看服务和原渠道日志，不用无限重试代替诊断。

## 验证记录

仓库已新增正式行为测试，覆盖协议归一化与动态配置、真实增量及回压、工具映射、六个协作动作、任务队列、严格 fork、失败隔离、结束竞争、状态续接、摘要与预算，以及 HTTP/WS 服务行为。实时测试用屏障阻止上游终结，在释放终结前检查客户端已收到 delta；竞争场景使用明确的事件顺序，不用真实耗时阈值代替正确性断言。

在仓库根目录运行：

```sh
dotnet test opencodex_proxy/tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj --filter 'FullyQualifiedName~MultiAgent'
dotnet test opencodex_proxy/tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj
```

以下是此前临时确定性程序及真实客户端业务验证记录。`/tmp` 证据为本机临时产物，清理临时目录后不保证保留；它们补充正式测试，不替代当前代码的测试结果。

| 场景 | 结果与证据 |
| --- | --- |
| 模型开关分流 | 同一模型关闭能力后 `/v1` 返回普通响应且没有模拟会话响应头；重新开启后配置保存正确。未开启能力的其他模型也走普通路径。 |
| Codex 协议探针 | CLI `0.158.0-alpha.2.1` 执行服务端子代理工具，两个调用准确回传各自 `call_id`。证据：`/tmp/ocxp-v2-probe-20260930-dual/`。 |
| 确定性工具往返 | 双代理上游使用相同调用 ID 时，对外标识仍独立，结果不串上下文；HTTP 暂停不误判运行完成，内存续接正常，`store: false` 未写状态目录。 |
| 确定性后续任务 | 运行中收到 follow-up 后执行新任务并报告，子代理共执行两轮。 |
| 确定性中断 | 中断旧任务后 follow-up 正常完成，旧任务不覆盖新任务结果。以上三场景均 PASS，证据：`/tmp/ocxp-ma-acceptance/events.json`。 |
| 确定性持久化、摘要与截断 | 快照重新加载后工具续接完成；强制独立摘要保留原约束、隔离其他代理并合计用量；截断输出只发 incomplete。与前三个场景共六项 PASS，证据：`/tmp/ocxp-ma-acceptance/events.json`。 |
| 真实 Codex 与 Cline | 两个子代理通过客户端命令分别读取 `alpha=17`、`beta=29`，根代理汇总 46。证据：`/tmp/ocxp-v2-live/codex3.jsonl`。 |
| 真实 WebSocket 与 Cline | 子代理调用 `lookup_value`，注入 42 获得 ACK，根代理回答 `VERIFIED:42`；完成后与未知响应的注入均返回预期错误、保留 input，更新任务说明后的复验为一层子代理、34 个事件序号严格递增，耗时约 9.5 秒。证据：`/tmp/ocxp-v2-live/ws_acceptance_events.json`。初次运行出现的重复创建后代问题已在任务说明调整后复验。 |

验证发现并修复了 `type: additional_tools` 的 `tools` 读取位置错误导致工具丢失、Cline 非流式外壳导致空 output，以及 custom tool `input` 包装未解开等问题。修复限定在新多代理实现及接入处，没有借此修改原有转换器。

## 本轮验证结果

- `dotnet test opencodex_proxy/OpenCodex.sln --no-restore --nologo`：1029 通过、0 失败、0 跳过，包含原有后端测试。
- 新增行为测试覆盖实时 delta 先于模型终结、交错事件不串代理、动态工具与指令、相同任务的新提交与重试、六动作、失败隔离、唯一终态、严格任务回合、摘要/快照后的边界，以及 HTTP/WS 注入与并发创建。
- 真实 Codex + Cline/DeepSeek 再次完成两个只读子代理任务，读取 17 和 29 后汇总 46。临时证据：`/tmp/ocxp-v2-live/codex-stream.jsonl`。
- 一次真实流式采样中，首文本增量在约 1.469 秒到达，响应在约 4.096 秒结束，收到 456 个文本增量。该结果用于确认实时传递，不作为延迟保证。证据：`/tmp/ocxp-v2-live/live-stream-timing.json`。
- 多实例共享调度已按最新范围撤回，相关代码、测试及专用 Redis 测试容器均已删除；没有新增 Redis 部署要求。

## 明确边界

当前覆盖服务端代理运行、客户端工具往返、HTTP/SSE、WebSocket 注入和本地状态续接，不宣称与官方 v2 所有行为完全一致。

真实 CLI 回传历史会丢弃 hosted 多代理事件和 `agent` 属性，因此必须保留服务器运行状态和调用映射。没有服务器状态时，不能仅凭该客户端回传历史恢复完整代理树。官方密文互通与外部 hosted 运行导入不在支持范围。

工具可执行不代表 Codex UI 原生显示服务端 hosted 代理树。正式测试覆盖的故障与竞争场景不等于每种真实上游都已经实机验证；遇到相关问题应保留日志、快照，针对具体场景单独验证。当前改动尚未提交或部署。
