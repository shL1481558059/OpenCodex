# OpenCodex Proxy 后端文档

本文档面向维护者和二次开发者，基于当前 Python 后端代码整理。项目主体位于 `opencodex_proxy/`，测试位于 `tests/`，部署相关文件包括 `Dockerfile`、`requirements.txt` 和 `scripts/update_remote_image.sh`。

## 1. 项目概述

详见 [项目概述](project-overview/main.md)。

- 项目名称：OpenCodex Proxy
- 项目定位：面向 OpenAI Responses、Chat Completions 和 Anthropic Messages 协议的轻量代理服务
- 核心能力：协议转换、渠道路由、访问鉴权、管理后台、请求日志、统计看板、Web Search 模拟、兼容参数改写
- 技术栈：Python、Flask、SQLite、urllib、python-dotenv、Docker

## 2. 模块功能说明

每个模块文档均按“模块名称、模块职责、输入、输出、依赖模块、核心逻辑、数据结构、外部接口、异常处理、流程图、备注”的格式组织。

| 模块 | 文档 | 主要源码 |
| --- | --- | --- |
| 应用网关与代理入口 | [app-gateway](modules/app-gateway/main.md) | `opencodex_proxy/app.py` |
| 管理台前端字段/API 对照 | [admin-frontend-api](modules/app-gateway/admin-frontend-api.md) | `frontend/src/*.vue`, `opencodex_proxy/app.py` |
| 认证与访问控制 | [auth-access-control](modules/auth-access-control/main.md) | `opencodex_proxy/app.py`, `opencodex_proxy/db.py` |
| 配置管理与路由 | [config-routing](modules/config-routing/main.md) | `opencodex_proxy/config.py`, `opencodex_proxy/routing.py` |
| 协议转换 | [protocol-conversion](modules/protocol-conversion/main.md) | `opencodex_proxy/protocols.py`, `opencodex_proxy/patch_semantics.py` |
| apply_patch 兼容格式细则 | [apply-patch](modules/protocol-conversion/apply-patch.md) | `opencodex_proxy/protocols.py`, `opencodex_proxy/streaming.py` |
| 上游请求与流式转换 | [upstream-streaming](modules/upstream-streaming/main.md) | `opencodex_proxy/upstream.py`, `opencodex_proxy/streaming.py` |
| Web Search 模拟 | [web-search](modules/web-search/main.md) | `opencodex_proxy/web_search.py`, `opencodex_proxy/app.py` |
| 持久化、日志与统计 | [persistence-observability](modules/persistence-observability/main.md) | `opencodex_proxy/db.py`, `opencodex_proxy/logging_utils.py` |
| 兼容规则与 Reasoning 缓存 | [compat-reasoning-cache](modules/compat-reasoning-cache/main.md) | `opencodex_proxy/compat.py`, `opencodex_proxy/reasoning_cache.py` |

## 3. 系统流程图与交互

详见 [系统流程图与交互](system-flow/main.md)。

- 核心请求流程：客户端鉴权、JSON 入参校验、按用户选择渠道、协议转换、兼容规则处理、上游调用、响应转换、日志写入
- 模块间交互：Flask 应用层编排配置、路由、协议转换、上游请求、数据库、Web Search 和流式处理
- 数据流说明：请求体和响应体会被脱敏后进入日志详情表，统计数据来自请求日志表

## 4. 部署与运行说明

详见 [部署与运行说明](deployment/main.md)。

- 本地运行：创建虚拟环境、安装依赖、配置 `.env`、运行 `python -m opencodex_proxy`
- Docker：镜像构建依赖前端构建阶段和 Python 运行阶段
- 远程部署：可使用 `scripts/update_remote_image.sh` 构建、推送并更新远程容器

## 5. 附录

详见 [附录](appendix/main.md)。

- 缩略语与术语表
- 版本记录
- 参考文档与关键源码入口
