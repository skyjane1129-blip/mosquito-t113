# Mosquito Capture Cloud Service

蚊虫采集设备客户端的云端 API。当前实现支持单账号演示，但数据模型、查询条件和对象路径都按多设备设计。

## 已实现

- 操作员登录和带有效期的 HMAC Bearer Token；
- 设备 `X-Device-Key` 鉴权，供开发板 4G 直传；
- 拍摄记录创建、幂等重试、完成、列表、筛选和详情；
- 照片与元数据分离上传，服务端校验文件长度和 SHA-256；
- `COMPLETE` / `PARTIAL` 状态，不丢弃传感器失败时的照片；
- 开发模式：内存数据库和本地私有文件目录；
- 生产模式：PostgreSQL、阿里云私有 OSS、AES256 服务端加密、短时预签名 URL；
- ECS RAM Role 自动获取和刷新临时凭据，不保存 AccessKey；
- 生产安全门禁：默认口令、本地存储、空数据库或非 HTTPS 配置会拒绝启动。

环境数据只描述拍摄现场，不包含姓名、身份证号、患者信息或其他个人信息。

## 本地运行

```bash
export PATH=/home/janelinux/.local/share/dotnet:$PATH
dotnet run --project src/Mosquito.Cloud.Api --urls http://127.0.0.1:5080
```

开发账号仅用于本机联调：`demo / change-me`。不得把该口令带入客户环境。

运行自动测试：

```bash
dotnet build src/Mosquito.Cloud.Api/Mosquito.Cloud.Api.csproj -c Release
dotnet run --project tests/Mosquito.Cloud.SmokeTests -c Release
```

## 2026-09-10 演示部署（本机服务器 + frp）

演示服务端运行在开发客户端的这台 Windows 电脑上，公网入口由云主机上的 frps 转发（`frpc.toml` 中的 `mosquito_api` 代理，本机 `127.0.0.1:5080` → 公网 `8086`；frp 配置和 token 在 `C:\Users\Jane\Documents\frp`，不进入仓库）。

- `scripts\Start-DemoServer.ps1`：以隐藏进程启动 API（`Development` 环境、SQLite `data\mosquito-cloud.db`、本地文件存储 `Storage.PublicBaseUrl=http://139.224.11.244:8086`）和 frpc；日志在 `artifacts\api.*.log`。已在运行的进程不重复启动。
- `scripts\Stop-DemoServer.ps1 [-IncludeFrpc]`：停止 API（可选同时停 frpc）。
- `scripts\Register-DemoServerAutostart.ps1`：注册登录时自动运行 Start-DemoServer 的计划任务（`Mosquito Demo Server`，登录后 30 秒）。脚本不改电源策略；演示期间需自行保持电脑不休眠（例如 `powercfg /change standby-timeout-ac 0`）。反注册：`Unregister-ScheduledTask -TaskName 'Mosquito Demo Server' -Confirm:$false`。
- 演示为 HTTP 非标端口：板端 `MOSQUITO_ALLOW_HTTP=1`，API 以 Development 运行；正式上线仍须 HTTPS 与阿里云门禁。
- 修改代码后：`Stop-DemoServer.ps1` → `dotnet build src\Mosquito.Cloud.Api -c Release` → `dotnet run --project tests\Mosquito.Cloud.SmokeTests -c Release` → `Start-DemoServer.ps1`。

## API

- `GET /health`
- `POST /api/auth/login`
- `POST /api/captures`
- `POST /api/captures/{id}/complete`
- `GET /api/captures?deviceSerial=&status=&from=&to=&limit=`
- `GET /api/captures/{id}`

设备端（`X-Device-Key`）：

- `POST /api/device/heartbeat`：登记设备、在线状态（180 秒窗口）、最近环境/电源读数、镜像版本、可选位置；
- `POST /api/device/location`：上报 WGS84 位置（`source` 为 `LBS` / `GNSS`），服务端转换为 GCJ02 供客户端地图使用；
- `GET /api/device/commands?deviceId=&wait=`：长轮询领取指令（最长 30 秒，无指令返回 204），领取即标记 `Dispatched`；
- `POST /api/device/commands/{id}/ack`：回执 `Completed` / `Failed`。

