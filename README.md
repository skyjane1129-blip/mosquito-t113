# Mosquito T113-S3 野外监测终端

Mosquito 是基于全志 T113-S3 的嵌入式 Linux 监测终端项目。目前已经完成定制 Tina Linux 镜像、USB 摄像头高清拍照、白色闪光灯辅助、TF 卡持久化、UART 串口 ZMODEM 文件传输，以及 ARM/musl 应用交叉编译闭环。

## 当前硬件

- 主控：Allwinner T113-S3，双核 Cortex-A7，128 MiB DDR3
- 启动与存储：TF 卡，7 分区 PhoenixCard 启动镜像
- 摄像头：Fifine K436，USB UVC，VID:PID `3142:0046`
- 图像：3264×2448 MJPEG，自动对焦、自动曝光、自动白平衡
- 摄像头电源：PE0/CAM_EN（GPIO 偏移128）
- 蜂窝与定位：Air780EG LTE/GNSS
- 传感器与电源：DHT30、BQ25895、EA3056
- 调试与传输：UART0 115200 8N1，`rz/sz` ZMODEM

## 已验证功能

- Tina Linux 5.4.61 从 TF 卡稳定启动
- USB1 Host 与 UVC/V4L2 摄像头枚举
- 一条命令完成摄像头上电、120帧自动对焦预热、闪光拍照、TF 卡同步和断电
- 照片依次保存且不会覆盖已有文件
- UART 双向 ZMODEM 原始二进制传输
- Windows 主机 + Ubuntu 虚拟机交叉编译 ARMv7 hard-float/musl 程序
- 交叉编译程序通过 UART 传入开发板并成功运行

## 一键拍照

被摄物应距离镜头至少10厘米；实测30～50厘米、目标具有明显文字或边缘时效果较好。

```sh
camera-test
```

照片保存在：

```text
/mnt/UDISK/mosquito-test/camera/photo-hd-flash-000001.jpg
```

每次拍摄编号自动递增。`camera-test` 默认使用3264×2448 MJPEG并预热120帧，使自动对焦充分稳定。

## 串口传输

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

最小示例位于 [`apps/hello-t113`](apps/hello-t113/README.md)。已有完整 SDK 时可以一条命令编译：

```sh
./scripts/build-hello.sh /路径/tina-t113
```

## 换电脑继续开发

本仓库已经提供环境检查、SDK身份校验、覆盖层安装、应用编译、完整镜像编译和SDK迁移脚本。完整步骤见 [`DEVELOPMENT_SETUP.md`](DEVELOPMENT_SETUP.md)。

推荐先在当前电脑执行：

```sh
./scripts/backup-sdk.sh ./tina-t113 /移动硬盘上的目录
```

然后在新电脑克隆本仓库、恢复 SDK，并运行：

```sh
./scripts/check-host.sh /路径/tina-t113
./scripts/verify-sdk.sh /路径/tina-t113
source scripts/setup-env.sh /路径/tina-t113
```

## Tina SDK 覆盖层

本仓库不重新分发约17GB的第三方 Tina SDK、下载缓存、工具链或构建产物。`tina-overlay/` 只保存 Mosquito 自研覆盖层：

- `target/allwinner/t113-mosquito`：目标板与根文件系统配置
- `device/config/chips/t113/configs/mosquito`：DTS、内核和分区配置
- `package/utils/mosquito-board-test`：板级诊断、摄像头和手动 USB0 ADB 工具
- `lichee/.../sun8iw20p1_mosquito_defconfig`：Mosquito U-Boot 配置

恢复原始 SDK 时，使用 `scripts/install-overlay.sh` 安装 `tina-overlay/` 和 `sdk-patches/`；参考 [`MOSQUITO_BUILD.md`](tina-overlay/MOSQUITO_BUILD.md) 构建。

## 镜像

镜像为 Allwinner/PhoenixCard 格式，不是普通 `dd` 磁盘镜像。最新推荐版本为 `dev-v4`，包含120帧清晰对焦流程和 `lrzsz`。镜像二进制作为 [GitHub Releases](https://github.com/skyjane1129-blip/mosquito-t113/releases) 附件发布；仓库中的 [`releases/`](releases/mosquito-t113/) 保存版本说明和 SHA-256。

已知会导致开机重启的 `dev-v1` 禁止烧录。烧录会重建目标 TF 卡分区，操作前必须备份。

## 安全说明

USB0 ADB 不会开机自动绑定，以避免实板循环重启。开发阶段的 root、无认证 ADB 只能手动启用，不适合直接部署到野外生产环境。

本仓库暂未声明开源许可证；未经许可，不代表可重新分发第三方 SDK、芯片厂商工具链或硬件资料。
