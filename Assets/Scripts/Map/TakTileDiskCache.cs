using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace TakXr.Map
{
    /// <summary>
    /// Persistent PNG tile cache for DEM + basemap imagery (ATAK-style shared folder).
    /// Prefers <c>/sdcard/takxr/tiles</c> so data can survive app updates and, when
    /// the OS allows, uninstall. Falls back to
    /// <see cref="Application.persistentDataPath"/>/takxr/tiles when external
    /// storage is unavailable (still survives updates, wiped on uninstall).
    /// Cesium's SQLite cache cannot target this path — we only cache DEM imagery.
    /// </summary>
    public static class TakTileDiskCache
    {
        const string FolderName = "takxr";
        const string TilesSub = "tiles";
        const long MaxBytes = 512L * 1024L * 1024L; // soft cap ~512 MB
        static string _root;
        static bool _inited;
        static long _approxBytes = -1;

        public static string Root
        {
            get
            {
                EnsureInit();
                return _root;
            }
        }

        public static bool Enabled => !string.IsNullOrEmpty(Root) && Directory.Exists(Root);

        public static void EnsureInit()
        {
            if (_inited) return;
            _inited = true;
            _root = ResolveRoot();
            try
            {
                if (!string.IsNullOrEmpty(_root))
                    Directory.CreateDirectory(_root);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakTileCache] mkdir failed: " + ex.Message);
                _root = Path.Combine(Application.persistentDataPath, FolderName, TilesSub);
                try { Directory.CreateDirectory(_root); }
                catch { _root = null; }
            }
            if (!string.IsNullOrEmpty(_root))
                Debug.Log("[TakTileCache] root=" + _root);
        }

        static string ResolveRoot()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            TryRequestLegacyStorage();
            try
            {
                using var env = new AndroidJavaClass("android.os.Environment");
                using var file = env.CallStatic<AndroidJavaObject>("getExternalStorageDirectory");
                string ext = file != null ? file.Call<string>("getAbsolutePath") : null;
                if (!string.IsNullOrEmpty(ext))
                {
                    string candidate = Path.Combine(ext, FolderName, TilesSub);
                    // Prove we can write (scoped storage often blocks this).
                    Directory.CreateDirectory(candidate);
                    string probe = Path.Combine(candidate, ".write_test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakTileCache] external /takxr unavailable: " + ex.Message);
            }
#endif
            return Path.Combine(Application.persistentDataPath, FolderName, TilesSub);
        }

        static void TryRequestLegacyStorage()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (!Permission.HasUserAuthorizedPermission(Permission.ExternalStorageWrite))
                    Permission.RequestUserPermission(Permission.ExternalStorageWrite);
            }
            catch { /* older Unity / missing API */ }
#endif
        }

        public static string DemKey(int z, int x, int y) =>
            Path.Combine("dem", z.ToString(), x.ToString(), y + ".png");

        public static string ImageryKey(string provider, int z, int x, int y) =>
            Path.Combine("img", provider ?? "x", z.ToString(), x.ToString(), y + ".png");

        public static bool TryLoadTexture(string relativeKey, bool readable, out Texture2D tex)
        {
            tex = null;
            EnsureInit();
            if (string.IsNullOrEmpty(_root) || string.IsNullOrEmpty(relativeKey)) return false;
            string path = Path.Combine(_root, relativeKey);
            if (!File.Exists(path)) return false;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes == null || bytes.Length < 32) return false;
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes, markNonReadable: !readable))
                {
                    UnityEngine.Object.Destroy(tex);
                    tex = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakTileCache] read fail " + relativeKey + ": " + ex.Message);
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = null;
                return false;
            }
        }

        public static void StoreBytes(string relativeKey, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || string.IsNullOrEmpty(relativeKey)) return;
            EnsureInit();
            if (string.IsNullOrEmpty(_root)) return;
            try
            {
                string path = Path.Combine(_root, relativeKey);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, bytes);
                TouchApproxSize(bytes.Length);
                MaybePrune();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakTileCache] write fail " + relativeKey + ": " + ex.Message);
            }
        }

        public static void StoreTexture(string relativeKey, Texture2D tex)
        {
            if (tex == null) return;
            try
            {
                // Only works when the texture is CPU-readable (DEM path).
                byte[] png = tex.EncodeToPNG();
                StoreBytes(relativeKey, png);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakTileCache] encode fail " + relativeKey + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Disk-first fetch. Downloads raw bytes so imagery can be cached even when
        /// the GPU texture is marked non-readable.
        /// </summary>
        public static IEnumerator FetchTexture(
            string url, string cacheKey, bool readable, int timeoutSec,
            Action<Texture2D> onDone)
        {
            if (TryLoadTexture(cacheKey, readable, out var cached))
            {
                onDone?.Invoke(cached);
                yield break;
            }

            using var req = UnityWebRequest.Get(url);
            req.timeout = timeoutSec;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success ||
                req.downloadHandler?.data == null ||
                req.downloadHandler.data.Length < 32)
            {
                onDone?.Invoke(null);
                yield break;
            }

            byte[] bytes = req.downloadHandler.data;
            if (!string.IsNullOrEmpty(cacheKey))
                StoreBytes(cacheKey, bytes);

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes, markNonReadable: !readable))
            {
                UnityEngine.Object.Destroy(tex);
                onDone?.Invoke(null);
                yield break;
            }
            onDone?.Invoke(tex);
        }

        static void TouchApproxSize(long added)
        {
            if (_approxBytes < 0) _approxBytes = 0;
            _approxBytes += added;
        }

        static void MaybePrune()
        {
            if (_approxBytes < MaxBytes || string.IsNullOrEmpty(_root)) return;
            try
            {
                var files = new DirectoryInfo(_root).GetFiles("*.png", SearchOption.AllDirectories);
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                long total = 0;
                foreach (var f in files) total += f.Length;
                _approxBytes = total;
                int i = 0;
                while (total > MaxBytes * 3 / 4 && i < files.Length)
                {
                    try
                    {
                        total -= files[i].Length;
                        files[i].Delete();
                    }
                    catch { /* ignore */ }
                    i++;
                }
                _approxBytes = total;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakTileCache] prune failed: " + ex.Message);
            }
        }
    }
}
