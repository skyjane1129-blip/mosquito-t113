# Mosquito 下一镜像计划

更新时间：2026-08-28（Asia/Shanghai）

## 1. 当前决定

**现在不制作新镜像。** 当前任务是把已经存在的 `dev-v5.6.2-adb-on-validated` 精确成品烧录到目标板，完成最终冷启动与关键路径回归。没有新的根因证据，也没有用户对“下一版镜像”的明确授权，因此：

- 不分配 v5.6.3 或其他新版本号；
- 不修改 `mosquito-version.conf` 或板级包版本；
- 不把新的临时修复写入固件源码；
- 不运行 Tina `make` 或 `pack`；
- 不建立新版本发布目录；
- 不复制、上传或发布新的镜像。

v5.6.2 已经生成，不属于“下一镜像”。其当前 SHA-256 是：

```text
f0d24710f165122fe7539098c777d15f25c52d0b73874ac6a3ca421b38edaf08
```

## 2. 当前验收目标

验收需要回答一个问题：在不依赖 v5.6.1 临时 overlay 的情况下，v5.6.2 成品是否可以从干净冷启动完成以下闭环：

```text
TF 冷启动
  -> UART0 Root Shell
  -> ADB 默认关闭
  -> 一条 adb-on
  -> Windows ADB device/root shell/push/pull
  -> 摄像头拍照与元数据
  -> 4G PPP/DNS/NTP/HTTPS
  -> 4G 与 ADB 共存
  -> 4g-stop
  -> 重启后 ADB 再次默认关闭
```

验收结果必须来自本目录中的精确 `.img`，不能用“v5.6.1 overlay 已通过”代替。

用户已收到有源陶瓷GNSS天线，并要求优先完成v5.6.2的真实室外GNSS定位。聚焦交接、天线边界、首轮命令、隐私要求和失败分类见 `docs/HANDOFF_2026-08-26_V562_GNSS.md`。2026-08-26已由连板Windows PowerShell确认实际运行身份为`dev-v5.6.2-adb-on-validated`/`2.17-1`，`/usr/bin/gnss-test`存在，且PPP已停止。15×15室外R1/R2分别最多3颗和4颗可见、C/N0 25–27，两轮均0颗参与定位。2026-08-27用户回传的拼接文本最后一段结合操作顺序暂按18×18室外R1：5分钟大部分为0颗，后期仅1颗、C/N0 21–28，直到末尾才解出UTC，仍`fix=0 mode=1`和`PASS=6 FAIL=0 WARN=1`；该段还需用唯一命名原始文件确认来源。当前不再重复换尺寸：先断电复查RF2/IPEX和摆放，做UART-only/ADB停止对照；仍弱则测RF2在GNSS开/关时是否约3.3 V/0 V，并用已知正常天线或开发板交叉验证。不制作新镜像。

## 3. 验收前准备

### 3.1 主机与文件

- Windows 安装 Google 官方 Platform-Tools，确保 `adb version` 可用。
- 准备能够保存完整启动日志的 3.3 V TTL UART0，115200 8N1、无流控。
- 准备已验证的数据 USB 线、USB0/TYPE_C1、USB1 摄像头和可用 Air780EG 天线/SIM。
- 准备一个可接收非敏感测试 JPEG 的 HTTPS 地址。
- 备份目标 TF 卡中的照片、日志和其他数据。
- 保留 `2026-08-14-boot-ok` 恢复镜像。

PowerShell 校验候选镜像：

```powershell
Get-FileHash .\releases\mosquito-t113\2026-08-26-dev-v5.6.2-adb-on-validated\mosquito-t113-dev-v5.6.2-adb-on-validated.img -Algorithm SHA256
```

必须得到 `f0d24710...edaf08`。不一致时立即停止，不得烧录。

### 3.2 烧录安全

- PhoenixCard 必须使用“启动卡 / Startup”模式。
- 再次确认选中的是目标 TF 卡；烧录会重建分区。
- 不使用普通 `dd` 或 Etcher。
- 烧录完成后保留 PhoenixCard 的成功/校验结果。

