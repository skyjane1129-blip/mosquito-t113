# Windows 客户端开发环境

核对日期：2026-09-07（Asia/Shanghai）。

## 源码位置与 Git

- 本地目录：`C:\project\mosquito\mosquito-windows-client`
- 远端：`https://github.com/skyjane1129-blip/mosquito-t113.git`
- 本地分支及上游：`mosquito-windows-client` → `origin/mosquito-windows-client`
- 克隆基线：`a9b8b4e714cd2a654c961279b4ed3245c5487051`
- 使用 `--single-branch` 克隆，工作树只有客户端组件。
- 客户端后续以此 Windows 工作树为主要开发位置。WSL 原客户端保留作迁移来源，避免两处同时编辑。
- 板端权威仓库仍是 WSL `/home/janelinux/work/mosquito/mosquito-t113`，Windows 客户端任务仅只读同步。
- 当前 Windows 目录旁已有旧 `mosquito-t113` checkout，未用于此次迁移。

开始更新前运行 `git status`，处理好本地修改后再运行 `git pull --ff-only`。
2026-09-07 迁移时，环境配置和测试修复尚未提交或推送；后续同步基线见下节。

## 2026-09-09 客户端同步基线

- 同步目标为本仓库的 `mosquito-windows-client` 分支，包含当前双模式工作区、统一登录、设备检测、历史与上报检查、v5.6.4 直连接口适配、开发脚本、需求及接口文档。
- 提交前重新运行 `dev.ps1 Build`、`dev.ps1 Test`、`dev.ps1 UiTest`，全部退出码为 0；Release 构建为 0 警告、0 错误，6 组核心测试与 WPF 模拟交互检查通过。模拟测试不替代下文记录的实板验收，也不代表远程服务已经部署。
- 当前普通启动配置为 `BoardCommandPath=/usr/bin:/bin`；服务器地址仍是本机 `http://127.0.0.1:5080`，远程环境配置和真实云端联调留待后续任务。
- SDK、缓存、运行数据、截图和发布程序由 Git 忽略。旧发布目录可能保留历史配置，交付时应从当前源码重新生成。

## 已配置的工具

| 项目 | 配置 |
| --- | --- |
| .NET SDK | `8.0.424`，遵循 `global.json` 的 `latestPatch` 策略 |
| SDK 位置 | `.tools\dotnet`，Windows x64 |
| 运行时 | .NET / ASP.NET Core / Windows Desktop `8.0.30` |
| 桌面框架 | WPF，`net8.0-windows10.0.19041.0` |
| SQLite | `Microsoft.Data.Sqlite 8.0.30`，原项目依赖 |
| NuGet 缓存 | `.tools\nuget-packages` |
| CLI 状态 | `.tools\cli-home` |
| ADB | `C:\embedded\android-platform-tools\adb.exe`，37.0.1 |
| IDE 入口 | `Mosquito.Client.sln`，包含客户端、核心库和两个测试项目 |

SDK 使用微软官方签名安装脚本安装；调用前已验证 Authenticode 为 Valid、签名者为 Microsoft Corporation。
这是一份项目内可独立使用的 SDK，不依赖系统级安装或全局 PATH。
没有安装 Visual Studio；命令行已足够构建、运行和发布。使用 IDE 时需使其识别项目内 SDK，或另行安装兼容的 .NET 8 开发组件。

`.tools/`、`artifacts/`、`bin/`、`obj/` 均由 Git 忽略。
不要把 SDK、包缓存、照片、数据库或发布二进制提交到源码仓库。

## 常用操作

在客户端根目录打开 PowerShell：

```powershell
# 首次配置或恢复依赖；已有匹配 SDK 时不重复安装
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Setup

# 工具检查、全解决方案构建、核心测试
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Check
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Build
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Test

# 启动客户端
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Run

# 生成 Windows x64 自包含程序
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Publish
```

