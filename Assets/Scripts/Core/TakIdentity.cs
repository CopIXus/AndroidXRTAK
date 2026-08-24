using System;
using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// PlayerPrefs identity for self SA / Marti presence: callsign, team, CoT type,
    /// and a stable client UID shared with SetActiveGroups.
    /// </summary>
    public static class TakIdentity
    {
        const string PrefsKey = "takxr.identity";

        /// <summary>
        /// Self CoT type: TAK sensor/observation point ("Observer (VR)"), NOT a
        /// ground unit — the XR user is an observer, not a troop on the ground.
        /// ATAK/CloudTAK render it as a sensor point and honor the &lt;sensor&gt;
        /// detail (azimuth/fov/range) as a look-direction FOV cone.
        /// </summary>
        public const string ObserverCotType = "b-m-p-s-p-loc";
        /// <summary>Settings-visible label for <see cref="ObserverCotType"/>.</summary>
        public const string ObserverTypeLabel = "Observer (VR)";
        /// <summary>&lt;takv platform/&gt; value identifying VRTAK XR clients.</summary>
        public const string ObserverPlatform = "VRTAK-XR";
        /// <summary>
        /// ATAK Generic Icons stick-figure man — matches the in-app "Generic Icons"
        /// picker (Shapes/man.png). Published as &lt;usericon iconsetpath=…/&gt; so
        /// ATAK/WinTAK render the man glyph instead of a team-color SA dot.
        /// </summary>
        public const string ManIconsetPath =
            "ad78aafb-83a6-4c07-b2b9-a897a8b6a38f/Shapes/man.png";
        /// <summary>Pre-observer default (ground unit) — migrated on Load.</summary>
        const string LegacyGroundType = "a-f-G-U-C";

        public static readonly string[] TeamColors =
        {
            "Cyan", "White", "Yellow", "Orange", "Magenta", "Red", "Maroon",
            "Purple", "Dark Blue", "Blue", "Teal", "Green", "Dark Green", "Brown",
        };

        [Serializable]
        public class State
        {
            public string callsign = "XR-USER";
            public string team = "Cyan";
            public string role = "Team Member";
            /// <summary>CoT type for self presence (default: VR observer).</summary>
            public string cotType = ObserverCotType;
            public string clientUid;
        }

        static State _cache;

        public static State Load()
        {
            if (_cache != null) return _cache;
            var json = PlayerPrefs.GetString(PrefsKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { _cache = JsonUtility.FromJson<State>(json); }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TakIdentity] prefs parse: " + ex.Message);
                }
            }
            if (_cache == null) _cache = new State();
            EnsureClientUid(_cache);
            if (string.IsNullOrEmpty(_cache.callsign)) _cache.callsign = "XR-USER";
            if (string.IsNullOrEmpty(_cache.team)) _cache.team = "Cyan";
            if (string.IsNullOrEmpty(_cache.role)) _cache.role = "Team Member";
            // Migrate the old ground-unit default to the observer type — the XR
            // user should never read as a troop on the ground in ATAK/CloudTAK.
            if (string.IsNullOrEmpty(_cache.cotType) || _cache.cotType == LegacyGroundType)
                _cache.cotType = ObserverCotType;
            return _cache;
        }

        public static void Save(State state = null)
        {
            _cache = state ?? _cache ?? Load();
            EnsureClientUid(_cache);
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(_cache));
            PlayerPrefs.Save();
            TakXrStateStore.Capture();
        }

        public static string ClientUid => Load().clientUid;
        public static string Callsign => Load().callsign;
        public static string Team => Load().team;
        public static string Role => Load().role;
        public static string CotType => Load().cotType;

        public static void SetCallsign(string v)
        {
            var s = Load();
            s.callsign = string.IsNullOrEmpty(v) ? "XR-USER" : v.Trim();
            Save(s);
        }

        public static void CycleTeam(int delta = 1)
        {
            var s = Load();
            int idx = 0;
            for (int i = 0; i < TeamColors.Length; i++)
                if (string.Equals(TeamColors[i], s.team, StringComparison.OrdinalIgnoreCase))
                { idx = i; break; }
            idx = ((idx + delta) % TeamColors.Length + TeamColors.Length) % TeamColors.Length;
            s.team = TeamColors[idx];
            Save(s);
        }

        public static void SetTeam(string team)
        {
            var s = Load();
            s.team = string.IsNullOrEmpty(team) ? "Cyan" : team;
            Save(s);
        }

        public static void SetCotType(string type)
        {
            var s = Load();
            s.cotType = string.IsNullOrEmpty(type) ? ObserverCotType : type;
            Save(s);
        }

        static void EnsureClientUid(State s)
        {
            if (!string.IsNullOrEmpty(s.clientUid)) return;
            var dev = SystemInfo.deviceUniqueIdentifier;
            var shortDev = string.IsNullOrEmpty(dev)
                ? Guid.NewGuid().ToString("N").Substring(0, 12)
                : dev.Substring(0, Math.Min(12, dev.Length));
            s.clientUid = "takxr-" + shortDev;
        }
    }
}
