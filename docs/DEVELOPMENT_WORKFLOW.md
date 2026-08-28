# Mosquito T113 开发环境与自动测试闭环

> 状态：已由项目负责人确认采用。本文以 GitHub `main` 提交 `5c36380` 为审计基线编写。
>
> 建议仓库路径：`docs/DEVELOPMENT_WORKFLOW.md`。
>
> 规则优先级：仓库根目录的 [`AGENTS.md`](../AGENTS.md) 是 Agent 的强制规则；本文负责解释环境、流程和协作方式。若两者不一致，在同一次合并中同步修改 `AGENTS.md`、`README.md` 和本文，不能只提交本文。

## 1. 目标

本方案用于消除原来在 Ubuntu 虚拟机与 Windows PowerShell 之间反复复制命令和日志的人工中转：

```text
提出需求
  -> Agent 阅读项目状态和操作边界
  -> 在 WSL2 编写或修改代码
  -> 在 WSL2 编译
  -> 调用 Windows ADB/UART 工具测试开发板
  -> 自动收集返回码和日志
  -> Agent 分析、修复并重新测试
  -> 输出测试证据和风险结论
  -> 项目负责人决定是否制作新镜像
  -> 获得明确授权后，才集成、构建、打包和发布
```

开发人员主要关注三件事：需求、危险操作或发布审批、最终结果。

## 2. 当前仓库的真实边界

`mosquito-t113` 不是完整 Tina SDK，也不是 17 GB SDK 的镜像仓库。它是一个小型、可审查的项目覆盖层仓库：

```text
mosquito-t113/
├── AGENTS.md                    Agent 强制规则和安全边界
├── README.md                    项目入口
├── apps/hello-t113/             应用示例
├── docs/                        当前状态、下一步和交接文档
├── hardware/                    数据手册和 PCB 网表
├── releases/mosquito-t113/      版本说明和镜像校验值
└── tina-overlay/                复制到 Tina SDK 的板级覆盖层
```

完整的 Tina SDK、交叉编译缓存、构建输出和实际 `.img` 镜像不进入 Git。SDK 的覆盖安装和构建步骤以 [`tina-overlay/MOSQUITO_BUILD.md`](../tina-overlay/MOSQUITO_BUILD.md) 为准。

当前 `main` 的含义是“已整合的开发基线”，不是“量产稳定版”。当前候选版本、已验证内容和未解决问题分别以以下文件为准：

1. [`docs/CURRENT_STATE.md`](CURRENT_STATE.md)：当前事实和证据；
2. [`docs/NEXT_IMAGE.md`](NEXT_IMAGE.md)：当前允许进行的下一步；
3. 当前版本对应的 `releases/mosquito-t113/<version>/README.md`；
4. [`README.md`](../README.md) 和构建说明。

## 3. 总体架构

```text
Windows 交互与硬件层
├── ChatGPT Windows 应用或 PowerShell
├── Windows adb.exe 与 USB 驱动
├── UART/COM 口采集工具
├── MobaXterm（人工观察）
└── PhoenixCard 等烧录工具
          │
          │ WSL/Windows 互操作
          ▼
WSL2 Linux 开发层（ext4）
├── Codex Agent 或 Linux 版 Codex CLI
├── mosquito-t113 Git 仓库
├── 与仓库分离的 Tina SDK
├── 交叉编译工具链
└── 构建与自动测试脚本
          │
          │ ADB/UART/经批准的烧录
          ▼
T113 开发板
├── 当前系统镜像
├── 临时测试程序或修复
└── 运行日志与错误码
```

核心原则：

1. 迁移完成后，板端 Git 仓库、Tina SDK 和编译输出都放在 WSL2 ext4。
2. USB、ADB、UART 和烧录工具继续由 Windows 管理。
3. WSL2 中的 Agent 可以直接调用 Windows 的 `adb.exe` 或固定 PowerShell 脚本。
4. 同一板端项目只保留一个可写 Git checkout，不在 Windows 和 WSL 各维护一份。
5. 仓库与完整 Tina SDK 是相邻但独立的目录，不把 SDK 嵌入仓库。

