# GNSS 地图定位实施（第一阶段）

本阶段只增加板端位置采集和 Traccar OsmAnd 上传，不制作或烧录镜像。

## 板端命令

- `mosquito-location`：独占 `/dev/ttyS1`，打开 Air780EG GNSS，等待有效 `CGNSINF`，输出一行 JSON。输出坐标始终是 WGS84。
- `mosquito-location-upload`：调用上述工具，停止 PPP 后定位；定位完成后启动 PPP，向 Traccar 上传；失败请求保存在 `/mnt/UDISK/mosquito-test/location/outbox/`。

两条命令不能和 `pppd`、`4g-start` 或其他 Air780EG 串口测试并发执行。

## 配置

将 `files/mosquito-location.conf.example` 复制到板端 `/etc/mosquito/location.conf`，设置为 `0600`，填写：

```sh
MOSQUITO_LOCATION_DEVICE_ID=MOSQUITO001
MOSQUITO_TRACCAR_URL=https://tracker.example.cn:5055
MOSQUITO_APN=3gnet
```

在 Traccar 中预先创建设备，设备标识必须与 `MOSQUITO_LOCATION_DEVICE_ID` 完全一致。

## 验收顺序

1. 室外开阔天空下运行 `MOSQUITO_LOCATION_TIMEOUT_SEC=300 mosquito-location`，确认 `valid=true`、`fix_mode=2/3`，并连续取得三次有效定位。
2. 用电脑或局域网先向 Traccar 5055 端口发送固定测试坐标，确认服务器地图能显示。
3. 配置板端后执行 `mosquito-location-upload`，检查 Traccar 设备在线、当前位置和历史轨迹。
4. 断网测试，确认 `.url` 文件保留；恢复网络后确认队列上传并删除。

设备上传原始 WGS84。高德/百度地图的 GCJ-02/BD-09 转换应放在服务器显示层，避免轨迹数据被重复转换。
