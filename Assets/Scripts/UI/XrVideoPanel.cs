using System;
using System.Collections;
using TakXr.Cot;
using UnityEngine;
using UnityEngine.Video;

namespace TakXr.UI
{
    /// <summary>
    /// In-headset video popup matching the web player: dark plate with a subtle
    /// border, camera name top-left, X close top-right, 16:9 video area below.
    /// Plays progressive HTTP(S) AND HLS (.m3u8) — Android's VideoPlayer sits on
    /// the platform media framework (ExoPlayer/MediaPlayer) which handles HLS
    /// natively, so URLs are fed straight to VideoSource.Url. Live streams that
    /// end or error are retried after 3 s (max 3 attempts).
    ///
    /// Android IL2CPP / Samsung XR: MaterialOverride on the mesh Renderer is the
    /// primary path (RenderTexture→Sprites/Default often stays black). Status text
    /// uses a higher renderQueue than the screen so Buffering/Playing/errors stay
    /// visible over the video quad.
    /// </summary>
    public class XrVideoPanel : MonoBehaviour
    {
        const float VideoMaxW = 1.28f;   // 16:9 video area bounds (metres)
        const float VideoMaxH = 0.72f;
        const float VideoCenterY = -0.06f;
        const int MaxRetries = 8; // local HLS windows are short; reconnect often for live
        const float RetryDelay = 3f;
        const float PrepareTimeout = 20f;
        const int ScreenQueue = 3010;
        const int StatusQueue = 3025; // must draw above the screen quad

        Transform _root;
        TextMesh _title;
        TextMesh _status;
        VideoPlayer _player;
        RenderTexture _rt;
        Transform _screen;
        Renderer _screenRenderer;
        Material _screenMat;
        string _texProperty = "_MainTex";
        string _url;
        string _playUrl; // resolved RTSP/HLS play URL
        CotVideo _videoMeta;
        Coroutine _watchdogCo;
        Coroutine _retryCo;
        Coroutine _prepareCo;
        int _retryCount;
        int _session; // invalidates stale player callbacks after Hide/re-Show
        bool _awaitingFirstFrame;
        int _bindSession;
        bool _usedHlsProxy;
        AndroidExoVideo _exo;
        bool _usingExo;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrVideoPanel Create()
        {
            var go = new GameObject("XrVideoPanel");
            return go.AddComponent<XrVideoPanel>();
        }

        void Awake() => Build();

        void OnDestroy()
        {
            if (_player != null)
            {
                _player.prepareCompleted -= OnPrepared;
                _player.errorReceived -= OnError;
                _player.loopPointReached -= OnStreamEnded;
                _player.frameReady -= OnFrameReady;
                _player.Stop();
            }
            _exo?.Dispose();
            _exo = null;
            if (_rt != null) Destroy(_rt);
            if (_screenMat != null) Destroy(_screenMat);
        }

        void Update()
        {
            if (!IsVisible) return;

            // ATAK-style path: ExoPlayer RTSP/HLS → RGBA Texture2D.
            if (_usingExo && _exo != null)
            {
                if (_exo.Tick())
                {
                    ApplyTextureToMaterial(_exo.Texture);
                    if (_awaitingFirstFrame)
                    {
                        _awaitingFirstFrame = false;
                        SetStatus("");
                        SizeScreenToAspect(_exo.Texture.width, _exo.Texture.height);
                    }
                }
                else if (_awaitingFirstFrame)
                {
                    string st = _exo.Status;
                    if (st == "error")
                    {
                        SetStatus("Video error\n" + (_exo.LastError ?? "ExoPlayer") + "\n" + UrlHost(_playUrl ?? _url));
                        ScheduleRetry();
                    }
                    else if (st == "buffering" || st == "preparing" || st == "connecting")
                        SetStatus("Buffering…\n" + UrlHost(_playUrl ?? _url));
                }
                return;
            }

            // Unity VideoPlayer fallback: re-bind until first frame.
            if (_awaitingFirstFrame && _player != null && _player.isPlaying)
                BindVideoTexture();
        }

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            // Subtle border: slightly larger quad BEHIND the plate (positive local Z —
            // XrUiFacing convention puts the viewer on the -Z side).
            Quad("Border", _root, new Vector3(0f, 0f, 0.002f), new Vector2(1.43f, 0.99f),
                new Color(0.35f, 0.45f, 0.58f, 0.55f), 3000);
            // Dark panel plate.
            Quad("Plate", _root, Vector3.zero, new Vector2(1.40f, 0.96f),
                new Color(0.06f, 0.07f, 0.09f, 0.95f), 3001);

