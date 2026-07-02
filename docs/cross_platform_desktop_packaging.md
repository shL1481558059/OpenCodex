# OpenCodex 跨平台桌面端打包方案

## 1. 目标

当前项目已经具备桌面端基础能力，但打包流程还没有完整收敛为可持续发布的跨平台方案。

本文件先整理目标产物、现状、推荐方案、风险和实施步骤，作为后续补齐 GitHub 自动打包的执行依据。

本阶段目标产物如下：

- macOS arm64: Apple Silicon
- Windows x64: 主流桌面 Windows
- Deepin Linux x64: 优先输出 `.deb`

## 2. 当前项目现状

### 2.1 已有基础

项目当前已经不是从零开始，桌面端相关能力已经存在：

- 根目录 [package.json](/Users/w/shL/Work/shL/OpenCodex/package.json) 已提供：
  - `desktop:prepare`
  - `desktop:dev`
  - `desktop:build`
- [src-tauri/tauri.conf.json](/Users/w/shL/Work/shL/OpenCodex/src-tauri/tauri.conf.json) 已启用 Tauri 2 打包配置
- [src-tauri/src/main.rs](/Users/w/shL/Work/shL/OpenCodex/src-tauri/src/main.rs) 已实现：
  - 启动 .NET sidecar 后端
  - 创建桌面窗口
  - 将 SQLite、日志、密钥目录、OCR 缓存落到应用数据目录
  - 支持重启后端
- [scripts/prepare_tauri_sidecar.mjs](/Users/w/shL/Work/shL/OpenCodex/scripts/prepare_tauri_sidecar.mjs) 已实现：
  - 构建前端
  - 复制前端静态资源到 `src-tauri/resources/wwwroot/admin`
  - 使用 `dotnet publish` 发布 self-contained 单文件后端
  - 生成 Tauri sidecar 所需命名格式的可执行文件
- 后端已支持桌面模式的配置注入和静态资源托管：
  - [opencodex_proxy/src/Presentation/OpenCodex.Api/Program.cs](/Users/w/shL/Work/shL/OpenCodex/opencodex_proxy/src/Presentation/OpenCodex.Api/Program.cs)
  - [opencodex_proxy/src/Presentation/OpenCodex.Api/Hosting/OpenCodexApplicationBuilderExtensions.cs](/Users/w/shL/Work/shL/OpenCodex/opencodex_proxy/src/Presentation/OpenCodex.Api/Hosting/OpenCodexApplicationBuilderExtensions.cs)
  - [opencodex_proxy/src/Presentation/OpenCodex.Api/Configuration/DesktopSystemSettingsStore.cs](/Users/w/shL/Work/shL/OpenCodex/opencodex_proxy/src/Presentation/OpenCodex.Api/Configuration/DesktopSystemSettingsStore.cs)

### 2.2 当前判断

当前架构方向是正确的，不建议换 Electron，也不建议拆成“两套桌面/网页后端”。

最稳妥的路线是继续沿用：

- Tauri 2 作为桌面壳
- Vue 管理台作为前端
- ASP.NET Core 作为本地 sidecar 后端
- SQLite 作为桌面端默认数据库

也就是说，当前问题不是“是否能做桌面端”，而是“如何把已有能力收敛为稳定的跨平台发布流程”。

## 3. 推荐发布架构

### 3.1 总体思路

推荐使用 GitHub Actions 在不同平台的原生 runner 上分别打包，而不是尝试在单一机器上做复杂交叉编译。

原因：

- Tauri 桌面打包本身就更适合在目标系统原生构建
- .NET sidecar 需要按目标平台生成 self-contained 可执行文件
- macOS、Windows、Linux 的安装包格式和系统依赖差异较大
- 用矩阵任务可以把每个平台的环境隔离清楚，后续维护成本更低

### 3.2 推荐构建模型

统一采用以下构建链：

1. 前端 `vite build`
2. 复制前端产物到 Tauri resources
3. `dotnet publish` 生成目标平台 sidecar
4. `tauri build` 生成桌面安装包
5. GitHub Actions 上传产物并关联到 Release

## 4. 目标平台映射

### 4.1 macOS arm64

- 目标用户：Apple Silicon
- Tauri target triple: `aarch64-apple-darwin`
- .NET RID: `osx-arm64`
- sidecar 文件名: `opencodex-api-aarch64-apple-darwin`
- 推荐产物：
  - `.dmg`
  - `.app`

