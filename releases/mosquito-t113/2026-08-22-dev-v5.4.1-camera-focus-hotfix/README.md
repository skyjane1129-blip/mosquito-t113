# Mosquito T113-S3 dev-v5.4.1 camera focus hotfix

状态：已完成源码静态检查、纯 Shell 焦距算法模拟、Tina 完整构建、SquashFS 内容反查和 PhoenixCard 打包；尚需实板验证锁焦回读及清晰率。

## 镜像

- 文件：`mosquito-t113-dev-v5.4.1-camera-focus-hotfix.img`
- 大小：11792384 bytes
- SHA-256：`7528f8edca3758991984db72cdc61cbdaf4cd37f135d59d71e7eb7f23f7433b4`
- 镜像版本：`dev-v5.4.1-camera-focus-hotfix`
- 板测包：`mosquito-board-test 2.9-1`
- 照片元数据格式：版本2
- 烧录：PhoenixCard，模式选择“启动卡 / Startup”
- 禁止使用普通 `dd` 或 Etcher

## v5.4.1修复内容

- 删除自动锁焦对外部 `sort` 命令的依赖，最近5个焦距值的最小值、最大值、中位数和范围全部由纯 Shell 计算。
- 选定焦距为空、非整数、越界或不符合步长时立即退出，不再拍摄焦距1和文件名0000的无效照片。
- 关闭自动对焦并写入焦距后最多回读3次，必须与目标值完全相同。
- 拍照前再次检查 `focus_absolute=目标值` 且 `focus_auto=0`；拍照后再次读取并写入元数据。
- 无参数 `camera-test1` 锁焦后改为跳过120帧，让曝光和白平衡在新的拍摄流中稳定。
- 手动 `camera-test1 数值` 同样增加焦距和自动对焦状态的回读验证。
- 每张 `.jpg.txt` 扩展为完整的、可直接阅读的元数据报告。

## 拍照命令

```sh
camera-test0
camera-test
camera-test1
camera-test1 300
```

无参数 `camera-test1` 成功时应出现类似日志：

```text
[PASS] autofocus window stabilized; median focus=300 ...
[PASS] focus readback confirmed at 300
[PASS] autofocus disabled and focus locked at 300 with confirmed readback
[INFO] focus is locked and verified at 300; skipping 120 frames before capture ...
```

如果回读不是300，命令会在拍照前输出 `[FAIL]` 并退出。

## 使用cat查看照片信息

先列出最新的元数据文件：

```sh
ls -t /mnt/UDISK/mosquito-test/camera/*.jpg.txt | sed -n '1p'
```

再查看完整内容：

```sh
cat /mnt/UDISK/mosquito-test/camera/对应照片.jpg.txt
```

摘要部分包含：

- 拍摄模式、稳定或超时结果、系统时间有效性和开机秒数；
- 焦距范围、容差、完整采样、窗口最小/最大/中位数和范围；
- 目标焦距、拍照前后实际回读、锁焦验证、自动对焦状态；
- 预览时间、采样间隔、采样上限和跳过帧数；
- 曝光、增益、白平衡、电源频率；
- JPEG尺寸、字节数、SHA-256和完整性检查；
- 镜像版本、活动视频格式、完整V4L2控制表、设备信息和USB设备列表。

相机无法自动知道实际物距，也不能自行判断蚊虫是否清晰，因此报告中物距为 `not-measured`，清晰度仍需查看照片或后续图像算法判断。

## 首轮实板测试

```sh
mosquito-version
camera-test0
camera-test1
```

拍摄后检查 `.jpg.txt`，至少确认：

```text
focus_selected=实际中位数
focus_readback_before_capture=与中位数相同
focus_readback_after_capture=与中位数相同
focus_lock_verified=yes
focus_auto_before_capture=0
focus_auto_after_capture=0
capture_skip_frames=120
```

然后分别在约9 cm和5.5 cm处重复测试。4G、GNSS、SIM、电源和上传主流程未改变。

## 已完成检查

- Shell语法、C警告和差异空白检查通过。
- 无系统命令PATH的算法模拟通过：`300×5` 的中位数为300，混合窗口和非法值路径结果正确。
- Tina构建生成 `mosquito-board-test_2.9-1_sunxi.ipk`。
- SquashFS内 `camera-test` 与源码SHA-256一致，并确认不含 `sort` 调用。
- SquashFS确认锁焦回读、120帧和元数据v2字段已经打包。
- PhoenixCard打包及发布镜像SHA-256复核通过。
