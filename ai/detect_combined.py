# -*- coding: utf-8 -*-
r"""
蚊子 + 蚊卵 合并识别（交接版）—— 蚊子用两级模型，蚊卵用切片模型。

    python detect_combined.py <图片或目录>                 结果写到 <目录>\识别结果\ + 汇总.csv
    python detect_combined.py <目录> --out D:\out
    python detect_combined.py <目录> --egg-tile 256        手机图(4160 宽)用 256；产品相机(3264 宽)默认 224
    python detect_combined.py <目录> --cls-thr 0.8          蚊子判定更严格
    python detect_combined.py <目录> --egg-conf 0.35        卵判定更严格
    python detect_combined.py <目录> --no-label             结果图只画框不写字

【为什么两个类走两套模型】
  蚊子：整图 1280 送 YOLO11m 找候选，再裁片送分类器挡掉蚂蚁/蛹/碎屑（同事的方案，实拍验证无误报）。
  蚊卵：卵只有 ~20px，整图送模型等于把卵缩到几个像素——必须切成小片再送。
        本脚本按图宽比例切片（3264 宽 -> 224px），2 倍上采样到 640 推理，再在全图坐标上跨片去重。
        去重只在**不同切片**的框之间做（IoU>0.2 合并）：同片内的重叠框是两颗真卵，不能合。

models/
  mosq_det_v2.pt     YOLO11m 检测，蚊子候选（也出卵，但本脚本不用它的卵）
  mosq_cls.pt        YOLO11s-cls，mosquito / not_mosquito
  mosquito_egg_r3.pt YOLO26s，卵（类 1）；它的蚊子头没评过，本脚本不用
"""
from __future__ import annotations

import argparse
import csv
import os
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageOps
from ultralytics import YOLO

sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
MOS_DET = HERE / "models" / "mosq_det_v2.pt"
MOS_CLS = HERE / "models" / "mosq_cls.pt"
EGG_DET = HERE / "models" / "mosquito_egg_r3.pt"
IMG_EXT = (".jpg", ".jpeg", ".png", ".bmp")


# ------------------------------------------------------------------ 蚊子路（同事的两级方案，原样保留）
def crop_box(im: Image.Image, x0, y0, x1, y1, pad=0.25):
    W, H = im.size
    s = max(x1 - x0, y1 - y0) * (1 + pad)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    return im.crop((max(0, cx - s / 2), max(0, cy - s / 2), min(W, cx + s / 2), min(H, cy + s / 2))).resize((256, 256), Image.LANCZOS)


class MosquitoDetector:
    def __init__(self, det_conf=0.15, cls_thr=0.7, imgsz=1280):
        self.det = YOLO(str(MOS_DET)); self.cls = YOLO(str(MOS_CLS))
        self.mi = [k for k, v in self.cls.names.items() if v == "mosquito"][0]
        self.det_conf, self.cls_thr, self.imgsz = det_conf, cls_thr, imgsz

    def run(self, im: Image.Image):
        W, H = im.size
        r = self.det.predict(im, imgsz=self.imgsz, conf=self.det_conf, iou=0.5, verbose=False)[0]
        keep, rejected = [], []
        for b, c, cf in zip(r.boxes.xyxy.tolist(), r.boxes.cls.tolist(), r.boxes.conf.tolist()):
            if int(c) != 0:
                continue                                   # 卵不走这一路
            if (b[2] - b[0]) > W * 0.35 or (b[3] - b[1]) > H * 0.35:
                continue                                   # 整张纸被框住的异常框
            p = self.cls.predict(crop_box(im, *b), imgsz=256, verbose=False)[0].probs.data[self.mi].item()
            (keep if p >= self.cls_thr else rejected).append((*b, p))
        return keep, rejected


# ------------------------------------------------------------------ 卵路（切片 + 跨片去重）
def _starts(total, tile, stride):
    if total <= tile:
        return [0]
    s = list(range(0, total - tile + 1, stride))
    if s[-1] + tile < total:
        s.append(total - tile)                             # 贴边补一片，否则右/下边缘扫不到
    return s


