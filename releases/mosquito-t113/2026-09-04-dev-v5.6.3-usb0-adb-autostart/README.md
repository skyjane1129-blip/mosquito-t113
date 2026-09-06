# Mosquito T113-S3 dev-v5.6.3 USB0 ADB 自动启动

状态：Tina 完整构建、官方 `pack` 打包和最终镜像 SquashFS 静态反查通过。临时 overlay 已在现有 v5.6.2 实板上完成 3 次物理断电上电测试，3/3 自动启动 ADB 通过；精确 v5.6.3 镜像已由用户烧录，连续 3 次物理断电上电的自动 ADB、Windows `device`、版本身份和 root 身份均通过，摄像头、4G、热插拔等完整功能回归仍待完成。

## 镜像

- 文件：`mosquito-t113-dev-v5.6.3-usb0-adb-autostart.img`
- 大小：`11508736` bytes
- SHA-256：`7f763d7795c9c0f4a8e3be5e904ec454f39db73cb34eb603af5f45bc587568a7`
- 镜像版本：`dev-v5.6.3-usb0-adb-autostart`
- 板级包：`mosquito-board-test 2.17-1`
- 基线：v5.6.2 的 2.17-1 基线，仅增加晚期 userspace 自动 `adb-on` hook
- 源状态：`auto-adb-rc-final-cdf51dd`
- 构建日期：`2026-09-04`
- 烧录：PhoenixCard“启动卡 / Startup”模式；禁止使用普通 `dd` 或 Etcher

校验：

```sh
sha256sum -c mosquito-t113-dev-v5.6.3-usb0-adb-autostart.img.sha256
```

## 本版本范围

- 系统启动后由 `/etc/init.d/rc.final` 在 late userspace 阶段后台调用现有 `/usr/bin/adb-on` 一次。
- 等待 `/overlay`、`/mnt/UDISK`、`/usr/bin/adb-on` 和 UDC 就绪后再启动，避免与早期 USB 初始化竞争。
- 使用每次启动的 `/tmp` 锁，保留 UART 手动 `adb-on`、`usb0-adb-stop` 和救援禁用标记。
- 日志写入 `/tmp/mosquito-v563-auto-adb.log` 及 `/overlay/mosquito-test/v563-auto-adb/`。
- 未加入板级包 2.18-1、看门狗、`adb reboot`、GNSS、生产/云端或安全策略变更。

## 临时 overlay 实板验证

测试对象是当前已烧录的 v5.6.2 / 2.17-1 系统，仅替换临时 overlay 的 late-userspace hook；3 次物理断电上电均未手动输入 `adb-on`，在约 12.2 秒内得到：

- Windows `adb devices` 显示 `MOSQUITO-T113-DEV device`；
- 单个 `adbd`；
- FunctionFS `ep0/ep1/ep2`；
- `4100000.udc-controller` 为 configured；
- `adb shell` 返回 root 身份。

证据目录：`build/test-runs/20260904-auto-adb-persistent/`。

## 成品静态检查

- `make -j1` 返回 0；
- 官方 `pack` 成功生成 `tina_t113-mosquito_uart0.img`；
- 最终 SquashFS 中 `/etc/init.d/rc.final` 为 0755，且哈希与构建 rootfs 一致；
- `/usr/bin/adb-on -> usb0-adb-start` 存在；
- `/etc/mosquito-version` 明确记录 v5.6.3、构建日期和 2.17-1；
- 成品文件列表中未发现 2.18-1 候选的 `mosquito-environment`、`mosquito-power`、`mosquito-capture*`、看门狗或自动 `adb reboot` 内容；
- 内核配置保持 `# CONFIG_WATCHDOG is not set`。

## 使用与验收

烧录并首次上电后，Windows 端在约 10–30 秒内执行：

```powershell
adb devices -l
adb shell id
adb shell mosquito-version
```

BusyBox 基线可能没有独立的 `id` 命令；若出现 `/bin/sh: id: not found`，应改查 `/proc/self/status` 的 `Uid: 0 0 0 0`，这不表示 ADB 失败。

本镜像已完成 3 次精确成品冷启动验收，但尚未完成 10 次冷启动、热插拔、摄像头和 4G 全量回归；当前不能将其描述为最终整版板测通过。
