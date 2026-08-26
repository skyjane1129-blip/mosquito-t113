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