## 4. 两种入口及 I/O 效率

### 4.1 Windows ChatGPT 应用 + WSL2 Agent

```text
Windows ChatGPT 应用
  -> 运行环境选择 WSL2
  -> WSL2 Git、源码和编译器
  -> Windows ADB/UART
  -> 开发板
```

适合长时间的开发—测试—修复闭环、差异审查和结果汇总。必须确认 Agent 的实际运行环境是 WSL2，而不只是终端界面显示 PowerShell 或 WSL。

可在 Agent 环境中检查：

```bash
echo "$WSL_DISTRO_NAME"
uname -a
pwd
```

预期看到 Linux 内核、Ubuntu 发行版和 `/home/...` 项目路径。

### 4.2 PowerShell 启动 WSL2 Codex CLI

PowerShell 只是入口，真正执行 Git、文件读写和编译的是 WSL2 中的 Linux 版 Codex CLI：

```powershell
wsl.exe -d Ubuntu -- bash -lc 'cd ~/work/mosquito/mosquito-t113 && exec codex'
```

也可以先进入 WSL：

```powershell
wsl -d Ubuntu
```

再执行：

```bash
cd ~/work/mosquito/mosquito-t113
codex
```

### 4.3 两种入口的性能结论

| 项目 | Windows 应用 + WSL2 Agent | PowerShell 启动 WSL2 Codex CLI |
|---|---|---|
| Agent 运行位置 | WSL2 | WSL2 |
| 仓库和 SDK | WSL2 ext4 | WSL2 ext4 |
| Git 和编译 I/O | 高 | 高 |
| 大量原始日志 | 建议保存文件后看摘要 | 终端查看更直接 |
| 图形化差异审查 | 更方便 | 以命令行为主 |
| 自动调用 Windows ADB | 支持 | 支持 |

只要 Agent、仓库、SDK和编译输出都位于 WSL2 ext4，两种入口的核心 I/O 性能基本相同。决定性能的是文件系统位置，不是从哪个窗口启动。

不要使用以下结构处理大规模源码和编译输出：

```text
Windows 版 Codex/Git -> \\wsl$ -> ext4
WSL2 编译器          -> /mnt/c -> NTFS
```

## 5. 文件和目录规划

### 5.1 WSL2 ext4：源码和构建区

推荐使用：

```text
/home/<user>/work/mosquito/
├── mosquito-t113/       本 Git 仓库，只保存项目覆盖层和文档
└── tina-t113/           完整供应商 SDK，不进入本仓库
    ├── kernel/
    ├── package/
    ├── target/
    └── out/              SDK 构建输出
```

需要长期保留在 ext4 的内容：

- `mosquito-t113` Git 仓库；
- Tina SDK、内核树和 Bootloader 源码；
- 交叉编译工具链；
- 构建缓存、中间文件和 Linux 脚本；
- 日常测试日志和临时产物。

不要在 `/mnt/c/...` 下展开完整 Tina SDK 或执行完整镜像编译。

### 5.2 Windows NTFS：硬件工具和少量暂存文件

推荐使用：

```text
C:\embedded\
├── android-platform-tools\
│   └── adb.exe
├── device-tools\
│   └── run-device-test.ps1
└── device-staging\
```

Windows 区域存放 Windows 驱动、ADB、UART、MobaXterm、PhoenixCard，以及准备推送到设备的少量最终文件。不要在此维护第二份 `mosquito-t113` 仓库。

版本化发布说明、SHA-256 和测试证据仍放在仓库的 `releases/mosquito-t113/<version>/`。实际 `.img` 文件可以在该版本目录本地保存但会被 Git 忽略，也可以复制到受控的 Windows 发布目录；正式分发建议使用 GitHub Release 附件或团队制品库，并保留校验值。

## 6. Windows ADB 与 UART 桥接

### 6.1 从 WSL2 直接调用 Windows ADB

