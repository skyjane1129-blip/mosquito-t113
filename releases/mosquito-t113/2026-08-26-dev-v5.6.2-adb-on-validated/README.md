# Mosquito T113-S3 dev-v5.6.2 adb-on validated

状态：Tina完整构建、官方PhoenixCard打包和最终镜像SquashFS反查通过。ADB与4G修复逻辑已经在当前v5.6.1系统的可写overlay上完成实机验证；本目录中的精确成品镜像仍需烧录后进行一次最终冷启动确认。

## 镜像

- 文件：`mosquito-t113-dev-v5.6.2-adb-on-validated.img`
- 大小：`11508736` bytes
- SHA-256：`f0d24710f165122fe7539098c777d15f25c52d0b73874ac6a3ca421b38edaf08`
- 镜像版本：`dev-v5.6.2-adb-on-validated`
- 板测包：`mosquito-board-test 2.17-1`
- 基线：`dev-v5.6.1-adbd-background-hotfix`
- 烧录：PhoenixCard“启动卡 / Startup”模式；禁止使用普通`dd`或Etcher

## 已验证修复

- 开机默认不启动ADB；UART0 Root Shell只需运行一条`adb-on`。
- 命令名由`adb_on`改为`adb-on`，旧命令不再安装。
- `/etc/init.d/adbd`不再用`pidof adbd`判断自身状态，避免脚本把自己误认为已运行的守护进程。
- 以FunctionFS `ep1`作为adbd就绪依据，PID文件改为`/tmp/mosquito-adbd.pid`。
- 当前系统上已验证`ep0/ep1/ep2`、UDC configured、Windows提示音、`adb devices`状态device、Root Shell、push/pull和大文件照片传输。
- 已验证ADB完全停止后由一条`adb-on`恢复、冷启动默认关闭后手动恢复，以及独立供电下USB0热拔插自动恢复且adbd PID不变。
- `4g-start`在PPP模块已经加载时不再受BusyBox `modprobe`返回255影响。
- `4g-start`和`4g-stop`只读取pppd linkname PID文件第一行，正确忽略第二行`ppp0`。
- 已验证LTE注册、PPP、DNS、HTTPS、NTP、`4g-stop`完全断开，以及4G运行期间ADB持续在线。

## 使用

系统启动并出现UART0 Root Shell后运行：

```sh
mosquito-version
adb-on
```

Windows PowerShell确认：

```powershell
adb kill-server
adb start-server
adb devices -l
adb shell mosquito-version
```

设备必须显示为`MOSQUITO-T113-DEV device`。照片通过以下方式下载：

```powershell
adb pull /mnt/UDISK/mosquito-test/camera/照片.jpg .
adb pull /mnt/UDISK/mosquito-test/camera/照片.jpg.txt .
```

4G联网和停止：

```sh
4g-start
4g-stop
```

## 2026-08-26实板身份确认

用户从连接目标板的Windows PowerShell回传了以下原始结果（主机精确时间未记录）：

```text
adb devices -l
MOSQUITO-T113-DEV      device transport_id:3

adb shell mosquito-version
MOSQUITO_IMAGE=dev-v5.6.2-adb-on-validated
MOSQUITO_BUILD_DATE=2026-08-26
MOSQUITO_BOARD_PACKAGE=2.17-1
MOSQUITO_SOURCE_STATE=workspace-snapshot
MOSQUITO_CAMERA_MODES=camera-test0,camera-test,camera-test1,camera-test1-focus
MOSQUITO_PHOTO_METADATA=2

adb shell "command -v gnss-test"
/usr/bin/gnss-test
```

这一证据确认了当前实际运行的镜像身份、Windows ADB `device`状态和GNSS工具存在性。它不等于完整冷启动验收，也不证明GNSS已获得室外定位；后两项仍需独立测试。

同一连板环境随后完成了GNSS前的PPP排他检查：

```text
adb shell 4g-stop
[INFO] Air780EG PPP is not running

adb shell "pidof pppd"
<no output>

adb shell "ip link show ppp0"
ip: can't find device 'ppp0'
```

这确认当时没有pppd进程或`ppp0`接口与GNSS诊断争用Air780EG的`/dev/ttyS1`。尚未运行`gnss-test`，因此仍不构成GNSS定位通过证据。

### GNSS首轮实板结果

