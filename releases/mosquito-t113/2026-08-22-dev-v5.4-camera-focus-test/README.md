# Mosquito T113-S3 dev-v5.4 camera focus test

状态：已完成源码检查、Tina 完整构建、SquashFS 内容反查和 PhoenixCard 打包；尚需在实板上验证摄像头报告的实际焦距范围和三种拍照策略的清晰率。

## 镜像

- 文件：`mosquito-t113-dev-v5.4-camera-focus-test.img`
- 大小：11792384 bytes
- SHA-256：`2e7f850e1e8c5faed7168b2099a8d95dd8c4254d56eaba8a2abe6928b4076ea7`
- 版本：`dev-v5.4-camera-focus-test`，板测包 `2.8-1`
- 烧录：PhoenixCard，模式选择“启动卡 / Startup”
- 禁止使用普通 `dd` 或 Etcher

烧录启动后可直接确认镜像版本：

```sh
mosquito-version
```

## 摄像头命令

先检测摄像头固件实际提供的焦距范围，不拍照：

```sh
camera-test0
cat /overlay/mosquito-test/camera-focus-capabilities.conf
```

输出包括 `FOCUS_MIN`、`FOCUS_MAX`、`FOCUS_STEP`、`FOCUS_DEFAULT` 和 `FOCUS_CURRENT`，日志保存在 `/overlay/mosquito-test/camera-test0.log`。范围以实板返回值为准，不预设为 0–1000。

保留 v5.3 的连续自动对焦对照模式：

```sh
camera-test
```

它开启自动对焦、自动曝光和自动白平衡，跳过前 120 帧后拍摄一张 3264×2448 MJPEG。

新的自动稳定锁焦模式：

```sh
camera-test1
```

它在预览流中每秒读取一次 `focus_absolute`，检查最近 5 个有效值。若 `最大值 - 最小值 <= 2 × FOCUS_STEP`，取这 5 个值的中位数，关闭自动对焦并锁定后拍摄。最多采样 30 次；超时则取最后最多 5 个有效值的中位数，并在照片名及元数据中标记 `timeout`；没有任何有效焦距值则不拍照并失败退出。

手动定焦模式：

```sh
camera-test1 数值
```

数值必须是摄像头本次上电实际报告范围内的整数，并满足 `(数值 - FOCUS_MIN) % FOCUS_STEP == 0`。越界或不符合步长时拒绝拍摄，并提示合法范围及相邻合法值。

## 照片与记录

照片仍保存在：

```text
/mnt/UDISK/mosquito-test/camera/
```

每张 JPEG 同目录新增一个 `.jpg.txt` 文件，记录模式、稳定/超时状态、焦距范围、自动对焦采样值、最终选择值、曝光、增益、白平衡温度和 JPEG 字节数，便于将清晰度与拍摄参数对应起来。

摄像头命令和 `board-test` 的摄像头供电检查增加了互斥保护，避免同时控制 PE0/CAM_EN。4G、GNSS、电源检测和上传主流程未改变；USB ADB 产品字符串更新为 `Mosquito T113 v5.4 Test`。

## 建议实板测试顺序

固定相机、蚊虫、光线和支架，先在约 9 cm 距离执行：

```sh
mosquito-version
camera-test0
cat /overlay/mosquito-test/camera-focus-capabilities.conf
camera-test
camera-test1
camera-test1 合法焦距值
```

三种拍照模式各重复多次，再在 5 cm 距离重复。比较 JPEG 和对应 `.jpg.txt`，重点统计清晰率、`focus_samples`、`focus_selected`、曝光和增益。`focus_absolute` 稳定只代表镜头位置趋于稳定，并不等同于图像已经清晰，因此最终结论仍以实拍清晰率为准。

## 已完成检查

- 所有 Shell 脚本语法检查、C 代码警告检查和 `git diff --check` 通过。
- Tina 完整构建和 PhoenixCard 打包成功。
- 从生成的 SquashFS 反查，四个摄像头/版本命令存在且权限为 `0755`，`/etc/mosquito-version` 为 `0644`。
- SquashFS 内四个脚本的 SHA-256 与当前源码逐一一致。
- 软件包状态确认为 `mosquito-board-test 2.8-1`。
- 发布镜像 SHA-256 复核通过。
