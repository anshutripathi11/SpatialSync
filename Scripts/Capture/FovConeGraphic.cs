using UnityEngine;
using UnityEngine.UI;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 4 (visual) — draws the camera's field-of-view wedge as a fan mesh.
    /// Apex at this RectTransform's pivot; opens towards heading (clockwise from up), fading with distance.
    /// The length is in blueprint units (= metres × MapUnitsPerMeter), so it scales with the map.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public class FovConeGraphic : MaskableGraphic
    {
        [SerializeField] private float headingDegrees;
        [SerializeField, Range(1f, 170f)] private float fovDegrees = 60f;
        [SerializeField, Min(1f)] private float length = 60f;
        [SerializeField, Range(2, 32)] private int segments = 12;
        [SerializeField, Range(0f, 1f)] private float edgeAlpha = 0.05f;

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false; // taps go to the pin button, not the cone
        }

        public void SetCone(float heading, float fov, float lengthUnits)
        {
            headingDegrees = heading;
            fovDegrees = Mathf.Clamp(fov, 1f, 170f);
            length = Mathf.Max(1f, lengthUnits);
            rectTransform.sizeDelta = Vector2.one * length * 2f; // keeps RectMask2D culling correct
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var v = UIVertex.simpleVert;

            Color apex = color;
            Color edge = color; edge.a *= edgeAlpha;

            v.position = Vector3.zero; v.color = apex; vh.AddVert(v);

            float start = headingDegrees - fovDegrees * 0.5f;
            for (int i = 0; i <= segments; i++)
            {
                float a = (start + fovDegrees * i / segments) * Mathf.Deg2Rad;
                v.position = new Vector3(Mathf.Sin(a), Mathf.Cos(a)) * length; // clockwise-from-up
                v.color = edge;
                vh.AddVert(v);
            }
            for (int i = 1; i <= segments; i++) vh.AddTriangle(0, i, i + 1);
        }
    }
}