## 4. v5.6.2 最终验收矩阵

### A. 首次冷启动与身份

首次测试先只连接 UART0，不连接 USB0。保存从 Boot0 开始的完整日志。

```sh
mosquito-version
cat /proc/cmdline
grep -E '^processor' /proc/cpuinfo
nproc
mount
cat /proc/uptime
pidof adbd || true
usb0-status
```

通过条件：

- Boot0、U-Boot、Linux 和 BusyBox Root Shell 连续出现，无循环重启或内核崩溃；
- `nproc` 为 `2`；
- 版本精确为 `dev-v5.6.2-adb-on-validated`、包为 `2.17-1`；
- 执行 `adb-on` 前没有存活 `adbd`，ADB 默认关闭；
- TF 分区和可写 overlay 正常挂载。

### B. 一条命令启动 ADB

板端：

```sh
adb-on
echo $?
ls -la /dev/usb-ffs/adb
cat /sys/class/udc/*/state
usb0-status
```

Windows PowerShell：

```powershell
adb kill-server
adb start-server
adb devices -l
adb shell id
adb shell mosquito-version
```

通过条件：

- `adb-on` 退出码为 0；
- FunctionFS 存在 `ep0`、`ep1`、`ep2`；
- UDC 达到 `configured`；
- Windows 设备是 `MOSQUITO-T113-DEV device`，不是 `offline`；
- `adb shell` 是预期 root 环境。

保存以下日志：

```sh
cat /tmp/mosquito-adbd.log
cat /tmp/mosquito-usb0-adb.log
cat /overlay/mosquito-test/usb0-adb.log
```

### C. ADB 数据与恢复

准备一个已知 SHA-256 的大文件或最近一张 JPEG：

```powershell
adb push .\测试文件 /mnt/UDISK/mosquito-test/bin/
adb pull /mnt/UDISK/mosquito-test/bin/测试文件 .\roundtrip-测试文件
Get-FileHash .\测试文件 -Algorithm SHA256
Get-FileHash .\roundtrip-测试文件 -Algorithm SHA256
```

随后验证：

1. 板端 `usb0-adb-stop` 后，Windows 不再列出在线设备。
2. 再执行一条 `adb-on`，设备恢复为 `device`。
3. 保持板子独立供电，USB0 拔出再插入，ADB 自动恢复且 `adbd` PID 不应无故变化。
4. Type-C 正反插至少各一次。
5. 至少完成 10 次“冷启动 → UART Shell → `adb-on` → `adb devices -l` → `adb shell`”。

任何一次 `offline`、循环重启、只能重插才能恢复或 FunctionFS 只有 ep0，都算失败。

### D. 摄像头回归

固定相机、目标、距离和光线：

```sh
camera-test0
cat /overlay/mosquito-test/camera-focus-capabilities.conf
camera-test
camera-test1
camera-test1 合法焦距值
```

通过条件：

- 摄像头能上电、枚举、拍摄 3264×2448 MJPEG、同步并断电；
- JPEG 非空且 SOI/EOI 检查通过；
- 顺序编号不覆盖旧文件；
- 最终 `PHOTO=`、`METADATA=`、`LOG=` 是完整独立行；
- 自动锁焦成功时，元数据的 `focus_selected`、拍摄前后回读一致，`focus_lock_verified=yes`，`focus_auto_before_capture=0`；
- ADB 可把 JPEG 和 `.jpg.txt` 拉回 Windows，哈希一致。

焦距稳定不等于图像清晰。至少人工检查连续多张图片清晰率，并记录物距和光线。

### E. 4G、上传与 ADB 共存

保持 ADB 在线，板端运行：

```sh
4g-start
ip addr show ppp0
ip route
cat /etc/resolv.conf
date
photo-upload 'https://测试接收地址' '/mnt/UDISK/mosquito-test/camera/照片.jpg'
4g-stop
ip link show ppp0 || true
```

并在 Windows 同时重复：

```powershell
adb devices -l
adb shell true
```

