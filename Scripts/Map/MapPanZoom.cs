using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FloorTrack
{
    /// <summary>
    /// Optional quality-of-life: one-finger pan, two-finger pinch zoom, mouse-wheel zoom, and auto-follow of
    /// the user marker. Works with both the old and new Input System because it only uses EventSystem events.
    ///
    /// Put this on the map VIEWPORT (a RectTransform with RectMask2D + a transparent Image so it receives raycasts).
    /// "content" is the BlueprintMap RectTransform (child of the viewport, anchors centred, pivot 0.5/0.5).
    /// Do NOT also add a ScrollRect.
    /// </summary>
    public class MapPanZoom : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IScrollHandler
    {
        [SerializeField] private RectTransform content;
        [SerializeField] private float minZoom = 0.2f;
        [SerializeField] private float maxZoom = 8f;
        [SerializeField] private float mouseWheelStep = 0.1f;

        [Header("Follow user")]
        [SerializeField] private RectTransform followTarget;
        [SerializeField] private bool follow = true;
        [SerializeField] private float followSharpness = 6f;
        [Tooltip("Seconds after the last manual pan/zoom before auto-follow resumes.")]
        [SerializeField] private float resumeFollowAfter = 4f;

        private RectTransform viewport;
        private readonly Dictionary<int, Vector2> pointers = new Dictionary<int, Vector2>();
        private float lastInteractionTime = -999f;

        public float Zoom => content != null ? content.localScale.x : 1f;
        public bool Follow { get => follow; set => follow = value; }

        private void Awake()
        {
            viewport = (RectTransform)transform;
            if (content != null) content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f);
        }

        public void OnBeginDrag(PointerEventData e)
        {
            pointers[e.pointerId] = e.position;
            lastInteractionTime = Time.unscaledTime;
        }

        public void OnDrag(PointerEventData e)
        {
            lastInteractionTime = Time.unscaledTime;
            Vector2 previous = pointers.TryGetValue(e.pointerId, out var p) ? p : e.position - e.delta;
            Vector2 current = e.position;
            var cam = e.pressEventCamera;

            if (pointers.Count >= 2)
            {
                // Pinch: find the other finger.
                Vector2 other = current;
                foreach (var kv in pointers) if (kv.Key != e.pointerId) { other = kv.Value; break; }

                float oldDist = Vector2.Distance(previous, other);
                float newDist = Vector2.Distance(current, other);
                if (oldDist > 1f) ZoomAround((current + other) * 0.5f, newDist / oldDist, cam);
                PanByScreenDelta((previous + other) * 0.5f, (current + other) * 0.5f, cam);
            }
            else
            {
                PanByScreenDelta(previous, current, cam);
            }
            pointers[e.pointerId] = current;
        }

        public void OnEndDrag(PointerEventData e) => pointers.Remove(e.pointerId);

        public void OnScroll(PointerEventData e)
        {
            lastInteractionTime = Time.unscaledTime;
            ZoomAround(e.position, 1f + e.scrollDelta.y * mouseWheelStep, e.enterEventCamera);
        }

        /// <summary>Instantly centre the view on a blueprint-local point.</summary>
        public void CenterOn(Vector2 blueprintLocal)
        {
            content.localPosition = -(Vector3)(blueprintLocal * Zoom);
        }

        private void LateUpdate()
        {
            if (!follow || followTarget == null || content == null) return;
            if (Time.unscaledTime - lastInteractionTime < resumeFollowAfter) return;

            // Want: contentPos + targetLocal * zoom == viewport centre (0,0)
            Vector3 goal = -(Vector3)((Vector2)followTarget.localPosition * Zoom);
            float t = 1f - Mathf.Exp(-followSharpness * Time.unscaledDeltaTime);
            content.localPosition = Vector3.Lerp(content.localPosition, goal, t);
        }

        private void PanByScreenDelta(Vector2 fromScreen, Vector2 toScreen, Camera cam)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, fromScreen, cam, out var a)) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, toScreen, cam, out var b)) return;
            content.localPosition += (Vector3)(b - a);
        }

        private void ZoomAround(Vector2 screenPoint, float factor, Camera cam)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, screenPoint, cam, out var f)) return;

            float oldZoom = Zoom;
            float newZoom = Mathf.Clamp(oldZoom * factor, minZoom, maxZoom);
            if (Mathf.Approximately(oldZoom, newZoom)) return;

            // Keep the blueprint point under the finger fixed: c = (f - P)/s  ->  P' = f - c*s'
            Vector2 P = content.localPosition;
            Vector2 c = (f - P) / oldZoom;
            content.localScale = new Vector3(newZoom, newZoom, 1f);
            content.localPosition = f - c * newZoom;
        }
    }
}
