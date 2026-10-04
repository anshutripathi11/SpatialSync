using System;
using UnityEngine;

namespace FloorTrack
{
    /// <summary>
    /// Pose in the CALIBRATED physical frame (metres), produced by a pose source.
    /// Meters.x = metres to the RIGHT of where you stood when you tapped Calibrate,
    /// Meters.y = metres FORWARD of where you stood when you tapped Calibrate.
    /// YawDegrees = clockwise turn (seen from above) relative to the calibration facing, in (-180, 180].
    /// </summary>
    public readonly struct TrackedPose
    {
        public readonly Vector2 Meters;
        public readonly float YawDegrees;
        public readonly bool IsTracking;

        public TrackedPose(Vector2 meters, float yawDegrees, bool isTracking)
        {
            Meters = meters;
            YawDegrees = yawDegrees;
            IsTracking = isTracking;
        }
    }

    /// <summary>
    /// Pose on the 2D blueprint.
    /// Position is in blueprint-local UI units (relative to the blueprint RectTransform's pivot, +y = up).
    /// HeadingDegrees is clockwise from "up" on the image (0 = up, 90 = right).
    /// </summary>
    public readonly struct MapPose
    {
        public readonly Vector2 Position;
        public readonly float HeadingDegrees;
        public readonly Vector2 Meters;
        public readonly bool IsTracking;

        public MapPose(Vector2 position, float headingDegrees, Vector2 meters, bool isTracking)
        {
            Position = position;
            HeadingDegrees = headingDegrees;
            Meters = meters;
            IsTracking = isTracking;
        }
    }

    /// <summary>Anything that can report a calibrated physical pose (AR tracker, editor simulator, replay...).</summary>
    public interface IPoseSource
    {
        event Action<TrackedPose> PoseUpdated;
        event Action Calibrated;

        bool IsCalibrated { get; }
        /// <summary>True when the underlying tracker currently has a reliable pose.</summary>
        bool IsTracking { get; }
        /// <summary>Human-readable tracking state for the status bar.</summary>
        string TrackingStatus { get; }
        TrackedPose Current { get; }

        /// <summary>Treat the device's current position/facing as the new (0,0) and 0°.</summary>
        void Calibrate();
    }

    /// <summary>Converts calibrated physical coordinates into blueprint coordinates.</summary>
    public interface IMapProjection
    {
        /// <summary>Raised when scale, entrance point, or the floor plan image changes.</summary>
        event Action Changed;

        /// <summary>Entrance point in blueprint-local units.</summary>
        Vector2 ReferencePosition { get; }
        /// <summary>Entrance facing, degrees clockwise from image-up.</summary>
        float ReferenceHeading { get; }
        /// <summary>Blueprint-local UI units per real-world metre.</summary>
        float MapUnitsPerMeter { get; }

        Vector2 MetersToMap(Vector2 meters);
        float YawToMapHeading(float relativeYawDegrees);
    }

    /// <summary>Produces a photo asynchronously. Swap implementations (screenshot, CPU camera image...) freely.</summary>
    public interface IPhotoCaptureService
    {
        bool IsBusy { get; }
        void Capture(Action<Texture2D> onComplete);
    }
}
