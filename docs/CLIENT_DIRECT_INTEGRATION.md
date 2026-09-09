# Windows 客户端直连模式与板端接口对照

更新时间：2026-09-09（Asia/Shanghai）

本文用于 WSL 板端 Agent 与 Windows 客户端 Agent 直接读取对方工作树时对齐接口。客户端权威工作树为 `C:\project\mosquito\mosquito-windows-client`，WSL 只读路径为 `/mnt/c/project/mosquito/mosquito-windows-client`；板端权威仓库为 `/home/janelinux/work/mosquito/mosquito-t113`。本阶段只验收直连模式。

当前结论（2026-09-09）：`dev-v5.6.4-client-direct` 已烧录，原生板端直连子集与 Windows WPF 真实按钮闭环均通过。自动对焦的命令与文件协议保留，光学画质按用户决定等相机固定后再验。尚待补做三次物理冷启动/UART、USB 物理热拔插及 UART 条件下的 ADB stop/start。

## 1. 客户端按钮与板端契约

| 客户端入口 | Windows 实际动作 | 板端命令 | 板端返回 | 验收方法 |
| --- | --- | --- | --- | --- |
| 登录（工程师） | 本机校验开发账号，解锁直连工作区 | 无 | 无 | 登录前不访问板子；登录成功后才允许检测、采集和读取 |
| 检测设备 | Windows PnP/WinUSB 检查；`adb version`、`adb devices -l`；只读 shell 探测 | `export PATH=/usr/bin:/bin; echo MOSQUITO_PROBE=1; mosquito-version; command -v mosquito-capture mosquito-environment mosquito-power` | `MOSQUITO_PROBE=1`、版本键值、三个 `CAP:<命令>=1/0` | 目标 USB/ADB 唯一且状态为 `device`；三个命令必须来自 `/usr/bin`；成品声明 `MOSQUITO_PHOTO_METADATA=3` |
| 读取设备信息 | 依次发两个 ADB shell；结果只显示为独立实时测量 | `mosquito-environment --machine`；`mosquito-power --machine`，均使用配置的 PATH | 环境：`RESULT`、`ERROR_CODE`、`SAMPLED_UPTIME_MS`、`TEMPERATURE_CENTI_C`、`HUMIDITY_CENTI_RH`、`CRC_OK`。电源：`RESULT`、`ERROR_CODE`、`SAMPLED_UPTIME_MS`、`BATTERY_MV`、`CHARGE_STATE`、`VBUS_GOOD`、`VBUS_MV`、`CHARGE_CURRENT_MA`、`FAULT_REG` | 保存 stdout、stderr 和退出码；数值范围与单位正确；DHT30 CRC 为 1；BQ25895 非零故障寄存器必须显示警告，不伪造电量百分比；此结果不得替换照片关联数据 |
| 采集并保存本机（手动对焦） | 生成 UUID，记录 Windows 起止时间；运行板端命令；拉取两个文件；校验后写入本机 SQLite | `mosquito-capture --id <UUID> --focus <整数>`，PATH 同上 | 板端语义退出 0 表示 `COMPLETE`，退出 10 表示 `PARTIAL`；stdout 必须有唯一的 `PHOTO=`、`METADATA=`、`CAPTURE_ID=`、`CAPTURE_RESULT=` | `capture_id` 与请求 UUID 一致；metadata v3；焦距在相机 min/max/step 内且前后回读一致；JPEG 3264×2448、字节数与 SHA-256 同元数据；照片与 metadata 均能 ADB pull 且本地哈希一致；传感器区与该照片同一元数据文件关联。本机 ADB 不透传远端退出码，Windows 必须按第 6 节封装命令 |
| 采集并保存本机（自动锁焦） | 与手动模式相同，参数改为 `auto` | `mosquito-capture --id <UUID> --focus auto` | 同上；元数据还应记录自动对焦采样窗口、选择值、稳定或 timeout、锁焦回读 | 必须完成最近 5 个有效样本判稳或明确 timeout；`focus_selected`、拍摄前后回读、`focus_lock_verified=yes`、`focus_auto_before_capture=0`；再做 JPEG、关联数据和 ADB 传输校验 |
| 上传待传记录 | 从本机 SQLite 读取待传照片和元数据，调用云 API；直连采集本身不要求云端登录 | 无新的板端命令 | 无 | 属于 Windows/云端联调；失败不能删除本机或板端原始文件。本阶段板端验收不把上传成功作为拍照通过条件 |
| 保存照片 / 放大原图 / 记录查询 | 读取已经拉到 Windows 的文件或本机 SQLite | 无 | 无 | 不重新调用板端、不改变照片与传感器快照关联 |
| 取消当前操作 | 取消 Windows 侧异步操作和 ADB 进程 | 没有独立取消协议 | 未定义 | 当前需作为风险项：Windows 取消不等于板端脚本一定收到信号；应确认相机最终断电、锁目录清理、`.part` 不被当成成品 |

