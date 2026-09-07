# Mosquito Windows Capture Client

面向疾控中心和卫健部门现场人员的 Windows 10/11 桌面客户端。界面采用浅色政务医疗工作台风格，深蓝用于主导航和可信操作，青绿色用于采集主按钮与正常状态。

## 主要能力

- 固定目标设备序列号，拒绝误连其他 ADB 设备；
- 一键完成相机上电、焦距锁定、3264×2448 拍照、拍前温湿度/电源采样、拍后断电；
- 校验元数据版本、采集 UUID、照片长度、SHA-256 和 JPEG 尺寸；
- 默认使用 Windows 网络上传，也可明确选择开发板 4G 直传；两个通道不会静默回退；
- 传感器失败时保留照片并显示 `PARTIAL`；
- 只显示电池电压和充电状态，不伪造电量百分比；
- SQLite 本地上传队列，失败最多重试 5 次；
- 云端记录列表、状态/日期筛选、详情、短时照片预览和 UTF-8 CSV 导出；
- 底部始终显示 `DEMO ONLY`，避免把自动 root ADB 演示方案误认为生产方案。

本软件只采集现场环境和设备数据，不设计患者、个人身份或健康信息字段。

## 构建

```bash
export PATH=/home/janelinux/.local/share/dotnet:$PATH
dotnet build src/Mosquito.Client/Mosquito.Client.csproj -c Release
dotnet run --project tests/Mosquito.Client.CoreTests -c Release
dotnet publish src/Mosquito.Client/Mosquito.Client.csproj \
  -c Release -r win-x64 --self-contained true \
  -o artifacts/publish/win-x64
```

发布目录可复制到 Windows，但 Git 仓库和可写源码继续保留在 WSL ext4。

## 配置

`src/Mosquito.Client/appsettings.json` 包含：

- Windows ADB 路径；
- 唯一允许的开发板序列号；
- 板端命令搜索路径；
- 云端 API 地址；
- 本地照片、元数据和 SQLite 队列目录；
- ADB 超时和默认焦距。

配置文件不保存云端密码。操作员每次启动后在界面顶部登录。生产版应进一步接入单位统一身份认证，并将令牌保存到 Windows Credential Manager。

## 当前演示边界

- 自动 ADB 依赖演示镜像和当前临时部署的板端命令；
- 开发板 4G 直传需要正式 HTTPS API、板端设备密钥和 4G 联网后才能实测；
- 真实阿里云部署需要企业账号、域名、ICP 备案、私有 OSS、PostgreSQL 和 ECS RAM Role；
- 对外发布前应完成代码签名、安装包、杀毒软件兼容性及客户单位终端策略测试。
