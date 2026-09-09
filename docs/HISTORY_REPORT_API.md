# 历史与上报检查接口约定（待服务端实现）

日期：2026-09-07。本文是 Windows 客户端已实现的消费契约，**不表示云端已上线这些接口**。现有登录、采集上传、详情接口继续兼容。未在板端或云端部署任何修改。

## 通用约定

- 使用当前登录返回的 Bearer token，沿用单位实际配置的 `ApiBaseUrl`。
- JSON camelCase；枚举为字符串；所有传输时间为带时区的 ISO 8601。界面与 CSV 固定转换为 UTC+8。
- `deviceId` 是稳定的登记编号，与 ADB `deviceSerial` 分开。设备编号不得因连接通道变化而变更。
- 客户端内存中保持一次完整查询快照，UI 50 条分页；刷新前的 CSV 使用这一快照，确保导出与已查询条件一致。
- 没有当前新接口时返回 404 或 501。断网、401、5xx 不应伪装成空列表；客户端保留之前数据。

## GET /api/v2/captures

参数：`from`（含）、`toExclusive`（不含）、可选 `deviceId`、可选 `status=Complete|Partial|Failed`、`limit=50`、后续页 `cursor`、`snapshot`。

默认排序 `capturedAtUtc DESC, id ASC`。首次查询建立稳定快照；游标只能在此快照与相同过滤条件内使用。后续所有页返回相同 snapshot，不得因新上报而跳过 / 重复记录。过期快照应返回明确错误，客户端重新查询。

```json
{
  "items": [],
  "nextCursor": null,
  "snapshot": "opaque-stable-snapshot-id",
  "isComplete": true
}
```

`nextCursor=null` 表示最终页。`isComplete=true` 表示服务端此次查询数据完整可用，**不代表这是最后一页**。任何页 `isComplete=false` 都使整个查询不可做完整导出。不得把 500 条硬截断包装成完整结果。

每个 item 保留现有 CloudCaptureRecord 字段，并增加：

| 字段 | 含义 |
|---|---|
| `deviceId` | 稳定设备编号；旧接口缺省时显示编号待确认 |
| `receivedAtUtc` | 服务端校验完成且记录可用的时间；不是创建上传会话时间 |
| `triggerSource` | `SCHEDULED` / `MANUAL`；缺省未提供 |
| `timeSource` | `BOARD` / `WINDOWS` / `SERVER`；不可靠或未知时间源不能标称准确板端采集时间 |
| `failureStage` | 例如 `CAPTURE` / `TRANSFER` / `UPLOAD` / `VALIDATION`，结合既有 failureCode |
| `location` | 此照片关联的 GNSS 快照，不用后来的位置覆盖旧照片 |

```json
{
  "latitude": 31.21,
  "longitude": 121.56,
  "sampledAtUtc": "2026-09-07T03:06:40Z",
  "district": "浦东新区",
  "coordinateSystem": "WGS84"
}
```

上述经纬度仅示例。`location=null` 表示无有效定位。位置必须注明坐标系，不能把 GNSS 原始 WGS84 当作 GCJ02 绘图。当前示意地图仅绘制明确为 `GCJ02` 的有效点，其余保留数值与设备列表。

`GET /api/captures/{id}` 继续返回 `{ capture, photoDownloadUrl, downloadUrlExpiresAt }`，其中 capture 使用同样字段。URL 需是可读取原图的短时地址；客户端直接下载、完整解码后显示，保存使用已下载原始字节。云端 JSON 不允许指定客户端的本地照片路径。

旧接口 `GET /api/captures?limit=500` 兼容：服务器尚未提供 v2 时使用现有数组接口，设备过滤在完整返回范围内完成。返回条数达到 500 就认为可能截断；不推断总数量，不允许完整导出。

## GET /api/v2/devices

参数 `limit=50`、`cursor`、`snapshot`，响应分页 envelope 与 captures 相同。数据来自完整设备登记目录，不能从当前照片查询页拼出“总设备数”。每项：

