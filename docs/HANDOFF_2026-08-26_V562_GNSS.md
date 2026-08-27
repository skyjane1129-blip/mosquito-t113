# Mosquito v5.6.2 GNSS 实板测试交接

更新时间：2026-08-27（Asia/Hong_Kong）

## 1. 新对话的唯一当前任务

使用已经烧录并确认身份的 `dev-v5.6.2-adb-on-validated` 候选镜像，继续诊断 Air780EG 的 GNSS 接收质量并完成真实室外定位验收。

当前已完成 15×15 mm 天线的室内测试及两轮室外测试，但均未定位；18×18 mm 天线的首轮结果暂从拼接日志推定，同样未定位且仍需唯一命名的原始日志确认。下一步不是继续按尺寸盲目换天线，而是确认日志归属、复查 RF2/IPEX 扣接和摆放、做 UART-only/停止 ADB 的干扰对照、检查 RF2 有源偏置，并在条件允许时用已知正常天线或开发板交叉验证。

本任务是现有 v5.6.2 的实板验收，不是制作新镜像。出现异常时必须先在当前已烧录系统上保存证据、定位和临时验证，不得直接构建 v5.6.3。

新 Agent 开始前必须依次阅读：

1. `AGENTS.md`
2. `docs/CURRENT_STATE.md`
3. `docs/NEXT_IMAGE.md`
4. 本文件
5. `releases/mosquito-t113/2026-08-26-dev-v5.6.2-adb-on-validated/README.md`

## 2. 最重要的版本边界

v5.6.2 镜像已经完整构建、官方 `pack`、SquashFS 反查和 SHA-256 校验。2026-08-26后续又收到连接实板的Windows PowerShell原始输出：`adb devices -l`显示`MOSQUITO-T113-DEV device`，`adb shell mosquito-version`显示`dev-v5.6.2-adb-on-validated`/`2026-08-26`/`2.17-1`，`command -v gnss-test`返回`/usr/bin/gnss-test`。因此当前v5.6.2运行身份门槛已通过；GNSS室外定位本身仍未完成。

在这次新证据之前，最后一次明确的旧实板版本输出曾是：

```text
MOSQUITO_IMAGE=dev-v5.6.1-adbd-background-hotfix
MOSQUITO_BUILD_DATE=2026-08-25
MOSQUITO_BOARD_PACKAGE=2.16-1
```

上述v5.6.1输出仅保留为历史背景，已被当前v5.6.2实板身份证据取代。为避免后续换板或重新烧录后误用证据，每个新的实板验收会话仍先运行：

```powershell
adb devices -l
adb shell mosquito-version
```

只有看到以下身份，才能把后续结果记为 v5.6.2 精确成品证据：

```text
MOSQUITO_IMAGE=dev-v5.6.2-adb-on-validated
MOSQUITO_BUILD_DATE=2026-08-26
MOSQUITO_BOARD_PACKAGE=2.17-1
```

如果未来任何一次复查不再是上述v5.6.2身份，应停止“v5.6.2验收”的表述，不要自行烧录，也不要把其他版本的GNSS结果写成v5.6.2已通过。

## 3. v5.6.2 成品位置与 Windows 边界

```text
releases/mosquito-t113/2026-08-26-dev-v5.6.2-adb-on-validated/mosquito-t113-dev-v5.6.2-adb-on-validated.img
```

```text
大小：11,508,736 bytes
SHA-256：f0d24710f165122fe7539098c777d15f25c52d0b73874ac6a3ca421b38edaf08
```

它是 Allwinner/PhoenixCard 七分区整卡镜像，只能使用 PhoenixCard“启动卡 / Startup”模式；不能使用 Etcher 或普通 `dd`。烧录会重建目标TF卡分区，必须先备份照片和日志并再次核对目标盘符。

上述是仓库相对位置，Windows 和 Linux 均应从仓库根目录解析。实际 `.img` 被 `.gitignore` 排除，不会随普通 `git clone` 下载；当前板子已经烧录且已确认 v5.6.2 身份，继续 GNSS 诊断不需要重新烧录。只有确需再次烧录时，才从受控的本地版本目录或以后经用户授权建立的 GitHub Release 获取镜像，并先核对 SHA-256。

## 4. 当前已知实板状态

