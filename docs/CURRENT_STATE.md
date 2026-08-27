# Mosquito 当前状态

更新时间：2026-08-27（Asia/Hong_Kong）

本文只记录已有证据支持的事实。新 Agent 接手时应先读根目录 `AGENTS.md`，再读本文和 `docs/NEXT_IMAGE.md`。

## 1. 当前结论

- GitHub 仓库：`https://github.com/skyjane1129-blip/mosquito-t113.git`。
- 目标集成分支：`main`。本次整理开始时，工作现场位于 `agent/sim-diagnostics`，它与本地及远端 `main` 都基于 `90000d8`，大量后续开发尚在工作区中；本轮目标是把审核后的现场整理为新的 `main` 基线。
- 当前源码对应的镜像身份：`dev-v5.6.2-adb-on-validated`，板级包 `mosquito-board-test 2.17-1`。
- 当前首选候选成品：`releases/mosquito-t113/2026-08-26-dev-v5.6.2-adb-on-validated/mosquito-t113-dev-v5.6.2-adb-on-validated.img`。
- v5.6.2 的 ADB 与 4G 修复曾在已运行的 v5.6.1 系统可写 overlay 上完成实板验证；v5.6.2 成品本身已完成构建、打包、SquashFS 反查和本地 SHA-256 校验，但尚缺“烧录这一精确成品后的最终冷启动验收”。
- 因此当前状态是“开发主线候选”，不是野外生产稳定版；当前第一任务是验证已有 v5.6.2，不是制作 v5.6.3。
- 本次文档整理环境没有 `adb` 命令，也没有发现 `/dev/ttyUSB*` 或 `/dev/ttyACM*`，因此本轮没有新增任何实板验证结论。
- 2026-08-26用户从连接实板的Windows PowerShell回传：`adb devices -l`显示`MOSQUITO-T113-DEV device`，`adb shell mosquito-version`精确显示`dev-v5.6.2-adb-on-validated`/`2026-08-26`/`2.17-1`，`command -v gnss-test`返回`/usr/bin/gnss-test`。因此当前运行镜像身份、ADB通道和GNSS工具存在性已实板确认；尚未提供该镜像的完整冷启动日志或GNSS室外定位结果。
- 用户的有源陶瓷GNSS天线已确认标称支持3–5 V、50 Ω、GPS L1/BeiDou B1频点、120 mm馈线和IPEX一代接头，电压与Air780EG的3.3 V `GNSS_VCC`相容；但商家尚未给出3.3 V下的典型/最大工作电流，且三种陶瓷尺寸尚未实板比较。
- 2026-08-26同一连板PowerShell执行`adb shell 4g-stop`返回`Air780EG PPP is not running`，`pidof pppd`无输出，`ip link show ppp0`返回`can't find device 'ppp0'`。这确认当时PPP进程和`ppp0`均不存在，GNSS诊断不会与pppd争用`/dev/ttyS1`；尚未运行`gnss-test`。
- 2026-08-26首轮`adb shell gnss-test`在该v5.6.2实板上完整运行5分钟：Air安全状态、3.9 V使能、UART、AT响应、`AT+CGNSPWR=1`和最终`AT+CGNSPWR=0`全部PASS，但每个已报告的搜索样本均为`fix=0`、`UTC=none`、satellites `0/0/0`、`C/N0-max=0`，5分钟后汇总`PASS=6 FAIL=0 WARN=1 MANUAL=0`。用户后续确认当轮使用`15×15×4.6 mm`有源陶瓷天线，测试地点在室内。因此这是精确v5.6.2的“室内GNSS已开启但零卫星/未定位”实板证据，不能据此判定天线或RF2硬件故障；天线确切摆放和RF2扣接状态尚未记录。
- 同日用户重启开发板，UART上一条`adb-on`完成FunctionFS和UDC绑定，并出现`high-speed config #1`及`USB_STATE=CONFIGURED`。Windows经扩展坞连接时`adb devices -l`一度为`offline transport_id:4`；用户改用电脑USB口直连后变为`device transport_id:5`。因此当前证据支持板端adbd/FunctionFS可用，`offline`与扩展坞/主机USB连接拓扑相关；扩展坞ADB稳定性未通过。
- 随后保持同一`15×15×4.6 mm`天线并经电脑USB口直连执行第二轮5分钟`gnss-test`：搜索结果从卫星`0/0/0`提升到`1/0/0`、再稳定为`3/0/0`，`C/N0-max`为31–32 dB-Hz，并从`UTC=none`进展到有效UTC `20260826074511`及后续时间。但5分钟内仍为`fix=0 mode=1`，最终`PASS=6 FAIL=0 WARN=1`。这证明有源天线供电/连接、RF2射频输入和Air780EG GNSS接收链至少可观测卫星并解码时间；当轮是否已在室外开阔天空尚未随原始输出明确记录，定位通过仍待验证。
- 用户随后按`15×15`天线室外R1步骤、保持电脑USB口直连又完成一轮5分钟`gnss-test`：卫星从`0/0/0`逐步增加到`1/0/0`、`2/0/0`和`3/0/0`，最大C/N0仅25–26 dB-Hz，从`UTC=none`进展到有效UTC `20260826081431`及后续时间，但全程仍为`fix=0 mode=1`，没有卫星参与定位，最终`PASS=6 FAIL=0 WARN=1`且`GNSS-power-off`通过。用户已将设备内`/tmp/mosquito-gnss-test.log`拉取为`gnss-v562-15x15-outdoor-r1-device.txt`，测试后ADB仍为`MOSQUITO-T113-DEV device transport_id:5`。这一轮继续证明RF接收链并非完全断路，但信号比上一轮的31–32 dB-Hz更弱且不足以定位；场地具体遮挡、天线与金属/线束距离及接收面方向尚未随输出单独记录。
- 紧接的`15×15`天线室外R2仍完成了全部5分钟：可见卫星从0逐步增加到4颗，最大C/N0为25–27 dB-Hz，并从`UTC=none`进展到有效UTC `20260826082424`至`20260826082740`；但4颗始终都只是“可见”，参与GNSS/GLONASS定位的数量均为0，整个过程为`fix=0 mode=1`，最终仍是`PASS=6 FAIL=0 WARN=1`且`GNSS-power-off`通过。因此R2相较R1只增加了1颗可见卫星，没有形成可用定位；控制链继续正常，接收质量仍是主要待查方向。
- 2026-08-27用户回传了一组拼接的GNSS文本；前两段可由UTC和误粘贴命令识别为既有15×15 R1，最后一段结合用户刚报告“18×18不理想”的操作顺序，暂按18×18室外R1记录，但仍需用唯一命名的原始文件确认来源。该最后一段从Air无AT响应开始，安全施加一次1.2秒PWRKEY脉冲后正常启动；GNSS开启后的5分钟大部分时间为0颗卫星，后期仅观测1颗，最大C/N0从28降至21–24 dB-Hz，直到`UTC=20260826090312`才解出时间，始终0颗参与定位、`fix=0 mode=1`，最终`GNSS-power-off`通过且`PASS=6 FAIL=0 WARN=1`。若天线、场地和摆放记录无误，18×18个体并未改善接收，且明显弱于15×15的3–4颗/25–27 dB-Hz；这把下一步收窄到天线个体/扣接、有源偏置和共同RF/干扰路径诊断，而不是继续按尺寸推定性能。
- 下一对话的聚焦交接见 `docs/HANDOFF_2026-08-26_V562_GNSS.md`；当前GNSS目标仍是验证既有v5.6.2，不是制作新镜像。