自包含程序为 `artifacts\publish\win-x64\MosquitoCapture.exe`。
分发时需要整个发布文件夹，不能只复制这一个 EXE。

若要在交互终端直接运行 `dotnet`：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -NoExit -Command ". .\scripts\Enter-Dev.ps1"
```

环境脚本只修改当前进程环境。上述执行策略参数只影响启动的 PowerShell 进程，不修改用户或系统策略。

## 本机配置与联调前提

当前 `src\Mosquito.Client\appsettings.json` 的配置基线如下；新加入的稳定设备编号尚待登记后配置：

- `AdbPath`：上述 Windows ADB 路径。
- `DeviceSerial`：`MOSQUITO-T113-DEV`。
- `DeviceId`：目前为 null，登记后填写直连与远程一致的稳定编号；不能随意用 ADB 序列号替代。
- `BoardCommandPath`：`/usr/bin:/bin`；v5.6.4 的三个业务命令均安装在 `/usr/bin`，不再搜索 `client-demo`。
- `ApiBaseUrl`：`http://127.0.0.1:5080`。
- `LocalDataRoot`：空值会解析为当前用户的 `%LOCALAPPDATA%\MosquitoCapture`。
- 默认固定焦距：500；命令超时：180 秒。

当前程序只加载 `appsettings.json`；虽然 `.gitignore` 排除了 `appsettings.Local.json`，代码尚未实现该文件的覆盖加载。
需要修改本机配置时编辑实际加载的文件，避免把个人地址或凭据推送远端。配置文件不存放密码。

普通启动会把生产 `appsettings.json` 复制到输出目录；配置文件缺失时 `AppSettings` 的代码默认值也为 `/usr/bin:/bin`。真实 WPF runner 使用独立配置，但路径值与普通启动一致。

### 2026-09-08 历史快照（v5.6.3 联调前）

以下结果保留为历史证据，不代表当前已烧录系统：

- `adb devices -l`：目标开发板为 `device`。
- `mosquito-version`：`dev-v5.6.3-usb0-adb-autostart`，2026-09-04，板级包 `2.17-1`，镜像声明照片元数据版本 2。
- `/data/local/tmp/client-demo/` 下检查的 `mosquito-capture`、`mosquito-environment`、`mosquito-power` 均不存在。
- `GET http://127.0.0.1:5080/health` 返回 HTTP 200，`status=ok`、`environment=Development`、`repository=InMemory`、`storage=Local`。

当时客户端要求 metadata v3 和对应采集命令，而 ADB 在线并不代表一键采集可用；当时尚未部署成品板端命令、未拍照、未运行上传集成测试。
API 是开发实例，InMemory 数据会随服务退出丢失；健康检查通过不等于登录和上传已验收。

### 2026-09-09 v5.6.4 当前实板结果

- `mosquito-version`：`dev-v5.6.4-client-direct`，板级包 `2.18-1`，镜像声明 metadata v3。
- `mosquito-capture`、`mosquito-environment`、`mosquito-power` 均精确解析到 `/usr/bin/`；`BoardCommandPath=/usr/bin:/bin`，不依赖 `/data/local/tmp/client-demo`。
- 真实 WPF 通过 `LoginButton`、`DetectButton`、`ReadDeviceButton`、`CaptureButton` 完成检测、传感器 WARN 展示、手动焦距 500 采集、两次 pull、SQLite PendingUpload、历史和 3264×2448 预览，主机退出码 0。
- Windows USB FriendlyName 仍带 v5.6.2 文本，`DeviceId` 仍待配置；运行时版本和直连功能已由 `mosquito-version`、metadata 及真实采集独立确认。
- 自动对焦光学效果仍延期；真实云端认证和上传不在本次直连验收范围。

## 验证结果与板端同步

