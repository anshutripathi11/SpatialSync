using System;
using System.Collections.Generic;
using UnityEngine;

namespace FloorTrack
{
    /// <summary>
    /// Data for one captured photo. Position is stored in SOURCE-IMAGE pixels (top-left origin), which never
    /// changes when the map is resized, re-scaled, or re-calibrated — so pins survive app restarts.
    /// </summary>
    [Serializable]
    public class PinRecord
    {
        public string id;
        public Vector2 imagePixel;            // where the user stood, on the floor plan image
        public float headingDegrees;          // clockwise from image-up
        public float horizontalFovDegrees;    // camera horizontal FOV at capture
        public float verticalFovDegrees;      // camera vertical FOV at capture
        public float pitchDegrees;            // + = camera looking up
        public float cameraHeightMeters;      // camera height above the floor
        public Vector2 calibratedMeters;      // right/forward from the entrance (for reports)
        public string timestampIso;
        public string photoFileName;          // relative to the pin store folder

        [NonSerialized] public Texture2D photo; // in-memory copy (may be null if only on disk)
    }

    [Serializable]
    public class PinIndex
    {
        public List<PinRecord> pins = new List<PinRecord>();
    }
}