## 2. 证据等级

本文使用以下等级，避免混淆“逻辑验证”和“最终镜像验证”：

| 等级 | 含义 |
| --- | --- |
| A | 精确成品镜像已烧录并在目标实板完成对应测试 |
| B | 同一修复已在当前/前一镜像的临时 overlay 上实板通过，但精确新成品仍需烧录复核 |
| C | 源码静态检查、完整构建、`pack`、成品反查或 SHA-256 通过 |
| D | 待验证、仅有设计或源码推断 |

v5.6.2 当前总体为 **B+C**，尚未达到整版 A。

## 3. 硬件基线

| 项目 | 当前事实 |
| --- | --- |
| SoC | Allwinner T113-S3，双 Cortex-A7，128 MiB DDR3 |
| 启动介质 | TF/SDC0，PhoenixCard 七分区启动镜像 |
| Linux | Tina Linux，内核 5.4.61 |
| 根文件系统 | `/dev/mmcblk0p5`；ext4 `rootfs_data` 提供可写 overlay |
| 摄像头 | Fifine K436 USB UVC，VID:PID `3142:0046`，3264×2448 MJPEG |
| 摄像头电源 | PE0/CAM_EN，GPIO 偏移 128 |
| 蜂窝/定位 | Air780EG LTE/GNSS，共用 `/dev/ttyS1` |
| 电源诊断 | BQ25895，可读电池/系统/VBUS/充电和故障寄存器；不是电量计 |
| 调试 | UART0 115200 8N1；USB0 手动 root ADB；ZMODEM 救援 |
| 当前未完成硬件项 | DHT30、EA3056 业务集成；精确 SOC；低功耗/RTC 唤醒；生产级安全维护通道 |

