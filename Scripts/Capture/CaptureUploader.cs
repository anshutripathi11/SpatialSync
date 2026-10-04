using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace FloorTrack
{
    /// <summary>
    /// Sends every new pin (photo + pose JSON) to the twin server, which updates only the walls in that
    /// photo's view. Uploads are queued and retried, so walking through a Wi-Fi dead zone loses nothing.
    /// Put on the Systems object; set Server Url to your laptop, e.g. http://192.168.1.20:8000
    /// (Player Settings → "Allow downloads over HTTP" = Always allowed, for plain http).
    /// </summary>
    public class CaptureUploader : MonoBehaviour
    {
        [SerializeField] private SpatialPinController pins;
        [SerializeField] private string serverUrl = "http://192.168.1.20:8000";
        [SerializeField] private float retryDelaySeconds = 5f;

        private readonly Queue<PinRecord> pending = new Queue<PinRecord>();
        private bool running;

        public int PendingCount => pending.Count;
        public string LastError { get; private set; }

        private void OnEnable() { if (pins != null) pins.PinDropped += Enqueue; }
        private void OnDisable() { if (pins != null) pins.PinDropped -= Enqueue; }

        public void Enqueue(PinRecord record)
        {
            pending.Enqueue(record);
            if (!running) StartCoroutine(Pump());
        }

        private IEnumerator Pump()
        {
            running = true;
            while (pending.Count > 0)
            {
                var record = pending.Peek();
                byte[] jpg = pins.GetPhotoBytes(record);
                if (jpg == null) { pending.Dequeue(); continue; }

                var form = new List<IMultipartFormSection>
                {
                    new MultipartFormFileSection("photo", jpg, $"{record.id}.jpg", "image/jpeg"),
                    new MultipartFormDataSection("meta", JsonUtility.ToJson(record)),
                };
                using (var req = UnityWebRequest.Post(serverUrl.TrimEnd('/') + "/capture", form))
                {
                    req.timeout = 30;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        pending.Dequeue();
                        LastError = null;
                    }
                    else
                    {
                        LastError = req.error;
                        Debug.LogWarning($"[FloorTrack] upload of {record.id} failed: {req.error}; retrying");
                        yield return new WaitForSeconds(retryDelaySeconds);
                    }
                }
            }
            running = false;
        }
    }
}
