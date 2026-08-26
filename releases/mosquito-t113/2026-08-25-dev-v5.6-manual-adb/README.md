# Mosquito T113-S3 dev-v5.6 manual ADB

状态：已知实板问题，已由 v5.6.1 取代。执行 `adb_on` 后 FunctionFS 只有 `ep0`，因为 init 脚本以前台 `exec` 方式启动厂商 `adbd`，进程会退出。不要继续烧录本版。

## 镜像

- 文件：`mosquito-t113-dev-v5.6-manual-adb.img`
- 大小：`11508736` bytes
- SHA-256：`1d8609c7c0cad1ca4bad0f67962d55bcdad886a97aa1e2711031e23ab663884c`
- 镜像版本：`dev-v5.6-manual-adb`
- 板测包：`mosquito-board-test 2.15-1`
- 基线：`dev-v5.5.3-usb0-controlled-rebind` 的完整功能工作区
- 烧录：PhoenixCard“启动卡 / Startup”模式；禁止使用普通 `dd` 或 Etcher

## 本版变化

- 取消 v5.5.x 的 ADB 开机自动启动、早期 FunctionFS 准备和受控重绑服务。
- 系统每次启动默认关闭 ADB；进入 UART0 Root Shell 后只需执行一条 `adb_on`。
- `adb_on` 最多等待 USB0 UDC 和最低系统 uptime 30 秒，然后按稳定的手动流程准备 FunctionFS、启动无认证 Root `adbd` 并绑定 USB0。
- USB Device 仅包含 ADB；重启后重新恢复关闭。
- 裁掉显示、触摸和音频内核顶层功能，保留 UVC/V4L2 摄像头、4G、GNSS、SIM、电源诊断、USB1 Host、TF/UDISK 和现有板级工具。

## 使用方法

先等待 UART0 出现 Root Shell，再输入：

```sh
adb_on
```

Windows PowerShell 随后运行：

```powershell
adb kill-server
adb start-server
adb devices -l
adb shell
```

正常状态必须显示：

```text
MOSQUITO-T113-DEV    device
```

不得显示 `offline`。板端日志位于 `/tmp/mosquito-usb0-adb.log` 和 `/overlay/mosquito-test/usb0-adb.log`。

## 已完成的软件检查

- `make -j4` 和官方 `pack` 成功，生成 7 分区 PhoenixCard 镜像。
- 从最终镜像偏移 `6789120` 提取 SquashFS，确认版本为 v5.6、板测包为 2.15-1。
- 成品包含 `/usr/bin/adb_on -> usb0-adb-start`，且脚本与源码 SHA-256 一致。
- 成品不包含 `mosquito-usb0-autostart`，不存在 ADB 启动链接或 `rc.preboot`/`load_script.conf` 自动入口。
- 最终内核配置关闭显示、触摸和音频，保留 USB Gadget ADB、媒体、摄像头和 UVC。

## 实板验收

连续执行 10 轮“冷启动、等待串口 Shell、执行 `adb_on`、Windows 运行 `adb devices -l` 和 `adb shell`”。每轮必须直接进入 `device`，不得出现 `offline`、设备消失或要求重插 USB。完成实板验收前，本镜像仍属于开发候选版。