重要约束：PCB 的 TF 卡检测信号状态不可靠，SDC0 在设备树中按 `non-removable` 处理；运行时不支持 TF 热插拔。SDK 自带 OP-TEE 二进制无法通过本板硬件信息检查，当前启动包不包含 OP-TEE。

## 4. 软件与仓库布局

本仓库不是完整 Tina SDK，而是可审计的 Mosquito 自研内容：

- `tina-overlay/target/allwinner/t113-mosquito/`：目标板、根文件系统和包选择。
- `tina-overlay/device/config/chips/t113/configs/mosquito/`：DTS、内核、分区及打包配置。
- `tina-overlay/package/utils/mosquito-board-test/`：板测 C 程序、摄像头、4G、上传、版本和 USB0 ADB 工具。
- `tina-overlay/lichee/.../sun8iw20p1_mosquito_defconfig`：U-Boot 配置。
- `releases/mosquito-t113/`：所有版本的本地镜像目录、版本说明和校验文件。
- `docs/`：技术分析、当前状态和下一镜像计划。
- `apps/hello-t113/`：ARMv7 hard-float/musl 交叉编译最小示例。

完整 Tina T113 SDK、下载缓存、工具链和构建输出不进入 Git。Windows Agent 应在 Ubuntu 虚拟机或 WSL 中把 `tina-overlay/` 按原相对路径覆盖到已有 SDK 后工作。

## 5. 当前成品身份与可追溯性

```text
MOSQUITO_IMAGE=dev-v5.6.2-adb-on-validated
MOSQUITO_BUILD_DATE=2026-08-26
MOSQUITO_BOARD_PACKAGE=2.17-1
MOSQUITO_SOURCE_STATE=workspace-snapshot
```

成品信息：

| 字段 | 值 |
| --- | --- |
| 文件 | `mosquito-t113-dev-v5.6.2-adb-on-validated.img` |
| 大小 | 11,508,736 bytes |
| SHA-256 | `f0d24710f165122fe7539098c777d15f25c52d0b73874ac6a3ca421b38edaf08` |
| 格式 | Allwinner/PhoenixCard 七分区整卡镜像 |
| 烧录方式 | PhoenixCard“启动卡 / Startup” |
| 禁止方式 | 普通 `dd`、Etcher |

成品静态检查记录显示：`make -j4` 和官方 `pack` 成功；最终 `.img` 中的 SquashFS 已提取；提取结果与构建 `rootfs.img` 一致；成品包含 `/usr/bin/adb-on -> usb0-adb-start`，不包含旧 `/usr/bin/adb_on`；没有 ADB 自动启动链接或 `rc.preboot` 自动入口。

## 6. 功能状态矩阵

