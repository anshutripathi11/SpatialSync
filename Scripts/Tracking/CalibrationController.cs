using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 2 — the "Calibrate" UI. Only enables the button once AR is tracking (calibrating on a
    /// non-tracking frame would anchor the origin to garbage), and shows a status line.
    /// </summary>
    public class CalibrationController : MonoBehaviour
    {
        [SerializeField] private MapPoseBridge bridge;
        [SerializeField] private Button calibrateButton;
        [SerializeField] private TMP_Text calibrateButtonLabel;
        [SerializeField] private TMP_Text statusText;

        [TextArea]
        [SerializeField] private string instruction =
            "Stand on the entrance marker, face the way the arrow points, hold the phone upright and tap Calibrate.";

        private string lastStatus;

        private void OnEnable() { if (calibrateButton != null) calibrateButton.onClick.AddListener(OnCalibrate); }
        private void OnDisable() { if (calibrateButton != null) calibrateButton.onClick.RemoveListener(OnCalibrate); }

        private void OnCalibrate()
        {
            if (bridge == null || bridge.Source == null || !bridge.Source.IsTracking) return;
            bridge.Calibrate();
#if UNITY_IOS || UNITY_ANDROID
            Handheld.Vibrate();
#endif
        }

        private void Update()
        {
            var src = bridge != null ? bridge.Source : null;
            if (src == null) return;

            if (calibrateButton != null) calibrateButton.interactable = src.IsTracking;
            if (calibrateButtonLabel != null) calibrateButtonLabel.text = src.IsCalibrated ? "Re-calibrate" : "Calibrate";

            string status;
            if (!src.IsTracking) status = src.TrackingStatus;
            else if (!src.IsCalibrated) status = instruction;
            else
            {
                var m = src.Current.Meters;
                status = $"{src.TrackingStatus}  ·  {m.x:+0.0;-0.0} m right, {m.y:+0.0;-0.0} m fwd, {src.Current.YawDegrees:0}°";
            }

            if (statusText != null && status != lastStatus)
            {
                statusText.text = status;
                lastStatus = status;
            }
        }
    }
}
