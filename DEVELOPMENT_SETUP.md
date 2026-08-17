# 在另一台电脑继续开发

本项目采用“GitHub 保存 Mosquito 自研代码，完整 Tina SDK 单独迁移”的方式。推荐的主机环境是 x86_64 Ubuntu 22.04；Windows 用户应在 Ubuntu 虚拟机中完成 Tina 构建和 ARM 交叉编译。

不要把项目或 SDK 放在 Windows 共享目录、NTFS 挂载目录或 `/mnt/c` 中。Tina SDK 包含大量符号链接、可执行权限和大小写敏感路径，应放在 Ubuntu 自己的 ext4 文件系统中。

## Ubuntu 主机依赖

在新安装的 Ubuntu 22.04 中执行：

```sh
sudo apt update
sudo apt install build-essential git subversion gawk flex bison quilt \
  libssl-dev libncurses-dev zlib1g-dev xsltproc libxml-parser-perl \
  unzip rsync file patch zstd python3 libc6-i386 lib32z1 lib32stdc++6
```

安装后用 `scripts/check-host.sh` 做最终检查。完整 Tina SDK 中的部分旧组件可能还会根据所选软件包要求其他依赖；脚本报告的缺项应先处理，再开始编译。

## 推荐方案：迁移当前已验证的 SDK

这是最可靠的方案，因为当前 SDK 中包含厂商源码、100ASK 扩展、下载缓存、预编译工具链以及已经验证的兼容修改。

### 1. 在当前电脑制作 SDK 迁移包

先接入一个至少有约20GB可用空间的移动硬盘或其他存储设备，然后执行：

```sh
cd /home/sky/Project/mosquito
./scripts/backup-sdk.sh ./tina-t113 /移动硬盘上的目录
```

脚本会生成：

```text
mosquito-t113-sdk-YYYYMMDD.tar.zst
mosquito-t113-sdk-YYYYMMDD.tar.zst.sha256
```

迁移包保留源码、`.repo` 元数据、`dl/` 下载缓存和 `prebuilt/` 工具链，只排除可重新生成的 `out/` 编译结果。不要把这个大型迁移包提交到 GitHub。

### 2. 在新电脑克隆本仓库

```sh
git clone https://github.com/skyjane1129-blip/mosquito-t113.git mosquito
cd mosquito
```

私有仓库需要先在新电脑登录 GitHub，或者配置有访问权限的 SSH 密钥。

### 3. 校验并解压 SDK

```sh
sha256sum -c mosquito-t113-sdk-YYYYMMDD.tar.zst.sha256
tar --zstd -xf mosquito-t113-sdk-YYYYMMDD.tar.zst -C /你希望保存SDK的上级目录
```

建议最终目录结构如下：

```text
工作目录/
├── mosquito/       GitHub仓库
└── tina-t113/      完整Tina SDK
```

### 4. 检查新电脑和 SDK

```sh
cd /路径/mosquito
./scripts/check-host.sh /路径/tina-t113
./scripts/verify-sdk.sh /路径/tina-t113
```

两个命令都显示 `[READY]` 才表示环境完整。

## 备选方案：从原始 SDK 重新恢复

如果无法复制当前 SDK，需要先取得与 [`sdk/SDK_BASELINE.md`](sdk/SDK_BASELINE.md) 对应的 100ASK T113s3 Tina4SDK，并按照它自带的 README 应用 `100ASK_T113-Pro_TinaSDK` 扩展。之后运行：

```sh
cd /路径/mosquito
./scripts/install-overlay.sh /路径/tina-t113 --check
./scripts/install-overlay.sh /路径/tina-t113 --apply
```

第一次命令只检查，不修改 SDK；第二次才会复制 Mosquito 板级文件并应用补丁。安装脚本不会删除 SDK 中的其他文件，并且可以重复运行。

由于厂商 SDK 中包含未被 manifest 跟踪的工具链和预编译文件，仅执行 `repo init`/`repo sync` 不能恢复当前完整开发环境，因此仍优先推荐使用迁移包。

## 配置当前终端

每次新开 Ubuntu 终端后执行：

```sh
cd /路径/mosquito
source scripts/setup-env.sh /路径/tina-t113
```

该命令会设置 `MOSQUITO_SDK`、`TINA_BUILD_TOP`、`STAGING_DIR`、`CROSS_COMPILE` 和工具链 `PATH`。

## 编译 Linux 应用

一条命令编译验证程序：

```sh
./scripts/build-hello.sh /路径/tina-t113
```

输出文件位于：

```text
apps/hello-t113/build/hello-t113
```

通过串口在板端执行 `rz`，再从 MobaXterm 选择该文件发送即可。

## 编译完整系统镜像

```sh
MOSQUITO_JOBS=8 ./scripts/build-image.sh /路径/tina-t113
```

脚本会先验证覆盖层和关键补丁，然后依次执行环境初始化、`lunch t113_mosquito-tina`、编译和 PhoenixCard 镜像打包。镜像输出在 SDK 的 `out/t113-mosquito/` 中。

## 新电脑验收清单

- `check-host.sh` 显示 `[READY]`。
- `verify-sdk.sh` 显示 `[READY]` 且工具链 SHA-256 匹配。
- `build-hello.sh` 生成 ARM 32位、musl hard-float 程序。
- 程序通过 `rz` 传到开发板后可以运行。
- 需要修改系统时，`build-image.sh` 能生成 PhoenixCard `.img`。
