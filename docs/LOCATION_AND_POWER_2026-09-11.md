# 定位精度与低功耗设计（2026-09-11，当前镜像验证，未制作镜像）

本文记录在已烧录的精确 `dev-v5.6.4-client-direct` 成品上，用临时部署（板端 `/data/local/tmp/remote-demo/`）完成的定位精度与低功耗改造与实测。未修改镜像、未 `make`/`pack`、未 commit。板端 GNSS 天线仍待硬件确认（见 `docs/CURRENT_STATE.md` 与 2026-08-26 GNSS 交接）。

## 1. 定位精度

### 板端（`src/mosquito-lbs.c`，交叉编译 musl ARM）
新增三条能力，全部走 Air780EG 主 UART `/dev/ttyS1`，与 PPP/air-test 互斥：

- `--locate`：单基站 LBS（`AT+CIPGSMLOC`，合宙官方端点），并额外用 `AT+CCED=0,1`/`0,2` 采集服务小区与最多 6 个邻区（MCC、MNC、TAC、E-CI、PCI、EARFCN、RSRP、RSRQ）。结果 JSON 增加 `cells` 数组与 `accuracy_m`。先读小区、再等注册（`AT+CEREG?`/`AT+CGATT?` 轮询）、再开承载查 LBS，因此即使 LBS 承载暂时开不出，也已经拿到小区列表供服务端多基站定位。
- `--gnss SECONDS [--no-agnss]`：打开内置 GNSS，默认先 `AT+CGNSAID=31,1,1,1` 做 AGNSS（时间+EPO 星历+粗位置辅助），再周期 `AT+CGNSINF` 等有效定位；输出含 HDOP、可视/参与卫星数、C/N0、TTFF；进度行不含坐标。
- `--at 'AT+...'`：串口 AT 诊断透传（用于低功耗/注册排查）。

固件为 `AirM2M_780EG2_V2007_LTE_AT`。注意该固件把 `AT+CEREG?` 有时回成 `+CGREG:`，`AT+CFUN=1,1` 复位可恢复正常 `+CEREG:` 上报。`--self-test` 覆盖 CCED（含手册与 V2007 两种空格写法）、CGNSINF、LBS 解析，交叉编译前在 x86 与板上均通过。

### 服务端（`mosquito-cloud-service`，`LocationEngine.cs`）
每次板端定位都带小区列表上报 `/api/device/location`。服务端并行调用可配置的多基站定位器，再融合：

- `LBS_AMAP`：高德智能硬件定位 `restapi.amap.com/v5/position/IoT`，主基站入 `bts`、邻区入 `nearbts`，返回 GCJ02。
- `LBS_BAIDU`：百度智能硬件定位 `api.map.baidu.com/locapi/v2`，`bts` 用 `|` 分隔多基站，请求 GCJ02。
- `CELL_OPENCELLID`：OpenCellID 逐基站查库后按 RSRP 加权质心（覆盖国内有限）。
- `FUSED`：对所有基于小区的估计做逆方差加权（权重=1/半径²，单基站默认半径 500 m），半径下限 20 m。
- `FUSED_MEDIAN`：对本设备最近 24 小时的 `FUSED` 历史取分量中位数（固定点稳态更稳，抗个别错误基站）。

选择策略 `Location:Policy`（默认 `auto`）：有 GNSS 用 GNSS；否则用 `FUSED_MEDIAN` > `FUSED` > 原始样本。可强制某一源做对照。所有候选都入库。

评估：`GET /api/v2/devices/{id}/location-evaluation?hours=` 给出每个源相对参考点（`Location:Reference*`，仅 `appsettings.Local.json`，不进 Git）的样本数、均值/中位数/P90/最值误差与平均半径，用于横向比对哪种算法在本点最准。

### 2026-09-14 追加：自学习小区表（无需地图 key 的多基站算法）
连续运行两天后攒下 135 个带小区列表的 LBS 样本。发现单基站答案在几个离散位置之间跳（对参考点分别约 37、103、172、540、663、1128 m），取决于模组当时驻留哪个小区；朴素多次中值会收敛到出现次数最多的 540 m 小区，反而不如最好情况。

