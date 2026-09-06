# Mosquito T113 自动 ADB / 软件重启诊断交接

更新时间：2026-09-03（Asia/Shanghai）
交接目的：新开对话后继续 2026-09-01 晚间的自动 ADB 回归和软件重启根因处理。

## 0. 新 Agent 必须先读

按仓库规则依次阅读：

1. `AGENTS.md`
2. `docs/CURRENT_STATE.md`
3. `docs/NEXT_IMAGE.md`
4. 本文件
5. `docs/DEVELOPMENT_WORKFLOW.md`
6. `releases/mosquito-t113/2026-08-26-dev-v5.6.2-adb-on-validated/README.md`

本文件是自动 ADB/重启问题的聚焦交接；权威总体状态仍以 `CURRENT_STATE.md` 和 `NEXT_IMAGE.md` 为准。

## 1. 用户当前要求

用户要求把昨天做过的事情和“自动 ADB 为什么不通过”从零解释清楚，并继续昨天的工作；随后要求把当前对话整理成 handoff，换新对话继续。

准确结论必须保持为：

> 自动 ADB 的“启动动作”单项已在临时 overlay 上通过；自动 ADB 的完整重启恢复回归未通过。已定位的失败点是 `adb reboot` 后 Linux 内核没有完成 SoC 硬件复位，不是 FunctionFS、`adbd` 或自动调用 `adb-on` 本身失败。

## 2. 仓库与工作区边界

- 板端仓库：`/home/janelinux/work/mosquito/mosquito-t113`
- 完整 Tina SDK：`/home/janelinux/work/mosquito/tina-t113`（不属于 Git 仓库，不得提交或公开分发）
- 当前板端分支：`feature/client-demo-capture`
- 目标集成分支：`main`
- 当前工作区已有用户修改和新增文件，不能清理或覆盖。最近状态至少包括：
  - 已修改：`docs/CURRENT_STATE.md`、`docs/NEXT_IMAGE.md`、`docs/WSL2_SDK_MIGRATION.md`、v5.6.2 README、板测包 Makefile、`camera-test`、`mosquito-version.conf`、`mosquito-board-test.c`
  - 新增：`docs/PRODUCTION_ROLLOUT_PENDING.md`、`mosquito-capture`、`mosquito-capture-4g`、`mosquito-cloud.conf.example`、`mosquito-upload-4g`
- 没有执行 Git commit、push、PR、tag 或 Release。
- 本次交接前没有新增固件源码改动、没有执行 Tina 完整 `make`/`pack`、没有制作或烧录新镜像。

## 3. 项目当前版本与镜像

当前目标实板和候选成品身份：

```text
MOSQUITO_IMAGE=dev-v5.6.2-adb-on-validated
MOSQUITO_BUILD_DATE=2026-08-26
MOSQUITO_BOARD_PACKAGE=2.17-1
```

候选成品：

```text
releases/mosquito-t113/2026-08-26-dev-v5.6.2-adb-on-validated/mosquito-t113-dev-v5.6.2-adb-on-validated.img
size: 11,508,736 bytes
sha256: f0d24710f165122fe7539098c777d15f25c52d0b73874ac6a3ca421b38edaf08
```

这个镜像默认不开 ADB，UART Root Shell 中手动执行一条 `adb-on`。成品本身没有自动 ADB 启动入口；昨天的自动 ADB 是临时写入当前系统可写 overlay 的实验钩子。

## 4. 昨天做了什么（2026-09-01）

### 4.1 开发环境和业务演示

- WSL2 中恢复完整 Tina SDK，关键目录、工具链存在。
- 板测 C 程序交叉编译通过。
- 没有执行完整 Tina 镜像构建或 `pack`。
- 在当前 v5.6.2 实板上临时部署新接口：DHT30 连续 10 次通过；BQ25895 连续 10 次可读，出现过一次 `FAULT_REG=0x80` 警告；固定焦距 500 拍照成功，照片为 3264×2448，温湿度和电源元数据随照片归档。
- Windows 客户端完成真实 Windows ADB 下的一键采集、照片/元数据拉取、长度和 SHA-256 校验、本地云 API 上传、详情和列表闭环。`BOARD_4G` 只做了 API 协议仿真，没有真实 Air780EG 蜂窝直传证据。

### 4.2 临时晚期自动 ADB

目标是验证这样一条链路：

```text
系统 userspace 就绪
  -> 临时脚本只调用一次现有 /usr/bin/adb-on
  -> FunctionFS + USB Gadget + adbd 建立
  -> Windows ADB 显示 device
```

脚本等待 userspace/UDC 具备条件，调用现有 `adb-on`，使用一次性 marker 防止重复启动；没有恢复 v5.5.x 的早期 `rc.preboot` 重绑逻辑，也没有另写一套 ConfigFS/FunctionFS 流程。

物理 Reset 后的板端日志显示：