```bash
/mnt/c/embedded/android-platform-tools/adb.exe devices
/mnt/c/embedded/android-platform-tools/adb.exe wait-for-device
/mnt/c/embedded/android-platform-tools/adb.exe shell
/mnt/c/embedded/android-platform-tools/adb.exe logcat -d
```

简单操作优先直接调用 `adb.exe`。涉及超时、设备序列号、UART、部署和日志归档时，调用固定脚本：

```bash
powershell.exe -NoProfile \
  -File 'C:\embedded\device-tools\run-device-test.ps1'
```

当前 `main` 还没有上述 Windows 自动化脚本，因此这些命令是目标设计，不是已经具备的能力。脚本完成后，Git 权威版本建议放在 `scripts/windows/`，Windows 目录只保存由安装脚本部署的副本。

### 6.2 推送单个 WSL2 构建产物

为避免 Windows ADB 递归读取 Linux 构建树，先复制单个最终文件：

```bash
cp <wsl-build-output>/test_program /mnt/c/embedded/device-staging/
```

然后指定设备序列号推送：

```bash
/mnt/c/embedded/android-platform-tools/adb.exe \
  -s "$DEVICE_SERIAL" push \
  'C:\embedded\device-staging\test_program' \
  /data/local/tmp/test_program
```

### 6.3 UART 规则

1. 人工观察可使用 MobaXterm；自动测试使用可保存日志的命令行采集脚本。
2. MobaXterm 与采集脚本不能同时占用同一个 COM 口。
3. 串口参数、ADB 序列号和设备端口属于本机配置，不写死在公开仓库中。
4. 如果串口允许发送命令，脚本可执行 `adb-on`，然后等待 ADB 上线。
5. 任何自动烧录、擦除分区、Bootloader 或 eFuse 操作都必须由负责人明确授权。

## 7. 开发、测试、反馈和镜像闭环

### 7.1 开始任务前

Agent 必须按仓库规定的顺序读取：

1. `AGENTS.md`；
2. `docs/CURRENT_STATE.md`；
3. `docs/NEXT_IMAGE.md`；
4. `README.md`、`tina-overlay/MOSQUITO_BUILD.md`；
5. 当前版本对应的 release README。

随后确认 Git 状态、目标板序列号、测试通过条件、允许的重启或系统修改范围、超时和最大重试次数。

### 7.2 默认先诊断当前镜像

收到故障或新需求不等于获得了制作新镜像的授权。默认闭环是：

```text
在当前镜像复现
  -> 定位应用、配置、驱动、内核或连接层问题
  -> 尽可能在当前镜像做临时修复或最小测试
  -> 重新测试并执行回归
  -> 保存证据
  -> 汇报结论和制作镜像的必要性
```

只有项目负责人明确批准后，Agent 才能把修复整合进覆盖层、提升版本、执行完整构建和 `pack`、创建发布记录。测试失败也不能自动开始制作下一版本。

### 7.3 板端自动测试

```text
启动 UART 日志采集
  -> 必要时发送 adb-on
  -> adb wait-for-device
  -> 校验设备序列号
  -> adb push/install
  -> 执行测试程序
  -> 保存 stdout、stderr 和退出码
  -> 收集 logcat、dmesg、UART 和设备信息
  -> 停止采集并生成结构化结果
```

建议测试程序额外输出机器可解析结果：

```json
{
  "result": "PASS",
  "exit_code": 0,
  "duration_seconds": 120,
  "errors": []
}
```

自动重试必须设置最大次数和总超时。达到上限后停止，保留失败证据并汇报，不无限循环。

本地测试产物建议放在已被根级 `/build/` 规则忽略的位置：

```text
build/test-runs/<timestamp>/
├── summary.json
├── stdout.log
├── stderr.log
├── logcat.txt
├── dmesg.txt
├── uart.log
├── device-info.txt
└── build-manifest.json
```

### 7.4 证据等级

沿用当前 `AGENTS.md` 的证据概念，报告时不要把推测写成实测：

