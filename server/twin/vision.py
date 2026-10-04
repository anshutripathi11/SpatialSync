"""
Photo + visible wall spans -> what each wall REALLY looks like.

1. annotate(): draw each visible span on the photo as a labelled, coloured band with boundary lines,
   so the model knows "the region between these two lines is wall W012".
2. ClaudeObserver.observe(): send original + annotated photo; force a tool call so the answer is JSON.
3. to_wall_observations(): convert the model's photo-fraction coordinates into wall coordinates:
     x (photo column)  -> t along the wall   via the exact ray geometry (visibility.u_to_t)
     y (photo row)     -> height in metres   via pitch, vertical FOV, camera height and the ray distance
   The model only says WHAT and WHERE IN THE IMAGE; metric placement comes from the AR pose.
"""
from __future__ import annotations

import base64
import io
import json
import math
import os
from dataclasses import dataclass, field, asdict

from PIL import Image, ImageDraw, ImageFont

from .visibility import CameraPose, VisibleSpan, u_to_t
from .walls import Wall

MODEL = os.environ.get("FLOORTWIN_MODEL", "claude-sonnet-5-5")
WALL_HEIGHT_M = float(os.environ.get("FLOORTWIN_WALL_HEIGHT", "2.7"))

PALETTE = [(230, 25, 75), (60, 180, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180),
           (70, 240, 240), (240, 50, 230), (210, 245, 60), (250, 190, 212), (0, 128, 128)]

FEATURE_TYPES = ["door", "window", "opening", "glass_partition", "sign", "fire_extinguisher",
                 "whiteboard", "screen", "artwork", "outlet_or_switch", "vent", "damage", "other"]

DEFAULT_HEIGHTS = {  # (bottom, top) metres when the model gives no usable y-range
    "door": (0.0, 2.1), "opening": (0.0, 2.3), "glass_partition": (0.0, WALL_HEIGHT_M),
    "window": (0.9, 2.1), "whiteboard": (0.9, 2.1), "screen": (1.0, 1.8), "artwork": (1.2, 1.8),
    "sign": (1.5, 2.0), "fire_extinguisher": (0.6, 1.2), "outlet_or_switch": (0.3, 1.2),
    "vent": (2.2, 2.6), "damage": (0.0, WALL_HEIGHT_M), "other": (0.5, 2.0),
}
CUTS_WALL = {"door", "opening", "window", "glass_partition"}  # these become holes in the 3D wall


# ---------------------------------------------------------------- data out
@dataclass
class Feature:
    type: str
    label: str
    t0: float            # along the wall, 0..1 (a -> b)
    t1: float
    bottom_m: float
    top_m: float
    cuts_wall: bool
    state: str = ""


@dataclass
class WallObservation:
    wall_id: str
    photo_id: str
    t0: float            # the part of the wall this photo saw
    t1: float
    exists: bool
    color_hex: str
    material: str
    features: list[Feature] = field(default_factory=list)
    notes: str = ""
    confidence: float = 0.5
    pixels_per_meter: float = 0.0

    def to_dict(self):
        return asdict(self)


