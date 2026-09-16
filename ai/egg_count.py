# -*- coding: utf-8 -*-
"""
双尺度推理 + 跨片去重 -> 每图和总计的 蚊子数 / 蚊卵数。

【为什么必须两个尺度】实测目标像素尺寸:
  公开蚊卵 29px @479宽   我们蚊卵 27px @3264宽   -> 比例接近，走切片
  公开蚊子 66px @640宽   我们蚊子 400px @3264宽  -> 差 6 倍，必须整图缩放
上一版用单尺度同时伺候两个类，所以漏掉整只蚊子、却把 30px 的灰尘框成目标。

  蚊卵路: 448px 切片 -> imgsz 640    27px -> 38.6px，对齐训练时的 29*(640/479)=38.7px
  蚊子路: 整图      -> imgsz 544     400px -> 66.6px，对齐训练时的 66px

【为什么必须去重】切片重叠 25%，同一颗卵会在相邻两片各检出一次
（标注侧实测 111 个框切完变 204 个实例，1.84 倍）。直接相加会让计数虚高近一倍。
去重用全图坐标上的类内 NMS(IoU 0.5)。相邻两颗卵是并列关系，实测相切时
轴对齐框 IoU 中位 0.114、最大 0.363，都在 0.5 以下，不会被误并。
"""
from __future__ import annotations

import argparse
import csv
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO

NAMES = {0: "mosquito", 1: "egg"}
COLOR = {0: (0, 255, 0), 1: (0, 190, 255)}


def rd(p: Path):
    d = np.fromfile(str(p), np.uint8)
    return cv2.imdecode(d, cv2.IMREAD_COLOR) if d.size else None


def wr(p: Path, im) -> None:
    ok, b = cv2.imencode(p.suffix or ".jpg", im)
    if ok:
        b.tofile(str(p))


def starts(total: int, tile: int, stride: int) -> list[int]:
    if total <= tile:
        return [0]
    s = list(range(0, total - tile + 1, stride))
    if s[-1] + tile < total:
        s.append(total - tile)          # 贴边补一片，否则右/下边缘扫不到
    return s


