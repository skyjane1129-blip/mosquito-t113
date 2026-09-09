# T113-S3 Mosquito TF 启动镜像

本目标用于 [`hardware/pcb/netlist/Netlist_PCB1_2026-07-29.tel`](../hardware/pcb/netlist/Netlist_PCB1_2026-07-29.tel) 对应的自研 PCB。当前目标已启用 TF/SDC0、UART0、USB1 Host、UVC/V4L2 摄像头、Air780EG LTE/GNSS、I2C 和板级测试工具。由于 SDK 自带的 OP-TEE 二进制无法通过该板 T113-S3 的硬件信息检查，本目标不打包 OP-TEE，由 Linux 直接启动第二个 Cortex-A7 核心。

当前实板已烧录 `dev-v5.6.4-client-direct`（板级包 `2.18-1`、metadata v3）。2026-09-09 的板端直连子集与 Windows WPF 真实按钮闭环已通过；自动对焦光学画质、三次物理冷启动、USB 物理热拔插和 UART 在场时的 ADB stop/start 仍待补测。当前身份、证据和后续门槛以 [`../docs/CURRENT_STATE.md`](../docs/CURRENT_STATE.md)、[`../docs/NEXT_IMAGE.md`](../docs/NEXT_IMAGE.md) 和当前版本 [`README.md`](../releases/mosquito-t113/2026-09-08-dev-v5.6.4-client-direct/README.md) 为准。

SDC0 是承载系统和根文件系统的启动 TF 卡。当前 PCB 的卡座检测触点在 Linux 中始终报告未插卡，因此设备树将 SDC0 标记为 `non-removable`，启动时直接枚举介质，不依赖 PF6 卡检测。此配置不支持系统运行期间的 TF 热插拔；若后续确认并修复 CD 硬件，可恢复 `cd-gpios`。

## 编译和打包

若使用 GitHub 仓库中的精简覆盖层，先把 `tina-overlay/` 的内容按原相对路径复制到完整 Tina T113 SDK 根目录。

在 SDK 根目录执行：

```sh
source build/envsetup.sh
lunch t113_mosquito-tina
make -j"$(nproc)"
pack
cd out/t113-mosquito
sha256sum -c tina_t113-mosquito_uart0.img.sha256
```

正常情况下，整卡固件位于：

```text
out/t113-mosquito/tina_t113-mosquito_uart0.img
out/t113-mosquito/tina_t113-mosquito_uart0.img.sha256
```

## PhoenixCard 制作启动卡

1. 在 Windows 中以管理员权限启动 PhoenixCard。
2. 选择上述 `.img` 文件和正确的 TF 卡盘符。
3. 模式选择“启动卡 / Startup”，不要选择“量产卡 / Product”。
4. 写卡完成并通过工具校验后，安全弹出 TF 卡。

这是 Allwinner/PhoenixCard 固件容器，不是可直接挂载的裸磁盘镜像；请勿用
Etcher 或普通 `dd` 代替 PhoenixCard。

## 串口与验收

U13 是 3.3V TTL UART0，115200 8N1、无流控：

- U13.2（T113 TX）接 USB-TTL RX。
- U13.1（T113 RX）接 USB-TTL TX。
- U13.3 接 USB-TTL GND。

内核的 `DEBUG_LL`/`earlyprintk` 也固定到 UART0 `0x02500000`，因此从内核最早期启动阶段开始都应在 U13 输出。

正常启动应依次看到 Boot0、U-Boot、Linux 和 BusyBox shell。进入系统后检查：

```sh
grep -E '^processor' /proc/cpuinfo
nproc
cat /proc/meminfo | head
cat /proc/cmdline
ls -l /dev/mmcblk0*
mount
echo persistent >/root/overlay-test
sync
```

v5.6.4 没有纳入已知软件重启故障的修复，不要在没有 UART 和物理恢复条件时直接执行 `reboot`。保存 UART 后使用物理 Reset 或完全断电再上电，启动完成后执行 `cat /root/overlay-test`；输出 `persistent` 表示 `rootfs_data` 的 ext4 overlay 工作正常。
`/proc/cpuinfo` 应列出 `processor 0` 和 `processor 1`，且 `nproc` 应输出 `2`。

## 外设与开发命令

```sh
camera-test0
camera-test
camera-test1
camera-test1 500
mosquito-environment --machine
mosquito-power --machine
mosquito-capture --id 00000000-0000-4000-8000-000000000001 --focus 500
air-test
gnss-test
sim-test
power-test
4g-start
photo-upload https://webhook.site/your-uuid
4g-stop
board-test
which rz
which sz
```