```text
[PASS] late userspace ready
[PASS] USB0 role is usb_device
[PASS] vendor USB manager bound ...
[PASS] automatic ADB startup completed
adbd_pids=221 pid_count=1 endpoints=ep0 ep1 ep2
UDC state=configured
```

这说明自动启动动作本身通过了一轮。原始日志：
`build/test-runs/20260901-174914+0800-auto-adb/after-uart-recovery/auto-adb-run.log`

### 4.3 重启和断电回归

- 17:54 左右首次 `adb reboot` 测试：设备从主机消失，120 秒内没有重新上线。
- 随后的多次物理断电脚本有 `POWER_CYCLE_NOT_OBSERVED` 或测试前设备不在线。它们说明测试窗口没有可靠观察到物理断电，不能单独当作自动 ADB 代码根因。
- 23:21 再次执行更严格的 `adb reboot` 诊断，等待时间扩大到 900 秒；设备消失后始终没有回来，Windows 没有进入 `offline`，而是完全没有设备。
- 按物理 Reset 后，板子恢复并再次完成临时自动 ADB 启动。

## 5. 为什么说“整套回归没通过”，但“自动 ADB 启动通过”

测试不是一个单一开关，而是多段链路：

| 测试段 | 结果 | 含义 |
|---|---|---|
| 等待 userspace/UDC | 通过 | 自动脚本没有过早启动 |
| 调用现有 `adb-on` | 通过 | 没有另写并行 USB 流程 |
| FunctionFS `ep0/ep1/ep2` | 通过 | ADB 数据端点已建立 |
| 单个 `adbd` | 通过 | 没有重复守护进程 |
| UDC `configured` | 通过 | USB Gadget 已绑定 |
| `adb reboot` 后系统重新起来 | 失败 | 系统没有完成硬件复位 |
| 重启后再次自动 ADB | 无法继续 | 因为系统根本没有重新启动 |

因此不能把最后一项失败倒推成“自动 ADB 启动代码失败”。

## 6. 已确认的故障根因

`adb reboot` 后的 UART 顺序为：

```text
USB_STATE=DISCONNECTED
The system is going down NOW!
Sent SIGTERM to all processes
reboot: Restarting system
Reboot failed -- System halted
```

USB 断开、进程退出和文件系统处理都发生了，说明系统已经正常进入关机流程；失败发生在 Linux 请求 SoC 真正复位的最后一步。

重启后从运行板子的 `/proc/config.gz` 取得：

```text
CONFIG_ARCH_SUNXI=y
CONFIG_SUNXI_SOC_NAME="sun8iw20"
CONFIG_SMP=y
# CONFIG_POWER_RESET is not set
# CONFIG_WATCHDOG is not set
```

Linux 的 ARM 重启路径会调用 `do_kernel_restart()`。如果没有注册任何 restart handler，就会等待一秒并打印 `Reboot failed -- System halted`，随后停死。

T113 的设备树已有硬件看门狗节点：

```text
文件：lichee/linux-5.4/arch/arm/boot/dts/sun8iw20p1.dtsi
节点：watchdog@20500A0
地址：0x020500A0
compatible = "allwinner,sun6i-a31-wdt"
```

本地 `drivers/watchdog/sunxi_wdt.c` 也有 `.restart = sunxi_wdt_restart`，并在注册 watchdog 设备时注册 restart handler。但当前 `CONFIG_WATCHDOG` 没打开，所以这个驱动没有进入运行内核，重启时没有可用的复位处理器。

证据文件：

- `build/test-runs/20260901-174914+0800-auto-adb/SOFTWARE_REBOOT_DIAGNOSIS.md`
- `build/test-runs/20260901-174914+0800-auto-adb/software-reboot-2-uart.log`
- `build/test-runs/20260901-174914+0800-auto-adb/software-reboot-2-result.txt`
- `build/test-runs/20260901-174914+0800-auto-adb/software-reboot-2-reset-recovery/config.gz`

## 7. 为什么不是自动 ADB 造成的

1. 自动 ADB 在开机约 12 秒时已经报告 `automatic ADB startup completed`。
2. `adb reboot` 后系统继续完成 USB 断开、进程终止和文件系统处理，说明命令已经传到系统并被执行。
3. 失败点是 `reboot: Restarting system` 之后，而不是 USB Gadget 建立之前。
4. Windows 端看到的是设备消失，没有反复 `offline` 握手；这更符合系统停死。
5. 物理 Reset 之后，同一个临时自动 ADB 钩子又能正常完成。

所以正确表述是：**自动 ADB 启动通过；软件重启恢复失败；完整自动 ADB 回归失败。**

## 8. 候选修复及其当前状态

完整 SDK 工作副本中的板型配置文件目前相对于 Git overlay 只有以下两行差异：

```diff
-# CONFIG_WATCHDOG is not set
+CONFIG_WATCHDOG=y
+CONFIG_SUNXI_WATCHDOG=y
```

路径：
`/home/janelinux/work/mosquito/tina-t113/device/config/chips/t113/configs/mosquito/linux-5.4/config-5.4`