2026-08-26已完成v5.6.2精确身份下的首轮5分钟`gnss-test`：Air安全状态、3.9 V使能、UART、AT、GNSS开启和关闭全部PASS，但所有搜索输出都是`fix=0`、`UTC=none`、卫星`0/0/0`和`C/N0-max=0`，最终`PASS=6 FAIL=0 WARN=1`。用户后续确认当轮使用`15×15×4.6 mm`天线且测试在室内执行；天线确切摆放和RF2扣接状态未记录。因此当前结论只是“软件/模组控制链通，15×15 mm天线在室内未观测到卫星”，不能判定天线或RF2硬件故障。

同日重启后，UART一条`adb-on`完成FunctionFS/UDC绑定并进入`USB_STATE=CONFIGURED`。Windows经扩展坞连接时一度为`offline transport_id:4`，改为电脑USB口直连后立即为`device transport_id:5`。当前GNSS测试应保持直连USB拓扑，不把扩展坞的额外枚举/供电/信号完整性变量混入GNSS结果。

改为直连后的第二轮5分钟`gnss-test`仍使用同一`15×15×4.6 mm`天线：搜索从卫星`0/0/0`进展为`1/0/0`和`3/0/0`，`C/N0-max=31–32`，且解码出了有效UTC，但全程仍为`fix=0 mode=1`，最终`PASS=6 FAIL=0 WARN=1`。这已排除“天线供电/RF2射频路径完全无信号”，但定位未通过；当轮是否已在室外开阔天空尚需现场确认。

用户之后按室外R1步骤、保持电脑USB口直连和同一`15×15×4.6 mm`天线又完成一轮完整5分钟测试：最多观测3颗卫星，0颗参与定位，C/N0仅25–26 dB-Hz，虽解码出UTC但仍为`fix=0 mode=1`，最终`PASS=6 FAIL=0 WARN=1`且正常执行`GNSS-power-off`。设备内日志已经拉取为Windows文件`gnss-v562-15x15-outdoor-r1-device.txt`，测试后ADB仍为`device transport_id:5`。下一步应保持同一天线、位置、方向和直连拓扑紧接做室外R2；若仍只有不超过3颗且C/N0偏低，再记录环境细节并断电换18×18 mm天线做同条件对比。

紧接完成的室外R2最多观测4颗卫星、C/N0为25–27 dB-Hz，并能持续解码UTC，但统计始终为`4/0/0`，即4颗可见、0颗参与GNSS定位、0颗参与GLONASS定位；全程仍为`fix=0 mode=1`，最终`PASS=6 FAIL=0 WARN=1`且`GNSS-power-off`通过。R2只比R1多看见1颗，没有解决接收质量不足。下一步先拉取R2设备日志，随后完全断电换18×18 mm天线做同地点、同方向对比；若结果仍同样弱，再检查GNSS_VCC有源偏置、天线工作电流、IPEX和RF路径。

2026-08-27用户回传了一组拼接日志，最后一段结合其刚报告18×18测试不理想的操作顺序，暂按18×18室外R1：Air需一次安全PWRKEY启动脉冲，随后AT和GNSS开关正常；5分钟大部分时间0颗卫星，后期仅1颗，C/N0从28降至21–24，直到`UTC=20260826090312`才解出时间，始终`1/0/0`、`fix=0 mode=1`，最终`PASS=6 FAIL=0 WARN=1`。因来源文件没有唯一标签，仍需确认这最后一段确属18×18。若确认，下一步应断电复查扣接与摆放，做UART-only/ADB停止对照，随后检查RF2有源3.3 V偏置并做已知正常天线/开发板交叉验证。

以下能力曾在 v5.6.1 可写 overlay 上完成实板验证，并已固化到 v5.6.2 成品；v5.6.2身份已确认，其余最终复核和GNSS室外定位仍待执行：

- UART0 Root Shell正常。
- 手动一条 `adb-on` 后，FunctionFS具有 `ep0/ep1/ep2`，UDC为 `configured`。
- Windows `adb devices -l` 显示 `MOSQUITO-T113-DEV device`。
- ADB为root、无认证，shell/push/pull和JPEG传输正常。
- `usb0-adb-stop` 后可再次用 `adb-on` 恢复。
- 独立供电时USB0热拔插可以恢复，adbd PID保持稳定。
- `camera-test0`、实拍、照片元数据和ADB下载已有通过记录。
- `sim-test`、`air-test`、LTE注册、PPP、DNS、HTTPS、NTP及ADB共存已有通过记录。
- `4g-start` 已修复重复加载PPP模块问题；`4g-stop` 已修复双行PID文件问题。
- GNSS工具已经实现，但真实室外定位尚未完成，是本次交接的主要未验项。

当前会话最近涉及的照片路径是：

