# FloorTwin — floor plan → 3D walls → photos update only the walls they see

```
 floor plan PNG ──► walls.py ──► 3D walls (extruded, doorways already open)
                                       ▲
 phone capture ──► visibility.py ──► which walls are in THIS photo's view cone, and which
 (photo + pose)        (2D ray cast)    photo columns show which metres of each wall
                          │
                          ▼
                    vision.py ──► Claude reads the photo strip-by-strip:
                          │        exists? colour? material? doors / windows / signs (bounding boxes)
                          ▼
                    state.py ──► merge ONLY into the visible stretch of the visible walls
                          │        (features replaced, best photo kept as texture, rev++)
                          ▼
 Unity TwinWorld ◄── /state poll ──► rebuild only walls whose rev changed (they flash yellow)
```

## Why it only changes walls in the photo's POV

1. **Ray casting per photo column.** For each column `u` of the photo the server casts a ray from the
   camera position on the plan at `heading + atan((2u−1)·tan(hFOV/2))` (true pinhole spacing). The ray
   stops at the **nearest** wall, so walls hidden behind others are never touched.
2. Consecutive columns that hit the same wall form a **span**: photo columns `[u0,u1]` ↔ wall stretch
   `[t0,t1]`. Only `[t0,t1]` of that wall can change from this photo.
3. The model is told exactly which strip of the photo belongs to which wall (coloured labelled bands
   drawn on a copy of the photo). It answers in photo fractions; the server converts back:
   * `x → t along the wall` with the same ray geometry (exact, not linear),
   * `y → height in metres` with pitch, vertical FOV, camera height and the ray distance.
   So the model decides *what* is there; AR tracking decides *where*.
4. **Merging**: features whose centre lies in `[t0,t1]` are replaced; a much worse view (further away,
   oblique, low confidence) can't overwrite a better one. Photo texture coverage is re-partitioned so
   every stretch shows its best photo. Each changed wall gets `rev + 1`; Unity rebuilds only those.

Accuracy check on synthetic data: a door painted at 40–48 % along a wall, 0–2.1 m tall, comes back as
40.0–48.1 %, 0–2.11 m; Unity's projection of the door corners matches the photo within 1 px.

---

## Vision backends

| `--observer` | What runs | Cost | Needs |
|---|---|---|---|
| `local` | SegFormer-B2 (ADE20K semantic segmentation: wall / door / window / painting… per pixel) + YOLOE open-vocabulary detector (fire extinguisher, exit sign, whiteboard, TV, outlet…) + pixel-median wall colour | free, offline | ~0.8 GB models |
| `hybrid` | `local` for every wall; walls with confidence < 0.45 (mostly hidden by furniture/people) re-checked by Claude | a few API calls | API key |
| `claude` | Claude reads every wall strip | API per photo | API key |
| `mock` | fake answers | free | nothing |

Swap models with env vars: `FLOORTWIN_SEG_MODEL` (any Hugging Face ADE20K segmentation model, e.g.
`nvidia/segformer-b0-finetuned-ade-512-512` for speed or `Intel/dpt-large-ade` for accuracy),
`FLOORTWIN_DET_MODEL` (`yoloe-11s-seg.pt` default, `yoloe-11l-seg.pt` bigger, or `yolov8s-worldv2.pt`).
Check any model on your own photos: `python tests/check_local_vision.py photo.jpg` writes `photo_check.jpg`
(walls blue, doors green, windows yellow, wall objects magenta, boxes = reported features).

License note: Ultralytics YOLOE/YOLO-World are AGPL-3.0 (fine for a hackathon; matters for a commercial product).

## 1. Run the server (laptop)

Easiest: `python -m twin.cli serve --plan plan.png --entrance-x 398 --entrance-y 575` (see START_HERE.md).
Manual alternative:

```bash
cd server
pip install -r requirements.txt
export ANTHROPIC_API_KEY=sk-ant-...            # from platform.claude.com (optional)
uvicorn twin.server:app --host 0.0.0.0 --port 8000
#   vision:      FLOORTWIN_OBSERVER=local|hybrid|claude|mock
#   Claude model: FLOORTWIN_MODEL=claude-opus-5-5  (default claude-sonnet-5-5)
```

Preview wall extraction first (writes `plan_walls.png` with every wall in red + its id):
```bash
python -m twin.cli walls --plan plan.png
#   thin walls missed?   --min-thickness 3        text detected as walls?  --min-thickness 8
```

Upload the plan (use the **exact same image file** the app loads, so pixel coordinates match):
```bash
curl -F file=@plan.png -F entrance_x=398 -F entrance_y=575 -F entrance_heading_deg=0 \
     http://localhost:8000/plan
# returns the auto-detected scale (from doorway widths). Add -F px_per_m=40 to override it.
```

## 2. Phone app (existing FloorTrack scene)

* Add **CaptureUploader** to the Systems object → Pins = SpatialPinController, Server Url =
  `http://<laptop-LAN-ip>:8000` (phone and laptop on the same Wi-Fi).
* Player Settings → Other → **Allow downloads over HTTP: Always allowed**.
* Pins now also record pitch, vertical FOV and camera height (SpatialPinController →
  *Calibration Eye Height* = how high you hold the phone when tapping Calibrate, default 1.4 m).
* Every Capture is uploaded and queued; failed uploads retry automatically.

## 3. Unity viewer (laptop, separate scene or project)

1. New scene → Main Camera: add **TwinViewerControls**.
2. Empty GameObject "Twin": add **TwinClient** (Server Url `http://127.0.0.1:8000`) and **TwinWorld**.
3. Press Play. That's all; materials are created automatically (assign your own in TwinWorld for nicer
   glass). The floor shows the plan image, walls rise from it, photo frustums show where pictures were
   taken (yellow = processing, green = done, red = error). Each new photo flashes the walls it changed.

Controls: left-drag orbit, right-drag pan, wheel zoom, **F** walk-through (WASD), **T** top view,
hover anything for details.

## Offline / batch mode

Process a folder the app already saved (`<persistentDataPath>/FloorTrack/<storageKey>/` contains
`pins.json` + jpgs), then point the server at the result to view it:
```bash
python -m twin.cli build --plan plan.png --pins path/to/pins.json --out twin_data \
       --entrance-x 398 --entrance-y 575 [--mock]
FLOORTWIN_DATA=twin_data uvicorn twin.server:app --port 8000
```

## API

| Method | Path | Body | |
|---|---|---|---|
| POST | `/plan` | `file`, `entrance_x`, `entrance_y`, `entrance_heading_deg`, `px_per_m?` | extract walls, reset |
| POST | `/capture` | `photo` (jpg), `meta` (PinRecord JSON) | queue one photo |
| GET | `/state?since=N` | | full state, or `{"unchanged":true}` |
| GET | `/photo/{id}`, `/plan_image` | | images |

## Limits and next steps

* Roll is assumed 0 (phone upright). Tilted photos shift features slightly; add roll to PinRecord if needed.
* Wall *positions* come from the plan; photos can open, remove, recolour and furnish them, not move
  them. To move walls, record AR depth (ARCore Depth / LiDAR) at capture and fit planes.
* Each photo is one model call (~5–15 s). Processing is sequential so the model always sees the latest
  state; walls update live while you keep walking.
* Plans with hatched or double-line walls may need `--min-thickness` tuning; check `twin.cli walls` first.