## 2. metadata v3 最低契约

客户端会拒绝以下任一不满足的采集：

- 根区 `metadata_version=3`；
- `capture_id` 与客户端本次请求 UUID 相同；
- `record_status` 只能为 `COMPLETE` 或 `PARTIAL`；
- `photo`、`jpeg_sha256`、`jpeg_bytes`、`jpeg_width`、`jpeg_height` 存在且可解析；
- ADB 拉回照片的 SHA-256、字节数和 JPEG 尺寸与元数据一致；
- `[environment]` 与 `[power]` 来自同一个 `.jpg.txt`，缺失时记录为失败/不完整，不能拼接后来独立读取的数值。

对焦、采样时间、电源详情及完整 V4L2 信息由客户端保存为扩展字段；板端仍应原样提供，供验收和故障分析。

## 3. 三端职责

| 板端 | Windows 客户端 | 云端 |
| --- | --- | --- |
| 控制相机电源、对焦、拍照、JPEG 完整性检查、TF 原子落盘与同步 | 唯一匹配 USB/ADB 设备、生成采集 UUID、发命令、保存主机时间 | 鉴权、持久保存记录、对象上传目标、列表/详情和跨设备查询 |
| 在曝光前读取 DHT30/BQ25895，把结果写入同一照片元数据 | 拉取照片与元数据，复算哈希/字节/尺寸，拒绝错配 | 校验上传清单和完成状态，向 Windows 返回记录状态 |
| 返回稳定的机器可读键值、退出码、文件路径；失败时关闭相机并保留诊断日志 | 保存本机照片与 SQLite 队列；展示 `COMPLETE`/`PARTIAL` 和真实单位；不虚构缺失值 | 远程设备、计划、GNSS、上报检查属于延期范围，本阶段不作为直连门槛 |
| v5.6.4 当前实板原生提供三个直连命令和 metadata v3；精确成品板端回归已通过 | Windows Agent 负责修改、构建和界面联调；真实 WPF 按钮与普通生产配置检测均已通过 | 真实生产部署、OSS、PostgreSQL 和正式账号仍按既有延期范围 |

## 4. 2026-09-08 构建前基线和未提交边界

- 当前实板身份需每轮以 `mosquito-version` 为准。2026-09-09 已确认 v5.6.4、构建日期 2026-09-08、板级包 2.18-1、metadata v3；三个新命令均来自原生 `/usr/bin`，关键哈希与发布镜像一致。
- 板端工作区当时已有未提交的 2.18-1、环境/电源机器接口、metadata v3、采集和 4G 候选，不属于本次 Agent 新写代码。本轮保留脏工作区，只按白名单把已验证的直连子集写入 v5.6.4；4G/云候选没有进入镜像。
- Windows 工作区当时基于 `a9b8b4e`，为 24 个已跟踪修改、33 个未跟踪文件、暂存区为空。真实 WPF 阶段修改过四个生产代码文件和测试 runner；随后生产路径审计又修改 `appsettings.json`、`AppSettings.cs` 及三份客户端说明。这是构建与联调时的历史快照；后续 Git 状态必须以客户端分支实时结果为准。
- `/data/local/tmp/client-demo` 只属于 2026-09-08 的 v5.6.3 历史临时验证。v5.6.4 成品测试已确认该目录不存在，当前不得再把临时路径加入命令优先级。

## 5. 本阶段执行进度

1. v5.6.3 的临时路径验证已完成并作为历史接口依据保留。
2. v5.6.4 已完成构建、官方 `pack`、静态反查、release 归档和 PhoenixCard 烧录。
3. 精确 v5.6.4 成品已完成环境、电源、手动焦距、metadata v3、照片与传感器关联、ADB 文件哈希、非法 UUID、受控 `PARTIAL` 和资源清理回归。
4. Windows 客户端已通过真实 WPF 按钮完成检测、读取、手动采集、本机 SQLite、历史和预览联调。
5. 客户端普通启动配置和代码默认值已从临时路径改为 `/usr/bin:/bin`；Release/Core/模拟 WPF 及生产配置只读实板检测通过。
6. 尚待三次物理冷启动/UART、USB 物理热拔插/正反插，以及 UART Root Shell 在场时的 ADB stop/start。

