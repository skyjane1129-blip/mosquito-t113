# -*- coding: utf-8 -*-
"""服务端蚊子 + 蚊卵识别入口（合并模型 2026-09-16）：一张照片 -> 检测框 JSON + 标注图。

由 Mosquito.Cloud.Api 的 EggAnalysisService 以子进程方式调用：
    python analyze.py --image in.jpg --out-json out.json --out-image out.jpg
                      [--conf 0.25] [--egg-tile 224] [--ref-width 3264] [--cls-thr 0.7] [--det-conf 0.15]

推理逻辑完全复用模型作者交接的 detect_combined.py：
  蚊子 = YOLO11m 整图 1280 找候选 + YOLO11s-cls 分类器挡误报（models/mosq_det_v2.pt + mosq_cls.pt）
  蚊卵 = YOLO26s r3 按 224 px 切片、2 倍上采样、跨片去重（models/mosquito_egg_r3.pt）
这里只做参数装配、JSON 输出和画框。--weights 参数保留但忽略（权重路径由 detect_combined.py 固定在 models/）。
旧的单模型入口保留为 analyze_r1.py。
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import cv2  # noqa: E402
import numpy as np  # noqa: E402
from PIL import Image, ImageOps  # noqa: E402

MODEL_VERSION = "combined_20260916"


def draw(bgr, mosquitoes, rejected, eggs, show_rejected=False):
    """与 detect_combined.py 相同的标注风格：蓝框蚊子（标签为分类器概率）、红框卵、左上角计数。"""
    im = bgr.copy()
    sc = max(im.shape[1] / 1600, 0.6)

    def put(t, x, y, col):
        cv2.putText(im, t, (int(x), max(int(24 * sc), int(y) - int(8 * sc))), cv2.FONT_HERSHEY_SIMPLEX, 0.7 * sc, col, max(1, int(2 * sc)))

    if show_rejected:
        for x0, y0, x1, y1, pr in rejected:
            cv2.rectangle(im, (int(x0), int(y0)), (int(x1), int(y1)), (170, 170, 170), 2)
            put(f"reject {pr:.2f}", x0, y0, (170, 170, 170))
    for x0, y0, x1, y1, pr in mosquitoes:
        cv2.rectangle(im, (int(x0), int(y0)), (int(x1), int(y1)), (255, 60, 0), max(3, int(4 * sc)))
        put(f"mosquito {pr:.2f}", x0, y0, (255, 60, 0))
    for x0, y0, x1, y1, _ in eggs:
        cv2.rectangle(im, (int(x0), int(y0)), (int(x1), int(y1)), (30, 30, 220), max(2, int(2 * sc)))
    cv2.putText(im, f"mosquito {len(mosquitoes)}   egg {len(eggs)}", (int(20 * sc), int(52 * sc)),
                cv2.FONT_HERSHEY_SIMPLEX, 1.3 * sc, (30, 30, 220), max(2, int(3 * sc)))
    return im


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", default="", help="兼容旧调用，忽略")
    ap.add_argument("--image", required=True)
    ap.add_argument("--out-json", required=True)
    ap.add_argument("--out-image", default="")
    ap.add_argument("--conf", type=float, default=0.25, help="卵置信度阈值（杯壁杂质多时 0.35）")
    ap.add_argument("--egg-tile", type=int, default=224, help="以 --ref-width 为基准的卵切片边长（产品相机 224，手机图 256）")
    ap.add_argument("--ref-width", type=int, default=3264)
    ap.add_argument("--cls-thr", type=float, default=0.7, help="蚊子分类器阈值（误报多 0.8~0.9，漏得多 0.5）")
    ap.add_argument("--det-conf", type=float, default=0.15, help="蚊子候选阈值，一般不动")
    ap.add_argument("--show-rejected", action="store_true", help="标注图上画出被分类器排除的灰框")
    ap.add_argument("--jpeg-quality", type=int, default=90)
    a = ap.parse_args()

    started = time.perf_counter()
    try:
        pil = ImageOps.exif_transpose(Image.open(a.image)).convert("RGB")
    except Exception as exc:  # noqa: BLE001
        print(f"cannot decode image: {a.image}: {exc}", file=sys.stderr)
        return 2
    bgr = cv2.cvtColor(np.array(pil), cv2.COLOR_RGB2BGR)

    import ultralytics
    import torch
    import detect_combined as dc  # 延迟导入：加载三套权重

    mos = dc.MosquitoDetector(a.det_conf, a.cls_thr)
    egg = dc.EggDetector(a.conf, a.egg_tile, a.ref_width)
    m_keep, m_rej = mos.run(pil)
    eggs, tile = egg.run(bgr)

    if a.out_image:
        ok, buf = cv2.imencode(".jpg", draw(bgr, m_keep, m_rej, eggs, a.show_rejected), [int(cv2.IMWRITE_JPEG_QUALITY), a.jpeg_quality])
        if not ok:
            print("cannot encode annotated image", file=sys.stderr)
            return 3
        buf.tofile(a.out_image)

    H, W = bgr.shape[:2]
    stride = max(1, int(tile * (1 - egg.overlap)))
    tiles = len(dc._starts(H, tile, stride)) * len(dc._starts(W, tile, stride))
    detections = [
        {"cls": 0, "name": "mosquito", "conf": round(float(p), 4),
         "x1": round(float(x0), 1), "y1": round(float(y0), 1), "x2": round(float(x1), 1), "y2": round(float(y1), 1)}
        for x0, y0, x1, y1, p in m_keep
    ] + [
        {"cls": 1, "name": "egg", "conf": round(float(s), 4),
         "x1": round(float(x0), 1), "y1": round(float(y0), 1), "x2": round(float(x1), 1), "y2": round(float(y1), 1)}
        for x0, y0, x1, y1, s in eggs
    ]
    result = {
        "model": MODEL_VERSION,
        "models": {"mosquitoDetector": dc.MOS_DET.name, "mosquitoClassifier": dc.MOS_CLS.name, "eggDetector": dc.EGG_DET.name},
        "conf": a.conf,
        "clsThreshold": a.cls_thr,
        "detConf": a.det_conf,
        "eggTile": a.egg_tile,
        "refWidth": a.ref_width,
        "tile": tile,
        "tiles": tiles,
        "width": int(W),
        "height": int(H),
        "eggCount": len(eggs),
        "mosquitoCount": len(m_keep),
        "mosquitoRejected": len(m_rej),
        "eggRawCount": None,
        "detections": detections,
        "durationMs": int((time.perf_counter() - started) * 1000),
        "ultralytics": getattr(ultralytics, "__version__", "?"),
        "torch": getattr(torch, "__version__", "?"),
        "device": "cuda" if torch.cuda.is_available() else "cpu",
    }
    Path(a.out_json).write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    print(f"mosquito {len(m_keep)} (rejected {len(m_rej)}) egg {len(eggs)} tiles {tiles} ({result['durationMs']} ms)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
