using System.Collections;
using TakXr.Cot;
using TakXr.Locomotion;
using TakXr.Map;
using TakXr.UI;
using TakXr.Xr;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;

namespace TakXr.Core
{
    /// <summary>
    /// XR Origin (HMD) + XrWorldRoot (map/CoTs). DEM+ESRI terrain, Hand Interaction
    /// locomotion, CoT select/info/Follow/video, ATAK chrome.
    /// </summary>
    public class TakXrBootstrap : MonoBehaviour
    {
        [SerializeField] AppConfig config;
        [SerializeField] bool startWithMap = true;
        [SerializeField] bool startFeedOnAwake = true;

        CotFeedClient _feed;
        TakDirectHub _direct;
        TakLayersService _layers;
        CotLayerController _cotLayer;
        CotShapeRenderer _shapes;
        XrLayersPanel _layersPanel;
        XrDrawTool _drawTool;
        CesiumMapController _map;
        DemTerrainMap _terrain;
        XrWorldLocomotion _loco;
        XrFollowController _follow;
        XrCopController _cop;
        XrInfoPanel _info;
        XrVideoPanel _video;
        XrRadialMenu _radial;
        SettingsPanelRuntime _settings;
        HeadsetHud _hud;
        XrChromeHud _chrome;
        BootHud _boot;
        Camera _cam;
        XROrigin _xrOrigin;
        XrWorldRoot _world;
        SelfPresence _selfPresence;

        void Awake()
        {
            Application.targetFrameRate = 72;
            QualitySettings.vSyncCount = 0;
            _boot = BootHud.Create();
            _boot.SetStatus("TAKXR", "Booting…");

            try
            {
                if (config == null)
                    config = Resources.Load<AppConfig>("AppConfig");
                if (config == null)
                    config = ScriptableObject.CreateInstance<AppConfig>();

                EnsureLighting();
                _xrOrigin = XrRigBuilder.EnsureRig(out _cam);
                _world = XrWorldRoot.Ensure();
                XrInputVisuals.Ensure(_xrOrigin.transform);

                config.showMap = false;
                _boot.SetStatus("TAKXR", $"XR ready\n{config.backendBaseUrl}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TakXr] Awake: {ex}");
                _boot?.SetStatus("TAKXR ERROR", ex.Message);
            }
        }

        IEnumerator Start()
        {
            yield return null;
            yield return null;

            try
            {
                yield return LocalConfigLoader.ApplyAsync(config);
                WireSystems();
                ApplyOverviewFacingNorth();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TakXr] WireSystems: {ex}");
                _boot?.SetStatus("TAKXR ERROR", ex.ToString());
                yield break;
            }

            if (startFeedOnAwake)
            {
                if (config.takDirectEnabled && _direct != null)
                {
                    // Standalone: CoTs stream straight from the TAK server.
                    // Backend fallback is opt-in (AppConfig.allowBackendFallback).
                    _feed?.StartDirectMode();
                    _direct.StartClient();
                    if (config.allowBackendFallback)
                        StartCoroutine(DirectFallback());
                }
                else
                {
                    _feed?.StartFeed();
                }
            }

            yield return new WaitForSeconds(0.25f);
            if (startWithMap)
            {
                config.showMap = false;
                _map?.SetMapEnabled(false);
                if (_terrain != null)
                {
                    _terrain.Configure(config);
                    _boot.SetStatus("TAKXR", "Loading 3D terrain (DEM + satellite)…");
                }
            }

            yield return new WaitForSeconds(1f);
            ApplyOverviewFacingNorth();

            _boot?.SetPanelVisible(false);
            InvokeRepeating(nameof(UpdateBootStatus), 1f, 2f);
        }

