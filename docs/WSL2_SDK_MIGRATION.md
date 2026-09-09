# Mosquito Tina SDK 从 VMware 迁移到 WSL2

更新时间：2026-09-01（Asia/Shanghai）

## 1. 目的与边界

本文件用于把 VMware Ubuntu 中已有的 Tina T113 SDK 工作环境迁移到 WSL2，同时继续通过 GitHub 同步 Mosquito 自研源码、配置和文档。

开发环境总体架构、Git 分支和 Windows 硬件桥接规则以 [`DEVELOPMENT_WORKFLOW.md`](DEVELOPMENT_WORKFLOW.md) 为准。本文只记录本次 VMware SDK 迁移归档的身份、转移、解压和校验步骤。

两类内容必须分开管理：

```text
GitHub 仓库：Mosquito 自研内容
  /home/<wsl-user>/work/mosquito/mosquito-t113/

本地 SDK：第三方 Tina SDK、工具链、下载缓存和本地构建输出
  /home/<wsl-user>/work/mosquito/tina-t113/
```

`<wsl-user>` 是占位符；下面的 shell 命令使用 `$HOME`，无需手工写入用户名。SDK 必须放在 WSL2 的 Linux 文件系统中，不要放在 `/mnt/c`、`/mnt/d` 或 GitHub 仓库内部。

本次迁移只是建立构建环境，不代表授权生成新镜像。除非已经按 `AGENTS.md` 完成实板诊断门槛并取得用户明确同意，否则 Agent 不得运行 Tina `make`、`pack`，不得修改版本号或建立新发布目录。

截至 2026-09-01 的迁移验收状态：迁移包已经解压到 `/home/janelinux/work/mosquito/tina-t113`，目录占用约 15 GB；`build/envsetup.sh`、`.config`、`.repo` 和 ARM 交叉工具链已检查存在，Mosquito 板测程序已经使用恢复后的工具链交叉编译通过。当时尚未执行完整 Tina `make`、`pack` 和新镜像生成；后续版本构建状态以 `docs/CURRENT_STATE.md` 和对应 release README 为准。

## 2. VMware 迁移包身份

当前迁移包由 VMware Ubuntu 中现有的 `tina-t113/` SDK 根目录生成。个人绝对路径不写入公开仓库。

```text
tina-t113/
```

归档范围包含 SDK 源码、`.repo` 元数据、`dl` 下载缓存、`prebuilt`、工具链、Mosquito 板级配置和根目录 `.config`。以下主机相关历史产物被排除：

```text
tina-t113/out/
tina-t113/logs/
```

归档文件：

```text
tina-t113-sdk-vm-snapshot-2026-08-31.tar.zst
```

归档身份：

| 字段 | 值 |
| --- | --- |
| 文件大小 | `10,207,332,418` bytes |
| SHA-256 | `87b179f5aabae86b51df020a6f32b63a1df67a66530e7a04aad0d150b0007bc0` |
| 生成环境 | VMware Ubuntu，`systemd-detect-virt=vmware` |
| 原始输入大小 | 约 15 GB（排除 `out/`、`logs/` 后） |
| 归档验证 | `sha256sum -c` 通过；`tar --zstd -tf` 完整遍历通过 |
| 范围复核 | 关键构建入口、`.repo`、`dl`、`lichee`、`prebuilt` 均存在；`out/`、`logs/` 条目为 0 |

配套的 `.sha256` 文件必须与归档一起转移：

```text
tina-t113-sdk-vm-snapshot-2026-08-31.tar.zst.sha256
```

该归档包含第三方 Tina SDK 和厂商工具，可能受许可和再分发限制。它只能在用户控制的 VMware、Windows 和 WSL2 环境之间私下迁移，不得加入 Git、推送到 GitHub、上传为 GitHub Release 附件或公开分发。

## 3. 从 VMware 转移到 Windows

归档在 VMware 中生成于：

