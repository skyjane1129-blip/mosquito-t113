# Mosquito T113-S3 野外监测终端

Mosquito 是基于全志 T113-S3 的嵌入式 Linux 监测终端项目。目前已经完成定制 Tina Linux 镜像、USB 摄像头高清拍照、白色闪光灯辅助、TF 卡持久化、UART 串口 ZMODEM 文件传输，以及 ARM/musl 应用交叉编译闭环。

## 开发接续入口

从 GitHub 克隆后，无论是开发者还是 Agent，都先按以下顺序阅读：

1. [`AGENTS.md`](AGENTS.md)：实板诊断、新镜像门槛、Git 和发布规则；
2. [`docs/CURRENT_STATE.md`](docs/CURRENT_STATE.md)：当前镜像、证据等级、功能状态和风险；
3. [`docs/NEXT_IMAGE.md`](docs/NEXT_IMAGE.md)：当前唯一允许推进的验收步骤；
4. [`docs/DEVELOPMENT_WORKFLOW.md`](docs/DEVELOPMENT_WORKFLOW.md)：WSL2、Windows 硬件桥接、Git 和自动测试闭环；
5. [`releases/mosquito-t113/README.md`](releases/mosquito-t113/README.md)：全部历史版本索引。

当前 `main` 是集成开发基线，不等同于生产稳定版。普通 Git 克隆不包含被忽略的 `.img` 或第三方 Tina SDK。板端仓库、Tina SDK 和编译输出放在 WSL2 ext4，并只由 WSL Git 写入；Windows 继续负责 ADB、USB/UART、PhoenixCard 和少量产物暂存。Windows ChatGPT 应用中的 WSL2 Agent 与 PowerShell 启动的 WSL2 Codex CLI 是同一套开发架构的两种入口。

## 当前硬件

- 主控：Allwinner T113-S3，双核 Cortex-A7，128 MiB DDR3
- 启动与存储：TF 卡，7 分区 PhoenixCard 启动镜像
- 摄像头：Fifine K436，USB UVC，VID:PID `3142:0046`
- 图像：3264×2448 MJPEG，自动对焦、自动曝光、自动白平衡
- 摄像头电源：PE0/CAM_EN（GPIO 偏移128）
- 蜂窝与定位：Air780EG LTE/GNSS
- 传感器与电源：DHT30、BQ25895、EA3056
- 调试与传输：USB0 root ADB（Windows WinUSB）为主，UART0 115200 8N1 与 `rz/sz` ZMODEM 为救援备用

## 已验证功能

- Tina Linux 5.4.61 从 TF 卡稳定启动
- USB1 Host 与 UVC/V4L2 摄像头枚举
- 一条命令完成摄像头上电、120帧自动对焦预热、闪光拍照、TF 卡同步和断电
- 照片依次保存且不会覆盖已有文件
- UART 双向 ZMODEM 原始二进制传输
- Air780EG SIM/LTE 注册、UART PPP、DNS、HTTPS JPEG 上传全链路验证
- BQ25895 电池/系统/VBUS 电压与充电状态只读诊断
- Air780EG GNSS 定位质量、卫星数与可选坐标诊断
- Windows 主机 + Ubuntu 虚拟机交叉编译 ARMv7 hard-float/musl 程序
- 交叉编译程序通过 UART 传入开发板并成功运行
- USB0 高速枚举、Windows WinUSB、ADB shell 及文件双向传输

## 一键拍照

当前近摄测试建议先从约9～10厘米开始，并固定相机、目标和光线；5厘米处景深更浅，自动对焦清晰率可能明显下降。

```sh
camera-test0
camera-test
camera-test1
camera-test1 500
```

照片保存在：

```text
/mnt/UDISK/mosquito-test/camera/photo-hd-flash-000001.jpg
```

每次拍摄编号自动递增。`camera-test0` 只给摄像头上电并读取固件声明的 `focus_absolute` 最小值、最大值、步长和默认值，不拍照。`camera-test` 保留3264×2448 MJPEG、连续自动对焦和跳过120帧的基准模式。

`camera-test1` 无参数时每秒读取焦距，以最近5个有效值的范围不超过两倍步长作为稳定条件，使用纯 Shell 计算中位数并锁焦；锁焦写入后必须回读一致，否则不拍照。锁焦成功后与其他模式一样跳过120帧。30次内未稳定则以最后5个有效值的中位数拍摄并在文件名标记 `timeout`。传入数值时执行手动定焦，并严格检查 `min/max/step` 和回读值。