```json
{
  "deviceId": "MQ-SH-001",
  "deviceSerial": "provisioned-adb-serial",
  "lastLocation": null,
  "latestCaptureId": null
}
```

`lastLocation` 为最后一次有效 GNSS 样本，字段同上；`latestCaptureId` 是最新可查看采集记录。即使设备暂无采集或无 GNSS，也必须出现在登记目录中。

## GET /api/v2/report-checks

参数：`from`、`toExclusive`（按计划时段时间过滤）、可选 `deviceId`、`limit=50`、`cursor`、`snapshot`。客户端读取完整检查快照后按状态过滤，以保证无效状态被保守转成“待确认”时仍可正确筛选。

响应：

```json
{
  "items": [
    {
      "slotId": "MQ-SH-001:plan-revision-3:2026-09-07T03:07:00Z",
      "deviceId": "MQ-SH-001",
      "expectedAtUtc": "2026-09-07T03:07:00Z",
      "state": "Late",
      "scheduleConfirmed": true,
      "scheduleEffectiveAtUtc": "2026-09-07T00:00:00Z",
      "scheduleAnchorUtc": "2026-09-07T00:07:00Z",
      "intervalMinutes": 60,
      "associationReliable": true,
      "captureId": "a73d7c04-a788-4426-93bf-412e4829fc5e",
      "receivedAtUtc": "2026-09-07T03:24:00Z",
      "recordStatus": "Partial",
      "reason": null,
      "district": "浦东新区"
    }
  ],
  "nextCursor": null,
  "snapshot": "opaque-check-snapshot-id",
  "checkedAtUtc": "2026-09-07T03:25:00Z",
  "isComplete": true
}
```

每页 `snapshot` 和 `checkedAtUtc` 必须一致。上报检查服务必须读取服务端完整记录和有效计划，生成稳定 slotId；客户端**不自行生成时段或从 50 / 500 条历史记录推断漏报**。不完整检查响应被拒绝并保留旧快照。

以 T 为计划时间，deadline=T+15 分钟，服务端以 checkedAtUtc 判定：

| state | 条件 |
|---|---|
| Pending | 计划、生效时间、时段关联、时间可信度不能确认 |
| NotDue | checkedAtUtc < T |
| Waiting | T ≤ checkedAtUtc < deadline，尚无可用记录 |
| OnTime | 已收到关联的 Complete / Partial，receivedAtUtc < deadline |
| Overdue | checkedAtUtc ≥ deadline，尚无可用记录 |
| Late | 已收到关联的 Complete / Partial，receivedAtUtc ≥ deadline |

- 必须先确认计划实际生效，不检查 effectiveAt 之前的时段。以 anchor+N×60 分钟计算，示例 11:07 而不是假设 11:00。
- 部分传感器缺失但照片可用的 Partial 也算收到；开始上传 / 上传失败不算收到。
- 重复上传同一个采集 ID 幂等，迟到只修复它原来的 slotId；一次采集不能同时满足两个时段。未来人工额外采集不能顶替定时采集或重置周期。
- `captureId` 只有在关联可靠且记录可用时才用于照片跳转。未知关联应 Pending。
- 客户端对计划参数、15 分钟边界、接收时间、重复 / 冲突关联做一致性校验，异常降为 Pending；这不是代替服务端计算。
- 设备低功耗或 ADB 未连接都不能直接改变这些状态为硬件故障。
- GNSS 缺失不等同上传缺失；没有定位也可以有完整 / 部分采集记录。

## 联调验收

CoreTests 使用假 HttpMessageHandler 验证 553 条跨页、快照和游标、旧接口截断、时区边界、15 分钟边界、部分收到、晚到、未知计划、生效时间、重复关联及旧缺口。

UiTests 使用模拟记录验证 WPF 导航、断网保留、无照片时段、关联跳转和请求乱序。真实服务端需要按同样用例验收后才能宣称端到端可用；当前自动化通过不表示真实低功耗、GNSS、云端计划或生产 4G 链路已经验证。
