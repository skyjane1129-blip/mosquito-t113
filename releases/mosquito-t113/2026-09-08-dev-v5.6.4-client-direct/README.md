# dev-v5.6.4-client-direct

状态：**已烧录精确成品；板端直连子集与 Windows WPF 实板闭环通过。三次物理冷启动/UART、USB 物理热拔插和 UART 条件下的 ADB stop/start 仍待补。**

## 身份

```text
MOSQUITO_IMAGE=dev-v5.6.4-client-direct
MOSQUITO_BUILD_DATE=2026-09-08
MOSQUITO_BOARD_PACKAGE=2.18-1
MOSQUITO_SOURCE_STATE=client-direct-bb7b92a-worktree
MOSQUITO_CAMERA_MODES=camera-test0,camera-test,camera-test1,camera-test1-focus
MOSQUITO_PHOTO_METADATA=3
```

| 项目 | 值 |
| --- | --- |
| 文件 | `mosquito-t113-dev-v5.6.4-client-direct.img` |
| 大小 | 11,508,736 bytes |
| SHA-256 | `01f95c8008a0f3957ecaf8f4f9fd5f4bb92ad3cd4c0c0dddc27e95e53a0e251b` |
| rootfs 大小 | 4,718,592 bytes |
| rootfs SHA-256 | `42922be01976e488cc1b1d32ab83ed151fa2c555b65fee172a8ebd85caa9fbb9` |
| Git 基线 | 分支 `mosquito-t113`，HEAD `bb7b92acf817c7dac3007298233bff6d3d24ff03`，带已盘点的未提交工作区 |
| 构建证据 | `build/firmware-runs/20260908-v564-client-direct/` |

## 2026-09-09 烧录后实板结果

运行时 `mosquito-version`、板级包、metadata 版本、source state、原生命令路径及关键文件哈希均与本 release 一致。`/data/local/tmp/client-demo` 不存在。

板端结果为 `PASS_BOARD_DIRECT_SUBSET`：

- environment 10/10 PASS、CRC 通过；
- power 10/10 可读，9 PASS、1 WARN，告警为 `POWER_FAULT_ACTIVE / FAULT_REG=0x80`；
- 手动焦距 500 三次均 COMPLETE，metadata v3、UUID、唯一 marker、实际 3264×2448、字节数/SHA-256和同次传感器关联全部通过；
- 三组 COMPLETE 加一组受控 PARTIAL 共 8 个拉回文件与远端哈希一致；ADB push/pull 往返 `cmp` 通过；
- 非法 UUID 返回远端退出码 2；受控传感器失败保留照片和 metadata，状态 PARTIAL、远端退出码 10；
- 拍后 `/dev/video0`、相机锁和 `.part` 均不存在，临时故障注入目录已删除。

板端原始命令、stdout/stderr、host/remote 退出码、哈希和报告见 `build/test-runs/20260909-105100+0800-v564-exact-image-board/`。

Windows 最新 WPF 客户端直接针对该精确成品通过真实 Login、Detect、Read、Capture 按钮完成回归，进程退出码 0。采集 ID 为 `74dd2db1-5f7d-485e-96bb-8a2428d17cec`；客户端确认 `/usr/bin` 命令、实时电源 WARN、手动焦距 500、两次 ADB pull、严格文件校验、SQLite `PendingUpload`、历史及当前/历史预览。证据见 `build/test-runs/20260909-111101+0800-v5.6.4-windows-real/`。

随后审计发现客户端普通启动配置仍优先搜索历史临时目录。Windows Agent 已把生产配置和代码默认值统一改为 `/usr/bin:/bin`；Release 构建、Core、模拟 WPF 和使用生产配置原文件的只读实板检测均以退出码 0 通过，后者确认当前 v5.6.4 已连接且采集接口就绪。73 个证据文件的哈希校验通过，见 `build/test-runs/20260909-115342+0800-windows-command-path/`。旧的客户端生成/发布目录可能仍含历史配置，分发时必须重新生成。

## 目的与范围

本版本把已经在 v5.6.3 当前镜像临时路径完成实板验证、并由 Windows WPF 客户端真实按钮联调通过的直连子集正式放入 `/usr/bin`：

- `mosquito-capture --id UUID --focus 1..1023|auto`；
- `mosquito-environment --machine`；
- `mosquito-power --machine`；
- metadata v3，包括 UUID、照片 SHA-256/字节数/尺寸、焦距字段以及同次环境和电源数据；
- 修复 `mosquito-capture` 重复输出 `PHOTO`、`METADATA` marker 的问题。

Windows 最新源码在旧 v5.6.3 成品加临时候选路径上完成真实 WPF 回归，结果为 `PASS`、进程退出码 0。证据目录为 `/mnt/c/project/mosquito/mosquito-windows-client/artifacts/real-device-20260908-172947/`，采集 ID 为 `70e9e657-0f6e-484e-b0d0-ec49d199d140`。该次手动焦距 500 采集生成 3264×2448 JPEG，客户端完成远端退出码解析、电源 WARN 展示、ADB pull、metadata/UUID/SHA/尺寸/焦距校验、SQLite 历史和当前/历史预览。

上述 v5.6.3 证据是构建前接口依据；2026-09-09 已由本 README 前节的 v5.6.4 精确成品结果取代为烧录后证据。

## 未纳入内容