def _iou(a, b):
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0])); iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    i = ix * iy
    return i / ((a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - i + 1e-9)


def nms_per_class(raw, conf, iou_same=0.5, iou_cross=0.2):
    """按来源切片区分的类内去重。raw 每项: [x1,y1,x2,y2,conf,cls,tile_id]。

    重复检出只可能来自**不同**切片 —— 同一片内 NMS-free 头已做一对一匹配，
    片内两个 IoU 0.2~0.5 的框是两颗真卵（实测真值里有中心距 5px 的卵对）。
    所以跨片用低阈值 0.2 合并，同片用 0.5 只兜底。
    实测微调模型在 3 张测试图上有 19 对跨片重复框 IoU 落在 0.24~0.50，
    统一 IoU 0.5 的 NMS 一个都合不掉，计数虚高 25%。
    """
    kept = []
    for c in sorted({q[5] for q in raw}):
        cand = sorted((q for q in raw if q[5] == c), key=lambda q: -q[4])
        keep_c = []
        for q in cand:
            dup = False
            for k in keep_c:
                thr = iou_same if q[6] == k[6] else iou_cross
                if _iou(q, k) > thr:
                    dup = True; break
            if not dup:
                keep_c.append(q)
        kept += keep_c
    return kept


def detect(model, img, args):
    """蚊卵走切片、蚊子走整图；各自只保留本路负责的那个类。"""
    H, W = img.shape[:2]
    raw = []

    # --- 蚊卵路: 切片 ---
    ref = args.ref_width
    tile = max(160, int(round(args.egg_tile * W / ref / 32)) * 32)
    stride = max(1, int(tile * (1 - args.overlap)))
    n_tile = 0
    for y in starts(H, tile, stride):
        for x in starts(W, tile, stride):
            n_tile += 1
            tid = n_tile
            r = model.predict(img[y:y + tile, x:x + tile], imgsz=args.egg_imgsz,
                              conf=args.conf, max_det=2000, verbose=False)[0]
            if r.boxes is None:
                continue
            for b, s, c in zip(r.boxes.xyxy.cpu().numpy(), r.boxes.conf.cpu().numpy(),
                               r.boxes.cls.cpu().numpy()):
                if int(c) != 1:
                    continue                       # 这一路只认卵
                raw.append([b[0] + x, b[1] + y, b[2] + x, b[3] + y, float(s), 1, tid])
    n_egg_raw = len(raw)

    # --- 蚊子路: 整图，多尺度 ---
    # 单一 imgsz 推不出来: 蚊子在两台设备里占画幅的比例不同(相机 12.3% / 手机 7.3%)，
    # 544 是按相机标的，用在手机照上蚊子只剩 40px(训练尺度 66px)，实测直接漏检。
    # 图宽推不出正确尺度(那取决于放大倍率，不是分辨率)，所以跑几个尺度再让 NMS 合并。
    for si, sz in enumerate(args.mos_imgsz):
        r = model.predict(img, imgsz=sz, conf=args.conf, max_det=300, verbose=False)[0]
        if r.boxes is None:
            continue
        for b, s, c in zip(r.boxes.xyxy.cpu().numpy(), r.boxes.conf.cpu().numpy(),
                           r.boxes.cls.cpu().numpy()):
            if int(c) != 0:
                continue                           # 这一路只认蚊子
            raw.append([b[0], b[1], b[2], b[3], float(s), 0, -1 - si])

    return nms_per_class(raw, args.conf), n_egg_raw, tile, n_tile


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("weights")
    ap.add_argument("sources", nargs="+", help="图片或目录")
    ap.add_argument("--out", default="")
    ap.add_argument("--conf", type=float, default=0.25)
    ap.add_argument("--egg-tile", type=int, default=448, help="以 --ref-width 为基准的切片边长")
    ap.add_argument("--ref-width", type=int, default=3264)
    ap.add_argument("--egg-imgsz", type=int, default=640)
    ap.add_argument("--mos-imgsz", default="544,768,960",
                    type=lambda v: [int(x) for x in v.split(",")],
                    help="蚊子路的多个整图尺度，逗号分隔")
    ap.add_argument("--overlap", type=float, default=0.25)
    ap.add_argument("--clean-bg", default="", help="纯背景清单：这些图上的任何框都是误检")
    a = ap.parse_args()

    files = []
    for s in a.sources:
        p = Path(s)
        files += sorted(q for q in p.iterdir()
                        if q.suffix.lower() in (".jpg", ".png")) if p.is_dir() else [p]
    clean = set()
    if a.clean_bg and Path(a.clean_bg).exists():
        clean = {Path(l).stem for l in Path(a.clean_bg).read_text(encoding="utf-8").splitlines()
                 if l.strip()}

    model = YOLO(a.weights)
    out = Path(a.out) if a.out else None
    if out:
        out.mkdir(parents=True, exist_ok=True)

    rows, tot_m, tot_e = [], 0, 0
    fp_imgs = fp_boxes = 0
    for p in files:
        img = rd(p)
        if img is None:
            print(f"读取失败 {p.name}"); continue
        det, n_raw, tile, n_tile = detect(model, img, a)
        nm = sum(1 for d in det if d[5] == 0)
        ne = sum(1 for d in det if d[5] == 1)
        tot_m += nm; tot_e += ne
        mark = ""
        if p.stem in clean:
            fp_imgs += 1; fp_boxes += nm + ne
            mark = "  <纯背景，此图任何框都是误检>"
        if out:
            im = img.copy()
            sc = max(img.shape[1] / 1600, 0.6)
            for x1, y1, x2, y2, s, c, *_ in det:
                cv2.rectangle(im, (int(x1), int(y1)), (int(x2), int(y2)), COLOR[c], max(2, int(3 * sc)))
                cv2.putText(im, f"{NAMES[c]} {s:.2f}", (int(x1), max(int(24*sc), int(y1) - int(6*sc))),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6 * sc, COLOR[c], max(1, int(2 * sc)))
            cv2.putText(im, f"mosquito {nm}   egg {ne}", (int(20*sc), int(52*sc)),
                        cv2.FONT_HERSHEY_SIMPLEX, 1.3 * sc, (0, 0, 255), max(2, int(3*sc)))
            wr(out / f"{p.stem}.jpg", im)
        rows.append([p.name, f"{img.shape[1]}x{img.shape[0]}", tile, n_tile, nm, ne, n_raw, len(det)])
        print(f"{p.name:26s} 蚊子 {nm:4d}  蚊卵 {ne:5d}   (卵切片检出 {n_raw} -> 去重 {ne}){mark}")

    print("\n" + "=" * 60)
    print(f"共 {len(rows)} 张    蚊子总计 {tot_m}    蚊卵总计 {tot_e}")
    if clean:
        n = max(fp_imgs, 1)
        print(f"纯背景图 {fp_imgs} 张 -> 误检 {fp_boxes} 个（{fp_boxes/n:.1f} 个/图）  ← 这一项不需要标注即可测量")
    print("=" * 60)
    if out:
        with open(out / "_计数.csv", "w", newline="", encoding="utf-8-sig") as f:
            w = csv.writer(f)
            w.writerow(["文件", "尺寸", "卵切片边长", "切片数", "蚊子", "蚊卵", "卵去重前", "去重后总框"])
            w.writerows(rows)
            w.writerow(["合计", "", "", "", tot_m, tot_e, "", ""])
        print(f"结果 -> {out}")


if __name__ == "__main__":
    main()
