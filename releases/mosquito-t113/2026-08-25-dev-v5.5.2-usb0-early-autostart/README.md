# Mosquito T113-S3 dev-v5.5.2 USB0 early autostart

状态：已知实板问题，已由v5.5.3取代。USB0预连接冷启动时可达到`configured`，但Windows ADB停在`offline`；不要继续烧录本版。

## 镜像

- 文件：`mosquito-t113-dev-v5.5.2-usb0-early-autostart.img`
- 大小：`11792384` bytes
- SHA-256：`8dbbff8e7fb3bb462189358b743f46a37d86ce6a370603c805f0a7a0d21d6eae`
- 镜像版本：`dev-v5.5.2-usb0-early-autostart`
- 板测包：`mosquito-board-test 2.13-1`
- 基线：`dev-v5.4.2-clean-camera-output`
- 烧录：PhoenixCard“启动卡 / Startup”模式；禁止使用普通`dd`或Etcher

## 启动路径

- BusyBox init执行`rcS boot`。
- `rcS`首先source `/etc/init.d/rc.preboot`。
- `rc.preboot`在后台调用`mosquito-usb0-autostart boot`，不等待UDISK检查。
- 自动服务等待内核uptime达到3秒，然后准备FunctionFS、启动adbd并绑定USB0 UDC。

本镜像使用无认证root ADB，只用于受控开发环境，不能作为野外生产镜像。

## 已完成检查

- Tina完整构建成功，生成`mosquito-board-test_2.13-1_sunxi.ipk`。
- 官方`pack`成功生成7分区PhoenixCard镜像。
- 从最终镜像偏移`7072768`提取SquashFS并反查：版本为v5.5.2、板测包为2.13-1。
- 成品中的`rc.preboot`在UDISK检查前后台调用自动ADB服务，`load_script.conf`为空。
- 自动服务、`usb0-adb-start`和`usb0-status`均存在并通过Shell语法检查。
- 发布镜像SHA-256校验通过。

## 首次实板验收

USB0预先连接电脑并保留UART。在Windows PowerShell先运行：

```powershell
adb wait-for-device
adb devices -l
adb shell mosquito-version
```

UART应在内核约3秒后看到FunctionFS描述符和USB连接日志，不应等到约90秒的UDISK检查结束。随后检查：

```sh
cat /tmp/mosquito-usb0-autostart.log
usb0-status
```

继续完成预连接冷启动10次、`adb reboot` 10次、无USB启动后热插、正反插、文件传输哈希及`camera-test1 500`回归。若出现循环重启，立即停止使用并通过`2026-08-14-boot-ok`恢复。
