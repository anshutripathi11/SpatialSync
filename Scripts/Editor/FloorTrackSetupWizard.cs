#if UNITY_EDITOR
using System.IO;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using FloorTrack.Twin;

namespace FloorTrack.EditorTools
{
    /// <summary>
    /// Menu: FloorTrack → Setup Wizard.
    /// Builds the whole phone-app scene (AR + map UI + pins + uploader, every reference wired), the Pin prefab,
    /// the 3D viewer scene, and applies the mobile AR Player Settings. Nothing to drag by hand.
    /// </summary>
    public class FloorTrackSetupWizard : EditorWindow
    {
        private const string Root = "Assets/FloorTrack/Generated";
        private Texture2D floorPlan;
        private float pxPerMeter = 40f;
        private Vector2 entrancePixel = new Vector2(100, 900);
        private float entranceHeading;
        private string serverUrl = "http://192.168.1.20:8000";

        [MenuItem("FloorTrack/Setup Wizard")]
        public static void Open() => GetWindow<FloorTrackSetupWizard>("FloorTrack Setup").minSize = new Vector2(420, 360);

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Use the SAME floor plan image and numbers you give the twin server.", MessageType.Info);
            floorPlan = (Texture2D)EditorGUILayout.ObjectField("Floor plan image", floorPlan, typeof(Texture2D), false);
            pxPerMeter = EditorGUILayout.FloatField("Pixels per metre", pxPerMeter);
            entrancePixel = EditorGUILayout.Vector2Field("Entrance pixel (from top-left)", entrancePixel);
            entranceHeading = EditorGUILayout.Slider("Entrance facing (° clockwise from up)", entranceHeading, -180, 180);
            serverUrl = EditorGUILayout.TextField("Twin server URL (laptop IP)", serverUrl);
            EditorGUILayout.Space();

            if (GUILayout.Button("1. Apply mobile AR Player Settings", GUILayout.Height(28))) ApplyPlayerSettings();
            using (new EditorGUI.DisabledScope(floorPlan == null))
                if (GUILayout.Button("2. Build phone app scene", GUILayout.Height(28))) BuildAppScene();
            if (GUILayout.Button("3. Build 3D viewer scene", GUILayout.Height(28))) BuildViewerScene();
        }