```text
/mnt/UDISK/mosquito-test/camera/photo-hd-focus-500-000304.jpg
```

这不是GNSS测试的前置条件，只说明ADB文件通道正在使用。

## 5. 天线与PCB连接事实

用户报告：有源陶瓷 GNSS 天线已经到货，标称支持 3–5 V、50 Ω、GPS L1/BeiDou B1、120 mm 馈线和 IPEX 一代接头；工作电压与板上 3.3 V `GNSS_VCC` 相容。当前已测试 15×15×4.6 mm 和 18×18 mm 个体，但尚未记录明确品牌/型号、3.3 V 下典型及最大工作电流和完整 LNA 参数，三种陶瓷尺寸也尚未完成相同条件下的实板对比。

PCB网表 `hardware/pcb/netlist/Netlist_PCB1_2026-07-29.tel` 能确认：

```text
RF1 -> LTE_ANT -> Air780EG U11.35
RF2 -> GNSS_ANT -> D2/L5 -> Air780EG U11.2
GNSS_VCC -> L5 -> Air780EG U11.8
```

因此板上 `RF2` 是GNSS天线口，`RF1` 是LTE天线口。网表显示GNSS射频路径带有 `GNSS_VCC`/L5 偏置路径，但仅凭网表不能确认用户购买天线所需的精确电压、电流和连接兼容性。

物理连接前必须：

1. 确认连接的是 `RF2/GNSS`，不要误插 `RF1/LTE`。
2. 核对天线频段、接头类型和有源供电要求与Air780EG/PCB相容。
3. 关闭开发板电源后再扣接微型同轴连接器，垂直对准，不能斜压或硬掰。
4. 陶瓷贴片的接收面朝向天空，尽量远离金属板、USB线束、4G天线和强干扰源。
5. 测试地点必须在室外并具有尽可能开阔的天空视野；室内无定位不能判定硬件故障。

如果天线规格不明确，先停在规格核对阶段，不要通过试插试错判断供电兼容性。

## 6. `gnss-test` 的真实行为

`gnss-test` 是 `mosquito-board-test` 的命令链接，使用 Air780EG 主UART `/dev/ttyS1`。其当前源码行为是：

1. 检查 `4g-start` 锁、`ppp0` 和 pppd PID，避免与PPP争用UART。
2. 将PWRKEY与RESET控制置于安全状态，绝不脉冲RESET_N。
3. 拉起 PE1/4G_EN；若模组没有AT响应，只允许一次1.2秒PWRKEY启动脉冲。
4. 以115200 8N1、无流控方式独占打开 `/dev/ttyS1`。
5. 发送 `AT+CGNSPWR=1` 打开GNSS。
6. 每约2秒查询一次 `AT+CGNSINF`，最长等待5分钟。
7. 定位成功后报告耗时、UTC、模式、海拔、HDOP、卫星统计和最大C/N0。
8. 默认只说明获得有效坐标，不在日志中打印精确经纬度。
9. 正常结束或收到SIGINT/SIGTERM时发送 `AT+CGNSPWR=0`，再关闭UART。

日志位置：

```text
/tmp/mosquito-gnss-test.log
/overlay/mosquito-test/gnss-test.log
```

诊断程序横幅目前仍可能显示历史文字 `board package 2.10`。镜像身份应以 `mosquito-version` 的 `MOSQUITO_BOARD_PACKAGE=2.17-1` 为准，不要仅凭横幅误判镜像版本。

## 7. 新对话建议的第一轮命令

### 7.1 确认ADB与版本

Windows PowerShell：

```powershell
adb devices -l
adb shell mosquito-version
```

如果设备不是 `device`，先通过UART确认当前系统状态和是否需要执行 `adb-on`。不要在没有版本身份的情况下继续记录验收结论。

### 7.2 确认PPP已经停止

2026-08-26已有一次连板证据通过这一门槛：`4g-stop`报告PPP未运行，`pidof pppd`无输出，`ip link show ppp0`报告设备不存在。更换天线并重新上电后，实际定位轮次前仍应快速复查。

```powershell
adb shell 4g-stop
adb shell "pidof pppd"
adb shell "ip link show ppp0"
```

通过条件：`pidof pppd`没有输出，`ip`报告找不到 `ppp0`。最后一条因此返回错误文字是预期结果。

GNSS、`air-test`、`sim-test` 和PPP共用 `/dev/ttyS1`，运行GNSS时不要同时执行 `4g-start`、`air-test` 或 `sim-test`。ADB走USB0，可以保持在线。

### 7.3 在室外运行默认隐私模式