说明：

- 这是当前最值得优先支持的 macOS 桌面目标
- 后续如有 Intel Mac 用户，再补 `x86_64-apple-darwin`

### 4.2 Windows x64

- 目标用户：主流 64 位 Windows
- Tauri target triple: `x86_64-pc-windows-msvc`
- .NET RID: `win-x64`
- sidecar 文件名: `opencodex-api-x86_64-pc-windows-msvc.exe`
- 推荐产物：
  - 安装包优先 `nsis`
  - 可同时保留 `msi`

说明：

- 当前不建议把 32 位 Windows 作为第一阶段目标
- 主流桌面环境基本都是 64 位系统

### 4.3 Deepin Linux x64

- 目标用户：Deepin 64 位桌面环境
- Tauri target triple: `x86_64-unknown-linux-gnu`
- .NET RID: `linux-x64`
- sidecar 文件名: `opencodex-api-x86_64-unknown-linux-gnu`
- 推荐产物：
  - 优先 `.deb`
  - 可选补充 `AppImage`

说明：

- Deepin 的实际分发体验更适合 `.deb`
- `AppImage` 可作为通用 Linux 备用包，但不是第一优先级

## 5. GitHub 自动打包方案

### 5.1 触发方式

建议采用两类触发：

- `workflow_dispatch`
  - 手动触发构建
  - 适合先验证流程
- `push tags: v*`
  - 版本 tag 自动构建
  - 自动创建或更新 GitHub Release

当前仓库已新增 workflow：

- [.github/workflows/desktop-release.yml](/Users/w/shL/Work/shL/OpenCodex/.github/workflows/desktop-release.yml)

### 5.2 构建矩阵

建议先做三条构建线：

- `macos-latest` -> `aarch64-apple-darwin`
- `windows-latest` -> `x86_64-pc-windows-msvc`
- `ubuntu-22.04` -> `x86_64-unknown-linux-gnu`

其中 Linux 产物面向 Deepin x64，优先发布 `.deb`。

### 5.3 Workflow 关键步骤

每个平台的 GitHub Actions 任务建议包含：

1. `actions/checkout`
2. 安装 Node.js
3. 安装 .NET 10 SDK
4. 安装 Rust toolchain
5. Linux runner 安装 Tauri 所需系统依赖
6. `npm ci`
7. `npm --prefix frontend ci`
8. `dotnet test opencodex_proxy/OpenCodex.sln`
9. 运行 Tauri 打包
10. 上传安装包产物

### 5.4 当前 workflow 的实际产物策略

当前 workflow 已按目标平台拆分为三条矩阵任务：

- `macOS arm64`
  - runner: `macos-latest`
  - Tauri target: `aarch64-apple-darwin`
  - bundle: `dmg`
- `Windows x64`
  - runner: `windows-latest`
  - Tauri target: `x86_64-pc-windows-msvc`
  - bundle: `nsis`
- `Deepin Linux x64`
  - runner: `ubuntu-22.04`
  - Tauri target: `x86_64-unknown-linux-gnu`
  - bundle: `deb`

行为约定如下：

- 手动触发 `workflow_dispatch`
  - 只上传 GitHub Actions workflow artifacts
  - 不创建 Release
- 推送 `v*` tag
  - 自动创建或更新 GitHub Release
  - Release 先以 draft 形式生成
  - 同时上传 workflow artifacts

### 5.5 为什么不要依赖“本机架构自动识别”

当前 [scripts/prepare_tauri_sidecar.mjs](/Users/w/shL/Work/shL/OpenCodex/scripts/prepare_tauri_sidecar.mjs) 按 `process.platform` 和 `process.arch` 自动选择目标。

这在“本机打本机”时是可行的，但在 GitHub Actions 里不够稳：

- Tauri 可以通过 `--target` 指定目标 triple
- 但 sidecar 脚本如果仍按 runner 自身架构判断，可能出现：
  - Tauri 打的是目标 A
  - .NET sidecar 出的是目标 B

因此后续落地时，建议让 sidecar 脚本支持显式传入目标平台，例如：

- `OPENCODEX_DESKTOP_TARGET=darwin-arm64`
- `OPENCODEX_DESKTOP_TARGET=win32-x64`
- `OPENCODEX_DESKTOP_TARGET=linux-x64`

