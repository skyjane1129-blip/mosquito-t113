# Mosquito 下一镜像计划

更新时间：2026-09-10（Asia/Shanghai）

## 0. 2026-09-09 v5.6.4 烧录后最新决定

用户已经烧录 `dev-v5.6.4-client-direct`。运行身份、2.18-1、metadata v3、source state、原生 `/usr/bin` 命令及关键文件哈希均与发布镜像一致，不依赖 `/data/local/tmp/client-demo`。

发布镜像仍为：

```text
releases/mosquito-t113/2026-09-08-dev-v5.6.4-client-direct/mosquito-t113-dev-v5.6.4-client-direct.img
size=11,508,736 bytes
sha256=01f95c8008a0f3957ecaf8f4f9fd5f4bb92ad3cd4c0c0dddc27e95e53a0e251b
rootfs_sha256=42922be01976e488cc1b1d32ab83ed151fa2c555b65fee172a8ebd85caa9fbb9
```

本镜像只新增本阶段已验证的直连功能：`mosquito-capture`、`mosquito-environment`、`mosquito-power`、相机实现、板测 C 程序、2.18-1 包装和 metadata v3 版本声明。新增 `mosquito-capture-4g`、`mosquito-upload-4g`、云配置、板级包新增 `jsonfilter` 依赖以及 Watchdog/GNSS/CPUFreq/CPUIdle 均排除；云端和新增 4G 直传继续延期。

**当前状态是精确成品已烧录，板端直连子集、Windows WPF 回归和客户端生产命令路径修正均通过。** 已完成环境/电源 10 次、手动焦距 500 三次、metadata/哈希/传感器关联、ADB pull/push、非法 UUID、受控 `PARTIAL`、资源清理和真实 GUI 全流程；普通启动配置与代码默认值均已改为 `/usr/bin:/bin`，生产配置只读实板检测通过。旧的客户端生成/发布目录不得直接分发，应从修正后的源码重新生成。下一步只补 3 次物理冷启动与 UART、USB0 物理热拔插/正反插，以及 UART Root Shell 在场时的 ADB stop/start。自动对焦光学画质继续等相机固定；云端、远程和新增 4G 仍延期。本阶段没有依据继续制作下一个镜像。下文 `0A` 及后续章节保留构建前计划和历史背景；冲突时以本节为准。

2026-09-10 在该精确成品上完成了一次临时 Air780EG 单基站 LBS 实测：驻网、数据附着、官方 LBS 端点和一次 `CIPGSMLOC` 查询均通过，承载清理完成。测试程序和私有结果只在 `build/test-runs/20260910-lbs-O1kvSt/` 与板端 `/tmp` 临时目录中，不属于本镜像内容。下一镜像不得据此自动加入 LBS、地图或云端上传；若产品化，需另行确定位置上报协议、隐私与凭据、刷新周期、误差展示和端到端地图验收。

## 0B. 2026-09-10 远程模式演示后的下一镜像候选（待用户决定）

2026-09-10 远程模式端到端已在精确 v5.6.4 成品上以临时部署通过（见 `docs/CURRENT_STATE.md` 第 0C 节）。按用户要求，所有功能与客户端/服务端联动测试完成后再询问是否制作镜像；本节只列出候选内容，不构成制作授权。

候选 `dev-v5.6.5-remote-demo` 相对 v5.6.4 的内容（均在 `test/lbs-temp-20260910` 工作区，未提交）：