## 6. 2026-09-08 v5.6.3 临时部署历史结果

证据目录：`build/test-runs/20260908-144221+0800-client-direct/`。测试使用 Windows ADB 37.0.1，经 `MOSQUITO-T113-DEV` 连接；成品身份始终保持：

```text
MOSQUITO_IMAGE=dev-v5.6.3-usb0-adb-autostart
MOSQUITO_BUILD_DATE=2026-09-04
MOSQUITO_BOARD_PACKAGE=2.17-1
MOSQUITO_PHOTO_METADATA=2
```

成品 `/usr/bin:/bin` 内没有 `mosquito-capture`、`mosquito-environment`、`mosquito-power`。本轮只向 `/data/local/tmp/client-demo` 部署候选，不覆盖 `/etc/mosquito-version`、`/usr/bin`、启动项或 SDK。主要临时文件 SHA-256 为：

| 文件 | SHA-256 |
| --- | --- |
| `mosquito-board-test` | `77502f1b29c692888cb41929c2f0ee4e2b9835c17f0d1b4210f52dd6fb0062fb` |
| `camera-test` | `640c3a6327a8bc46513a6eb459e63466803d24ce27afc96f4ec72e5a92cac7d4` |
| `camera-test1` | `79ec77c005a2910d274e99c04c46868b9e4bfb2a79e64b612732fafc76c5f462` |
| 修复后的 `mosquito-capture` | `877390ec31ef75cb242ac90ddf484d6341fceb9bc8405e68ec3056dae1e06db5` |

| 项目 | 结果 | 说明 |
| --- | --- | --- |
| 客户端原样检测命令 | 符合临时阶段预期 | 三个 `CAP:` 都为 1；成品仍诚实声明 metadata v2，因此客户端应显示“已连接、存在待确认项” |
| `mosquito-environment --machine` | 通过 | 两轮各 10/10 `RESULT=PASS`、DHT30 CRC=1；补测中的远端退出码均为 0 |
| `mosquito-power --machine` | 条件通过 | 两轮均 10/10 可读且远端退出码为 0；每轮各有 2 次 `POWER_FAULT_ACTIVE / FAULT_REG=0x80`，说明该告警是瞬态且不能只靠退出码展示 |
| 原始 `mosquito-capture` wrapper | 失败并已修复 | 首次手动采集成功，但 `PHOTO`、`METADATA` 各输出两次；根因是 wrapper 透传底层 marker 后又重复输出。现已在透传日志时过滤这两个 marker，由 wrapper 统一输出一次 |
| 修复后手动焦距 500 | 通过 | 3/3 `COMPLETE`，marker 唯一，焦距请求/选择/拍前拍后回读均为 500，`focus_auto=0`；三张照片目视可辨蚊虫腿、触角和身体轮廓 |
| 修复后自动锁焦 | 控制与文件通过，图像失败 | 3/3 `COMPLETE`，选择 300、280、280，均报告最近 5 点稳定且锁焦回读一致；三张真实照片却明显失焦，不能作为自动对焦通过 |
| 自动对焦定位 | 已定位到场景/固件 AF 结果 | 连续 AF 3 次同样收敛 280 且失焦。诊断副本从 220、500 两个起点在预览后启用 AF，均先移动至 540、再到 280，并在完整 30 秒内保持 280；排除了只因过早锁定造成的假象。当前白色低纹理、目标小且偏离中心的场景下，固件 AF 选错焦平面 |
| metadata v3 与关联 | 文件级通过 | 六组主流程均核对 UUID、照片路径、环境/电源段、采样 uptime 顺序、SHA-256、字节数和实际 3264×2448；六组照片与 metadata 的板端/拉回端共 12 个 SHA-256 全部一致 |
| `PARTIAL` 路径 | 通过（故障注入） | 临时 PATH 注入电源采样退出 7；照片仍保存，metadata 为 v3/`PARTIAL`、`power_sample_exit=7`，wrapper 远端退出 10 |
| ADB 文件传输 | 通过 | 六组照片与 metadata 共 12 次 pull 成功；另取 378864 字节照片做 push/pull 回环，原文件、板端副本、回拉文件 SHA-256 一致 |
| 拍照后资源 | 通过 | `/dev/video0`、相机锁和 `.part` 均不存在，ADB 仍在线，临时候选哈希未改变 |
| 板端时间 | 风险 | 所有 metadata 均为 `system_time_valid=no`、1970 时间；当前可靠关联依赖 UUID、板端 uptime 与 Windows 主机时间 |