- A：在目标板和目标镜像上直接验证；
- B：在高度接近的版本或环境中验证；
- C：由源码、配置或日志推导；
- D：尚未验证的假设。

报告必须写清镜像版本、Git commit、设备、测试命令、原始日志位置、结论和证据等级。

### 7.5 获得授权后的镜像构建

在负责人明确授权后，进入完整构建：

```bash
cd ~/work/mosquito/tina-t113
source build/envsetup.sh
lunch t113_mosquito-tina
make -j"$(nproc)"
pack
```

实际命令和覆盖安装方式以 `tina-overlay/MOSQUITO_BUILD.md` 为准。完成后至少记录：

```text
产品和镜像版本
Git commit 与分支
Tina SDK 基线
Linux 内核与工具链版本
板级配置和构建时间
镜像 SHA-256
测试结果和证据等级
```

## 8. 优化后的 Git 协作规则

### 8.1 已确认的开发环境规则

项目负责人已确认采用“WSL2 ext4 中使用 WSL Git”的新方案。板端仓库、完整 Tina SDK 和编译输出位于 WSL2；Windows 负责 ADB、UART、PhoenixCard 和少量产物暂存。

该决定必须在同一个文档/基础设施变更中同步维护：

- `AGENTS.md` 中 Git 的运行位置；
- `README.md` 的开发环境入口；
- 本文和实际安装脚本；
- 必要的 `.gitignore`、`.gitattributes`。

以后改变开发环境时也必须同步更新这些入口，避免出现互相矛盾的 Git 写入规则。

### 8.2 单一权威 checkout

迁移后：

- 板端仓库只在 `/home/<user>/work/mosquito/mosquito-t113` 可写；
- 只使用 WSL Git 操作该仓库；
- Windows 通过 `\\wsl$` 查看文件时不使用 Windows Git 写入；
- 不用复制目录的方式同步源码；
- GitHub 是团队协作入口，但本地未提交内容仍需自行保护。

首次克隆后建议检查仓库级配置：

```bash
git config --local core.autocrlf false
git config --local core.filemode true
git config --local core.ignorecase false
git config --get-regexp '^core\.(autocrlf|filemode|ignorecase)$'
```

### 8.3 分支模型

`main` 表示当前集成开发基线，不保证是量产稳定镜像。每个任务使用短生命周期分支：

```text
feature/<topic>       新功能
fix/<topic>           缺陷修复
test/<topic>          测试程序或测试基础设施
docs/<topic>          纯文档
infra/<topic>         构建、环境和工具脚本
release/<version>     获准后的发布准备
```

建议流程：

```bash
git fetch origin
git switch main
git pull --ff-only
git switch -c docs/wsl2-development-workflow
```

不要直接向 `main` 开发；通过 PR 审查和合并。禁止 force-push `main`，禁止改写共享历史。Agent 不得未经明确授权自动提交、推送、合并、打标签或创建 Release。

仓库已有的 `agent/portable-development-setup` 是从早期 `dev-v4` 基线派生的未合并分支，不应把它作为今后的默认分支命名规范。

### 8.4 提交规则

一个提交只表达一个可以独立审查和回退的逻辑变化。推荐主题：

```text
feat: add 4g telemetry upload
fix: handle adb reconnect timeout
test: add gnss cold-start scenario
docs: document wsl2 device-test workflow
build: record firmware build manifest
```

提交前至少执行：

```bash
git status --short
git diff --check
git diff --stat
git diff
```

在推送或创建 PR 前，必须列出所有新引入文件，说明来源、用途、许可证或敏感性，并由负责人确认。不要提交密码、密钥、真实云端凭据、个人路径或设备唯一标识。

### 8.5 `.gitignore` 优化建议

当前规则已经正确忽略 Tina SDK、根级构建目录、应用二进制、镜像、对象文件和日志。建议在迁移 PR 中补充：

