using UnityEngine;

namespace FloorTrack
{
    /// <summary>
    /// Unity can't serialize interface fields, so components expose a MonoBehaviour slot and resolve it here.
    /// Lets you drag ANY component that implements the interface (e.g. ARPoseTracker or EditorPoseSimulator).
    /// </summary>
    public static class InterfaceRef
    {
        public static T Resolve<T>(Component candidate, Object context) where T : class
        {
            if (candidate == null) return null;
            if (candidate is T direct) return direct;

            var sibling = candidate.GetComponent<T>();
            if (sibling != null) return sibling;

            Debug.LogError($"[FloorTrack] '{candidate.name}' does not implement {typeof(T).Name}.", context);
            return null;
        }
    }

    public static class UIRectUtil
    {
        /// <summary>
        /// Makes a child RectTransform cover its parent exactly with the same pivot, so that
        /// child-local coordinates == parent-local coordinates. Every overlay layer on the map uses this.
        /// </summary>
        public static void MatchParent(RectTransform rt)
        {
            var parent = rt.parent as RectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            if (parent != null) rt.pivot = parent.pivot;
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;
        }
    }
}
