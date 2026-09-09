# Mosquito T113 镜像档案

本目录是所有 Mosquito T113 系统镜像版本的统一索引。每个版本使用独立目录保存身份、SHA-256、变更、验证边界和风险。

## 存储策略

- 本地实际镜像位于 `releases/mosquito-t113/<版本>/`，与版本 README 放在一起。
- `*.img` 由 Git 忽略，不进入源码历史；普通 GitHub 克隆不会下载镜像。
- 版本 README、`.img.sha256`、脱敏测试记录及 `DO_NOT_FLASH.txt` 进入 Git。
- 经用户授权上传时，正式编号且需要保留的 `.img` 作为对应 GitHub Release 的附件。
- 从 GitHub 下载后必须放回同名版本目录，并先校验 SHA-256 再烧录。
- 临时构建和未编号试验产物不进入本目录，不建立发布版本。
- 已发布文件不可覆盖；内容变化必须使用新版本号和新目录。

## 当前版本

当前已烧录版本是 [`2026-09-08-dev-v5.6.4-client-direct`](2026-09-08-dev-v5.6.4-client-direct/README.md)。精确成品的板端直连子集和 Windows WPF 真实按钮闭环已通过；三次物理冷启动/UART、USB 物理热拔插及 UART 在场时的 ADB stop/start 仍待补测。完整当前事实和下一步分别见：

- [`docs/CURRENT_STATE.md`](../../docs/CURRENT_STATE.md)
- [`docs/NEXT_IMAGE.md`](../../docs/NEXT_IMAGE.md)

## 版本索引

| 日期/版本 | 状态 |
| --- | --- |
| [`2026-08-14-boot-ok`](2026-08-14-boot-ok/README.md) | 已验证恢复基线 |
| [`2026-08-14-board-test-v2-usb0-fix`](2026-08-14-board-test-v2-usb0-fix/README.md) | 历史板测版本 |
| [`2026-08-14-usb0-test-v3`](2026-08-14-usb0-test-v3/README.md) | 历史 USB0 单项测试 |
| [`2026-08-16-dev-v1`](2026-08-16-dev-v1/README.md) | **已知循环重启，禁止烧录** |
| [`2026-08-16-dev-v2`](2026-08-16-dev-v2/README.md) | 已被后续版本取代 |
| [`2026-08-17-dev-v3`](2026-08-17-dev-v3/README.md) | 已由 dev-v4 取代 |
| [`2026-08-17-dev-v4`](2026-08-17-dev-v4/README.md) | 摄像头/ZMODEM 历史验证版本 |
| [`2026-08-18-dev-v5-4g-upload`](2026-08-18-dev-v5-4g-upload/README.md) | 4G 上传测试版本 |
| [`2026-08-21-dev-v5.1-candidate`](2026-08-21-dev-v5.1-candidate/README.md) | 成品检查通过，实板验收不完整 |
| [`2026-08-22-dev-v5.2-focus-test`](2026-08-22-dev-v5.2-focus-test/README.md) | 焦距测试候选 |
| [`2026-08-22-dev-v5.3-autofocus-lock-test`](2026-08-22-dev-v5.3-autofocus-lock-test/README.md) | 自动锁焦候选 |
| [`2026-08-22-dev-v5.4-camera-focus-test`](2026-08-22-dev-v5.4-camera-focus-test/README.md) | 焦距/元数据候选 |
| [`2026-08-22-dev-v5.4.1-camera-focus-hotfix`](2026-08-22-dev-v5.4.1-camera-focus-hotfix/README.md) | 锁焦回读热修候选 |
| [`2026-08-22-dev-v5.4.2-clean-camera-output`](2026-08-22-dev-v5.4.2-clean-camera-output/README.md) | 串口输出清理候选 |
| [`2026-08-24-dev-v5.5-usb0-adb-autostart`](2026-08-24-dev-v5.5-usb0-adb-autostart/README.md) | 启动入口失效，不建议烧录 |
| [`2026-08-25-dev-v5.5.1-usb0-autostart-hotfix`](2026-08-25-dev-v5.5.1-usb0-autostart-hotfix/README.md) | 启动延迟约 90 秒，不建议烧录 |
| [`2026-08-25-dev-v5.5.2-usb0-early-autostart`](2026-08-25-dev-v5.5.2-usb0-early-autostart/README.md) | ADB `offline`，不建议烧录 |
| [`2026-08-25-dev-v5.5.3-usb0-controlled-rebind`](2026-08-25-dev-v5.5.3-usb0-controlled-rebind/README.md) | 成品候选，实板循环验收未完成 |
| [`2026-08-25-dev-v5.6-manual-adb`](2026-08-25-dev-v5.6-manual-adb/README.md) | FunctionFS 只有 ep0，不建议烧录 |
| [`2026-08-25-dev-v5.6.1-adbd-background-hotfix`](2026-08-25-dev-v5.6.1-adbd-background-hotfix/README.md) | 后台 adbd 热修候选 |
| [`2026-08-26-dev-v5.6.2-adb-on-validated`](2026-08-26-dev-v5.6.2-adb-on-validated/README.md) | 2.17-1 手动 ADB 历史基线 |
| [`2026-09-04-dev-v5.6.3-usb0-adb-autostart`](2026-09-04-dev-v5.6.3-usb0-adb-autostart/README.md) | 晚期自动 ADB 成品；精确镜像冷启动 3/3 通过 |
| [`2026-09-08-dev-v5.6.4-client-direct`](2026-09-08-dev-v5.6.4-client-direct/README.md) | 当前已烧录；板端直连子集与 Windows WPF 实板闭环通过 |

版本目录是档案，不是推荐列表。烧录任何版本前必须先读该目录 README；`dev-v1` 明确禁止烧录。

## 新版本目录最低内容

只有满足 `AGENTS.md` 的新镜像门槛并获得用户明确同意后，才能新建目录。README 至少记录：

1. 完整版本名、日期、用途和基线；
2. 镜像文件名、字节数和 SHA-256；
3. 根因及已在当前镜像通过的临时修复证据；
4. 新成品的构建、打包和 rootfs 反查结果；
5. 精确成品实板测试命令、通过项、失败项、警告和未测项；
6. 烧录方式、破坏性警告、恢复点及安全边界；
7. 对应 Git commit/tag 和 GitHub Release 附件链接（发布后补充）。
