# Mosquito Windows Capture Client

面向疾控中心和卫健部门现场人员的 Windows 10/11 桌面客户端。界面采用浅色政务医疗工作台风格，深蓝用于主导航和可信操作，青绿色用于采集主按钮与正常状态。

## 主要能力

- 固定目标设备序列号，拒绝误连其他 ADB 设备；
- 一键完成相机上电、焦距锁定、3264×2448 拍照、拍前温湿度/电源采样、拍后断电；
- 校验元数据版本、采集 UUID、照片长度、SHA-256 和 JPEG 尺寸；
- 统一登录入口：工程师本地账号或真实云端认证，登录前工作区锁定；
- 登录后顶部显示紧凑的直连 / 远程模式按钮，直连先保存本机，云端上传为独立操作；
- 直连一键检测 ADB、Windows USB / WinUSB、板端响应和接口；登录及热插拔自动检查；
- 远程模式查看每小时自动上报的记录，暂不提供主动拍照、唤醒或读取控制；
- 传感器失败时保留照片并显示“部分完成”，上传失败单独说明；
- 只显示电池电压和充电状态，不伪造电量百分比；
- SQLite 本地上传队列，失败最多重试 5 次；
- 本机 / 云端历史记录、设备编号与北京时间日期筛选、50 条分页、自动详情、原图缩放和保存；
- CSV 导出整个已查询的完整快照；旧接口达到 500 条时提示不完整并暂停导出；
- 远程上报检查和静默的超时提示，15 分钟宽限，依赖服务端已确认的计划与时段结果；
- 上海行政区示意地图和设备目录接口，未知位置或坐标系不会生成虚构点位。

本软件只采集现场环境和设备数据，不设计患者、个人身份或健康信息字段。

最新范围见 [客户端需求记录](docs/CLIENT_REQUIREMENTS.md)。历史与上报检查已实现客户端部分，新增云端能力的契约见 [历史与上报接口约定](docs/HISTORY_REPORT_API.md)；当前仓库不部署云端，也不修改板端镜像。

## Windows 开发与构建

客户端现在以 Windows 本地工作树为主要开发位置，使用 Windows Git 跟踪
`origin/mosquito-windows-client`。当前目录为 `C:\project\mosquito\mosquito-windows-client`。
WSL 板端仓库仍保持只读边界，不要让两个环境同时修改客户端的不同副本。

在客户端根目录打开 PowerShell：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Setup
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Build
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Test
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 UiTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 Run
```

`Setup` 按 `global.json` 安装项目内 Windows x64 .NET SDK 并还原 NuGet 包。
SDK、包缓存和 CLI 状态位于被 Git 忽略的 `.tools/`；不依赖全局 `dotnet` PATH。
脚本来源与参数见 [微软安装脚本说明](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)。
`ExecutionPolicy Bypass` 只用于这次 PowerShell 进程，不修改系统执行策略。

`Check` 检查 SDK 和 ADB；`Publish` 生成自包含程序
`artifacts\publish\win-x64\MosquitoCapture.exe`。IDE 可打开 `Mosquito.Client.sln`。
`UiTest` 用模拟数据在屏幕外运行 WPF 交互检查并生成 `artifacts\ui-check\` 截图，不连接开发板或真实云端。
详细环境、验证结果与联调前提见 [Windows 开发说明](docs/WINDOWS_DEVELOPMENT.md)。

## 配置

`src/Mosquito.Client/appsettings.json` 包含：

- Windows ADB 路径；
- 唯一允许的开发板序列号；
- `DeviceId`：登记后的稳定设备编号，直连与远程必须使用同一编号；未配置时显示待确认，不编造编号；
- 板端命令搜索路径，当前固定为 `/usr/bin:/bin`；v5.6.4 的采集、环境和电源命令均从 `/usr/bin` 运行；
- 云端 API 地址；
- 本地照片、元数据和 SQLite 队列目录；
- ADB 超时和默认焦距。

普通启动会把该配置复制到程序输出目录；配置缺失时的代码默认值同样是 `/usr/bin:/bin`。客户端不再搜索已删除的 `/data/local/tmp/client-demo`。配置文件不保存云端密码。操作员每次启动后在界面顶部登录。生产版应进一步接入单位统一身份认证，并将令牌保存到 Windows Credential Manager。

当前工程师入口为 `admin / 000`，可在断网时操作真实直连板子。其他账号走配置的云端 API；工程师会话不能访问真实云端数据。首次输入框为空，未登录不能检测、采集或查询。正式客户版工程师凭据管理待发布阶段确定。

当前可运行程序仍按整个发布目录分发；客户一键安装并自动部署 ADB、依赖及驱动处理已列入需求，安装包尚未制作。

## 当前演示边界

- 直连已在 `dev-v5.6.4-client-direct` / 板级包 `2.18-1` / metadata v3 成品镜像上完成真实 WPF 回归，三个业务命令均为 `/usr/bin` 原生命令；
- Windows USB FriendlyName 仍带 v5.6.2 文本，稳定 `DeviceId` 仍待登记；运行时镜像身份以 `mosquito-version` 和 metadata 为准；
- 开发板 4G 直传需要正式 HTTPS API、板端设备密钥和 4G 联网后才能实测；
- 真实阿里云部署需要企业账号、域名、ICP 备案、私有 OSS、PostgreSQL 和 ECS RAM Role；
- 对外发布前应完成代码签名、安装包、杀毒软件兼容性及客户单位终端策略测试。