通过条件：

- 已加载 PPP 模块不会导致 `4g-start` 失败；
- LTE 注册、`ppp0`、默认路由和 DNS 正常；
- NTP 把明显错误的 1970 年时间校正；
- 正式 HTTPS 验收不使用 `MOSQUITO_CURL_INSECURE=1`，服务器返回成功状态；
- 双行 pppd PID 文件不会阻断 start/stop；
- `4g-stop` 后 PPP 确实结束；
- 整个过程中 ADB 保持 `device`。

### F. 板级与持久化回归

在不与 PPP 争用 `/dev/ttyS1` 的前提下：

```sh
board-test
sim-test
power-test
4g-stop
gnss-test
```

GNSS 需要室外开阔天空，不能在室内失败后直接判硬件故障。默认日志不得暴露精确坐标；确需坐标时只在受控现场临时使用 `MOSQUITO_GNSS_SHOW_COORDS=1`。

检查 BQ25895 的原始寄存器输出并记录 `REG0C watchdog=1` 是否再次出现。只要自动检查 `FAIL=0` 且主路径未受影响，可继续作为已知非阻断风险，但不能删掉记录。

验证 overlay 持久化：

```sh
echo v5.6.2-final-validation > /root/mosquito-overlay-test
sync
reboot
cat /root/mosquito-overlay-test
pidof adbd || true
```

重启后测试文件应存在，ADB 应重新默认关闭。

## 5. 最终通过标准

只有同时满足以下条件，才可把 v5.6.2 从“候选”提升为当前验证开发基线：

- 精确镜像身份和 SHA-256 正确；
- 10 次冷启动无循环重启，10 次手动 `adb-on` 全部一次成功；
- Windows ADB 始终为 `device`，shell/push/pull 和哈希闭环通过；
- stop/start、热拔插和 Type-C 正反插通过；
- 摄像头拍照、焦距回读、JPEG、元数据及 ADB 下载通过；
- PPP、DNS、NTP、HTTPS、`4g-stop` 和 ADB 共存通过；
- TF/overlay 和重启后 ADB 默认关闭通过；
- 所有警告、未测项和外部条件有清晰记录；
- `CURRENT_STATE.md`、本文件和该版本 README 已按结果更新。

GNSS 户外定位若因现场条件无法执行，可作为明确的剩余项，但不能写成已通过。生产安全也不能因开发验收通过而自动解决。

## 6. 若 v5.6.2 失败

失败后仍不得立即构建 v5.6.3。严格执行：

1. 保存完整 UART、ADB、`dmesg`、`logread`、相关 `/tmp` 与 `/overlay` 日志。
2. 记录 `mosquito-version`、镜像 SHA-256、连接方式、失败轮次和复现率。
3. 在当前 v5.6.2 上稳定复现并定位根因。
4. 用 `/tmp`、`/overlay` 或临时命令实施最小修复。
5. 重复原失败路径，并回归 ADB、摄像头、4G 和启动关键路径。
6. 向用户汇总根因、临时修复证据、回归结果和剩余风险。
7. 明确询问用户是否制作下一版本镜像。

获得明确同意前，不设置新版本号、不改固件源码、不构建、不 `pack`、不建发布目录。

## 7. 下一版本候选方向：晚期自动触发 ADB

这一节记录已经商定的后续方向，但不表示现在可以开发或构建该镜像。必须先完成本文第 4、5 节的 v5.6.2 精确成品验收。

### 7.1 目标

保留 v5.6.2 已验证的手动 `adb-on` 作为唯一 USB0/FunctionFS/adbd 底层启动流程。下一阶段只研究在明确的“系统 userspace 已就绪”节点调用一次同一条流程，使 Windows 在正常启动后无需 UART 输入即可看到 ADB。

预期关系必须保持简单：

```text
userspace 明确就绪
  -> 一次性触发现有 adb-on
  -> 成功：记录日志并结束
  -> 失败：记录日志并结束，UART 与手动 adb-on 仍可恢复
```

### 7.2 明确禁止