合宙返回的本质是“驻留小区的位置”，因此每个样本都在教我们一个小区的位置。把它记成“小区→位置”表（已学到 8 个小区），再从板子上报的邻区里挑 RSRP 最强的已知小区，通常就是最近的基站。离线回放（`scratchpad/cell_algos.py`，参考点 GCJ02）：

| 估计器 | 误差中位数 | 均值 | P90 |
|---|---|---|---|
| 单基站合宙（原始） | 540 m | 400 m | 663 m |
| 多次中值（FUSED_MEDIAN） | 540 m | 492 m | 663 m |
| 最强已知小区（CELL_LEARNED） | 103 m | 248 m | 663 m |
| 最强已知小区 + 近 24 次中值（CELL_LEARNED_MEDIAN） | **74 m** | 215 m | 663 m |
| RSRP 加权质心 | 172 m | 262 m | 663 m |

已实现进服务端 `LocationEngine`：新增源 `CELL_LEARNED`、`CELL_LEARNED_MEDIAN`，`auto` 策略顺序改为 GNSS > CELL_LEARNED_MEDIAN > CELL_LEARNED > FUSED_MEDIAN > FUSED > 原始。P90 仍 663 m 是因为部分时刻可见的已知小区只有远的那几个；随样本继续积累会改善。客户端已加对应中文标签。

### 2026-09-14 追加：“快速定位学习”（Learn 指令）
自学习表按地点学习，被动等待要一天（15 min 一次约 96 次才稳定到几十米），原因是模组静止时长期驻留在远处小区，近处小区只占约两成时间。实验：`AT+CFUN=1,1` 让模组重搜网三次，三次都驻留到最近的小区（RSRP −64 dBm）。据此新增操作员指令 `Learn`（客户端“快速定位学习”按钮，`rounds` 默认 5，1–12）：板端 `execute_learn` 每轮 `4g-stop` → `AT+CFUN=1,1` → 等 `MOSQUITO_LEARN_RESEARCH_SEC`（40 s）→ `mosquito-lbs --locate --cells` → 上报位置与心跳；轮间隔不小于 `MOSQUITO_LOCATION_MIN_SEC`（合宙限速）；有任一轮定位成功即回执 Completed。到新地点后点一次约 10 分钟即可让服务端学到近处小区。用途：09-20 前后在闵行申虹路市疾控中心的客户演示（与当前学习地点不同）。

合宙免费单基站定位的官方限制（`docs.openluat.com/air780eg/at/app/command/lbswifi/`）：**2 分钟最多 1 次**，超频返回失败；免费库日访问量 7 亿次以上，“服务器繁忙也是正常的”，因此 `CODE=7` 既可能是超频也可能是拥堵。首次 2 轮学习实测两轮均 `CODE=7`（重启后 40 s 查询）；代理已改为每轮失败后等 `LOCATION_MIN_SEC+10` s 不重启再重试 `MOSQUITO_LEARN_RETRIES`（默认 1）次。重启后需等多久、是否只是拥堵，见板上 `lbs-limit-test.sh` 的定量结果（本文下方追加）。

限频定量测试（2026-09-14，板上 `lbs-limit-test.sh`/`lbs-limit-test2.sh`，代理停用期间，同一 IMEI）：

| 查询 | 距上次查询 | 之前是否重启模组 | 结果 |
|---|---|---|---|
| A | 13.0 min | 否 | 成功 |
| B | 2.8 min | 是（150 s 前） | CODE=7 |
| C | 10.2 min | 否 | 成功 |
| D | 12.5 min | 是（12 min 前） | 成功 |
| E | 5.4 min | 否 | CODE=7 |
| F | 2.7 min | 否 | CODE=7 |