文件级校验器的 `PASS` 只表示协议、控制回读、文件完整性与关联通过，不包含照片清晰度。该次自动模式的光学画质在当时未通过；用户随后决定等相机固定后再验，因此它已从本版阻塞项改为明确保留项。

## 7. 交给 Windows Agent 的具体接口差异及完成状态

1. **已完成：修复远端退出码获取。** 实板执行 `adb shell false` 时，Windows ADB 进程仍退出 0；stdout 中显式追加的标记才显示远端为 1。客户端现已为每次 shell 调用生成随机 marker，在远端命令末尾保存语义退出码：

   ```sh
   remote_rc=$?; printf '\n__MOSQUITO_REMOTE_EXIT_<随机值>__=%s\n' "$remote_rc"
   ```

   Windows 从 stdout 唯一且位于末尾的精确 marker 解析远端码，删除 marker 后再把正文交给业务解析器；marker 缺失或冲突视为协议失败，并与 ADB 主机进程失败分开记录。

2. **已完成从临时能力到成品能力的迁移。** v5.6.3 当时保持 2.17-1/metadata v2，三个命令只来自临时 PATH；当前 v5.6.4 已正式声明 2.18-1/metadata v3，并由原生 `/usr/bin` 提供三个命令。

3. **已完成：展示机器输出中的告警。** `mosquito-power` 的 `RESULT=WARN` 即使远端退出 0，实时界面和照片详情仍会显示 `RESULT`、`ERROR_CODE`、`FAULT_REG`。

4. **已完成：补强交叉校验。** 除 UUID、SHA、长度、尺寸外，客户端现已校验 stdout marker 的唯一性、`CAPTURE_ID`、照片/metadata 路径、请求焦距、选择值和控制回读。

5. **已完成：保留直连诊断日志。** 客户端现以 JSONL 保存实际 ADB 命令、stdout、stderr、主机/远端退出码和耗时，并有本机真实 WPF runner 证明按钮触发了调用。

6. **保留：自动模式暂不可标记光学成功。** 控制回读 `stable` 不等于图像清晰。正式验收需等相机固定后，在固定目标、距离和光线下建立清晰度判据；当前使用已经验证的手动 500。

7. **补齐设备身份。** `appsettings.json` 的 `DeviceId` 仍为 `null`，客户端没有与板端身份握手校验。量产前需确定稳定设备编号的配置和核对机制。

Windows 工作树在本轮联调结束时的盘点：分支 `mosquito-windows-client`、HEAD `a9b8b4e`，暂存区为空，24 个已跟踪修改、33 个未跟踪文件。本轮实际修改 `AdbClient.cs`、`CaptureWorkflow.cs`、`MainViewModel.cs`、`PhotoViewModel.cs` 和 UI 测试 runner。该段只记录测试发生时的源码输入，不作为后续 Git 同步状态。

## 8. 当前结果与下一门槛

- Windows WPF 真实按钮已经完成登录、检测、实时读取、手动焦距 500 采集、本机 SQLite、历史和预览闭环，最终进程退出码为 0；
- Windows 已补齐随机远端退出 marker、命令诊断日志、marker/UUID/路径/焦距交叉校验和电源 WARN 展示；
- 直连镜像 `dev-v5.6.4-client-direct` 已生成，板级包为 `2.18-1`、metadata 为 v3；完整 Tina 构建、官方 `pack`、最终容器/rootfs 精确比对和范围审计均通过；
- 延期的新增 4G/云端脚本及板级包 `+jsonfilter` 依赖没有进入镜像，版本文件已改为 v5.6.4；
- 自动对焦历史实拍仍失焦。按用户决定，保留协议与实现，光学画质等相机固定后再验，不影响当前手动直连子集的通过结论；
- v5.6.4 精确成品已烧录；板端原生直连子集与 Windows WPF 真实按钮回归均通过。
- 客户端生产配置和代码默认值已固定为 `/usr/bin:/bin`，使用该生产配置原文件的只读实板检测为 `Connected=true`、`CaptureReady=true`；证据见 `build/test-runs/20260909-115342+0800-windows-command-path/`。

下一步保持当前 v5.6.4，补做 3 次物理冷启动/UART、USB0 物理热拔插/正反插，以及 UART Root Shell 在场时的 ADB stop/start。已经完成的环境/电源、手动采集、metadata v3、ADB 传输、异常语义、资源清理和 WPF 证据分别保存在两个 2026-09-09 test-runs 目录中。
