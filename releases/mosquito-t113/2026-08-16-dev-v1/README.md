# Mosquito T113 开发镜像 dev-v1

> **禁止烧录：这是已知故障镜像。** 实板确认该版会在开机约 3 秒时因 ADB/USB0 UDC 过早自动绑定而循环重启。请改用 `2026-08-16-dev-v2`；需要救援时使用 `2026-08-14-boot-ok` 基线。

构建日期：2026-08-16

镜像：`mosquito-t113-dev-v1.img`（10,218,496 bytes）

SHA-256：`196d5fab0360bdca98dc2defe53e73cf7422aa20fe8628a8bef3f9950d7169a4`

## 烧录

这是 Allwinner/PhoenixCard 格式的 7 分区 TF 卡镜像。请使用此前已经成功使用过的 PhoenixCard“启动卡”流程，不要用 `dd`、Etcher 或普通磁盘镜像写入器。

烧录会重建目标 TF 卡上的系统分区，卡内现有系统和数据会被覆盖。先备份需要保留的内容，并再次确认 PhoenixCard 选中的确实是目标 TF 卡。

已确认可启动的旧基线没有被覆盖，仍位于：

`releases/mosquito-t113/2026-08-14-boot-ok/mosquito-t113-boot-ok.img`

## 本版功能

- 保留 UART0（115200 8N1）作为救援控制台。
- USB0 开机自动建立 ADB gadget，序列号为 `MOSQUITO-T113-DEV`。
- `adbd` 为开发用途的 root、无认证模式。
- 内核已启用 V4L2、videobuf2 和 USB UVC 摄像头支持。
- 系统包含 `adb`、`lsusb`、`v4l2-ctl` 和板级测试命令。
- 保持 boot-resource、env、env-redund、boot、rootfs、rootfs_data、UDISK 共 7 个分区。

## USB0 / ADB

板子启动完成后，把 USB0 接到电脑的数据 USB 口。在电脑命令行运行：

```text
adb kill-server
adb devices
adb shell
```

正常时 `adb devices` 会出现 `MOSQUITO-T113-DEV`，`adb shell` 直接进入 root shell。Windows 第一次使用可能需要把该设备驱动指定为 Android ADB Interface。

如果电脑仍看不到设备，在 UART0 中只运行：

```text
usb0-status
```

它会检查 adbd、FunctionFS、UDC 绑定和物理连接状态。若 UDC 已绑定但状态仍是 `not attached`，重点检查数据线、TYPE-C 的 CC1/CC2、D+、D- 和焊接，不需要再手工创建 gadget。

## 摄像头、4G 和 GNSS

```text
camera-test
air-test
gnss-test
board-test
```

- `camera-test`：拉高 PE0/CAM_EN，等待 USB1 UVC 枚举，先尝试 1280x720 MJPEG，失败再尝试 640x480；照片保存到 `/mnt/UDISK/mosquito-test/camera/`，退出时自动关闭 PE0。
- `air-test`：检查 Air780EG 型号、SIM、信号和 LTE 注册；仅在没有 AT 响应时脉冲一次 PWRKEY，绝不操作 RESET_N。
- `gnss-test`：开启 GNSS，最多等待 5 分钟定位并解析经纬度；正常退出、失败或 Ctrl+C 都会发送关闭 GNSS 的命令。
- `board-test`：执行整板的安全只读/低风险综合检查。

临时日志位于 `/tmp/`，持久日志位于 `/overlay/mosquito-test/`。

## 注意

这个镜像中的无认证 root ADB 只适合当前开发调试，正式部署到野外前必须关闭或改成有认证的维护通道。本次已完成软件编译、根文件系统内容和 7 分区打包检查；USB0 实机枚举、摄像头取帧及 GNSS 室外定位仍需在板子上执行上述命令确认。
