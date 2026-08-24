using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TakXr.Cot
{
    [Serializable] public class MartiGroup
    {
        public string name;
        public string direction;
        public string created;
        public string type;
        public int bitpos;
        public bool active;
    }
    [Serializable] class MartiGroupsResponse { public List<MartiGroup> data; }

    [Serializable] public class MartiMission
    {
        public string name;
        public string description;
        public string createTime;
    }
    [Serializable] class MartiMissionsResponse { public List<MartiMission> data; }

    [Serializable] public class MartiPackage
    {
        public string UID;
        public string Name;
        public string Hash;
        public string Size;
        public string SubmissionDateTime;
        public string Keywords;
    }
    [Serializable] class MartiSearchResponse { public List<MartiPackage> results; }

    /// <summary>
    /// TAK Server Marti REST API client — channels (groups), Data Sync (missions)
    /// and data packages — using the enrolled client certificate, standalone on the
    /// headset. Implemented as HTTP/1.1 over SslStream: the same TLS + client-cert
    /// path as the CoT stream, which is proven to work under IL2CPP/Android
    /// (UnityWebRequest/HttpClient cannot present client certificates reliably).
    /// </summary>
    public class TakMartiClient
    {
        const int ConnectTimeoutMs = 25_000;
        const int TlsTimeoutMs = 20_000;
        const int MaxAttempts = 3; // initial + 2 retries
        const int DnsTimeoutMs = 10_000;

        readonly string _host;
        readonly int _port;
        readonly X509Certificate2 _cert;
        readonly X509CertificateCollection _certs;

        public TakMartiClient(string host, int port, byte[] p12, string p12Password)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentException("host required");
            if (p12 == null || p12.Length == 0) throw new ArgumentException("p12 required");
            _host = host;
            _port = port;
            _cert = new X509Certificate2(p12, p12Password, TakXr.Core.TakCertStore.KeyFlags);
            _certs = new X509CertificateCollection { _cert };
            Debug.Log($"[TakMarti] client ready host={_host} port={_port} certSubject={_cert.Subject} hasPrivateKey={_cert.HasPrivateKey}");
        }

        // ---- Channels (groups) ----

        public async Task<List<MartiGroup>> GetGroups()
        {
            var body = await RequestString("GET", "/Marti/api/groups/all?useCache=true");
            var parsed = JsonUtility.FromJson<MartiGroupsResponse>(body);
            return parsed?.data ?? new List<MartiGroup>();
        }

        public async Task SetActiveGroups(List<MartiGroup> groups, string clientUid)
        {
            // JsonUtility can't serialize top-level arrays — build by hand.
            var sb = new StringBuilder("[");
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(JsonEsc(g.name))
                  .Append("\",\"direction\":\"").Append(JsonEsc(g.direction))
                  .Append("\",\"created\":\"\",\"type\":\"SYSTEM\",\"bitpos\":").Append(g.bitpos)
                  .Append(",\"active\":").Append(g.active ? "true" : "false").Append('}');
            }
            sb.Append(']');
            await RequestString("PUT",
                "/Marti/api/groups/active?clientUid=" + Uri.EscapeDataString(clientUid),
                sb.ToString());
        }

        // ---- Data Sync (missions) ----

        public async Task<List<MartiMission>> GetMissions()
        {
            var body = await RequestString("GET", "/Marti/api/missions?passwordProtected=false&defaultRole=true");
            var parsed = JsonUtility.FromJson<MartiMissionsResponse>(body);
            return parsed?.data ?? new List<MartiMission>();
        }

        public Task SubscribeMission(string name, string clientUid) =>
            RequestString("PUT",
                $"/Marti/api/missions/{Uri.EscapeDataString(name)}/subscription?uid={Uri.EscapeDataString(clientUid)}");

        public async Task UnsubscribeMission(string name, string clientUid)
        {
            try
            {
                await RequestString("DELETE",
                    $"/Marti/api/missions/{Uri.EscapeDataString(name)}/subscription?uid={Uri.EscapeDataString(clientUid)}");
            }
            catch { /* best-effort */ }
        }

        /// <summary>Mission CoT events as one XML document.</summary>
        public Task<string> GetMissionCotXml(string name) =>
            RequestString("GET", $"/Marti/api/missions/{Uri.EscapeDataString(name)}/cot");

        // ---- Data packages (Marti sync) ----

        public async Task<List<MartiPackage>> SearchPackages()
        {
            var body = await RequestString("GET", "/Marti/sync/search?tool=public");
            var parsed = JsonUtility.FromJson<MartiSearchResponse>(body);
            return parsed?.results ?? new List<MartiPackage>();
        }

        public Task<byte[]> DownloadPackage(string hash) =>
            Request("GET", "/Marti/sync/content?hash=" + Uri.EscapeDataString(hash));

        // ---- HTTP/1.1 over SslStream ----

        async Task<string> RequestString(string method, string path, string jsonBody = null)
        {
            var bytes = await Request(method, path, jsonBody);
            return Encoding.UTF8.GetString(bytes);
        }

        Task<byte[]> Request(string method, string path, string jsonBody = null)
        {
            return Task.Run(() =>
            {
                Exception last = null;
                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    try
                    {
                        return RequestOnce(method, path, jsonBody, attempt);
                    }
                    catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
                    {
                        last = ex;
                        int backoffMs = 500 * attempt;
                        Debug.LogWarning($"[TakMarti] {method} {path} attempt {attempt}/{MaxAttempts} failed ({Classify(ex)}): {ex.Message} — retry in {backoffMs}ms");
                        System.Threading.Thread.Sleep(backoffMs);
                    }
                }
                throw last ?? new IOException($"Marti {method} {path} failed");
            });
        }

        static bool IsTransient(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is TimeoutException || e is SocketException || e is IOException)
                    return true;
                if (e is AuthenticationException) return true;
            }
            return false;
        }

        static string Classify(Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.IndexOf("DNS", StringComparison.OrdinalIgnoreCase) >= 0) return "dns";
            if (msg.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0) return "connect";
            if (msg.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("handshake", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ex is AuthenticationException) return "tls";
            return "request";
        }

        byte[] RequestOnce(string method, string path, string jsonBody, int attempt)
        {
            var addrs = ResolvePreferIpv4(_host);
            Debug.Log($"[TakMarti] {method} {path} attempt={attempt} → {_host}:{_port} addrs=[{FormatAddrs(addrs)}]");

            using var tcp = ConnectTcp(addrs, _port);
            tcp.ReceiveTimeout = 30_000;
            tcp.SendTimeout = 15_000;

            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            AuthenticateTls(ssl);

            var body = jsonBody != null ? Encoding.UTF8.GetBytes(jsonBody) : null;
            var req = new StringBuilder();
            req.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            req.Append("Host: ").Append(_host).Append(':').Append(_port).Append("\r\n");
            req.Append("Accept: */*\r\n");
            req.Append("Connection: close\r\n");
            if (body != null)
            {
                req.Append("Content-Type: application/json\r\n");
                req.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            }
            req.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(req.ToString());
            ssl.Write(head, 0, head.Length);
            if (body != null) ssl.Write(body, 0, body.Length);
            ssl.Flush();

            var (status, payload) = ReadResponse(ssl);
            if (status < 200 || status >= 300)
                throw new Exception($"Marti {method} {path} → HTTP {status}");
            Debug.Log($"[TakMarti] {method} {path} → HTTP {status}, {payload.Length} B");
            return payload;
        }

        void AuthenticateTls(SslStream ssl)
        {
            Exception authEx = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    // SNI + client cert; server uses internal CA so we accept any server cert.
                    ssl.AuthenticateAsClient(_host, _certs, SslProtocols.Tls12, false);
                }
                catch (Exception ex) { authEx = ex; }
                finally { done.Set(); }
            })
            { IsBackground = true, Name = "TakMarti-TLS" };

            thread.Start();
            if (!done.Wait(TlsTimeoutMs))
            {
                // Outer using disposes the stream/socket and unblocks the auth thread.
                throw new TimeoutException($"Marti TLS handshake timed out ({_host}:{_port})");
            }
            if (authEx != null)
                throw new AuthenticationException($"Marti TLS failed ({_host}:{_port}): {authEx.Message}", authEx);
            if (!ssl.IsAuthenticated)
                throw new AuthenticationException($"Marti TLS not authenticated ({_host}:{_port})");
        }

        static IPAddress[] ResolvePreferIpv4(string host)
        {
            // Literal IP — skip DNS.
            if (IPAddress.TryParse(host, out var literal))
                return new[] { literal };

            IPAddress[] all = null;
            Exception dnsEx = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            var thread = new System.Threading.Thread(() =>
            {
                try { all = Dns.GetHostAddresses(host); }
                catch (Exception ex) { dnsEx = ex; }
                finally { done.Set(); }
            })
            { IsBackground = true, Name = "TakMarti-DNS" };

            thread.Start();
            if (!done.Wait(DnsTimeoutMs))
                throw new TimeoutException($"Marti DNS timed out ({host})");
            if (dnsEx != null)
                throw new IOException($"Marti DNS failed ({host}): {dnsEx.Message}", dnsEx);
            if (all == null || all.Length == 0)
                throw new IOException($"Marti DNS returned no addresses ({host})");

            // Android often has AAAA records that black-hole; try IPv4 first.
            var ipv4 = Array.FindAll(all, a => a.AddressFamily == AddressFamily.InterNetwork);
            var ipv6 = Array.FindAll(all, a => a.AddressFamily == AddressFamily.InterNetworkV6);
            if (ipv4.Length == 0) return all;
            if (ipv6.Length == 0) return ipv4;
            var ordered = new IPAddress[ipv4.Length + ipv6.Length];
            Array.Copy(ipv4, 0, ordered, 0, ipv4.Length);
            Array.Copy(ipv6, 0, ordered, ipv4.Length, ipv6.Length);
            return ordered;
        }

        TcpClient ConnectTcp(IPAddress[] addrs, int port)
        {
            Exception last = null;
            foreach (var addr in addrs)
            {
                var tcp = new TcpClient(addr.AddressFamily);
                try
                {
                    var ar = tcp.BeginConnect(addr, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                    {
                        try { tcp.Close(); } catch { /* ignore */ }
                        last = new TimeoutException($"Marti connect timed out ({_host}/{addr}:{port})");
                        Debug.LogWarning($"[TakMarti] TCP timeout to {addr}:{port}");
                        continue;
                    }
                    tcp.EndConnect(ar);
                    Debug.Log($"[TakMarti] TCP connected {_host} via {addr}:{port}");
                    return tcp;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Debug.LogWarning($"[TakMarti] TCP failed {addr}:{port}: {ex.Message}");
                    try { tcp.Close(); } catch { /* ignore */ }
                }
            }
            throw last ?? new IOException($"Marti connect failed ({_host}:{port})");
        }

        static string FormatAddrs(IPAddress[] addrs)
        {
            if (addrs == null || addrs.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < addrs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(addrs[i]);
            }
            return sb.ToString();
        }

        static (int status, byte[] body) ReadResponse(Stream s)
        {
            // Read everything (Connection: close), then split head/body.
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                ms.Write(buf, 0, n);
            var all = ms.ToArray();

            int headEnd = IndexOf(all, "\r\n\r\n");
            if (headEnd < 0) throw new IOException("Malformed HTTP response");
            var head = Encoding.ASCII.GetString(all, 0, headEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var statusParts = lines[0].Split(' ');
            int status = statusParts.Length > 1 && int.TryParse(statusParts[1], out var st) ? st : 0;

            bool chunked = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                    line.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    chunked = true;
            }

            int bodyStart = headEnd + 4;
            var raw = new byte[all.Length - bodyStart];
            Array.Copy(all, bodyStart, raw, 0, raw.Length);
            return (status, chunked ? DecodeChunked(raw) : raw);
        }

        static byte[] DecodeChunked(byte[] raw)
        {
            using var outMs = new MemoryStream();
            int pos = 0;
            while (pos < raw.Length)
            {
                int lineEnd = IndexOf(raw, "\r\n", pos);
                if (lineEnd < 0) break;
                var sizeLine = Encoding.ASCII.GetString(raw, pos, lineEnd - pos).Trim();
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out var size))
                    break;
                if (size == 0) break;
                int dataStart = lineEnd + 2;
                if (dataStart + size > raw.Length) size = raw.Length - dataStart;
                outMs.Write(raw, dataStart, size);
                pos = dataStart + size + 2; // skip trailing CRLF
            }
            return outMs.ToArray();
        }

        static int IndexOf(byte[] haystack, string needle, int start = 0)
        {
            var nb = Encoding.ASCII.GetBytes(needle);
            for (int i = start; i <= haystack.Length - nb.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < nb.Length; j++)
                {
                    if (haystack[i + j] != nb[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        static string JsonEsc(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
