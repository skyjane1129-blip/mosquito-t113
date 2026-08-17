# Mosquito T113 开发镜像 dev-v4

构建日期：2026-08-17

镜像：`mosquito-t113-dev-v4.img`（10,218,496 bytes）

SHA-256：`fed764198d1438f2e04fc32c492f11840b24431cea2be8673bc5ee02dde1ef1d`

## 本版变化

- 根据实板对比结果，将 `camera-test` 的自动对焦预热由30帧固定为120帧。实拍确认120帧照片比30帧照片更清晰。
- 修复拍照成功后日志错误显示 `(0 bytes)`；改用当前BusyBox支持的命令读取实际JPEG大小。
- 保留 dev-v3 的 `lrzsz 0.12.20`，支持串口 `rz/sz` ZMODEM二进制文件传输。
- 保留顺序照片命名、自动闪光、TF卡同步、拍照结束自动断开摄像头电源等功能。

## 拍照

将被摄物放在镜头10厘米以外；建议测试和识别目标距离为30～50厘米，并让文字或明显边缘处于画面中央。执行：

```text
camera-test
```

命令会自动完成摄像头上电、自动对焦/曝光/白平衡、120帧预热、3264×2448 MJPEG拍摄、闪光灯辅助、TF卡同步及摄像头断电。

照片保存在：

```text
/mnt/UDISK/mosquito-test/camera/photo-hd-flash-000001.jpg
/mnt/UDISK/mosquito-test/camera/photo-hd-flash-000002.jpg
```

编号会持续递增，不覆盖已有照片。

## 串口传输

开发板向Windows发送照片：

```text
sz /mnt/UDISK/mosquito-test/camera/photo-hd-flash-000001.jpg
```

在MobaXterm终端中使用 `Shift+鼠标右键`，选择 `Receive file using Z-modem`。实板已验证文本文件和JPEG均可完成ZMODEM传输。

Windows向开发板发送交叉编译程序：

```text
mkdir -p /mnt/UDISK/mosquito-test/bin
cd /mnt/UDISK/mosquito-test/bin
rz
```

电脑端选择 `Send file using Z-modem`。传输完成后执行：

```text
chmod +x ./程序名
./程序名
```

目标程序必须匹配 ARMv7-A/Cortex-A7、32位EABI5 hard-float和musl libc；项目工具链前缀为 `arm-openwrt-linux-muslgnueabi-`。

## 烧录

这是Allwinner/PhoenixCard格式的7分区TF卡镜像，请使用此前验证过的PhoenixCard“启动卡”流程。烧录会重建目标卡的系统分区，请先备份。

本镜像已完成完整编译、rootfs检查、7分区打包和SHA-256校验。120帧参数来自本项目摄像头的实板对比结果。
