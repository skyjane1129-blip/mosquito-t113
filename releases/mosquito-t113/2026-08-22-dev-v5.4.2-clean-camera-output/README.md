# Mosquito T113-S3 dev-v5.4.2 clean camera output

状态：已完成源码静态检查、Tina 完整构建、SquashFS 内容反查和 PhoenixCard 打包；尚需在实板串口确认 USB 断开日志位于最终路径行之前。

## 镜像

- 文件：`mosquito-t113-dev-v5.4.2-clean-camera-output.img`
- 大小：11792384 bytes
- SHA-256：`f797846345a3e1c85f1c2c703e5321f5e4ca5316ff148c82e54caac05f8b5534`
- 镜像版本：`dev-v5.4.2-clean-camera-output`
- 板测包：`mosquito-board-test 2.10-1`
- 照片元数据格式：版本2
- 烧录：PhoenixCard，模式选择“启动卡 / Startup”
- 禁止使用普通 `dd` 或 Etcher

## 本版修复

- 保留 v5.4.1 的自动锁焦、中位数计算、锁焦回读验证、跳过120帧及完整照片元数据修复。
- 拍照和元数据同步完成后，先等待已有串口输出排空，再关闭摄像头电源。
- 关闭电源后等待 USB 断开内核日志完成，再输出独立、可直接复制的照片和元数据路径。
- `camera-test0` 使用相同的关机顺序，并在最后输出能力文件路径。

`camera-test`、`camera-test1` 和 `camera-test1 数值` 成功结束时，最终应看到完整的独立行：

```text
PHOTO=/mnt/UDISK/mosquito-test/camera/对应照片.jpg
METADATA=/mnt/UDISK/mosquito-test/camera/对应照片.jpg.txt
LOG=/overlay/mosquito-test/camera-test.log
```

内核的 `usb 1-1: USB disconnect` 日志应出现在这些行之前，不再插入文件路径中。前面的 `[PASS] captured ...` 属于过程日志，即使发生视觉插入，也以最后三行为准。

`camera-test0` 最后应输出：

```text
CAPABILITIES=/overlay/mosquito-test/camera-focus-capabilities.conf
LOG=/overlay/mosquito-test/camera-test0.log
```

## 首轮实板验证

```sh
mosquito-version
camera-test1
```

确认版本和包号分别为：

```text
MOSQUITO_IMAGE=dev-v5.4.2-clean-camera-output
MOSQUITO_BOARD_PACKAGE=2.10-1
```

拍摄完成后直接复制最终的 `PHOTO=` 或 `METADATA=` 行。查看完整照片信息时，只复制等号后的 `.jpg.txt` 路径：

```sh
cat /mnt/UDISK/mosquito-test/camera/对应照片.jpg.txt
```

本次修复只调整摄像头断电和最终路径的输出时序，不改变对焦、曝光、白平衡、闪光灯、4G、GNSS、SIM、电源及上传策略。

## 已完成检查

- Shell 语法、C 编译告警和差异空白检查通过。
- Tina 构建生成 `mosquito-board-test_2.10-1_sunxi.ipk`。
- SquashFS 中的版本、包号和最终输出顺序已反查确认。
- SquashFS 内 `camera-test`、`camera-test0` 与覆盖层源码 SHA-256 完全一致。
- PhoenixCard 打包成功，发布镜像与构建产物逐字节一致，SHA-256 校验通过。
