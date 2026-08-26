# Mosquito T113-S3 dev-v5.2 focus test

状态：已完成源码编译、rootfs 内容检查和 PhoenixCard 打包；用于实板寻找固定焦距，尚未完成实板拍照验收。

## 镜像

- 文件：`mosquito-t113-dev-v5.2-focus-test.img`
- 大小：11792384 bytes
- SHA-256：`b714e2b7778ea16e89dbdc4f8fe303bf8cdfd38701b5e2806e13f472be7eb474`
- 烧录：PhoenixCard，模式选择“启动卡 / Startup”
- 禁止使用普通 `dd` 或 Etcher

## 定焦测试

`camera-test` 保持原有自动对焦流程。新增命令：

```sh
camera-test1 FOCUS_ABSOLUTE
```

`camera-test1` 复用 `camera-test` 的摄像头供电、自动曝光、自动白平衡、3264×2448 MJPEG、120 帧预热、自动补光、TF 卡空间检查、JPEG 完整性校验、同步和断电流程，只把对焦改为：

```text
focus_auto=0
focus_absolute=传入值
```

定焦照片文件名包含焦距值，例如：

```text
/mnt/UDISK/mosquito-test/camera/photo-hd-focus-100-000001.jpg
```

## 实板测试步骤

固定相机、目标、拍摄距离和环境光。先查询摄像头实际报告的焦距范围：

```sh
camera-test1 0
grep -i focus /overlay/mosquito-test/camera-test.log
```

若日志确认范围是 `0～255、step=5`，可先粗测：

```sh
camera-test1 0
camera-test1 50
camera-test1 100
camera-test1 150
camera-test1 200
camera-test1 250
```

比较照片后，在最清晰区间内按摄像头报告的步长继续细调。确认最佳值后，再将其固化到正式拍照流程。

## 验收边界

本镜像已验证构建和打包成功，并从生成的 SquashFS 根文件系统确认 `/usr/bin/camera-test1` 存在、权限为 `0755`。焦距范围、每个焦距值的实际执行效果和最终清晰度必须在目标开发板上验证。
