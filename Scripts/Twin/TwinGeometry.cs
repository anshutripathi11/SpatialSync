using UnityEngine;

namespace FloorTrack.Twin
{
    /// <summary>
    /// Plan pixels ↔ Unity world, and the photo camera model used to project photos onto walls.
    /// World: entrance at the origin, +Z = image-up, +X = image-right, 1 unit = 1 m, floor at Y = 0.
    /// </summary>
    public class TwinGeometry
    {
        public readonly float PxPerM;
        public readonly Vector2 EntrancePx;

        public TwinGeometry(TwinStateDto s)
        {
            PxPerM = Mathf.Max(0.001f, s.px_per_m);
            EntrancePx = s.entrance != null && s.entrance.Length >= 2 ? new Vector2(s.entrance[0], s.entrance[1]) : Vector2.zero;
        }

        public Vector3 PxToWorld(float x, float y, float height = 0f) =>
            new Vector3((x - EntrancePx.x) / PxPerM, height, -(y - EntrancePx.y) / PxPerM);

        public Vector3 PxToWorld(float[] p, float height = 0f) => PxToWorld(p[0], p[1], height);
    }

    /// <summary>Pinhole model of the phone camera at capture time (roll assumed 0: phone held upright).</summary>
    public readonly struct PhotoCamera
    {
        public readonly Vector3 Position, Forward, Right, Up;
        public readonly float TanH, TanV;

        public PhotoCamera(PhotoDto p, TwinGeometry g)
        {
            Position = g.PxToWorld(p.x, p.y, p.height_m);
            float yaw = p.heading_deg * Mathf.Deg2Rad, pitch = p.pitch_deg * Mathf.Deg2Rad;
            Forward = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(yaw) * Mathf.Cos(pitch));
            Right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            Up = Vector3.Cross(Forward, Right);
            TanH = Mathf.Tan(p.hfov_deg * 0.5f * Mathf.Deg2Rad);
            TanV = Mathf.Tan(p.vfov_deg * 0.5f * Mathf.Deg2Rad);
        }

        /// <summary>World point → photo UV (0..1, v up). Returns false if behind the camera.</summary>
        public bool Project(Vector3 world, out Vector2 uv)
        {
            Vector3 d = world - Position;
            float z = Vector3.Dot(d, Forward);
            if (z <= 0.01f) { uv = default; return false; }
            uv = new Vector2(0.5f + 0.5f * Vector3.Dot(d, Right) / (z * TanH),
                             0.5f + 0.5f * Vector3.Dot(d, Up) / (z * TanV));
            return true;
        }

        /// <summary>
        /// Height range [yMin, yMax] of a vertical line at floor point (x,z) that is inside the photo
        /// vertically (v in 0..1). Solves the projection equation for v = 0 and v = 1 in closed form.
        /// </summary>
        public bool VerticalRange(Vector3 floorPoint, float wallHeight, out float yMin, out float yMax)
        {
            Vector3 d0 = new Vector3(floorPoint.x - Position.x, 0f, floorPoint.z - Position.z);
            float z0 = Vector3.Dot(d0, Forward) - Position.y * Forward.y;   // z at height 0
            float y0 = Vector3.Dot(d0, Up) - Position.y * Up.y;             // y-cam at height 0
            // At height h: z = z0 + h*F.y, ycam = y0 + h*U.y. v=k <=> ycam = m*z, m = (2k-1)*TanV
            float Solve(float k)
            {
                float m = (2f * k - 1f) * TanV;
                float den = Up.y - m * Forward.y;
                return Mathf.Abs(den) < 1e-6f ? float.NaN : (m * z0 - y0) / den;
            }
            float h0 = Solve(0f), h1 = Solve(1f);
            if (float.IsNaN(h0) || float.IsNaN(h1)) { yMin = yMax = 0; return false; }
            yMin = Mathf.Clamp(Mathf.Min(h0, h1), 0f, wallHeight);
            yMax = Mathf.Clamp(Mathf.Max(h0, h1), 0f, wallHeight);
            return yMax - yMin > 0.02f;
        }
    }
}