            // Video surface (16:9, resized to the real aspect once prepared).
            // Readable side is local -Z (XrUiFacing); keep Cull Off so back-face is not black.
            var screenGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screenGo.name = "Screen";
            screenGo.transform.SetParent(_root, false);
            screenGo.transform.localPosition = new Vector3(0f, VideoCenterY, -0.005f);
            screenGo.transform.localScale = new Vector3(VideoMaxW, VideoMaxH, 1f);
            Destroy(screenGo.GetComponent<Collider>());
            _screen = screenGo.transform;

            _rt = new RenderTexture(1280, 720, 0, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _rt.Create();
            ClearRt();

            _screenMat = CreateScreenMaterial();
            _texProperty = ResolveTexProperty(_screenMat);
            ApplyTextureToMaterial(_rt);
            _screenMat.color = Color.white;
            if (_screenMat.HasProperty("_Color")) _screenMat.SetColor("_Color", Color.white);
            if (_screenMat.HasProperty("_BaseColor")) _screenMat.SetColor("_BaseColor", Color.white);
            if (_screenMat.HasProperty("_Cull")) _screenMat.SetFloat("_Cull", 0f); // Cull Off
            _screenMat.renderQueue = ScreenQueue;

            _screenRenderer = screenGo.GetComponent<Renderer>();
            _screenRenderer.sharedMaterial = _screenMat;
            _screenRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _screenRenderer.receiveShadows = false;

            _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = false; // live streams: reconnect handled explicitly
            _player.waitForFirstFrame = true;
            _player.skipOnDrop = true;
            _player.sendFrameReadyEvents = true;
            // MaterialOverride is more reliable than RT-only on Android IL2CPP / XR.
            // Do NOT also set targetTexture — dual mode often leaves the quad black.
            _player.renderMode = VideoRenderMode.MaterialOverride;
            _player.targetMaterialRenderer = _screenRenderer;
            _player.targetMaterialProperty = _texProperty;
            _player.targetTexture = null;
            _player.aspectRatio = VideoAspectRatio.Stretch;
            _player.audioOutputMode = VideoAudioOutputMode.Direct;
            _player.prepareCompleted += OnPrepared;
            _player.errorReceived += OnError;
            _player.loopPointReached += OnStreamEnded;
            _player.frameReady += OnFrameReady;

            // Title bar: camera name top-left, close button top-right (web parity).
            _title = Text("Title", _root, new Vector3(-0.66f, 0.41f, -0.01f), "VIDEO",
                0.0016f, new Color(0.92f, 0.95f, 1f));
            _title.anchor = TextAnchor.MiddleLeft;
            _title.alignment = TextAlignment.Left;

            // Status overlays the video area — must outrank ScreenQueue or it is invisible.
            _status = Text("Status", _root, new Vector3(0f, VideoCenterY, -0.012f), "",
                0.0012f, new Color(0.85f, 0.9f, 1f, 1f));
            BumpStatusQueue();

            var close = new GameObject("Close").transform;
            close.SetParent(_root, false);
            close.localPosition = new Vector3(0.62f, 0.41f, -0.01f);
            Quad("Bg", close, Vector3.zero, new Vector2(0.09f, 0.07f),
                new Color(0.22f, 0.24f, 0.28f, 0.95f), 3010);
            Text("X", close, new Vector3(0f, 0f, -0.005f), "X", 0.0014f, Color.white);
            var col = close.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.11f, 0.09f, 0.04f);
            col.isTrigger = true;
        }

        public void Show(NormalizedCot cot, Transform camera)
        {
            _videoMeta = cot?.detail?.video;
            _url = _videoMeta?.url?.Trim();
            _root.gameObject.SetActive(true);
            if (_title != null)
                _title.text = FirstNonEmpty(_videoMeta?.alias, cot?.Callsign, "VIDEO");
            PlaceFacing(camera);

            StopPlayback();
            _session++;
            _retryCount = 0;
            _awaitingFirstFrame = false;
            _usedHlsProxy = false;
            _usingExo = false;
            _playUrl = null;
            ClearRt();
            BindVideoTexture();

            // Prefer ATAK ConnectionEntry → RTSP (ResolvePlayUrl); fall back to url.
            string resolved = _videoMeta != null ? _videoMeta.ResolvePlayUrl() : _url;
            if (string.IsNullOrEmpty(resolved) && string.IsNullOrEmpty(_url))
            {
                Debug.LogWarning("[TakXr] XrVideoPanel: No stream URL (alias-only / missing __video)");
                SetStatus("No stream URL");
                return;
            }

            _playUrl = XrHlsLocalProxy.NormalizeUrl(resolved ?? _url);
            Debug.Log("[TakXr] XrVideoPanel.Show play=" + _playUrl
                      + " raw=" + _url
                      + " proto=" + (_videoMeta?.protocol ?? "?")
                      + " addr=" + (_videoMeta?.address ?? "?"));
            if (_prepareCo != null) StopCoroutine(_prepareCo);
            _prepareCo = StartCoroutine(PrepareAndStart(_session));
        }

