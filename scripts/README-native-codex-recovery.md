# 真实客户端取消、资源回收与续接验收

`verify_native_codex_recovery.py` 复用生命周期脚本的真实 Codex 0.160 app-server 驱动及实际二进制生成的 schema，仅连接回环 API。模型固定 `deepseek-v4.1-flash`。测试 key 放在 `OCXP_E2E_API_KEY`，输出目录必须不存在。

分别以 `--transport http` 和 `--transport websocket` 运行；后者配置 provider 的 `supports_websockets=true`，通过本机 `--strict-config` 校验，并要求每条 API native 路由日志都为对应 transport。HTTP 回退会导致 WS 验收失败。这里验证的是 Codex 向 OpenCodex 发出的 Responses WebSocket，app-server 控制接口仍使用 stdio。

```sh
python3 scripts/verify_native_codex_recovery.py \
  --base-url http://127.0.0.1:18541/v1 \
  --output-dir /absolute/private/recovery-http \
  --native-trace-log /absolute/private/api.log \
  --transport http
```

场景：

- 真实 `functions.wait` 使用无效 cell ID；真实 `functions.exec` yield 后，通过 `functions.wait(terminate=true)` 终止真实 cell，再等待该 cell 必须报错。保存原始工具调用、实际结果及 ID 关联，模型声称成功不算证据。
- 真实 shell 产生 ready 文件后中断 turn，确认没有 final。通过实际 `thread/backgroundTerminals/list` 找到该进程，再通过 `thread/backgroundTerminals/terminate` 显式回收。等到原命令执行期限后，必须无迟到文件。此场景检验显式资源清理，不假定 turn interrupt 自动杀死进程。
- 关闭 app-server 进程，使用同一独立 CODEX_HOME 重启，通过正式 `thread/resume` 恢复线程；必须保留旧 turn，正确回答引用旧内容的新问题。控制连接的原始 RPC 分别保存到 connection-1、connection-2。

任一场景不支持、工具错误或实际 transport 不匹配都会失败，不计为通过。服务端 API 重启需要独占窗口，当前脚本不重启共享 API。该脚本验证真实客户端协议及进程行为，不代表桌面 UI 截图验收。
