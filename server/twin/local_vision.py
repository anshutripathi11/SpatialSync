"""
Open-source, offline replacement for the Claude vision step. Same contract as ClaudeObserver:
    observe(photo: PIL.Image, spans) -> {"walls": [ {wall_id, exists, color_hex, material, features, confidence} ]}

Two models:
  1. Semantic segmentation trained on ADE20K (default SegFormer-B2, ~100 MB, any HF ADE20K model works).
     Every pixel gets a class: wall, door, windowpane, painting, ceiling, floor, chair, person...
     Per wall strip we measure:
        exists      = enough wall/door/window surface between ceiling and floor
        openings    = connected door / window regions -> bounding boxes
        colour      = median RGB of the pure 'wall' pixels (no model needed)
        material    = texture heuristic (smooth -> painted, strong regular texture -> brick/tile, glassy...)
        confidence  = how much of the strip is actually visible wall (furniture/people lower it) x model certainty
  2. Open-vocabulary detector YOLOE (default) or YOLO-World (you type the class names, no training) for things on walls:
     fire extinguisher, exit sign, whiteboard, TV, outlet... Kept only if they sit on wall pixels.

HybridObserver runs this first and only sends low-confidence walls to Claude (if an API key is set).
"""
from __future__ import annotations

import os
from functools import lru_cache

import numpy as np
from PIL import Image

from .visibility import VisibleSpan

SEG_MODEL = os.environ.get("FLOORTWIN_SEG_MODEL", "nvidia/segformer-b2-finetuned-ade-512-512")
DET_MODEL = os.environ.get("FLOORTWIN_DET_MODEL", "yoloe-11s-seg.pt")  # or yolov8s-worldv2.pt
WORK_SIZE = int(os.environ.get("FLOORTWIN_WORK_SIZE", "640"))   # longest side used for inference

# ADE20K class names (as they appear in model.config.id2label) grouped by role.
WALL = {"wall"}
DOOR = {"door", "double door", "screen door"}
WINDOW = {"windowpane", "window"}
ON_WALL = {  # things mounted on a wall: count as wall surface, and become features
    "painting": ("artwork", "painting"), "picture": ("artwork", "picture"),
    "poster": ("sign", "poster"), "signboard": ("sign", "sign"), "sign": ("sign", "sign"),
    "bulletin board": ("whiteboard", "notice board"), "mirror": ("other", "mirror"),
    "clock": ("other", "clock"), "sconce": ("other", "wall light"), "radiator": ("other", "radiator"),
    "television receiver": ("screen", "TV"), "crt screen": ("screen", "screen"), "screen": ("screen", "screen"),
    "blind": ("other", "blind"), "curtain": ("other", "curtain"),
}
NOT_VERTICAL = {"ceiling", "floor", "sky", "rug", "light", "chandelier", "stairs", "step", "stairway"}

DETECT_CLASSES = {  # prompt text -> feature type
    "fire extinguisher": "fire_extinguisher", "exit sign": "sign", "sign": "sign", "whiteboard": "whiteboard",
    "television": "screen", "computer monitor": "screen", "light switch": "outlet_or_switch",
    "power outlet": "outlet_or_switch", "fire alarm": "other", "first aid kit": "other",
    "air vent": "vent", "framed picture": "artwork", "door": "door", "window": "window",
}


def _device():
    import torch
    if torch.cuda.is_available():
        return "cuda"
    if getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
        return "mps"
    return "cpu"


