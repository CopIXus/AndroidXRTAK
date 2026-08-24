using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.UI
{
    /// <summary>
    /// Rewrites remote HLS (Wowza/skyvdn master + live chunklist) into a local
    /// file-based media playlist under persistentDataPath so Unity's Android
    /// VideoPlayer can play it. Remote live HLS with explicit :443 often fails
    /// with "VideoPlayer cannot play url"; a local VOD-style window of recent
    /// .ts segments is far more reliable on Android XR.
    /// </summary>
    public sealed class XrHlsLocalProxy : MonoBehaviour
    {
        const int WindowSegments = 4;
        const float RefreshSec = 2.5f;

        static XrHlsLocalProxy _instance;
        string _cacheDir;
        string _localPlaylistPath;
        string _upstreamMediaUrl;
        string _upstreamBase;
        Coroutine _refreshCo;
        int _session;

        public static XrHlsLocalProxy Ensure()
        {
            if (_instance != null) return _instance;
            var go = new GameObject("XrHlsLocalProxy");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<XrHlsLocalProxy>();
            return _instance;
        }

        /// <summary>
        /// Absolute filesystem path to the local stream.m3u8 (preferred for Android
        /// VideoPlayer), or null if not prepared.
        /// </summary>
        public string LocalPlaylistPath =>
            (!string.IsNullOrEmpty(_localPlaylistPath) && File.Exists(_localPlaylistPath))
                ? _localPlaylistPath
                : null;

        public IEnumerator Prepare(string remoteUrl, Action<string> onReady, Action<string> onError)
        {
            if (string.IsNullOrEmpty(remoteUrl))
            {
                onError?.Invoke("empty url");
                yield break;
            }

            _session++;
            int session = _session;
            StopRefresh();

            string normalized = NormalizeUrl(remoteUrl);
            Debug.Log("[TakXr] HlsProxy prepare " + normalized);

            string mediaUrl = null;
            string err = null;
            yield return ResolveMediaPlaylist(normalized, u => mediaUrl = u, e => err = e);
            if (session != _session) yield break;
            if (!string.IsNullOrEmpty(err) || string.IsNullOrEmpty(mediaUrl))
            {
                onError?.Invoke(err ?? "failed to resolve HLS playlist");
                yield break;
            }

            _upstreamMediaUrl = mediaUrl;
            _upstreamBase = mediaUrl.Substring(0, mediaUrl.LastIndexOf('/') + 1);
            _cacheDir = Path.Combine(Application.persistentDataPath, "hls_cache");
            Directory.CreateDirectory(_cacheDir);
            _localPlaylistPath = Path.Combine(_cacheDir, "stream.m3u8");

            // First materialization must succeed before we hand URL to VideoPlayer.
            bool ok = false;
            string buildErr = null;
            yield return BuildLocalWindow(success => ok = success, e => buildErr = e);
            if (session != _session) yield break;
            if (!ok)
            {
                onError?.Invoke(buildErr ?? "failed to fetch HLS segments");
                yield break;
            }

            _refreshCo = StartCoroutine(RefreshLoop(session));
            onReady?.Invoke(LocalPlaylistPath);
        }

        public void StopProxy()
        {
            _session++;
            StopRefresh();
            _upstreamMediaUrl = null;
        }

        void OnDestroy()
        {
            StopProxy();
            if (_instance == this) _instance = null;
        }

        void StopRefresh()
        {
            if (_refreshCo != null)
            {
                StopCoroutine(_refreshCo);
                _refreshCo = null;
            }
        }

        IEnumerator RefreshLoop(int session)
        {
            while (session == _session)
            {
                yield return new WaitForSecondsRealtime(RefreshSec);
                if (session != _session) yield break;
                yield return BuildLocalWindow(_ => { }, _ => { });
            }
        }

        IEnumerator BuildLocalWindow(Action<bool> done, Action<string> fail)
        {
            if (string.IsNullOrEmpty(_upstreamMediaUrl))
            {
                fail?.Invoke("no upstream");
                done?.Invoke(false);
                yield break;
            }

            string playlistText = null;
            string err = null;
            yield return FetchText(_upstreamMediaUrl, t => playlistText = t, e => err = e);
            if (!string.IsNullOrEmpty(err) || string.IsNullOrEmpty(playlistText))
            {
                fail?.Invoke(err ?? "empty media playlist");
                done?.Invoke(false);
                yield break;
            }

            // Parse last N (uri, extinf) pairs.
            var segs = new List<(string uri, string extinf)>(16);
            string pendingInf = "#EXTINF:3.0,";
            using (var reader = new StringReader(playlistText))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
                    {
                        pendingInf = line;
                        continue;
                    }
                    if (line[0] == '#') continue;
                    string uri = line.Trim();
                    if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        uri = _upstreamBase + uri;
                    segs.Add((NormalizeUrl(uri), pendingInf));
                }
            }

            if (segs.Count == 0)
            {
                fail?.Invoke("media playlist has no segments");
                done?.Invoke(false);
                yield break;
            }

            int start = Math.Max(0, segs.Count - WindowSegments);
            var chosen = segs.GetRange(start, segs.Count - start);

            // Download missing segments into cache dir.
            var localNames = new List<(string file, string extinf)>(chosen.Count);
            for (int i = 0; i < chosen.Count; i++)
            {
                var (uri, extinf) = chosen[i];
                string fileName = "seg_" + StableHash(uri).ToString("x8") + ".ts";
                string path = Path.Combine(_cacheDir, fileName);
                if (!File.Exists(path) || new FileInfo(path).Length < 64)
                {
                    byte[] data = null;
                    string ferr = null;
                    yield return FetchBytes(uri, b => data = b, e => ferr = e);
                    if (data == null || data.Length < 64)
                    {
                        Debug.LogWarning("[TakXr] HlsProxy seg fail " + (ferr ?? uri));
                        continue;
                    }
                    try { File.WriteAllBytes(path, data); }
                    catch (Exception e)
                    {
                        fail?.Invoke("write seg: " + e.Message);
                        done?.Invoke(false);
                        yield break;
                    }
                }
                localNames.Add((fileName, extinf));
            }

            if (localNames.Count == 0)
            {
                fail?.Invoke("no segments downloaded");
                done?.Invoke(false);
                yield break;
            }

            // VOD-style playlist (ENDLIST) — Android VideoPlayer handles this;
            // refresh loop rewrites with newer segments and VideoPlayer reconnects
            // via the panel's end/error retry path when needed.
            var sb = new StringBuilder(256);
            sb.AppendLine("#EXTM3U");
            sb.AppendLine("#EXT-X-VERSION:3");
            sb.AppendLine("#EXT-X-TARGETDURATION:8");
            sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
            sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
            foreach (var (file, extinf) in localNames)
            {
                sb.AppendLine(string.IsNullOrEmpty(extinf) ? "#EXTINF:3.0," : extinf);
                // Relative segment names (same directory as stream.m3u8) — most reliable
                // for Android VideoPlayer file playback.
                sb.AppendLine(file);
            }
            sb.AppendLine("#EXT-X-ENDLIST");

            try
            {
                File.WriteAllText(_localPlaylistPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                fail?.Invoke("write playlist: " + e.Message);
                done?.Invoke(false);
                yield break;
            }

            // Opportunistic cleanup of old segs.
            try
            {
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (file, _) in localNames) keep.Add(file);
                keep.Add("stream.m3u8");
                foreach (var f in Directory.GetFiles(_cacheDir))
                {
                    string name = Path.GetFileName(f);
                    if (!keep.Contains(name))
                    {
                        try { File.Delete(f); } catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }

            done?.Invoke(true);
        }

        IEnumerator ResolveMediaPlaylist(string url, Action<string> ok, Action<string> fail)
        {
            string text = null;
            string err = null;
            yield return FetchText(url, t => text = t, e => err = e);
            if (!string.IsNullOrEmpty(err) || string.IsNullOrEmpty(text))
            {
                fail?.Invoke(err ?? "empty playlist");
                yield break;
            }

            bool isMaster = text.IndexOf("#EXT-X-STREAM-INF", StringComparison.Ordinal) >= 0;
            if (!isMaster)
            {
                ok?.Invoke(url);
                yield break;
            }

            string variant = null;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                    variant = line.Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(variant))
            {
                fail?.Invoke("master playlist has no variants");
                yield break;
            }
            if (!variant.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                variant = url.Substring(0, url.LastIndexOf('/') + 1) + variant;
            ok?.Invoke(NormalizeUrl(variant));
        }

        static IEnumerator FetchText(string url, Action<string> ok, Action<string> fail)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 15;
            req.SetRequestHeader("User-Agent", "TAKXR-HLS/1.0");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                fail?.Invoke(req.error ?? "GET failed");
                yield break;
            }
            ok?.Invoke(req.downloadHandler?.text ?? "");
        }

        static IEnumerator FetchBytes(string url, Action<byte[]> ok, Action<string> fail)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 20;
            req.SetRequestHeader("User-Agent", "TAKXR-HLS/1.0");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                fail?.Invoke(req.error ?? "GET failed");
                yield break;
            }
            ok?.Invoke(req.downloadHandler?.data);
        }

        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            // Protect scheme while stripping explicit default TLS port (breaks some
            // Android MediaPlayer stacks used by Unity VideoPlayer).
            string s = url.Replace("https://", "HTTPS|").Replace("http://", "HTTP|");
            s = s.Replace(":443/", "/").Replace(":443?", "?");
            if (s.EndsWith(":443", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 4);
            return s.Replace("HTTPS|", "https://").Replace("HTTP|", "http://");
        }

        public static bool LooksLikeHls(string url) =>
            !string.IsNullOrEmpty(url) &&
            url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

        static uint StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619;
                }
                return h;
            }
        }
    }
}
