# hello-t113

用于验证 Mosquito T113-S3 的最小交叉编译和串口传输流程。

推荐在仓库根目录执行：

```sh
./scripts/build-hello.sh /路径/tina-t113
```

程序会输出到 `apps/hello-t113/build/hello-t113`，并自动显示文件类型和 MD5。

以下是手动编译方式。

在已将 Tina 工具链加入 `PATH` 的 Ubuntu 终端中运行：

```sh
export STAGING_DIR="$TINA_BUILD_TOP/out/t113-mosquito/staging_dir/target"
arm-openwrt-linux-muslgnueabi-gcc -Os -s -o hello-t113 hello-t113.c
file hello-t113
```

目标文件应显示为 32-bit ARM EABI5、动态链接、musl hard-float。

通过串口在开发板执行 `rz`，用 Windows 端 ZMODEM Send 选择 `hello-t113`。传输完成后：

```sh
chmod +x hello-t113
./hello-t113
```
