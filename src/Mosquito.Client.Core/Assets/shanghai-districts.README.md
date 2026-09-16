# 上海市行政区边界数据

- 文件：`shanghai-districts.geojson`（嵌入资源 `Mosquito.Client.Core.Assets.shanghai-districts.geojson`）
- 来源：阿里云 DataV.GeoAtlas 地理小工具，`https://geo.datav.aliyun.com/areas_v3/bound/310000_full.json`
- 获取日期：2026-09-10（Asia/Shanghai）
- SHA-256：`f699513d5eb63d56350018d90db92345aeefc88c9e4f76197ea405178b6c31ae`
- 大小：83,280 字节；FeatureCollection，16 个 Feature，全部为 MultiPolygon，坐标顺序 `[经度, 纬度]`
- 坐标系：GCJ02（与设备位置、高德瓦片一致；使用 WGS84 底图时由客户端反算）
- 属性：`adcode`、`name`、`center`、`centroid`，加载顺序固定为黄浦区、徐汇区、长宁区、静安区、普陀区、虹口区、杨浦区、闵行区、宝山区、嘉定区、浦东新区、金山区、松江区、青浦区、奉贤区、崇明区

## 使用条款

DataV.GeoAtlas 页面（`https://datav.aliyun.com/portal/school/atlas/area_selector`）为前端动态渲染，2026-09-10 用命令行抓取未能取得条款正文。按该工具公开说明，边界数据由阿里云汇总自公开来源（高德等），面向学习与研究用途；**正式交付前需由项目负责人确认其许可是否覆盖本产品的使用场景**。若不可用，替代方案：

- OpenStreetMap 行政边界（`admin_level=6` relation，ODbL 许可，需署名“© OpenStreetMap contributors”，坐标为 WGS84，需自行拼接多边形并转换为 GCJ02）；
- 天地图（国家地理信息公共服务平台）矢量边界与底图服务，需申请开发者密钥（tk），适合政府项目正式使用；
- 上海市测绘部门提供的正式行政区划数据。

边界用于地图展示、按区筛选与设备计数，不用于精确的地址判定。

## 面积核对

`DistrictShape.AreaSquareKm` 用球面多边形公式计算（R = 6371.0088 km），`MapTests` 校验浦东新区与崇明区为面积最大的两个区、黄浦区最小；实际数值见核心测试输出。