def _iou(a, b):
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0])); iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    i = ix * iy
    return i / ((a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - i + 1e-9)


class EggDetector:
    def __init__(self, conf=0.25, egg_tile=224, ref_width=3264, imgsz=640, overlap=0.25):
        self.m = YOLO(str(EGG_DET))
        self.conf, self.egg_tile, self.ref_width, self.imgsz, self.overlap = conf, egg_tile, ref_width, imgsz, overlap

    def run(self, bgr: np.ndarray):
        H, W = bgr.shape[:2]
        tile = max(160, int(round(self.egg_tile * W / self.ref_width / 32)) * 32)
        stride = max(1, int(tile * (1 - self.overlap)))
        raw, tid = [], 0
        for y in _starts(H, tile, stride):
            for x in _starts(W, tile, stride):
                tid += 1
                r = self.m.predict(bgr[y:y + tile, x:x + tile], imgsz=self.imgsz, conf=self.conf, max_det=2000, verbose=False)[0]
                if r.boxes is None:
                    continue
                for b, s, c in zip(r.boxes.xyxy.cpu().numpy(), r.boxes.conf.cpu().numpy(), r.boxes.cls.cpu().numpy()):
                    if int(c) == 1:
                        raw.append([b[0] + x, b[1] + y, b[2] + x, b[3] + y, float(s), tid])
        # 跨片去重：不同切片 IoU>0.2 视为同一颗；同片只 0.5 兜底（片内低重叠 = 两颗真卵）
        kept = []
        for q in sorted(raw, key=lambda q: -q[4]):
            if not any(_iou(q, k) > (0.5 if q[5] == k[5] else 0.2) for k in kept):
                kept.append(q)
        return [(q[0], q[1], q[2], q[3], q[4]) for q in kept], tile


# ------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src"); ap.add_argument("--out")
    ap.add_argument("--det-conf", type=float, default=0.15); ap.add_argument("--cls-thr", type=float, default=0.7)
    ap.add_argument("--egg-conf", type=float, default=0.25); ap.add_argument("--egg-tile", type=int, default=224,
                    help="以 3264 宽为基准的切片边长；产品相机 224，手机 4160 宽图 256")
    ap.add_argument("--no-reject", action="store_true", help="不画被分类器排除的灰框")
    ap.add_argument("--no-label", action="store_true", help="只画框不写字")
    a = ap.parse_args()

    src = a.src
    files = sorted(p for p in Path(src).iterdir() if p.suffix.lower() in IMG_EXT) if os.path.isdir(src) else [Path(src)]
    if not files:
        raise SystemExit("没找到图片")
    out = Path(a.out) if a.out else (Path(src) if os.path.isdir(src) else Path(src).parent) / "识别结果"
    out.mkdir(parents=True, exist_ok=True)

    mos = MosquitoDetector(a.det_conf, a.cls_thr)
    egg = EggDetector(a.egg_conf, a.egg_tile)
    rows = []
    for p in files:
        pil = ImageOps.exif_transpose(Image.open(p)).convert("RGB")
        bgr = cv2.cvtColor(np.array(pil), cv2.COLOR_RGB2BGR)
        m_keep, m_rej = mos.run(pil)
        eggs, tile = egg.run(bgr)

        im = bgr.copy(); sc = max(im.shape[1] / 1600, 0.6)
        def put(t, x, y, col):
            if not a.no_label:
                cv2.putText(im, t, (int(x), max(int(24 * sc), int(y) - int(8 * sc))), cv2.FONT_HERSHEY_SIMPLEX, 0.7 * sc, col, max(1, int(2 * sc)))
        if not a.no_reject:
            for x0, y0, x1, y1, pr in m_rej:
                cv2.rectangle(im, (int(x0), int(y0)), (int(x1), int(y1)), (170, 170, 170), 2); put(f"reject {pr:.2f}", x0, y0, (170, 170, 170))
        for x0, y0, x1, y1, pr in m_keep:
            cv2.rectangle(im, (int(x0), int(y0)), (int(x1), int(y1)), (255, 60, 0), max(3, int(4 * sc))); put(f"mosquito {pr:.2f}", x0, y0, (255, 60, 0))
        for x0, y0, x1, y1, cf in eggs:
            cv2.rectangle(im, (int(x0), int(y0)), (int(x1), int(y1)), (30, 30, 220), max(2, int(2 * sc)))
        cv2.putText(im, f"mosquito {len(m_keep)}   egg {len(eggs)}", (int(20 * sc), int(52 * sc)), cv2.FONT_HERSHEY_SIMPLEX, 1.3 * sc, (30, 30, 220), max(2, int(3 * sc)))
        ok, buf = cv2.imencode(".jpg", im, [cv2.IMWRITE_JPEG_QUALITY, 90])
        if ok:
            buf.tofile(str(out / p.name))
        rows.append([p.name, len(m_keep), len(eggs), len(m_rej), tile])
        print(f"{p.name}: 蚊子 {len(m_keep)}  卵 {len(eggs)}  (排除候选 {len(m_rej)}, 卵切片 {tile}px)")

    with open(out / "汇总.csv", "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f); w.writerow(["文件", "蚊子数", "卵数", "被排除候选数", "卵切片边长"]); w.writerows(rows)
        w.writerow(["合计", sum(r[1] for r in rows), sum(r[2] for r in rows), "", ""])
    print(f"\n完成 {len(rows)} 张，结果在 {out}")


if __name__ == "__main__":
    main()