- Windows Release 全解决方案构建通过：0 警告、0 错误。
- 核心测试通过：metadata v3/UUID、传感器单位、SQLite 队列状态、UTF-8 CSV。
- 修复 Windows 测试收尾：在删除临时目录前调用 `SqliteConnection.ClearAllPools()`，释放连接池保留的数据库句柄；生产数据库逻辑未改。
- Windows x64 自包含发布成功。
- 2026-09-08 历史启动检查：发布程序创建“病媒生物现场采集平台”主窗口，独立测试目录中 SQLite 初始化成功，关闭窗口后退出码为 0；当时尚未做完整 UI 交互验收。2026-09-09 的真实 WPF 结果见上节。
- 本机原先运行的其他客户端实例未关闭或替换。

以下 Git 数量也是 2026-09-08 历史快照：当时 WSL 板端 Git 为 `mosquito-t113` / `bb7b92a`，有 6 个已跟踪文件修改、5 个未跟踪文件，暂存区为空；未提交内容包括 2.18-1、DHT30/BQ25895、metadata v3、采集/4G 候选和历史文档。v5.6.3 自动 ADB 冷启动当时已通过 3/3，但完整相机、4G、热插拔回归尚未全部完成。
旧文档关于客户端尚未推送、源码仅放 WSL 的描述已落后于实际 Git 和本次 Windows 迁移决定。
本轮可靠迁移结论已记录于本文件；没有以旧聊天结论替代实板或 Git 检查。

## 2026-09-07 历史与上报界面增量

以上为迁移时检查记录。后续本轮已实现双模式工作区、历史查询与上报检查客户端，具体范围见 `CLIENT_REQUIREMENTS.md`，新云端契约见 `HISTORY_REPORT_API.md`。

- 增加 `dev.ps1 UiTest` 和 WPF 模拟测试项目，可复现截图库 `artifacts/ui-check/`。
- 核心测试扩展至 553 条分页 / 整体 CSV、北京时间边界、计划和 15 分钟宽限一致性、迟到去重；测试异常捕获后返回非零，不再放任测试异常弹出系统对话框。
- UI 测试通过模式与导航状态、乱序详情请求、断网保留、关联记录跳转、离线采集落盘；WPF 绑定检查通过。截图使用模拟数据，不构成实板结果。
- 客户端不自动部署板端命令；本条记录的“命令缺失”是 2026-09-08 历史状态。当前 v5.6.4 已由板端镜像提供三个 `/usr/bin` 原生命令。

## 2026-09-07 统一登录、紧凑顶部与设备检测

- 窗口与页头名称改为“智能诱蚊诱卵采集平台”。原独立模式卡片行移除，登录后在顶部切换模式；最小窗口 1180×800，右侧信息与检测详情可滚动。
- 未登录界面锁定，启动不访问板子或加载业务记录。`admin / 000` 为本阶段工程师入口，可以操作真实直连设备；其他账号使用 `api/auth/login`。真实云端账号尚未配置，不把模拟认证测试当成云端联调。
- 云端会话记录过期时间，401 或到期锁定工作区。退出清空界面和口令控件、令牌，取消查询，丢弃旧响应，保留本机记录；临时网络失败保留未过期会话。
- 直连“检测设备”与登录 / 模式切换 / USB 通知复用同一检测服务。Windows SetupAPI 枚举 USB 与 WinUSB，无需 PowerShell 或 ADB 来发现 USB；本机实测在普通受限测试进程中成功读取。
- 检测仅调用 ADB version、devices、只读 shell 的版本及命令存在性查询；不拍照、不读取实时传感器、不部署命令、不重启板子。
- 新增登录、检测与取消、过期、延迟响应、工程师采集落盘、退出保留记录、最小窗口布局回归；与原有历史记录 / 上报检查测试一起执行。截图和测试结果位于 `artifacts/ui-check/`，均使用模拟数据。
- 2026-09-08 当时的只读结果：ADB 1.0.41、USB 与 WinUSB 正常，实际镜像为 `dev-v5.6.3-usb0-adb-autostart`，三个命令当时缺失，界面正确显示“已连接 · 采集接口未就绪”。当前 v5.6.4 状态以上方 2026-09-09 实板结果为准。
- 当时的只读检测记录为 `artifacts/device-check/read-only-detection.json`，不能证明拍照链路；2026-09-09 已另行完成 v5.6.4 真实直连拍照回归，云端认证 / 上传仍未纳入该验收。
- Windows x64 自包含发布输出仍为 `artifacts/publish/win-x64/MosquitoCapture.exe`，需要整个目录；本轮未制作客户安装器或打包 ADB。客户一键安装与自动部署通信组件已经作为正式需求记录于 `CLIENT_REQUIREMENTS.md`。