        void WireSystems()
        {
            if (_world == null) _world = XrWorldRoot.Ensure();
            if (_cam == null) _xrOrigin = XrRigBuilder.EnsureRig(out _cam);

            TakXrStateStore.RestorePrefsIfEmpty();

            // Persist / restore multi-server list; apply active host onto AppConfig
            // before CoT + Marti clients start.
            var activeServer = TakServerDirectory.EnsureApplied(config);
            if (activeServer != null)
                Debug.Log($"[TakXr] active TAK server {activeServer.displayName} · {activeServer.EndpointLabel}");

            _feed = gameObject.GetComponent<CotFeedClient>() ?? gameObject.AddComponent<CotFeedClient>();
            _feed.Configure(config);

            _direct = gameObject.GetComponent<TakDirectHub>() ?? gameObject.AddComponent<TakDirectHub>();
            _direct.Configure(config, _feed);

            // Channels / data packages / missions — Marti API, standalone.
            _layers = gameObject.GetComponent<TakLayersService>() ?? gameObject.AddComponent<TakLayersService>();
            _layers.Configure(config, _feed, _direct);

            _selfPresence = gameObject.GetComponent<SelfPresence>() ?? gameObject.AddComponent<SelfPresence>();
            // Wired after cam/world/terrain exist — Configure below after terrain.

            _map = _world.GetComponent<CesiumMapController>() ?? _world.gameObject.AddComponent<CesiumMapController>();
            config.showMap = false;
            _map.Configure(config);
            _map.SetFallbackVisible(false);

            var flat = _world.GetComponent<EsriTileMap>();
            if (flat != null)
            {
                flat.Clear();
                Destroy(flat);
            }

            _terrain = _world.GetComponent<DemTerrainMap>() ?? _world.gameObject.AddComponent<DemTerrainMap>();
            _terrain.SetViewer(_cam != null ? _cam.transform : null);
            _terrain.Configure(config);

            _settings = gameObject.GetComponent<SettingsPanelRuntime>() ??
                        gameObject.AddComponent<SettingsPanelRuntime>();
            _settings.Configure(config, _terrain);
            CotMarkerView.ScaleMultiplier = _settings.IconScaleMultiplier;
            CotMarkerView.LabelScaleMultiplier = _settings.TextScaleMultiplier;

            _cotLayer = _world.GetComponent<CotLayerController>() ?? _world.gameObject.AddComponent<CotLayerController>();
            _cotLayer.Configure(config, _feed, _cam);
            _cotLayer.SetTerrain(_terrain);
            _cotLayer.SetOrigin(new GeoMath.Geodetic(config.originLat, config.originLon, config.originAlt));

            // Drawing CoTs (routes/polygons/circles) as terrain lines.
            _shapes = _world.GetComponent<CotShapeRenderer>() ?? _world.gameObject.AddComponent<CotShapeRenderer>();
            _shapes.Configure(config, _feed, _world, _terrain);

            _loco = gameObject.GetComponent<XrWorldLocomotion>() ?? gameObject.AddComponent<XrWorldLocomotion>();
            _loco.Configure(_world, _cam.transform, _map);
            _loco.SetSpeedMultiplier(_settings.MoveSpeedMultiplier);

            if (_settings != null)
            {
                _loco.SetSavedPitch(_settings.WorldPitchDeg);
                _loco.SetPitchChangedHandler(deg => _settings.SetWorldPitch(deg));
            }

            _follow = gameObject.GetComponent<XrFollowController>() ?? gameObject.AddComponent<XrFollowController>();
            _follow.Configure(_world, _cam.transform, _cotLayer);

            _info = XrInfoPanel.Create();
            _video = XrVideoPanel.Create();

            // ATAK-style radial coin menu on CoT tap (Details / Video / Follow / R&B / Delete).
            _radial = XrRadialMenu.Create();
            _radial.Configure(_cam.transform, _feed, _cotLayer, _info, _video, _follow,
                _direct, _world, msg => _chrome?.FlashStatus(msg));

            _cop = gameObject.GetComponent<XrCopController>() ?? gameObject.AddComponent<XrCopController>();
            _cop.Configure(_cotLayer, _feed, _cam.transform, _loco, _info, _video, _follow, _radial);

            var legacy = gameObject.GetComponent<XrLocomotionController>();
            if (legacy != null) legacy.enabled = false;

            _hud = gameObject.GetComponent<HeadsetHud>() ?? gameObject.AddComponent<HeadsetHud>();
            _hud.Configure(config, _feed, _map, _loco);
            _hud.enabled = false;

            _layersPanel = XrLayersPanel.Create();
            _layersPanel.Configure(_layers, _cam.transform);

            var basemapPanel = XrBasemapPanel.Create();
            basemapPanel.Configure(_terrain, _cam.transform);

            var serverPanel = XrServerPanel.Create();
            serverPanel.Configure(config, _direct, _layers, _feed, _cam.transform);

            var settingsPanel = XrSettingsPanel.Create();
            settingsPanel.Configure(config, _settings, _loco, _cam.transform,
                msg => _chrome?.FlashStatus(msg),
                onOriginHere: SetOriginToViewer,
                onFitTracks: FitVisibleTracks,
                selfPresence: _selfPresence);

            _drawTool = gameObject.GetComponent<XrDrawTool>() ?? gameObject.AddComponent<XrDrawTool>();

            var goToPanel = XrGoToPanel.Create();
            var videoBrowser = XrVideoBrowser.Create();
            var trackHistory = XrTrackHistory.Create();
            var rangeTool = XrRangeMeasureTool.Create();
            var elevationTool = XrElevationTool.Create();

            _chrome = XrChromeHud.Create();
            _chrome.Configure(config, _cam.transform, _terrain, _loco, _world, _settings,
                _feed, _cotLayer, _follow, _direct, _layersPanel, _drawTool,
                basemapPanel, serverPanel, settingsPanel, _radial, _cop, _info, _video,
                goToPanel, videoBrowser, trackHistory, rangeTool, elevationTool);
            serverPanel.SetFlashStatus(msg => _chrome.FlashStatus(msg));
            settingsPanel.SetFlashStatus(msg => _chrome.FlashStatus(msg));
            _drawTool.Configure(config, _feed, _direct, _world, _terrain, _cam.transform,
                msg => _chrome.FlashStatus(msg));
            goToPanel.Configure(config, _terrain, _loco, _world, _cotLayer, _cam.transform,
                msg => _chrome.FlashStatus(msg));
            videoBrowser.Configure(_feed, _video, _cam.transform, msg => _chrome.FlashStatus(msg));
            trackHistory.Configure(config, _world, _terrain, _cam.transform,
                msg => _chrome.FlashStatus(msg));
            rangeTool.Configure(config, _world, _terrain, _cam.transform,
                msg => _chrome.FlashStatus(msg));
            elevationTool.Configure(config, _feed, _direct, _world, _terrain, _cam.transform,
                msg => _chrome.FlashStatus(msg));

            _loco.SetSnapTurnEnabled(_settings != null && _settings.SnapTurnEnabled);

            _selfPresence?.Configure(config, _feed, _direct, _world, _cam.transform, _terrain);
            _selfPresence?.PublishOnce();

            var life = gameObject.GetComponent<AppLifecycleHost>() ??
                       gameObject.AddComponent<AppLifecycleHost>();
            life.Configure(_direct, _layers, _selfPresence, _chrome);

            IconResolver.EnsureLoaded();

            // Regression guard: the CoT→marker classification order is a contract
            // (observer / video / dCFS / team / aircraft / iconset / dot). Runs
            // every launch — cheap (~19 in-memory cases) — and LogErrors loudly on
            // any reorder instead of silently shipping broken icons. See CotClassifier.cs.
            CotClassifier.SelfTest();
            TakQrParser.SelfTest();

            Debug.Log("[TakXr] bootstrap ready · DEM + COP select/Follow/video + chrome");
        }

