# FloorTrack — AR floor-plan tracker (Unity + AR Foundation)

> New here? Read **START_HERE.md** — it covers the whole project step by step. The Setup Wizard
> (menu FloorTrack → Setup Wizard) builds everything below automatically; this file explains it.

Walk a building with your phone; your path is drawn live on a 2D floor plan and every photo drops a pin
with a field-of-view wedge. Works on iOS (ARKit) and Android (ARCore) via AR Foundation.

## Scripts and how they talk

```
Core/      Contracts.cs (IPoseSource, IMapProjection, IPhotoCaptureService, TrackedPose, MapPose)
           FloorMath.cs (all the math, pure static)   InterfaceRef.cs (interface slots + UI helper)
Map/       BlueprintMap.cs        ← Component 1: image, MetersToPixelsScale, entrance point  (IMapProjection)
           UserMarkerView.cs      ← Component 1: dot + orientation arrow (view only)
           MapPanZoom.cs, ConstantScreenSize.cs  (optional: pinch/pan/follow)
Tracking/  ARPoseTracker.cs       ← Component 2: AR camera pose → calibrated metres       (IPoseSource)
           EditorPoseSimulator.cs   WASD fake pose for testing in the Editor              (IPoseSource)
           MapPoseBridge.cs       ← Component 2: metres → blueprint coordinates, moves the marker
           CalibrationController.cs  Calibrate button + status text
Path/      UILineGraphic.cs, PathTracer.cs           ← Component 3
Capture/   ScreenshotCaptureService.cs (IPhotoCaptureService), FovConeGraphic.cs, PinView.cs,
           PhotoModal.cs, PinRecord.cs, SpatialPinController.cs   ← Component 4
```

```
ARPoseTracker ──TrackedPose(m)──▶ MapPoseBridge ──MapPose(px)──▶ UserMarkerView
  (IPoseSource)                    uses IMapProjection         ├──▶ PathTracer ─▶ UILineGraphic
                                   (BlueprintMap)              └──▶ SpatialPinController ─▶ PinView / PhotoModal
```
The tracker never knows about the map; the map never knows about AR. Swap the tracker for the simulator,
or the screenshot service for a CPU-image service, without touching anything else.

---

## 1. Project setup

1. Unity 2022.3 LTS or Unity 6, **3D (URP or Built-in)** template.
2. Package Manager: **AR Foundation**, **Apple ARKit XR Plugin**, **Google ARCore XR Plugin** (all same major version, 5.x or 6.x). TextMeshPro (import essentials when prompted).
3. *Project Settings → XR Plug-in Management*: tick **ARKit** on the iOS tab, **ARCore** on the Android tab.
4. iOS: *Player → Camera Usage Description* = "Used to track your position in the building". Target iOS 14+.
   Android: Minimum API 24+, **Graphics APIs: remove Vulkan if you see a black camera feed** (use OpenGLES3), Scripting Backend IL2CPP, ARM64. On URP add the **AR Background Renderer Feature** to the renderer asset.
5. Copy the `Scripts` folder into `Assets/FloorTrack/`.
6. Import your floor plan PNG: Texture Type **Sprite (2D and UI)**, Max Size 4096, Compression None/High Quality (thin lines blur otherwise).

## 2. Scene hierarchy

Right-click → *XR → AR Session* and *XR → XR Origin (Mobile AR)*. Delete the default Main Camera.

```
AR Session
XR Origin (Mobile AR)                [ARPoseTracker]  (xrOrigin = self, arCamera auto)
  └─ Camera Offset
       └─ Main Camera                (ARCameraManager, ARCameraBackground, tag MainCamera)
PoseSimulator                        [EditorPoseSimulator]   (editor testing only)
Systems                              [MapPoseBridge] [PathTracer] [SpatialPinController]
                                     [ScreenshotCaptureService] [CalibrationController]
EventSystem
UI Canvas                            Canvas: Screen Space – Overlay
                                     Canvas Scaler: Scale With Screen Size, 1080×1920, Match 0.5
  ├─ MapPanel                        anchors bottom-stretch, height ~55% of screen; Image white @ 92%
  │    └─ MapViewport                stretch full; RectMask2D; Image alpha 0 (Raycast Target ON) [MapPanZoom]
  │         └─ Blueprint             anchors+pivot centre (0.5,0.5); Image (floor plan) [BlueprintMap]
  │              ├─ PathLayer        [UILineGraphic]  colour #1E8CFF, thickness 6 (auto-stretched)
  │              ├─ PinLayer         empty RectTransform (auto-stretched)
  │              └─ UserMarker       size 0×0; CanvasGroup; [UserMarkerView]
  │                   └─ Visual      [ConstantScreenSize]
  │                        ├─ Arrow  Image of an arrow/wedge pointing UP, 70×70, pivot (0.5, 0.2)
  │                        └─ Dot    Image circle 28×28, white outline
  ├─ TopBar                          anchors top-stretch, height 160
  │    └─ StatusText                 TMP_Text, font 28, wrap on
  ├─ BottomBar                       anchors bottom-stretch, height 180, Horizontal Layout Group
  │    ├─ CalibrateButton            Button + TMP label
  │    └─ CaptureButton              Button (camera icon)
  └─ PhotoModal                      stretch full, empty, ALWAYS ACTIVE [PhotoModal]
       └─ ModalRoot                  stretch full (hidden at runtime)
            ├─ Backdrop              stretch full; Image black @ 75%; Button
            └─ Card                  anchors centre, 960×1500; Vertical Layout Group (control child size ✓)
                 ├─ PhotoFrame       Layout Element flexible height 1
                 │    └─ Photo       RawImage, stretch full; AspectRatioFitter = Fit In Parent
                 ├─ Caption          TMP_Text, font 30
                 └─ CloseButton      Button "Close"
```
Order matters: PathLayer → PinLayer → UserMarker means the marker draws on top of pins, pins on top of the path.
Leave the top half of the screen clear so the user still sees the camera feed while walking.