连接并摆放好天线后，让命令完整运行，最长约5分钟：

```powershell
adb shell gnss-test
```

不要因为第一分钟没有定位就中断。测试期间不要启动PPP、拔掉天线或切断开发板电源。

### 7.4 保存日志

```powershell
adb pull /tmp/mosquito-gnss-test.log .\mosquito-gnss-test-v562.txt
adb pull /overlay/mosquito-test/gnss-test.log .\mosquito-gnss-test-v562-persistent.txt
```

若要把输出发给Agent，默认日志不含精确坐标，可以直接提供；仍应检查是否混入ICCID、私人地址或其他敏感数据。

只有用户明确需要现场核对坐标时，才使用：

```powershell
adb shell 'MOSQUITO_GNSS_SHOW_COORDS=1 gnss-test'
```

该输出包含精确位置，不应提交GitHub，也不要在公开聊天中完整粘贴。

## 8. 通过条件

一轮成功至少应包含：

```text
[PASS] Air-safe-state
[PASS] Air-3V9
[PASS] Air-UART
[PASS] Air-AT
[PASS] GNSS-power-on
[PASS] GNSS-fix 3D/2D fix acquired ...
[INFO] GNSS-quality ...
[INFO] GNSS-location valid coordinates received; hidden from logs by default
[PASS] GNSS-power-off
```

同时记录：

- 测试日期、时区和大致环境，不记录公开精确位置；
- 天线型号、连接口和摆放方式；
- 冷启动/热启动背景；
- 首次定位耗时；
- fix mode、HDOP、卫星数和最大C/N0；
- `gnss-test` 汇总中的 PASS/FAIL/WARN；
- ADB在测试前后是否持续为 `device`。

完整验收建议至少做3轮室外测试，其中至少1轮从开发板和Air780EG冷启动开始。不能只凭一次偶然定位直接宣布所有GNSS硬件长期稳定。

## 9. 失败时如何判断

5分钟无定位不等于需要新镜像。先保存完整日志并按现象分类：

- `Air-AT`失败：先检查Air780EG供电、UART和PWRKEY路径，不要先怀疑GNSS算法。
- `GNSS-power-on`失败：保存AT原始响应，检查模组命令支持和当前UART占用。
- `run=1`但卫星数始终为0、C/N0无有效值：优先检查是否误接RF1、连接器、天线供电兼容性、RF路径和天空环境。
- 能看到卫星/CN0但 `fix=0`：保留完整5分钟，改善天空视野和天线摆放，再做独立重复测试。
- ADB中断但UART仍在：先保存串口、UDC和adbd日志；不要把ADB故障和GNSS射频故障混为一谈。
- Ctrl+C或连接中断后没有看到 `GNSS-power-off`：不要直接开始PPP，先确认GNSS和UART状态，必要时在安全的独占UART条件下重新运行测试完成清理。

发现失败后遵循 `AGENTS.md`：当前镜像复现、保存证据、定位、临时验证、回归，然后向用户汇总；未经明确授权不得制作新镜像。

## 10. 下一版本ADB方向（本次不实施）

已经商定的后续优化是：保留 v5.6.2 已验证的手动 `adb-on` 作为唯一底层流程，在明确的 userspace-ready 节点增加一次性自动调用。禁止恢复v5.5.x的 `rc.preboot` 早期启动或另写并行USB流程。

该方向与本次GNSS测试分开。本次不得因为GNSS测试或对话切换而开始开发、构建或发布自动ADB新镜像。

## 11. 可直接粘贴到新对话的开场提示

```text
请先完整阅读仓库根目录 AGENTS.md、docs/CURRENT_STATE.md、docs/NEXT_IMAGE.md、docs/HANDOFF_2026-08-26_V562_GNSS.md 和当前 v5.6.2 发布说明。现在只继续 dev-v5.6.2-adb-on-validated 的实板 GNSS 诊断与验收，不制作新镜像。该镜像身份和 ADB 已确认；15×15 mm 天线室外 R1/R2 最多观测 3/4 颗卫星但均未定位，18×18 mm 的拼接日志最后一段暂定同样未定位且需原始文件确认。请先检查当前分支和工作区，再让我保存并唯一命名 18×18 原始日志，断电复查 RF2/IPEX 和天线摆放，随后做 UART-only/停止 ADB 的干扰对照；仍弱时检查 GNSS 开/关状态下 RF2 有源偏置，并用已知正常天线或开发板交叉验证。任何失败都先在当前镜像保存证据、定位和临时验证，不要直接生成系统镜像。
```
