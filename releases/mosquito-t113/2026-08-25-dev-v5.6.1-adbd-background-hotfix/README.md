# Mosquito T113-S3 dev-v5.6.1 adbd background hotfix

状态：Tina 完整构建、官方 PhoenixCard 打包和最终 SquashFS 反查通过；ADB 后台启动方式已由 v5.6 实板手动命令验证，完整一命令流程仍需烧录后验收。

## 镜像

- 文件：`mosquito-t113-dev-v5.6.1-adbd-background-hotfix.img`
- 大小：`11508736` bytes
- SHA-256：`a8dd7e806f46e7114227552ad74b03effb4e2027fbba3d07c9c73b526a249eef`
- 镜像版本：`dev-v5.6.1-adbd-background-hotfix`
- 板测包：`mosquito-board-test 2.16-1`
- 基线：`dev-v5.6-manual-adb`
- 烧录：PhoenixCard“启动卡 / Startup”模式；禁止使用普通 `dd` 或 Etcher

## 修复内容

- v5.6 实板执行 `adb_on` 时 FunctionFS 挂载成功但只有 `ep0`，`adbd` 未保持运行。
- 实板直接后台运行 `ADB_AUTH_ENABLE=0 /bin/adbd ... &` 后，PID、`ep1/ep2`、UDC 连接和 ADB 数据通道均正常。
- v5.6.1 将 init 服务改为相同的显式后台启动方式，记录 PID，并将输出写入 `/tmp/mosquito-adbd.log`。
- 启动 `adbd` 前明确设置 USB0 为 `usb_device`，减少设备角色状态差异。
- 保持开机默认关闭 ADB；进入 UART0 Root Shell 后仍只需执行 `adb_on`。
- 保持无认证 Root ADB、ADB-only USB Device、重启后自动关闭策略。

## 使用与验收

系统启动并出现 UART0 Root Shell 后运行：

```sh
mosquito-version
adb_on
```

板端成功条件：

```sh
pidof adbd
ls -la /dev/usb-ffs/adb
```

必须存在 `adbd` PID 和 `ep0`、`ep1`、`ep2`。Windows PowerShell 随后运行：

```powershell
adb kill-server
adb start-server
adb devices -l
adb shell
```

设备状态必须为 `MOSQUITO-T113-DEV device`，不得为 `offline`。随后完成连续 10 轮冷启动验收。

## 已完成检查

- `make -j4` 和官方 `pack` 成功，生成 7 分区 PhoenixCard 镜像。
- 从最终镜像偏移 `6789120` 提取 SquashFS，确认版本和板测包正确。
- 成品 `adbd` init 与热修复源码 SHA-256 一致，包含后台启动、PID 和日志逻辑。
- 成品包含 `/usr/bin/adb_on`，不包含 ADB 自动服务、启动链接或 `rc.preboot` 自动入口。
- 显示、触摸和音频继续关闭；相机、4G、GNSS、SIM、电源及原有板级功能保持 v5.6 配置。
