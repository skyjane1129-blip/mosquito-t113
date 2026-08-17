# Tina SDK 基线身份

以下信息于2026-08-17从已经成功生成并烧录 `dev-v4` 的开发环境中采集，用于判断另一台电脑上的 SDK 是否与当前项目兼容。

## 已验证基线

- 主机：x86_64 Ubuntu 22.04.5 LTS
- SDK：100ASK T113s3 Tina4SDK，底层 manifest 为 `D1_Tina_Open`
- manifest 项目提交：`a132e2985a9b122a9eb97859faf60d03d9460279`
- manifest selector SHA-256：`fa852181de5f04b546004e7a71103dd6da4e2b37187f99010c9c2e38803c2e45`
- `tina-d1-h.xml` SHA-256：`44777bbb16572474b133ebd03e54d7194c30b96f4ab10bcd4ee0c27afa1d851f`
- 目标：`t113_mosquito-tina`
- 内核：Linux 5.4.61
- 工具链：OpenWrt/Linaro GCC 6.4.1，ARMv7 hard-float musl
- 编译器文件 SHA-256：`54b1327151da9d2e0ac805345ccb4c5ac9f080e22f1c89d881873b3fa48e87f6`

相同内容也保存在机器可读的 [`BASELINE.env`](BASELINE.env) 中，`scripts/verify-sdk.sh` 会自动使用这些值。

## SDK 来源线索

当前 SDK 自带 README 将其标识为“100ASK T113s3 Tina4SDK”，并引用以下扩展仓库：

```text
https://github.com/DongshanPI/100ASK_T113-Pro_TinaSDK.git
```

基础 SDK 的实际下载受原发布方的分发方式和授权约束影响，本仓库不重新分发完整 SDK 或工具链。

## 本仓库额外保存的差异

`tina-overlay/` 保存 Mosquito 板级目录、设备树、根文件系统配置和应用包；`sdk-patches/` 保存当前已验证 SDK 中原先遗漏的修改：

- SPL 安全库构建修正。
- T113/Sun8iW20 U-Boot 时钟驱动接入。
- 无 OP-TEE 场景下启动第二个 Cortex-A7 核心。
- Tina 包构建兼容修正以及手动 root ADB。
- `pack_img.sh` 的 shell 兼容修正。
- Ubuntu 22.04 下 mklibs 和 squashfs 主机工具编译修正。

XR829 Wi-Fi 固件在当前本机 SDK 中也与 manifest 版本不同，但 Mosquito `defconfig` 未启用 XR829/Wi-Fi，因此这些二进制不提交到本仓库，也不是当前镜像构建依赖。若要求整个 SDK 字节级保存，请使用 `scripts/backup-sdk.sh`。
