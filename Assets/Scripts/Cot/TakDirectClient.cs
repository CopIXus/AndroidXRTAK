using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Xml;
using TakXr.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Cot
{
    /// <summary>
    /// Standalone TAK server client: TLS socket to the CoT streaming port (8089)
    /// using an enrolled client certificate (PKCS#12 in StreamingAssets), parsing
    /// CoT XML events straight into the shared feed — no backend required.
    /// Mirrors the backend TakClient including the data-silence watchdog.
    /// </summary>
    public class TakDirectClient : MonoBehaviour
    {
        const float SilenceTimeoutSec = 180f;
        const float ReconnectDelaySec = 5f;
        const float StaleSweepSec = 60f;
        /// <summary>Grace beyond a CoT's stale time before it is dropped.</summary>
        const double StaleGraceSec = 300;

        AppConfig _config;
        CotFeedClient _feed;
        Thread _thread;
        volatile bool _stop;
        int _bootGeneration;
        byte[] _p12;
        string _p12Password;
        string _serverId;
        string _bindHost;
        int _bindPort;
        readonly Queue<NormalizedCot> _inbox = new Queue<NormalizedCot>();
        readonly object _lock = new object();
        long _lastDataTicks;
        volatile string _state = "off"; // off / loading-cert / connecting / connected / cert-missing / error
        int _events;
        string _lastError;
        volatile bool _everConnected;
        int _consecutiveFailures;

        SslStream _activeStream;
        readonly object _sendLock = new object();

        public string State => _state;
        public bool IsConnected => _state == "connected";
        public int EventsReceived => _events;
        public string LastError => _lastError;
        public string ServerId => _serverId;
        public string BoundHost => !string.IsNullOrEmpty(_bindHost)
            ? _bindHost
            : (_config != null ? _config.takHost : null);
        public int BoundPort => _bindPort > 0
            ? _bindPort
            : (_config != null ? _config.takPort : 0);
        /// <summary>Client cert bundle, available once loaded — shared with the Marti REST client.</summary>
        public byte[] P12Bytes => _p12;
        public string P12Password => string.IsNullOrEmpty(_p12Password)
            ? (_config != null ? _config.takClientP12Password : "")
            : _p12Password;

        /// <summary>Publish a CoT event to the TAK server over the live TLS stream.</summary>
        public bool SendCot(string xml)
        {
            var ssl = _activeStream;
            if (ssl == null || _state != "connected" || string.IsNullOrEmpty(xml)) return false;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(xml + "\n");
                lock (_sendLock)
                {
                    ssl.Write(bytes, 0, bytes.Length);
                    ssl.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakDirect] send failed: " + ex.Message);
                return false;
            }
        }

        public void Configure(AppConfig config, CotFeedClient feed)
        {
            _config = config;
            _feed = feed;
        }

        /// <summary>Bind this worker to a specific directory entry (multi-server hub).</summary>
        public void BindServer(string serverId, string host, int port)
        {
            _serverId = serverId;
            _bindHost = host;
            _bindPort = port;
        }

        public void StartClient()
        {
            if (_config == null || !_config.takDirectEnabled) { _state = "off"; return; }
            int gen = ++_bootGeneration;
            StartCoroutine(Boot(gen));
        }

        /// <summary>
        /// Stop the worker (wait for exit), then reconnect using current
        /// <see cref="AppConfig"/> host/port — used when switching TAK servers.
        /// </summary>
        public IEnumerator RestartClientRoutine()
        {
            _bootGeneration++; // invalidate any in-flight Boot
            StopClient();
            float deadline = Time.unscaledTime + 4f;
            while (_thread != null && _thread.IsAlive && Time.unscaledTime < deadline)
                yield return null;
            _thread = null;
            _state = "off";
            _events = 0;
            _lastError = null;
            _consecutiveFailures = 0;
            yield return new WaitForSecondsRealtime(0.25f);
            StartClient();
        }

        IEnumerator Boot(int gen)
        {
            _state = "loading-cert";
            string serverId = _serverId;
            if (string.IsNullOrEmpty(serverId))
            {
                var active = TakServerDirectory.GetActive(TakServerDirectory.LoadOrSeed(_config));
                serverId = active?.id;
                if (active != null && string.IsNullOrEmpty(_bindHost))
                    BindServer(active.id, active.host, active.cotPort);
            }

            // Always reload via TakCertStore so per-server imported P12 applies.
            _p12 = null;
            _p12Password = null;
            yield return TakCertStore.LoadP12Routine(_config, serverId, (bytes, pwd) =>
            {
                _p12 = bytes;
                _p12Password = pwd;
            });

            if (gen != _bootGeneration) yield break;

            if (_p12 == null || _p12.Length == 0)
            {
                _state = "cert-missing";
                _lastError = "Client cert missing — Import cert or place takclient.p12 in StreamingAssets";
                TakConnectionLog.Error(serverId, BoundHost ?? "?", _lastError);
                Debug.LogWarning("[TakDirect] " + _lastError);
                yield break;
            }

            // Don't start a second worker if one is still alive.
            if (_thread != null && _thread.IsAlive)
            {
                Debug.LogWarning("[TakDirect] connection thread still alive — skip Start");
                yield break;
            }

            Debug.Log($"[TakDirect] cert loaded ({_p12.Length} B) — connecting {BoundHost}:{BoundPort}");
            _stop = false;
            _thread = new Thread(ConnectionLoop) { IsBackground = true, Name = "TakDirect-" + (BoundHost ?? "?") };
            _thread.Start();
            CancelInvoke(nameof(SweepStale));
            InvokeRepeating(nameof(SweepStale), StaleSweepSec, StaleSweepSec);
        }

        public void StopClient()
        {
            _stop = true;
            CancelInvoke(nameof(SweepStale));
            // Unblock a stuck Read/Connect so the worker can exit quickly.
            try { _activeStream?.Dispose(); } catch { /* ignore */ }
            _activeStream = null;
        }

        void OnDestroy() => StopClient();

        void Update()
        {
            // Drain parsed CoTs onto the main thread.
            if (_feed == null) return;
            lock (_lock)
            {
                if (_inbox.Count == 0) return;
                while (_inbox.Count > 0)
                    _feed.UpsertDirect(_inbox.Dequeue(), notify: false);
            }
            _feed.NotifyChanged();
        }

        void SweepStale()
        {
            _feed?.SweepStaleDirect(StaleGraceSec);
        }

        // ---------------- worker thread ----------------

        void ConnectionLoop()
        {
            X509Certificate2 cert;
            try
            {
                cert = TakCertStore.CreateCert(_p12, P12Password);
            }
            catch (Exception ex)
            {
                _lastError = "cert: " + ex.Message;
                _state = "error";
                TakConnectionLog.Error(_serverId, BoundHost ?? "?", _lastError);
                Debug.LogError("[TakDirect] failed to load p12: " + ex.Message);
                return;
            }

            var certs = new X509CertificateCollection { cert };

            while (!_stop)
            {
                TcpClient tcp = null;
                SslStream ssl = null;
                try
                {
                    _state = "connecting";
                    string endpoint = $"{BoundHost}:{BoundPort}";
                    TakConnectionLog.Info(_serverId, endpoint, "TCP connecting…");
                    tcp = new TcpClient();
                    // The receive timeout IS the data-silence watchdog: a healthy TAK
                    // stream is never quiet this long, and an SslStream cannot be safely
                    // resumed after a timed-out read anyway — so timeout = reconnect.
                    tcp.ReceiveTimeout = (int)(SilenceTimeoutSec * 1000);
                    if (!tcp.ConnectAsync(BoundHost, BoundPort).Wait(15_000))
                    {
                        _lastError = $"TCP timeout to {endpoint} (15s) — host unreachable, wrong port, or firewall";
                        _state = "error";
                        TakConnectionLog.Error(_serverId, endpoint, _lastError);
                        throw new TimeoutException(_lastError);
                    }

                    TakConnectionLog.Info(_serverId, endpoint, "TLS handshake…");
                    // TAK servers use internal CAs — accept the server cert (parity with backend).
                    ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                    ssl.AuthenticateAsClient(BoundHost, certs, SslProtocols.Tls12, false);

                    _state = "connected";
                    _everConnected = true;
                    _consecutiveFailures = 0;
                    _lastError = null;
                    _lastDataTicks = DateTime.UtcNow.Ticks;
                    _activeStream = ssl;
                    TakConnectionLog.Info(_serverId, endpoint, "Connected (TLS + client cert)");
                    Debug.Log($"[TakDirect] connected to {endpoint} (TLS, client cert)");

                    ReadLoop(ssl);
                }
                catch (TimeoutException)
                {
                    _consecutiveFailures++;
                    // Already logged + state=error above.
                }
                catch (AuthenticationException ex)
                {
                    string endpoint = $"{BoundHost}:{BoundPort}";
                    _lastError = $"TLS/cert rejected at {endpoint}: {ex.Message}";
                    _consecutiveFailures++;
                    _state = "error";
                    TakConnectionLog.Error(_serverId, endpoint, _lastError);
                }
                catch (Exception ex)
                {
                    string endpoint = $"{BoundHost}:{BoundPort}";
                    string detail = ex.InnerException?.Message ?? ex.Message;
                    // Socket errors often say only "No connection could be made…" — prefix host.
                    if (detail.IndexOf(BoundHost ?? "", StringComparison.OrdinalIgnoreCase) < 0)
                        detail = $"{endpoint}: {detail}";
                    _lastError = detail;
                    _consecutiveFailures++;
                    if (!_everConnected && _consecutiveFailures >= 2)
                        _state = "error";
                    if (!_stop)
                    {
                        TakConnectionLog.Warn(_serverId, endpoint, _lastError);
                        Debug.LogWarning("[TakDirect] connection error: " + _lastError);
                    }
                }
                finally
                {
                    _activeStream = null;
                    try { ssl?.Dispose(); } catch { /* ignore */ }
                    try { tcp?.Close(); } catch { /* ignore */ }
                }

                if (_stop) break;
                if (_state != "error") _state = "connecting";
                Thread.Sleep(TimeSpan.FromSeconds(ReconnectDelaySec));
            }

            _state = "off";
        }

        void ReadLoop(SslStream ssl)
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (!_stop)
            {
                int n;
                try
                {
                    n = ssl.Read(buf, 0, buf.Length);
                }
                catch (IOException ex)
                {
                    Debug.LogWarning("[TakDirect] read timeout/error — reconnecting: " +
                                     (ex.InnerException?.Message ?? ex.Message));
                    return;
                }
                if (n <= 0) return; // remote closed

                Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                ExtractEvents(sb);
            }
        }

        void ExtractEvents(StringBuilder sb)
        {
            var text = sb.ToString();
            int consumed = 0;
            while (true)
            {
                int start = text.IndexOf("<event", consumed, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf("</event>", start, StringComparison.Ordinal);
                if (end < 0) break;
                end += "</event>".Length;
                var xml = text.Substring(start, end - start);
                consumed = end;

                var cot = ParseCot(xml);
                if (cot != null)
                {
                    cot.sourceServerId = _serverId;
                    lock (_lock) _inbox.Enqueue(cot);
                    int total = Interlocked.Increment(ref _events);
                    if (total == 1)
                        Debug.Log("[TakDirect] first CoT event received");
                }
            }
            if (consumed > 0) sb.Remove(0, consumed);
            // Defend against garbage floods with no event framing.
            if (sb.Length > 512 * 1024) sb.Clear();
        }

        /// <summary>Extract every &lt;event&gt;…&lt;/event&gt; block from a document
        /// (mission CoT XML, package .cot files, stream buffers).</summary>
        public static List<string> SplitEvents(string text)
        {
            var events = new List<string>();
            int pos = 0;
            while (true)
            {
                int start = text.IndexOf("<event", pos, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf("</event>", start, StringComparison.Ordinal);
                if (end < 0) break;
                end += "</event>".Length;
                events.Add(text.Substring(start, end - start));
                pos = end;
            }
            return events;
        }

        public static NormalizedCot ParseCot(string xml)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var ev = doc.DocumentElement;
                if (ev == null || ev.Name != "event") return null;

                var uid = ev.GetAttribute("uid");
                if (string.IsNullOrEmpty(uid)) return null;

                var pointNode = ev.SelectSingleNode("point") as XmlElement;
                if (pointNode == null) return null;
                if (!double.TryParse(pointNode.GetAttribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return null;
                if (!double.TryParse(pointNode.GetAttribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;

                var cot = new NormalizedCot
                {
                    uid = uid,
                    type = ev.GetAttribute("type"),
                    how = ev.GetAttribute("how"),
                    time = ev.GetAttribute("time"),
                    start = ev.GetAttribute("start"),
                    stale = ev.GetAttribute("stale"),
                    point = new CotPoint
                    {
                        lat = lat,
                        lon = lon,
                        hae = ParseF(pointNode.GetAttribute("hae"), 0f),
                        ce = ParseF(pointNode.GetAttribute("ce"), 9999999f),
                        le = ParseF(pointNode.GetAttribute("le"), 9999999f),
                    },
                    detail = new CotDetail(),
                };

                var detail = ev.SelectSingleNode("detail") as XmlElement;
                if (detail != null)
                {
                    if (detail.SelectSingleNode("contact") is XmlElement contact)
                    {
                        cot.contact = new CotContact
                        {
                            callsign = contact.GetAttribute("callsign"),
                            endpoint = contact.GetAttribute("endpoint"),
                        };
                    }
                    if (detail.SelectSingleNode("__group") is XmlElement grp)
                    {
                        cot.detail.team = new CotTeam
                        {
                            name = grp.GetAttribute("name"),
                            role = grp.GetAttribute("role"),
                        };
                    }
                    if (detail.SelectSingleNode("track") is XmlElement track)
                    {
                        cot.detail.track = new CotTrack
                        {
                            course = ParseF(track.GetAttribute("course"), 0f),
                            speed = ParseF(track.GetAttribute("speed"), 0f),
                        };
                    }
                    if (detail.SelectSingleNode("remarks") is XmlElement remarks)
                        cot.detail.remarks = remarks.InnerText;

                    var video = (detail.SelectSingleNode("__video") ?? detail.SelectSingleNode("video")) as XmlElement;
                    if (video != null)
                    {
                        // ATAK emits:
                        // <__video url="https://…/playlist.m3u8">
                        //   <ConnectionEntry protocol="rtsp" address="…" port="554"
                        //     path="/rtplive/…" alias="…" rtspReliable="1"/>
                        // </__video>
                        // Prefer building an RTSP URL from ConnectionEntry (what ATAK
                        // feeds its native player); keep the raw url as fallback.
                        var connEntry = video.SelectSingleNode("ConnectionEntry") as XmlElement;
                        var url = video.GetAttribute("url");
                        if (string.IsNullOrEmpty(url) && connEntry != null)
                            url = connEntry.GetAttribute("url");

                        var cv = new CotVideo
                        {
                            url = url,
                            alias = connEntry != null ? connEntry.GetAttribute("alias") : null,
                        };
                        if (connEntry != null)
                        {
                            cv.address = connEntry.GetAttribute("address");
                            cv.path = connEntry.GetAttribute("path");
                            cv.protocol = connEntry.GetAttribute("protocol");
                            if (int.TryParse(connEntry.GetAttribute("port"), out var port))
                                cv.port = port;
                            if (int.TryParse(connEntry.GetAttribute("rtspReliable"), out var rel))
                                cv.rtspReliable = rel;
                            // Some producers put the host only in address with empty url.
                            if (string.IsNullOrEmpty(cv.url) && !string.IsNullOrEmpty(cv.address))
                                cv.url = cv.address;
                        }
                        if (!string.IsNullOrEmpty(cv.url) || !string.IsNullOrEmpty(cv.address))
                            cot.detail.video = cv;
                    }

                    // TAK client/platform tag — identifies VR observers (VRTAK-XR)
                    // and other TAK clients for classification.
                    if (detail.SelectSingleNode("takv") is XmlElement takv)
                    {
                        var platform = takv.GetAttribute("platform");
                        if (!string.IsNullOrEmpty(platform))
                        {
                            cot.detail.takv = new CotTakv
                            {
                                platform = platform,
                                version = takv.GetAttribute("version"),
                                device = takv.GetAttribute("device"),
                                os = takv.GetAttribute("os"),
                            };
                        }
                    }

                    // TAK sensor detail (azimuth/fov/range/elevation) — observer
                    // gaze direction for VRTAK markers, FOV cones elsewhere.
                    if (detail.SelectSingleNode("sensor") is XmlElement sensor)
                    {
                        var sens = new CotSensor
                        {
                            azimuth = ParseF(sensor.GetAttribute("azimuth"), 0f),
                            fov = ParseF(sensor.GetAttribute("fov"), 0f),
                            range = ParseF(sensor.GetAttribute("range"), 0f),
                            elevation = ParseF(sensor.GetAttribute("elevation"), 0f),
                        };
                        if (sens.fov > 0f || sens.range > 0f)
                            cot.detail.sensor = sens;
                    }

                    // ATAK iconset reference (<usericon iconsetpath="UID/group/name"/>)
                    // — captured for future icon resolution, no HTTP dependency here.
                    if (detail.SelectSingleNode("usericon") is XmlElement userIcon)
                    {
                        var iconsetPath = userIcon.GetAttribute("iconsetpath");
                        if (!string.IsNullOrEmpty(iconsetPath))
                            cot.detail.userIcon = new CotUserIcon { iconsetpath = iconsetPath };
                    }

                    // ATAK <color argb="..."> → CSS hex for marker tinting.
                    if (detail.SelectSingleNode("color") is XmlElement colorNode)
                    {
                        var argbStr = colorNode.GetAttribute("argb");
                        if (!string.IsNullOrEmpty(argbStr) && int.TryParse(argbStr, out var argb))
                            cot.detail.markerColor = CotXmlBuilder.ArgbIntToCss(argb);
                    }

                    // Drawing/route vertices: repeated <link point="lat,lon,hae"/>.
                    var links = detail.SelectNodes("link");
                    if (links != null && links.Count > 0)
                    {
                        var pts = new List<CotShapePoint>();
                        foreach (XmlNode ln in links)
                        {
                            var attr = (ln as XmlElement)?.GetAttribute("point");
                            if (string.IsNullOrEmpty(attr)) continue;
                            var parts = attr.Split(',');
                            if (parts.Length < 2) continue;
                            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var plat)) continue;
                            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var plon)) continue;
                            pts.Add(new CotShapePoint
                            {
                                lat = plat,
                                lon = plon,
                                hae = parts.Length > 2 ? ParseF(parts[2], 0f) : 0f,
                            });
                        }
                        if (pts.Count >= 2) cot.detail.shapePoints = pts;
                    }
                    // Closed polygons (u-d-f) vs open routes (b-m-r).
                    if (!string.IsNullOrEmpty(cot.type) && cot.type.StartsWith("u-d-f"))
                        cot.detail.closed = true;

                    if (detail.SelectSingleNode("shape/ellipse") is XmlElement el)
                    {
                        cot.detail.ellipse = new CotEllipse
                        {
                            major = ParseF(el.GetAttribute("major"), 0f),
                            minor = ParseF(el.GetAttribute("minor"), 0f),
                            angle = ParseF(el.GetAttribute("angle"), 0f),
                        };
                    }

                    if (detail.SelectSingleNode("strokeColor") is XmlElement sc &&
                        int.TryParse(sc.GetAttribute("value"), out var strokeArgb))
                        cot.detail.strokeColor = CotXmlBuilder.ArgbIntToCss(strokeArgb);
                    if (detail.SelectSingleNode("fillColor") is XmlElement fc &&
                        int.TryParse(fc.GetAttribute("value"), out var fillArgb))
                        cot.detail.fillColor = CotXmlBuilder.ArgbIntToCss(fillArgb);
                }

                return cot;
            }
            catch
            {
                return null;
            }
        }

        static float ParseF(string s, float fallback)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : fallback;
        }
    }
}
