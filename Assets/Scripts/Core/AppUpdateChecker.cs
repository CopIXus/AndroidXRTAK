using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Core
{
    /// <summary>
    /// Polls the public GitHub Releases API for a newer TAKXR.apk and installs it
    /// through the system package installer.
    /// </summary>
    public class AppUpdateChecker : MonoBehaviour
    {
        public const string ReleasesUrl =
            "https://api.github.com/repos/CopIXus/AndroidXRTAK/releases/latest";
        const string UserAgent = "TAKXR-AndroidXR";
        const float PollSeconds = 30f * 60f;

        public static AppUpdateChecker Instance { get; private set; }

        public string LatestVersion { get; private set; } = "";
        public string StatusMessage { get; private set; } = "Checking for updates…";
        public bool UpdateAvailable { get; private set; }
        public bool Busy { get; private set; }
        public string DownloadUrl { get; private set; }

        public event Action Changed;

        bool _checking;

        [Serializable]
        class ReleaseDto
        {
            public string tag_name;
            public AssetDto[] assets;
        }

        [Serializable]
        class AssetDto
        {
            public string name;
            public string browser_download_url;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        IEnumerator Start()
        {
            while (isActiveAndEnabled)
            {
                yield return CheckOnce();
                yield return new WaitForSecondsRealtime(PollSeconds);
            }
        }

        public void CheckNow()
        {
            if (!_checking) StartCoroutine(CheckOnce());
        }

        public void BeginInstall()
        {
            if (!UpdateAvailable || string.IsNullOrEmpty(DownloadUrl) || Busy) return;
            StartCoroutine(DownloadAndInstall());
        }

        public string LatestLabel =>
            string.IsNullOrEmpty(LatestVersion) ? "…" : LatestVersion;

        IEnumerator CheckOnce()
        {
            if (_checking) yield break;
            _checking = true;
            StatusMessage = "Checking GitHub…";
            Notify();

            using (var req = UnityWebRequest.Get(ReleasesUrl))
            {
                req.SetRequestHeader("User-Agent", UserAgent);
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    StatusMessage = "Update check failed";
                    Debug.LogWarning("[TakXr] update check: " + req.error);
                    _checking = false;
                    Notify();
                    yield break;
                }

                ReleaseDto release;
                try
                {
                    release = JsonUtility.FromJson<ReleaseDto>(req.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    StatusMessage = "Update check failed";
                    Debug.LogWarning("[TakXr] update parse: " + ex.Message);
                    _checking = false;
                    Notify();
                    yield break;
                }

                var tag = release != null ? release.tag_name : null;
                LatestVersion = Normalize(tag);
                string apk = null;
                if (release?.assets != null)
                {
                    foreach (var asset in release.assets)
                    {
                        if (asset != null && string.Equals(asset.name, "TAKXR.apk", StringComparison.OrdinalIgnoreCase))
                        {
                            apk = asset.browser_download_url;
                            break;
                        }
                    }
                }

                var newer = IsNewer(LatestVersion, Application.version);
                UpdateAvailable = newer && !string.IsNullOrEmpty(apk);
                DownloadUrl = UpdateAvailable ? apk : null;
                if (UpdateAvailable)
                    StatusMessage = "Update " + LatestVersion + " available";
                else if (newer && string.IsNullOrEmpty(apk))
                    StatusMessage = "Release " + LatestVersion + " has no APK";
                else if (!string.IsNullOrEmpty(LatestVersion))
                    StatusMessage = "Up to date";
                else
                    StatusMessage = "No release found";
            }

            _checking = false;
            Notify();
        }

        IEnumerator DownloadAndInstall()
        {
            Busy = true;
            StatusMessage = "Downloading " + LatestVersion + "…";
            Notify();

            var dir = Path.Combine(Application.temporaryCachePath, "updates");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "TAKXR.apk");
            if (File.Exists(file)) File.Delete(file);

            using (var req = new UnityWebRequest(DownloadUrl, UnityWebRequest.kHttpVerbGET))
            {
                req.downloadHandler = new DownloadHandlerFile(file);
                req.SetRequestHeader("User-Agent", UserAgent);
                req.SetRequestHeader("Accept", "application/octet-stream");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    StatusMessage = "Download failed";
                    Debug.LogWarning("[TakXr] update download: " + req.error);
                    Busy = false;
                    Notify();
                    yield break;
                }
            }

            StatusMessage = PromptInstall(file);
            Busy = false;
            Notify();
        }

        static string PromptInstall(string file)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var installer = new AndroidJavaClass("us.copix.takxr.update.ApkInstaller"))
                {
                    var result = installer.CallStatic<string>("install", activity, file);
                    if (result == "need-permission")
                        return "Allow installs from TAKXR, then tap Install again";
                    if (result == "started")
                        return "Confirm the Android install prompt";
                    return "Update file missing";
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakXr] install: " + ex.Message);
                return "Could not open installer";
            }
#else
            Debug.Log("[TakXr] update APK saved at " + file);
            return "Installer runs on the headset";
#endif
        }

        void Notify() => Changed?.Invoke();

        public static string Normalize(string version)
        {
            if (string.IsNullOrEmpty(version)) return "";
            version = version.Trim();
            if (version.Length > 0 && (version[0] == 'v' || version[0] == 'V'))
                version = version.Substring(1);
            return version;
        }

        public static bool IsNewer(string latest, string current)
        {
            var a = Parts(latest);
            var b = Parts(current);
            for (int i = 0; i < 3; i++)
            {
                if (a[i] != b[i]) return a[i] > b[i];
            }
            return false;
        }

        static int[] Parts(string version)
        {
            var n = new int[3];
            var bits = Normalize(version).Split('.');
            for (int i = 0; i < 3 && i < bits.Length; i++)
                int.TryParse(bits[i], out n[i]);
            return n;
        }
    }
}
