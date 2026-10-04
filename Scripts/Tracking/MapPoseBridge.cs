using System;
using UnityEngine;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 2 — the 3D → 2D translation step.
    ///
    /// Listens to an IPoseSource (metres), projects through IMapProjection (blueprint units), moves the
    /// UserMarkerView, and re-broadcasts a MapPose for the path tracer and pin system.
    /// This is the single place where "physical" meets "map", so the tracker and the map never reference each other.
    /// </summary>
    public class MapPoseBridge : MonoBehaviour
    {
        [Header("Sources (drag any component implementing the interface)")]
        [Tooltip("Pose source used on device (ARPoseTracker).")]
        [SerializeField] private MonoBehaviour poseSource;
        [Tooltip("Optional pose source used only in the Editor (EditorPoseSimulator).")]
        [SerializeField] private MonoBehaviour editorPoseSource;
        [Tooltip("The BlueprintMap (implements IMapProjection).")]
        [SerializeField] private MonoBehaviour mapProjection;

        [Header("View")]
        [SerializeField] private UserMarkerView userMarker;

        public event Action<MapPose> MapPoseUpdated;
        /// <summary>Raised after the user re-zeroes at the entrance.</summary>
        public event Action Calibrated;

        public IPoseSource Source { get; private set; }
        public IMapProjection Projection { get; private set; }
        public MapPose Current { get; private set; }
        public bool HasPose => Source != null && Source.IsCalibrated;

        private void Awake()
        {
            var chosen = (Application.isEditor && editorPoseSource != null) ? editorPoseSource : poseSource;
            Source = InterfaceRef.Resolve<IPoseSource>(chosen, this);
            Projection = InterfaceRef.Resolve<IMapProjection>(mapProjection, this);
        }

        private void OnEnable()
        {
            if (Source != null) { Source.PoseUpdated += HandlePose; Source.Calibrated += HandleCalibrated; }
            if (Projection != null) Projection.Changed += Refresh;
        }

        private void OnDisable()
        {
            if (Source != null) { Source.PoseUpdated -= HandlePose; Source.Calibrated -= HandleCalibrated; }
            if (Projection != null) Projection.Changed -= Refresh;
        }

        private void Start() => Refresh();

        /// <summary>Convenience for UI buttons.</summary>
        public void Calibrate() => Source?.Calibrate();

        private void HandleCalibrated()
        {
            if (userMarker != null) userMarker.SetCalibrated(true);
            Calibrated?.Invoke();
        }

        private void HandlePose(TrackedPose pose)
        {
            if (Projection == null) return;

            Vector2 mapPos = Projection.MetersToMap(pose.Meters);
            float mapHeading = Projection.YawToMapHeading(pose.YawDegrees);

            Current = new MapPose(mapPos, mapHeading, pose.Meters, pose.IsTracking);
            if (userMarker != null) userMarker.Place(mapPos, mapHeading, pose.IsTracking);
            MapPoseUpdated?.Invoke(Current);
        }

        /// <summary>Re-project after the map scale/entrance/image changes. Before calibration, shows a ghost marker
        /// at the entrance pointing the way the user must face.</summary>
        private void Refresh()
        {
            if (Projection == null) return;
            if (HasPose)
            {
                HandlePose(Source.Current);
            }
            else if (userMarker != null)
            {
                userMarker.Place(Projection.ReferencePosition, Projection.ReferenceHeading, true);
                userMarker.SetCalibrated(false);
            }
        }
    }
}