本次采用精确文件白名单，没有递归复制整个工作区。以下新增候选没有进入镜像：

- `mosquito-capture-4g`；
- `mosquito-upload-4g`；
- `mosquito-cloud.conf.example`；
- 板级包新加的 `+jsonfilter` 依赖；
- 真实云 API、设备密钥、OSS 和远程控制；
- 看门狗/`adb reboot` 修复、GNSS、CPUFreq、CPUIdle、EA3056。

`jsonfilter` 自初始板型 defconfig 起就是既有包，本镜像仍保留该基线文件；它不是由本次板级包依赖新增。原有 `4g-start`、`4g-stop` 和 `photo-upload` 也保持 v5.6.3 基线，本次没有把它们声明为新验证功能。

## 构建与静态验证

- `package/mosquito-board-test/clean`：退出码 0；
- `package/mosquito-board-test/compile`：退出码 0，产出 `mosquito-board-test_2.18-1_sunxi.ipk`，SHA-256 为 `7e43a36b55c17a44e2ebdc9b1027c8486afaa4c05f56cafdd87105ba00a2a5ba`；
- 完整 `make -j1 V=s`：退出码 0；
- 沙箱内第一次 `pack`：退出码 1，厂商 `fsbuild` 被沙箱拦截并报告 `Bad system call`；该失败及旧输出均保留，未冒充新成品；
- 相同输入在授权的外层环境运行官方 `pack`：退出码 0，日志以 `Dragon execute image.cfg SUCCESS` 和 `pack finish` 结束；
- 最终整卡镜像只有一个 SquashFS magic，offset 为 6,789,120；提取 4,718,592 bytes 后与构建 `rootfs.img` 的 `cmp` 返回 0，二者 SHA-256 相同；
- 最终 rootfs 中三条新命令、版本文件、metadata v3、0755 权限及命令软链均通过反查；新增 4G/云脚本不存在；
- 最终 rootfs 与 v5.6.3 rootfs 的文件级内容/类型/权限对比只有 11 个允许路径发生变化：三个新命令、两个已有直连实现、版本文件、包清单以及构建时间元数据；没有额外文件差异；
- `rc.final` 和 `usb0-adb-start` 的 SHA-256 与 v5.6.3 完全一致；内核配置源也与 v5.6.3 一致，Watchdog、CPUFreq、CPUIdle 仍未启用；
- 凭据扫描对 API key、云 API 地址、私钥和常见云访问密钥模式为 0 命中。

## 烧录

先在本目录校验：

```powershell
Get-FileHash .\mosquito-t113-dev-v5.6.4-client-direct.img -Algorithm SHA256
```

结果必须是：

```text
01f95c8008a0f3957ecaf8f4f9fd5f4bb92ad3cd4c0c0dddc27e95e53a0e251b
```

使用 PhoenixCard 的“启动卡 / Startup”模式写入 TF 卡。不要使用 Etcher 或普通 `dd`；写卡前再次核对目标盘符并备份卡中数据。

## 烧录后回归进度

1. **待补**：连续 3 次物理冷启动，保存 UART，并验证自动 ADB、唯一设备、root 身份和本版本的 `mosquito-version`。
2. **通过**：三个新命令来自 `/usr/bin`，不使用 `/data/local/tmp/client-demo`。
3. **通过**：environment 和 power 各 10 次，完整保存机器输出、远端退出码和 WARN 分布。
4. **通过**：手动焦距 500 三次，唯一 marker、UUID、metadata v3、3264×2448、字节数/SHA-256、同次传感器和 ADB pull 均通过。
5. **通过**：Windows WPF 成品界面的检测、读取、手动采集、保存、SQLite、历史、预览和电源 WARN。
6. **通过**：非法 UUID 远端退出码 2；受控传感器失败为 PARTIAL、远端退出码 10，文件保留。
7. **通过**：拍摄后 `/dev/video0`、`/var/run/mosquito-camera.lock` 和 `.part` 均不存在。
8. **部分通过**：shell、push、pull 通过；USB0 物理热拔插和 ADB stop/start 待补。stop/start 因系统没有 `nohup`/`setsid`，无法保证当前 ADB 被停止后自动恢复，故未冒险执行；需在 UART Root Shell 在场时测试。

## 已知限制

- 相机尚未机械固定。自动模式的命令、控制和文件协议保留，但光学清晰度按用户决定暂列待验；本版本不声明自动对焦画质通过。
- Windows USB FriendlyName 仍显示历史字符串 `Mosquito T113 v5.6.2 adb-on validated`；运行时 `mosquito-version`、metadata 和命令哈希均确认实际为 v5.6.4。稳定 `DeviceId` 仍待配置。
- 板端 wall clock 仍无效；本轮照片通过 UUID、同一 sidecar、monotonic uptime 和 Windows 采集时间关联。
- 当前 root、无认证、自动启动的 ADB 只适合受控开发环境。
- `adb reboot` 的已知停机问题没有纳入本次修复；烧录回归使用物理断电/上电或 Reset，并保留 UART 证据。
- 云端和新增 4G 直传继续延期。
- 镜像构建时的源码输入来自已盘点工作区；完整 diff、工作区快照、输入哈希、命令日志和退出码均保存在构建证据目录。后续 Git 整理不会改变本镜像的身份或已经记录的输入哈希。