结论：模组重启不影响查询（D）；本设备的有效限频约 **10 分钟一次**（可能因连日高频查询被合宙降级），远严于文档的 2 分钟。据此：`MOSQUITO_LOCATION_MIN_SEC` 600、学习轮间隔 `MOSQUITO_LEARN_QUERY_GAP_SEC` 600、默认 3 轮（约 35 min），第 1 轮成功即已学到近处小区并在客户端显示，第 3 轮后进入“多次中值”。演示当天在申虹路提前 1 小时按一次“快速定位学习”即可。

### 实测结论（室内窗边，单 SIM，中国联通 LTE）
- LBS 单基站 + 融合：与参考点误差约 **660 m**（仅单基站、无高德/百度 key 时融合≈单基站）。这印证单基站 LBS 精度为数百米到公里级。
- 内置 GNSS + AGNSS：室内 240 s 仍 `fix=0`、可视卫星 0，与既有 GNSS 硬件结论一致（天线/LNA 待查）。
- **结论：≤20 m 只能靠 GNSS（解决天线后）；4G 侧多基站在上海城区通常 50–300 m，达不到 20 m。** 多基站需要高德或百度的 Web 服务 key 才能真正生效并压误差。

## 2. 低功耗

板端代理 `files/mosquito-remote-agent` 新增 `MOSQUITO_POWER_PROFILE`：

- `always-on`（默认，演示用）：保持 PPP、长轮询，远程指令约 1 秒内领取，功耗最高。
- `duty`：一次即时轮询无指令后，`4g-stop` 断开 PPP、`AT+CSCLK=2` 让模组串口浅睡、整机空闲 `MOSQUITO_IDLE_SLEEP_SEC` 秒再重连。远程指令在“空闲时长 + 重连（约 10–15 s）”内领取。实测 `duty`（空闲 40 s）下发远程拍照仍成功：领取 5 s、完成 56 s（拍照+4G 上传本身约 50 s）。

当前镜像内核未开 CPUFreq/CPUIdle/RTC（`config-5.4` 中 `# CONFIG_CPU_FREQ is not set`、`# CONFIG_CPU_IDLE is not set`、`# CONFIG_RTC_CLASS is not set`；`CONFIG_SUSPEND=y`、`CONFIG_PM_SLEEP=y`、`mem_sleep=[s2idle]`）。因此 `duty` 只能省“模组射频/联网”功耗，T113 主控仍全速空转。摄像头已在拍照后由 PE0/CAM_EN 断电，空闲不耗电。

**低功耗的主要收益需要 v5.6.5 内核，且必须实板验证后才能进镜像：**
- 开启 CPUFreq + 合适 governor、CPUIdle（需确认 sunxi T113 平台 OPP/驱动，盲开可能不生效或编不过）。
- 补齐 RTC 驱动并验证 `freeze`/`mem` 挂起与 RTC 定时唤醒；配合 `duty` 做“挂起—定时/模组 RI 唤醒—联网领指令”。模组 RI 已接到 T113 PE7（网表 `AIR_T113_RI ; Q3.3 ... U1.40`，PE7 唤醒），可作唤醒中断。
- 模组 PSM/`AT+POWERMODE`（µA 级）需 LPAT 固件，且 PSM 下服务端无法远程唤醒，只适合“定时上报”而非“随时远程拍照”，与演示②冲突，作为长期野外方案备选。

功耗数值：手边无 USB 电流表/万用表，未量化 mA。BQ25895 是充电器不直接给放电电流，只能用电池电压随时间下降粗估。量化待仪表。

## 2b. 用户决定（2026-09-13）与实测方法

- 参考点坐标取自地图 App，即 GCJ02；服务端评估参考系按 GCJ02。
- 远程指令可接受 3–5 分钟延迟：`duty` 档 `MOSQUITO_IDLE_SLEEP_SEC` 建议 240 s（最坏领取约 4–5 分钟），`cloud.conf.example` 默认已改为 240。演示②仍用 `always-on`（约 1 秒领取）。
- 用户有万用表，无地图厂商 key（待注册）。多基站定位在拿到高德/百度 key 前无法压误差。

