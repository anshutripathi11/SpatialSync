"""Glue: floor plan -> walls, and one capture -> visible spans -> model -> merged wall updates."""
from __future__ import annotations

import os
import uuid

import cv2
from PIL import Image

from .state import TwinState, PhotoState
from .visibility import CameraPose, visible_spans
from .vision import to_wall_observations
from .walls import extract_walls, estimate_pixels_per_meter, read_image


def load_plan(state: TwinState, plan_path: str, px_per_m: float | None = None,
              entrance=(0.0, 0.0), entrance_heading_deg: float = 0.0,
              min_thickness_px: int = 5, min_length_px: int = 20) -> dict:
    try:
        img = read_image(plan_path)
    except SystemExit as e:
        raise ValueError(str(e).strip())
    walls = extract_walls(img, min_thickness_px, min_length_px)
    est, votes = estimate_pixels_per_meter(walls)
    state.plan_file = os.path.basename(plan_path)
    state.plan_height, state.plan_width = img.shape[:2]
    state.px_per_m = float(px_per_m or est or state.px_per_m)
    state.entrance = [float(entrance[0]), float(entrance[1])]
    state.entrance_heading_deg = float(entrance_heading_deg)
    state.set_walls(walls)
    return {"walls": len(walls), "px_per_m": state.px_per_m, "px_per_m_estimate": est, "door_votes": votes}


def pose_from_meta(meta: dict, photo_w: int, photo_h: int) -> CameraPose:
    """meta = the PinRecord JSON the app writes (plus the new pitch/vfov/height fields)."""
    ip = meta["imagePixel"]
    return CameraPose(
        x=float(ip["x"]), y=float(ip["y"]),
        heading_deg=float(meta["headingDegrees"]),
        hfov_deg=float(meta.get("horizontalFovDegrees", 60.0)),
        vfov_deg=float(meta.get("verticalFovDegrees", 0.0)) or _vfov_from_aspect(meta, photo_w, photo_h),
        pitch_deg=float(meta.get("pitchDegrees", 0.0)),
        height_m=float(meta.get("cameraHeightMeters", 1.4)),
        photo_w=photo_w, photo_h=photo_h,
    )


def _vfov_from_aspect(meta, w, h) -> float:
    import math
    hf = math.radians(float(meta.get("horizontalFovDegrees", 60.0)))
    return math.degrees(2 * math.atan(math.tan(hf / 2) * h / max(w, 1)))


def register_capture(state: TwinState, photo_path: str, meta: dict, photo_id: str | None = None) -> PhotoState:
    with Image.open(photo_path) as im:
        w, h = im.size
    pose = pose_from_meta(meta, w, h)
    pid = photo_id or meta.get("id") or uuid.uuid4().hex[:8]
    ps = PhotoState(pid, os.path.basename(photo_path), pose.x, pose.y, pose.heading_deg, pose.hfov_deg,
                    pose.vfov_deg, pose.pitch_deg, pose.height_m, w, h)
    with state._lock:
        state.photos[pid] = ps
        state.version += 1
    return ps


def process_capture(state: TwinState, photo_path: str, ps: PhotoState, observer) -> list[str]:
    """Runs the whole per-photo update. Only walls inside this photo's view cone can change."""
    ps.status = "processing"
    try:
        pose = CameraPose(ps.x, ps.y, ps.heading_deg, ps.hfov_deg, ps.vfov_deg, ps.pitch_deg, ps.height_m,
                          ps.width, ps.height)
        walls = state.wall_objects()
        spans = visible_spans(pose, walls, state.px_per_m)
        ps.walls_seen = sorted({s.wall_id for s in spans})
        with Image.open(photo_path) as im:
            raw = observer.observe(im.convert("RGB"), spans)
        obs = to_wall_observations(raw, pose, spans, {w.id: w for w in walls}, state.px_per_m, ps.id)
        changed = state.apply(obs)
        ps.status = "done"
        return changed
    except Exception as e:  # keep the server alive; surface the error in /state
        ps.status, ps.error = "error", f"{type(e).__name__}: {e}"
        return []
    finally:
        with state._lock:
            state.version += 1