# ------------------------------------------------------------------------------ segmentation
class Segmenter:
    def __init__(self, model_id: str = SEG_MODEL):
        import torch
        from transformers import AutoImageProcessor, AutoModelForSemanticSegmentation

        self.torch = torch
        self.device = _device()
        self.proc = AutoImageProcessor.from_pretrained(model_id)
        self.model = AutoModelForSemanticSegmentation.from_pretrained(model_id).to(self.device).eval()
        names = {int(i): str(n).lower() for i, n in self.model.config.id2label.items()}

        def ids(group):
            return [i for i, n in names.items() if any(part.strip() in group for part in n.replace(",", ";").split(";"))]

        self.wall_ids = ids(WALL)
        self.door_ids = ids(DOOR)
        self.window_ids = ids(WINDOW)
        self.not_vertical_ids = ids(NOT_VERTICAL)
        self.on_wall = {}  # class id -> (feature type, label)
        for i, n in names.items():
            for part in n.replace(",", ";").split(";"):
                if part.strip() in ON_WALL:
                    self.on_wall[i] = ON_WALL[part.strip()]
        if not self.wall_ids:
            raise ValueError(f"{model_id} has no 'wall' class: use an ADE20K-trained model")

    def __call__(self, img: Image.Image):
        """Returns (labels HxW int, confidence HxW float) at img's resolution."""
        torch = self.torch
        inputs = self.proc(images=img, return_tensors="pt").to(self.device)
        with torch.no_grad():
            logits = self.model(**inputs).logits
        logits = torch.nn.functional.interpolate(logits, size=(img.height, img.width), mode="bilinear", align_corners=False)
        prob = logits.softmax(1)[0]
        conf, labels = prob.max(0)
        return labels.cpu().numpy().astype(np.int32), conf.cpu().numpy().astype(np.float32)


# ------------------------------------------------------------------------------ detection
class WallObjectDetector:
    def __init__(self, model: str = DET_MODEL, classes=None):
        self.names = list(classes or DETECT_CLASSES.keys())
        if "yoloe" in os.path.basename(model).lower():
            # YOLOE: open-vocabulary, text encoder (MobileCLIP) downloads from GitHub like the weights
            from ultralytics import YOLOE
            self.model = YOLOE(model)
            self.model.set_classes(self.names, self.model.get_text_pe(self.names))
        else:
            from ultralytics import YOLOWorld
            self.model = YOLOWorld(model)
            self.model.set_classes(self.names)
        self.device = _device()

    def __call__(self, img: Image.Image, conf: float = 0.15):
        """-> list of (prompt_name, score, (x0, y0, x1, y1) pixel box)."""
        res = self.model.predict(img, conf=conf, verbose=False, device=self.device)[0]
        out = []
        for b in res.boxes:
            cls = int(b.cls[0])
            out.append((self.names[cls], float(b.conf[0]), tuple(float(v) for v in b.xyxy[0].tolist())))
        return out


@lru_cache(maxsize=1)
def _segmenter():
    return Segmenter()


@lru_cache(maxsize=1)
def _detector():
    try:
        return WallObjectDetector()
    except Exception as e:  # detector is optional: segmentation alone still updates walls
        print(f"[twin] object detector disabled: {type(e).__name__}: {e}")
        if "clip" in str(e).lower():
            print('[twin]   fix: pip install "clip @ https://github.com/ultralytics/CLIP/archive/refs/heads/main.zip"')
        return None


# ------------------------------------------------------------------------------ per-strip analysis
def _components(mask: np.ndarray, min_area: int):
    import cv2
    n, _, stats, _ = cv2.connectedComponentsWithStats(mask.astype(np.uint8), connectivity=8)
    return [tuple(stats[i][:4]) for i in range(1, n) if stats[i][4] >= min_area]


def _material(rgb_pixels: np.ndarray, gray_strip: np.ndarray, wall_mask: np.ndarray, window_frac: float) -> str:
    """Conservative texture heuristic, calibrated on real photos: plain walls ~1, panelling/stone 2-3, brick 4+."""
    import cv2
    if window_frac > 0.6:
        return "glass"                      # mostly window/glass -> treat as a glass partition
    inner = cv2.erode(wall_mask.astype(np.uint8), np.ones((7, 7), np.uint8)).astype(bool)  # skip edges
    if rgb_pixels.shape[0] < 50 or inner.sum() < 50:
        return "painted"
    g = cv2.GaussianBlur(gray_strip, (5, 5), 0).astype(np.float32)
    texture = float(np.median(np.abs(cv2.Laplacian(g, cv2.CV_32F))[inner]))
    r, gr, b = np.median(rgb_pixels, axis=0)
    if texture >= 4 and r > gr * 1.15 and r > b * 1.3:
        return "brick"
    if texture >= 4:
        return "tile"
    if texture >= 2.5:
        return "other"                      # panelled / stone / wallpaper
    return "painted"


