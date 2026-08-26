# Mosquito T113-S3 dev-v5.5.3 USB0 controlled rebind

状态：候选开发镜像。针对v5.5.2在USB0预连接冷启动时Windows枚举成功但ADB停在`offline`的问题，加入受控UDC重绑和完整ADB清理；构建与成品反查通过，仍需实板验收。

## 镜像

- 文件：`mosquito-t113-dev-v5.5.3-usb0-controlled-rebind.img`
- 大小：`11792384` bytes
- SHA-256：`4ea52f1bd9fa883f9c74990e1b8638355916841970f730e97cd7617d5bf44eea`
- 镜像版本：`dev-v5.5.3-usb0-controlled-rebind`
- 板测包：`mosquito-board-test 2.14-1`
- 基线：`dev-v5.4.2-clean-camera-output`
- 烧录：PhoenixCard“启动卡 / Startup”模式；禁止使用普通`dd`或Etcher

## 根因与修复

实板证据表明`adbd`进程正常、FunctionFS的ep0/ep1/ep2均已打开、UDC也达到`configured`，但Windows ADB传输仍为`offline`。日志同时表明vendor USB manager在`prepare`阶段抢先绑定UDC，使正式`bind`路径只返回`already bound`，没有执行预期的稳定等待和重新枚举。另一个已确认缺陷是旧`stop_service`覆盖了procd默认停止路径，`usb0-adb-stop`后`adbd`和FunctionFS仍然存活。

v5.5.3做以下修复：

- 在发布FunctionFS描述符前先固定USB0为`usb_device`。
- adbd端点准备完成后，若vendor manager已经抢先绑定，则强制解绑并保持至少2秒物理断开。
- 等待内核uptime达到10秒后，由`usb0-adb-start`唯一地重新绑定UDC。
- 不再使用这套Tina精简`rc.common`中无效的procd兼容层；显式导出`ADB_AUTH_ENABLE=0`并实现adbd启动/停止，同时在`usb0-adb-stop`中加入TERM、超时KILL、进程消失确认和FunctionFS卸载确认。
- 保持`rc.preboot`后台启动，避免再次被UDISK的ext4检查延迟约90秒。

本镜像使用无认证root ADB，只用于受控开发环境，不能作为生产镜像。

## 已完成检查

- Tina全量构建成功，生成`mosquito-board-test_2.14-1_sunxi.ipk`。
- 官方`pack`成功生成7分区PhoenixCard镜像。
- 所有ADB相关Shell脚本通过`sh -n`检查。
- 从最终镜像偏移`7072768`反查SquashFS，确认版本、包号、rc.preboot、10秒稳定窗口、受控解绑/重绑以及可靠停止逻辑。
- `load_script.conf`为空，ADB自动启动只有`rc.preboot`一个入口。
- 发布镜像SHA-256校验通过。

## 首次实板验收

首次测试时USB0预先连接电脑并保留UART。上电后UART应先出现一次vendor绑定产生的短暂连接，随后脚本主动断开，并在内核约10秒时重新枚举。Windows PowerShell等待15秒后执行：

```powershell
adb kill-server
adb start-server
adb devices -l
adb shell mosquito-version
```

设备必须显示`device`，不能是`offline`。随后验证`adb shell`、`adb push`、`adb pull`、`camera-test1 500`和`usb0-adb-stop`/`usb0-adb-start all`恢复流程。候选版至少完成10次USB0预连接冷启动、10次`adb reboot`、无USB启动后热插及Type-C正反插测试后，才能升级为稳定开发镜像。
