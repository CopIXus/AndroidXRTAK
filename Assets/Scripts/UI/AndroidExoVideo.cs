using System;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// C# bridge to us.copix.takxr.video.TakXrExoPlayer — ATAK-style direct
    /// RTSP/HLS via Media3 ExoPlayer, frames copied into a Unity Texture2D.
    /// </summary>
    public sealed class AndroidExoVideo : IDisposable
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _player;
#endif
        Texture2D _tex;
        int _texW, _texH;
        long _lastSeq = -1;
        string _status = "idle";
        string _error = "";

        public Texture2D Texture => _tex;
        public string Status => _status;
        public string LastError => _error;
        public bool IsPlaying => string.Equals(_status, "playing", StringComparison.OrdinalIgnoreCase);
        public static bool IsSupported
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public void Start(string url, bool forceRtpTcp = true)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (string.IsNullOrEmpty(url)) return;
            EnsurePlayer();
            _error = "";
            _status = "connecting";
            _lastSeq = -1;
            Debug.Log("[TakXr] AndroidExoVideo.start " + url + " tcp=" + forceRtpTcp);
            _player.Call("start", url, forceRtpTcp);
#else
            _error = "ExoPlayer only on Android device";
            _status = "error";
#endif
        }

        public void Stop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { _player?.Call("stop"); } catch { /* ignore */ }
#endif
            _status = "idle";
        }

        /// <summary>Poll native player; returns true when the texture was updated.</summary>
        public bool Tick()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_player == null) return false;
            try
            {
                _status = _player.Call<string>("getStatus") ?? _status;
                _error = _player.Call<string>("getLastError") ?? "";
                long seq = _player.Call<long>("getFrameSeq");
                if (seq == _lastSeq) return false;
                byte[] rgba = _player.Call<byte[]>("pollRgbaFrame");
                if (rgba == null || rgba.Length < 16) return false;
                int w = _player.Call<int>("getWidth");
                int h = _player.Call<int>("getHeight");
                if (w <= 0 || h <= 0 || rgba.Length < w * h * 4) return false;
                EnsureTexture(w, h);
                _tex.LoadRawTextureData(rgba);
                _tex.Apply(false, false);
                _lastSeq = seq;
                return true;
            }
            catch (Exception e)
            {
                _error = e.Message;
                _status = "error";
                Debug.LogWarning("[TakXr] AndroidExoVideo.Tick: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        public void Dispose()
        {
            Stop();
#if UNITY_ANDROID && !UNITY_EDITOR
            try { _player?.Call("release"); } catch { /* ignore */ }
            _player?.Dispose();
            _player = null;
#endif
            if (_tex != null)
            {
                UnityEngine.Object.Destroy(_tex);
                _tex = null;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void EnsurePlayer()
        {
            if (_player != null) return;
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using var context = activity.Call<AndroidJavaObject>("getApplicationContext");
            _player = new AndroidJavaObject("us.copix.takxr.video.TakXrExoPlayer", context);
        }
#endif

        void EnsureTexture(int w, int h)
        {
            if (_tex != null && _texW == w && _texH == h) return;
            if (_tex != null) UnityEngine.Object.Destroy(_tex);
            _tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _texW = w;
            _texH = h;
        }
    }
}