        void SetOriginToViewer()
        {
            if (config == null || _cam == null || _world == null) return;
            var local = _world.Root.InverseTransformPoint(_cam.transform.position);
            const double mPerDegLat = 111320.0;
            double mPerDegLon = mPerDegLat * System.Math.Cos(config.originLat * System.Math.PI / 180.0);
            if (System.Math.Abs(mPerDegLon) < 1e-3) mPerDegLon = mPerDegLat;
            double lat = config.originLat + local.z / mPerDegLat;
            double lon = config.originLon + local.x / mPerDegLon;
            float alt = (float)config.originAlt;
            if (_terrain != null && _terrain.TrySampleHae(lat, lon, out var demHae))
                alt = demHae;
            config.originLat = lat;
            config.originLon = lon;
            config.originAlt = alt;
            _cotLayer?.SetOrigin(new GeoMath.Geodetic(lat, lon, alt));
            _terrain?.Rebuild();
            ApplyOverviewFacingNorth();
        }

        void FitVisibleTracks()
        {
            if (_cotLayer == null || _loco == null) return;
            var pts = new System.Collections.Generic.List<UnityEngine.Vector3>(64);
            _cotLayer.CollectVisibleWorldPositions(pts);
            _loco.FitWorldPoints(pts);
        }

        static void EnsureLighting()
        {
#if UNITY_2023_1_OR_NEWER
            if (FindFirstObjectByType<Light>() != null) return;
#else
            if (FindObjectOfType<Light>() != null) return;
#endif
            var lightGo = new GameObject("TakXr Sun");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.65f, 0.8f);
            RenderSettings.ambientEquatorColor = new Color(0.4f, 0.45f, 0.4f);
            RenderSettings.ambientGroundColor = new Color(0.2f, 0.2f, 0.2f);
        }

        void ApplyOverviewFacingNorth()
        {
            if (_world == null || _cam == null) return;
            _world.ApplyInitialOverview(_cam.transform, 160f, 50f);
            _world.OrientNorth(_cam.transform);
            _loco?.RestoreSavedPitch();
        }

        IEnumerator DirectFallback()
        {
            // Opt-in only (allowBackendFallback). Direct keeps retrying either way.
            if (config == null || !config.allowBackendFallback) yield break;
            float deadline = Time.unscaledTime + 60f;
            while (Time.unscaledTime < deadline)
            {
                yield return new WaitForSeconds(5f);
                if (_direct == null) break;
                if (_direct.IsConnected) yield break;
                if (_direct.State == "error" || _direct.State == "cert-missing") break;
            }
            if (_direct != null && _direct.IsConnected) yield break;
            if (config == null || !config.allowBackendFallback) yield break;
            Debug.LogWarning($"[TakXr] direct TAK unavailable ({_direct?.State}: {_direct?.LastError}) — falling back to backend feed");
            _feed?.ExitDirectMode();
            _feed?.StartFeed();
        }

        void UpdateBootStatus()
        {
            if (_boot == null || !_boot.isActiveAndEnabled) return;
            int tracks = _feed != null ? _feed.Cots.Count : 0;
            int shown = _cotLayer != null ? _cotLayer.VisibleMarkerCount : 0;
            int tiles = _terrain != null ? _terrain.LoadedCount : 0;
            string ws = _feed != null ? _feed.ConnectionState : "?";
            string follow = _follow != null && _follow.IsFollowing ? " · FOLLOW" : "";
            _boot.SetStatus("TAKXR", $"Tracks {tracks} (shown {shown}) · WS {ws} · DEM {tiles}{follow}");
        }
    }
}
