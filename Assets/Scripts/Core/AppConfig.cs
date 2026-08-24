using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// Runtime config for the headset APK. Backend URL defaults to the public COP.
    /// </summary>
    [CreateAssetMenu(fileName = "AppConfig", menuName = "TAKXR/App Config")]
    public class AppConfig : ScriptableObject
    {
        [Header("Backend")]
        [Tooltip("HTTPS origin of TAKXR backend (no trailing slash).")]
        public string backendBaseUrl = "";

        [Header("Direct TAK server (standalone mode)")]
        [Tooltip("Connect straight to the TAK server CoT stream — no backend required.")]
        public bool takDirectEnabled = true;
        [Tooltip("If true, auto-switch to LXC backend feed when direct TAK never connects. Default false (standalone).")]
        public bool allowBackendFallback = false;
        [Tooltip("Active TAK host (runtime). Multi-server list lives in PlayerPrefs via TakServerDirectory.")]
        public string takHost = "";
        public int takPort = 8089;
        [Tooltip("Marti REST API port (channels / missions / packages), cert auth.")]
        public int takMartiPort = 8443;
        [Tooltip("Marti cert enrollment port (username/password → P12).")]
        public int takEnrollPort = 8446;
        [Tooltip("PKCS#12 client cert bundle in StreamingAssets (from TAK enrollment).")]
        public string takClientP12 = "takclient.p12";
        public string takClientP12Password = "";

        [Tooltip("Optional Ion token override; otherwise fetched from /api/config/public.")]
        public string cesiumIonToken = "";

        [Header("AOI / clustering")]
        [Tooltip("Map focus radius and ground-CoT cluster enter distance (meters). 100 miles.")]
        public float mapRadiusMeters = 100f * 1609.344f;

        [Tooltip("Once clustered, stay clustered until inside this radius (hysteresis). ~90 miles.")]
        public float clusterExitMeters = 90f * 1609.344f;

        [Tooltip("Lat/lon cell size (degrees) for far-CoT geo buckets.")]
        public float clusterGridDegrees = 0.5f;
        [Tooltip("Minimum members before a far cell becomes a bubble (singles stay individual).")]
        public int clusterMinCount = 2;

        /// <summary>Ground CoTs beyond this horizontal range from the viewer collapse into bubbles.</summary>
        public float ClusterEnterMeters => mapRadiusMeters;
        /// <summary>Uncluster hysteresis radius (never above enter distance).</summary>
        public float ClusterExitMeters =>
            clusterExitMeters > 0f && clusterExitMeters < mapRadiusMeters
                ? clusterExitMeters
                : mapRadiusMeters * 0.9f;

        [Header("Map")]
        public bool showMap = true;
        public bool allowHighAltitudeCots = false;

        [Header("Cesium XR budgets")]
        public float tilesetMaximumScreenSpaceError = 80f;
        public float tilesetMovingScreenSpaceError = 120f;
        public int tilesetMaximumSimultaneousTileLoads = 4;
        public long tilesetMaximumCachedBytes = 100L * 1024L * 1024L;

        [Header("Origin (degrees / meters HAE)")]
        public double originLat = 36.29571;
        public double originLon = -82.19937;
        // Approximate local terrain HAE so Cesium ground ≈ Unity Y=0 (TN ridge ~480 m).
        public double originAlt = 480;

        [Header("Camera")]
        [Tooltip("Start height above origin (meters) so the map is visible under the headset.")]
        public float startCameraHeightMeters = 420f;
        [Tooltip("Start distance south of origin (meters).")]
        public float startCameraBackMeters = 520f;

        public string CotSnapshotUrl => $"{backendBaseUrl.TrimEnd('/')}/api/cot/current";
        public string PublicConfigUrl => $"{backendBaseUrl.TrimEnd('/')}/api/config/public";
        public string HealthUrl => $"{backendBaseUrl.TrimEnd('/')}/api/health";
        public string WsUrl
        {
            get
            {
                var u = backendBaseUrl.TrimEnd('/');
                if (u.StartsWith("https://")) return "wss://" + u.Substring("https://".Length) + "/ws";
                if (u.StartsWith("http://")) return "ws://" + u.Substring("http://".Length) + "/ws";
                return u + "/ws";
            }
        }

        /// <summary>
        /// Absolute HTTP(S) icon URLs only. Relative paths are resolved locally via
        /// IconResolver (StreamingAssets) — never rewritten to the LXC backend.
        /// </summary>
        public string ResolveIconUrl(string iconUrl)
        {
            if (string.IsNullOrEmpty(iconUrl)) return null;
            if (iconUrl.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase) ||
                iconUrl.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
                return iconUrl;
            return null;
        }
    }
}