万用表实测整机电流（用户操作，需断线串入电流表）：
1. 拔掉 Type-C（全部电流走电池，才能测真实待机）。
2. 万用表拨到直流电流、用 10 A 挡（4G 发射瞬时可超 0.5 A，mA 挡保险丝会烧）。
3. 断开电池正极（CN1），把电流表串入电池正极与板子之间。
4. 分别记录 `always-on` 与 `duty`（空闲 240 s）下的：空闲电流、心跳/轮询瞬时、4G 发射峰值、以及一分钟平均。
5. 对照即可量化 duty 省下的射频/联网功耗。软件侧交叉参考：电池供电时 `mosquito-power --machine` 的 `BATTERY_MV` 随时间下降速度（BQ25895 不直接给放电电流）。

## 2c. 内核级低功耗调研结论（2026-09-14，只读核对 SDK，未改源码）

| 项 | SDK 现状 | 结论 |
|---|---|---|
| CPU 实际频率 | `clk_summary`：pll-cpux/cpux = **720 MHz**；`sys_config.fex` `boot_clock=720` | 已经是频率表第二低档（表：480@0.90 V、720@0.90、912@0.95、1008@1.00、1104@1.05、1200@1.10 V），板子 VDD_CPU 为固定 0.95 V 稳压器（`board.dts` `reg_vdd_cpu`），本就不可调压 |
| cpufreq | `# CONFIG_CPU_FREQ is not set`；`cpufreq-dt-platdev.c` 白名单无 sun8iw20；`sun50i-cpufreq-nvmem.c` 存在但无 Kconfig 符号、且需 speed-bin nvmem；无 sunxi 专用驱动 | **不是配置开关，需驱动/DT 适配；收益仅空闲 720→480 MHz。v5.6.5 不做** |
| cpuidle | `# CONFIG_CPU_IDLE is not set`；`sun8iw20p1.dtsi` 有 PSCI idle-states，但 `mosquito/board.dts` 明确 `/delete-property/ cpu-idle-states`、`/delete-node/ idle-states` 并把 enable-method 改为 `allwinner,sun8iw20p1`；参考板型 100ask/default 亦未开 | **需重新 bring-up（PSCI CPU_SUSPEND 固件支持未知，有挂死风险）。v5.6.5 不做** |
| RTC | `drivers/rtc/rtc-sunxi.c` 兼容 `allwinner,sun8iw20-rtc`（v200）；`sun8iw20p1.dtsi` 有 `rtc@7090000` 且 `wakeup-source`；`# CONFIG_RTC_CLASS is not set` | **配置开关即可：`CONFIG_RTC_CLASS=y`、`CONFIG_RTC_DRV_SUNXI=y`（可加 `CONFIG_RTC_HCTOSYS=y`）。建议进 v5.6.5**，烧录后验证 `/dev/rtc0`、`wakealarm` 与 `echo freeze > /sys/power/state` 定时唤醒 |
| 挂起 | `CONFIG_SUSPEND=y`，`mem_sleep` 仅 `[s2idle]`，无 sunxi 深度待机固件项 | 有 RTC 后可做第三档 `sleep` 省电（睡 N 分钟由 RTC 唤醒再联网领指令）；无 cpuidle 时 s2idle 收益有限，需实测 |

功耗量化仍待用户用万用表按 2b 节步骤实测（always-on / duty 两档）。

## 3. 若制作 v5.6.5（候选，需用户明确批准）
见 `docs/NEXT_IMAGE.md` 0B 节。定位相关新增文件已就绪并实板验证：更新后的 `mosquito-lbs`（cells+GNSS/AGNSS）、`mosquito-remote-agent`（location 模式+power 模式）、`mosquito-cloud.conf.example`。内核低功耗（CPUFreq/CPUIdle/RTC/挂起）建议单独作为一档，先在当前镜像不改内核的前提下用 `duty` 验证联动，内核项按 AGENTS.md 复现—定位—临时验证—回归后再入镜像。