| 功能 | 状态 | 等级 | 说明 |
| --- | --- | --- | --- |
| TF 启动、UART0、双核、rootfs | 已有实板基线 | A（历史） | `2026-08-14-boot-ok` 是明确恢复点；v5.6.2 精确成品仍需冷启动复核 |
| 可写 overlay | 已验证 | A（历史） | 开发和临时修复依赖 `/overlay`；新成品仍要做持久化回归 |
| USB1/UVC 摄像头 | 已有实拍 | A（继承） | 3264×2448 MJPEG、自动闪光、120 帧、顺序文件、同步与断电流程已有实板使用记录 |
| 自动锁焦与完整元数据 | 已打包并被后续版本继承 | B/C | 焦距回读、元数据字段已有实现；v5.6.2 精确成品需执行 `camera-test0/1` 回归 |
| UART ZMODEM | 已验证 | A（历史） | 文本和 JPEG 的 `rz/sz` 实传记录来自 dev-v4 |
| USB0 `adb-on` | v5.6.2单次重启后直连通过 | A（单轮）/B/C（其余） | UART一条`adb-on`后电脑直连为`device`；扩展坞路径曾`offline`，10轮最终验收仍未完成 |
| ADB stop/start、热拔插 | 临时修复实板通过 | B/C | 完全停止后恢复、独立供电热拔插和 PID 保持已有记录 |
| ADB push/pull | 临时修复实板通过 | B/C | 包括大文件照片传输；精确 v5.6.2 仍待复核 |
| LTE/PPP/DNS/NTP/HTTPS | 临时修复实板通过 | B/C | v5.6.1 overlay 验证，包含已加载模块和双行 PID 文件修复 |
| ADB 与 4G 共存 | 临时修复实板通过 | B/C | 4G 运行期间 ADB 持续在线已有记录 |
| SIM 诊断 | 已实现 | B/C | 默认遮挡 ICCID；与 PPP 共用 UART，不能并发 |
| GNSS | 15×15/18×18 mm天线均未定位 | A（当前现象）/D（定位通过待验） | 15×15最多4颗/CN0 25–32；暂按18×18 R1的最后一段仅1颗/CN0 21–28；均0颗参与定位、`fix=0 mode=1` |
| BQ25895 | 只读检查可用 | B | 曾见 `REG0C watchdog=1` 警告，但自动检查 `FAIL=0` |
| DHT30、EA3056 | 未完成业务集成 | D | 不属于 v5.6.2 已验收能力 |
| CPUFreq/CPUIdle/RTC 唤醒 | 待开发与测量 | D | 见 `docs/低功耗讨论备忘.md` |
| 生产安全 | 不通过 | 明确限制 | 当前 root、无认证 ADB 仅适合受控开发环境 |

## 7. 当前使用路径

系统冷启动后 ADB 应保持关闭。UART0 出现 Root Shell 后运行：

```sh
mosquito-version
adb-on
```

Windows PowerShell：

```powershell
adb kill-server
adb start-server
adb devices -l
adb shell mosquito-version
```

设备必须显示 `MOSQUITO-T113-DEV device`，不能是 `offline`。摄像头与联网主路径：

```sh
camera-test0
camera-test
camera-test1
camera-test1 500
4g-start
photo-upload 'https://接收端地址' '/mnt/UDISK/mosquito-test/camera/照片.jpg'
4g-stop
```

`gnss-test`、`sim-test`、`air-test` 和 PPP 共用 Air780EG UART。运行这些诊断前必须先 `4g-stop`。

## 8. 历史镜像状态