        public void Hide()
        {
            _session++;
            _awaitingFirstFrame = false;
            StopPlayback();
            XrHlsLocalProxy.Ensure().StopProxy();
            if (_root != null) _root.gameObject.SetActive(false);
            _url = null;
            _playUrl = null;
            _videoMeta = null;
        }

        public void PlaceFacing(Transform camera)
        {
            if (!IsVisible || camera == null) return;
            var camPos = camera.position;
            var flat = camera.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            // Farther than chrome so the panel is comfortable.
            _root.position = camPos + flat * 2.0f + Vector3.up * 0.05f;
            // XrUiFacing convention: +Z away from camera (readable side toward viewer).
            _root.rotation = XrUiFacing.RotationFacingUser(_root.position, camPos);
        }

        public bool HandleRaySelect(Ray ray, float maxDist = 6f)
        {
            if (!IsVisible) return false;
            if (!Physics.Raycast(ray, out var hit, maxDist, ~0, QueryTriggerInteraction.Collide)) return false;
            if (!hit.transform.IsChildOf(_root) && hit.transform != _root) return false;
            if (hit.transform.name == "Close" || hit.transform.parent?.name == "Close")
            {
                Hide();
                return true;
            }
            return true;
        }

        // ---- playback ----

        IEnumerator PrepareAndStart(int session)
        {
            if (string.IsNullOrEmpty(_playUrl))
                _playUrl = XrHlsLocalProxy.NormalizeUrl(_url);

            // On device: ATAK-style direct RTSP/HLS via Media3 ExoPlayer.
            if (AndroidExoVideo.IsSupported)
            {
                SetStatus("Connecting…\n" + UrlHost(_playUrl));
                StartExo(_playUrl);
                yield break;
            }

            // Editor / non-Android: Unity VideoPlayer (HLS proxy for .m3u8).
            if (XrHlsLocalProxy.LooksLikeHls(_playUrl))
            {
                SetStatus("Resolving stream…\n" + UrlHost(_playUrl));
                string local = null;
                string err = null;
                var proxy = XrHlsLocalProxy.Ensure();
                yield return proxy.Prepare(_playUrl, u => local = u, e => err = e);
                if (session != _session || !IsVisible) yield break;
                if (!string.IsNullOrEmpty(local))
                {
                    _playUrl = local;
                    _usedHlsProxy = true;
                }
                else
                    Debug.LogWarning("[TakXr] HlsProxy failed (" + (err ?? "?") + ")");
            }

            if (session != _session || !IsVisible) yield break;
            StartUnityVideoPlayer(_playUrl);
        }

        void StartExo(string url)
        {
            _usingExo = true;
            _awaitingFirstFrame = true;
            if (_player != null) _player.Stop();
            _exo ??= new AndroidExoVideo();
            bool forceTcp = (_videoMeta != null && _videoMeta.rtspReliable != 0)
                            || (url != null && url.IndexOf(":443", StringComparison.Ordinal) >= 0)
                            || true; // RTP/TCP is more reliable through NAT / XR Wi-Fi
            SetStatus("Buffering…\n" + UrlHost(url));
            _exo.Start(url, forceTcp);
            if (_watchdogCo != null) StopCoroutine(_watchdogCo);
            _watchdogCo = StartCoroutine(PrepareWatchdog(_session));
            Debug.Log("[TakXr] XrVideoPanel ExoPlayer " + url);
        }

        void StartUnityVideoPlayer(string url)
        {
            _usingExo = false;
            SetStatus("Buffering…\n" + UrlHost(_url));
            _awaitingFirstFrame = false;
            _player.Stop();
            _player.source = VideoSource.Url;
            _player.url = url;
            _player.renderMode = VideoRenderMode.MaterialOverride;
            _player.targetMaterialRenderer = _screenRenderer;
            _player.targetMaterialProperty = _texProperty;
            _player.targetTexture = null;
            BindVideoTexture();
            Debug.Log("[TakXr] XrVideoPanel.Prepare (VideoPlayer) url=" + url);
            _player.Prepare();
            if (_watchdogCo != null) StopCoroutine(_watchdogCo);
            _watchdogCo = StartCoroutine(PrepareWatchdog(_session));
        }

