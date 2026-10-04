using System.Collections.Generic;
using UnityEngine;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 3 — Live path tracer.
    ///
    /// Drops a breadcrumb every <see cref="minSpacingMeters"/> of real walking and feeds them to a UILineGraphic.
    /// Breadcrumbs are stored in METRES (calibrated frame), not pixels, so if you fix the map scale or move the
    /// entrance point after walking, the whole trail is re-projected correctly.
    /// With liveTip on, the last vertex of the line is glued to the marker so the trail never lags behind it.
    /// </summary>
    public class PathTracer : MonoBehaviour
    {
        [SerializeField] private MapPoseBridge bridge;
        [SerializeField] private UILineGraphic line;

        [Header("Sampling")]
        [Tooltip("Distance between breadcrumbs. 0.2–0.5 m smooths out hand jitter while keeping corners.")]
        [SerializeField, Min(0.02f)] private float minSpacingMeters = 0.3f;
        [Tooltip("A jump bigger than this between frames (SLAM relocalisation) starts a new stroke instead of drawing a straight line through walls.")]
        [SerializeField, Min(0.5f)] private float breakOnJumpMeters = 3f;
        [SerializeField] private int maxPoints = 10000;

        [Header("Behaviour")]
        [SerializeField] private bool clearOnCalibrate = true;
        [SerializeField] private bool liveTip = true;

        private readonly List<List<Vector2>> strokesMeters = new List<List<Vector2>>();
        private Vector2 lastCommitted;
        private Vector2 lastSeen;
        private bool hasPoint;
        private bool wasTracking = true;
        private int totalPoints;

        /// <summary>Total walked distance in metres (sum of breadcrumb segments).</summary>
        public float DistanceWalked { get; private set; }

        private void Awake()
        {
            if (line != null) UIRectUtil.MatchParent(line.rectTransform);
        }

        private void OnEnable()
        {
            if (bridge == null) return;
            bridge.MapPoseUpdated += OnPose;
            bridge.Calibrated += OnCalibrated;
        }

        private void Start()
        {
            // Projection is resolved in the bridge's Awake, so subscribe here.
            if (bridge != null && bridge.Projection != null) bridge.Projection.Changed += Rebuild;
        }

        private void OnDisable()
        {
            if (bridge == null) return;
            bridge.MapPoseUpdated -= OnPose;
            bridge.Calibrated -= OnCalibrated;
        }

        private void OnDestroy()
        {
            if (bridge != null && bridge.Projection != null) bridge.Projection.Changed -= Rebuild;
        }

        public void Clear()
        {
            strokesMeters.Clear();
            hasPoint = false;
            totalPoints = 0;
            DistanceWalked = 0f;
            if (line != null) line.Clear();
        }

        private void OnCalibrated()
        {
            if (clearOnCalibrate) Clear();
            hasPoint = false; // never connect paths from two different calibrations
        }

        private void OnPose(MapPose pose)
        {
            if (line == null) return;
            if (!pose.IsTracking) { wasTracking = false; return; }

            Vector2 m = pose.Meters;
            bool jumped = hasPoint && Vector2.Distance(m, lastSeen) > breakOnJumpMeters;
            lastSeen = m;

            // Start a fresh stroke: first point ever, after tracking loss, or after a relocalisation jump.
            if (!hasPoint || !wasTracking || jumped)
            {
                strokesMeters.Add(new List<Vector2>());
                line.BeginStroke();
                CommitPoint(m);
                if (liveTip) line.AddPoint(Project(m)); // tip vertex
                hasPoint = true;
                wasTracking = true;
                return;
            }

            float d = Vector2.Distance(m, lastCommitted);
            if (d >= minSpacingMeters && totalPoints < maxPoints)
            {
                DistanceWalked += d;
                if (liveTip)
                {
                    // Freeze the tip here as a real breadcrumb, then add a new tip on top of it.
                    line.SetLastPoint(Project(m));
                    strokesMeters[strokesMeters.Count - 1].Add(m);
                    lastCommitted = m;
                    totalPoints++;
                    line.AddPoint(Project(m));
                }
                else
                {
                    CommitPoint(m);
                }
            }
            else if (liveTip)
            {
                line.SetLastPoint(pose.Position);
            }
        }

        private void CommitPoint(Vector2 meters)
        {
            strokesMeters[strokesMeters.Count - 1].Add(meters);
            line.AddPoint(Project(meters));
            lastCommitted = meters;
            totalPoints++;
        }

        private Vector2 Project(Vector2 meters) =>
            bridge.Projection != null ? bridge.Projection.MetersToMap(meters) : meters;

        /// <summary>Re-project every breadcrumb (after a scale / entrance / image change).</summary>
        private void Rebuild()
        {
            if (line == null) return;
            line.Clear();
            foreach (var stroke in strokesMeters)
            {
                line.BeginStroke();
                foreach (var m in stroke) line.AddPoint(Project(m));
            }
            if (liveTip && hasPoint) line.AddPoint(Project(lastSeen));
        }
    }
}
