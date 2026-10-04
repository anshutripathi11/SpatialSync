"""
The digital-twin state: floor-plan walls + everything photos have taught us about them.

Update rule ("only walls in the POV change"):
  A photo produces WallObservations for the walls it can see, each limited to the stretch [t0, t1] that
  was actually visible. Merging touches ONLY those stretches:
    * features (doors, windows...) whose centre lies inside [t0, t1] are replaced by the new ones,
      unless the earlier view of that stretch was much better (closer / more head-on, higher confidence);
    * photo texture coverage is re-partitioned so every piece of wall shows the best photo of it;
    * colour/material follow the best observation so far;
    * "no wall here" turns that stretch into a full-height 'removed' opening.
  Walls that are not visible in the photo are never modified. Each wall carries a `rev` so the Unity
  viewer rebuilds only walls that changed.
"""
from __future__ import annotations

import json
import threading
from dataclasses import dataclass, field, asdict

from .vision import WallObservation, WALL_HEIGHT_M
from .walls import Wall

REPLACE_IF_AT_LEAST = 0.5   # new view must be >= 50 % as good as the old one to overwrite its features


def _score(o: WallObservation) -> float:
    return max(o.pixels_per_meter, 1.0) * max(o.confidence, 0.05)


@dataclass
class WallState:
    id: str
    a: list[float]
    b: list[float]
    thickness_px: float
    height_m: float = WALL_HEIGHT_M
    color_hex: str = "#E6E6E6"
    material: str = "unknown"
    best_score: float = 0.0
    observed: bool = False
    features: list[dict] = field(default_factory=list)   # Feature dicts + "score", "photo_id"
    coverage: list[dict] = field(default_factory=list)   # [{t0, t1, photo_id, score}], non-overlapping
    rev: int = 0


@dataclass
class PhotoState:
    id: str
    file: str
    x: float
    y: float
    heading_deg: float
    hfov_deg: float
    vfov_deg: float
    pitch_deg: float
    height_m: float
    width: int
    height: int
    status: str = "queued"      # queued | processing | done | error
    error: str = ""
    walls_seen: list[str] = field(default_factory=list)


@dataclass
class TwinState:
    plan_file: str = ""
    plan_width: int = 0
    plan_height: int = 0
    px_per_m: float = 40.0
    entrance: list[float] = field(default_factory=lambda: [0.0, 0.0])
    entrance_heading_deg: float = 0.0
    wall_height_m: float = WALL_HEIGHT_M
    walls: dict[str, WallState] = field(default_factory=dict)
    photos: dict[str, PhotoState] = field(default_factory=dict)
    version: int = 0

    def __post_init__(self):
        self._lock = threading.RLock()

    # ------------------------------------------------------------ setup
    def set_walls(self, walls: list[Wall]):
        with self._lock:
            self.walls = {w.id: WallState(w.id, [w.a[0], w.a[1]], [w.b[0], w.b[1]], w.thickness) for w in walls}
            self.version += 1

    def wall_objects(self) -> list[Wall]:
        return [Wall(s.id, tuple(s.a), tuple(s.b), s.thickness_px) for s in self.walls.values()]

    # ------------------------------------------------------------ merge
    def apply(self, observations: list[WallObservation]) -> list[str]:
        """Merge one photo's observations. Returns the ids of walls that changed."""
        changed = []
        with self._lock:
            for o in observations:
                ws = self.walls.get(o.wall_id)
                if ws is None:
                    continue
                s = _score(o)
                lo, hi = o.t0, o.t1

                # Quality of what we currently know about this stretch
                old = [c["score"] for c in ws.coverage if c["t1"] > lo and c["t0"] < hi]
                old_best = max(old, default=0.0)
                # A much better photo of this stretch already exists -> keep its features.
                trust_new = not (old_best and s < REPLACE_IF_AT_LEAST * old_best)

                # 1) features in the visible stretch are replaced
                if trust_new:
                    ws.features = [f for f in ws.features if not (lo <= (f["t0"] + f["t1"]) / 2 <= hi)]
                    for f in o.features:
                        d = asdict(f); d.update(score=s, photo_id=o.photo_id)
                        ws.features.append(d)
                    if not o.exists and o.confidence >= 0.6:
                        ws.features.append(dict(type="removed", label="no wall observed", t0=lo, t1=hi,
                                                bottom_m=0.0, top_m=ws.height_m, cuts_wall=True, state="",
                                                score=s, photo_id=o.photo_id))
                    ws.features.sort(key=lambda f: f["t0"])

                # 2) texture coverage: the new photo takes over [lo, hi] wherever it is at least as good
                ws.coverage = _repartition(ws.coverage, lo, hi, o.photo_id, s)

                # 3) appearance follows the best view of the wall
                if s >= ws.best_score:
                    ws.best_score = s
                    ws.color_hex = o.color_hex
                    ws.material = o.material
                ws.observed = True
                ws.rev += 1
                if ws.id not in changed:
                    changed.append(ws.id)
            if changed:
                self.version += 1
        return changed

    # ------------------------------------------------------------ io
    def to_dict(self) -> dict:
        with self._lock:
            d = {k: v for k, v in self.__dict__.items() if not k.startswith("_")}
            d["walls"] = [asdict(w) for w in self.walls.values()]
            d["photos"] = [asdict(p) for p in self.photos.values()]
            return d

    def save(self, path: str):
        with open(path, "w") as f:
            json.dump(self.to_dict(), f, indent=1)

    @classmethod
    def load(cls, path: str) -> "TwinState":
        with open(path) as f:
            d = json.load(f)
        walls = {w["id"]: WallState(**w) for w in d.pop("walls")}
        photos = {p["id"]: PhotoState(**p) for p in d.pop("photos")}
        st = cls(**d)
        st.walls, st.photos = walls, photos
        return st


def _repartition(coverage: list[dict], lo: float, hi: float, photo_id: str, score: float) -> list[dict]:
    """Insert [lo, hi] -> photo_id into a non-overlapping interval list, winning only where it scores >=."""
    out, new_pieces = [], [(lo, hi)]
    for c in coverage:
        if c["t1"] <= lo or c["t0"] >= hi or c["score"] > score:
            out.append(c)
            if not (c["t1"] <= lo or c["t0"] >= hi):  # better old piece: carve it out of the new range
                nxt = []
                for a, b in new_pieces:
                    if c["t1"] <= a or c["t0"] >= b:
                        nxt.append((a, b))
                    else:
                        if a < c["t0"]: nxt.append((a, c["t0"]))
                        if c["t1"] < b: nxt.append((c["t1"], b))
                new_pieces = nxt
            continue
        # old piece loses inside [lo, hi]; keep its outside parts
        if c["t0"] < lo: out.append(dict(c, t1=lo))
        if c["t1"] > hi: out.append(dict(c, t0=hi))
    out += [dict(t0=a, t1=b, photo_id=photo_id, score=score) for a, b in new_pieces if b - a > 1e-4]
    return sorted(out, key=lambda c: c["t0"])
