using System;
using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// Parses TAK / infraTAK / iTAK QR payloads (port of packages/frontend takQr.ts).
    /// </summary>
    public class TakQrConnection
    {
        public string Name;
        public string Host;
        public int Port = 8089;
        public int EnrollPort = 8446;
        public int MartiPort = 8443;
        public string Username;
        public string Password;
    }

    public static class TakQrParser
    {
        public static TakQrConnection Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var text = raw.Trim();

            if (text.StartsWith("tak://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("takbeacon://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("tak%3A", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("takbeacon%3A", StringComparison.OrdinalIgnoreCase))
            {
                if (text.IndexOf("%3A", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    text.IndexOf("://", StringComparison.Ordinal) < 0)
                    text = Uri.UnescapeDataString(text);
                return ParseTakUri(text);
            }

            if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return ParseHttp(text);

            if (text.StartsWith("{"))
                return ParseJson(text);

            var csv = ParseCsv(text);
            if (csv != null) return csv;

            if (LooksLikeHost(text))
                return new TakQrConnection { Host = text, Port = 8089, EnrollPort = 8446, MartiPort = 8443 };

            return null;
        }

        static TakQrConnection ParseHttp(string text)
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
            if (string.IsNullOrEmpty(uri.Host)) return null;
            int port = uri.IsDefaultPort ? 0 : uri.Port;
            int cot = 8089;
            int enroll = 8446;
            if (port == 8446)
                enroll = 8446;
            else if (port > 0 && port != 8443)
                cot = port;
            var q = ParseQuery(uri.Query);
            q.TryGetValue("username", out var user);
            if (string.IsNullOrWhiteSpace(user)) q.TryGetValue("user", out user);
            string pass = null;
            if (q.TryGetValue("token", out var tok) && !string.IsNullOrEmpty(tok)) pass = tok;
            else if (q.TryGetValue("password", out var pw)) pass = pw;
            q.TryGetValue("name", out var name);
            return new TakQrConnection
            {
                Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(),
                Host = uri.Host,
                Port = cot,
                EnrollPort = enroll,
                MartiPort = 8443,
                Username = string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
                Password = string.IsNullOrEmpty(pass) ? null : pass,
            };
        }

        static TakQrConnection ParseTakUri(string text)
        {
            try
            {
                // Unity's Uri handles tak:// as an unknown scheme.
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                    return ParseTakUriManual(text);

                var q = ParseQuery(uri.Query);
                if (!q.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
                    return ParseTakUriManual(text);
                return FromHostParam(host.Trim(), q);
            }
            catch
            {
                return ParseTakUriManual(text);
            }
        }

        static TakQrConnection ParseTakUriManual(string text)
        {
            int q = text.IndexOf('?');
            if (q < 0) return null;
            var dict = ParseQuery(text.Substring(q));
            if (!dict.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
                return null;
            return FromHostParam(host.Trim(), dict);
        }

        static TakQrConnection FromHostParam(string hostField, System.Collections.Generic.Dictionary<string, string> q)
        {
            // ATAK host=server.com:port:protocol
            string host = hostField;
            int port = 8089;
            var parts = hostField.Split(':');
            if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]))
                host = parts[0];
            if (parts.Length >= 2 && int.TryParse(parts[1], out var hp) && hp > 0)
                port = hp;
            if (q.TryGetValue("port", out var ps) && int.TryParse(ps, out var qp) && qp > 0)
                port = qp;

            int enroll = 8446;
            foreach (var ek in new[] { "enrollPort", "enrollmentPort", "csrport", "csr_port" })
            {
                if (q.TryGetValue(ek, out var ep) && int.TryParse(ep, out var ev) && ev > 0)
                {
                    enroll = ev;
                    break;
                }
            }

            if (!q.TryGetValue("username", out var user) || string.IsNullOrWhiteSpace(user))
                q.TryGetValue("user", out user);
            string pass = null;
            if (q.TryGetValue("token", out var tok) && !string.IsNullOrEmpty(tok)) pass = tok;
            else if (q.TryGetValue("password", out var pw)) pass = pw;

            if (!q.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
                q.TryGetValue("description", out name);

            return new TakQrConnection
            {
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                Host = host,
                Port = port,
                EnrollPort = enroll,
                MartiPort = 8443,
                Username = string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
                Password = string.IsNullOrEmpty(pass) ? null : pass,
            };
        }

        static System.Collections.Generic.Dictionary<string, string> ParseQuery(string query)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;
            if (query[0] == '?') query = query.Substring(1);
            foreach (var part in query.Split('&'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    d[Uri.UnescapeDataString(part)] = "";
                    continue;
                }
                var k = Uri.UnescapeDataString(part.Substring(0, eq).Replace('+', ' '));
                var v = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                d[k] = v;
            }
            return d;
        }

        static TakQrConnection ParseJson(string text)
        {
            try
            {
                // Minimal JSON: JsonUtility needs a wrapper type.
                var obj = JsonUtility.FromJson<JsonShape>(text);
                if (obj == null || string.IsNullOrEmpty(obj.host)) return null;
                return new TakQrConnection
                {
                    Name = string.IsNullOrEmpty(obj.name) ? null : obj.name,
                    Host = obj.host,
                    Port = obj.port > 0 ? obj.port : 8089,
                    EnrollPort = obj.enrollPort > 0 ? obj.enrollPort : 8446,
                    MartiPort = obj.martiPort > 0 ? obj.martiPort : 8443,
                    Username = string.IsNullOrEmpty(obj.username) ? null : obj.username,
                    Password = !string.IsNullOrEmpty(obj.password) ? obj.password
                        : (string.IsNullOrEmpty(obj.token) ? null : obj.token),
                };
            }
            catch
            {
                return null;
            }
        }

        [Serializable]
        class JsonShape
        {
            public string name;
            public string host;
            public int port;
            public int enrollPort;
            public int martiPort;
            public string username;
            public string password;
            public string token;
        }

        static TakQrConnection ParseCsv(string text)
        {
            // iTAK: Name,host,port,ssl
            var parts = text.Split(',');
            if (parts.Length < 3) return null;
            var name = parts[0].Trim();
            var host = parts[1].Trim();
            if (string.IsNullOrEmpty(host)) return null;
            if (!int.TryParse(parts[2].Trim(), out var port) || port <= 0) return null;

            // iTAK: Name,host,port,ssl
            // TAK enrollment: Name,host,port,ssl,username,password
            // or Name,host,port,username,password
            string user = null;
            string pass = null;
            if (parts.Length >= 4)
            {
                string proto = parts[3].Trim();
                bool protoField = proto.Equals("ssl", StringComparison.OrdinalIgnoreCase)
                    || proto.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                    || proto.Equals("udp", StringComparison.OrdinalIgnoreCase)
                    || proto.Equals("quic", StringComparison.OrdinalIgnoreCase);
                if (protoField)
                {
                    if (parts.Length >= 5) user = parts[4].Trim();
                    if (parts.Length >= 6) pass = parts[5].Trim();
                }
                else
                {
                    user = proto;
                    if (parts.Length >= 5) pass = parts[4].Trim();
                }
            }
            return new TakQrConnection
            {
                Name = string.IsNullOrEmpty(name) ? null : name,
                Host = host,
                Port = port,
                EnrollPort = 8446,
                MartiPort = 8443,
                Username = string.IsNullOrEmpty(user) ? null : user,
                Password = string.IsNullOrEmpty(pass) ? null : pass,
            };
        }

        static bool LooksLikeHost(string text)
        {
            if (text.IndexOf(' ') >= 0) return false;
            if (text.Length < 3 || text.Length > 253) return false;
            bool hasDot = text.IndexOf('.') >= 0;
            if (!hasDot) return false;
            foreach (var c in text)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-'))
                    return false;
            }
            return true;
        }

        public static void SelfTest()
        {
            var a = Parse("tak://com.atakmap.app/enroll?host=tak.example.com&username=u&token=p");
            if (a == null || a.Host != "tak.example.com" || a.Username != "u" || a.Password != "p")
                Debug.LogError("[TakQrParser] enroll URI parse failed");
            var b = Parse("tak://com.atakmap.app/enroll?host=tak.example.com:8090:ssl");
            if (b == null || b.Host != "tak.example.com" || b.Port != 8090)
                Debug.LogError("[TakQrParser] host:port:proto parse failed");
            var c = Parse("InfraTAK,tak.example.com,8089,ssl");
            if (c == null || c.Host != "tak.example.com" || c.Port != 8089)
                Debug.LogError("[TakQrParser] iTAK CSV parse failed");
            var d = Parse("TAK,tak.example.com,8089,ssl,operator,s3cret");
            if (d == null || d.Username != "operator" || d.Password != "s3cret" || d.Port != 8089)
                Debug.LogError("[TakQrParser] enrollment CSV parse failed");
        }
    }
}
