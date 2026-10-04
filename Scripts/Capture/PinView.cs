using System;
using UnityEngine;
using UnityEngine.UI;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 4 (view) — one dropped pin on the blueprint: a tappable icon + FOV cone.
    ///
    /// Prefab hierarchy:
    ///   Pin (this, RectTransform 0×0 size, Button whose Target Graphic is the Icon)
    ///     ├─ FovCone (FovConeGraphic, colour e.g. yellow @ 50% alpha)  ← first child = drawn underneath
    ///     └─ Icon (Image, pin sprite ~48×48, pivot at the sprite tip, + ConstantScreenSize)
    /// </summary>
    public class PinView : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private FovConeGraphic cone;

        public PinRecord Record { get; private set; }
        private Action<PinRecord> onTapped;

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            var rt = (RectTransform)transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        }

        public void Bind(PinRecord record, Vector2 blueprintLocalPosition, float coneLengthUnits, Action<PinRecord> tapped)
        {
            Record = record;
            onTapped = tapped;
            SetPosition(blueprintLocalPosition, coneLengthUnits);

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => onTapped?.Invoke(Record));
        }

        public void SetPosition(Vector2 blueprintLocalPosition, float coneLengthUnits)
        {
            transform.localPosition = new Vector3(blueprintLocalPosition.x, blueprintLocalPosition.y, 0f);
            if (cone != null) cone.SetCone(Record.headingDegrees, Record.horizontalFovDegrees, coneLengthUnits);
        }
    }
}
