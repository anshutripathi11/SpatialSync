"""
Floor plan image -> wall segments.

Output units are SOURCE-IMAGE PIXELS with a top-left origin, the same convention BlueprintMap and
PinRecord.imagePixel use, so everything lines up without conversion.

Approach (robust for typical architectural plans, no ML needed):
  1. Binarise: walls are the dark, THICK strokes.
  2. Morphological opening with a kernel of ~min wall thickness removes text, dimension lines,
     door swing arcs and furniture, which are all thin.
  3. Manhattan walls: open the mask again with long horizontal / vertical line kernels; every connected
     component becomes one wall (centre line + thickness).
  4. Whatever is left (diagonal walls) goes through skeleton + probabilistic Hough.
Door gaps stay gaps, so doorways are already openings in the 3D model.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, asdict

import cv2
import numpy as np


@dataclass
class Wall:
    id: str
    a: tuple[float, float]       # endpoint, image px (top-left origin)
    b: tuple[float, float]
    thickness: float             # image px

    @property
    def length(self) -> float:
        return math.dist(self.a, self.b)

    def to_dict(self) -> dict:
        d = asdict(self)
        d["a"] = [round(self.a[0], 2), round(self.a[1], 2)]
        d["b"] = [round(self.b[0], 2), round(self.b[1], 2)]
        d["thickness"] = round(self.thickness, 2)
        return d


def read_image(path: str) -> np.ndarray:
    """cv2.imread that works with any Windows path and explains what went wrong."""
    import os
    if not os.path.isfile(path):
        folder = os.path.dirname(os.path.abspath(path)) or "."
        imgs = [f for f in os.listdir(folder) if f.lower().endswith((".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"))]
        hint = (f"Images in {folder}: {', '.join(imgs)}" if imgs else
                f"There are no images in {folder}. Copy your floor plan there or pass its full path.")
        raise SystemExit(f"\nFloor plan not found: {path}\n{hint}\n")
    data = np.fromfile(path, dtype=np.uint8)
    img = cv2.imdecode(data, cv2.IMREAD_COLOR)
    if img is None:
        raise SystemExit(f"\nCould not decode {path}. Use a PNG or JPG (PDF plans: export a page as PNG first).\n")
    return img


def wall_mask(img_bgr: np.ndarray, min_thickness_px: int = 5) -> np.ndarray:
    gray = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY) if img_bgr.ndim == 3 else img_bgr
    gray = cv2.GaussianBlur(gray, (3, 3), 0)
    _, mask = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU)
    k = max(2, int(min_thickness_px))
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (k, k))
    return cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel)


def _components_to_walls(mask: np.ndarray, horizontal: bool, min_len: int, prefix: str) -> list[Wall]:
    walls = []
    n, _, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    for i in range(1, n):
        x, y, w, h, _ = stats[i]
        if horizontal and w >= min_len:
            cy = y + h / 2.0
            walls.append(Wall(f"{prefix}{len(walls)}", (float(x), cy), (float(x + w), cy), float(h)))
        elif not horizontal and h >= min_len:
            cx = x + w / 2.0
            walls.append(Wall(f"{prefix}{len(walls)}", (cx, float(y)), (cx, float(y + h)), float(w)))
    return walls


def _diagonal_walls(residual: np.ndarray, min_len: int, thickness: float) -> list[Wall]:
    from skimage.morphology import skeletonize

    if cv2.countNonZero(residual) < min_len * 2:
        return []
    skel = skeletonize(residual > 0).astype(np.uint8) * 255
    lines = cv2.HoughLinesP(skel, 1, np.pi / 180, threshold=max(10, min_len // 2),
                            minLineLength=min_len, maxLineGap=4)
    walls = []
    if lines is None:
        return walls
    for x1, y1, x2, y2 in np.asarray(lines).reshape(-1, 4):
        ang = abs(math.degrees(math.atan2(y2 - y1, x2 - x1))) % 180
        if min(ang, 180 - ang) < 8 or abs(ang - 90) < 8:
            continue  # near-axis leftovers are already covered by the Manhattan pass
        walls.append(Wall(f"d{len(walls)}", (float(x1), float(y1)), (float(x2), float(y2)), thickness))
    return _merge_collinear(walls)


def _merge_collinear(walls: list[Wall], ang_tol=4.0, dist_tol=4.0, gap_tol=6.0) -> list[Wall]:
    walls = list(walls)
    merged = True
    while merged:
        merged = False
        for i in range(len(walls)):
            for j in range(i + 1, len(walls)):
                w1, w2 = walls[i], walls[j]
                d1 = np.subtract(w1.b, w1.a); d2 = np.subtract(w2.b, w2.a)
                a1 = math.degrees(math.atan2(d1[1], d1[0])) % 180
                a2 = math.degrees(math.atan2(d2[1], d2[0])) % 180
                if min(abs(a1 - a2), 180 - abs(a1 - a2)) > ang_tol:
                    continue
                u = d1 / (np.linalg.norm(d1) + 1e-9)
                nrm = np.array([-u[1], u[0]])
                if abs(np.dot(np.subtract(w2.a, w1.a), nrm)) > dist_tol:
                    continue
                ts = [0.0, float(np.linalg.norm(d1)), float(np.dot(np.subtract(w2.a, w1.a), u)),
                      float(np.dot(np.subtract(w2.b, w1.a), u))]
                lo1, hi1 = 0.0, ts[1]
                lo2, hi2 = sorted(ts[2:])
                if lo2 > hi1 + gap_tol or lo1 > hi2 + gap_tol:
                    continue
                lo, hi = min(ts), max(ts)
                a = tuple(np.add(w1.a, u * lo)); b = tuple(np.add(w1.a, u * hi))
                walls[i] = Wall(w1.id, (float(a[0]), float(a[1])), (float(b[0]), float(b[1])),
                                max(w1.thickness, w2.thickness))
                walls.pop(j)
                merged = True
                break
            if merged:
                break
    return walls


def _ray_segment(p, u, a, b):
    """Distance along ray p+s*u to segment ab, or None. Parallel/collinear -> None."""
    v = np.subtract(b, a)
    den = u[0] * v[1] - u[1] * v[0]
    if abs(den) < 1e-9:
        return None
    w = np.subtract(a, p)
    s = (w[0] * v[1] - w[1] * v[0]) / den
    t = (w[0] * u[1] - w[1] * u[0]) / den
    return s if (s >= -1e-6 and -0.02 <= t <= 1.02) else None


def _snap_junctions(walls: list[Wall], reach: float = 2.5, diagonal_reach: float = 5.0) -> list[Wall]:
    """
    Close corners/T-junctions: extend each wall END forward until it meets another wall's centre line,
    if that is within reach*thickness. Free ends (door gaps between collinear walls) never snap because a
    ray never 'hits' a collinear segment, so doorways stay open.
    """
    out = []
    for w in walls:
        ends = [np.array(w.a, float), np.array(w.b, float)]
        for k in (0, 1):
            p, q = ends[k], ends[1 - k]
            u = (p - q) / (np.linalg.norm(p - q) + 1e-9)  # outward direction
            best = None
            for o in walls:
                if o is w:
                    continue
                s = _ray_segment(p, u, o.a, o.b)
                r = diagonal_reach if w.id.startswith("d") else reach  # Hough trims diagonal ends more
                if s is not None and s <= r * max(w.thickness, o.thickness) and (best is None or s < best):
                    best = s
            if best is not None:
                ends[k] = p + u * best
        out.append(Wall(w.id, (float(ends[0][0]), float(ends[0][1])), (float(ends[1][0]), float(ends[1][1])), w.thickness))
    return out


def extract_walls(img_bgr: np.ndarray, min_thickness_px: int = 5, min_length_px: int = 20) -> list[Wall]:
    mask = wall_mask(img_bgr, min_thickness_px)

    h_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (min_length_px, 1))
    v_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (1, min_length_px))
    h_mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, h_kernel)
    v_mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, v_kernel)

    # A horizontal component must be wider than tall; strip vertical walls' pixels out of it first.
    h_only = cv2.subtract(h_mask, v_mask)
    v_only = cv2.subtract(v_mask, h_mask)
    walls = _components_to_walls(h_only, True, min_length_px, "h")
    walls += _components_to_walls(v_only, False, min_length_px, "v")

    covered = cv2.bitwise_or(h_mask, v_mask)
    residual = cv2.subtract(mask, cv2.dilate(covered, np.ones((3, 3), np.uint8)))
    median_t = float(np.median([w.thickness for w in walls])) if walls else float(min_thickness_px * 2)
    walls += _diagonal_walls(residual, min_length_px, median_t)

    # Re-join pieces that junction cleanup split by less than a wall thickness (not doorways).
    walls = _merge_collinear(walls, gap_tol=median_t * 1.2)
    walls = _snap_junctions(walls)
    for i, w in enumerate(walls):
        w.id = f"W{i:03d}"
    return walls


def estimate_pixels_per_meter(walls: list[Wall], door_width_m: float = 0.9) -> tuple[float | None, int]:
    """
    Auto-scale: doorways appear as gaps between collinear wall pieces. The most common gap width is
    almost always a standard interior door (~0.9 m clear incl. frame). Returns (px_per_m, votes).
    """
    gaps = []
    for i, w1 in enumerate(walls):
        for w2 in walls[i + 1:]:
            for horizontal in (True, False):
                k, o = (0, 1) if horizontal else (1, 0)
                if not (abs(w1.a[o] - w1.b[o]) < 1 and abs(w2.a[o] - w2.b[o]) < 1):
                    continue
                if abs(w1.a[o] - w2.a[o]) > max(w1.thickness, w2.thickness) * 0.5:
                    continue
                lo1, hi1 = sorted((w1.a[k], w1.b[k])); lo2, hi2 = sorted((w2.a[k], w2.b[k]))
                gap = lo2 - hi1 if lo2 > hi1 else lo1 - hi2
                if 1.5 * max(w1.thickness, w2.thickness) < gap < 30 * max(w1.thickness, w2.thickness):
                    gaps.append(gap)
    if not gaps:
        return None, 0
    hist, edges = np.histogram(gaps, bins=max(5, len(gaps)))
    best = int(np.argmax(hist))
    sel = [g for g in gaps if edges[best] <= g <= edges[best + 1]]
    return float(np.median(sel)) / door_width_m, len(sel)
