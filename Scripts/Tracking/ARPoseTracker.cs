using System;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 2 — AR Foundation alignment & tracking engine (the physical side).
    ///
    /// Attach to the XR Origin (formerly "AR Session Origin"). Every frame it reads the AR camera pose
    /// (X/Z position and Y-axis yaw), expresses it relative to the pose captured when the user tapped
    /// Calibrate, and publishes a TrackedPose in metres/degrees.
    ///
    /// WHY a software origin instead of moving the XR Origin / calling ARSession.Reset()?
    ///   Resetting the session throws away the SLAM map and takes seconds to re-initialise, and physically
    ///   moving the XR Origin fights with anything else in the scene. Storing "origin position + origin yaw"
    ///   and subtracting it is mathematically identical, instant, and can be redone any number of times.
    /// </summary>
    [DisallowMultipleComponent]
    public class ARPoseTracker : MonoBehaviour, IPoseSource
    {
        [SerializeField] private XROrigin xrOrigin;
        [Tooltip("Defaults to XROrigin.Camera.")]
        [SerializeField] private Transform arCamera;
        [Tooltip("Below this flattened-forward length the phone is considered pointing at the floor and the camera's up vector is used for heading.")]
        [SerializeField, Range(0.05f, 0.6f)] private float flatForwardThreshold = 0.25f;

        public event Action<TrackedPose> PoseUpdated;
        public event Action Calibrated;

        public bool IsCalibrated { get; private set; }
        public TrackedPose Current { get; private set; }

        public bool IsTracking => ARSession.state == ARSessionState.SessionTracking;

        public string TrackingStatus
        {
            get
            {
                switch (ARSession.state)
                {
                    case ARSessionState.SessionTracking: return "Tracking";
                    case ARSessionState.Unsupported: return "AR not supported on this device";
                    case ARSessionState.NeedsInstall: return "Install / update AR services";
                    case ARSessionState.SessionInitializing:
                        return ARSession.notTrackingReason == UnityEngine.XR.ARSubsystems.NotTrackingReason.None
                            ? "Initialising… move the phone slowly"
                            : $"Initialising… ({ARSession.notTrackingReason})";
                    default: return $"AR: {ARSession.state}";
                }
            }
        }

        // Calibration origin (in XR-Origin space)
        private Vector3 originPosition;
        private float originYaw;

        private void Awake()
        {
            if (xrOrigin == null) xrOrigin = GetComponent<XROrigin>();
            if (arCamera == null && xrOrigin != null && xrOrigin.Camera != null) arCamera = xrOrigin.Camera.transform;
            if (arCamera == null && Camera.main != null) arCamera = Camera.main.transform;
        }

        /// <summary>Called by the Calibrate button while the user stands at the entrance facing the agreed direction.</summary>
        public void Calibrate()
        {
            ReadRawPose(out originPosition, out originYaw);
            IsCalibrated = true;
            Current = new TrackedPose(Vector2.zero, 0f, IsTracking);
            Calibrated?.Invoke();
            PoseUpdated?.Invoke(Current);
        }

        private void Update()
        {
            if (!IsCalibrated || arCamera == null) return;

            if (!IsTracking)
            {
                // Freeze the last good position while tracking is lost; just flag it.
                Current = new TrackedPose(Current.Meters, Current.YawDegrees, false);
                PoseUpdated?.Invoke(Current);
                return;
            }

            ReadRawPose(out var position, out var yaw);
            var meters = FloorMath.ToCalibratedMeters(originPosition, originYaw, position);
            var relYaw = FloorMath.NormalizeAngle(yaw - originYaw);

            Current = new TrackedPose(meters, relYaw, true);
            PoseUpdated?.Invoke(Current);
        }

        /// <summary>
        /// Camera pose in XR-Origin space. This is the same as reading arCamera.localPosition /
        /// localEulerAngles.y when the camera is a direct child, but also correct with the
        /// "Camera Offset" child that XR Origin adds. Height (Y) is ignored.
        /// </summary>
        private void ReadRawPose(out Vector3 position, out float yawDegrees)
        {
            Vector3 pos = arCamera.position, fwd = arCamera.forward, up = arCamera.up;
            if (xrOrigin != null)
            {
                var space = xrOrigin.transform;
                pos = space.InverseTransformPoint(pos);
                fwd = space.InverseTransformDirection(fwd);
                up = space.InverseTransformDirection(up);
            }
            position = pos;
            yawDegrees = FloorMath.YawFromCameraAxes(fwd, up, flatForwardThreshold);
        }
    }
}
