using System;
using System.Collections;
using UnityEngine;

namespace FloorTrack
{
    /// <summary>
    /// COMPONENT 4 (photo source) — grabs what the AR camera shows, without the UI.
    ///
    /// Simplest cross-platform approach: hide the UI canvases for one frame, wait until the frame is rendered,
    /// read the back buffer, restore the UI. The image matches exactly what the user saw (same crop as the
    /// projection matrix used for the FOV cone).
    /// Swap this for an ARCameraManager.TryAcquireLatestCpuImage implementation of IPhotoCaptureService if you
    /// need full-resolution sensor images — nothing else has to change.
    /// </summary>
    public class ScreenshotCaptureService : MonoBehaviour, IPhotoCaptureService
    {
        [Tooltip("Canvases to hide while capturing (your main UI).")]
        [SerializeField] private Canvas[] canvasesToHide = new Canvas[0];
        [Tooltip("Longest side of the stored photo in pixels. Keeps memory low on phones.")]
        [SerializeField, Min(128)] private int maxDimension = 1280;

        public bool IsBusy { get; private set; }

        public void Capture(Action<Texture2D> onComplete)
        {
            if (IsBusy) return;
            StartCoroutine(CaptureRoutine(onComplete));
        }

        private IEnumerator CaptureRoutine(Action<Texture2D> onComplete)
        {
            IsBusy = true;

            var wasEnabled = new bool[canvasesToHide.Length];
            for (int i = 0; i < canvasesToHide.Length; i++)
            {
                if (canvasesToHide[i] == null) continue;
                wasEnabled[i] = canvasesToHide[i].enabled;
                canvasesToHide[i].enabled = false;
            }

            yield return new WaitForEndOfFrame(); // this frame renders without the UI

            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();

            for (int i = 0; i < canvasesToHide.Length; i++)
                if (canvasesToHide[i] != null) canvasesToHide[i].enabled = wasEnabled[i];

            Texture2D result = Downscale(shot, maxDimension);
            if (result != shot) Destroy(shot);
            result.name = $"Capture_{DateTime.Now:HHmmss}";

            IsBusy = false;
            onComplete?.Invoke(result);
        }

        private static Texture2D Downscale(Texture2D src, int maxDim)
        {
            int longest = Mathf.Max(src.width, src.height);
            if (longest <= maxDim) return src;

            float k = (float)maxDim / longest;
            int w = Mathf.RoundToInt(src.width * k), h = Mathf.RoundToInt(src.height * k);

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var dst = new Texture2D(w, h, TextureFormat.RGB24, false);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            dst.Apply(false, false);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return dst;
        }
    }
}