- 板级包 `mosquito-board-test` 2.18 → 2.19：`DEPENDS` 增加 `+jsonfilter`；安装 `mosquito-lbs`（`src/mosquito-lbs.c`，`-lm`）、`mosquito-location`（`src/mosquito-location.c`，GNSS→Traccar，未实测通过，可按需排除）、`mosquito-capture-4g`、`mosquito-upload-4g`、`mosquito-remote-agent`、`mosquito-location-upload`、`/etc/mosquito/cloud.conf.example`、`/etc/mosquito/location.conf.example`。
- `rc.final` 新增后台块：`/etc/mosquito-cloud.conf` 含 `MOSQUITO_REMOTE_AGENT=1` 且无 `/overlay/mosquito-test/disable-remote-agent` 时，等 overlay/UDISK/`/dev/ttyS1`/工具就绪并延迟 20 秒后 `exec mosquito-remote-agent run`（日志 `/tmp/mosquito-remote-agent-boot.log`）。自动 ADB 块不变。
- 板端配置不进镜像：`/etc/mosquito-cloud.conf`（0600）需在烧录后通过 ADB/UART 写入（模板为 `cloud.conf.example`，演示值为公网 HTTP 地址、`MOSQUITO_ALLOW_HTTP=1`、设备密钥、`MOSQUITO_DEVICE_ID=MQ-SH-001`、`MOSQUITO_REMOTE_AGENT=1`），或由镜像制作时写入 overlay（需用户决定）。
- **2026-09-14 已在现有 v5.6.4 板子的可写覆盖层（overlayfs upper=/overlay/upper）上提前装入并冷启动验证了同一套内容**：`/usr/bin/{mosquito-remote-agent,mosquito-lbs,mosquito-upload-4g}`、`/etc/mosquito-cloud.conf`（0600，`MOSQUITO_REMOTE_AGENT=1`）、`rc.final` 末尾追加的远程代理自启动块（原文件备份 `/overlay/mosquito-test/rc.final.v564.orig`）。物理断电再上电一次：ADB 在开机 16 s 回来（不受影响），代理由 rc.final 于 22.7 s 拉起，PPP 37 s 建立，141 s 心跳被服务端接受，设备重新在线。这等于 v5.6.5 门槛中“冷启动确认 rc.final 自启代理且自动 ADB 不受影响”已在同一份代码上通过一次；烧录后仍需在精确成品上复验。板端 `/data/local/tmp/remote-demo/` 仍保留作为手动回退，之后更新脚本时两处都要同步。
- 需要同步到 SDK 包目录的文件：SDK 侧 `package/utils/mosquito-board-test/` 尚未同步以上 overlay 新文件；制作镜像前必须按 `AGENTS.md` 同步并做静态审计。

2026-09-11 增量（详见 `docs/LOCATION_AND_POWER_2026-09-11.md`，均在当前镜像临时部署实板验证、未提交）：
- `mosquito-lbs` 升级：`--locate` 额外用 `AT+CCED` 采服务小区+邻区并写入结果 `cells`；新增 `--gnss`（内置 GNSS + `AT+CGNSAID` AGNSS）与 `--at` 诊断。`--self-test` 已扩展并通过。服务端据小区做高德/百度多基站定位与逆方差/中值融合，新增 `GET /api/v2/devices/{id}/location-evaluation` 做精度对照（参考点仅在 `appsettings.Local.json`）。
- `mosquito-remote-agent` 升级：`MOSQUITO_LOCATION_MODE` 增加 `gnss`/`gnss-lbs`；新增 `MOSQUITO_POWER_PROFILE=always-on|duty` 低功耗占空比（duty 实测远程拍照仍成功）；09-14 新增操作员 `Learn` 指令（快速定位学习：重搜网+LBS 轮次，`MOSQUITO_LEARN_*`），`MOSQUITO_LOCATION_MIN_SEC` 建议 600（合宙实测限频≈10 min）。`cloud.conf.example` 已补全新键与注释。
- 修复：`rc.final` 与 `mosquito-remote-agent`/`mosquito-upload-4g` 的可执行位（曾被 Windows 写入重置为 644，已恢复 755）；代理在 `/var/run` 不存在时先 `mkdir -p` 再建锁（冷启动 PPP 未起时也能起代理）。
- 低功耗内核项（2026-09-14 只读调研结论，详见 `docs/LOCATION_AND_POWER_2026-09-11.md` 2c 节）：**CPUFreq、CPUIdle 不纳入**（CPU 已固定跑 720 MHz、VDD_CPU 固定 0.95 V，SDK 无适配驱动/板型已删 idle-states，收益小风险大）；**RTC 已由用户于 2026-09-14 批准纳入**：overlay `device/config/chips/t113/configs/mosquito/linux-5.4/config-5.4` 已把 `# CONFIG_RTC_CLASS is not set` 改为 `CONFIG_RTC_CLASS=y` + `CONFIG_RTC_DRV_SUNXI=y`（连同 HCTOSYS/SYSTOHC=rtc0、NVMEM、INTF_SYSFS/PROC/DEV 常规子项；驱动兼容 `allwinner,sun8iw20-rtc`，DTS 节点已带 `wakeup-source`）。这是本候选中唯一的内核配置改动，无法在当前镜像验证，烧录后必须验收：`/dev/rtc0` 存在、`hwclock -r` 可读、`echo +60 > /sys/class/rtc/rtc0/wakealarm && echo freeze > /sys/power/state` 60 s 后自动唤醒且 ADB/4G 恢复。**默认电源档位保持 `always-on`**（代理默认值与 `cloud.conf.example` 均为 always-on），RTC 不改变演示行为；`sleep` 档留待烧录后按放电实测决定。

制作前门槛（建议）：物理拔掉 Type-C 与串口后，由 ADB 手动启动的代理连续运行至少 1 小时并完成 ≥3 次远程拍照；拔线状态下客户端刷新可见心跳与位置；烧录后冷启动 1 次确认 `rc.final` 自动启动代理且自动 ADB 不受影响。若要合入 `--gnss` 定位，还需在天线修好后完成一次室外 GNSS 定位实测。

