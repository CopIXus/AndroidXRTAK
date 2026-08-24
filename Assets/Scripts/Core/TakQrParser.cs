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

            if (text.StartsWith("tak://", StringComparison.OrdinalIgnoreCase))
                return ParseTakUri(text);

            if (text.StartsWith("{"))
                return ParseJson(text);

            var csv = ParseCsv(text);
            if (csv != null) return csv;

            if (LooksLikeHost(text))
                return new TakQrConnection { Host = text, Port = 8089, EnrollPort = 8446, MartiPort = 8443 };

            return null;
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
            if (q.TryGetValue("enrollPort", out var ep) && int.TryParse(ep, out var ev) && ev > 0)
                enroll = ev;

            q.TryGetValue("username", out var user);
            string pass = null;
            if (q.TryGetValue("token", out var tok)) pass = tok;
            else if (q.TryGetValue("password", out var pw)) pass = pw;

            q.TryGetValue("name", out var name);

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
                    Password = string.IsNullOrEmpty(obj.password) ? null : obj.password,
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
            return new TakQrConnection
            {
                Name = string.IsNullOrEmpty(name) ? null : name,
                Host = host,
                Port = port,
                EnrollPort = 8446,
                MartiPort = 8443,
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
        }
    }
}
