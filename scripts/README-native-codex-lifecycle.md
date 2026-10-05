# 真实 Codex app-server 生命周期验收

`verify_native_codex_lifecycle.py` 使用桌面附带的 Codex CLI 0.160.x，通过真实 app-server JSON RPC 调用 `deepseek-v4.1-flash`，验收客户端拥有对话状态的 native 路径。仅允许回环 HTTP(S) API；每次新建独立 `CODEX_HOME` 和工作目录，不读取或修改用户真实会话，不修改生产配置。

接口依据为 [OpenAI 官方 app-server 文档](https://developers.openai.com/codex/app-server/) 和**实际运行二进制**生成的 `app-server generate-json-schema --experimental` 输出。脚本检查 RPC 字段名和必需字段，不以猜测方式替代缺失的 compact/steer/interrupt API。完整 schema 随验收证据保存。

## 运行

先在独立本地数据库中配置真实 chat 上游与 `deepseek-v4.1-flash`，为模型启用 v2 agent 能力，启动本地服务。测试 API key 通过 `OCXP_E2E_API_KEY` 环境变量提供；脚本不打印凭据。输出目录必须不存在。

```sh
python3 scripts/verify_native_codex_lifecycle.py \
  --base-url http://127.0.0.1:18541/v1 \
  --native-trace-log /absolute/private/test-dir/api.log \
  --output-dir /absolute/private/native-lifecycle-run
```

默认二进制为 `/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex`。可用 `--codex-bin` 指定相同版本的实际安装。脚本显式启用 `code_mode`、`multi_agent`、`multi_agent_v2`、`use_agent_identity`、`api_key_model_discovery`，固定模型名称，不允许模型自动替换。

使用 `--prepare-only` 可只生成配置和 schema，不发模型请求。真实执行时每个 RPC/turn 默认限时 240 秒，可用 `--timeout` 覆盖。退出时清理本次 app-server 进程组。

## 验收断言与证据

- 新建问答：随机 nonce 的严格单行 final，恰好一次 `turn/completed`，`thread/read` 中恰好一个非 commentary 终态消息。0.160 schema 允许 phase 为 null；脚本接受 null 或 final_answer，但保留实际 phase，仍拒绝多条终态消息。
- 相同文字的显式新 turn：必须得到不同 turn ID，历史中保留两个 turn，不能吞掉新请求或重放旧 turn。
- `thread/fork`：新 thread ID，继承历史 turn；只回答当前新问题，并能引用继承上下文。
- `thread/compact/start`：必须观察到真实 compact 通知和持久化 `contextCompaction` item，随后新问题正常完成。
- `turn/steer`：真实 shell 创建 ready 文件且仍在运行时发送更正；`expectedTurnId` 必须匹配；更正必须进入同一 turn，最后只输出更正后的 final。
- `turn/interrupt`：在真实 shell 执行期间取消，必须得到 `interrupted`，不能出现 final；下一 turn 正常完成且不恢复已取消任务。Codex 0.160 明确允许此前启动的 unified exec 进程继续后台运行，因此不把操作系统进程终止作为代理的保证。脚本等到原命令应结束后检查实际文件，并在 summary 的 `late_side_effect_observed` 中如实记录是否产生迟到副作用。
- code mode：真实 `functions.exec` 使用 1 毫秒 yield 参数调用延时 shell，再由真实 `functions.wait` 接收完成结果。检查原始 rollout 中 exec 保持 `custom_tool_call`、wait 保持 `function_call`，exec 输出确实包含 running cell，wait 输出含真实结果。追加文件恰好一行，拒绝重复执行。

输出目录包含 `rpc.jsonl`、`stderr.log`、各阶段 `*.thread.json`、`tool-shapes.json`、`summary.json`、schema、独立原生 rollout 和实际副作用文件。通过条件来自客户端事件、原始工具形状、持久化 thread/read 与文件内容；模型文字声称成功不能替代这些证据。此脚本验证 app-server 和真实 CLI 客户端协议，不冒称已点击或截图验证桌面 UI。

正常退出码为 0；任一场景失败则退出 1，并在 `summary.json` 中记录失败原因。脚本不会把未执行或不支持的场景算作通过。

## 强制验证 native 路由

模型的 `v2_agent_simulation=true` 必须在**实际请求名**的全局匹配规则上生效；仅 ModelKey 相同、但 exact MatchPatterns 带 provider 前缀，并不能匹配裸模型名。启用本地 `Logging__LogLevel__OpenCodex.Api.Services.MultiAgentResponseService=Debug`，保存 API 日志，并通过 `--native-trace-log` 提供路径。脚本必须逐一找到本次真实 root/child thread 的 `Native client response: thread=...` 记录才通过，避免把普通协议转换路径误算为新 native 执行器验收。日志仅记录身份和传输，不记录请求正文。
