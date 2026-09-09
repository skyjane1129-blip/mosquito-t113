# Mosquito T113-S3 dev-v5.6.3 USB0 ADB 自动启动

状态：Tina 完整构建、官方 `pack` 打包和最终镜像 SquashFS 静态反查通过。临时 overlay 已在现有 v5.6.2 实板上完成 3 次物理断电上电测试，3/3 自动启动 ADB 通过；精确 v5.6.3 镜像已由用户烧录，连续 3 次物理断电上电的自动 ADB、Windows `device`、版本身份和 root 身份均通过。2026-09-08 在该成品上临时部署的直连候选通过板端数据协议验证，但自动锁焦照片 3/3 明显失焦，Windows GUI、4G 和热插拔等完整功能回归仍待完成。

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

## 2026-09-08 直连候选临时实板验证

测试仍运行在本目录对应的精确 v5.6.3 / 2.17-1 成品上；新命令只部署到 `/data/local/tmp/client-demo`，原生 `/usr/bin` 中不存在 `mosquito-capture`、`mosquito-environment`、`mosquito-power`，`mosquito-version` 仍报告 `MOSQUITO_PHOTO_METADATA=2`。因此以下结论是临时候选证据，不表示本镜像已经包含 2.18-1 或 metadata v3。

- 临时 `mosquito-board-test` 为 ARM EABI5/musl 可执行文件，板端 SHA-256 为 `77502f1b29c692888cb41929c2f0ee4e2b9835c17f0d1b4210f52dd6fb0062fb`。
- DHT30 独立读取 10/10 `PASS`；用带内退出 marker 补测的另一组 10 次也全部 `PASS`。
- BQ25895 两组各 10 次均返回完整数据，每组均为 8 次 `PASS` 和 2 次 `WARN`；警告为 `POWER_FAULT_ACTIVE` / `FAULT_REG=0x80`，远端退出码为 0。
- 首轮 `mosquito-capture` 会把内部和最终 `PHOTO=`、`METADATA=` 各输出两次。候选脚本只过滤内部这两个 marker 后重新部署，脚本 SHA-256 为 `877390ec31ef75cb242ac90ddf484d6341fceb9bc8405e68ec3056dae1e06db5`；固定焦距 500 的 3 次和自动锁焦的 3 次协议回归中，四个顶层结果 marker 均各出现一次。
- 这 3+3 次采集都返回 `COMPLETE`，生成 3264×2448 JPEG 和 metadata v3；UUID、照片路径、JPEG 长度/SHA-256、环境段和电源段关联校验均通过。6 张照片及 6 个 metadata 共 12 个文件的板端与 ADB 拉回哈希一致，另有一张照片完成主机→板端→主机的 push/pull 往返哈希闭环。
- 受控替换 `mosquito-power` 后，采集保留照片和 metadata，返回 `PARTIAL` 及远端退出码 10；无效 UUID 返回远端退出码 2。测试后的临时故障注入和 AF 诊断文件均已从板端删除。
- 本机 Windows `adb.exe shell` 的进程退出码不可靠透传板端 shell 状态；探针中的远端 `false` 需要从 `__MOSQUITO_REMOTE_EXIT__=1` 识别，正常、参数错误和 `PARTIAL` 也分别以同一带内 marker 记录 0、2、10。
- 自动锁焦 3/3 均在控制层完成 AF 启用、稳定窗口、AF 关闭、锁焦写入与回读，但人工查看 3 张照片均明显失焦。以 220 和 500 为起点的两轮约 30 秒诊断都收敛并停留在 280；当前不能用数值稳定代替光学清晰度验收。
- 本轮未运行 Windows WPF GUI。原始命令输出、拉回文件、哈希和结构化校验位于 `build/test-runs/20260908-144221+0800-client-direct/`。

## 使用与验收

烧录并首次上电后，Windows 端在约 10–30 秒内执行：

```powershell
adb devices -l
adb shell id
adb shell mosquito-version
```

BusyBox 基线可能没有独立的 `id` 命令；若出现 `/bin/sh: id: not found`，应改查 `/proc/self/status` 的 `Uid: 0 0 0 0`，这不表示 ADB 失败。

本镜像已完成 3 次精确成品冷启动验收，并承载了上述直连候选临时验证；但候选尚未进入成品，自动锁焦实际图像失败，Windows GUI、10 次冷启动、热插拔和 4G 全量回归也未完成。当前不能将其描述为最终整版板测通过，也不满足制作下一镜像的门槛。