# ---------------------------------------------------------------- annotation
def annotate(photo: Image.Image, spans: list[VisibleSpan]) -> Image.Image:
    img = photo.convert("RGB").copy()
    W, H = img.size
    overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)
    band = max(24, H // 22)
    try:
        font = ImageFont.load_default(size=max(16, band // 2))
    except TypeError:
        font = ImageFont.load_default()
    for i, s in enumerate(spans):
        c = PALETTE[i % len(PALETTE)]
        x0, x1 = int(s.u0 * W), int(s.u1 * W)
        d.rectangle([x0, 0, x1, band], fill=c + (200,))
        d.rectangle([x0, H - band, x1, H], fill=c + (200,))
        d.line([x0, 0, x0, H], fill=c + (255,), width=3)
        d.line([x1 - 1, 0, x1 - 1, H], fill=c + (255,), width=3)
        d.text(((x0 + x1) // 2, band // 2), s.wall_id, fill=(255, 255, 255, 255), font=font, anchor="mm")
    return Image.alpha_composite(img.convert("RGBA"), overlay).convert("RGB")


def _b64_jpeg(img: Image.Image, max_side: int = 1280) -> str:
    img = img.copy()
    img.thumbnail((max_side, max_side))
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=85)
    return base64.b64encode(buf.getvalue()).decode()


# ---------------------------------------------------------------- model call
REPORT_TOOL = {
    "name": "report_walls",
    "description": "Report what each labelled wall region in the photo actually looks like.",
    "input_schema": {
        "type": "object",
        "properties": {
            "walls": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "wall_id": {"type": "string"},
                        "exists": {"type": "boolean",
                                   "description": "False if there is no solid wall in this region (open space, removed wall)."},
                        "color_hex": {"type": "string", "description": "Dominant surface colour, e.g. #E8E4DC"},
                        "material": {"type": "string",
                                     "enum": ["painted", "brick", "concrete", "glass", "wood", "tile", "metal", "other"]},
                        "features": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "type": {"type": "string", "enum": FEATURE_TYPES},
                                    "label": {"type": "string", "description": "Short description, e.g. 'double wooden door'"},
                                    "x_start": {"type": "number", "description": "Left edge, fraction 0-1 of FULL photo width"},
                                    "x_end": {"type": "number", "description": "Right edge, fraction 0-1 of FULL photo width"},
                                    "y_top": {"type": "number", "description": "Top edge, fraction 0-1 of photo height (0 = top)"},
                                    "y_bottom": {"type": "number", "description": "Bottom edge, fraction 0-1 of photo height"},
                                    "state": {"type": "string", "description": "e.g. open, closed, broken; empty if n/a"},
                                },
                                "required": ["type", "label", "x_start", "x_end"],
                            },
                        },
                        "notes": {"type": "string"},
                        "confidence": {"type": "number", "description": "0-1"},
                    },
                    "required": ["wall_id", "exists", "color_hex", "material", "features", "confidence"],
                },
            }
        },
        "required": ["walls"],
    },
}


def _prompt(spans: list[VisibleSpan]) -> str:
    lines = [f"- {s.wall_id}: photo x from {s.u0:.2f} to {s.u1:.2f}, about {s.dist0_m:.1f}-{s.dist1_m:.1f} m away"
             for s in spans]
    return (
        "You are updating a 3D model of a building from a floor plan. The first image is a photo taken "
        "inside the building; the second is the same photo with coloured bands marking which floor-plan "
        "wall should be visible in each vertical strip (computed from the phone's tracked position and "
        "heading, so it can be off by a few percent of the width).\n\n"
        "Walls expected in view:\n" + "\n".join(lines) + "\n\n"
        "For EACH listed wall, look at its strip in the FIRST (clean) photo and report: whether a solid "
        "wall is really there, its dominant colour and material, and every door, window, opening, glass "
        "panel or notable object mounted on it, with its bounding box as fractions of the full photo. "
        "Only report features on that wall's surface, not furniture standing in front of it. "
        "If the strip is blocked by people or furniture so you cannot tell, give low confidence. "
        "Call report_walls exactly once."
    )


class ClaudeObserver:
    def __init__(self, model: str = MODEL):
        import anthropic
        self.client = anthropic.Anthropic()  # reads ANTHROPIC_API_KEY
        self.model = model

    def observe(self, photo: Image.Image, spans: list[VisibleSpan]) -> dict:
        if not spans:
            return {"walls": []}
        resp = self.client.messages.create(
            model=self.model,
            max_tokens=4096,
            tools=[REPORT_TOOL],
            tool_choice={"type": "tool", "name": "report_walls"},
            messages=[{
                "role": "user",
                "content": [
                    {"type": "image", "source": {"type": "base64", "media_type": "image/jpeg", "data": _b64_jpeg(photo)}},
                    {"type": "image", "source": {"type": "base64", "media_type": "image/jpeg", "data": _b64_jpeg(annotate(photo, spans))}},
                    {"type": "text", "text": _prompt(spans)},
                ],
            }],
        )
        for block in resp.content:
            if block.type == "tool_use":
                return block.input
        return {"walls": []}


class MockObserver:
    """Offline stand-in: every wall exists, light grey, one door in the middle of the widest span."""

    def observe(self, photo: Image.Image, spans: list[VisibleSpan]) -> dict:
        widest = max(spans, key=lambda s: s.u1 - s.u0, default=None)
        out = []
        for s in spans:
            feats = []
            if s is widest:
                mid = (s.u0 + s.u1) / 2
                feats.append({"type": "door", "label": "mock door", "x_start": mid - 0.05, "x_end": mid + 0.05,
                              "y_top": 0.35, "y_bottom": 0.75, "state": "closed"})
            out.append({"wall_id": s.wall_id, "exists": True, "color_hex": "#D9D6CF", "material": "painted",
                        "features": feats, "confidence": 0.6, "notes": "mock"})
        return {"walls": out}