- 不恢复 v5.5.x 在 `rc.preboot` 中提前准备 FunctionFS、绑定或强制重绑 USB0 的实现。
- 不另写一套与 `adb-on` 并行的 ConfigFS/FunctionFS/adbd 启动逻辑。
- 不在 UDISK、rootfs 或 userspace 尚未明确就绪时启动。
- 自动触发失败时不重启系统、不循环重试、不无限解绑/重绑 UDC。
- 不删除 UART0、手动 `adb-on` 或 `usb0-adb-stop` 救援入口。
- 不把 root、无认证 ADB 误写为生产可接受的安全方案。

### 7.3 在当前 v5.6.2 上的临时验证门槛

只有 v5.6.2 精确成品基线验收通过后，才可在其可写 overlay 上设计晚期一次性触发。临时验证至少覆盖：

1. USB0 预连接冷启动 10 次，每次都进入 `device`，无 `offline` 或循环重启。
2. 系统重启 10 次，自动触发时间和结果可从日志追踪。
3. 不接 USB0 启动，稳定后热插；Type-C 正反插均恢复。
4. 板子独立供电时 USB0 多次拔插，不产生重复守护进程或无限重绑。
5. 自动触发失败后，UART Root Shell 可用，手动 `adb-on` 能恢复。
6. `usb0-adb-stop` 能完全停止；手动 `adb-on` 能再次启动。
7. 摄像头拍照、JPEG/元数据、ADB push/pull 回归通过。
8. 4G、PPP、DNS、NTP、HTTPS 与 ADB 共存，`4g-stop` 正常。
9. 日志保存在 `/tmp` 和必要的 `/overlay/mosquito-test/`，不会写满持久存储。
10. 重启或断电后没有遗留状态把下一次启动卡死。

在当前镜像完成以上临时验证后，必须向用户汇总触发点、实现方式、原始证据、成功率、失败保护、回归结果和安全风险，然后明确询问是否制作包含晚期自动 ADB 的新镜像。

## 8. 用户批准新镜像后的流程

只有门槛满足且用户明确批准后，才按以下顺序执行：

1. 将已经在当前镜像验证通过的最小修复写入 `tina-overlay/`。
2. 更新镜像版本、构建日期和板级包版本，确保三者一致。
3. 静态检查 Shell、C、配置差异及 Git 空白错误。
4. 在完整 Tina SDK 中执行 `make`，成功后执行官方 `pack`。
5. 建立唯一的新版本目录，不覆盖旧文件。
6. 复制精确成品，生成 `.img.sha256`，逐字节或哈希复核。
7. 提取/反查成品 rootfs，确认版本、脚本、权限、启动入口和关闭项。
8. 烧录这一精确成品并重新执行相应实板验收，不能沿用临时 overlay 的通过结论。
9. 更新三个权威文档和版本 README。
10. 经用户同意后提交/推送源码；如需上传镜像，再创建对应 Git tag 与 GitHub Release 附件。

## 9. WSL2 Agent 接手清单

在 WSL2 ext4 克隆 GitHub 项目后，Agent 的第一轮工作必须是：

```sh
echo "$WSL_DISTRO_NAME"
uname -a
pwd
git remote -v
git branch --show-current
git status --short --branch
git rev-parse HEAD
cat AGENTS.md
cat docs/CURRENT_STATE.md
cat docs/NEXT_IMAGE.md
cat docs/DEVELOPMENT_WORKFLOW.md
cat releases/mosquito-t113/2026-08-26-dev-v5.6.2-adb-on-validated/README.md
powershell.exe -NoProfile -Command 'adb version; adb devices -l'
```

如果只克隆了 Git 仓库而未下载 Release 附件，本地没有 `.img` 是正常现象。Agent 可以继续读代码、诊断和规划；需要烧录时才把对应附件下载到现有版本目录并校验 SHA-256。没有设备连接时，不得把源码分析写成实板验证。

当前可执行的下一步只有：**验证现有 v5.6.2，或在无法连接设备时继续完善验证准备；不得自行生成新镜像。**
