using UnityEngine;

namespace FloorTrack
{
    /// <summary>
    /// Keeps icons (user dot, pin icons) the same size on screen while the map is pinch-zoomed.
    /// Put it on the VISUAL child only (not on things whose size means metres, like the FOV cone).
    /// </summary>
    public class ConstantScreenSize : MonoBehaviour
    {
        private MapPanZoom panZoom;

        private void Awake() => panZoom = GetComponentInParent<MapPanZoom>();

        private void LateUpdate()
        {
            if (panZoom == null) return;
            float s = 1f / Mathf.Max(0.0001f, panZoom.Zoom);
            transform.localScale = new Vector3(s, s, 1f);
        }
    }
}
