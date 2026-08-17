# Mosquito T113 开发镜像 dev-v3

> 已由 `2026-08-17-dev-v4` 取代。实板对比表明120帧自动对焦预热比本版的30帧更清晰；新烧录请使用dev-v4。

构建日期：2026-08-17

镜像：`mosquito-t113-dev-v3.img`（10,218,496 bytes）

SHA-256：`6fc793cb6c5ce41bac73e660e46621c1d826f12981d09f12e2273f649621fbce`

## 本版新增

- 固定高清拍照流程为一条命令：`camera-test`。
- 内置 `lrzsz 0.12.20`，串口终端可使用 `rz` 和 `sz` 进行 ZMODEM 二进制文件传输。
- 修正手动 USB0 ADB ConfigFS 功能链接路径，但仍不允许 ADB 开机自动绑定。
- 修正 Air780EG `+CEREG` 注册状态的解析。

## 一条命令拍照

请让被摄物距离镜头至少 10 cm，然后执行：

```text
camera-test
```

命令会自动完成以下工作：

1. 通过 PE0/CAM_EN 给 USB1 摄像头上电，并寻找支持 3264×2448 MJPEG 的视频节点。
2. 开启自动对焦、自动曝光、自动白平衡和 50 Hz 防闪烁；固定帧率优先，降低手抖拖影。
3. 预热并跳过 30 帧，再保存一张 3264×2448 JPEG；摄像头固件会自动打开自带白色闪光灯。
4. 执行 `sync` 将照片写入 TF 卡，然后自动关闭摄像头电源。

照片固定保存在：

```text
/mnt/UDISK/mosquito-test/camera/
```

文件名依次为：

```text
photo-hd-flash-000001.jpg
photo-hd-flash-000002.jpg
photo-hd-flash-000003.jpg
```

编号持久保存，已有照片不会被下一次拍摄覆盖。查看照片可运行：

```text
ls -lh /mnt/UDISK/mosquito-test/camera/*.jpg
```

摄像头日志保存在 `/tmp/mosquito-camera-test.log` 和 `/overlay/mosquito-test/camera-test.log`。

## 通过 UART 串口传输文件

`rz/sz` 传输的是原始二进制文件，JPEG、程序和压缩包都不需要转成十六进制 TXT。电脑端串口软件必须支持 ZMODEM。

Windows 向开发板发送程序：

```text
mkdir -p /mnt/UDISK/mosquito-test/bin
cd /mnt/UDISK/mosquito-test/bin
rz
```

运行 `rz` 后，在 Windows 串口软件中选择 ZMODEM Send 并选择文件。传输完成后检查并运行：

```text
ls -lh
chmod +x ./程序名
./程序名
```

开发板向 Windows 发送文件：

```text
sz /mnt/UDISK/mosquito-test/camera/photo-hd-flash-000001.jpg
```

## Windows 交叉编译目标

`lrzsz` 只负责传输，不是编译器。Windows 侧程序必须使用本项目的交叉工具链/SDK，并匹配以下目标：

- CPU：ARMv7-A / Cortex-A7
- ABI：32-bit ARM EABI5、hard-float
- C 库：musl
- 工具链前缀：`arm-openwrt-linux-muslgnueabi-`
- 动态加载器：`/lib/ld-musl-armhf.so.1`

不要把 Windows x86/x64 `.exe` 直接传入开发板运行。

## 烧录与安全说明

这是 Allwinner/PhoenixCard 格式的 7 分区 TF 卡镜像。请继续使用已验证的 PhoenixCard“启动卡”流程；烧录会重建目标 TF 卡的系统分区，请先备份卡内文件。

已确认可启动的旧基线保留在 `releases/mosquito-t113/2026-08-14-boot-ok/`。已知有开机重启问题的 `2026-08-16-dev-v1` 禁止烧录。

本版已完成编译、根文件系统内容检查、7 分区打包和 SHA-256 校验。新的 `camera-test` 自动流程和 `rz/sz` 串口实传仍需要在实板烧录后各执行一次，完成最终硬件验收。