def _dedupe(feats: list[dict]) -> list[dict]:
    """A glass door is often labelled both door and window: keep the door, mark it glass."""
    def iou(a, b):
        ix = max(0, min(a["x_end"], b["x_end"]) - max(a["x_start"], b["x_start"]))
        iy = max(0, min(a["y_bottom"], b["y_bottom"]) - max(a["y_top"], b["y_top"]))
        inter = ix * iy
        area = lambda f: (f["x_end"] - f["x_start"]) * (f["y_bottom"] - f["y_top"])
        return inter / max(min(area(a), area(b)), 1e-9)
    doors = [f for f in feats if f["type"] == "door"]
    out = []
    for f in feats:
        if f["type"] == "window":
            hit = next((d for d in doors if iou(f, d) > 0.5), None)
            if hit is not None:
                hit["state"] = "glass"
                continue
        out.append(f)
    return out


class LocalObserver:
    """Segmentation (+ optional detector). No API key, runs offline after the first model download."""

    def __init__(self, use_detector: bool = True, min_strip_px: int = 6):
        self.seg = _segmenter()
        self.det = _detector() if use_detector else None
        self.min_strip_px = min_strip_px
        self.last_debug = None   # (labels, conf) of the last photo, for visual checks

    def observe(self, photo: Image.Image, spans: list[VisibleSpan]) -> dict:
        if not spans:
            return {"walls": []}
        img = photo.convert("RGB")
        img.thumbnail((WORK_SIZE, WORK_SIZE))
        W, H = img.size
        rgb = np.asarray(img)
        gray = np.asarray(img.convert("L"))
        labels, conf = self.seg(img)
        self.last_debug = (labels, conf)
        S = self.seg

        is_wall = np.isin(labels, S.wall_ids)
        is_door = np.isin(labels, S.door_ids)
        is_window = np.isin(labels, S.window_ids)
        is_on_wall = np.isin(labels, list(S.on_wall.keys())) if S.on_wall else np.zeros_like(is_wall)
        is_flat = np.isin(labels, S.not_vertical_ids)
        surface = is_wall | is_door | is_window | is_on_wall
        detections = self.det(img) if self.det else []

        merged: dict[str, dict] = {}
        for s in spans:
            x0, x1 = int(round(s.u0 * W)), int(round(s.u1 * W))
            if x1 - x0 < self.min_strip_px:
                continue
            sl = slice(x0, x1)
            vertical = ~is_flat[:, sl]
            n_vert = int(vertical.sum())
            if n_vert == 0:
                continue
            surf_frac = float(surface[:, sl].sum()) / n_vert
            occl_frac = 1.0 - surf_frac
            certainty = float(conf[:, sl][surface[:, sl]].mean()) if surface[:, sl].any() else float(conf[:, sl].mean())

            wall_px = rgb[:, sl][is_wall[:, sl]]
            color = "#%02X%02X%02X" % tuple(int(c) for c in np.median(wall_px, axis=0)) if wall_px.shape[0] > 30 else "#DDDDDD"
            window_frac = float(is_window[:, sl].sum()) / n_vert
            material = _material(wall_px, gray[:, sl], is_wall[:, sl], window_frac)

            # A wall is 'missing' only if we see neither wall surface nor things blocking the view.
            exists = not (surf_frac < 0.12 and occl_frac < 0.5)

            feats = []
            min_area = max(30, int(0.002 * W * H))
            for mask, ftype, label in ((is_door, "door", "door"), (is_window, "window", "window")):
                for (bx, by, bw, bh) in _components(mask[:, sl], min_area):
                    if ftype == "window" and by / H > 0.6:
                        continue  # 'window' near the floor = reflection in a glass table / floor
                    feats.append(dict(type=ftype, label=label,
                                      x_start=(x0 + bx) / W, x_end=(x0 + bx + bw) / W,
                                      y_top=by / H, y_bottom=(by + bh) / H, state=""))
            for cid, (ftype, label) in S.on_wall.items():
                for (bx, by, bw, bh) in _components(labels[:, sl] == cid, min_area):
                    feats.append(dict(type=ftype, label=label,
                                      x_start=(x0 + bx) / W, x_end=(x0 + bx + bw) / W,
                                      y_top=by / H, y_bottom=(by + bh) / H, state=""))
            for name, score, (dx0, dy0, dx1, dy1) in detections:
                cx = (dx0 + dx1) / 2
                if not (x0 <= cx < x1):
                    continue
                ftype = DETECT_CLASSES.get(name, "other")
                if ftype in ("door", "window") and any(f["type"] == ftype for f in feats):
                    continue  # segmentation already has it
                bx0, bx1 = int(max(dx0, 0)), int(min(dx1, W)); by0, by1 = int(max(dy0, 0)), int(min(dy1, H))
                region = surface[by0:by1, bx0:bx1]
                if region.size and region.mean() < 0.2 and ftype not in ("door", "window"):
                    continue  # not on a wall (e.g. a TV on a table in the middle of the room)
                feats.append(dict(type=ftype, label=f"{name} ({score:.2f})",
                                  x_start=max(dx0, x0) / W, x_end=min(dx1, x1) / W,
                                  y_top=dy0 / H, y_bottom=dy1 / H, state=""))

            feats = _dedupe(feats)
            obs = dict(wall_id=s.wall_id, exists=exists, color_hex=color, material=material,
                       features=feats, confidence=round(min(1.0, surf_frac) * certainty, 3),
                       notes=f"local: {surf_frac:.0%} visible surface, {occl_frac:.0%} occluded")
            # a wall split into several spans (partly hidden): keep the most informative one
            prev = merged.get(s.wall_id)
            if prev is None or obs["confidence"] > prev["confidence"]:
                if prev is not None:
                    obs["features"] = prev["features"] + obs["features"]
                merged[s.wall_id] = obs
            else:
                prev["features"] += feats
        return {"walls": list(merged.values())}


