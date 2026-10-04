using UnityEngine;
using UnityEngine.UI;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 1 — the "You are here" dot + orientation arrow.
    /// Pure view: knows nothing about AR. Something else calls Place().
    ///
    /// Expected hierarchy:
    ///   UserMarker (this, RectTransform, CanvasGroup)
    ///     ├─ FovOrArrow (RectTransform, Image of an arrow pointing UP)   ← orientationArrow
    ///     └─ Dot (Image, circle)
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class UserMarkerView : MonoBehaviour
    {
        [SerializeField] private RectTransform orientationArrow;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private Graphic[] tintTargets;

        [Header("Look")]
        [SerializeField] private Color trackingColor = new Color(0.10f, 0.55f, 1f);
        [SerializeField] private Color lostColor = new Color(1f, 0.55f, 0.1f);
        [SerializeField, Range(0, 1)] private float uncalibratedAlpha = 0.45f;

        private RectTransform rt;

        private void Awake()
        {
            rt = (RectTransform)transform;
            // Centre anchors so localPosition is purely "offset from the blueprint pivot".
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup != null) canvasGroup.blocksRaycasts = false; // never steal taps from pins
        }

        /// <summary>Places the marker on the blueprint (local units) and rotates the arrow to the heading.</summary>
        /// <param name="headingDegrees">Clockwise from image-up.</param>
        public void Place(Vector2 blueprintLocalPosition, float headingDegrees, bool isTracking = true)
        {
            if (rt == null) rt = (RectTransform)transform;
            rt.localPosition = new Vector3(blueprintLocalPosition.x, blueprintLocalPosition.y, 0f);

            // UI rotation about Z is COUNTER-clockwise positive, our heading is clockwise -> negate.
            if (orientationArrow != null)
                orientationArrow.localEulerAngles = new Vector3(0f, 0f, -headingDegrees);

            var c = isTracking ? trackingColor : lostColor;
            if (tintTargets != null)
                foreach (var g in tintTargets) if (g != null) g.color = c;
        }

        /// <summary>Ghosted while waiting for calibration (shows where to stand and which way to face).</summary>
        public void SetCalibrated(bool calibrated)
        {
            if (canvasGroup != null) canvasGroup.alpha = calibrated ? 1f : uncalibratedAlpha;
        }

        public RectTransform RectTransform => rt != null ? rt : (RectTransform)transform;
    }
}
