# -*- coding: utf-8 -*-
"""服务端蚊卵识别入口：一张照片 -> 检测框 JSON + 标注图。

由 Mosquito.Cloud.Api 的 EggAnalysisService 以子进程方式调用：
    python analyze.py --weights ai/models/mosquito_egg_r1.pt --image in.jpg
                      --out-json out.json --out-image out.jpg [--conf 0.25] [--egg-tile 448]

推理逻辑完全复用模型作者提供的 egg_count.py（count.py 原文）：蚊卵走切片 + 跨片去重，
蚊子走整图多尺度。这里只做参数装配、JSON 输出和标注图绘制，不改动检测算法。
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import cv2  # noqa: E402

import egg_count  # noqa: E402


def draw(img, det):
    """与 count.py 相同的标注风格：绿框蚊子、橙框蚊卵，左上角计数。"""
    im = img.copy()
    sc = max(img.shape[1] / 1600, 0.6)
    nm = sum(1 for d in det if d[5] == 0)
    ne = sum(1 for d in det if d[5] == 1)
    for x1, y1, x2, y2, s, c, *_ in det:
        h = y2 - y1
        # Board photos have eggs of only ~10 px: keep lines thin and label only boxes big enough to read,
        # otherwise the text hides everything around the detection.
        thick = max(1, int(3 * sc)) if h >= 40 else 1
        cv2.rectangle(im, (int(x1), int(y1)), (int(x2), int(y2)), egg_count.COLOR[c], thick)
        if h >= 40:
            cv2.putText(im, f"{egg_count.NAMES[c]} {s:.2f}", (int(x1), max(int(24 * sc), int(y1) - int(6 * sc))),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6 * sc, egg_count.COLOR[c], max(1, int(2 * sc)))
    cv2.putText(im, f"mosquito {nm}   egg {ne}", (int(20 * sc), int(52 * sc)),
                cv2.FONT_HERSHEY_SIMPLEX, 1.3 * sc, (0, 0, 255), max(2, int(3 * sc)))
    return im, nm, ne


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", required=True)
    ap.add_argument("--image", required=True)
    ap.add_argument("--out-json", required=True)
    ap.add_argument("--out-image", default="")
    ap.add_argument("--conf", type=float, default=0.25)
    ap.add_argument("--egg-tile", type=int, default=448, help="以 --ref-width 为基准的切片边长（固定相机 3264 宽用 448）")
    ap.add_argument("--ref-width", type=int, default=3264)
    ap.add_argument("--egg-imgsz", type=int, default=640)
    ap.add_argument("--mos-imgsz", default="544,768,960")
    ap.add_argument("--overlap", type=float, default=0.25)
    ap.add_argument("--jpeg-quality", type=int, default=90)
    a = ap.parse_args()

    started = time.perf_counter()
    img = egg_count.rd(Path(a.image))
    if img is None:
        print(f"cannot decode image: {a.image}", file=sys.stderr)
        return 2

    from ultralytics import YOLO  # 延迟导入：让参数错误尽早报出来
    import ultralytics
    import torch

    model = YOLO(a.weights)
    args = SimpleNamespace(
        conf=a.conf, egg_tile=a.egg_tile, ref_width=a.ref_width, egg_imgsz=a.egg_imgsz,
        mos_imgsz=[int(x) for x in a.mos_imgsz.split(",")], overlap=a.overlap)
    det, n_egg_raw, tile, n_tile = egg_count.detect(model, img, args)

    if a.out_image:
        annotated, nm, ne = draw(img, det)
        ok, buf = cv2.imencode(".jpg", annotated, [int(cv2.IMWRITE_JPEG_QUALITY), a.jpeg_quality])
        if not ok:
            print("cannot encode annotated image", file=sys.stderr)
            return 3
        buf.tofile(a.out_image)
    else:
        nm = sum(1 for d in det if d[5] == 0)
        ne = sum(1 for d in det if d[5] == 1)

    result = {
        "model": Path(a.weights).stem,
        "conf": a.conf,
        "eggTile": a.egg_tile,
        "refWidth": a.ref_width,
        "tile": tile,
        "tiles": n_tile,
        "width": int(img.shape[1]),
        "height": int(img.shape[0]),
        "eggCount": ne,
        "mosquitoCount": nm,
        "eggRawCount": n_egg_raw,
        "detections": [
            {"cls": int(c), "name": egg_count.NAMES[int(c)], "conf": round(float(s), 4),
             "x1": round(float(x1), 1), "y1": round(float(y1), 1), "x2": round(float(x2), 1), "y2": round(float(y2), 1)}
            for x1, y1, x2, y2, s, c, *_ in det
        ],
        "durationMs": int((time.perf_counter() - started) * 1000),
        "ultralytics": getattr(ultralytics, "__version__", "?"),
        "torch": getattr(torch, "__version__", "?"),
        "device": "cuda" if torch.cuda.is_available() else "cpu",
    }
    Path(a.out_json).write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
    print(f"egg {ne} mosquito {nm} tiles {n_tile} ({result['durationMs']} ms)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