操作员 v2（客户端契约）：

- `GET /api/v2/devices`、`GET /api/v2/devices/{id}`：设备目录（分页 snapshot / cursor），含心跳字段与 GCJ02 位置；
- `GET /api/v2/captures?from&toExclusive&deviceId&status&limit&cursor&snapshot`：分页采集记录；
- `POST /api/v2/devices/{id}/commands`（`{"type":"capture","focus":500}`，有效期 10 分钟）、`GET /api/v2/commands/{id}`、`GET /api/v2/devices/{id}/commands`、`GET /api/v2/devices/{id}/locations`；
- `GET /api/v2/report-checks`：暂返回 501（上报计划未实现）。
- `GET /api/v2/devices/{id}/location-evaluation?hours=`：各定位源（LBS、多基站高德/百度、OpenCellID、FUSED、FUSED_MEDIAN、GNSS）相对参考点的误差统计（样本数、均值/中位/P90/最值、平均半径），用于对比哪种算法在该点最准。

### 位置与多源融合（2026-09-11）

设备每次定位都随 `POST /api/device/location`（及心跳、采集）带上 Air780EG 观测到的服务小区与邻区（`cells`：mcc/mnc/tac/cellId/pci/earfcn/rsrp/rsrq）。`LocationEngine` 据此并行请求可配置的多基站定位器，再做逆方差加权融合，并对固定点的近期历史取中值，全部候选入库、择优对外。板端 LBS 原始估计为 WGS84，服务端仍统一转 GCJ02 供地图使用。

不依赖任何厂商 key 的自学习算法（`CELL_LEARNED`）：合宙单基站答案本质是“驻留小区的位置”，服务端把每次答案按驻留小区记成“小区→位置”表，再从当前邻区里挑 RSRP 最强的已知小区作为估计，并对其近期历史取中值（`CELL_LEARNED_MEDIAN`）。在同一固定点 135 个样本上回放，误差中位数由 540 m 降到 74 m。`auto` 策略顺序：GNSS > CELL_LEARNED_MEDIAN > CELL_LEARNED > FUSED_MEDIAN > FUSED > 原始。

`appsettings.json` 的 `Location` 段（密钥与参考点放在 Git 忽略的 `appsettings.Local.json`）：

- `Policy`：`auto`（默认）或强制某一源（如 `LBS`、`FUSED`、`FUSED_MEDIAN`）做对照。
- `AmapKey` / `BaiduAk` / `OpenCellIdKey`：多基站定位器的密钥；留空则该定位器停用（无 key 时融合等于单基站 LBS）。
- `ReferenceLatitude` / `ReferenceLongitude` / `ReferenceCoordinateSystem`：评估用的真实点（GCJ02 或 WGS84）。
- `SingleCellRadiusMeters`（默认 500）、`HistoryWindowHours`、`HistoryMinSamples`、`HistoryMaxSamples`。

精度实况：单基站 LBS 在上海城区误差数百米到公里级；多基站（高德/百度，需 key）通常 50–300 m；≤20 m 只能靠 GNSS。

采集通过 `POST /api/captures` 带 `commandId` 上传并完成时，服务端自动把指令标为 `Completed` 并更新设备最新采集与照片位置。

本地存储模式额外提供带签名、带时限的上传和下载路由。OSS 模式返回 OSS 预签名 URL，照片不经过应用服务器中转。

### 蚊卵识别（2026-09-15，2026-09-16 换合并模型）

服务端在存好的照片上运行用户训练的模型，生成检测框和标注图；客户端只发请求、等结果、显示。

**当前模型（`combined_20260916`）**：蚊子走两级（`ai/models/mosq_det_v2.pt` YOLO11m 整图 1280 找候选 + `mosq_cls.pt` YOLO11s-cls 分类器挡误报，阈值 `Analysis:MosquitoClassifierThreshold` 0.7），蚊卵走 `mosquito_egg_r3.pt`（YOLO26s，224 px 切片、跨片去重）。推理代码是作者交接的 `ai/detect_combined.py` 原文，`ai/analyze.py` 只做装配与输出。板子 3264×2448 照片约 300 个切片、CPU 23 秒。

