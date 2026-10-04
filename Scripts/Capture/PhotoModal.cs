using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 4 (view) — full-screen popup that shows one photo. Knows nothing about pins.
    ///
    /// Hierarchy:
    ///   PhotoModal (this; empty, ALWAYS ACTIVE, stretch-full)
    ///     └─ ModalRoot (← root; stretch-full)
    ///          ├─ Backdrop (Image black @ 75% + Button → closes)
    ///          └─ Card (Vertical Layout)
    ///               ├─ PhotoFrame (RectTransform, flexible height)
    ///               │    └─ Photo (RawImage + AspectRatioFitter mode = FitInParent)
    ///               ├─ Caption (TMP_Text)
    ///               └─ CloseButton (Button)
    /// </summary>
    public class PhotoModal : MonoBehaviour
    {
        [SerializeField] private GameObject root;
        [SerializeField] private RawImage photo;
        [SerializeField] private AspectRatioFitter aspectFitter;
        [SerializeField] private TMP_Text caption;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button backdropButton;

        public bool IsOpen => root != null && root.activeSelf;

        private void Awake()
        {
            // This component must sit on an ALWAYS-ACTIVE object; 'root' is the child panel that is toggled.
            if (root == null && transform.childCount > 0) root = transform.GetChild(0).gameObject;
            if (closeButton != null) closeButton.onClick.AddListener(Hide);
            if (backdropButton != null) backdropButton.onClick.AddListener(Hide);
            if (root != null && root != gameObject) root.SetActive(false);
        }

        public void Show(Texture texture, string captionText)
        {
            photo.texture = texture;
            if (aspectFitter != null && texture != null)
                aspectFitter.aspectRatio = (float)texture.width / Mathf.Max(1, texture.height);
            if (caption != null) caption.text = captionText;
            root.SetActive(true);
            root.transform.SetAsLastSibling(); // on top of everything
        }

        public void Hide()
        {
            root.SetActive(false);
            photo.texture = null;
        }
    }
}
