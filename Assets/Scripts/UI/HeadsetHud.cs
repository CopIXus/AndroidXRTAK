using TakXr.Cot;
using TakXr.Core;
using TakXr.Locomotion;
using TakXr.Map;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>Simple on-screen HUD for FPS, track count, connection, map mode.</summary>
    public class HeadsetHud : MonoBehaviour
    {
        [SerializeField] AppConfig config;
        [SerializeField] CotFeedClient feed;
        [SerializeField] CesiumMapController map;
        [SerializeField] XrWorldLocomotion locomotion;

        float _fps;
        float _fpsAcc;
        int _fpsFrames;
        float _nextFpsLog;
        GUIStyle _style;

        public void Configure(AppConfig cfg, CotFeedClient f, CesiumMapController m, XrWorldLocomotion loco)
        {
            config = cfg;
            feed = f;
            map = m;
            locomotion = loco;
        }

        void Update()
        {
            _fpsAcc += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsAcc >= 0.5f)
            {
                _fps = _fpsFrames / _fpsAcc;
                _fpsAcc = 0;
                _fpsFrames = 0;
            }

            if (Time.unscaledTime >= _nextFpsLog)
            {
                _nextFpsLog = Time.unscaledTime + 5f;
                int tracks = feed != null ? feed.Cots.Count : 0;
                string mapState = map == null ? "n/a" : (config != null && config.showMap
                    ? (map.HasTerrainRelief ? "DEM" : "fallback")
                    : "off");
                string conn = feed != null ? feed.ConnectionState : "n/a";
                Debug.Log($"[FpsGate] fps={_fps:0.0} tracks={tracks} map={mapState} conn={conn}");
            }
        }

        void OnGUI()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 18,
                    normal = { textColor = Color.white }
                };
            }

            int tracks = feed != null ? feed.Cots.Count : 0;
            string mapState = map == null ? "n/a" : (config != null && config.showMap
                ? (map.HasTerrainRelief ? "Cesium terrain" : "fallback")
                : "off");
            string conn = feed != null ? feed.ConnectionState : "n/a";

            GUI.Box(new Rect(12, 12, 420, 110), GUIContent.none);
            GUI.Label(new Rect(20, 18, 400, 24), $"TAKXR APK  FPS {_fps:0.0}", _style);
            GUI.Label(new Rect(20, 42, 400, 24), $"Tracks {tracks}  WS {conn}", _style);
            GUI.Label(new Rect(20, 66, 400, 24), $"Map {mapState}  AOI {(config != null ? config.mapRadiusMeters / 1609.344f : 0):0} mi", _style);
            string cesium = map != null && map.CesiumAvailable ? "cesium-ready" : "no-cesium-pkg";
            GUI.Label(new Rect(20, 90, 400, 24), $"Pinch/stretch/sticks · {cesium}", _style);

            if (GUI.Button(new Rect(12, 130, 140, 36), "Toggle Map") && config != null && map != null)
                map.SetMapEnabled(!config.showMap);
            if (GUI.Button(new Rect(160, 130, 140, 36), "Orient North"))
                locomotion?.OrientNorth();
            if (GUI.Button(new Rect(308, 130, 120, 36), "Fly Fwd"))
                locomotion?.TeleportForward(120f);
        }
    }
}
