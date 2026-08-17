# Mosquito T113 开发镜像 dev-v2

构建日期：2026-08-16

镜像：`mosquito-t113-dev-v2.img`（10,218,496 bytes）

SHA-256：`d7f11a8add4cc8c67bcaa5e0886516649692c30c07bbe0db78c401b3b9edf3a4`

## 这版解决什么问题

`dev-v1` 会在系统刚启动时自动配置 ADB gadget 并绑定 USB0 UDC，实机在约 3 秒处循环重启。`dev-v2` 完全取消 ADB 开机自启动，系统先正常进入 Tina Linux，再由用户分两步手动准备和绑定 USB0。即使手动绑定在当前硬件上仍触发异常，下次重启也不会再次自动绑定。

本版保留 UART0、TWI0/I2C、Air780EG UART、USB1 Host、UVC/V4L2、7 个 TF 卡分区以及板级测试工具。

## 烧录

这是 Allwinner/PhoenixCard 格式的 7 分区 TF 卡镜像。请使用此前成功使用过的 PhoenixCard“启动卡”流程，不要用 `dd`、Etcher 或普通磁盘镜像写入器。

烧录会重建目标 TF 卡上的系统分区并覆盖卡内原系统和数据。请先备份，并确认 PhoenixCard 选择的是目标 TF 卡。

已确认可启动的旧基线仍保留在：

`releases/mosquito-t113/2026-08-14-boot-ok/mosquito-t113-boot-ok.img`

## 第一次启动和 USB0 测试

第一次启动先不要连接 USB0，只保留 UART0。确认系统稳定进入 Tina Linux 大 Logo 和 `root@mosquito:/#`，等待至少 10 秒，然后运行：

```text
usb0-status
usb0-adb-start prepare
```

`prepare` 只建立 ConfigFS/FunctionFS 并启动 root、无认证的 `adbd`，不会绑定 USB0。确认板子没有重启，再运行：

```text
usb0-status
usb0-adb-start bind
```

确认仍不重启后，再把 USB0 接到电脑。在电脑命令行运行：

```text
adb kill-server
adb devices
adb shell
```

正常时会看到序列号 `MOSQUITO-T113-DEV`。Windows 首次识别可能需要选择 Android ADB Interface 驱动。

测试完成后可运行：

```text
usb0-adb-stop
```

分步测试通过以后，可以用 `usb0-adb-start` 一条命令完成准备、等待和绑定。若 `bind` 后板子重启，先不要反复尝试；拔掉 USB0 后重新上电仍能正常启动，并把 UART0 最后的日志保存下来分析。

## 外设测试命令

```text
camera-test
air-test
gnss-test
board-test
```

- `camera-test`：打开 PE0/CAM_EN，等待 USB1 UVC 摄像头枚举并抓取一张照片，文件保存到 `/mnt/UDISK/mosquito-test/camera/`。
- `air-test`：测试 Air780EG、SIM、LTE 信号和注册状态。
- `gnss-test`：开启 GNSS，等待并解析定位结果，退出时关闭 GNSS。
- `board-test`：执行整板低风险综合检查。

日志保存在 `/tmp/` 和 `/overlay/mosquito-test/`。无认证 root ADB 只适合开发阶段，正式部署到野外前必须关闭或改成安全的维护通道。

## 已完成的软件检查

- UVC/V4L2 和 USB 摄像头驱动已编入内核。
- `load_script.conf` 为空，`/etc/rc.d` 中不存在 ADB 自启动链接。
- 手动 ADB、摄像头、4G、GNSS 和整板测试命令已进入根文件系统。
- 镜像已完成 7 分区打包和 SHA-256 校验。

USB0 手动绑定和摄像头取帧仍必须在实板上完成最终确认。