```gitignore
# App-local output
/apps/hello-t113/build/

# SDK migration archives and interrupted downloads
*.tar.zst
*.tar.zst.sha256
*.partial

# Local device/environment configuration
/.env
/.env.local
/local/
```

不要使用过宽的 `*.bin` 或忽略整个 `releases/`，否则可能误隐藏应审查的固件资源、版本说明和校验值。

### 8.6 `.gitattributes` 优化建议

当前仓库已将 C、头文件、Markdown、shell、配置、DTS、FEX、Makefile 强制为 LF，并正确将 PDF、DOCX、IMG、TEL 标记为二进制。建议补充未来会使用的文本类型：

```gitattributes
*.patch text eol=lf
*.env text eol=lf
*.ps1 text eol=lf
```

即使 PowerShell 脚本在 Windows 上执行，也建议以 LF 保存在这个以 WSL2 为主的仓库中；现代 PowerShell 可以正常处理 LF。

仓库当前有若干非脚本文件带可执行位。迁移到 ext4 后应逐项审核，而不是一次性清除：

```bash
git ls-files --stage | awk '$1 == "100755" {print $4}'
```

只有真正需要直接执行的脚本保留 `+x`；如果目标文件的执行位是 Tina 打包语义的一部分，则保留并在提交说明中记录原因。

### 8.7 PR 和 `main` 保护

建议在 GitHub 为 `main` 开启：

- 禁止 force push 和删除分支；
- 至少一次审查后合并；
- 合并前要求分支与 `main` 无冲突；
- 自动检查 Markdown 链接、`git diff --check`、脚本语法和可执行的主机测试；
- 镜像制作和实机测试作为人工受控检查，不把“尚无硬件”误判为软件通过。

文档、源码和实机状态必须同步更新。任何改变当前结论的提交，都应检查 `CURRENT_STATE.md`、`NEXT_IMAGE.md` 和对应 release README 是否需要一起修改。

### 8.8 标签、镜像和 Release

当前仓库只有早期标签，不能仅根据目录名推断历史镜像与 commit 的映射，也不应未经核实补打旧标签。

以后在最终验收且获得授权后使用统一的注释标签，例如：

```text
mosquito-t113-dev-v5.6.2-adb-on-validated
```

每个发布版本应包含 release README、`.img.sha256`、测试证据和对应 tag。`.img` 保持不进 Git；需要团队分发时上传为 GitHub Release 附件或放入受控制品库。

### 8.9 旧的便携环境分支如何处理

`agent/portable-development-setup` 包含 SDK 基线描述、补丁和环境脚本，方向与 WSL2 迁移相符，但它从早期基线派生，README 和部分板级补丁已落后于当前 v5.6.2。

不要直接合并整个分支。建议新建 `infra/wsl2-development-setup`，从当前 `main` 出发，逐项移植并复核：

1. 可优先复用主机检查、SDK 校验、备份和覆盖安装脚本；
2. 所有板级补丁必须与当前 `tina-overlay/` 逐项比较；
3. ADB、包版本和 README 的旧改动不能覆盖当前 v5.6.2 状态；
4. 移植完成后用当前 SDK 基线和目标板重新验证。

### 8.10 公开仓库治理

当前仓库没有声明开源许可证。继续公开协作前应明确：

- 自研代码采用何种许可证，或明确保持专有；
- Tina SDK 衍生文件是否允许公开；
- 数据手册、PCB 网表等硬件资料是否有再分发权；
- 是否需要增加 `SECURITY.md`，说明漏洞和敏感信息报告方式。

未确认权利前，不要自动上传新增的第三方 SDK、数据手册、固件二进制或供应商工具。

## 9. `AGENTS.md` 的职责

仓库已经存在详细的 `AGENTS.md`，不应再创建第二份竞争规则。它应继续集中保存：

- 开始任务前的读取顺序；
- 当前镜像优先诊断和新镜像审批门槛；
- A/B/C/D 证据等级；
- 危险操作、Git、发布和新增文件规则；
- 设备身份校验、超时和最大重试次数；
- 哪些动作必须由项目负责人确认。