随后在同一v5.6.2实板上完整运行`adb shell gnss-test`，关键原始输出为：

```text
[PASS] Air-safe-state
[PASS] Air-3V9
[PASS] Air-UART
[INFO] Air-PWRKEY no AT response yet; applying one 1.2-second boot pulse
[PASS] Air-AT modem answered AT after 1 startup wait cycle(s)
[PASS] GNSS-power-on AT+CGNSPWR=1 OK
[INFO] GNSS-search run=1 fix=0 mode=0 UTC=none satellites(view/GNSS/GLONASS)=0/0/0 C/N0-max=0
[INFO] GNSS-search run=1 fix=0 mode=1 UTC=none satellites(view/GNSS/GLONASS)=0/0/0 C/N0-max=0
[WARN] GNSS-fix no position fix within 5 minutes; check outdoor sky view and GNSS antenna path
[PASS] GNSS-power-off AT+CGNSPWR=0 OK
PASS=6  FAIL=0  WARN=1  MANUAL=0
```

全部已报告的搜索样本都是卫星`0/0/0`和`C/N0-max=0`。因此这一轮实板证明Air780EG主电源、UART、AT通道和GNSS开/关控制链可用，但未收到任何可观测卫星，定位未通过。用户后续确认当轮使用`15×15×4.6 mm`有源陶瓷天线并在室内测试；室内屋顶/墙体遮挡下的零卫星不能用于判定天线或RF2硬件故障。下一轮应保持同一天线和接法，移到室外开阔天空复测。

### 重启后ADB连接拓扑记录

用户随后重启开发板，在UART上执行一条`adb-on`后，板端显示FunctionFS注册、UDC绑定、`high-speed config #1`和`USB_STATE=CONFIGURED`。Windows两种连接的结果为：

```text
经扩展坞：MOSQUITO-T113-DEV offline transport_id:4
改为电脑USB口直连：MOSQUITO-T113-DEV device transport_id:5
```

这一对比支持v5.6.2板端adbd/FunctionFS在该轮可用，而`offline`与扩展坞或其主机USB连接拓扑相关。扩展坞路径尚未做独立重复诊断，不能宣布兼容；当前GNSS验收使用电脑USB口直连。

### GNSS第二轮实板结果

改为电脑USB口直连后，保持同一`15×15×4.6 mm`有源陶瓷天线再运行一轮5分钟`gnss-test`。主电源、UART、AT、GNSS开/关仍全部PASS，搜索关键输出为：

```text
run=1 fix=0 mode=0 UTC=none satellites=0/0/0 C/N0-max=0
run=1 fix=0 mode=1 UTC=none satellites=1/0/0 C/N0-max=31
run=1 fix=0 mode=1 UTC=none satellites=1/0/0 C/N0-max=32
run=1 fix=0 mode=1 UTC=20260826074511 satellites=3/0/0 C/N0-max=32
...
run=1 fix=0 mode=1 UTC=20260826074903 satellites=3/0/0 C/N0-max=31
[WARN] GNSS-fix no position fix within 5 minutes
[PASS] GNSS-power-off AT+CGNSPWR=0 OK
PASS=6  FAIL=0  WARN=1  MANUAL=0
```

该轮已证明射频链能观测至少3颗卫星、最强C/N0为31–32 dB-Hz，并能解码UTC；因此天线供电/连接、RF2射频输入和Air780EG GNSS接收机并非完全无信号。然而仍无卫星参与定位解算，`fix=0 mode=1`，所以GNSS定位未通过。当轮是否已在室外开阔天空尚未随PowerShell输出明确记录。

### 15×15 mm天线室外R1结果

用户随后按室外R1步骤保持电脑USB口直连和同一`15×15×4.6 mm`天线，再完成一轮完整5分钟测试。卫星统计从`0/0/0`依次增加到`1/0/0`、`2/0/0`和`3/0/0`，最大C/N0为25–26 dB-Hz，并从`UTC=none`进展到有效UTC `20260826081431`及后续时间，但仍没有卫星参与定位，始终为`fix=0 mode=1`。最终结果为：

```text
[WARN] GNSS-fix no position fix within 5 minutes
[PASS] GNSS-power-off AT+CGNSPWR=0 OK
PASS=6  FAIL=0  WARN=1  MANUAL=0
```

