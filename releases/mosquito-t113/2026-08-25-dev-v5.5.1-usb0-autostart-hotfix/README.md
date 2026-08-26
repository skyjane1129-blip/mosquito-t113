# Mosquito T113-S3 dev-v5.5.1 USB0 autostart hotfix

状态：功能已在实板启动，但自动ADB受UDISK检查阻塞，约90秒才开始；已由v5.5.2前移到`rc.preboot`。不建议继续烧录本版。

## 镜像

- 文件：`mosquito-t113-dev-v5.5.1-usb0-autostart-hotfix.img`
- 大小：11792384 bytes
- SHA-256：`e698d20a5d9ccd7c81198570fe61317a80972a11bfd9b2640cebba1960b93d08`
- 镜像版本：`dev-v5.5.1-usb0-autostart-hotfix`
- 板测包：`mosquito-board-test 2.12-1`
- 基线：`dev-v5.4.2-clean-camera-output`
- 烧录：PhoenixCard“启动卡 / Startup”模式；禁止使用普通`dd`或Etcher

## 修复内容

- 不再依赖当前Tina不会扫描的`/etc/rc.d/S99...`链接。
- 将`mosquito-usb0-autostart`加入`/etc/init.d/load_script.conf`，由实际的`rcS -> rc_load_script`路径启动。
- 服务启动时保证内核uptime至少3秒，再准备FunctionFS、启动adbd并绑定USB0 UDC。
- `usb0-status`直接检查`load_script.conf`中的自动启动项。

本镜像使用无认证root ADB，只用于受控开发环境，不能作为野外生产镜像。

## 首次实板验收

USB0可在上电前连接电脑。Windows PowerShell执行：

```powershell
adb wait-for-device
adb devices -l
adb shell mosquito-version
```

板端应自动生成：

```text
/tmp/mosquito-usb0-autostart.log
/overlay/mosquito-test/usb0-autostart.log
```

随后完成USB0预连接冷启动10次、`adb reboot` 10次、无USB启动后热插、正反插、文件传输哈希和`camera-test1 500`回归。

历史dev-v1曾因约3秒自动绑定USB0而循环重启。如果本候选版复现，立即停止使用并通过`2026-08-14-boot-ok`恢复。

## 已完成检查

- Shell语法、差异空白和Tina完整构建通过。
- 生成`mosquito-board-test_2.12-1_sunxi.ipk`和7分区PhoenixCard镜像。
- 发布副本与构建产物逐字节一致，SHA-256校验通过。
- 从成品SquashFS反查确认`load_script.conf`包含`mosquito-usb0-autostart`，且`rcS`实际调用`rc_load_script`。
- 成品内版本、自动服务、USB0脚本和状态工具均为v5.5.1。