### 0B-final. v5.6.5 范围（2026-09-14 用户确认并批准；**镜像已于当晚制作完成，状态 STATIC_PASS / NOT_FLASHED**）

成品：`releases/mosquito-t113/2026-09-14-dev-v5.6.5-remote-learn/mosquito-t113-dev-v5.6.5-remote-learn.img`（11,654,144 bytes，SHA-256 `8ece11ae21bc3bd2b0513415e2f5a6fa407ee883f550709fff8ef88302f28fbb`），证据 `build/firmware-runs/20260914-v565-remote-learn/`，Windows 烧录副本 `C:\project\mosquito\firmware\`。烧录与验收步骤见发布目录 README。下面是制作前的范围表（已按此执行）：

镜像身份建议 `dev-v5.6.5-remote-learn`，板级包 2.19-1。相对 v5.6.4 的全部改动：

| # | 改动 | 状态 |
|---|---|---|
| 1 | 板级包 Makefile：2.18→2.19，`DEPENDS +jsonfilter`，安装 `mosquito-lbs`/`mosquito-location`（C）与 6 个脚本/模板 | 源码就绪 |
| 2 | `src/mosquito-lbs.c`：单基站 LBS + `AT+CCED` 邻区采集 + `--gnss`（AGNSS）+ `--at` 诊断，自检通过 | 当前镜像实板验证 |
| 3 | `files/mosquito-remote-agent`：心跳/长轮询/4G 上传/LBS；`MOSQUITO_LOCATION_MODE` lbs/gnss/gnss-lbs；`MOSQUITO_POWER_PROFILE` always-on/duty；`Learn` 快速定位学习指令；`/var/run` 冷启动修复；SIGHUP 忽略 | 当前镜像实板验证（含冷启动自启、duty、Learn） |
| 4 | `files/mosquito-upload-4g`、`mosquito-capture-4g`、`mosquito-location-upload`、`mosquito-cloud.conf.example`、`mosquito-location.conf.example` | 上传已实板验证；location-upload（Traccar）未验证，可按需排除 |
| 5 | `rc.final` 末尾远程代理自启动块（读 `/etc/mosquito-cloud.conf` 的 `MOSQUITO_REMOTE_AGENT=1`，可用 `/overlay/mosquito-test/disable-remote-agent` 禁用） | 已装入现有板子覆盖层并冷启动验证 |
| 6 | 内核 `config-5.4`：`CONFIG_RTC_CLASS=y`、`CONFIG_RTC_DRV_SUNXI=y` 及常规子项（唯一内核改动） | 用户批准；只能烧录后验收 |
| 7 | 可执行位修复：`rc.final`、`mosquito-remote-agent`、`mosquito-upload-4g` 均为 755 | 已修 |
| 8 | 不纳入：cpufreq、cpuidle、Watchdog/`adb reboot`、GNSS 硬件相关（二版板）、`/etc/mosquito-cloud.conf` 本体（烧录后经 ADB 写入） | 决定 |

默认行为：电源档 `always-on`，定位 `lbs`，定位最小间隔 600 s，学习 3 轮。制作流程照 `build/firmware-runs/20260908-v564-client-direct/BUILD_SUMMARY.md`：快照脏树 → 白名单同步到 SDK（含本次 config-5.4）→ package clean/compile → `make -j1 V=s` → 官方 `pack`（沙箱拦截时需授权外层执行）→ 容器/rootfs 反查审计 → 范围审计、凭据扫描 → 复制到 `releases/` 并生成 sha256 与 README。

## 0A. 2026-09-08 构建前历史快照（不可作为当前执行入口）

整体进度、代码问题及本地分支事实见 [`REVIEW_2026-09-08_PROGRESS.md`](REVIEW_2026-09-08_PROGRESS.md)。v5.6.3 精确成品已烧录并有自动 ADB 冷启动 3/3 记录；后文保留的“尚未烧录”等表述属于过期步骤，不应覆盖这一事实。2026-09-08 又在该精确成品上完成直连候选的临时实板测试，证据位于 `build/test-runs/20260908-144221+0800-client-direct/`。

- 直连候选已临时完成环境 10/10 `PASS`、电源 10/10 可读（8 `PASS` / 2 `WARN`）、固定焦距 3 次和自动锁焦 3 次采集、metadata v3/JPEG/UUID/传感器关联、12 个文件 ADB 拉取哈希以及单张照片 push/pull 往返哈希验证。带远端退出 marker 的第二组环境/电源 10 次补测得到相同的 10/10 和 8/2 分布。
- `mosquito-capture` 首轮重复输出 `PHOTO=`/`METADATA=`；板端候选已用最小过滤修复，并在重新部署后的手动 3 次、自动 3 次协议回归中确认每个 marker 恰好出现一次。受控故障注入另验证 `PARTIAL`、远端退出码 10 和数据保留。
- 构建前自动锁焦控制流程 3/3 完成稳定判断、关闭 AF、锁焦写入和回读，但 3/3 实际图像明显失焦；以 220 和 500 为起点的两次约 30 秒诊断均收敛到 280。此项后来按用户决定改为等待相机固定后验光学画质。
- 构建前确认 Windows `adb.exe shell` 不可靠透传板端命令退出码，并用带内 marker 取得 0、1、2、10。该适配和真实 WPF GUI 联调后来均已完成并通过。
- 当时还计划做 v5.6.3 热插拔及 4G/ADB 共存回归。最新直连范围已把新增 4G 延期，并在 GUI 通过后制作 v5.6.4；软件重启故障仍未纳入。
- 客户端/云端另有两个已在隔离测试复现的修复待办：上传部分成功后的逐对象幂等恢复；取消/进程中断后 `Uploading` 队列的恢复。现有测试通过不能代替这两项验收。
- 真实云联调还应验证 OSS 对象正文的完整性检查、签名上传及中断重试；当前仅比较自定义 SHA-256 元数据不能等同于计算正文哈希。
- 2.18-1、环境/电源机器接口、metadata v3 和新采集命令仍属于未集成候选；客户端演示临时部署不能代替干净成品验收。
- 本次核对仅补充进度文档，不构成新镜像、硬件修改、云部署、提交或推送授权。

## 1. v5.6.3 历史决定

**v5.6.3 已按用户明确授权制作，并完成精确成品自动 ADB 冷启动 3/3 验收。** 本版本严格采用 v5.6.2 的 `mosquito-board-test 2.17-1` 基线，只加入 userspace 就绪后的自动调用既有 `adb-on`。其后直连候选完成 Windows GUI 联调，自动光学按用户决定延期，并已经形成 v5.6.4 静态通过候选。

本次 v5.6.3 明确：

- 不把当前未提交的 `2.18-1` candidate、看门狗、`adb reboot` 修复、GNSS、生产云/安全改动放入镜像；
- 不把 v5.6.2 临时 overlay 的 3/3 通过结果写成精确 v5.6.3 成品已上板通过；
- 在用户确认 v5.6.3 通过前，不开始包含 `2.18-1` 的下一镜像集成。

v5.6.2 的历史成品 SHA-256 是：

```text
f0d24710f165122fe7539098c777d15f25c52d0b73874ac6a3ca421b38edaf08
```

v5.6.3 精确成品已生成，文件位于：

```text
releases/mosquito-t113/2026-09-04-dev-v5.6.3-usb0-adb-autostart/mosquito-t113-dev-v5.6.3-usb0-adb-autostart.img
size=11,508,736 bytes
sha256=7f763d7795c9c0f4a8e3be5e904ec454f39db73cb34eb603af5f45bc587568a7
```

它的身份是 `dev-v5.6.3-usb0-adb-autostart`、构建日期 `2026-09-04`、板级包 `2.17-1`，源码状态为 `auto-adb-rc-final-cdf51dd`。成品已通过低并行 `make -j1`、官方 `pack` 和 SquashFS 静态反查；用户已烧录并完成 3 次精确成品冷启动验收。

2026-09-01 晚间的临时晚期自动 ADB 回归还发现：自动启动本身在物理 Reset 后可达到 `adbd=1`、`ep0/ep1/ep2` 和 `UDC=configured`，但 `adb reboot` 会在 UART 报 `Reboot failed -- System halted` 并停死，900 秒内不会自行回来。当前 v5.6.2 因此不能把软件重启列为通过项。初步根因是运行内核未启用 `CONFIG_WATCHDOG`/`CONFIG_SUNXI_WATCHDOG`，候选修复需要单独批准；在批准前不改 overlay、不构建、不 `pack`、不烧录。

开发主机迁移已完成到“SDK 恢复和板测程序交叉编译可用”阶段：完整 SDK 位于 `/home/janelinux/work/mosquito/tina-t113`，当前占用约 15 GB。本轮已在受控低并行配置下完成完整 Tina `make -j1`、官方 `pack` 和成品静态反查；迁移步骤和边界见 `docs/WSL2_SDK_MIGRATION.md`。WSL 期间未出现 OOM、磁盘耗尽或 I/O 错误，后续仍应保持低并行和日志重定向。

## 2. v5.6.3 历史验收目标

验收需要继续回答一个问题：在不依赖临时 overlay 的情况下，v5.6.3 精确成品除自动 ADB 冷启动 3/3 已通过外，能否完成以下关键路径闭环：

```text
TF 冷启动
  -> userspace 就绪
  -> 自动调用一次 adb-on
  -> Windows ADB device/root shell
  -> push/pull
  -> 摄像头拍照与元数据
  -> 4G PPP/DNS/NTP/HTTPS
  -> 4G 与 ADB 共存
  -> 4g-stop
  -> 重启后自动 ADB 再次在线