本文只解释工作方式。具体构建和测试命令应固化为版本化脚本，避免 Agent 每次临时拼接复杂命令。

## 10. 新同事环境配置顺序

### 10.1 Windows

1. 启用虚拟化并安装 WSL2 Ubuntu；
2. 用 `wsl -l -v` 确认发行版 `VERSION` 为 `2`；
3. 安装 Windows ADB、USB/UART 驱动和必要的烧录工具；
4. 固定 ADB 与脚本路径，但把设备序列号和 COM 口保存在本机配置中。

### 10.2 WSL2

```bash
mkdir -p ~/work/mosquito
cd ~/work/mosquito
git clone https://github.com/skyjane1129-blip/mosquito-t113.git
cd mosquito-t113
```

完整 Tina SDK 放在同级 `~/work/mosquito/tina-t113`。SDK 的版本、来源、校验值和恢复方法应单独记录，但 SDK 本体不提交到本仓库。

### 10.3 最小验收清单

```text
[ ] WSL 发行版 VERSION 为 2
[ ] 仓库和 Tina SDK 位于 /home/...，不是 /mnt/c/...
[ ] command -v codex 返回 WSL 中的 Linux 路径
[ ] Git 工作树正常，并且迁移完成后只由 WSL Git 写入
[ ] 已读 AGENTS.md、CURRENT_STATE.md 和 NEXT_IMAGE.md
[ ] Tina SDK 当前基线可编译
[ ] Windows adb.exe 能识别指定开发板
[ ] WSL2 能调用 Windows adb.exe
[ ] UART 自动采集不会与 MobaXterm 抢占串口
[ ] 测试程序可部署、执行并返回结构化结果
[ ] logcat、dmesg、UART 和退出码可归档
[ ] 失败结果可以交给 Agent 自动分析
[ ] 未经负责人明确授权不会制作或发布新镜像
```

当前 `main` 尚未提供完整的一键环境检查和板端自动测试脚本。相关脚本从旧分支复核迁移并在目标板验证后，才可以把它们写成同事必做步骤。

## 11. 后续 Windows 4G 数据客户端

当前仓库中的 4G 内容属于 T113 板端系统和上传逻辑。以后开发 Windows 数据客户端时，建议建立不同职责的仓库：

```text
WSL2 ext4
~/work/mosquito/mosquito-t113
└── Tina 覆盖层、板端应用和板端 4G 上传逻辑

Windows NTFS
C:\project\mosquito-windows-client
└── Windows UI、安装包和 Windows 测试
```

这不是同一仓库的两份副本。板端仓库使用 WSL2/Linux 工具链；Windows 客户端使用 Windows-native 工具链。双方共享的协议应版本化，可先在一个仓库中建立明确的 `protocol/` 目录；只有在协议需要独立发布和被多个产品消费时，再拆成单独仓库。

典型数据链路建议为：

```text
开发板 -> HTTPS/MQTT -> 公网服务或消息平台 -> Windows 客户端
```

## 12. 仍需负责人填写的本机和项目参数

- [ ] WSL Ubuntu 的准确版本；
- [ ] Tina SDK 来源、版本、校验值和许可边界；
- [ ] Windows `adb.exe` 的固定路径；
- [ ] 测试板 ADB 序列号配置方式；
- [ ] UART COM 口、波特率和 `adb-on` 执行方式；
- [ ] 自动测试超时和最大重试次数；
- [ ] PhoenixCard 等烧录工具的授权边界；
- [ ] 正式镜像的本地保存和团队发布位置；
- [ ] 4G 服务端、协议和 Windows 客户端仓库规划。

## 13. 一句话总结

```text
板端仓库、SDK 和编译留在 WSL2 ext4，硬件访问留在 Windows；
WSL2 Agent 自动调用 Windows ADB/UART 完成诊断和测试；
只有取得明确授权后才制作新镜像；
GitHub 保存可审查的覆盖层、规则和证据，不保存完整 SDK 和镜像二进制。
```
