using System;
using System.Collections;
using System.Reflection;
using TakXr.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Map
{
    /// <summary>
    /// Cesium World Terrain + ESRI with XR budgets. Never hides the visible fallback
    /// floor until an Ion token is present — otherwise the failed tileset is a black void.
    /// </summary>
    public class CesiumMapController : MonoBehaviour
    {
        const long WorldTerrainAssetId = 1;
        // Match web XrTileMap URL (server.arcgisonline.com).
        const string EsriWorldImageryTemplate =
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

        [SerializeField] AppConfig config;
        [SerializeField] Transform fallbackFloor;
        [SerializeField] bool mapEnabled = true;

        float _moveSseBoostUntil;
        Vector3 _lastPos;
        bool _ionFetchStarted;
        bool _ionReady;

        Component _tileset;
        Component _georeference;
        Type _tilesetType;
        Type _georeferenceType;
        Type _dataSourceType;
        Type _urlOverlayType;
        bool _cesiumTypesResolved;

        public bool HasTerrainRelief { get; private set; }
        public bool CesiumAvailable => _cesiumTypesResolved && _tilesetType != null;

        public void Configure(AppConfig cfg)
        {
            config = cfg;
            mapEnabled = cfg.showMap;
            ResolveCesiumTypes();
            EnsureFallback(true);
            HasTerrainRelief = false;

            if (!_ionFetchStarted)
            {
                _ionFetchStarted = true;
                StartCoroutine(FetchIonTokenThenMaybeEnableMap());
            }
            else
            {
                EnsureMap();
            }
        }

        public void SetMapEnabled(bool enabled)
        {
            mapEnabled = enabled;
            if (config != null) config.showMap = enabled;
            EnsureMap();
        }

        public void SetFallbackVisible(bool visible)
        {
            if (fallbackFloor != null)
                fallbackFloor.gameObject.SetActive(visible);
        }

        public void ApplyOrigin(double lat, double lon, double alt)
        {
            if (config != null)
            {
                config.originLat = lat;
                config.originLon = lon;
                config.originAlt = alt;
            }
            if (_georeference != null)
            {
                SetProp(_georeference, "latitude", lat);
                SetProp(_georeference, "longitude", lon);
                SetProp(_georeference, "height", alt);
            }
        }

        public void NotifyMoving(bool moving)
        {
            if (moving) _moveSseBoostUntil = Time.time + 0.75f;
            ApplyBudgets(moving || Time.time < _moveSseBoostUntil);
        }

        void Update()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var p = cam.transform.position;
            float speed = (p - _lastPos).magnitude / Mathf.Max(Time.deltaTime, 1e-4f);
            _lastPos = p;
            bool moving = speed > 2f;
            if (moving) _moveSseBoostUntil = Time.time + 0.75f;
            ApplyBudgets(moving || Time.time < _moveSseBoostUntil);
        }

        void ResolveCesiumTypes()
        {
            if (_cesiumTypesResolved) return;
            _cesiumTypesResolved = true;
            _georeferenceType = FindType("CesiumForUnity.CesiumGeoreference");
            _tilesetType = FindType("CesiumForUnity.Cesium3DTileset");
            _dataSourceType = FindType("CesiumForUnity.CesiumDataSource");
            _urlOverlayType = FindType("CesiumForUnity.CesiumUrlTemplateRasterOverlay");
            if (_tilesetType == null)
                Debug.Log("[CesiumMap] Cesium for Unity not loaded — using fallback floor.");
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { /* ignore */ }
            }
            return null;
        }

        IEnumerator FetchIonTokenThenMaybeEnableMap()
        {
            if (config != null && !string.IsNullOrEmpty(config.cesiumIonToken))
            {
                _ionReady = true;
                EnsureMap();
                yield break;
            }

            using var req = UnityWebRequest.Get(config.PublicConfigUrl);
            req.timeout = 20;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[CesiumMap] public config fetch failed: {req.error} — keeping fallback floor");
                EnsureFallback(true);
                HasTerrainRelief = false;
                yield break;
            }

            // Backend may use cesiumIonToken; also accept camelCase variants via string search.
            var text = req.downloadHandler.text ?? "";
            string token = null;
            try
            {
                var pub = JsonUtility.FromJson<PublicConfigDto>(text);
                token = pub?.cesiumIonToken;
            }
            catch { /* ignore */ }

            if (string.IsNullOrEmpty(token))
                token = ExtractJsonString(text, "cesiumIonToken");

            if (!string.IsNullOrEmpty(token))
            {
                config.cesiumIonToken = token;
                _ionReady = true;
                Debug.Log($"[CesiumMap] Ion token applied from /api/config/public (len={token.Length})");
                EnsureMap();
            }
            else
            {
                Debug.LogWarning("[CesiumMap] No cesiumIonToken in public config — keeping fallback floor");
                EnsureFallback(true);
                HasTerrainRelief = false;
            }
        }

        static string ExtractJsonString(string json, string key)
        {
            var marker = $"\"{key}\":\"";
            var i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            i += marker.Length;
            var j = json.IndexOf('"', i);
            return j < 0 ? null : json.Substring(i, j - i);
        }

        void EnsureMap()
        {
            ResolveCesiumTypes();
            if (!mapEnabled)
            {
                SetTilesetVisible(false);
                EnsureFallback(true);
                HasTerrainRelief = false;
                return;
            }

            // Critical: do not spawn Cesium (or hide the floor) without a token —
            // a 401 tileset renders as a black void on device.
            if (!_ionReady || config == null || string.IsNullOrEmpty(config.cesiumIonToken))
            {
                SetTilesetVisible(false);
                EnsureFallback(true);
                HasTerrainRelief = false;
                Debug.Log("[CesiumMap] Map requested but Ion token not ready — showing fallback floor");
                return;
            }

            if (_tilesetType != null)
            {
                EnsureGeoreference();
                EnsureTileset();
                if (_tileset != null)
                {
                    ApplyIonToken(config.cesiumIonToken);
                    SetTilesetVisible(true);
                    // Keep fallback under the tileset until relief is confirmed useful;
                    // still visible if Cesium fails to stream.
                    EnsureFallback(true);
                    HasTerrainRelief = true;
                    ApplyBudgets(moving: false);
                    return;
                }
            }

            EnsureFallback(true);
            HasTerrainRelief = false;
        }

        void EnsureGeoreference()
        {
            if (_georeferenceType == null) return;
            if (_georeference == null)
                _georeference = FindObjectOfTypeSafe(_georeferenceType);
            if (_georeference != null)
            {
                if (config != null)
                {
                    SetProp(_georeference, "latitude", config.originLat);
                    SetProp(_georeference, "longitude", config.originLon);
                    SetProp(_georeference, "height", config.originAlt);
                }
                return;
            }

            var go = new GameObject("CesiumGeoreference");
            go.transform.SetParent(transform, false);
            _georeference = go.AddComponent(_georeferenceType);
            if (config != null)
            {
                SetProp(_georeference, "latitude", config.originLat);
                SetProp(_georeference, "longitude", config.originLon);
                SetProp(_georeference, "height", config.originAlt);
            }
        }

        void EnsureTileset()
        {
            if (_tilesetType == null) return;
            if (_tileset == null)
                _tileset = FindObjectOfTypeSafe(_tilesetType);
            if (_tileset != null)
            {
                EnsureEsriOverlay(_tileset.gameObject);
                ApplyIonToken(config != null ? config.cesiumIonToken : null);
                return;
            }

            if (_georeference == null) return;

            var go = new GameObject("CesiumWorldTerrain");
            go.transform.SetParent(_georeference.transform, false);
            _tileset = go.AddComponent(_tilesetType);
            if (_dataSourceType != null)
            {
                var fromIon = Enum.Parse(_dataSourceType, "FromCesiumIon");
                SetProp(_tileset, "tilesetSource", fromIon);
            }
            SetProp(_tileset, "ionAssetID", WorldTerrainAssetId);
            ApplyIonToken(config != null ? config.cesiumIonToken : null);
            EnsureEsriOverlay(go);
            Debug.Log("[CesiumMap] Created World Terrain tileset + ESRI imagery overlay");
        }

        void EnsureEsriOverlay(GameObject tilesetGo)
        {
            if (_urlOverlayType == null || tilesetGo == null) return;
            if (tilesetGo.GetComponent(_urlOverlayType) != null) return;
            var overlay = tilesetGo.AddComponent(_urlOverlayType);
            SetProp(overlay, "templateUrl", EsriWorldImageryTemplate);
        }

        void ApplyIonToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            if (_tileset != null)
                SetProp(_tileset, "ionAccessToken", token);

            // Also stamp CesiumRuntimeSettings default so overlays/plugins see the token.
            try
            {
                var settingsType = FindType("CesiumForUnity.CesiumRuntimeSettings");
                if (settingsType == null) return;
                var instanceProp = settingsType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                var settings = instanceProp?.GetValue(null);
                if (settings == null) return;
                SetProp(settings, "defaultIonAccessToken", token);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CesiumMap] CesiumRuntimeSettings token: {ex.Message}");
            }
        }

        void ApplyBudgets(bool moving)
        {
            if (config == null || _tileset == null) return;
            SetProp(_tileset, "maximumScreenSpaceError",
                moving ? config.tilesetMovingScreenSpaceError : config.tilesetMaximumScreenSpaceError);
            SetProp(_tileset, "maximumSimultaneousTileLoads", config.tilesetMaximumSimultaneousTileLoads);
            SetProp(_tileset, "maximumCachedBytes", config.tilesetMaximumCachedBytes);
            SetProp(_tileset, "preloadAncestors", false);
            SetProp(_tileset, "preloadSiblings", false);
            SetProp(_tileset, "enableFrustumCulling", true);
        }

        void SetTilesetVisible(bool visible)
        {
            if (_tileset == null) return;
            _tileset.gameObject.SetActive(visible);
        }

        void EnsureFallback(bool show)
        {
            if (fallbackFloor == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
                go.name = "FallbackFloor";
                // Stay under world root so pinch locomotion moves the floor with the map.
                go.transform.SetParent(transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                // Unity Plane is 10m; ×200 → ~2 km square under the AOI.
                go.transform.localScale = Vector3.one * 200f;
                // Dark placeholder under ESRI tiles — no yellow pillar.
                TintRenderer(go.GetComponent<Renderer>(), new Color(0.12f, 0.18f, 0.16f, 1f));
                fallbackFloor = go.transform;
            }
            fallbackFloor.gameObject.SetActive(show);
        }

        /// <summary>
        /// Never call new Material(null) — Shader.Find often returns null on IL2CPP Android
        /// when URP shaders are stripped. Fall back to the primitive's built-in material.
        /// </summary>
        static void TintRenderer(Renderer r, Color c)
        {
            if (r == null) return;
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("UI/Default")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Hidden/InternalErrorShader");
            if (shader != null)
            {
                try { r.material = new Material(shader); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CesiumMap] Material create failed: {ex.Message}");
                }
            }
            // If shader lookup failed, keep CreatePrimitive's default material.
            if (r.material == null) return;
            if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
            if (r.material.HasProperty("_Color")) r.material.SetColor("_Color", c);
            else
            {
                try { r.material.color = c; } catch { /* ignore */ }
            }
        }

        static Component FindObjectOfTypeSafe(Type t)
        {
#if UNITY_2023_1_OR_NEWER
            return FindFirstObjectByType(t) as Component;
#else
            return FindObjectOfType(t) as Component;
#endif
        }

        static void SetProp(object target, string name, object value)
        {
            if (target == null) return;
            var t = target.GetType();
            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.CanWrite)
            {
                var converted = value;
                if (value != null && prop.PropertyType != value.GetType())
                {
                    try { converted = Convert.ChangeType(value, prop.PropertyType); }
                    catch { converted = value; }
                }
                prop.SetValue(target, converted);
                return;
            }
            var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                var converted = value;
                if (value != null && field.FieldType != value.GetType())
                {
                    try { converted = Convert.ChangeType(value, field.FieldType); }
                    catch { converted = value; }
                }
                field.SetValue(target, converted);
            }
        }

        [Serializable]
        class PublicConfigDto
        {
            public string cesiumIonToken;
        }
    }
}
