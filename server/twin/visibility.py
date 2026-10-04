"""
Which walls does a photo actually see, and which part of the photo shows which part of each wall?

2D ray casting on the floor plan:
  * One ray per photo column, spaced like a real pinhole camera (uniform in tan, not in angle):
        angle(u) = heading + atan((2u - 1) * tan(hFOV / 2))       u = 0 (left edge) .. 1 (right edge)
  * Each ray stops at the NEAREST wall, so walls hidden behind other walls are never touched.
  * Consecutive columns that hit the same wall form a VisibleSpan: photo columns [u0, u1] <-> wall
    positions [t0, t1] (t = 0 at wall.a, 1 at wall.b). This mapping is what lets us
      - tell the vision model where each wall is in the photo,
      - convert "door at 40 % across the photo" into "door 1.8 m along wall W012",
      - and project the photo onto only that piece of wall in Unity.

Coordinates: image pixels, top-left origin, heading clockwise from image-up (same as the app).
"""
from __future__ import annotations

import math
from dataclasses import dataclass, asdict

from .walls import Wall


@dataclass
class CameraPose:
    x: float                 # image px
    y: float                 # image px
    heading_deg: float       # clockwise from image-up
    hfov_deg: float
    vfov_deg: float = 60.0
    pitch_deg: float = 0.0   # + = looking up
    height_m: float = 1.4    # camera height above floor
    photo_w: int = 1080
    photo_h: int = 1920


@dataclass
class VisibleSpan:
    wall_id: str
    u0: float                # photo column fraction where this wall starts (left)
    u1: float                # ... ends (right)
    t0: float                # wall parameter seen at u0
    t1: float                # wall parameter seen at u1
    dist0_m: float
    dist1_m: float
    pixels_per_meter: float  # photo resolution on the wall: higher = closer / more head-on = better
    columns: int

    def to_dict(self):
        return {k: (round(v, 4) if isinstance(v, float) else v) for k, v in asdict(self).items()}


def _ray_hit(px, py, dx, dy, w: Wall):
    ax, ay = w.a; bx, by = w.b
    vx, vy = bx - ax, by - ay
    den = dx * vy - dy * vx
    if abs(den) < 1e-12:
        return None
    wx, wy = ax - px, ay - py
    s = (wx * vy - wy * vx) / den          # distance along ray (px, since |d| = 1)
    t = (wx * dy - wy * dx) / den          # position along wall
    if s <= 1e-6 or t < 0.0 or t > 1.0:
        return None
    return s, t


def column_angle(pose: CameraPose, u: float) -> float:
    half = math.radians(pose.hfov_deg) / 2.0
    return math.radians(pose.heading_deg) + math.atan((2.0 * u - 1.0) * math.tan(half))


def cast_column(pose: CameraPose, u: float, walls: list[Wall], max_range_px: float):
    a = column_angle(pose, u)
    dx, dy = math.sin(a), -math.cos(a)     # image y points DOWN, heading 0 = image-up
    best = None
    for w in walls:
        h = _ray_hit(pose.x, pose.y, dx, dy, w)
        if h and h[0] <= max_range_px and (best is None or h[0] < best[1]):
            best = (w, h[0], h[1])
    return best


def visible_spans(pose: CameraPose, walls: list[Wall], px_per_m: float,
                  columns: int = 240, max_range_m: float = 20.0,
                  min_columns: int = 3) -> list[VisibleSpan]:
    max_range_px = max_range_m * px_per_m
    hits = [cast_column(pose, (i + 0.5) / columns, walls, max_range_px) for i in range(columns)]

    spans: list[VisibleSpan] = []
    i = 0
    while i < columns:
        if hits[i] is None:
            i += 1
            continue
        w = hits[i][0]
        j = i
        while j + 1 < columns and hits[j + 1] is not None and hits[j + 1][0] is w:
            j += 1
        n = j - i + 1
        if n >= min_columns:
            u0, u1 = i / columns, (j + 1) / columns
            t0, t1 = hits[i][2], hits[j][2]
            wall_len_m = w.length / px_per_m
            seen_m = max(abs(t1 - t0) * wall_len_m, 1e-3)
            spans.append(VisibleSpan(
                wall_id=w.id, u0=u0, u1=u1, t0=t0, t1=t1,
                dist0_m=hits[i][1] / px_per_m, dist1_m=hits[j][1] / px_per_m,
                pixels_per_meter=(u1 - u0) * pose.photo_w / seen_m,
                columns=n))
        i = j + 1
    return spans


def u_to_t(pose: CameraPose, span: VisibleSpan, wall: Wall, u: float) -> float:
    """Exact photo-column -> wall-parameter conversion for a point inside a span."""
    a = column_angle(pose, min(max(u, span.u0), span.u1))
    h = _ray_hit(pose.x, pose.y, math.sin(a), -math.cos(a), wall)
    if h is None:  # numerical edge: fall back to linear interpolation
        k = (u - span.u0) / max(span.u1 - span.u0, 1e-9)
        return span.t0 + k * (span.t1 - span.t0)
    return h[1]