```text
/tmp/tina-t113-sdk-vm-snapshot-2026-08-31.tar.zst
/tmp/tina-t113-sdk-vm-snapshot-2026-08-31.tar.zst.sha256
```

`/tmp` 是临时位置，文件可能在系统清理或重启后消失。完成本文校验后应尽快把两个文件复制到 Windows 中转目录；不要把它们移动到 Mosquito Git 工作区。

把这两个文件复制到 Windows，例如：

```text
C:\mosquito-transfer\
```

可以使用 VMware Shared Folders、WinSCP/SCP 或 NTFS/exFAT 移动存储。不要使用 FAT32，因为归档可能超过其单文件大小限制。不要在 Windows 中解压归档；Windows 只作为中转和备份位置。

## 4. 在 WSL2 中校验归档

先确认 WSL2 身份和剩余空间：

```sh
uname -a
whoami
df -h /
```

`uname -a` 应包含 Microsoft/WSL 标识。建议为 SDK、首次干净构建和临时文件预留至少 35–50 GB。

在 WSL2 中进入 Windows 中转目录并校验：

```sh
cd /mnt/c/mosquito-transfer
sha256sum -c tina-t113-sdk-vm-snapshot-2026-08-31.tar.zst.sha256
```

只有得到以下结果才能继续：

```text
tina-t113-sdk-vm-snapshot-2026-08-31.tar.zst: OK
```

校验失败时停止，不得解压或尝试编译；重新传输文件后再验证。

## 5. 解压到 WSL2 Linux 文件系统

先确保目标不存在，避免覆盖来源不明的旧 SDK：

```sh
test ! -e "$HOME/work/mosquito/tina-t113"
echo $?
```

退出码必须为 `0`。然后创建父目录并解压：

```sh
mkdir -p "$HOME/work/mosquito"
cd "$HOME/work/mosquito"
tar --zstd -xpf /mnt/c/mosquito-transfer/tina-t113-sdk-vm-snapshot-2026-08-31.tar.zst
```

解压完成后的 SDK 根目录应为：

```text
/home/<wsl-user>/work/mosquito/tina-t113/
```

不要在 `/mnt/c` 中直接解压和编译。Tina 构建会创建大量区分大小写的文件、符号链接和可执行文件，放在 WSL2 的 Linux 文件系统更可靠。

## 6. 解压后的只读完整性检查

进入 SDK：

```sh
cd "$HOME/work/mosquito/tina-t113"
```

检查关键入口：

```sh
test -x build/envsetup.sh &&
test -f rules.mk &&
test -f .config &&
test -d .repo &&
test -d dl &&
test -d lichee &&
test -d package &&
test -d prebuilt &&
test -d target &&
test -d toolchain &&
test -d tools
echo $?
```

退出码必须为 `0`。检查是否出现损坏的符号链接：

```sh
find . -xtype l | sed -n '1,100p'
```

随后只加载构建环境并确认命令存在，不执行构建：

```sh
source build/envsetup.sh
type lunch
type pack
```

`lunch` 和 `pack` 都应显示为 shell function。这里通过只表示 SDK 入口存在，不代表 WSL2 已经完成真实编译验证。

SDK 自带 `README.md` 记录的是 Ubuntu 18 时代的依赖列表，不能直接当作当前 WSL2 发行版已经验证的安装清单。迁移归档不包含 Ubuntu 主机软件包；后续首次授权构建若发现缺失依赖，应记录 WSL2 发行版、失败命令和原始错误，再按最小范围补齐，不能因为环境报错就修改 Mosquito 固件源码。

## 7. 克隆 GitHub 项目

Mosquito GitHub 仓库与 SDK 分开放置。例如：

```sh
mkdir -p "$HOME/work/mosquito"
cd "$HOME/work/mosquito"
git clone https://github.com/skyjane1129-blip/mosquito-t113.git mosquito-t113
cd "$HOME/work/mosquito/mosquito-t113"
git switch main
```

接手时按顺序检查：

