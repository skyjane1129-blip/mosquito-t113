# Mosquito T113-S3 dev-v5.3 autofocus lock test

状态：已完成源码编译、rootfs 内容检查和 PhoenixCard 打包；用于实板验证“自动对焦位置稳定后锁焦拍摄”，尚未完成实板拍照验收。

## 镜像

- 文件：`mosquito-t113-dev-v5.3-autofocus-lock-test.img`
- 大小：11792384 bytes
- SHA-256：`2c3e6b8c8f43fee5f1829fb4d1c7a83a11348ac5c5298765f657e4a8dfb363fe`
- 烧录：PhoenixCard，模式选择“启动卡 / Startup”
- 禁止使用普通 `dd` 或 Etcher

## 拍照模式

原命令保持不变：

```sh
camera-test
```

它仍使用连续自动对焦并跳过120帧后拍摄，作为对照组。

新增自动锁焦测试：

```sh
camera-test1
```

流程如下：

1. 开启自动曝光、自动白平衡和连续自动对焦。
2. 启动3264×2448 MJPEG预览流，等待3秒。
3. 每秒读取一次 `focus_absolute`，连续5次相同即认为镜头位置暂时稳定。
4. 最多读取30次；若未稳定，使用最后一次有效读数并记录警告；若没有任何有效读数则失败。
5. 关闭自动对焦，回写并锁定选定的焦距。
6. 停止预览流，在锁焦状态下跳过10帧后保存JPEG。

照片文件名包含最终锁定值，例如：

```text
/mnt/UDISK/mosquito-test/camera/photo-hd-aflock-0450-000001.jpg
```

手动定焦模式继续保留：

```sh
camera-test1 450
```

## 实板验收

固定相机、目标、距离和环境光，依次运行：

```sh
camera-test
camera-test1
camera-test1 450
```

自动锁焦完成后检查：

```sh
grep -E 'autofocus sample|stayed at|did not stabilize|focus locked' /overlay/mosquito-test/camera-test.log
ls -lh /mnt/UDISK/mosquito-test/camera/*.jpg
```

需要确认摄像头允许在预览流运行期间由另一个 `v4l2-ctl` 进程读取和设置控制项，并比较三种模式的清晰度。连续5次相同只表示固件报告的镜头位置未变化，不等同于画面清晰度算法判定。

## 已完成检查

- Shell语法与参数错误路径检查通过。
- Tina完整构建成功，软件包版本为 `mosquito-board-test 2.7-1`。
- 从生成的SquashFS确认 `/usr/bin/camera-test` 和 `/usr/bin/camera-test1` 存在且权限为 `0755`。
- PhoenixCard打包和镜像SHA-256复核通过。
