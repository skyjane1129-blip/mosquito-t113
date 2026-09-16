# ai/ —— 蚊子 + 蚊卵识别推理环境

| 文件 | 说明 |
| --- | --- |
| `analyze.py` | 服务端入口（当前，合并模型 `combined_20260916`）：一张图 → `--out-json` 检测结果 + `--out-image` 标注图 |
| `detect_combined.py` | 模型作者 2026-09-16 交接的推理脚本原文（蚊子两级 + 卵切片去重），不要改 |
| `models/mosq_det_v2.pt` | YOLO11m 蚊子候选检测（同事训练） |
| `models/mosq_cls.pt` | YOLO11s-cls 蚊子/非蚊子分类器（同事训练） |
| `models/mosquito_egg_r3.pt` | YOLO26s 蚊卵检测 r3（本项目） |
| `模型使用说明_合并_20260916.md` | 作者说明原文（阈值含义、已知水平） |
| `testimages/` | 作者附的 3 张测试图（板子相机 2 张 + 1 张），README 里的对比就是用它们跑的 |
| `analyze_r1.py`、`egg_count.py`、`models/mosquito_egg_r1.pt`、`模型使用说明_r1.md` | 2026-09-15 首版单模型，仅保留对照 |
| `python311/` | 嵌入版 CPython 3.11.9 + pip + ultralytics/torch(CPU)/opencv/pillow（Git 忽略） |

## 重建 `python311/`（换机器或误删时）

```powershell
# 1. 嵌入版 Python（无需安装、无需管理员）
curl.exe -L -o python-embed.zip https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip
Expand-Archive python-embed.zip ai\python311
# 2. 启用 site-packages：把 ai\python311\python311._pth 里的 "#import site" 改为 "import site"，并追加一行 "Lib\site-packages"
# 3. pip
curl.exe -L -o get-pip.py https://bootstrap.pypa.io/get-pip.py
ai\python311\python.exe get-pip.py -i https://pypi.tuna.tsinghua.edu.cn/simple
# 4. 依赖（约 1.1 GB）
ai\python311\python.exe -m pip install -i https://pypi.tuna.tsinghua.edu.cn/simple "ultralytics>=8.4" opencv-python-headless numpy pillow
```

验证：

```powershell
ai\python311\python.exe ai\analyze.py --image ai\testimages\img_040116.jpg --out-json out.json --out-image out.jpg
```

2026-09-16 实测：ultralytics 8.4.153、torch 2.14.0+cpu、opencv 5.0.0、pillow 12.3.0；板子 3264×2448 照片 300 个切片、约 23 秒；`img_040116` 蚊子 2、卵 103。