开发辅助：默认 `UiTest` 完全使用模拟设备；只有显式传入下面参数才调用实板只读检测。在执行过 `scripts/Enter-Dev.ps1` 的开发终端中运行：

```powershell
dotnet run --project tests/Mosquito.Client.UiTests -c Release -- --detect-device src/Mosquito.Client/appsettings.json artifacts/device-check/read-only-detection.json
```

检测进程退出码 0 表示检查报告已生成，设备是否就绪以报告各分项为准。WSL 本轮保持 `mosquito-t113 / bb7b92a`，与本地远端追踪记录一致，6 个已跟踪修改、5 个未跟踪文件和空暂存区均保留；没有提交、推送或修改板端仓库。

## 2026-09-07 记录入口简化

- 删除直连和远程工作区的“全部记录”按钮及对应通用跳转命令，提示文案改为从顶部“记录查询”进入。
- Release 构建与现有 WPF 回归通过，截图更新于 `artifacts/ui-check/`。进一步布局调整仍在讨论，未实施。
- 因原 `artifacts/publish/win-x64/` 程序正在运行，本次可查看版本单独生成于 `artifacts/publish/win-x64-record-entry/MosquitoCapture.exe`。用户可关闭旧版后打开此版本；本次没有关闭现有进程。

## 2026-09-08 工作区布局调整

- 实现两栏布局调整：直连左侧为单一主图和放大 / 保存入口，右侧为设备状态、采集操作和可滚动的本次采集结果。上传待传记录保留为次要操作；“全部记录”不恢复，历史统一从顶部进入。
- 采集结果用四个数据格显示温度、湿度、电池电压和充电状态，保留时间、记录状态、保存 / 接收状态；完整记录折叠显示。实时读取结果单独展开，不修改照片的采集快照。
- 远程地图页保留右侧预览；左侧切到采集影像时隐藏重复预览，把照片操作移至左侧。设备选择、GNSS 和异常提示固定在上方，地图 / 影像切换复用已加载内容。
- Release 解决方案构建通过，0 警告、0 错误；WPF 回归通过，包括登录与设备检测、历史分页与上报检查、乱序请求保护，以及新增的单张可见照片、地图 / 影像选择保留、最小窗口长详情滚动时常用按钮保持可见检查。没有改动核心采集或传输协议。
- 已检查 1500×960 和 1180×800 渲染截图。`artifacts/ui-check/` 中的 `direct-workspace.png`、`direct-minimum-window.png`、`direct-details-minimum.png`、`direct-detection-minimum.png`、`remote-workspace.png`、`remote-image-workspace.png`、`remote-image-minimum.png`、`remote-map-minimum.png` 使用模拟记录与测试影像，不代表实板采集结果。
- 新的 Windows x64 自包含版本位于 `artifacts/publish/win-x64-workspace-layout/MosquitoCapture.exe`，运行时需保留整个输出目录。本次不覆盖先前发布目录、不关闭用户正在运行的程序，也未制作客户安装器。
- 2026-09-08 历史快照：开始前曾只读同步 WSL `mosquito-t113 / bb7b92a`，并保留当时 6 个已跟踪修改、5 个未跟踪文件和空暂存区；当时发现的 metadata v3 命令缺失后来已由 v5.6.4 三个 `/usr/bin` 原生命令解决。真实云端认证 / 上传仍未纳入直连验收。