`camera-test0` 只检测摄像头固件声明的手动焦距范围和步长，不拍照。`camera-test` 使用3264×2448 MJPEG、自动闪光和120帧连续自动对焦预热，作为基准模式。`camera-test1` 无参数时用最近5个焦距读数的范围判稳，以纯 Shell 计算中位数，关闭自动对焦、写入并回读确认焦距，再跳过120帧拍摄；带整数参数时同样执行范围、步长和回读验证。任何空值、越界值或回读不一致都会在拍照前失败退出。照片先写入临时文件，校验 JPEG 起止标记后再原子改名到 `/mnt/UDISK/mosquito-test/camera/`，并生成同名 `.jpg.txt`；该文本包含摘要字段以及完整V4L2活动格式、控制表和设备信息。`sim-test` 只读检查 Air780EG 的 SIM 检测开关、当前接口、主卡槽在位状态、CPIN 和 ICCID；日志中的 ICCID 默认遮挡。`power-test` 触发 BQ25895 ADC 后报告电池电压、系统电压、VBUS、充电电流和故障状态。BQ25895 是充电管理芯片而不是电量计，因此该命令不能可靠给出剩余电量百分比。

摄像头命令完成数据同步后先关闭摄像头并等待USB断开日志输出完毕，随后单独输出 `PHOTO=`、`METADATA=` 和 `LOG=` 行，避免内核串口日志插入文件路径。

`gnss-test` 打开 Air780EG GNSS 并等待定位，默认只报告定位耗时、定位模式、HDOP、卫星数和最大 C/N0，不打印精确经纬度；现场调试确需坐标时可运行 `MOSQUITO_GNSS_SHOW_COORDS=1 gnss-test`。GNSS、SIM/LTE 诊断和 PPP 共用 `/dev/ttyS1`，请先执行 `4g-stop`，确认 PPP 已停止后再运行这些诊断命令。

`4g-start` 默认用中国联通 `3gnet` APN，自动加载 PPP 模块、创建 `/dev/ppp`、等待注册、建立 `ppp0`、安装 DNS 并尝试 NTP 校时；其他 APN 可作为第一个参数传入。该命令可幂等重复执行，并用锁避免诊断程序与 pppd 同时占用 UART。`photo-upload URL [JPEG]` 强制 HTTP/1.1，HTTPS 默认验证系统 CA、系统时间和内核随机池，再以原始 `image/jpeg` 请求体上传，并携带文件名和 SHA-256 请求头。`MOSQUITO_CURL_INSECURE=1` 只允许用于非敏感临时联调，正式验收不得使用。`rz/sz` 用于在UART串口上进行ZMODEM原始二进制传输。

`air-test`、`sim-test` 和 `pppd` 会独占同一个 Air780EG UART，PPP 在线时不要同时运行这些诊断命令。拨号及上传日志分别位于 `/tmp/mosquito-4g-ppp.log` 和 `/overlay/mosquito-test/photo-upload.log`。

当前 v5.6.4 沿用 v5.6.3 的晚期自动 ADB 路径：系统 userspace 就绪后调用既有底层启动流程，Windows 端出现状态为 `device` 的 `MOSQUITO-T113-DEV` 后即可使用 `adb shell/push/pull`。`adb-on` 保留为 UART 救援入口，`usb0-adb-start` 和 `usb0-adb-stop` 只作底层诊断。当前成品的 shell、push 和 pull 已通过；物理 USB 热拔插以及 stop/start 因需确保 UART 恢复通道在场，仍待补测。v5.6.2 阶段形成的 FunctionFS ep1 运行状态判断、`/tmp` PID 文件、PPP 重复加载和两行 PID 文件修复继续保留。

v5.6.4 原生提供 `/usr/bin/mosquito-capture`、`/usr/bin/mosquito-environment` 和 `/usr/bin/mosquito-power`。手动焦距 500 的三次实板采集均完成，生成 metadata v3，并通过 UUID、JPEG 大小/SHA-256/3264×2448 尺寸、同次传感器及 ADB 拉取校验。自动模式的命令和数据协议保留，光学画质等相机固定后再验。

运行 `mosquito-version` 可读取 `/etc/mosquito-version`，确认当前镜像和板级软件包版本。

## 当前约束

- CPU 启动频率固定为 720 MHz，核心电源按 PCB 固定 0.95 V 建模。
- 启动包不含 OP-TEE，不提供 TEE 可信应用、安全存储或基于 OP-TEE 的安全功能。
- USB 摄像头、Air780EG、DHT30 环境读取和 BQ25895 只读电源诊断已提供业务命令；DHT30 已随 metadata v3 完成直连实板验证。EA3056 和真正的电池荷电百分比仍需完成最终业务程序集成；精确 SOC 需要额外电量计或经过标定的整机估算模型。
- 显示、触摸和音频内核组件已裁掉；无线功能不在当前Mosquito镜像范围内。
- `rootfs` 固定为 TF 第 5 分区，即 `/dev/mmcblk0p5`。
- `boot` 预留 8 MiB，`rootfs_data` 预留 64 MiB；`UDISK` 使用 TF 卡剩余空间。
