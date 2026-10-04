"""
Visual check of the local CV observer on real photos.
    python tests/check_local_vision.py photo1.jpg photo2.jpg ...
Pretends each photo shows 3 walls (left / middle / right thirds), runs LocalObserver, prints the JSON and
writes <photo>_check.jpg: wall pixels blue, doors green, windows yellow, wall objects magenta,
features as boxes, per-strip result in the header band.
"""
import json
import sys
import time

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, ".")
from twin.local_vision import LocalObserver, WORK_SIZE  # noqa: E402
from twin.visibility import VisibleSpan  # noqa: E402


def fake_spans():
    return [VisibleSpan(f"W{i}", i / 3, (i + 1) / 3, 0, 1, 4, 4, 100, 80) for i in range(3)]


def main(paths):
    t = time.time()
    obs = LocalObserver()
    print(f"models loaded in {time.time() - t:.1f}s (detector {'on' if obs.det else 'off'})")
    for path in paths:
        photo = Image.open(path).convert("RGB")
        t = time.time()
        result = obs.observe(photo, fake_spans())
        dt = time.time() - t
        print(f"\n== {path}  ({dt:.2f}s)")
        for w in result["walls"]:
            print(json.dumps({k: w[k] for k in ("wall_id", "exists", "color_hex", "material", "confidence", "notes")}),
                  [(f["type"], f["label"], round(f["x_start"], 2), round(f["x_end"], 2)) for f in w["features"]])

        img = photo.copy(); img.thumbnail((WORK_SIZE, WORK_SIZE))
        labels, _ = obs.last_debug
        S = obs.seg
        over = np.zeros((*labels.shape, 4), np.uint8)
        over[np.isin(labels, S.wall_ids)] = (40, 90, 255, 90)
        over[np.isin(labels, S.door_ids)] = (40, 220, 60, 140)
        over[np.isin(labels, S.window_ids)] = (255, 220, 0, 140)
        over[np.isin(labels, list(S.on_wall))] = (230, 40, 230, 140)
        out = Image.alpha_composite(img.convert("RGBA"), Image.fromarray(over, "RGBA")).convert("RGB")
        d = ImageDraw.Draw(out)
        W, H = out.size
        for i, w in enumerate(result["walls"]):
            x0 = int(i * W / 3)
            d.line([x0, 0, x0, H], fill="white", width=2)
            d.rectangle([x0, 0, x0 + W // 3, 34], fill=w["color_hex"])
            d.text((x0 + 4, 2), f'{w["wall_id"]} exists={w["exists"]} {w["material"]}', fill="black")
            d.text((x0 + 4, 18), f'conf {w["confidence"]:.2f}', fill="black")
            for f in w["features"]:
                d.rectangle([f["x_start"] * W, f["y_top"] * H, f["x_end"] * W, f["y_bottom"] * H], outline="red", width=2)
                d.text((f["x_start"] * W + 3, f["y_top"] * H + 3), f["type"], fill="red")
        out.save(path.rsplit(".", 1)[0] + "_check.jpg")


if __name__ == "__main__":
    main(sys.argv[1:])