### Pin prefab (Assets/FloorTrack/Prefabs/Pin.prefab)
```
Pin        RectTransform 0×0, anchors centre; Button (Target Graphic = Icon, Transition None); [PinView]
  ├─ FovCone   [FovConeGraphic]  colour #FFC400 alpha 140   (first child → drawn under the icon)
  └─ Icon      Image pin sprite 56×56, pivot (0.5, 0) if the sprite's tip is at the bottom; [ConstantScreenSize]
```
PinView: Button = Pin, Cone = FovCone.

## 3. Wiring the Inspector

| Component | Field | Drag in |
|---|---|---|
| **BlueprintMap** | Blueprint Image | Blueprint's Image |
| | Floor Plan | your sprite (or call `SetFloorPlan(tex)` at runtime) |
| | Meters To Pixels Scale | see *Scale calibration* below |
| | Entrance Image Pixel | entrance position in image pixels, from the **top-left** (read it in any image editor) |
| | Entrance Heading Degrees | the direction the user will face, clockwise from image-up (0 up, 90 right, 180 down, -90 left) |
| **ARPoseTracker** | XR Origin | XR Origin |
| **MapPoseBridge** | Pose Source | XR Origin (ARPoseTracker) |
| | Editor Pose Source | PoseSimulator (optional) |
| | Map Projection | Blueprint |
| | User Marker | UserMarker |
| **UserMarkerView** | Orientation Arrow | Arrow · Canvas Group: UserMarker · Tint Targets: Dot, Arrow |
| **MapPanZoom** | Content | Blueprint · Follow Target: UserMarker |
| **CalibrationController** | Bridge, Calibrate Button, Button Label, Status Text | — |
| **PathTracer** | Bridge, Line | Systems, PathLayer |
| **ScreenshotCaptureService** | Canvases To Hide | UI Canvas |
| **SpatialPinController** | Bridge / Map / Capture Service / AR Camera | Systems / Blueprint / Systems / Main Camera |
| | Capture Button / Pin Layer / Pin Prefab / Photo Modal | — |
| | Storage Key | e.g. `science-building-floor2` |

With the Blueprint selected, the Scene view draws a **green arrow** at the entrance (2 m long) and a
**cyan 5 m / 1 m scale bar** — check that the arrow sits on the door and points into the building.

### Scale calibration (MetersToPixelsScale)
Pick two points on the plan whose real distance you know (a dimension line, a corridor you paced, a standard
0.9 m door). `scale = pixel distance / metres`. Example: a 12 m corridor spans 480 px → **40 px/m**.
At runtime you can also call `blueprintMap.SetScaleFromTwoPoints(pxA, pxB, metres)`.
Tip for the demo: verify by walking a 10 m straight line and checking the trail length.

## 4. Testing

* **Editor**: press Play, tap Calibrate, drive with **W/A/S/D**, turn with **Q/E**, Shift to run. Capture works (screenshots the Game view).
* **Device**: Build & Run. Stand on the entrance spot, face the direction of the green arrow, hold the phone upright, wait for status "Tracking", tap **Calibrate**. Walk normally, keep the camera pointed at textured surfaces (not blank walls), avoid covering the lens.

## 5. Accuracy notes (read before the demo)

* VIO drift is typically ~1–2 % of distance walked; a 100 m loop may end 1–2 m off. Recalibrate at known spots for long walks.
* Most visible error comes from **heading misalignment at calibration**: 5° off = 0.87 m sideways error after 10 m. Line up with a wall or door frame when tapping Calibrate.
* Dark corridors, glass, mirrors, and featureless walls make tracking slip; the marker turns orange and the trail breaks instead of drawing a fake jump.
* Elevators and stairs: Y is ignored; use one floor plan per floor and recalibrate after changing floors.
* Upgrade path: `IPhotoCaptureService` → `ARCameraManager.TryAcquireLatestCpuImage` for full-res photos; `IPoseSource` → anchor/Geospatial or image-marker relocalisation to kill drift.

---
## FloorTwin (3D reconstruction from plan + photos)
See **TWIN_README.md**: `server/` (Python: walls, visibility, Claude vision, live server) and
`Scripts/Twin/` (Unity viewer), plus `Scripts/Capture/CaptureUploader.cs` for the phone.