class HybridObserver:
    """Local models for every wall; Claude re-checks only walls the local models are unsure about."""

    def __init__(self, threshold: float = float(os.environ.get("FLOORTWIN_HYBRID_THRESHOLD", "0.45"))):
        self.local = LocalObserver()
        self.threshold = threshold
        self.claude = None
        if os.environ.get("ANTHROPIC_API_KEY"):
            from .vision import ClaudeObserver
            self.claude = ClaudeObserver()

    def observe(self, photo: Image.Image, spans: list[VisibleSpan]) -> dict:
        out = self.local.observe(photo, spans)
        if self.claude is None:
            return out
        unsure = {w["wall_id"] for w in out["walls"] if w["confidence"] < self.threshold}
        seen = {w["wall_id"] for w in out["walls"]}
        unsure |= {s.wall_id for s in spans} - seen
        if not unsure:
            return out
        try:
            remote = self.claude.observe(photo, [s for s in spans if s.wall_id in unsure])
        except Exception as e:
            print(f"[twin] Claude fallback failed, keeping local result: {e}")
            return out
        by_id = {w["wall_id"]: w for w in out["walls"]}
        for w in remote.get("walls", []):
            if w.get("wall_id") in unsure:
                w["notes"] = (w.get("notes", "") + " [claude fallback]").strip()
                by_id[w["wall_id"]] = w
        return {"walls": list(by_id.values())}


def make_observer(kind: str | None = None):
    """FLOORTWIN_OBSERVER = local | hybrid | claude | mock (default: hybrid if an API key is set, else local)."""
    kind = (kind or os.environ.get("FLOORTWIN_OBSERVER")
            or ("mock" if os.environ.get("FLOORTWIN_MOCK") else None)
            or ("hybrid" if os.environ.get("ANTHROPIC_API_KEY") else "local")).lower()
    if kind == "mock":
        from .vision import MockObserver
        return MockObserver()
    if kind == "claude":
        from .vision import ClaudeObserver
        return ClaudeObserver()
    if kind == "hybrid":
        return HybridObserver()
    if kind == "local":
        return LocalObserver()
    raise ValueError(f"unknown observer '{kind}'")