```sh
git remote -v
git branch --show-current
git status --short --branch
git rev-parse HEAD
sed -n '1,240p' AGENTS.md
sed -n '1,260p' docs/CURRENT_STATE.md
sed -n '1,380p' docs/NEXT_IMAGE.md
sed -n '1,680p' docs/DEVELOPMENT_WORKFLOW.md
sed -n '1,220p' README.md
sed -n '1,180p' tina-overlay/MOSQUITO_BUILD.md
sed -n '1,320p' releases/mosquito-t113/2026-08-26-dev-v5.6.2-adb-on-validated/README.md
sed -n '1,320p' docs/WSL2_SDK_MIGRATION.md
```

如果仓库已有未提交修改，不得自动覆盖、清理或合并。

## 8. 把 GitHub 覆盖层同步到 SDK

`tina-overlay/` 是 Mosquito 自研内容的权威来源。先做只读差异检查：

```sh
rsync -ainc \
  "$HOME/work/mosquito/mosquito-t113/tina-overlay/" \
  "$HOME/work/mosquito/tina-t113/"
```

逐项审核输出后，才执行覆盖：

```sh
rsync -a \
  "$HOME/work/mosquito/mosquito-t113/tina-overlay/" \
  "$HOME/work/mosquito/tina-t113/"
```

不得添加 `--delete`。`tina-overlay/` 只保存少量自研文件，使用 `--delete` 会把完整 SDK 中其他必需文件当成多余内容删除。

同步后再次运行只读检查：

```sh
rsync -ainc \
  "$HOME/work/mosquito/mosquito-t113/tina-overlay/" \
  "$HOME/work/mosquito/tina-t113/"
```

不应再出现文件内容差异。目录或符号链接时间戳差异需要单独判断，不能使用删除或全量重建处理。

## 9. Agent 在 WSL2 中的使用边界

启动 Agent 时应把工作目录设为 GitHub 仓库：

```text
/home/<wsl-user>/work/mosquito/mosquito-t113
```

同时告诉 Agent，完整 SDK 位于：

```text
/home/<wsl-user>/work/mosquito/tina-t113
```

推荐在新对话开头说明：

```text
当前在 WSL2 中工作。Mosquito Git 仓库位于
/home/<wsl-user>/work/mosquito/mosquito-t113，完整 Tina SDK 位于
/home/<wsl-user>/work/mosquito/tina-t113。先按 AGENTS.md 顺序读取权威文档，
只检查迁移环境，不运行 make、pack，不生成新镜像，也不清理现有修改。
```

Windows 继续负责 ADB、UART、PhoenixCard 和文件管理。WSL2 主要负责 Git、源码检查和 Tina 构建环境。没有实板连接时，Agent 只能记录源码推断或待实板验证，不能写成实板已验证。

## 10. 获得新镜像授权后的构建入口

只有满足 `AGENTS.md` 和 `docs/NEXT_IMAGE.md` 的门槛并取得用户明确授权后，才允许进入 SDK 执行：

```sh
cd "$HOME/work/mosquito/tina-t113"
source build/envsetup.sh
lunch t113_mosquito-tina
make -j"$(nproc)"
pack
```

首次 WSL2 构建必须视为新的主机环境验证。成功 `make` 或 `pack` 只属于构建/成品静态证据，不等于实板通过。最终镜像仍需记录精确文件名、大小和 SHA-256，反查成品内容，并在目标板重新完成规定的验收。

## 11. 禁止事项

- 不把 `tina-t113/`、迁移压缩包、下载缓存、工具链、`out/` 或实际 `.img` 加入 Git。
- 不通过 GitHub 普通提交或 GitHub Release 分发第三方 SDK。
- 不在 Windows 文件系统中直接长期编译 Tina。
- 不用普通复制覆盖一个来源不明的既有 WSL2 SDK。
- 不使用 `rsync --delete` 同步 `tina-overlay/`。
- 不把环境迁移、成功解压或成功编译写成实板验证。
- 未经用户明确授权，不制作下一版本镜像。
