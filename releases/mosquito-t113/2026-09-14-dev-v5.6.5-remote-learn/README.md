# dev-v5.6.5-remote-learn

状态：**STATIC_PASS / NOT_FLASHED**。已完成构建前快照与静态检查、SDK 白名单同步、板级包 clean/compile、完整 Tina `make`、官方 `pack`、从最终整卡镜像提取 rootfs 并与 make 产物逐字节比对、SquashFS 内容审计、相对 v5.6.4 的范围审计、凭据扫描、发布目录复制校验。**尚未烧录**，不能声明为成品实板通过。

## 身份

```text
MOSQUITO_IMAGE=dev-v5.6.5-remote-learn
MOSQUITO_BUILD_DATE=2026-09-14
MOSQUITO_BOARD_PACKAGE=2.19-1
MOSQUITO_SOURCE_STATE=remote-learn-f9be772-worktree
MOSQUITO_CAMERA_MODES=camera-test0,camera-test,camera-test1,camera-test1-focus
MOSQUITO_PHOTO_METADATA=3
```

| 项目 | 值 |
| --- | --- |
| 文件 | `mosquito-t113-dev-v5.6.5-remote-learn.img`（同目录 `.sha256`） |
| 大小 | 11,654,144 bytes |
| SHA-256 | `8ece11ae21bc3bd2b0513415e2f5a6fa407ee883f550709fff8ef88302f28fbb` |
| rootfs（SquashFS）大小 | 4,849,664 bytes，位于镜像偏移 6,803,456 |
| rootfs SHA-256 | `81e3879801dfe202b495c081897a82683b9f3f9e42ed2c1c44e74e559c7dcea7`（make 产物与从最终镜像提取的副本一致） |
| 板级包 | `mosquito-board-test_2.19-1_sunxi.ipk` |
| Git 基线 | 分支 `test/lbs-temp-20260910`，HEAD `f9be772f2dbf6265efec06106e8843f33be62296`，带已盘点的未提交工作区（18 个脏文件快照在构建证据目录） |
| 构建证据 | `build/firmware-runs/20260914-v565-remote-learn/` |
| Windows 烧录副本 | `C:\project\mosquito\firmware\mosquito-t113-dev-v5.6.5-remote-learn.img`（与发布件逐字节一致） |

## 相对 v5.6.4 的改动（rootfs 范围审计：19 处，全部在预期内）

- 新增 `/usr/bin/mosquito-remote-agent`（远程代理：心跳、长轮询、4G 上传、LBS 定位、`always-on|duty` 电源档、`Learn` 快速定位学习、冷启动 `/var/run` 修复、忽略 SIGHUP）、`mosquito-lbs`（单基站 LBS + `AT+CCED` 邻区 + `--gnss` AGNSS + `--at`）、`mosquito-location`、`mosquito-upload-4g`、`mosquito-capture-4g`、`mosquito-location-upload`；均 0755。
- 新增 `/etc/mosquito/cloud.conf.example`、`/etc/mosquito/location.conf.example`（模板，无密钥）。
- `/etc/init.d/rc.final` 末尾追加远程代理开机自启动块（读 `/etc/mosquito-cloud.conf` 的 `MOSQUITO_REMOTE_AGENT=1`；`/overlay/mosquito-test/disable-remote-agent` 可禁用）。同一段代码已于 2026-09-14 在 v5.6.4 板子的覆盖层冷启动验证通过（ADB 16 s、代理 22.7 s、PPP 37 s、心跳 141 s）。
- 内核：`CONFIG_RTC_CLASS=y`、`CONFIG_RTC_DRV_SUNXI=y`（及 HCTOSYS/SYSTOHC=rtc0、NVMEM、INTF_SYSFS/PROC/DEV），`rtc-sunxi.o` 已编译进新 zImage。**本候选唯一内核改动，只能烧录后验收。**
- 板级包 2.18-1 → 2.19-1，`DEPENDS +jsonfilter`；`/etc/mosquito-version` 更新；其余差异为 opkg 元数据与 `openwrt_release` 构建号。
- 未纳入：cpufreq、cpuidle、Watchdog/`adb reboot`、GNSS 硬件相关、`/etc/mosquito-cloud.conf` 本体。

镜像内不含设备密钥、公网地址、地图 key 或任何坐标（扫描 0 命中）。

## 构建结果

| 阶段 | 结果 |
| --- | --- |
| 构建前 shell 语法 / C 语法 / 行尾与权限检查 | 全部通过 |
| SDK 白名单同步（13 个文件） | 退出码 0，逐字节一致 |
| `package/mosquito-board-test/clean`、`compile` | 0 / 0 |
| `make -j12 V=s` | 退出码 0（日志中 `busybox-init-base-files` 的两条 `Error 1 (ignored)` 为 SDK 复制不存在的 generic 目录，属基线固有，board 专用 rc.final 已正确安装） |
| 官方 `pack`（沙箱内） | 退出码 0 |
| 最终镜像提取 rootfs 与 make 产物 `cmp` | 一致 |
| 新 zImage 指纹在最终镜像中 | 命中（偏移 2,879,488） |
| 范围审计 / 凭据扫描 | 19 处预期差异 / 0 命中 |
| release 复制与 sha256 校验 | 一致 |

## 烧录

1. 用 PhoenixCard 的 **Startup / 启动卡** 模式写入上表镜像（Allwinner 七分区整卡容器，不能用 Etcher 或 `dd`）。烧录会重建 TF 卡分区：**`/overlay` 覆盖层与 UDISK 会被重置**，请先备份 `/mnt/UDISK/mosquito-test/camera/` 里需要的照片。
2. 烧录后现有板子上临时装在覆盖层的自启动、`/data/local/tmp/remote-demo/` 与 `/etc/mosquito-cloud.conf` 都会消失；工具已内置于本镜像，只需回写配置（下一节）。

## 烧录后验收（逐项记录命令、输出与退出码）

1. 冷启动：`adb devices -l` 为 `MOSQUITO-T113-DEV device`；`adb shell mosquito-version` 与上面身份一致。
2. 回写云配置（私有备份在 `build/remote-agent-candidate/mosquito-cloud.conf`，0600，含 `MOSQUITO_REMOTE_AGENT=1`、`MOSQUITO_LOCATION_MIN_SEC=600`）：
   `adb push build/remote-agent-candidate/mosquito-cloud.conf /etc/mosquito-cloud.conf && adb shell chmod 600 /etc/mosquito-cloud.conf`
3. 再冷启动一次：`/tmp/mosquito-remote-agent-boot.log` 出现 `starting mosquito-remote-agent`，`pidof mosquito-remote-agent` 有值，服务端 3 分钟内显示在线；自动 ADB 不受影响。
4. 远程拍照 1 次（52 秒量级完成）；基站定位含邻区上报（服务端出现 `CELL_LEARNED*`）；下发 `Learn` 1 轮回执 Completed。
5. RTC（建议演示之后、有人在场能断电时做）：`ls -l /dev/rtc0`；`hwclock -r`；`echo +60 > /sys/class/rtc/rtc0/wakealarm && echo freeze > /sys/power/state`，60 s 后自动唤醒，ADB 与 4G 恢复。
6. 直连子集回归：`mosquito-environment`、`mosquito-power`、`mosquito-capture --focus 500` 各 1 次，Windows 客户端直连检测/读取/拍照通过。

通过前本镜像状态保持 `NOT_FLASHED`。