        // =====================================================================================================
        private static void ApplyPlayerSettings()
        {
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;   // http:// to the laptop
            PlayerSettings.iOS.cameraUsageDescription = "Tracks your position in the building and takes photos.";
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 }); // ARCore + Vulkan = black camera on many phones
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            Debug.Log("[FloorTrack] Player Settings applied. Still do once: Project Settings → XR Plug-in Management → tick ARCore (Android tab) / ARKit (iOS tab).");
            EditorUtility.DisplayDialog("FloorTrack", "Player Settings applied.\n\nOne manual step left:\nProject Settings → XR Plug-in Management → tick ARCore (Android) and/or ARKit (iOS).", "OK");
        }

        // =====================================================================================================
        private void BuildAppScene()
        {
            if (TMP_Settings.instance == null || TMP_Settings.defaultFontAsset == null)
            {
                EditorUtility.DisplayDialog("FloorTrack", "Import TextMeshPro first:\nWindow → TextMeshPro → Import TMP Essential Resources.\nThen click Build again.", "OK");
                return;
            }
            Directory.CreateDirectory(Root);
            Sprite planSprite = MakeSprite(floorPlan);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---------- AR (uses AR Foundation's own menu items so the camera is configured exactly right)
            bool okSession = EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session");
            bool okOrigin = EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)");
            var origin = Object.FindAnyObjectByType<XROrigin>();
            if (!okSession || !okOrigin || origin == null)
            {
                EditorUtility.DisplayDialog("FloorTrack", "Could not create the AR objects. Install AR Foundation + ARCore/ARKit plugins (Package Manager) and try again.", "OK");
                return;
            }
            Camera arCam = origin.Camera;
            var tracker = origin.gameObject.AddComponent<ARPoseTracker>();
            Set(tracker, "xrOrigin", origin);

            var sim = new GameObject("PoseSimulator (Editor only)").AddComponent<EditorPoseSimulator>();

            // ---------- UI root
            var es = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
            var canvasGo = new GameObject("UI Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var canvasRt = (RectTransform)canvasGo.transform;

            // ---------- Map panel (bottom 55 %)
            var panel = Rect("MapPanel", canvasRt, new Vector2(0, 0.12f), new Vector2(1, 0.62f));
            Img(panel, new Color(1, 1, 1, 0.92f));
            var viewport = Rect("MapViewport", panel, Vector2.zero, Vector2.one);
            viewport.gameObject.AddComponent<RectMask2D>();
            Img(viewport, new Color(0, 0, 0, 0)).raycastTarget = true;
            var panZoom = viewport.gameObject.AddComponent<MapPanZoom>();

            var blueprint = new GameObject("Blueprint", typeof(RectTransform)).GetComponent<RectTransform>();
            blueprint.SetParent(viewport, false);
            blueprint.anchorMin = blueprint.anchorMax = blueprint.pivot = new Vector2(0.5f, 0.5f);
            var bpImage = blueprint.gameObject.AddComponent<Image>();
            bpImage.sprite = planSprite;
            bpImage.preserveAspect = true;
            bpImage.SetNativeSize();
            bpImage.raycastTarget = false;
            var map = blueprint.gameObject.AddComponent<BlueprintMap>();
            Set(map, "blueprintImage", bpImage);
            Set(map, "floorPlan", planSprite);
            SetFloat(map, "metersToPixelsScale", pxPerMeter);
            SetVec2(map, "entranceImagePixel", entrancePixel);
            SetFloat(map, "entranceHeadingDegrees", entranceHeading);

            var pathLayer = Rect("PathLayer", blueprint, Vector2.zero, Vector2.one);
            var line = pathLayer.gameObject.AddComponent<UILineGraphic>();
            line.color = new Color(0.12f, 0.55f, 1f);
            var pinLayer = Rect("PinLayer", blueprint, Vector2.zero, Vector2.one);

            // User marker: dot + heading wedge (a FovConeGraphic pointing up, rotated by UserMarkerView)
            var marker = new GameObject("UserMarker", typeof(RectTransform), typeof(CanvasGroup)).GetComponent<RectTransform>();
            marker.SetParent(blueprint, false);
            marker.sizeDelta = Vector2.zero;
            var visual = new GameObject("Visual", typeof(RectTransform), typeof(ConstantScreenSize)).GetComponent<RectTransform>();
            visual.SetParent(marker, false);
            var arrowGo = new GameObject("Arrow", typeof(RectTransform));
            arrowGo.transform.SetParent(visual, false);
            var arrow = arrowGo.AddComponent<FovConeGraphic>();
            arrow.color = new Color(0.1f, 0.55f, 1f, 0.9f);
            SetFloat(arrow, "fovDegrees", 50f); SetFloat(arrow, "length", 60f); SetFloat(arrow, "edgeAlpha", 0.6f);
            var dot = new GameObject("Dot", typeof(RectTransform)).AddComponent<Image>();
            dot.transform.SetParent(visual, false);
            dot.sprite = Knob(); dot.rectTransform.sizeDelta = new Vector2(30, 30); dot.color = new Color(0.1f, 0.55f, 1f);
            var markerView = marker.gameObject.AddComponent<UserMarkerView>();
            Set(markerView, "orientationArrow", arrowGo.GetComponent<RectTransform>());
            Set(markerView, "canvasGroup", marker.GetComponent<CanvasGroup>());
            SetArray(markerView, "tintTargets", new Object[] { dot, arrow });

            Set(panZoom, "content", blueprint);
            Set(panZoom, "followTarget", marker);

            // ---------- Top status bar
            var top = Rect("TopBar", canvasRt, new Vector2(0, 0.9f), new Vector2(1, 1));
            Img(top, new Color(0, 0, 0, 0.55f));
            var status = Text("StatusText", top, "Starting AR…", 30);
            status.rectTransform.offsetMin = new Vector2(24, 12); status.rectTransform.offsetMax = new Vector2(-24, -12);

            // ---------- Bottom buttons
            var bottom = Rect("BottomBar", canvasRt, new Vector2(0, 0), new Vector2(1, 0.12f));
            Img(bottom, new Color(0.08f, 0.08f, 0.1f, 0.9f));
            var calibrate = Button("CalibrateButton", bottom, "Calibrate", new Vector2(0.04f, 0.15f), new Vector2(0.48f, 0.85f), new Color(0.2f, 0.6f, 0.3f));
            var capture = Button("CaptureButton", bottom, "Capture", new Vector2(0.52f, 0.15f), new Vector2(0.96f, 0.85f), new Color(0.15f, 0.45f, 0.9f));

            // ---------- Photo modal
            var modalHost = Rect("PhotoModal", canvasRt, Vector2.zero, Vector2.one);
            var modalRoot = Rect("ModalRoot", modalHost, Vector2.zero, Vector2.one);
            var backdropRt = Rect("Backdrop", modalRoot, Vector2.zero, Vector2.one);
            Img(backdropRt, new Color(0, 0, 0, 0.75f));
            var backdropBtn = backdropRt.gameObject.AddComponent<Button>();
            var card = Rect("Card", modalRoot, new Vector2(0.05f, 0.12f), new Vector2(0.95f, 0.9f));
            Img(card, new Color(0.12f, 0.12f, 0.14f, 1f));
            var frame = Rect("PhotoFrame", card, new Vector2(0.03f, 0.2f), new Vector2(0.97f, 0.98f));
            var photoRt = Rect("Photo", frame, Vector2.zero, Vector2.one);
            var raw = photoRt.gameObject.AddComponent<RawImage>();
            var fitter = photoRt.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            var caption = Text("Caption", Rect("CaptionArea", card, new Vector2(0.03f, 0.09f), new Vector2(0.97f, 0.19f)), "", 28);
            var close = Button("CloseButton", card, "Close", new Vector2(0.3f, 0.01f), new Vector2(0.7f, 0.08f), new Color(0.35f, 0.35f, 0.4f));
            var modal = modalHost.gameObject.AddComponent<PhotoModal>();
            Set(modal, "root", modalRoot.gameObject);
            Set(modal, "photo", raw);
            Set(modal, "aspectFitter", fitter);
            Set(modal, "caption", caption);
            Set(modal, "closeButton", close);
            Set(modal, "backdropButton", backdropBtn);
            modalRoot.gameObject.SetActive(false);

            // ---------- Pin prefab
            var pinPrefab = BuildPinPrefab();

            // ---------- Systems
            var systems = new GameObject("Systems");
            var bridge = systems.AddComponent<MapPoseBridge>();
            Set(bridge, "poseSource", tracker);
            Set(bridge, "editorPoseSource", sim);
            Set(bridge, "mapProjection", map);
            Set(bridge, "userMarker", markerView);

            var calib = systems.AddComponent<CalibrationController>();
            Set(calib, "bridge", bridge);
            Set(calib, "calibrateButton", calibrate);
            Set(calib, "calibrateButtonLabel", calibrate.GetComponentInChildren<TMP_Text>());
            Set(calib, "statusText", status);

            var tracer = systems.AddComponent<PathTracer>();
            Set(tracer, "bridge", bridge);
            Set(tracer, "line", line);

            var shot = systems.AddComponent<ScreenshotCaptureService>();
            SetArray(shot, "canvasesToHide", new Object[] { canvas });

            var pins = systems.AddComponent<SpatialPinController>();
            Set(pins, "bridge", bridge);
            Set(pins, "map", map);
            Set(pins, "captureService", shot);
            Set(pins, "arCamera", arCam);
            Set(pins, "captureButton", capture);
            Set(pins, "pinLayer", pinLayer);
            Set(pins, "pinPrefab", pinPrefab);
            Set(pins, "photoModal", modal);
            SetString(pins, "storageKey", floorPlan.name);

            var uploader = systems.AddComponent<CaptureUploader>();
            Set(uploader, "pins", pins);
            SetString(uploader, "serverUrl", serverUrl);

            Directory.CreateDirectory("Assets/FloorTrack/Scenes");
            const string path = "Assets/FloorTrack/Scenes/FloorTrackApp.unity";
            EditorSceneManager.SaveScene(scene, path);
            AddToBuild(path);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            EditorUtility.DisplayDialog("FloorTrack", $"Phone app scene saved:\n{path}\n\nPress Play to test with W/A/S/D + Q/E,\nor Build And Run to your phone.", "OK");
        }

        private static PinView BuildPinPrefab()
        {
            var pin = new GameObject("Pin", typeof(RectTransform));
            ((RectTransform)pin.transform).sizeDelta = Vector2.zero;
            var coneGo = new GameObject("FovCone", typeof(RectTransform));
            coneGo.transform.SetParent(pin.transform, false);
            var cone = coneGo.AddComponent<FovConeGraphic>();
            cone.color = new Color(1f, 0.77f, 0f, 0.55f);
            var icon = new GameObject("Icon", typeof(RectTransform), typeof(ConstantScreenSize)).AddComponent<Image>();
            icon.transform.SetParent(pin.transform, false);
            icon.sprite = Knob(); icon.color = new Color(0.95f, 0.25f, 0.2f);
            icon.rectTransform.sizeDelta = new Vector2(44, 44);
            var btn = pin.AddComponent<Button>();
            btn.targetGraphic = icon;
            var view = pin.AddComponent<PinView>();
            Set(view, "button", btn);
            Set(view, "cone", cone);
            var prefab = PrefabUtility.SaveAsPrefabAsset(pin, $"{Root}/Pin.prefab");
            Object.DestroyImmediate(pin);
            return prefab.GetComponent<PinView>();
        }

        // =====================================================================================================
        private void BuildViewerScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single); // camera + light
            var cam = Camera.main;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.farClipPlane = 1000f;
            var twin = new GameObject("Twin");
            var client = twin.AddComponent<TwinClient>();
            SetString(client, "serverUrl", "http://127.0.0.1:8000");
            var world = twin.AddComponent<TwinWorld>();
            var controls = cam.gameObject.AddComponent<TwinViewerControls>();
            Set(controls, "world", world);
            Set(controls, "client", client);
            Directory.CreateDirectory("Assets/FloorTrack/Scenes");
            const string path = "Assets/FloorTrack/Scenes/TwinViewer.unity";
            EditorSceneManager.SaveScene(scene, path);
            EditorUtility.DisplayDialog("FloorTrack", $"Viewer scene saved:\n{path}\n\nStart the twin server, then press Play.", "OK");
        }

        // =====================================================================================================
        // helpers
        private static Sprite MakeSprite(Texture2D tex)
        {
            string p = AssetDatabase.GetAssetPath(tex);
            var imp = (TextureImporter)AssetImporter.GetAtPath(p);
            if (imp.textureType != TextureImporterType.Sprite || imp.maxTextureSize < 4096 || imp.textureCompression != TextureImporterCompression.Uncompressed)
            {
                imp.textureType = TextureImporterType.Sprite;
                imp.spriteImportMode = SpriteImportMode.Single;
                imp.maxTextureSize = 4096;
                imp.textureCompression = TextureImporterCompression.Uncompressed; // keep thin lines sharp
                imp.mipmapEnabled = false;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(p);
        }

        private static Sprite Knob() => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

        private static RectTransform Rect(string name, Transform parent, Vector2 aMin, Vector2 aMax)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        private static Image Img(RectTransform rt, Color c)
        {
            var i = rt.gameObject.AddComponent<Image>();
            i.color = c;
            i.raycastTarget = c.a > 0.01f;
            return i;
        }

        private static TextMeshProUGUI Text(string name, RectTransform parent, string text, float size)
        {
            var rt = Rect(name, parent, Vector2.zero, Vector2.one);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.color = Color.white;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = true;
            t.raycastTarget = false;
            return t;
        }

        private static Button Button(string name, RectTransform parent, string label, Vector2 aMin, Vector2 aMax, Color c)
        {
            var rt = Rect(name, parent, aMin, aMax);
            var img = Img(rt, c);
            img.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            img.type = Image.Type.Sliced;
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            Text("Label", rt, label, 40).fontStyle = FontStyles.Bold;
            return b;
        }

        private static void Set(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogError($"[FloorTrack] {target.GetType().Name}.{field} not found"); return; }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetFloat(Object target, string field, float v) { var so = new SerializedObject(target); so.FindProperty(field).floatValue = v; so.ApplyModifiedPropertiesWithoutUndo(); }
        private static void SetVec2(Object target, string field, Vector2 v) { var so = new SerializedObject(target); so.FindProperty(field).vector2Value = v; so.ApplyModifiedPropertiesWithoutUndo(); }
        private static void SetString(Object target, string field, string v) { var so = new SerializedObject(target); so.FindProperty(field).stringValue = v; so.ApplyModifiedPropertiesWithoutUndo(); }

        private static void SetArray(Object target, string field, Object[] values)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            p.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddToBuild(string path)
        {
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == path);
            list.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}
#endif
