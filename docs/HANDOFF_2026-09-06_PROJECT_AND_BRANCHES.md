# Mosquito 项目交接：2026-09-06 项目、分支与开发边界

更新时间：2026-09-06（Asia/Shanghai）

> **历史快照**：本文保留 2026-09-06 当日状态，不再作为当前分支和版本入口。2026-09-09 的权威事实见根目录 `AGENTS.md`、`docs/CURRENT_STATE.md` 和 `docs/NEXT_IMAGE.md`：板端使用 `mosquito-t113`，Windows 客户端使用同一 GitHub 仓库中的 `mosquito-windows-client` 分支；客户端权威工作树为 `C:\project\mosquito\mosquito-windows-client`。

新对话接手时，先阅读根目录 `AGENTS.md`，再阅读本文、`docs/CURRENT_STATE.md` 和 `docs/NEXT_IMAGE.md`。

## 1. 当前仓库与分支

板端仓库路径：`/home/janelinux/work/mosquito/mosquito-t113/`。

- 远程：`https://github.com/skyjane1129-blip/mosquito-t113.git`
- 当前分支：`mosquito-t113`
- 当前提交：`66f8915 feat(t113): add validated v5.6.3 automatic ADB startup`
- 远程分支：`origin/mosquito-t113` 已推送，并指向 `66f8915`
- `main` 基线：`cdf51dd`
- GitHub 远程分支 `docs/wsl2-sdk-migration` 已删除，本地旧远程跟踪引用也已清理

`mosquito-cloud-service` 和 `mosquito-windows-client` 目前只是本地初始化的 Git 仓库：只有本地 `main`，没有提交，也没有配置 `origin`。

## 2. v5.6.3 成品状态

v5.6.3 定义为 v5.6.2 的 `mosquito-board-test 2.17-1` 基线，加上 late userspace 自动调用既有 `adb-on`。

成品目录：`releases/mosquito-t113/2026-09-04-dev-v5.6.3-usb0-adb-autostart/`。

- 文件：`mosquito-t113-dev-v5.6.3-usb0-adb-autostart.img`
- 大小：`11,508,736` bytes
- SHA-256：`7f763d7795c9c0f4a8e3be5e904ec454f39db73cb34eb603af5f45bc587568a7`
- 板级包：`2.17-1`
- 源码状态：`auto-adb-rc-final-cdf51dd`

成品包含 `/etc/init.d/rc.final`，等待 `/overlay`、`/mnt/UDISK`、ADB 工具和 UDC 就绪后只调用一次既有 `adb-on`。不包含 2.18-1、看门狗、`adb reboot` 修复、GNSS、生产云和安全策略候选。

实板状态：

- 用户已经烧录精确 v5.6.3；
- 自动 ADB 冷启动 3/3 通过；
- Windows 显示 `MOSQUITO-T113-DEV device`；
- `mosquito-version` 显示 v5.6.3、2026-09-04、2.17-1；
- `/proc/self/status` 显示 `Uid: 0 0 0 0`；
- 摄像头、4G、热插拔和完整功能回归尚未全部完成；
- 暂时不要执行 `adb reboot`，本版本没有加入看门狗修复。

构建使用低并行 `make -j1` 和日志重定向；Tina 构建、官方 `pack`、SquashFS 反查和 SHA-256 均通过。日志在 `build/firmware-runs/20260904-v563-adb-autostart/`。

## 3. 分支策略

- `mosquito-t113`：稳定板端主线，放 v5.6.3 自动 ADB、2.17-1，以及以后已验证的板级包和系统镜像改动。
- `feature/board-package`：未验证的 2.18-1、DHT30、BQ25895、采集和 4G 候选。
- `mosquito-cloud-service` 的独立分支：云端 API、数据库、OSS 和生产配置。
- `mosquito-windows-client` 的独立分支：Windows 客户端改动。
- `integration`：各组件分别验证后，再做端到端联调。

目前只有 `mosquito-t113` 已创建并推送。其余分支暂未创建，不要自动创建或推送。

## 4. 尚未提交的候选改动

### 4.1 2.18-1 板级包候选

当前工作区保留 Makefile 2.18-1、`jsonfilter` 依赖、`mosquito-environment`、`mosquito-power`、DHT30/BQ25895 机器可读输出、metadata 3 和拍照前环境/电源采样。这些内容没有进入 v5.6.3，属于以后 `feature/board-package` 的候选。

### 4.2 采集和 4G 上传候选

未跟踪文件包括 `mosquito-capture`、`mosquito-capture-4g`、`mosquito-upload-4g` 和 `mosquito-cloud.conf.example`。这些功能需要真实 Air780EG、HTTPS API、设备密钥和失败恢复测试，不能直接并入稳定板端分支。

其他未提交内容还包括 WSL2 文档、v5.6.2 README、生产待办文档和客户端/云端候选改动。不要用 `git reset --hard`、`git checkout --` 或批量清理删除它们。

## 5. WSL Agent 与 Windows Agent 分工

WSL Agent 负责 WSL2 ext4 中的源码、Git、Linux/.NET 测试、构建和提交。Windows Agent 负责 Windows 客户端运行、GUI 调试、ADB/UART/USB、安装包、签名和最终验收。不要让两个 Agent 同时修改两个独立副本。

## 6. 新对话下一步边界

1. 先执行 `git status --short --branch`，不要清理未提交候选。
2. 确认当前分支为 `mosquito-t113`，远程跟踪为 `origin/mosquito-t113`。
3. 不把 2.18-1、采集、4G、云端候选提交到稳定分支。
4. 如果创建 `feature/board-package`，先从稳定提交建立，再选择性转移候选改动。
5. 未经明确授权，不创建 `integration` 或云端/Windows 远程仓库。
6. 未经明确授权，不制作包含 2.18-1 的新镜像。
7. 构建继续使用低并行和重定向日志，避免 WSL 终端高负载卡顿。
8. 自动 ADB 已完成 3/3，后续优先做热插拔、摄像头和 4G 关键路径回归。
9. `.img` 不加入 Git 源码提交，通过 release 目录或正式 Release 附件管理。

## 7. 常用核对命令

在 `mosquito-t113` 中使用 `git status --short --branch`、`git log -1 --oneline --decorate`、`git remote -v` 和 `git branch -r` 检查状态。Windows 验收使用 `adb devices -l`、`adb shell mosquito-version` 和 `adb shell cat /proc/self/status`。

## 8. 当前结论

- `mosquito-t113` 已推送，代表 v5.6.3 自动 ADB + 2.17-1 已验证基线。
- 2.18-1、采集、4G 和云端候选仍未验证、未提交到稳定分支。
- `docs/wsl2-sdk-migration` 远程分支已删除。
- 本地候选文件不等于已经进入 GitHub。
- 没有新的明确授权时，继续 v5.6.3 实板回归，不制作候选镜像。
