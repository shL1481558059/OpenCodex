# 附录

## 5.1 缩略语与术语表

| 术语 | 说明 |
| --- | --- |
| Responses | OpenAI Responses API 风格协议，入口为 `/v1/responses` |
| Chat | OpenAI Chat Completions 风格协议，入口为 `/v1/chat/completions` |
| Messages | Anthropic Messages 风格协议，入口为 `/v1/messages` |
| Channel | 上游渠道配置，包含协议类型、baseurl、apikey、headers、模型映射和兼容规则 |
| Entry Protocol | 客户端调用代理时使用的入口协议 |
| Upstream Protocol | 渠道配置决定的上游协议 |
| Model Mapping | 将客户端请求模型映射为上游模型的规则 |
| Compat | 发送上游前的参数兼容规则 |
| TTFT | Time To First Token，首个有效流式事件的延迟 |
| SSE | Server-Sent Events，服务端流式事件格式 |
| Web Search 模拟 | 代理本地调用 Tavily 并模拟 Responses `web_search` 工具 |
| Access API Key | 管理台创建的代理访问凭证，前缀为 `ocx_` |
| Upstream API Key | 上游模型服务的 API Key，配置在渠道中 |
| Reasoning Cache | 内存缓存，用于工具调用续轮时回填 reasoning/thinking |

## 5.2 版本记录

| 日期 | 版本 | 说明 |
| --- | --- | --- |
| 2026-06-05 | 0.1 | 首次整理 Python 后端模块文档 |

## 5.3 参考文档

### 项目文件

- `README.md`
- `Dockerfile`
- `requirements.txt`
- `scripts/update_remote_image.sh`

### 后端源码

- `opencodex_proxy/app.py`
- `opencodex_proxy/settings.py`
- `opencodex_proxy/config.py`
- `opencodex_proxy/routing.py`
- `opencodex_proxy/protocols.py`
- `opencodex_proxy/upstream.py`
- `opencodex_proxy/streaming.py`
- `opencodex_proxy/web_search.py`
- `opencodex_proxy/db.py`
- `opencodex_proxy/compat.py`
- `opencodex_proxy/reasoning_cache.py`
- `opencodex_proxy/logging_utils.py`
- `opencodex_proxy/errors.py`
- `opencodex_proxy/patch_semantics.py`

### 测试文件

- `tests/test_app.py`
- `tests/test_config.py`
- `tests/test_db.py`
- `tests/test_protocols.py`
- `tests/test_reasoning_cache.py`
- `tests/test_upstream.py`

## 备注

- 本文档基于当前代码静态阅读整理，没有改变运行逻辑。
- 如果后续新增协议、渠道字段、数据库表或管理 API，应同步更新对应模块文档。

