using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace FloorTrack
{
    /// <summary>
    /// Fake pose source for testing the whole map/path/pin pipeline in the Editor without deploying.
    /// W/S = walk forward/back, A/D = strafe, Q/E = turn, Shift = run.
    /// Same IPoseSource contract as ARPoseTracker, so nothing else in the app knows the difference.
    /// </summary>
    public class EditorPoseSimulator : MonoBehaviour, IPoseSource
    {
        [SerializeField] private float walkSpeed = 1.4f;   // m/s, typical walking pace
        [SerializeField] private float turnSpeed = 90f;    // deg/s
        [SerializeField] private float runMultiplier = 3f;

        public event Action<TrackedPose> PoseUpdated;
        public event Action Calibrated;

        public bool IsCalibrated { get; private set; }
        public bool IsTracking => true;
        public string TrackingStatus => "Simulated (WASD + Q/E)";
        public TrackedPose Current { get; private set; }

        // "Raw" world pose, as if it came from SLAM.
        private Vector3 position;
        private float yaw = 37f; // arbitrary, to prove calibration removes it
        private Vector3 originPosition;
        private float originYaw;

        public void Calibrate()
        {
            originPosition = position;
            originYaw = yaw;
            IsCalibrated = true;
            Current = new TrackedPose(Vector2.zero, 0f, true);
            Calibrated?.Invoke();
            PoseUpdated?.Invoke(Current);
        }

        private void Update()
        {
            float speed = walkSpeed * (Held('L') ? runMultiplier : 1f);
            float fwd = (Held('W') ? 1 : 0) - (Held('S') ? 1 : 0);
            float strafe = (Held('D') ? 1 : 0) - (Held('A') ? 1 : 0);
            float turn = (Held('E') ? 1 : 0) - (Held('Q') ? 1 : 0);

            yaw = FloorMath.NormalizeAngle(yaw + turn * turnSpeed * Time.deltaTime);
            float r = yaw * Mathf.Deg2Rad;
            var forwardDir = new Vector3(Mathf.Sin(r), 0, Mathf.Cos(r));
            var rightDir = new Vector3(Mathf.Cos(r), 0, -Mathf.Sin(r));
            position += (forwardDir * fwd + rightDir * strafe) * speed * Time.deltaTime;

            if (!IsCalibrated) return;
            Current = new TrackedPose(
                FloorMath.ToCalibratedMeters(originPosition, originYaw, position),
                FloorMath.NormalizeAngle(yaw - originYaw),
                true);
            PoseUpdated?.Invoke(Current);
        }

        // 'L' = left shift. Works with either input backend.
        private static bool Held(char key)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return false;
            switch (key)
            {
                case 'W': return kb.wKey.isPressed;
                case 'A': return kb.aKey.isPressed;
                case 'S': return kb.sKey.isPressed;
                case 'D': return kb.dKey.isPressed;
                case 'Q': return kb.qKey.isPressed;
                case 'E': return kb.eKey.isPressed;
                case 'L': return kb.leftShiftKey.isPressed;
            }
            return false;
#elif ENABLE_LEGACY_INPUT_MANAGER
            switch (key)
            {
                case 'W': return Input.GetKey(KeyCode.W);
                case 'A': return Input.GetKey(KeyCode.A);
                case 'S': return Input.GetKey(KeyCode.S);
                case 'D': return Input.GetKey(KeyCode.D);
                case 'Q': return Input.GetKey(KeyCode.Q);
                case 'E': return Input.GetKey(KeyCode.E);
                case 'L': return Input.GetKey(KeyCode.LeftShift);
            }
            return false;
#else
            return false;
#endif
        }
    }
}