注意：

- Git overlay 中的对应文件仍是关闭状态；
- SDK 的生成 `.config` 在最后检查时仍未形成可烧录成品证据；
- 没有同步到 Git overlay；
- 没有执行完整 `make`、`pack`、烧录或实板验证；
- 这只是合理的候选修复，不是“已修复”。

启用看门狗的理论链路是：

```text
CONFIG_WATCHDOG=y
  -> watchdog core 编译
CONFIG_SUNXI_WATCHDOG=y
  -> sunxi_wdt 编译并匹配 T113 设备树
  -> 注册 watchdog restart handler
  -> adb reboot 时写入 T113 watchdog 复位寄存器
  -> SoC 硬件复位并重新启动
```

仍需实板验证，不能只凭源码或成功编译宣布通过。

## 9. 授权门槛与下一步

仓库 `AGENTS.md` 要求：在当前镜像上完成证据、定位和临时修复验证后，必须明确询问用户是否开始新镜像流程。当前已经完成了故障复现和源码/运行配置定位，但尚未在当前镜像上验证看门狗临时修复。

如果用户明确批准继续候选修复，建议顺序为：

1. 将 `CONFIG_WATCHDOG=y`、`CONFIG_SUNXI_WATCHDOG=y` 写入 `tina-overlay/device/config/chips/t113/configs/mosquito/linux-5.4/config-5.4`。
2. 检查只发生预期配置变化，运行 `git diff --check`，同步 overlay 到完整 SDK。
3. 在完整 SDK 中构建内部候选并执行官方 `pack`；不要覆盖 v5.6.2 成品目录。
4. 对成品做 SHA-256、分区、SquashFS、版本和配置反查。
5. 只有用户再次明确允许烧录后，才使用 PhoenixCard “Startup” 模式烧录精确成品。
6. 实板至少验证：冷启动、自动 ADB、`adb devices` 为 `device`、FunctionFS 三端点、`adb reboot` 10 次、物理 Reset、物理断电、USB 热插拔、无重复 `adbd`、无 `offline`。
7. 回归相邻路径：ADB push/pull、摄像头、4G/PPP/DNS/NTP/HTTPS、ADB 与 4G 共存、`4g-stop`、重启后状态。
8. 通过后再更新版本 README、`CURRENT_STATE.md`、`NEXT_IMAGE.md`，并单独向用户汇总是否提交/推送 Git。

如果用户没有明确批准修改固件和构建，则只做当前 v5.6.2 的诊断、文档和测试准备，不改 overlay、不构建、不 `pack`、不烧录。

## 10. 当前环境限制

本对话中尝试从 WSL2 调用固定 Windows ADB：

```text
/mnt/c/embedded/android-platform-tools/adb.exe devices -l
<3>WSL ... UtilBindVsockAnyPort ... socket failed 1
```

这只是当前 WSL/Windows 互操作调用失败，不能当作开发板 ADB 测试结果。恢复硬件测试前必须先确认 `adb.exe` 可调用、USB 线/拓扑正确，并先执行：

```powershell
adb devices -l
adb shell mosquito-version
```

只有确认精确 v5.6.2 身份后，新的实板日志才可以归入本交接。

## 11. 本对话中已完成的文档动作

本对话把 2026-09-01 晚间结果同步进：

- `docs/CURRENT_STATE.md`
- `docs/NEXT_IMAGE.md`
- `releases/mosquito-t113/2026-08-26-dev-v5.6.2-adb-on-validated/README.md`

新增本文件后，运行过 `git diff --check`，没有发现空白错误。以上文档修改叠加在工作区原有的客户端、板端和迁移文档修改之上；新对话不得把整个工作区当成可以直接提交的单一变更集。

## 12. 新对话开场可直接使用的提示

```text
请先阅读 AGENTS.md、docs/CURRENT_STATE.md、docs/NEXT_IMAGE.md、docs/HANDOFF_2026-09-03_AUTO_ADB_REBOOT.md、docs/DEVELOPMENT_WORKFLOW.md 和 v5.6.2 README。

继续 2026-09-01 晚间的自动 ADB/软件重启诊断：临时晚期自动 ADB 在物理 Reset 后已通过，能达到单个 adbd、FunctionFS ep0/ep1/ep2 和 UDC configured；但 adb reboot 在精确 v5.6.2 实板上 900 秒不返回，UART 报 Reboot failed -- System halted。运行内核未启用 CONFIG_WATCHDOG/CONFIG_POWER_RESET，T113 设备树已有 sun6i-a31-wdt，候选只是在 Mosquito overlay 中启用 CONFIG_WATCHDOG=y 和 CONFIG_SUNXI_WATCHDOG=y。

先检查当前工作区和设备身份。未经用户明确批准，不改固件源码、不执行 Tina make/pack、不烧录、不提交推送。若获得批准，再按本 handoff 的候选修复和 10 次软件重启回归计划执行。
```
