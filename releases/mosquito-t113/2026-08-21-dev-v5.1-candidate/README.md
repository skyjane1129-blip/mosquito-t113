# Mosquito T113-S3 dev-v5.1 candidate

状态：已完成源码、交叉编译、rootfs 内容和 PhoenixCard 打包验证；尚未完成干净 TF 卡实板验收，因此暂标记为 candidate，不是正式 v5.1。

## 镜像

- 文件：`mosquito-t113-dev-v5.1-candidate.img`
- 大小：11792384 bytes
- SHA-256：`fc27bef1419f4ba8fe48699becb87786691f2c7f5c82da3de281728d502c41e1`
- 烧录：PhoenixCard，模式选择“启动卡 / Startup”
- 禁止使用普通 `dd` 或 Etcher

## 相比 dev-v5 的修复

- `4g-start` 不再依赖 BusyBox 中缺失的 `id`。
- 自动加载 `crc-ccitt`、`slhc`、`ppp_generic` 和 `ppp_async`。
- 镜像明确启用 BusyBox `mknod` 和 `rmdir`，自动、幂等创建 `/dev/ppp` 并正确释放启动锁。
- 预置 `/etc/resolv.conf -> /tmp/resolv.conf.auto`，PPP 成功后安装 DNS。
- PPP 联网后尝试 NTP 校时，并恢复/保存持久化随机种子。
- `photo-upload` 强制 HTTP/1.1，默认检查时间、随机池并执行 TLS CA 验证。
- 保留系统完整 CA bundle，额外补充官方 ISRG Root X1 供当前 Let's Encrypt 链使用。
- `camera-test` 增加 TF 卡剩余空间检查、`.part` 临时写入和 JPEG SOI/EOI 校验；允许 UVC 帧在 EOI 后附带对齐填充字节。
- 新增 `power-test`，读取 BQ25895 电压、供电、充电和故障状态。
- 增强 `gnss-test`，报告定位耗时、HDOP、卫星数和 C/N0，默认隐藏坐标。
- Air780EG 诊断默认遮挡 ICCID，并在 PPP/`4g-start` 占用 UART 时拒绝并发访问。

## 干净烧录验收

需要执行的命令：

```sh
4g-start
photo-upload 'https://测试接收地址'
4g-stop
power-test
gnss-test
```

其中 `gnss-test` 应在室外开阔天空下运行；它最多等待 5 分钟。正式 TLS 验收不得设置 `MOSQUITO_CURL_INSECURE=1`。

通过条件：无需人工 `modprobe`、`mknod`、创建 DNS 链接、校时或修改上传脚本，HTTPS 上传返回 HTTP 200；重启后可重复完成。实板通过后再将此候选版本晋级为正式 v5.1。
