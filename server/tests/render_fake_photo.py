"""
Tiny ray-casting renderer that produces a fake 'photo' from a pose on the synthetic plan, with a door
painted at a known place on one wall. Used to check that annotation bands line up with the walls and
that photo coordinates convert back to the right wall position and height.
"""
import math
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, ".")
from twin.visibility import CameraPose, cast_column  # noqa: E402

COLORS = {}


def render(pose: CameraPose, walls, px_per_m, door=None, wall_h=2.7):
    """door = (wall_id, t0, t1, bottom_m, top_m) painted dark brown."""
    W, H = pose.photo_w, pose.photo_h
    img = np.zeros((H, W, 3), np.uint8)
    img[: H // 2] = (200, 215, 230)   # ceiling
    img[H // 2:] = (120, 110, 100)    # floor
    tan_v = math.tan(math.radians(pose.vfov_deg) / 2)
    hd = math.radians(pose.heading_deg)
    rng = np.random.default_rng(1)
    for x in range(W):
        hit = cast_column(pose, (x + 0.5) / W, walls, 30 * px_per_m)
        if not hit:
            continue
        w, s_px, t = hit
        a = math.atan2(*(0, 0)) if False else None
        # perpendicular (optical-axis) distance
        ang = hd + math.atan((2 * (x + 0.5) / W - 1) * math.tan(math.radians(pose.hfov_deg) / 2))
        z = s_px / px_per_m * math.cos(ang - hd)

        def row(hm):
            return int((0.5 - 0.5 * (hm - pose.height_m) / (z * tan_v)) * H)

        col = COLORS.setdefault(w.id, tuple(int(c) for c in rng.integers(150, 245, 3)))
        top, bot = max(0, row(wall_h)), min(H, row(0.0))
        img[top:bot, x] = col
        if door and w.id == door[0] and door[1] <= t <= door[2]:
            img[max(0, row(door[4])):min(H, row(door[3])), x] = (90, 55, 30)
    return Image.fromarray(img)