用户已把设备内日志拉取为Windows文件`gnss-v562-15x15-outdoor-r1-device.txt`，测试后`adb devices -l`仍显示`MOSQUITO-T113-DEV device transport_id:5`。本轮仍只证明接收链可以观测和解码部分卫星信号，不能证明定位通过；C/N0还低于上一轮的31–32 dB-Hz。场地具体遮挡、天线接收面方向以及与金属和线束的距离尚未单独记录。下一步保持相同条件紧接做室外R2；若仍不超过3颗且C/N0偏低，再断电更换18×18 mm天线做同条件对比。

### 15×15 mm天线室外R2结果

在相同测试序列中紧接运行的室外R2，可见卫星从0逐步增加到4颗，最大C/N0为25–27 dB-Hz，并从`UTC=none`进展到有效UTC `20260826082424`至`20260826082740`。不过输出始终是：

```text
run=1 fix=0 mode=1 satellites(view/GNSS/GLONASS)=4/0/0 C/N0-max=25..27
[WARN] GNSS-fix no position fix within 5 minutes
[PASS] GNSS-power-off AT+CGNSPWR=0 OK
PASS=6  FAIL=0  WARN=1  MANUAL=0
```

第一项4只表示接收机看见4颗卫星，后面的`0/0`表示没有卫星被定位解算采用，因此它不满足“4颗可用卫星”的最低定位条件。R2比R1多看见1颗，但信号强度没有实质改善，也没有形成fix。下一步应先保存R2设备日志，然后完全断电并换18×18 mm天线做同地点、同方向对比；若仍只有类似的25–27 dB-Hz和0颗参与定位，再检查有源天线偏置、耗电、接头和RF路径。

### 18×18 mm天线室外R1暂定结果

2026-08-27用户回传的文本拼接了多段历史输出：前两段可由UTC和误粘贴命令识别为既有15×15 R1；最后一段结合用户刚报告18×18测试不理想的操作顺序，暂按18×18室外R1记录，但还需要唯一命名的原始日志确认来源。最后一段关键结果为：

```text
[INFO] Air-PWRKEY no AT response yet; applying one 1.2-second boot pulse
[PASS] Air-AT modem answered AT after 1 startup wait cycle(s)
[PASS] GNSS-power-on AT+CGNSPWR=1 OK
...大部分搜索样本 satellites=0/0/0 C/N0-max=0...
satellites=1/0/0 C/N0-max=28
satellites=1/0/0 C/N0-max=24/22/21
UTC=20260826090312 satellites=1/0/0 C/N0-max=21
[WARN] GNSS-fix no position fix within 5 minutes
[PASS] GNSS-power-off AT+CGNSPWR=0 OK
PASS=6  FAIL=0  WARN=1  MANUAL=0
```

控制链仍正常，但暂定18×18结果比15×15更弱，不能用“陶瓷尺寸更大就一定更强”解释。下一步先确认原始日志归属，断电复查RF2/IPEX和摆放，再做UART-only/ADB停止的干扰对照；仍弱时测GNSS_VCC经L5到RF2的3.3 V有源偏置，并用已知正常天线/开发板交叉验证。

## 成品检查

- `make -j4`成功，板测包实际安装为`2.17-1`。
- Allwinner官方`pack`成功，生成boot-resource、env、env-redund、boot、rootfs、rootfs_data和UDISK七分区PhoenixCard镜像。
- 从最终`.img`偏移`6789120`提取SquashFS并成功展开。
- 提取的SquashFS与构建产物`rootfs.img`逐字节一致。
- 成品包含`/usr/bin/adb-on -> usb0-adb-start`，不包含`/usr/bin/adb_on`。
- 成品不包含adbd或USB0 ADB自动启动链接，也没有`rc.preboot`自动入口。
- 成品中的adbd、4G启停和USB0脚本通过Shell语法检查，且哈希与SDK源码一致。

## 剩余非阻断项

- GNSS真实室外定位仍需在有开阔天空视野的环境下完成。
- BQ25895只读测试曾报告`REG0C watchdog=1`，但自动检查`FAIL=0`，未观察到对ADB、相机或4G的影响。
- 冷启动且尚未联网时系统时间可能为1970年；运行`4g-start`后NTP已验证可恢复正确时间。在校时前拍照会在元数据中标记`system_time_valid=no`。
- 开发ADB为root、无认证，只适合受控调试环境。
