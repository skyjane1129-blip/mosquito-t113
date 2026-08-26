# Mosquito T113-S3 dev-v5.5 USB0 ADB autostart

状态：已知集成问题，已由v5.5.1取代。实板确认本版的`S99mosquito-usb0-autostart`链接不会被当前Tina `rcS`扫描，因此ADB不会自动启动；不要继续烧录本版。

## 镜像

- 文件：`mosquito-t113-dev-v5.5-usb0-adb-autostart.img`
- 大小：11792384 bytes
- SHA-256：`30ae7bd9f8a015c6d1bac967d5a95b914b5b0746b3693f2a0b94d02a3c86ffdd`
- 镜像版本：`dev-v5.5-usb0-adb-autostart`
- 板测包：`mosquito-board-test 2.11-1`
- 基线：`dev-v5.4.2-clean-camera-output`
- 烧录：PhoenixCard“启动卡 / Startup”模式；禁止使用普通`dd`或Etcher

## 本版变化

- 启动序列末段自动准备USB0 ADB，内核uptime不足3秒时先等待；USB0可在上电前连接电脑。
- 厂商USB manager提前绑定UDC时按成功处理，不重复绑定；修正旧脚本与真实状态不一致的提示。
- `usb0-status`显示自动启动服务、FunctionFS、adbd、UDC绑定和主机枚举状态。
- 保留v5.4.2的摄像头输出顺序、自动锁焦、完整照片元数据、4G、GNSS、SIM和电源诊断。

本镜像使用无认证root ADB，只用于受控开发环境，不能作为野外生产镜像。

## Windows开发流程

板子上电后无需UART输入命令。在Windows PowerShell执行：

```powershell
adb wait-for-device
adb devices -l
adb shell
```

常用文件传输：

```powershell
adb push .\程序 /mnt/UDISK/mosquito-test/bin/
adb pull /mnt/UDISK/mosquito-test/camera/照片.jpg .
```

## 实板验收

1. USB0预先连接电脑，连续冷启动10次，每次均能进入ADB且不能循环重启。
2. USB0预先连接电脑，连续执行`adb reboot` 10次，每次均恢复ADB。
3. 不接USB0启动并稳定运行60秒，再热插USB0，应自动进入`configured`。
4. 正反插和拔插均能恢复；`adb push/pull`文件SHA-256一致。
5. 运行`camera-test1 500`并通过ADB下载JPEG和元数据。
6. `usb0-adb-stop`应停止本次启动周期的ADB，`usb0-adb-start all`应能手动恢复。

历史dev-v1曾因约3秒自动绑定USB0而循环重启。如果本候选版复现，立即停止使用并通过`2026-08-14-boot-ok`恢复。

## 已完成检查

- Shell语法和差异空白检查通过。
- Tina完整构建生成`mosquito-board-test_2.11-1_sunxi.ipk`。
- PhoenixCard打包生成7分区整卡镜像，发布副本与构建产物逐字节一致。
- 从成品镜像SquashFS反查确认版本、包号、`S99mosquito-usb0-autostart`启动链接及自动ADB脚本。
- 成品镜像内`camera-test`与v5.4.2覆盖层源码SHA-256一致。
