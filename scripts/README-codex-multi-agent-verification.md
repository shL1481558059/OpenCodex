# 本地真实 Codex 多 agent 验收

这组脚本调用真正的 Codex 进程和 `deepseek-v4.1-flash`，模型输出不由 fixture 模拟。原生客户端路径由 Codex 管理 turn、fork 和 compact，OpenCodex 每个请求只执行一次模型调用。

## 前置条件

- 本地启动待验证的 OpenCodex 源码构建，使用独立 SQLite 数据库、独立状态目录和仅回环地址监听。不要连接生产数据库。
- 安装 Python 3（仅标准库）及支持原生 collaboration 的 Codex。本次使用桌面内附的 Codex CLI 0.160.0，而不是 PATH 中的旧版本。
- 在本地管理端创建测试 API key、模型 `deepseek-v4.1-flash`，启用模型能力 `v2_agent_simulation`；配置真实 **chat** 上游渠道。这样真实请求会经过待测的 Responses → Chat 转换及本地 agent 运行器。
- 测试时对外模型名称固定为 `deepseek-v4.1-flash`；本次实际上游映射为 `cline-pass/deepseek-v4.1-flash`。按自己的真实渠道填写映射，不要使用另一个模型冒充。
- 本地 API key 放入 `OCXP_E2E_API_KEY` 环境变量。脚本不打印密钥，也不读取用户全局 Codex 配置。

示例本地后端启动（先配置上述本地模型与渠道）：

```sh
export OPENCODEX_DB_PROVIDER=sqlite
export OPENCODEX_DB_CONNECTION_STRING="Data Source=/absolute/private/test-dir/local.db"
export MultiAgent__StateDirectory=/absolute/private/test-dir/runs
dotnet run --launch-profile OpenCodex.Api \
  --project opencodex_proxy/src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj \
  -- --urls http://127.0.0.1:18541
```

脚本为每次运行创建独立 CODEX_HOME 和 work 目录，显式开启 `multi_agent=true`、`multi_agent_v2=true`、`use_agent_identity=true`，并使用本地模型目录。目录必须尚不存在，以免复用旧副作用文件造成假通过。

这些设置限定了验收范围：证明原生 v2 collaboration 链路，不表示默认 V1、任意客户端版本或任意自定义 WebSocket 注入格式都已通过。`api_key_model_discovery=true` 用于从本地 API 获取模型元数据。

## 原生子线程、消息、follow-up 与中断

```sh
python3 scripts/verify_multi_agent_codex.py \
  --codex-bin /Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex \
  --base-url http://127.0.0.1:18541/v1 \
  --native-trace-log /absolute/private/test-dir/api.log \
  --output-dir /absolute/private/test-dir/collaboration \
  --scenario collaboration
```

真实任务创建 alpha、beta、sleeper 三个原生子线程：alpha/beta 读取各自测试文件并计算；beta 接收消息；alpha 在原线程收到 follow-up；sleeper 的 sleep 工具被中断。父任务最后汇总真实结果。

验收不能只看模型自述。脚本检查原生 session 的父子身份、实际协作调用、子任务工具执行、消息投递、后续任务及 interrupted 事件。原始 CLI 事件与子线程 rollout 保留在输出目录，可进一步用同一 CODEX_HOME 的 app-server `thread/read` 读取。桌面截图和真实 UI 点击仍是另外的人工验收维度，脚本不会冒称已经点击了桌面界面。

### 命令退出码的证据边界

命令成功以本地 `CommandExecution` 的结构化 `exit_code` 和 `status` 为准。外层 `functions.exec` 的 `Script completed` 只表示 JavaScript 包装执行结束，内部 shell 可以返回非零。缺失退出码不能当作 `0`，被取消或仍运行的命令不能当作成功；复合 shell 命令整体返回 `0` 也不证明每一步成功。

code mode 的内部命令 ID（`exec-...`）与外层工具调用 ID（`call_...`）不同，验收不能强制它们相等。关联应检查同轮次、时间范围和实际返回输出，并拒绝存在多个候选的关联。结构化命令状态与模型可见文本需要分别核对。

