# FloorTrack + FloorTwin — run it step by step

What you get: walk a building with your phone → your path is drawn on the floor plan → every photo you take
updates **only the walls in that photo's view** in a live 3D model on your laptop.

```
FloorTrack/
  Scripts/      → copy into your Unity project as Assets/FloorTrack/Scripts
  server/       → Python server on your laptop (walls, vision, live state)
  START_HERE.md   (this file)   README.md (app details)   TWIN_README.md (3D/vision details)
```

---

## Part A — One-time installs (≈ 45 min, mostly downloads)

1. **Unity Hub** → install **Unity 6 LTS** with **Android Build Support** (incl. OpenJDK + SDK/NDK)
   and/or **iOS Build Support** (iPhone builds also need a Mac with Xcode).
2. **Python 3.10, 3.11 or 3.12** from python.org (tick "Add to PATH" on Windows).
3. Optional: an **Anthropic API key** (only for `hybrid`/`claude` vision modes) —
   platform.claude.com → API Keys → Create Key, and add credits under Billing.

## Part B — Laptop server (≈ 15 min)

Open a terminal in `FloorTrack/server`:

```bash
python -m venv .venv
# Mac/Linux:  source .venv/bin/activate        Windows:  .venv\Scripts\activate
pip install -r requirements.txt          (includes the CLIP text encoder for YOLOE; no Git needed)
python -m twin.download_models          # one-time ~0.8 GB of open-source models; do it on good Wi-Fi
```
NVIDIA GPU? Install the CUDA build of PyTorch from pytorch.org first; it makes vision ~10× faster.
Apple Silicon uses the GPU (MPS) automatically.

**Check the floor plan.** Put your plan image (e.g. `plan.png`) in `server/` and run:
```bash
python -m twin.cli walls --plan plan.png
```
Open `plan_walls.png`: every wall should have a red line. Missing thin walls → add `--min-thickness 3`;
text detected as walls → `--min-thickness 8`. It also prints an estimated **pixels per metre**.

**Find the entrance numbers** (open the plan in Paint / Preview / GIMP):
* entrance **x, y** in pixels from the top-left corner, e.g. 398, 575
* the direction you will face there, in degrees clockwise from "up" on the image: 0 up, 90 right, 180 down, -90 left

## Part C — Start the server (every session)

```bash
python -m twin.cli serve --plan plan.png --entrance-x 398 --entrance-y 575 --entrance-heading 0
#   add --px-per-m 40 if you know the scale (otherwise it's estimated from door widths)
#   vision mode:  --observer local   (default without API key: SegFormer + YOLOE, free, offline)
#                 --observer hybrid  (default with ANTHROPIC_API_KEY set: local, Claude only for unsure walls)
#                 --observer claude  |  --mock  (fake vision, to test the plumbing)
```
It prints two lines — **write down the Phone app URL**, e.g. `http://192.168.1.20:8000`.
Keep this terminal open. Allow Python through the firewall if asked. To start a new building/session,
add `--reset` (or use a different `--out` folder).

To use the API key: set it in the same terminal before `serve`
(`export ANTHROPIC_API_KEY="sk-ant-..."`, Windows PowerShell `$env:ANTHROPIC_API_KEY="sk-ant-..."`).

## Part D — Unity project (≈ 20 min, once)

1. Unity Hub → **New project** → template **3D (Built-In Render Pipeline)** → name `FloorTrackApp`.
   (Built-In avoids the extra URP step. If you use Universal 3D, add *AR Background Renderer Feature* to the URP renderer.)
2. **Window → Package Manager → Unity Registry**, install: **AR Foundation**, **Google ARCore XR Plugin**,
   **Apple ARKit XR Plugin**. If asked to enable the new Input System and restart: Yes.
3. **Window → TextMeshPro → Import TMP Essential Resources**.
4. Copy the `Scripts` folder to `Assets/FloorTrack/Scripts`. Wait for compiling; the Console must have no red errors.
5. Drag **the same `plan.png`** into `Assets/`.
6. **Edit → Project Settings → XR Plug-in Management**: Android tab tick **ARCore**; iOS tab tick **ARKit**.
7. Menu **FloorTrack → Setup Wizard**. Fill in:
   * Floor plan image = your plan, Pixels per metre = the server's value (it printed it),
   * Entrance pixel + facing = the same numbers as Part C,
   * Twin server URL = the Phone app URL from Part C.

   Click **1. Apply mobile AR Player Settings**, **2. Build phone app scene**, **3. Build 3D viewer scene**.
   This creates `Assets/FloorTrack/Scenes/FloorTrackApp.unity` (fully wired) and `TwinViewer.unity`.

**Quick Editor test (no phone):** open `FloorTrackApp`, press **Play**, click **Calibrate**, move with
**W/A/S/D**, turn **Q/E**. A blue trail follows you on the map. **Capture** drops a pin with a view cone and
uploads it (the server terminal prints which walls it saw). Stop Play.

## Part E — Phone build

1. **File → Build Profiles** → **Android** → **Switch Platform** (FloorTrackApp is already in the scene list).
2. Phone: enable **Developer options → USB debugging**, plug in, accept the prompt.
3. **Build And Run**. (iPhone: switch to iOS, **Build**, open the Xcode project, choose your Team, Run.)

## Part F — Use it

1. Laptop: server running (Part C). In Unity open **TwinViewer** and press **Play** — the plan appears with
   grey 3D walls.
2. Phone (same Wi-Fi): open the app, stand on the entrance facing the agreed direction, wait for
   **Tracking**, tap **Calibrate**.
3. Walk; tap **Capture** to photograph walls. In the viewer, within a few seconds per photo, the walls in
   that photo **flash yellow** and update: real colour, doors/windows cut out, objects marked, the photo
   projected on the wall. Green frustums show where photos were taken (yellow = processing, red = error).
4. Viewer controls: drag = orbit, right-drag = pan, wheel = zoom, **F** = walk inside (WASD), **T** = top view,
   hover anything for details.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Viewer: "server: Cannot connect" | Server not running, or wrong URL in the *Twin* object's TwinClient. |
| Phone pins appear but nothing changes in the viewer | Phone can't reach the laptop: same Wi-Fi? correct IP? firewall? Server URL on Systems → CaptureUploader. |
| Black camera on Android | Run wizard step 1 again (removes Vulkan); on URP add the AR Background Renderer Feature. |
| Calibrate button stays grey | AR isn't tracking yet: move the phone slowly, point at textured surfaces, better light. |
| Updates land on the wrong walls | App and server disagree: same image file, same entrance x/y/facing, same px per metre. Calibrate facing the right way. |
| Red frustum in viewer | Hover it to read the error (e.g. missing API key in claude/hybrid mode). |
| Vision is slow (5–10 s/photo) | CPU only. Use a GPU, or `FLOORTWIN_SEG_MODEL=nvidia/segformer-b0-finetuned-ade-512-512` (smaller). |
| Wizard says it can't create AR objects | AR Foundation / ARCore / ARKit packages not installed. |