        void OnPrepared(VideoPlayer vp)
        {
            if (!IsVisible || vp != _player) return;
            if (_watchdogCo != null) { StopCoroutine(_watchdogCo); _watchdogCo = null; }
            _retryCount = 0;

            SizeScreenToVideo(vp);
            BindVideoTexture();
            _awaitingFirstFrame = true;
            _bindSession = _session;

            try
            {
                if (vp.audioTrackCount > 0)
                {
                    vp.EnableAudioTrack(0, true);
                    vp.SetDirectAudioMute(0, false);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TakXr] XrVideoPanel audio track setup: " + e.Message);
            }

            Debug.Log("[TakXr] XrVideoPanel.prepareCompleted " + vp.width + "x" + vp.height
                      + " tracks=" + vp.audioTrackCount + " → Play()");
            SetStatus("Playing\n" + UrlHost(_url));
            vp.Play();
        }

        void OnFrameReady(VideoPlayer vp, long frameIdx)
        {
            if (!IsVisible || vp != _player || _bindSession != _session) return;
            BindVideoTexture();
            if (_awaitingFirstFrame)
            {
                _awaitingFirstFrame = false;
                Debug.Log("[TakXr] XrVideoPanel.frameReady idx=" + frameIdx);
                // Clear overlay once pixels are actually on the quad.
                SetStatus("");
            }
        }

        void OnError(VideoPlayer vp, string msg)
        {
            Debug.LogWarning("[TakXr] VideoPlayer error: " + msg + " url=" + _url);
            if (!IsVisible) return;
            _awaitingFirstFrame = false;
            var line = string.IsNullOrEmpty(msg) ? "unknown error"
                : (msg.Length > 100 ? msg.Substring(0, 100) + "…" : msg);
            SetStatus("Video error\n" + line + "\n" + UrlHost(_url));
            ScheduleRetry();
        }

        void OnStreamEnded(VideoPlayer vp)
        {
            // Live HLS feeds should not "end"; treat it as a drop and reconnect.
            if (!IsVisible || string.IsNullOrEmpty(_url)) return;
            _awaitingFirstFrame = false;
            SetStatus("Stream ended — reconnecting…\n" + UrlHost(_url));
            ScheduleRetry();
        }

