using System;
using TakXr.Core;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.Cot
{
    /// <summary>
    /// Publishes periodic self SA CoT over the direct TAK stream and upserts a
    /// local marker so the operator appears on the COP. Self is an OBSERVER
    /// (sensor/observation point, TakIdentity.ObserverCotType) — not a ground
    /// unit — with a &lt;sensor&gt; detail carrying the current gaze heading so
    /// ATAK/CloudTAK draw a look-direction FOV cone.
    /// </summary>
    public class SelfPresence : MonoBehaviour
    {
        const float PublishIntervalSec = 7f;
        /// <summary>Approx. headset horizontal FOV published in the sensor detail.</summary>
        const float SensorFovDeg = 70f;
        /// <summary>Nominal observation range (m) for the ATAK FOV cone.</summary>
        const float SensorRangeM = 500f;

        AppConfig _config;
        CotFeedClient _feed;
        TakDirectHub _direct;
        XrWorldRoot _world;
        Transform _cam;
        DemTerrainMap _terrain;
        float _nextPublish;
        bool _paused;

        public string SelfUid => TakIdentity.ClientUid;
        public bool IsRunning => !_paused && isActiveAndEnabled;

        public void Configure(
            AppConfig config,
            CotFeedClient feed,
            TakDirectHub direct,
            XrWorldRoot world,
            Transform cam,
            DemTerrainMap terrain = null)
        {
            _config = config;
            _feed = feed;
            _direct = direct;
            _world = world;
            _cam = cam;
            _terrain = terrain;
            _nextPublish = 0f;
        }

        public void Resume()
        {
            _paused = false;
            _nextPublish = 0f;
        }

        public void Pause() => _paused = true;

        void Update()
        {
            if (_paused || _config == null || _feed == null) return;
            if (Time.unscaledTime < _nextPublish) return;
            _nextPublish = Time.unscaledTime + PublishIntervalSec;
            PublishOnce();
        }

        public void PublishOnce()
        {
            if (_config == null || _feed == null) return;
            ApproximateViewerGeo(out double lat, out double lon, out float altM);
            GazeAngles(out float headingDeg, out float pitchDeg);
            var id = TakIdentity.Load();
            var now = DateTime.UtcNow;
            var cot = new NormalizedCot
            {
                uid = id.clientUid,
                type = string.IsNullOrEmpty(id.cotType) ? TakIdentity.ObserverCotType : id.cotType,
                how = "m-g",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddSeconds(30).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint
                {
                    lat = lat,
                    lon = lon,
                    hae = altM,
                    ce = 9999999f,
                    le = 9999999f,
                },
                contact = new CotContact
                {
                    callsign = id.callsign,
                    // Intentionally omit endpoint: ATAK treats endpoint SA as a
                    // Contacts team-dot and ignores usericon. Without endpoint the
                    // Generic Icons man glyph (below) is shown instead.
                },
                detail = new CotDetail
                {
                    team = new CotTeam
                    {
                        name = id.team,
                        role = string.IsNullOrEmpty(id.role) ? "Team Member" : id.role,
                    },
                    remarks = "VRTAK XR observer",
                    takv = new CotTakv
                    {
                        platform = TakIdentity.ObserverPlatform,
                        version = Application.version,
                        device = SystemInfo.deviceModel,
                        os = "Android XR",
                    },
                    // Gaze direction as a TAK sensor block — ATAK/CloudTAK render
                    // an FOV cone showing where this observer is looking.
                    sensor = new CotSensor
                    {
                        azimuth = headingDeg,
                        fov = SensorFovDeg,
                        range = SensorRangeM,
                        elevation = pitchDeg,
                    },
                    // ATAK Generic Icons → Shapes/man.png (same icon as "man 1").
                    userIcon = new CotUserIcon
                    {
                        iconsetpath = TakIdentity.ManIconsetPath,
                    },
                },
            };

            // Local marker first so self appears even if the stream is down.
            _feed.UpsertDirect(cot);
            if (_direct != null && _direct.IsConnected)
            {
                var xml = CotXmlBuilder.Build(cot);
                _direct.SendCot(xml);
            }
        }

        /// <summary>
        /// Camera gaze as compass heading + pitch, in the map's geographic frame
        /// (same math as XrChromeHud.UpdateCompass: geographic north = world-root
        /// +Z twisted by map yaw).
        /// </summary>
        void GazeAngles(out float headingDeg, out float pitchDeg)
        {
            headingDeg = 0f;
            pitchDeg = 0f;
            if (_world == null || _cam == null) return;

            var north = _world.Root.TransformDirection(Vector3.forward);
            var east = _world.Root.TransformDirection(Vector3.right);
            north.y = 0f;
            east.y = 0f;
            if (north.sqrMagnitude < 1e-6f || east.sqrMagnitude < 1e-6f) return;
            north.Normalize();
            east.Normalize();

            var fwd = _cam.forward;
            pitchDeg = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();
            headingDeg = Mathf.Atan2(Vector3.Dot(fwd, east), Vector3.Dot(fwd, north)) * Mathf.Rad2Deg;
            if (headingDeg < 0f) headingDeg += 360f;
        }

        void ApproximateViewerGeo(out double lat, out double lon, out float altM)
        {
            lat = _config != null ? _config.originLat : 0;
            lon = _config != null ? _config.originLon : 0;
            altM = _config != null ? (float)_config.originAlt : 0f;
            if (_world == null || _cam == null || _config == null) return;

            var local = _world.Root.InverseTransformPoint(_cam.position);
            const double mPerDegLat = 111320.0;
            double mPerDegLon = mPerDegLat * Math.Cos(_config.originLat * Math.PI / 180.0);
            if (Math.Abs(mPerDegLon) < 1e-3) mPerDegLon = mPerDegLat;
            lat = _config.originLat + local.z / mPerDegLat;
            lon = _config.originLon + local.x / mPerDegLon;
            if (_terrain != null && _terrain.TrySampleHae(lat, lon, out var demHae))
                altM = demHae;
            else
                altM = Mathf.Max(0f, _cam.position.y - _world.Root.position.y);
        }
    }
}
