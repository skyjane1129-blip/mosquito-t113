# T113-S3 Mosquito TF 启动镜像

本目标用于 `Netlist_PCB1_2026-07-29.tel` 对应的自研 PCB。当前目标已启用 TF/SDC0、UART0、USB1 Host、UVC/V4L2 摄像头、Air780EG LTE/GNSS、I2C 和板级测试工具。由于 SDK 自带的 OP-TEE 二进制无法通过该板 T113-S3 的硬件信息检查，本目标不打包 OP-TEE，由 Linux 直接启动第二个 Cortex-A7 核心。

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
reboot
```

重启后 `/root/overlay-test` 仍存在，表示 `rootfs_data` 的 ext4 overlay 工作正常。
`/proc/cpuinfo` 应列出 `processor 0` 和 `processor 1`，且 `nproc` 应输出 `2`。

## 外设与开发命令

```sh
camera-test
air-test
gnss-test
board-test
which rz
which sz
```

`camera-test` 使用3264×2448 MJPEG、自动闪光和120帧自动对焦预热，照片保存到 `/mnt/UDISK/mosquito-test/camera/`。`rz/sz` 用于在UART串口上进行ZMODEM原始二进制传输。

USB0 ADB不会开机自动启动。只有需要调试时才手动运行 `usb0-adb-start prepare` 和 `usb0-adb-start bind`，避免实板循环重启。

## 当前约束

- CPU 启动频率固定为 720 MHz，核心电源按 PCB 固定 0.95 V 建模。
- 启动包不含 OP-TEE，不提供 TEE 可信应用、安全存储或基于 OP-TEE 的安全功能。
- USB摄像头和Air780EG已启用并提供测试命令；DHT30、BQ25895和EA3056仍需完成最终业务程序集成。
- 显示、音频和无线功能不在当前Mosquito镜像范围内。
- `rootfs` 固定为 TF 第 5 分区，即 `/dev/mmcblk0p5`。
- `boot` 预留 8 MiB，`rootfs_data` 预留 64 MiB；`UDISK` 使用 TF 卡剩余空间。