与首版 r1（`analyze_r1.py`，仅保留对照）在同一批板子照片上的对比：

| 照片 | r1（切片 192） | 合并模型 |
| --- | --- | --- |
| MQ-SH-001_20260916_164411 | 卵 16 / 蚊 1 | 卵 93 / 蚊 2 |
| MQ-SH-001_20260916_171207 | 卵 34 / 蚊 0 | 卵 111 / 蚊 2 |
| img_040116 | 卵 61 / 蚊 0 | 卵 103 / 蚊 2 |
| 09-15 有卵滤纸（c5418dfe） | 卵 11 / 蚊 0 | 卵 89 / 蚊 0 |

作者自述水平（样本少）：蚊子实拍无误报、约每 7 只漏 2 只、摇蚊会被当成蚊子；卵在产品相机未见样本上召回约 88%，主要误差是杯壁划痕、污点被当成卵（每图约 10 个）和密集糊团内部漏检。选择服务端推理而不是客户端内置模型的原因：模型与 Python 环境只需维护一份，结果随采集记录持久化、所有客户端共享并可进报表，客户端安装包不用带 1 GB 的推理运行时。

- 参数：`Analysis:EggTile` 224（手机 4160 宽图 256）、`Analysis:Confidence` 卵阈值 0.25（杯壁杂质多时 0.35）、`MosquitoClassifierThreshold` 0.7、`MosquitoCandidateConfidence` 0.15。`ModelPath` 只用于存在性检查，三套权重路径由 `detect_combined.py` 固定在 `ai/models/`。
- 运行时：`ai/python311/` 是嵌入版 CPython 3.11.9 + pip，装有 ultralytics 8.4、torch CPU、opencv-python-headless（约 1.1 GB，Git 忽略，重建见 `ai/README.md`）。`Analysis:PythonPath` 留空时依次找 `ai/python311/python.exe`、`ai/.venv/Scripts/python.exe`、PATH 上的 `python`。
- 接口：`POST /api/v2/captures/{id}/analysis?force=false` 启动一次识别（照片未上传完成返回 400；已有完成结果且不 force 时直接返回；进行中返回 202），`GET /api/v2/captures/{id}/analysis` 轮询；结果同时出现在 `GET /api/captures/{id}` 的 `capture.analysis` 与 `annotatedDownloadUrl`（带签名的标注图下载地址）。`analysis` 含 `status`（Running/Completed/Failed）、`eggCount`、`mosquitoCount`、`detections[]`（原图像素坐标 x1,y1,x2,y2 与置信度）、`durationMs`、`modelVersion`、`error`。
- 执行：`EggAnalysisService` 把照片写到临时目录，以子进程运行 `analyze.py`（超时 `Analysis:TimeoutSeconds`，默认 600 s），标注图存为照片同目录的 `analysis-{modelVersion}.jpg`，同一时刻只跑一个识别（CPU 推理）。板子 3264×2448 照片实测约 70 个切片、CPU 7～9 秒。
- 已知限制：仅本地存储模式可用（OSS 模式下 `OpenReadAsync/PutAsync` 未实现）；单次识别约 23 s，同一时刻只跑一个。

## 阿里云上线门禁

在购买资源和得到域名后才能执行真实上线：

1. 选择中国大陆地域，创建专有网络、ECS、PostgreSQL 和私有 OSS Bucket；
2. 为 ECS 绑定仅允许目标 Bucket `PutObject`、`GetObject`、`HeadObject` 的 RAM Role；
3. Bucket 保持私有，开启默认服务端加密并禁止公共访问；
4. 使用环境变量覆盖 `.env.example` 中所有敏感值；
5. 只向公网开放 443，SSH 仅允许指定管理 IP；
6. 完成企业域名和 ICP 备案后启用正式域名；
7. 执行数据库初始化、API 健康检查、上传/下载和备份恢复演练。

项目不会自动购买云资源，也不会把演示口令或设备密钥写入仓库。