        IEnumerator PrepareWatchdog(int session)
        {
            float t = 0f;
            while (t < PrepareTimeout)
            {
                if (session != _session) yield break;
                // Unity VideoPlayer prepared, or Exo delivered first frame / left connecting.
                if (!_usingExo && _player != null && _player.isPrepared) yield break;
                if (_usingExo && !_awaitingFirstFrame) yield break;
                if (_usingExo && _exo != null && _exo.Status == "error") yield break;
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (session != _session) yield break;
            if (!_usingExo && _player != null && _player.isPrepared) yield break;
            if (_usingExo && !_awaitingFirstFrame) yield break;
            Debug.LogWarning("[TakXr] XrVideoPanel prepare timeout url=" + (_playUrl ?? _url));
            SetStatus("Stream timed out\n(network / codec)\n" + UrlHost(_playUrl ?? _url));
            ScheduleRetry();
        }

        void ScheduleRetry()
        {
            if (_retryCount >= MaxRetries)
            {
                SetStatus("Stream unavailable\n(gave up after retries)\n" + UrlHost(_url));
                return;
            }
            _retryCount++;
            if (_retryCo != null) StopCoroutine(_retryCo);
            _retryCo = StartCoroutine(RetryAfterDelay(_session));
        }

        IEnumerator RetryAfterDelay(int session)
        {
            yield return new WaitForSecondsRealtime(RetryDelay);
            _retryCo = null;
            if (session != _session || !IsVisible || string.IsNullOrEmpty(_url)) yield break;
            // Rebuild local HLS window (live streams advance) then play again.
            if (_prepareCo != null) StopCoroutine(_prepareCo);
            _prepareCo = StartCoroutine(PrepareAndStart(session));
        }

        void StopPlayback()
        {
            if (_watchdogCo != null) { StopCoroutine(_watchdogCo); _watchdogCo = null; }
            if (_retryCo != null) { StopCoroutine(_retryCo); _retryCo = null; }
            if (_prepareCo != null) { StopCoroutine(_prepareCo); _prepareCo = null; }
            if (_player != null) _player.Stop();
            if (_exo != null) _exo.Stop();
            _usingExo = false;
        }

        /// <summary>Letterbox the screen quad to the stream's true aspect ratio.</summary>
        void SizeScreenToVideo(VideoPlayer vp)
        {
            if (vp == null) return;
            SizeScreenToAspect((int)vp.width, (int)vp.height);
        }

        void SizeScreenToAspect(int vw, int vh)
        {
            if (_screen == null || vw <= 0 || vh <= 0) return;
            float aspect = (float)vw / vh;
            float w, h;
            if (aspect >= VideoMaxW / VideoMaxH) { w = VideoMaxW; h = VideoMaxW / aspect; }
            else { h = VideoMaxH; w = VideoMaxH * aspect; }
            _screen.localScale = new Vector3(w, h, 1f);
        }

        void BindVideoTexture()
        {
            if (_screenMat == null) return;
            // Prefer the external texture VideoPlayer exposes under MaterialOverride;
            // fall back to our RT (also set as targetTexture).
            Texture tex = null;
            if (_player != null && _player.texture != null) tex = _player.texture;
            if (tex == null) tex = _rt;
            ApplyTextureToMaterial(tex);
        }

        void ApplyTextureToMaterial(Texture tex)
        {
            if (_screenMat == null || tex == null) return;
            _screenMat.mainTexture = tex;
            if (!string.IsNullOrEmpty(_texProperty) && _screenMat.HasProperty(_texProperty))
                _screenMat.SetTexture(_texProperty, tex);
            if (_texProperty != "_BaseMap" && _screenMat.HasProperty("_BaseMap"))
                _screenMat.SetTexture("_BaseMap", tex);
            if (_texProperty != "_MainTex" && _screenMat.HasProperty("_MainTex"))
                _screenMat.SetTexture("_MainTex", tex);
        }

        void SetStatus(string msg)
        {
            if (_status != null) _status.text = msg ?? "";
            BumpStatusQueue();
        }

        void BumpStatusQueue()
        {
            if (_status == null) return;
            var r = _status.GetComponent<MeshRenderer>();
            if (r == null || r.sharedMaterial == null) return;
            // TextMesh may share a material; clone once so we can raise the queue.
            if (!r.sharedMaterial.name.Contains("XrVideoStatus"))
            {
                var m = new Material(r.sharedMaterial) { name = "XrVideoStatus" };
                m.renderQueue = StatusQueue;
                r.sharedMaterial = m;
            }
            else
            {
                r.sharedMaterial.renderQueue = StatusQueue;
            }
        }

        void ClearRt()
        {
            if (_rt == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = prev;
        }

        static Material CreateScreenMaterial()
        {
            // Prefer Unlit/Texture (samples _MainTex, often Cull Off) then URP Unlit,
            // then Sprites/Default. White tint so texture * color is not black.
            var sh = Shader.Find("Unlit/Texture")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Sprites/Default")
                     ?? Shader.Find("Unlit/Color");
            return new Material(sh);
        }

        static string ResolveTexProperty(Material mat)
        {
            if (mat == null) return "_MainTex";
            if (mat.HasProperty("_BaseMap")) return "_BaseMap";
            if (mat.HasProperty("_MainTex")) return "_MainTex";
            return "_MainTex";
        }

        static string UrlHost(string url)
        {
            if (string.IsNullOrEmpty(url)) return "(no url)";
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                    return u.Host;
            }
            catch { /* ignore */ }
            return url.Length > 48 ? url.Substring(0, 48) + "…" : url;
        }

        static string FirstNonEmpty(params string[] parts)
        {
            if (parts == null) return null;
            foreach (var p in parts)
                if (!string.IsNullOrEmpty(p)) return p;
            return null;
        }

        // ---- chrome helpers ----

        static GameObject Quad(string name, Transform parent, Vector3 localPos, Vector2 size,
            Color color, int renderQueue)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Color");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", color);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            // Explicit queue: deterministic layering for co-planar transparent chrome.
            r.sharedMaterial.renderQueue = renderQueue;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        static TextMesh Text(string name, Transform parent, Vector3 localPos, string msg,
            float charSize, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            tm.text = msg;
            // Crisp world text: large fontSize, tiny characterSize
            // (world height = characterSize * fontSize * 0.1).
            tm.characterSize = charSize;
            tm.fontSize = 300;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                      ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            var r = go.GetComponent<MeshRenderer>();
            if (r != null)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            return tm;
        }
    }
}
