using System;
using UnityEngine;
using UnityEngine.UI;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 1 — Blueprint overlay & scale calibration.
    ///
    /// Owns the floor plan image and the two numbers that tie it to the real world:
    ///   • MetersToPixelsScale — how many SOURCE-IMAGE pixels equal 1 m.
    ///   • The entrance point (image pixel) + the direction the user faces there.
    /// Everything that draws on the map (marker, path, pins) is parented under this RectTransform and works
    /// in its local coordinates, so pan/zoom of the map moves everything together for free.
    ///
    /// Put this on the GameObject that has the floor plan Image.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public class BlueprintMap : MonoBehaviour, IMapProjection
    {
        [Header("Blueprint image")]
        [SerializeField] private Image blueprintImage;
        [Tooltip("Optional: assign a floor plan sprite here; otherwise call SetFloorPlan(texture) at runtime.")]
        [SerializeField] private Sprite floorPlan;
        [Tooltip("Resize the RectTransform to the image's pixel size so 1 UI unit = 1 image pixel (recommended).")]
        [SerializeField] private bool useNativeSize = true;

        [Header("Scale calibration")]
        [Tooltip("How many pixels of the SOURCE IMAGE equal 1 metre in the real world.")]
        [SerializeField, Min(0.0001f)] private float metersToPixelsScale = 20f;

        [Header("Reference point (where the user calibrates)")]
        [Tooltip("Entrance position in source-image pixels, measured from the TOP-LEFT corner (as shown in Photoshop/GIMP/Paint).")]
        [SerializeField] private Vector2 entranceImagePixel = new Vector2(100, 900);
        [Tooltip("Direction the user faces at the entrance when tapping Calibrate. Degrees clockwise from image-up (0 = up, 90 = right).")]
        [SerializeField, Range(-180f, 180f)] private float entranceHeadingDegrees = 0f;

        public event Action Changed;

        public RectTransform Rect => (RectTransform)transform;
        public Image Image => blueprintImage;

        public float MetersToPixelsScale
        {
            get => metersToPixelsScale;
            set { metersToPixelsScale = Mathf.Max(0.0001f, value); RaiseChanged(); }
        }

        /// <summary>Pixel size of the floor plan source image.</summary>
        public Vector2 ImageSizePixels
        {
            get
            {
                var s = blueprintImage != null ? blueprintImage.sprite : null;
                return s != null ? s.rect.size : Rect.rect.size;
            }
        }

        /// <summary>UI units per source-image pixel (1 when useNativeSize is on).</summary>
        public float UnitsPerImagePixel
        {
            get
            {
                var img = ImageSizePixels;
                return img.x > 0f ? Rect.rect.width / img.x : 1f;
            }
        }

        // ---------- IMapProjection ----------
        public float MapUnitsPerMeter => metersToPixelsScale * UnitsPerImagePixel;
        public Vector2 ReferencePosition => ImagePixelToLocal(entranceImagePixel);
        public float ReferenceHeading => entranceHeadingDegrees;

        public Vector2 MetersToMap(Vector2 meters) =>
            ReferencePosition + FloorMath.CalibratedMetersToMapOffset(meters, entranceHeadingDegrees, MapUnitsPerMeter);

        public float YawToMapHeading(float relativeYawDegrees) =>
            FloorMath.NormalizeAngle(entranceHeadingDegrees + relativeYawDegrees);

        // ---------- Lifecycle ----------
        private void Reset() => blueprintImage = GetComponent<Image>();

        private void Awake()
        {
            if (blueprintImage == null) blueprintImage = GetComponent<Image>();
            if (floorPlan != null) ApplySprite(floorPlan);
        }

        // ---------- Public API ----------

        /// <summary>Load a floor plan at runtime (e.g. picked from the gallery or downloaded).</summary>
        public void SetFloorPlan(Texture2D texture)
        {
            if (texture == null) return;
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = texture.name;
            ApplySprite(sprite);
        }

        public void SetEntrance(Vector2 imagePixel, float headingDegrees)
        {
            entranceImagePixel = imagePixel;
            entranceHeadingDegrees = FloorMath.NormalizeAngle(headingDegrees);
            RaiseChanged();
        }

        /// <summary>
        /// Scale calibration helper: measure two points on the plan whose real distance you know
        /// (e.g. both ends of a 10 m corridor, or a 0.9 m door) and pass them here.
        /// </summary>
        public void SetScaleFromTwoPoints(Vector2 imagePixelA, Vector2 imagePixelB, float realDistanceMeters)
        {
            if (realDistanceMeters <= 0f) return;
            MetersToPixelsScale = Vector2.Distance(imagePixelA, imagePixelB) / realDistanceMeters;
        }

        /// <summary>Source-image pixel (top-left origin) -> blueprint-local UI position (pivot origin, y up).</summary>
        public Vector2 ImagePixelToLocal(Vector2 imagePixel)
        {
            var r = Rect.rect;
            var img = ImageSizePixels;
            float u = img.x > 0 ? imagePixel.x / img.x : 0f;
            float v = img.y > 0 ? 1f - imagePixel.y / img.y : 0f; // flip: image y goes down, UI y goes up
            return new Vector2(r.x + u * r.width, r.y + v * r.height);
        }

        /// <summary>Blueprint-local UI position -> source-image pixel (top-left origin).</summary>
        public Vector2 LocalToImagePixel(Vector2 local)
        {
            var r = Rect.rect;
            var img = ImageSizePixels;
            float u = r.width > 0 ? (local.x - r.x) / r.width : 0f;
            float v = r.height > 0 ? (local.y - r.y) / r.height : 0f;
            return new Vector2(u * img.x, (1f - v) * img.y);
        }

        /// <summary>Screen point (e.g. a tap) -> blueprint-local position. Handy for tap-to-place-entrance tools.</summary>
        public bool ScreenToLocal(Vector2 screenPoint, Camera uiCamera, out Vector2 local) =>
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Rect, screenPoint, uiCamera, out local);

        // ---------- Internals ----------
        private void ApplySprite(Sprite sprite)
        {
            floorPlan = sprite;
            blueprintImage.sprite = sprite;
            blueprintImage.preserveAspect = true;
            if (useNativeSize) blueprintImage.SetNativeSize();
            RaiseChanged();
        }

        private void RaiseChanged() => Changed?.Invoke();

        private void OnValidate()
        {
            if (Application.isPlaying) RaiseChanged();
        }

#if UNITY_EDITOR
        // Scene-view helper: shows the entrance point, the calibration facing, and a 1 m / 5 m scale bar.
        private void OnDrawGizmosSelected()
        {
            if (blueprintImage == null) blueprintImage = GetComponent<Image>();
            var p = ReferencePosition;
            float m = MapUnitsPerMeter;
            float h = entranceHeadingDegrees * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Sin(h), Mathf.Cos(h));

            Gizmos.color = Color.green;
            Vector3 a = transform.TransformPoint(p);
            Vector3 b = transform.TransformPoint(p + dir * m * 2f);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawWireSphere(a, transform.lossyScale.x * m * 0.3f);

            Gizmos.color = Color.cyan;
            var r = Rect.rect;
            var s0 = new Vector2(r.xMin + 20f, r.yMin + 20f);
            Gizmos.DrawLine(transform.TransformPoint(s0), transform.TransformPoint(s0 + Vector2.right * m * 5f));
            Gizmos.DrawLine(transform.TransformPoint(s0 + Vector2.up * 6f), transform.TransformPoint(s0 + Vector2.right * m + Vector2.up * 6f));
        }
#endif
    }
}
