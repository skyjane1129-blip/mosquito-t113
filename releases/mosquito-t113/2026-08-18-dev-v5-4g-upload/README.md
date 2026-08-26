# Mosquito T113 dev-v5 4G 照片上传测试镜像

本版在 dev-v4 拍照功能基础上加入 Air780EG UART PPP 和 HTTP/HTTPS 原始 JPEG 上传，供首次 4G 端到端测试使用。

## 新增内容

- Linux 5.4 异步 PPP 模块：`ppp_generic`、`ppp_async`、`slhc`、`crc-ccitt`
- `pppd`、`chat`、支持 OpenSSL 的 `curl` 和 CA 证书
- `4g-start [APN]`：启动模组、最多等待约3分钟完成 LTE 注册，再建立 `ppp0`
- `4g-stop`：安全结束 PPP 数据连接
- `photo-upload URL [JPEG]`：未指定文件时先运行 `camera-test`，随后上传原始 JPEG
- 板级诊断 v2.5，并修正 `*SIMDETEC: 1,SIM` 的状态解析

默认 APN 是中国联通 `3gnet`。其他运营商可直接传参，例如：

```sh
4g-start cmnet
```

## 烧录前校验

```sh
sha256sum -c tina_t113-mosquito_uart0.img.sha256
```

镜像必须使用 PhoenixCard 的“启动卡 / Startup”模式烧录，不能使用普通 `dd` 或 Etcher。

## 首次实板测试

在浏览器打开 `https://webhook.site` 并复制页面生成的唯一 URL，然后在板端执行：

```sh
4g-start
ip addr show ppp0
ping -c 3 1.1.1.1
photo-upload https://webhook.site/你的UUID
4g-stop
```

上传采用 `Content-Type: image/jpeg` 的原始请求体，请求头包含 `X-Device-ID`、`X-File-Name` 和 `X-Content-SHA256`。Webhook.site 仅用于非敏感测试照片，且可能限制请求体大小。

拨号失败时查看：

```sh
cat /tmp/mosquito-4g-ppp.log
logread | tail -n 100
```

如果 HTTPS 因开发板时间不正确而验证失败，应先校准系统时间。仅限临时诊断时可禁用证书验证：

```sh
MOSQUITO_CURL_INSECURE=1 photo-upload https://webhook.site/你的UUID
```
