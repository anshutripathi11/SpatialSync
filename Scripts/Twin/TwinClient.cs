using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace FloorTrack.Twin
{
    /// <summary>Polls the twin server for state changes and downloads/caches photo and plan textures.</summary>
    public class TwinClient : MonoBehaviour
    {
        [SerializeField] private string serverUrl = "http://127.0.0.1:8000";
        [SerializeField, Min(0.2f)] private float pollSeconds = 1f;

        public event Action<TwinStateDto> StateChanged;
        public TwinStateDto State { get; private set; }
        public string LastError { get; private set; }
        public string ServerUrl { get => serverUrl; set => serverUrl = value; }

        private readonly Dictionary<string, Texture2D> photoCache = new Dictionary<string, Texture2D>();
        private readonly Dictionary<string, List<Action<Texture2D>>> photoWaiters = new Dictionary<string, List<Action<Texture2D>>>();
        private Texture2D planTexture;
        private string planFileLoaded;

        private void OnEnable() => StartCoroutine(Poll());

        private IEnumerator Poll()
        {
            while (enabled)
            {
                int since = State != null ? State.version : -1;
                using (var req = UnityWebRequest.Get($"{Url}/state?since={since}"))
                {
                    req.timeout = 10;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        LastError = null;
                        var dto = JsonUtility.FromJson<TwinStateDto>(req.downloadHandler.text);
                        if (dto != null && !dto.unchanged)
                        {
                            State = dto;
                            StateChanged?.Invoke(dto);
                        }
                    }
                    else LastError = req.error;
                }
                yield return new WaitForSeconds(pollSeconds);
            }
        }

        private string Url => serverUrl.TrimEnd('/');

        /// <summary>Calls back with the photo texture (immediately if cached).</summary>
        public void GetPhoto(string photoId, Action<Texture2D> done)
        {
            if (photoCache.TryGetValue(photoId, out var tex)) { done(tex); return; }
            if (photoWaiters.TryGetValue(photoId, out var list)) { list.Add(done); return; }
            photoWaiters[photoId] = new List<Action<Texture2D>> { done };
            StartCoroutine(Download($"{Url}/photo/{photoId}", t =>
            {
                if (t != null) { t.wrapMode = TextureWrapMode.Clamp; photoCache[photoId] = t; }
                var waiters = photoWaiters[photoId];
                photoWaiters.Remove(photoId);
                if (t != null) foreach (var w in waiters) w(t);
            }));
        }

        public void GetPlan(Action<Texture2D> done)
        {
            if (State == null || string.IsNullOrEmpty(State.plan_file)) return;
            if (planTexture != null && planFileLoaded == State.plan_file) { done(planTexture); return; }
            string file = State.plan_file;
            StartCoroutine(Download($"{Url}/plan_image", t => { planTexture = t; planFileLoaded = file; if (t != null) done(t); }));
        }

        private IEnumerator Download(string url, Action<Texture2D> done)
        {
            using (var req = UnityWebRequestTexture.GetTexture(url))
            {
                yield return req.SendWebRequest();
                done(req.result == UnityWebRequest.Result.Success ? DownloadHandlerTexture.GetContent(req) : null);
            }
        }
    }
}