| 版本目录 | 状态摘要 |
| --- | --- |
| `2026-08-14-boot-ok` | 已验证恢复基线，必须长期保留 |
| `2026-08-14-board-test-v2-usb0-fix` | 修复无 Type-C 控制器时的内核崩溃，历史板测版本 |
| `2026-08-14-usb0-test-v3` | USB0 CDC-ACM 单项测试版本，历史用途 |
| `2026-08-16-dev-v1` | **已知循环重启，禁止烧录** |
| `2026-08-16-dev-v2` | 取消 ADB 自动启动，已被后续版本取代 |
| `2026-08-17-dev-v3` | 30 帧对焦预热，已由 dev-v4 取代 |
| `2026-08-17-dev-v4` | 120 帧实拍更清晰，ZMODEM 实板验证 |
| `2026-08-18-dev-v5-4g-upload` | 首个 4G 上传测试版本 |
| `2026-08-21-dev-v5.1-candidate` | 构建/打包通过，未完成干净 TF 实板验收 |
| `2026-08-22-dev-v5.2-focus-test` | 固定焦距测试，成品检查通过，实板验收未完成 |
| `2026-08-22-dev-v5.3-autofocus-lock-test` | 自动锁焦测试，成品检查通过，实板验收未完成 |
| `2026-08-22-dev-v5.4-camera-focus-test` | 动态范围、锁焦和元数据候选，实板清晰率待统计 |
| `2026-08-22-dev-v5.4.1-camera-focus-hotfix` | 纯 Shell 中位数和回读修复，成品检查通过 |
| `2026-08-22-dev-v5.4.2-clean-camera-output` | 清理串口最终路径输出，成品检查通过 |
| `2026-08-24-dev-v5.5-usb0-adb-autostart` | 启动链接不被扫描，已知问题，不建议烧录 |
| `2026-08-25-dev-v5.5.1-usb0-autostart-hotfix` | ADB 被 UDISK 检查延迟约 90 秒，不建议烧录 |
| `2026-08-25-dev-v5.5.2-usb0-early-autostart` | Windows ADB `offline`，已知问题，不建议烧录 |
| `2026-08-25-dev-v5.5.3-usb0-controlled-rebind` | 构建候选，未完成所要求的实板循环验收 |
| `2026-08-25-dev-v5.6-manual-adb` | FunctionFS 只有 ep0，已知问题，不建议烧录 |
| `2026-08-25-dev-v5.6.1-adbd-background-hotfix` | 后台启动修复成品，完整一命令流程当时仍待验收 |
| `2026-08-26-dev-v5.6.2-adb-on-validated` | 当前候选；修复逻辑实板通过，精确成品冷启动待验收 |

每一行的完整身份、哈希、构建检查和当时的验收边界，以对应目录中的 `README.md` 为准。

## 9. 本地镜像库存与 Git 策略

2026-08-26 盘点结果：

- 本地共有 21 个 `.img`，总计 231,529,472 bytes（目录占用约 222 MiB）。
- 20 个带独立 `.img.sha256` 的版本全部通过 `sha256sum -c`。
- `2026-08-14-board-test-v2-usb0-fix` 没有独立 sidecar，但实际文件 SHA-256 与 README 中的 `054e0e...e75` 一致。
- `.gitignore` 的 `/releases/**/*.img` 已生效；当前没有 `.img` 被 Git 跟踪。
- GitHub 提交版本 README、SHA-256 和测试证据；实际 `.img` 后续经用户授权后作为 GitHub Release 附件上传。

普通 `git clone` 不会取得 `.img`。Windows 只做源码开发和 Agent 接续不受影响；需要烧录时，必须另行下载对应 Release 附件到相同版本目录并校验哈希。

## 10. 当前阻断项与风险

1. **精确成品未做最终实板闭环**：v5.6.2 不能仅凭 overlay 逻辑验证标为整版稳定。
2. **无设备通道**：本轮环境无 ADB 和串口设备，不能新增实板证据。
3. **开发 ADB 不安全**：root、无认证，仅适合受控开发。
4. **系统时间**：离线冷启动可能停在 1970；`4g-start` 后 NTP 已有验证，校时前照片元数据会标记时间无效。
5. **GNSS**：15×15 mm天线室外R1/R2分别最多3颗和4颗、C/N0仅25–27，均0颗参与定位；用户拼接文本的最后一段暂按18×18 R1记录，则仅1颗、C/N0 21–28且仍未定位。下一步不再用尺寸判断，应先唯一保存18×18原始日志，断电复查RF2/IPEX扣接和天线方向，再做UART-only/ADB停止的抗干扰对照；仍弱时测量GNSS开启/关闭时RF2有源偏置是否约3.3 V/0 V，并用已知正常天线或开发板做交叉验证。不制作新镜像。
6. **BQ25895 watchdog 警告**：当前无功能阻断证据，但需在最终回归中记录。
7. **第三方依赖**：GitHub 不包含 Tina SDK、工具链、PhoenixCard 或厂商许可内容。
8. **许可**：仓库尚未声明开源许可证；公开可见不等于允许重新分发第三方内容。
9. **历史候选很多**：版本目录代表可追溯记录，不代表每版都推荐烧录；必须先看状态行。

下一步和精确验收命令见 `docs/NEXT_IMAGE.md`。