```

验收结果必须来自本目录中的精确 v5.6.3 `.img`，不能用“v5.6.2 临时 overlay 已通过”代替。

2026-09-08 的临时部署已经证明：`mosquito-environment`、`mosquito-power`、`mosquito-capture`、metadata v3、唯一结果 marker、ADB 拉取和哈希闭环在当前 v5.6.3 实板上可以工作。这属于临时候选证据，下一镜像准入前仍必须完成：

1. 修正或重新限定自动锁焦策略，并在固定物距和光线下以实际图像清晰度重复验收；
2. Windows 客户端使用带内 marker 取得远端退出码，完成真实 GUI 的设备检测、手动/自动采集、`PARTIAL` 和文件拉取联调；
3. 完成 v5.6.3 热插拔以及本阶段要求的相邻功能回归；
4. 只选择本阶段已验证的直连子集进入镜像，不把仍延期的 `mosquito-capture-4g`、`mosquito-upload-4g`、云配置或仅由其需要的依赖一并视为已验证。

软件重启的当前证据位于 `build/test-runs/20260901-174914+0800-auto-adb/`，包括 UART、轮询和 `/proc/config.gz`。物理 Reset 恢复后的自动 ADB 结果不能抵消 `adb reboot` 的失败。

用户已收到有源陶瓷GNSS天线，并要求优先完成 v5.6.2 的真实室外 GNSS 定位。聚焦交接、天线边界、首轮命令、隐私要求和失败分类见 `docs/HANDOFF_2026-08-26_V562_GNSS.md`。现有记录仍是 15×15/18×18 天线未定位；该 GNSS 诊断方向不属于 v5.6.3，本次不把 GNSS 修复纳入镜像。

## 3. 验收前准备

### 3.1 主机与文件

- 若使用 WSL2 作为 Tina 编译环境，先按 `docs/WSL2_SDK_MIGRATION.md` 完成归档校验、Linux 文件系统解压、关键入口检查和 `tina-overlay/` 同步；本次 v5.6.3 已完成首次低并行实际构建并保留日志。
- Windows 安装 Google 官方 Platform-Tools，确保 `adb version` 可用。
- 准备能够保存完整启动日志的 3.3 V TTL UART0，115200 8N1、无流控。
- 准备已验证的数据 USB 线、USB0/TYPE_C1、USB1 摄像头和可用 Air780EG 天线/SIM。
- 准备一个可接收非敏感测试 JPEG 的 HTTPS 地址。
- 备份目标 TF 卡中的照片、日志和其他数据。
- 保留 `2026-08-14-boot-ok` 恢复镜像。

PowerShell 校验当前 v5.6.4 候选镜像：

```powershell
Get-FileHash .\releases\mosquito-t113\2026-09-08-dev-v5.6.4-client-direct\mosquito-t113-dev-v5.6.4-client-direct.img -Algorithm SHA256
```

必须得到 `01f95c8008a0f3957ecaf8f4f9fd5f4bb92ad3cd4c0c0dddc27e95e53a0e251b`。不一致时立即停止，不得烧录。

### 3.2 烧录安全

- PhoenixCard 必须使用“启动卡 / Startup”模式。
- 再次确认选中的是目标 TF 卡；烧录会重建分区。
- 不使用普通 `dd` 或 Etcher。
- 烧录完成后保留 PhoenixCard 的成功/校验结果。

## 4. v5.6.2 历史最终验收矩阵

以下矩阵记录 v5.6.2 的历史基线要求；v5.6.3 需沿用相关数据、摄像头和 4G 回归，并将“ADB 默认关闭 + 手动 `adb-on`”改为“userspace 就绪后的自动 ADB”，同时仍保留手动命令作为救援入口。

### A. 首次冷启动与身份

首次测试先只连接 UART0，不连接 USB0。保存从 Boot0 开始的完整日志。

```sh
mosquito-version
cat /proc/cmdline
grep -E '^processor' /proc/cpuinfo
nproc
mount
cat /proc/uptime
pidof adbd || true
usb0-status
```

通过条件：

- Boot0、U-Boot、Linux 和 BusyBox Root Shell 连续出现，无循环重启或内核崩溃；
- `nproc` 为 `2`；
- 版本精确为 `dev-v5.6.2-adb-on-validated`、包为 `2.17-1`；
- 执行 `adb-on` 前没有存活 `adbd`，ADB 默认关闭；
- TF 分区和可写 overlay 正常挂载。

### B. 一条命令启动 ADB

板端：

```sh
adb-on
echo $?
ls -la /dev/usb-ffs/adb
cat /sys/class/udc/*/state
usb0-status
```

Windows PowerShell：

```powershell
adb kill-server
adb start-server
adb devices -l
adb shell id
adb shell mosquito-version
```

通过条件：

- `adb-on` 退出码为 0；
- FunctionFS 存在 `ep0`、`ep1`、`ep2`；
- UDC 达到 `configured`；
- Windows 设备是 `MOSQUITO-T113-DEV device`，不是 `offline`；
- `adb shell` 是预期 root 环境。

保存以下日志：

```sh
cat /tmp/mosquito-adbd.log
cat /tmp/mosquito-usb0-adb.log
cat /overlay/mosquito-test/usb0-adb.log
```

### C. ADB 数据与恢复

准备一个已知 SHA-256 的大文件或最近一张 JPEG：

```powershell
adb push .\测试文件 /mnt/UDISK/mosquito-test/bin/
adb pull /mnt/UDISK/mosquito-test/bin/测试文件 .\roundtrip-测试文件
Get-FileHash .\测试文件 -Algorithm SHA256
Get-FileHash .\roundtrip-测试文件 -Algorithm SHA256
```

随后验证：

1. 板端 `usb0-adb-stop` 后，Windows 不再列出在线设备。
2. 再执行一条 `adb-on`，设备恢复为 `device`。
3. 保持板子独立供电，USB0 拔出再插入，ADB 自动恢复且 `adbd` PID 不应无故变化。
4. Type-C 正反插至少各一次。
5. 至少完成 10 次“冷启动 → UART Shell → `adb-on` → `adb devices -l` → `adb shell`”。

任何一次 `offline`、循环重启、只能重插才能恢复或 FunctionFS 只有 ep0，都算失败。

### D. 摄像头回归

固定相机、目标、距离和光线：

```sh
camera-test0
cat /overlay/mosquito-test/camera-focus-capabilities.conf
camera-test
camera-test1
camera-test1 合法焦距值
```

通过条件：

- 摄像头能上电、枚举、拍摄 3264×2448 MJPEG、同步并断电；
- JPEG 非空且 SOI/EOI 检查通过；
- 顺序编号不覆盖旧文件；
- 最终 `PHOTO=`、`METADATA=`、`LOG=` 是完整独立行；
- 自动锁焦成功时，元数据的 `focus_selected`、拍摄前后回读一致，`focus_lock_verified=yes`，`focus_auto_before_capture=0`；
- ADB 可把 JPEG 和 `.jpg.txt` 拉回 Windows，哈希一致。

焦距稳定不等于图像清晰。至少人工检查连续多张图片清晰率，并记录物距和光线。

### E. 4G、上传与 ADB 共存

保持 ADB 在线，板端运行：

```sh
4g-start
ip addr show ppp0
ip route
cat /etc/resolv.conf
date
photo-upload 'https://测试接收地址' '/mnt/UDISK/mosquito-test/camera/照片.jpg'
4g-stop
ip link show ppp0 || true
```

并在 Windows 同时重复：

```powershell
adb devices -l
adb shell true
```

通过条件：

- 已加载 PPP 模块不会导致 `4g-start` 失败；
- LTE 注册、`ppp0`、默认路由和 DNS 正常；
- NTP 把明显错误的 1970 年时间校正；
- 正式 HTTPS 验收不使用 `MOSQUITO_CURL_INSECURE=1`，服务器返回成功状态；
- 双行 pppd PID 文件不会阻断 start/stop；
- `4g-stop` 后 PPP 确实结束；
- 整个过程中 ADB 保持 `device`。

### F. 板级与持久化回归

在不与 PPP 争用 `/dev/ttyS1` 的前提下：

```sh
board-test
sim-test
power-test
4g-stop
gnss-test
```

GNSS 需要室外开阔天空，不能在室内失败后直接判硬件故障。默认日志不得暴露精确坐标；确需坐标时只在受控现场临时使用 `MOSQUITO_GNSS_SHOW_COORDS=1`。

检查 BQ25895 的原始寄存器输出并记录 `REG0C watchdog=1` 是否再次出现。只要自动检查 `FAIL=0` 且主路径未受影响，可继续作为已知非阻断风险，但不能删掉记录。

验证 overlay 持久化：

```sh
echo v5.6.2-final-validation > /root/mosquito-overlay-test
sync
reboot
cat /root/mosquito-overlay-test
pidof adbd || true
```

重启后测试文件应存在，ADB 应重新默认关闭。

注意：上述持久化测试依赖系统能够完成重启；当前 v5.6.2 的 `adb reboot` 已知会停在 `Reboot failed -- System halted`，不得把超时当作偶发 ADB 问题。候选修复是启用内置 `CONFIG_WATCHDOG=y` 和 `CONFIG_SUNXI_WATCHDOG=y`，但必须先得到用户明确批准，再同步到 `tina-overlay/` 并制作新候选。

## 5. v5.6.2 历史最终通过标准

以下标准用于记录 v5.6.2 从“候选”提升为验证开发基线所需的条件；v5.6.3 精确成品已完成自动 ADB 冷启动 3/3，仍需按第 2 节完成热插拔和对应关键路径回归。

- 精确镜像身份和 SHA-256 正确；
- 10 次冷启动无循环重启，10 次手动 `adb-on` 全部一次成功；
- Windows ADB 始终为 `device`，shell/push/pull 和哈希闭环通过；
- stop/start、热拔插和 Type-C 正反插通过；
- 摄像头拍照、焦距回读、JPEG、元数据及 ADB 下载通过；
- PPP、DNS、NTP、HTTPS、`4g-stop` 和 ADB 共存通过；
- TF/overlay 和重启后 ADB 默认关闭通过；
- 所有警告、未测项和外部条件有清晰记录；
- 软件重启至少完成 10 次，且每次都能重新启动并恢复预期 ADB 状态；
- `CURRENT_STATE.md`、本文件和该版本 README 已按结果更新。

GNSS 户外定位若因现场条件无法执行，可作为明确的剩余项，但不能写成已通过。生产安全也不能因开发验收通过而自动解决。

## 6. 若 v5.6.2 失败（历史处置）

以下保留 v5.6.2 阶段的失败处置流程；当前 v5.6.3 已按明确授权完成构建，但新的 `2.18-1` 或看门狗方向仍不得自动纳入下一镜像。严格执行：

1. 保存完整 UART、ADB、`dmesg`、`logread`、相关 `/tmp` 与 `/overlay` 日志。
2. 记录 `mosquito-version`、镜像 SHA-256、连接方式、失败轮次和复现率。
3. 在当前 v5.6.2 上稳定复现并定位根因。
4. 用 `/tmp`、`/overlay` 或临时命令实施最小修复。
5. 重复原失败路径，并回归 ADB、摄像头、4G 和启动关键路径。
6. 向用户汇总根因、临时修复证据、回归结果和剩余风险。
7. 明确询问用户是否制作下一版本镜像。

获得明确同意前，不设置新版本号、不改固件源码、不构建、不 `pack`、不建发布目录。

当前软件重启故障的候选修复范围已经缩小为 Linux 5.4 看门狗配置：设备树已有 `allwinner,sun6i-a31-wdt`，本地 `drivers/watchdog/sunxi_wdt.c` 提供 restart handler；需要在用户批准后把 `CONFIG_WATCHDOG=y`、`CONFIG_SUNXI_WATCHDOG=y` 纳入 Mosquito overlay，再执行构建和实板回归。此前完整 SDK 工作副本中的同名配置变更尚未形成成品证据。

## 7. 下一版本候选方向：晚期自动触发 ADB

这一节记录晚期自动触发 ADB 的设计边界；该方向已经按用户批准制作成 v5.6.3，且精确成品自动 ADB 冷启动已 3/3 通过，当前只剩热插拔和完整功能实板回归。本文第 4、5 节保留的是 v5.6.2 的历史验收矩阵，不能用临时 overlay 结果替代 v5.6.3 成品验收。

### 7.1 目标

保留 v5.6.2 已验证的手动 `adb-on` 作为唯一 USB0/FunctionFS/adbd 底层启动流程。下一阶段只研究在明确的“系统 userspace 已就绪”节点调用一次同一条流程，使 Windows 在正常启动后无需 UART 输入即可看到 ADB。

预期关系必须保持简单：

```text
userspace 明确就绪
  -> 一次性触发现有 adb-on
  -> 成功：记录日志并结束
  -> 失败：记录日志并结束，UART 与手动 adb-on 仍可恢复
```

### 7.2 明确禁止

- 不恢复 v5.5.x 在 `rc.preboot` 中提前准备 FunctionFS、绑定或强制重绑 USB0 的实现。
- 不另写一套与 `adb-on` 并行的 ConfigFS/FunctionFS/adbd 启动逻辑。
- 不在 UDISK、rootfs 或 userspace 尚未明确就绪时启动。
- 自动触发失败时不重启系统、不循环重试、不无限解绑/重绑 UDC。
- 不删除 UART0、手动 `adb-on` 或 `usb0-adb-stop` 救援入口。
- 不把 root、无认证 ADB 误写为生产可接受的安全方案。

### 7.3 在当前 v5.6.2 上的临时验证门槛

用户明确要求先用临时 overlay 验证自动上电开启 ADB；该验证已完成 3 次物理断电/上电且 3/3 通过。这是 v5.6.2 可写 overlay 的 B 级证据，不等于精确 v5.6.3 成品已通过。以下 10 项是后续更高层回归清单，尚未宣称全部通过：

1. USB0 预连接冷启动 10 次，每次都进入 `device`，无 `offline` 或循环重启。
2. 系统重启 10 次，自动触发时间和结果可从日志追踪。
3. 不接 USB0 启动，稳定后热插；Type-C 正反插均恢复。
4. 板子独立供电时 USB0 多次拔插，不产生重复守护进程或无限重绑。
5. 自动触发失败后，UART Root Shell 可用，手动 `adb-on` 能恢复。
6. `usb0-adb-stop` 能完全停止；手动 `adb-on` 能再次启动。
7. 摄像头拍照、JPEG/元数据、ADB push/pull 回归通过。
8. 4G、PPP、DNS、NTP、HTTPS 与 ADB 共存，`4g-stop` 正常。
9. 日志保存在 `/tmp` 和必要的 `/overlay/mosquito-test/`，不会写满持久存储。
10. 重启或断电后没有遗留状态把下一次启动卡死。

上述临时验证结果已汇总并经用户批准，形成 v5.6.3；精确成品自动 ADB 冷启动已 3/3 通过，但仍需执行热插拔和对应功能回归，不能把临时 overlay 证据直接升级为全部成品通过。

## 8. 用户批准新镜像后的流程

以下是获用户批准后制作新镜像的标准流程；本次 v5.6.4 已执行构建、打包、成品反查、发布目录归档和烧录，且板端直连子集与 Windows WPF 成品回归已经通过。

1. 将已经在当前镜像验证通过的最小修复写入 `tina-overlay/`。
2. 更新镜像版本、构建日期和板级包版本，确保三者一致。
3. 静态检查 Shell、C、配置差异及 Git 空白错误。
4. 在完整 Tina SDK 中执行 `make`，成功后执行官方 `pack`。
5. 建立唯一的新版本目录，不覆盖旧文件。
6. 复制精确成品，生成 `.img.sha256`，逐字节或哈希复核。
7. 提取/反查成品 rootfs，确认版本、脚本、权限、启动入口和关闭项。
8. 烧录这一精确成品并重新执行相应实板验收，不能沿用临时 overlay 的通过结论。
9. 更新三个权威文档和版本 README。
10. 经用户同意后提交/推送源码；如需上传镜像，再创建对应 Git tag 与 GitHub Release 附件。

## 9. WSL2 Agent 接手清单

在 WSL2 ext4 克隆 GitHub 项目后，Agent 的第一轮工作必须是：

```sh
echo "$WSL_DISTRO_NAME"
uname -a
pwd
git remote -v
git branch --show-current
git status --short --branch
git rev-parse HEAD
cat AGENTS.md
cat docs/CURRENT_STATE.md
cat docs/NEXT_IMAGE.md
cat docs/DEVELOPMENT_WORKFLOW.md
cat releases/mosquito-t113/2026-09-08-dev-v5.6.4-client-direct/README.md
powershell.exe -NoProfile -Command 'adb version; adb devices -l'
```

如果只克隆了 Git 仓库而未下载 Release 附件，本地没有 `.img` 是正常现象。Agent 可以继续读代码、诊断和规划；需要烧录时才把对应附件下载到现有版本目录并校验 SHA-256。没有设备连接时，不得把源码分析写成实板验证。

当前可执行的下一步是：**保持现有 v5.6.4，不制作新镜像；补做 3 次物理冷启动/UART、USB0 物理热拔插与 Type-C 正反插，并在 UART Root Shell 可用时验证 ADB stop/start。自动对焦光学画质等相机固定后再验。**

## 10. 与客户端生产上线的关系

2026-09-01 完成的 Windows ADB 实板演示闭环、DHT30/BQ25895 临时接口和 `BOARD_4G` 协议仿真，不构成制作新镜像或生产上线授权。生产工作必须按 `docs/PRODUCTION_ROLLOUT_PENDING.md` 分阶段执行。

特别注意：

- 真实阿里云生产部署和 Air780EG 4G 直传仍未执行；
- 冷启动自动连接必须先解决生产维护通道安全边界，不能简单把 root、无认证 ADB 自动打开；
- 自动 ADB 已在 v5.6.3 精确成品完成冷启动 3/3；DHT30/BQ25895 和直连采集接口已经在 v5.6.4 精确成品完成板端与 Windows WPF 回归；v5.6.4 自身的三次物理冷启动和 USB 物理恢复仍待补；自动对焦光学画质按用户决定待相机固定后验；
- 用户对本次 v5.6.4 直连镜像的构建授权已经执行。未来加入 Watchdog、云端、新增 4G、GNSS 或其他范围时，仍需按 `AGENTS.md` 重新核对门槛和授权。
