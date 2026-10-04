# 本地真实 Codex 多 agent 验收

这组脚本调用真正的 Codex 进程和 `deepseek-v4.1-flash`。模型输出不由 fixture 模拟；故障代理仅让第一次内部摘要返回一个明确错误，其余调用逐字节转发真实上游。

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
  --output-dir /absolute/private/test-dir/collaboration \
  --scenario collaboration
```

真实任务创建 alpha、beta、sleeper 三个原生子线程：alpha/beta 读取各自测试文件并计算；beta 接收消息；alpha 在原线程收到 follow-up；sleeper 的 sleep 工具被中断。父任务最后汇总真实结果。

验收不能只看模型自述。脚本检查原生 session 的父子身份、实际协作调用、子任务工具执行、消息投递、后续任务及 interrupted 事件。原始 CLI 事件与子线程 rollout 保留在输出目录，可进一步用同一 CODEX_HOME 的 app-server `thread/read` 读取。桌面截图和真实 UI 点击仍是另外的人工验收维度，脚本不会冒称已经点击了桌面界面。

## 一次摘要失败后的原样重试恢复

1. 在**本地测试服务**中临时将 `MultiAgent__CompactThresholdTokens=1000`，重启服务。这个极低阈值仅为强制进入摘要路径，不是生产建议。
2. 启动以下回环测试代理，其中 upstream-url 是真实 chat 上游的 API 基础 URL：

```sh
python3 scripts/codex_summary_fault_proxy.py \
  --upstream-url https://your-real-chat-provider.example/api \
  --port 18542 \
  --events /absolute/private/test-dir/fault-events.jsonl
```

3. 把本地测试渠道的 baseurl 临时改成 `http://127.0.0.1:18542`，保留原真实凭据、模型映射及必要请求头。仅修改本地隔离数据库。测试代理转发认证但不记录认证或请求正文。
4. 每次验收重新启动故障代理，确保只有本轮第一次摘要失败，然后执行：

```sh
python3 scripts/verify_multi_agent_codex.py \
  --codex-bin /Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex \
  --base-url http://127.0.0.1:18541/v1 \
  --output-dir /absolute/private/test-dir/recovery \
  --scenario recovery \
  --fault-events /absolute/private/test-dir/fault-events.jsonl \
  --database /absolute/private/test-dir/local.db
```

任务执行一次不带幂等保护的追加写入，再输出严格的 `RECOVERY_OK 1`。因此若客户端重放了已执行命令，会得到两行，不能被“先判断文件存在”的保护逻辑掩盖。

通过条件同时包括：

- 本轮时间范围内确实注入一次摘要失败，不能拿一个没触发故障的正常运行充数。
- 随后有真实摘要调用及成功的模型请求。
- 请求主日志和渠道 attempt 都记录失败，且失败之后有新的成功调用。
- `marker.txt` 恰好一行，证明已回传的副作用没有重复执行。
- 最终答复严格为 `RECOVERY_OK 1`，不能用引用该标记的交接摘要冒充完成。

完成后停止自己的测试进程，恢复本地渠道地址和摘要阈值。输出目录包含本地测试状态与原始模型记录，不应提交密钥、数据库或整份 rollout 到仓库。

## 回归测试

```sh
dotnet test opencodex_proxy/tests/OpenCodex.Api.Tests/OpenCodex.Api.Tests.csproj --no-restore
```

单元与集成测试覆盖摘要失败重试、旧失败状态拒绝空成功、已提交 final 重放、整批工具提交、消息 phase、空消息、上游 EOF、错误日志和已发外部工具后不切渠道重放。真实模型验收用于补足模拟模型无法证明的任务恢复语义和原生客户端契约。