验收结果的 `native_session_audit.command_execution_audit` 按 agent 路径保存真实 `status`、`exit_code`、关联结果与退出码计数，不复制命令正文。`verified_success` 表示命令唯一关联且真实状态为 `completed/0`；完整 fixture 验收还要求外层工具成功及实际文件输出。缺失、空输出、截断或无法唯一关联的结果保留为未验证，不推定成功。

调用 `tools.exec_command` 后，使用 `text(result)` 可以把完整结果（包括可用的退出码，或仍在运行时的 session ID）交还模型。只使用 `text(result.output)` 会省略这些字段。OpenCodex 只接收客户端提交的工具结果，无法从省略后的文本恢复真实退出码；此验收脚本不会改写模型生成的 JavaScript，也不修改官方客户端运行器。要在模型省略字段时仍强制展示退出码，需要客户端运行器提供独立于 `text()` 的结构化状态通道。

建议真实负路径验收至少覆盖：命令输出固定标记后返回 `7`、随后命令返回 `0`、后台命令经轮询才结束，以及中断命令。核对本地结构化事件、模型可见工具结果和最终结论，不能只检查 CLI 自身的退出码。

## 客户端生命周期与压缩

运行 [真实 app-server 生命周期验收](README-native-codex-lifecycle.md)，验证同文新 turn、fork、真实客户端 compact、steer、interrupt 和 code-mode exec/wait。

旧 `--scenario recovery` 和 `codex_summary_fault_proxy.py` 保留作历史故障复现资料。它们要求服务端自动摘要，而 native 路径已经移除此行为，因此不能再用该场景验收新版 native，也不能通过降低服务端 CompactThreshold 强制客户端压缩。旧服务端 Runtime 的摘要恢复由确定性回归测试覆盖。

## 新 native 契约与边界

- 客户端完整 input 是权威历史；`previous_response_id` 只用于同 owner/thread 的续接。环境、AGENTS、compact 摘要不会生成服务端业务任务。
- HTTP 携带稳定 turn ID 时，同 turn、同规范请求重放已提交的 response/call ID；缺失 turn ID 不按内容猜测重试。WebSocket 升级头是连接级信息，不用于多条 `response.create` 的幂等判断。
- 所有工具经过完整名称、namespace、类型、call ID、payload 和整批事件一致性检查，成功保存后再发布可执行完成事件。工具执行端继续负责参数语义、JSON Schema 和 custom grammar。
- `incomplete` 保存为仅续接记录，保留原因和非工具输出，不发布或在续接中加入未完成工具；原请求重试仍调用模型。失败响应不作为成功重放。
- 新状态存于 `MultiAgent:StateDirectory/native-client-v1`，不加载旧 Runtime 会话快照。memory-only 状态随进程退出消失，客户端需重发完整历史；持久状态保留 response 引用。
- 内存正文按 owner/thread 共享不可变副本；每个响应仍保留独立引用列表。持久模式保存完整记录，目前没有自动保留期清理；容量需要按实际会话量监测。
- 正文 idle timeout 使用渠道 `timeout_seconds`，总预算默认 `timeout * max(10, retry_count + 1) + 30 * retry_count` 秒；可用 `compat.stream_total_timeout_seconds` 显式覆盖。总预算含重试退避，已发正文后不重试。

## 回归测试

```sh
python3 -m unittest discover -s scripts -p test_verify_multi_agent_codex.py
dotnet test opencodex_proxy/tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj --no-restore
```

单元与集成测试覆盖摘要失败重试、旧失败状态拒绝空成功、已提交 final 重放、整批工具提交、消息 phase、空消息、上游 EOF、错误日志和已发外部工具后不切渠道重放。真实模型验收用于补足模拟模型无法证明的任务恢复语义和原生客户端契约。

## 强制验证 native 路由

模型的 `v2_agent_simulation=true` 必须在**实际请求名**的全局匹配规则上生效；仅 ModelKey 相同、但 exact MatchPatterns 带 provider 前缀，并不能匹配裸模型名。启用本地 `Logging__LogLevel__OpenCodex.Api.Services.MultiAgentResponseService=Debug`，保存 API 日志，并通过 `--native-trace-log` 提供路径。脚本必须逐一找到本次真实 root/child thread 的 `Native client response: thread=...` 记录才通过，避免把普通协议转换路径误算为新 native 执行器验收。日志仅记录身份和传输，不记录请求正文。
