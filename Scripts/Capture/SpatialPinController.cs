using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 4 — Capture & spatial pinning engine.
    ///
    /// Capture button → snapshot the current map pose + camera FOV → take photo → drop a PinView at that spot
    /// with a FOV cone → tapping the pin opens PhotoModal. Photos and an index JSON are written to
    /// Application.persistentDataPath/FloorTrack/&lt;storageKey&gt;/ so pins reload on next launch.
    /// </summary>
    public class SpatialPinController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private MapPoseBridge bridge;
        [SerializeField] private BlueprintMap map;
        [Tooltip("Component implementing IPhotoCaptureService (e.g. ScreenshotCaptureService).")]
        [SerializeField] private MonoBehaviour captureService;
        [Tooltip("The AR camera, used to read the real horizontal field of view.")]
        [SerializeField] private Camera arCamera;

        [Header("UI")]
        [SerializeField] private Button captureButton;
        [SerializeField] private RectTransform pinLayer;
        [SerializeField] private PinView pinPrefab;
        [SerializeField] private PhotoModal photoModal;

        [Header("Settings")]
        [Tooltip("How far (in real metres) the FOV wedge reaches on the map.")]
        [SerializeField, Min(0.1f)] private float coneLengthMeters = 2.5f;
        [SerializeField] private bool saveToDisk = true;
        [SerializeField] private bool restoreOnStart = true;
        [Tooltip("Folder name; use one per floor plan.")]
        [SerializeField] private string storageKey = "default-floor";
        [SerializeField, Range(30, 100)] private int jpgQuality = 85;
        [Tooltip("How high the phone is held when tapping Calibrate; used to find the floor height for 3D reconstruction.")]
        [SerializeField, Range(0.8f, 2f)] private float calibrationEyeHeight = 1.4f;

        public event Action<PinRecord> PinDropped;
        public IReadOnlyList<PinRecord> Pins => pins;

        private readonly List<PinRecord> pins = new List<PinRecord>();
        private readonly List<PinView> views = new List<PinView>();
        private IPhotoCaptureService capture;
        private Texture2D openedFromDisk; // cache of the one photo currently loaded lazily
        private float floorY;             // world Y of the floor, set at calibration

        private string Folder => Path.Combine(Application.persistentDataPath, "FloorTrack", storageKey);
        private string IndexPath => Path.Combine(Folder, "pins.json");
        private float ConeLengthUnits => coneLengthMeters * map.MapUnitsPerMeter;

        private void Awake()
        {
            capture = InterfaceRef.Resolve<IPhotoCaptureService>(captureService, this);
            if (pinLayer != null) UIRectUtil.MatchParent(pinLayer);
        }

        private void OnEnable()
        {
            if (captureButton != null) captureButton.onClick.AddListener(Capture);
            if (map != null) map.Changed += RepositionAll;
            if (bridge != null) bridge.Calibrated += OnCalibrated;
        }

        private void OnCalibrated()
        {
            if (arCamera != null) floorY = arCamera.transform.position.y - calibrationEyeHeight;
        }

        private void OnDisable()
        {
            if (captureButton != null) captureButton.onClick.RemoveListener(Capture);
            if (map != null) map.Changed -= RepositionAll;
            if (bridge != null) bridge.Calibrated -= OnCalibrated;
        }

        private void Start()
        {
            if (restoreOnStart && saveToDisk) LoadFromDisk();
        }

        private void Update()
        {
            if (captureButton != null)
                captureButton.interactable = bridge != null && bridge.HasPose && bridge.Current.IsTracking
                                             && capture != null && !capture.IsBusy;
        }

        /// <summary>Wire this to the Capture button (done automatically if captureButton is assigned).</summary>
        public void Capture()
        {
            if (bridge == null || !bridge.HasPose || capture == null || capture.IsBusy) return;

            // Snapshot pose NOW — the photo arrives a frame later and the user may have moved slightly.
            MapPose pose = bridge.Current;
            float hFov = FloorMath.HorizontalFovDegrees(arCamera);
            float vFov = FloorMath.VerticalFovDegrees(arCamera);
            float pitch = arCamera != null ? FloorMath.PitchDegrees(arCamera.transform.forward) : 0f;
            float height = arCamera != null ? Mathf.Clamp(arCamera.transform.position.y - floorY, 0.2f, 2.5f)
                                            : calibrationEyeHeight;

            capture.Capture(photo => OnPhotoReady(photo, pose, hFov, vFov, pitch, height));
        }

        private void OnPhotoReady(Texture2D photo, MapPose pose, float hFov, float vFov, float pitch, float height)
        {
            var record = new PinRecord
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 8),
                imagePixel = map.LocalToImagePixel(pose.Position),
                headingDegrees = pose.HeadingDegrees,
                horizontalFovDegrees = hFov,
                verticalFovDegrees = vFov,
                pitchDegrees = pitch,
                cameraHeightMeters = height,
                calibratedMeters = pose.Meters,
                timestampIso = DateTime.Now.ToString("s"),
                photo = photo
            };

            if (saveToDisk) SavePhoto(record);
            pins.Add(record);
            Spawn(record);
            if (saveToDisk) SaveIndex();

            PinDropped?.Invoke(record);
        }

        private void Spawn(PinRecord record)
        {
            var view = Instantiate(pinPrefab, pinLayer);
            view.name = $"Pin_{record.id}";
            view.Bind(record, map.ImagePixelToLocal(record.imagePixel), ConeLengthUnits, OpenPin);
            views.Add(view);
        }

        private void OpenPin(PinRecord record)
        {
            if (photoModal == null) return;
            Texture2D tex = record.photo != null ? record.photo : LoadPhoto(record);
            string caption =
                $"{DateTime.Parse(record.timestampIso):MMM d, HH:mm:ss}\n" +
                $"{record.calibratedMeters.x:0.0} m right, {record.calibratedMeters.y:0.0} m forward of entrance · " +
                $"facing {Mathf.Repeat(record.headingDegrees, 360f):0}° (map)";
            photoModal.Show(tex, caption);
        }

        private void RepositionAll()
        {
            foreach (var v in views)
                if (v != null) v.SetPosition(map.ImagePixelToLocal(v.Record.imagePixel), ConeLengthUnits);
        }

        public void ClearAll(bool deleteFiles = false)
        {
            foreach (var v in views) if (v != null) Destroy(v.gameObject);
            views.Clear();
            foreach (var p in pins) if (p.photo != null) Destroy(p.photo);
            pins.Clear();
            if (deleteFiles && Directory.Exists(Folder)) Directory.Delete(Folder, true);
        }

        // ---------- Persistence ----------
        private void SavePhoto(PinRecord record)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                record.photoFileName = $"{record.id}.jpg";
                File.WriteAllBytes(Path.Combine(Folder, record.photoFileName), record.photo.EncodeToJPG(jpgQuality));

                // Saved safely -> free the RAM copy; it is reloaded on tap. Keeps memory flat for 100s of pins.
                Destroy(record.photo);
                record.photo = null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FloorTrack] Could not save photo: {e.Message}. Keeping it in memory.");
            }
        }

        /// <summary>JPEG bytes of a pin's photo (from memory or disk). Used by CaptureUploader.</summary>
        public byte[] GetPhotoBytes(PinRecord record)
        {
            if (record.photo != null) return record.photo.EncodeToJPG(jpgQuality);
            if (string.IsNullOrEmpty(record.photoFileName)) return null;
            string path = Path.Combine(Folder, record.photoFileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private Texture2D LoadPhoto(PinRecord record)
        {
            if (string.IsNullOrEmpty(record.photoFileName)) return null;
            string path = Path.Combine(Folder, record.photoFileName);
            if (!File.Exists(path)) return null;

            if (openedFromDisk != null) Destroy(openedFromDisk);
            openedFromDisk = new Texture2D(2, 2, TextureFormat.RGB24, false);
            openedFromDisk.LoadImage(File.ReadAllBytes(path));
            return openedFromDisk;
        }

        private void SaveIndex()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var index = new PinIndex { pins = new List<PinRecord>(pins) };
                File.WriteAllText(IndexPath, JsonUtility.ToJson(index, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FloorTrack] Could not save pin index: {e.Message}");
            }
        }

        private void LoadFromDisk()
        {
            if (!File.Exists(IndexPath)) return;
            var index = JsonUtility.FromJson<PinIndex>(File.ReadAllText(IndexPath));
            if (index?.pins == null) return;
            foreach (var r in index.pins)
            {
                pins.Add(r);
                Spawn(r);
            }
        }
    }
}