# ---------------------------------------------------------------- photo coords -> wall coords
def _row_to_height(pose: CameraPose, y_frac: float, dist_m: float) -> float:
    """Photo row -> height above the floor at horizontal distance dist_m (pinhole, roll ignored)."""
    half_v = math.radians(pose.vfov_deg) / 2.0
    ang = math.radians(pose.pitch_deg) + math.atan((1.0 - 2.0 * y_frac) * math.tan(half_v))
    return pose.height_m + dist_m * math.tan(ang)


def _dist_at(pose: CameraPose, wall: Wall, t: float, px_per_m: float) -> float:
    px = wall.a[0] + t * (wall.b[0] - wall.a[0])
    py = wall.a[1] + t * (wall.b[1] - wall.a[1])
    # distance along the optical axis (what perspective scales with), not straight-line distance
    h = math.radians(pose.heading_deg)
    return max(0.05, ((px - pose.x) * math.sin(h) - (py - pose.y) * math.cos(h)) / px_per_m)


def to_wall_observations(raw: dict, pose: CameraPose, spans: list[VisibleSpan],
                         walls_by_id: dict[str, Wall], px_per_m: float, photo_id: str) -> list[WallObservation]:
    by_wall = {}
    for s in spans:  # a wall may appear in several spans (partly occluded); keep the widest for mapping
        if s.wall_id not in by_wall or (s.u1 - s.u0) > (by_wall[s.wall_id].u1 - by_wall[s.wall_id].u0):
            by_wall[s.wall_id] = s

    out = []
    for w in raw.get("walls", []):
        wid = w.get("wall_id")
        if wid not in by_wall or wid not in walls_by_id:
            continue  # model invented an id: ignore
        span, wall = by_wall[wid], walls_by_id[wid]
        feats = []
        for f in w.get("features", []):
            x0 = min(max(float(f.get("x_start", 0)), span.u0), span.u1)
            x1 = min(max(float(f.get("x_end", 0)), span.u0), span.u1)
            if x1 - x0 < 0.005:
                continue
            ta, tb = u_to_t(pose, span, wall, x0), u_to_t(pose, span, wall, x1)
            t0, t1 = min(ta, tb), max(ta, tb)
            ftype = f.get("type", "other")
            bottom, top = DEFAULT_HEIGHTS.get(ftype, DEFAULT_HEIGHTS["other"])
            if "y_top" in f and "y_bottom" in f:
                d = _dist_at(pose, wall, (t0 + t1) / 2, px_per_m)
                hb = _row_to_height(pose, float(f["y_bottom"]), d)
                ht = _row_to_height(pose, float(f["y_top"]), d)
                if 0 <= min(hb, ht) and max(hb, ht) <= WALL_HEIGHT_M + 0.5 and abs(ht - hb) > 0.1:
                    bottom, top = max(0.0, min(hb, ht)), min(WALL_HEIGHT_M, max(hb, ht))
                if ftype in ("door", "opening", "glass_partition"):
                    bottom = 0.0  # doors always reach the floor; trust geometry over a cropped photo
                if ftype == "door":
                    top = min(top, 2.4)  # keep a lintel: photos cropped at the top otherwise cut the whole wall
            feats.append(Feature(ftype, f.get("label", ftype), t0, t1, round(bottom, 3), round(top, 3),
                                 ftype in CUTS_WALL, f.get("state", "")))
        out.append(WallObservation(
            wall_id=wid, photo_id=photo_id,
            t0=min(span.t0, span.t1), t1=max(span.t0, span.t1),
            exists=bool(w.get("exists", True)),
            color_hex=w.get("color_hex", "#DDDDDD"),
            material=w.get("material", "other"),
            features=feats, notes=w.get("notes", ""),
            confidence=float(w.get("confidence", 0.5)),
            pixels_per_meter=span.pixels_per_meter,
        ))
    return out


def dumps(obs: list[WallObservation]) -> str:
    return json.dumps([o.to_dict() for o in obs], indent=2)
