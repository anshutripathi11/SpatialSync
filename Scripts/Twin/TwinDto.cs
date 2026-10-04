using System;

namespace FloorTrack.Twin
{
    // Mirrors twin/state.py TwinState.to_dict(). Field names must match the JSON exactly (JsonUtility).

    [Serializable]
    public class TwinStateDto
    {
        public int version;
        public bool unchanged;
        public string plan_file;
        public int plan_width;
        public int plan_height;
        public float px_per_m;
        public float[] entrance;
        public float entrance_heading_deg;
        public float wall_height_m;
        public WallDto[] walls;
        public PhotoDto[] photos;
    }

    [Serializable]
    public class WallDto
    {
        public string id;
        public float[] a;            // image px
        public float[] b;
        public float thickness_px;
        public float height_m;
        public string color_hex;
        public string material;
        public bool observed;
        public FeatureDto[] features;
        public CoverageDto[] coverage;
        public int rev;
    }

    [Serializable]
    public class FeatureDto
    {
        public string type;
        public string label;
        public float t0, t1;         // along the wall, 0..1 from a to b
        public float bottom_m, top_m;
        public bool cuts_wall;
        public string state;
        public string photo_id;
    }

    [Serializable]
    public class CoverageDto
    {
        public float t0, t1;
        public string photo_id;
        public float score;
    }

    [Serializable]
    public class PhotoDto
    {
        public string id;
        public float x, y;           // image px
        public float heading_deg, hfov_deg, vfov_deg, pitch_deg, height_m;
        public int width, height;
        public string status;
        public string error;
        public string[] walls_seen;
    }
}
