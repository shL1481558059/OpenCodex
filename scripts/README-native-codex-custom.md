# 真实 custom 工具专项

使用真实 Codex 0.160 app-server 和真实 `deepseek-v4.1-flash`，复用生命周期及恢复脚本的连接驱动。只连接回环 API，key 通过 `OCXP_E2E_API_KEY` 提供。输出目录必须不存在。

```sh
python3 scripts/verify_native_codex_custom.py \
  --base-url http://127.0.0.1:18541/v1 \
  --output-dir /absolute/private/custom-http \
  --native-trace-log /absolute/private/api.log \
  --transport http
```

再以不同输出目录和 `--transport websocket` 运行，要求真实 Responses WS 路由。两种模式均通过本机 strict-config，并逐条比对 native 日志；WS 回退 HTTP 直接失败。

覆盖四个独立线程：

- 多行 JavaScript、首尾空格、中文、emoji、换行、制表符、反斜杠和引号。原始 rollout 的 custom input 必须逐字等于给定源码，工具输出也必须保留对应文本。
- 故意 `throw new Error(...)`，同一 turn 内再次执行合法 custom exec 恢复；必须观察真实错误和真实成功输出。
- 故意 JavaScript 语法错误，同一 turn 合法执行恢复；不能将错误伪装为成功。
- 一个 custom exec 内通过 `Promise.all` 并行调用三个真实 shell 工具，每个文件必须仅追加一行，原始输出包含全部标记。

每项保存 `*-tools.json`，包括原始 custom_tool_call 输入、call_id 和对应实际输出。最终回答不能代替工具证据；模型擅自改写源码、增加工具、跳过错误或重复执行均判失败。此测试不声称控制了模型是否在一个 Responses 批次中发出多个 custom 调用，两个错误恢复场景明确按前后依赖执行。

## 原生 apply_patch

增加 `--tool apply_patch` 会关闭 code mode，在同一固定 DeepSeek 模型上执行原生 `apply_patch` 的增、改、错误上下文后同 turn 恢复、删四项验收。实际客户端模型缓存必须声明 `apply_patch_tool_type=freeform`；原始调用必须是 `custom_tool_call`，patch 文本逐字匹配，且实际文件内容符合每一步预期。shell patch 或 exec 内部 apply_patch 不计通过。HTTP 和 WebSocket 使用独立输出目录分别执行。

所有共用 app-server 驱动的验收现在先请求带实际 `client_version` 的 Codex `/models` 接口，再要求真实客户端 `models_cache.json` 包含当前模型及工具声明。普通 `/models` 200 不能替代 Codex 专用目录成功；缺失目录资源或使用 fallback 元数据直接失败。隔离服务的 content root 必须包含项目原有的 `wwwroot/ocxp_codex_official_models.json`，无需也不应手工伪造客户端模型缓存。