这样 GitHub Actions 可以明确控制：

- Tauri target triple
- .NET RID
- sidecar 文件命名

三者保持一致。

### 5.6 如何手动触发 GitHub 自动打包

在 GitHub 仓库中：

1. 打开 `Actions`
2. 选择 `desktop-release`
3. 点击 `Run workflow`
4. 等待三条矩阵任务完成
5. 从 `Artifacts` 下载对应平台产物

如果要自动生成 Release：

1. 本地创建版本 tag，例如 `v0.1.0`
2. 推送 tag 到 GitHub
3. 等待 workflow 自动完成
4. 到 `Releases` 中查看 draft release

## 6. 当前代码里的已知缺口

### 6.1 前端依赖安装状态需要收敛

本地验证时，`npm run desktop:prepare` 当前会在前端构建阶段失败，原因是：

- `frontend/package.json` 已声明 `@tauri-apps/api`
- 但当前本地 `frontend/node_modules` 中缺少该依赖

这说明：

- 代码方向没有问题
- 但打包流程必须建立在“干净安装依赖”的前提上

因此 GitHub Actions 中必须明确使用：

- `npm ci`
- `npm --prefix frontend ci`

而不是依赖本地残留环境。

### 6.2 Rust 锁定文件缺失

当前 `src-tauri` 目录下尚未看到 `Cargo.lock`。

对于桌面应用发布工程，建议补齐并提交 `Cargo.lock`，原因是：

- 保证 CI 与本地依赖解析一致
- 避免 Tauri/Rust 依赖漂移导致偶发构建差异

### 6.3 应用图标与品牌资源尚未补齐

当前仓库里只有前端 favicon，没有看到桌面应用发布常用图标集：

- `.icns`
- `.ico`
- 多尺寸应用图标 PNG

这不会阻止第一版打包，但会影响正式发布质量。

### 6.4 签名与公证尚未纳入第一阶段

第一阶段建议先只完成“可自动构建并下载”的目标，不把签名作为阻塞条件。

后续再分阶段补：

- macOS notarization
- Windows 代码签名

## 7. 平台专项注意事项

### 7.1 macOS arm64

- 未签名包初期可能会被 Gatekeeper 拦截
- 如果后续面向普通终端用户分发，建议补签名和 notarization
- 如果只做内部使用，第一阶段可接受未签名测试包

### 7.2 Windows x64

- 未签名安装包可能触发 SmartScreen 警告
- 第一阶段可以先生成 NSIS 安装包验证
- 后续再评估代码签名证书

### 7.3 Deepin Linux x64

- Deepin 更适合直接使用 `.deb`
- Linux runner 需要额外安装 Tauri 的系统依赖
- 如果后续要覆盖更多发行版，再补 `AppImage`

## 8. 推荐实施顺序

建议按以下顺序推进，而不是一次性把所有打包问题混在一起处理：

### 第一步：整理方案文档

当前文件即为这一步产物。

### 第二步：收敛本机可重复构建

目标：

- 修正依赖安装流程
- 保证 `desktop:prepare` 稳定完成
- 保证 sidecar 与 Tauri 目标平台映射清晰

### 第三步：补 GitHub Actions 自动打包

目标：

- 先打通三平台矩阵构建
- 先上传 Release draft 或构建产物
- 不把签名和自动更新纳入第一版

### 第四步：补桌面发布质量

目标：

- 图标
- 安装包细节
- 版本号统一
- 签名/公证

## 9. 结论

当前项目最合适的跨平台桌面打包方案是：

- 保持 Tauri 2 + ASP.NET Core sidecar + Vue 前端 的现有架构
- 使用 GitHub Actions 在 macOS、Windows、Linux 原生 runner 上分别打包
- 第一阶段目标产物定为：
  - macOS arm64
  - Windows x64
  - Deepin Linux x64 `.deb`

当前代码基础已经足够支撑这个方向，后续重点不是重构架构，而是补齐：

- 目标平台显式映射
- 可重复依赖安装
- GitHub Actions workflow
- 发布元数据和图标资源

## 10. 后续建议

下一步建议直接进入“GitHub 自动打包 workflow 落地”：

1. 让 sidecar 准备脚本支持显式目标平台
2. 新增 GitHub Actions workflow
3. 跑通三平台构建
4. 再补图标和签名
