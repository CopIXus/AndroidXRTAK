using System;
using System.Collections.Generic;

namespace TakXr.Cot
{
    [Serializable]
    public class CotPoint
    {
        public double lat;
        public double lon;
        public float hae;
        public float ce;
        public float le;
    }

    [Serializable]
    public class CotContact
    {
        public string callsign;
        public string endpoint;
    }

    [Serializable]
    public class CotTrack
    {
        public float course;
        public float speed;
    }

    [Serializable]
    public class CotVideo
    {
        /// <summary>Raw URL from &lt;__video url&gt; (often HLS for TN Smartway).</summary>
        public string url;
        /// <summary>ConnectionEntry protocol: rtsp, https, http, rtmp, raw, …</summary>
        public string protocol;
        public string alias;
        /// <summary>ConnectionEntry address (host), when present.</summary>
        public string address;
        public int port;
        /// <summary>ConnectionEntry path (e.g. /rtplive/R1_220).</summary>
        public string path;
        /// <summary>1 = force RTSP over TCP (ATAK rtspReliable).</summary>
        public int rtspReliable;

        /// <summary>
        /// ATAK-style preferred play URL: RTSP from ConnectionEntry when possible
        /// (direct into the player), else the explicit url, else derived RTSP from
        /// Wowza/skyvdn HLS patterns.
        /// </summary>
        public string ResolvePlayUrl()
        {
            string proto = (protocol ?? "").Trim().ToLowerInvariant();
            // Explicit RTSP URL on the entry wins.
            if (!string.IsNullOrEmpty(url) &&
                url.StartsWith("rtsp", System.StringComparison.OrdinalIgnoreCase))
                return url;

            // Build from ConnectionEntry fields the way ATAK ConnectionEntry.getURL does.
            if (!string.IsNullOrEmpty(address) &&
                (proto == "rtsp" || proto == "rtsps" || string.IsNullOrEmpty(proto)))
            {
                string scheme = proto == "rtsps" ? "rtsps" : "rtsp";
                int p = port > 0 ? port : (scheme == "rtsps" ? 443 : 554);
                string pth = path ?? "";
                if (!string.IsNullOrEmpty(pth) && pth[0] != '/') pth = "/" + pth;
                // Omit default ports for cleaner URLs.
                bool defaultPort = (scheme == "rtsp" && p == 554) || (scheme == "rtsps" && p == 443);
                return defaultPort
                    ? $"{scheme}://{address}{pth}"
                    : $"{scheme}://{address}:{p}{pth}";
            }

            // Derive RTSP from Wowza/skyvdn HLS:
            // https://host[:443]/rtplive/STREAM/playlist.m3u8 → rtsp://host/rtplive/STREAM
            if (!string.IsNullOrEmpty(url) &&
                url.IndexOf(".m3u8", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (System.Uri.TryCreate(url, System.UriKind.Absolute, out var u))
                {
                    string pth = u.AbsolutePath ?? "";
                    // Strip /playlist.m3u8 or /chunklist_*.m3u8
                    int slash = pth.LastIndexOf('/');
                    if (slash > 0) pth = pth.Substring(0, slash);
                    if (pth.IndexOf("/rtplive/", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pth.IndexOf("/live/", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return "rtsp://" + u.Host + pth;
                }
            }

            // Address+path without protocol — assume RTSP (ATAK default for cameras).
            if (!string.IsNullOrEmpty(address) && !string.IsNullOrEmpty(path))
            {
                string pth = path[0] == '/' ? path : "/" + path;
                int p = port > 0 ? port : 554;
                return p == 554 ? $"rtsp://{address}{pth}" : $"rtsp://{address}:{p}{pth}";
            }

            return url;
        }
    }

    [Serializable]
    public class CotTeam
    {
        public string name;
        public string role;
    }

    [Serializable]
    public class CotUserIcon
    {
        /// <summary>ATAK &lt;usericon iconsetpath="UID/group/name.png"/&gt; detail.</summary>
        public string iconsetpath;
    }

    [Serializable]
    public class CotTakv
    {
        /// <summary>TAK client platform (&lt;takv platform="VRTAK-XR"/&gt;). Guard with
        /// !string.IsNullOrEmpty(takv?.platform): JsonUtility materializes empty
        /// instances for absent fields.</summary>
        public string platform;
        public string version;
        public string device;
        public string os;
    }

    [Serializable]
    public class CotSensor
    {
        /// <summary>TAK &lt;sensor azimuth fov range elevation/&gt; detail (degrees /
        /// meters). Guard with fov &gt; 0 || range &gt; 0: JsonUtility materializes
        /// empty instances for absent fields.</summary>
        public float azimuth;
        public float fov;
        public float range;
        public float elevation;
    }

    [Serializable]
    public class CotShapePoint
    {
        public double lat;
        public double lon;
        public float hae;
    }

    [Serializable]
    public class CotEllipse
    {
        public float major;
        public float minor;
        public float angle;
    }

    [Serializable]
    public class CotDetail
    {
        public string remarks;
        public CotTrack track;
        public CotVideo video;
        public string contactCallsign;
        public CotTeam team;
        public string markerColor;
        public string iconSource;
        // Guard with !string.IsNullOrEmpty(userIcon?.iconsetpath): JsonUtility
        // materializes empty instances for absent fields.
        public CotUserIcon userIcon;
        // Guard with !string.IsNullOrEmpty(takv?.platform).
        public CotTakv takv;
        // Guard with sensor.fov > 0 || sensor.range > 0.
        public CotSensor sensor;
        public string strokeColor;
        public string fillColor;
        // Drawing/route vertices (ATAK <link point="lat,lon,hae"/>). Guard with
        // Count >= 2: JsonUtility materializes empty lists for absent fields.
        public List<CotShapePoint> shapePoints;
        public bool closed;
        // Guard with major > 0 for the same reason.
        public CotEllipse ellipse;
    }

    [Serializable]
    public class NormalizedCot
    {
        public string uid;
        public string type;
        public string how;
        public string time;
        public string start;
        public string stale;
        public CotPoint point;
        public CotContact contact;
        public CotDetail detail;
        public string group;
        public string iconUrl;
        /// <summary>TAK server directory id this live track arrived from (direct stream).</summary>
        public string sourceServerId;

        public string Callsign =>
            !string.IsNullOrEmpty(contact?.callsign)
                ? contact.callsign
                : (!string.IsNullOrEmpty(detail?.contactCallsign) ? detail.contactCallsign : uid);

        public bool IsAirborne
        {
            get
            {
                if (string.IsNullOrEmpty(type)) return false;
                var parts = type.Split('-');
                return parts.Length >= 3 && parts[0] == "a" && parts[2] == "A";
            }
        }
    }

    [Serializable]
    public class CotWsMessage
    {
        public string type;
        public List<NormalizedCot> cots;
        public NormalizedCot cot;
        public string uid;
    }

    [Serializable]
    public class CotListWrapper
    {
        public List<NormalizedCot> items;
    }
}
