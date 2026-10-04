using UnityEngine;

namespace FloorTrack
{
    /// <summary>
    /// Pure, stateless math used by the whole system. No MonoBehaviour, so it is trivially unit-testable.
    ///
    /// Conventions
    ///   Unity world:  +X right, +Y up, +Z forward. Yaw = rotation about +Y, clockwise seen from above.
    ///                 A yaw of θ faces the direction (sin θ, 0, cos θ).
    ///   Blueprint UI: +x right, +y up (screen up). Heading = clockwise from image-up.
    ///                 A heading of H points along (sin H, cos H).
    /// Because both "clockwise from forward/up" conventions match, the same sin/cos formulas work in both spaces.
    /// </summary>
    public static class FloorMath
    {
        /// <summary>Wraps any angle into (-180, 180].</summary>
        public static float NormalizeAngle(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees <= -180f) degrees += 360f;
            return degrees;
        }

        /// <summary>
        /// Heading of the phone on the floor plane. Uses the camera's forward vector flattened onto XZ.
        /// When the phone points almost straight down (forward ≈ vertical), the flattened forward is tiny and
        /// noisy, so we fall back to the camera's UP vector, which then points where the user is walking.
        /// This is more robust than eulerAngles.y, which flips/gimbal-locks near ±90° pitch.
        /// </summary>
        public static float YawFromCameraAxes(Vector3 forward, Vector3 up, float minFlatLength = 0.2f)
        {
            var flat = new Vector2(forward.x, forward.z);
            if (flat.magnitude < minFlatLength)
            {
                // Phone looking down -> "up" of the screen points forward; looking up -> it points backward.
                var upFlat = new Vector2(up.x, up.z);
                flat = forward.y < 0f ? upFlat : -upFlat;
                if (flat.sqrMagnitude < 1e-6f) flat = new Vector2(forward.x, forward.z);
            }
            return Mathf.Atan2(flat.x, flat.y) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Converts an absolute AR position into the calibrated frame:
        /// returns (metres to the right, metres forward) relative to the calibration point and facing.
        /// This is a 2D rotation of the displacement by -originYaw.
        /// </summary>
        public static Vector2 ToCalibratedMeters(Vector3 originPosition, float originYawDegrees, Vector3 position)
        {
            float dx = position.x - originPosition.x;
            float dz = position.z - originPosition.z;
            float r = originYawDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(r), sin = Mathf.Sin(r);

            float right = dx * cos - dz * sin;   // dot(d, rightAxis),   rightAxis   = ( cos, -sin)
            float forward = dx * sin + dz * cos; // dot(d, forwardAxis), forwardAxis = ( sin,  cos)
            return new Vector2(right, forward);
        }

        /// <summary>
        /// Converts calibrated metres into a blueprint offset (UI units) relative to the entrance point.
        /// mapHeadingDegrees is the entrance facing on the image (clockwise from image-up).
        /// </summary>
        public static Vector2 CalibratedMetersToMapOffset(Vector2 meters, float mapHeadingDegrees, float unitsPerMeter)
        {
            float h = mapHeadingDegrees * Mathf.Deg2Rad;
            var forwardOnMap = new Vector2(Mathf.Sin(h), Mathf.Cos(h));
            var rightOnMap = new Vector2(Mathf.Cos(h), -Mathf.Sin(h));
            return (rightOnMap * meters.x + forwardOnMap * meters.y) * unitsPerMeter;
        }

        /// <summary>Horizontal field of view in degrees, read from the projection matrix that AR Foundation sets
        /// from the real camera intrinsics (Camera.fieldOfView is not reliable when a custom matrix is used).</summary>
        public static float HorizontalFovDegrees(Camera camera)
        {
            if (camera == null) return 60f;
            float m00 = camera.projectionMatrix.m00;
            if (m00 <= 1e-4f) return 60f;
            return 2f * Mathf.Atan(1f / m00) * Mathf.Rad2Deg;
        }

        /// <summary>Vertical field of view in degrees from the projection matrix.</summary>
        public static float VerticalFovDegrees(Camera camera)
        {
            if (camera == null) return 60f;
            float m11 = camera.projectionMatrix.m11;
            if (m11 <= 1e-4f) return 60f;
            return 2f * Mathf.Atan(1f / m11) * Mathf.Rad2Deg;
        }

        /// <summary>Camera pitch in degrees (+ = looking up) from its forward vector.</summary>
        public static float PitchDegrees(Vector3 forward) =>
            Mathf.Asin(Mathf.Clamp(forward.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
}