所有照片先以 `.part` 写入并校验JPEG，每张照片同时生成 `.jpg.txt`。直接执行 `cat 照片.jpg.txt` 可查看焦距完整采样窗口、目标值、拍照前后回读值、锁焦验证结果、曝光、增益、白平衡、帧数、JPEG哈希、镜像版本、活动格式、完整V4L2控制表和设备信息。

镜像版本可直接查询：

```sh
mosquito-version
```

## 4G、GPS 与电源测试

```sh
4g-start
photo-upload 'https://接收端地址' '/mnt/UDISK/mosquito-test/camera/照片.jpg'
4g-stop
gnss-test
power-test
```

`4g-start` 自动准备 PPP 内核模块和 `/dev/ppp`、安装 PPP DNS，并在联网后尝试 NTP 校时。`photo-upload` 强制 HTTP/1.1，正式路径启用 CA 证书验证，不应再依赖 `MOSQUITO_CURL_INSECURE=1`。

`gnss-test` 与 PPP 共用 Air780EG 的 `/dev/ttyS1`，必须在 `4g-stop` 后运行。默认输出定位质量和卫星统计；需要查看敏感坐标时使用 `MOSQUITO_GNSS_SHOW_COORDS=1 gnss-test`。

`power-test` 读取 BQ25895 的电池、系统、输入电压、充电电流和故障寄存器。BQ25895 不是库仑计，当前硬件只能判断供电/充电状态并读取近似电压，不能把电压直接当作准确剩余电量百分比。

## 串口传输

开发镜像上电后默认关闭 USB0 ADB。系统进入 UART0 Root Shell 后先运行一条 `adb-on`，Windows 安装 Google 官方 Platform-Tools 后即可使用：

```sh
adb-on
```

```powershell
adb devices
adb shell
adb push .\程序 /mnt/UDISK/mosquito-test/bin/
adb pull /mnt/UDISK/mosquito-test/camera/照片.jpg .
```

UART0 和 ZMODEM 保留为系统启动、内核故障及 ADB 不可用时的救援通道。

开发板向 Windows 发送照片：

```sh
sz -e /mnt/UDISK/mosquito-test/camera/photo-hd-flash-000001.jpg
```

Windows 向开发板发送程序：

```sh
mkdir -p /mnt/UDISK/mosquito-test/bin
cd /mnt/UDISK/mosquito-test/bin
rz
chmod +x ./程序名
./程序名
```

MobaXterm 中使用 `Shift + 鼠标右键` 打开菜单，板端运行 `sz` 时选择 `Receive file using Z-modem`，板端运行 `rz` 时选择 `Send file using Z-modem`。

## 应用交叉编译

目标 ABI：ARMv7-A/Cortex-A7、32位 EABI5、hard-float、musl libc。工具链前缀：

```text
arm-openwrt-linux-muslgnueabi-
```

最小示例位于 [`apps/hello-t113`](apps/hello-t113/README.md)。

## Tina SDK 覆盖层

本仓库不重新分发约17GB的第三方 Tina SDK、下载缓存、工具链或构建产物。`tina-overlay/` 只保存 Mosquito 自研覆盖层：

- `target/allwinner/t113-mosquito`：目标板与根文件系统配置
- `device/config/chips/t113/configs/mosquito`：DTS、内核和分区配置
- `package/utils/mosquito-board-test`：板级诊断、摄像头和手动 USB0 ADB 工具
- `lichee/.../sun8iw20p1_mosquito_defconfig`：Mosquito U-Boot 配置

将 `tina-overlay/` 中的文件按原相对路径覆盖到已有 Tina T113 SDK 后，参考 [`MOSQUITO_BUILD.md`](tina-overlay/MOSQUITO_BUILD.md) 构建。

## 镜像

镜像为 Allwinner/PhoenixCard 格式，不是普通 `dd` 磁盘镜像。当前开发候选版本为 `dev-v5.6.2-adb-on-validated`：开机保持ADB关闭，进入UART0 Root Shell后只需运行`adb-on`。该版本包含在v5.6.1系统可写overlay上实机验证通过的adbd FunctionFS启动、USB0热拔插、ADB stop/start、PPP重复加载和两行PID文件修复；精确v5.6.2成品仍需烧录后完成最终冷启动确认。

已知会导致开机重启的 `dev-v1` 禁止烧录。烧录会重建目标 TF 卡分区，操作前必须备份。

## 安全说明

当前开发镜像只在串口手动运行`adb-on`后启用root、无认证USB0 ADB，仅适合受控开发环境，不适合直接部署到野外生产环境。正式生产镜像必须关闭此维护入口或加入认证机制。

本仓库暂未声明开源许可证；未经许可，不代表可重新分发第三方 SDK、芯片厂商工具链或硬件资料。
