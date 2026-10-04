using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 3 (renderer) — a uGUI Graphic that draws one or more polylines as a single mesh.
    /// Lives inside the Canvas (unlike LineRenderer), so it is masked, sorted and pan/zoomed with the map.
    /// Points are in this RectTransform's local space (which equals blueprint-local space because the
    /// path layer is stretched over the blueprint with the same pivot).
    /// Multiple "strokes" let the trail break cleanly when tracking is lost instead of drawing a fake jump.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class UILineGraphic : MaskableGraphic
    {
        [SerializeField, Min(0.5f)] private float thickness = 6f;
        [Tooltip("Limits spikes at sharp turns (e.g. walking back the way you came).")]
        [SerializeField, Range(0.1f, 1f)] private float miterLimit = 0.4f;

        private readonly List<List<Vector2>> strokes = new List<List<Vector2>>();

        public float Thickness
        {
            get => thickness;
            set { thickness = Mathf.Max(0.5f, value); SetVerticesDirty(); }
        }

        public int StrokeCount => strokes.Count;

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        public void Clear()
        {
            strokes.Clear();
            SetVerticesDirty();
        }

        public void BeginStroke()
        {
            if (strokes.Count > 0 && strokes[strokes.Count - 1].Count == 0) return;
            strokes.Add(new List<Vector2>());
        }

        public void AddPoint(Vector2 p)
        {
            if (strokes.Count == 0) BeginStroke();
            strokes[strokes.Count - 1].Add(p);
            SetVerticesDirty();
        }

        /// <summary>Replace the last point (used for the live tip that follows the marker).</summary>
        public void SetLastPoint(Vector2 p)
        {
            if (strokes.Count == 0 || strokes[strokes.Count - 1].Count == 0) { AddPoint(p); return; }
            var s = strokes[strokes.Count - 1];
            s[s.Count - 1] = p;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            foreach (var stroke in strokes) DrawStroke(vh, stroke);
        }

        private void DrawStroke(VertexHelper vh, List<Vector2> pts)
        {
            int n = pts.Count;
            if (n < 2) return;

            float hw = thickness * 0.5f;
            int start = vh.currentVertCount;
            var vert = UIVertex.simpleVert;
            vert.color = color;

            for (int i = 0; i < n; i++)
            {
                Vector2 dirIn = i > 0 ? (pts[i] - pts[i - 1]).normalized : Vector2.zero;
                Vector2 dirOut = i < n - 1 ? (pts[i + 1] - pts[i]).normalized : Vector2.zero;
                Vector2 refDir = dirIn != Vector2.zero ? dirIn : dirOut;
                if (refDir == Vector2.zero) refDir = Vector2.up;

                Vector2 tangent = dirIn + dirOut;
                if (tangent.sqrMagnitude < 1e-6f) tangent = refDir;
                tangent.Normalize();

                // Miter join: offset along the averaged normal, lengthened so the edge stays parallel.
                Vector2 normal = new Vector2(-tangent.y, tangent.x);
                float cos = Vector2.Dot(normal, new Vector2(-refDir.y, refDir.x));
                float offset = hw / Mathf.Max(cos, miterLimit);

                vert.position = pts[i] + normal * offset; vh.AddVert(vert);
                vert.position = pts[i] - normal * offset; vh.AddVert(vert);
            }

            for (int i = 0; i < n - 1; i++)
            {
                int k = start + i * 2;
                vh.AddTriangle(k, k + 2, k + 1);
                vh.AddTriangle(k + 1, k + 2, k + 3);
            }
        }
    }
}
