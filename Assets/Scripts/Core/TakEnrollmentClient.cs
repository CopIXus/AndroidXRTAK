using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TakXr.Core
{
    /// <summary>
    /// On-device TAK Server quick-enroll. Same flow as ATAK / TAK_Beacon:
    /// GET /Marti/api/tls/config, POST PEM CSR to signClient/v2, store a
    /// Mono-readable DER PKCS#12 for the CoT stream.
    /// </summary>
    public static class TakEnrollmentClient
    {
        const int ConnectTimeoutMs = 10_000;
        const int TlsTimeoutMs = 15_000;
        const int ReadTimeoutMs = 15_000;
        /// <summary>Android OkHttp path (TAK_Beacon) usually finishes in a few seconds.</summary>
        const int AndroidSignTimeoutMs = 45_000;
        /// <summary>Mono SslStream fallback — may stall on keep-alive until the server closes.</summary>
        const int SignTimeoutMs = 90_000;
        static int _busy;
        /// <summary>Local PKCS#12 password. The TAK username/password is never used as the file password.</summary>
        public const string LocalP12Password = "takxr-enroll";

        class SubjectExtras
        {
            public string Organization;
            public string OrganizationalUnit;
            public string Country;
            public string State;
            public string Locality;
        }

        class HttpResult
        {
            public int Status;
            public string Body;
            public int ElapsedMs;
        }

        class Work
        {
            public byte[] Pfx;
            public string Step = "start";
            public string Error;
            public bool Ok;
            public volatile bool Finished;
        }

        /// <summary>
        /// Enroll <paramref name="username"/> and store the client cert on <paramref name="serverId"/>.
        /// Uses a raw TLS socket (UnityWebRequest hangs on slow signClient/v2 on Android XR)
        /// with BouncyCastle RSA-2048 PEM CSR + definite-length DER PKCS#12.
        /// </summary>
        public static IEnumerator Enroll(
            string serverId, string host, int enrollPort, string username, string password,
            Action<bool, string> onDone)
        {
            if (string.IsNullOrWhiteSpace(serverId))
            {
                onDone?.Invoke(false, "No server selected");
                yield break;
            }
            if (string.IsNullOrWhiteSpace(host) || enrollPort <= 0 ||
                string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                onDone?.Invoke(false, "Server, enroll port, username, and password are required");
                yield break;
            }

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                onDone?.Invoke(false, "Enrollment is already running");
                yield break;
            }

            TakConnectionLog.EnsureFile();
            string logHost = host.Trim();
            string cleanUser = username.Trim();
            string cleanPass = password;

            // Prefer Android HttpsURLConnection (TAK_Beacon stack). Mono SslStream often
            // holds signClient/v2 until the server closes keep-alive (~60–120s) even after
            // the JSON body is ready; Android returns in a few seconds on the same server.
            Note(logHost, $"Enroll start {cleanUser} @ {logHost}:{enrollPort}");
            var work = new Work { Step = "probe" };
            var enrollThread = new Thread(() =>
            {
                try
                {
                    work.Step = "probe";
                    ProbeReachability(logHost, enrollPort);

                    bool useAndroid = TakAndroidHttps.IsAvailable;
                    Note(logHost, useAndroid
                        ? "HTTP stack: Android HttpsURLConnection (TAK_Beacon)"
                        : "HTTP stack: Mono SslStream fallback");

                    work.Step = "config";
                    Note(logHost, $"GET /Marti/api/tls/config on :{enrollPort} user={cleanUser}");
                    var cfg = Http(
                        useAndroid, logHost, enrollPort, "GET", "/Marti/api/tls/config",
                        null, null, "application/xml", cleanUser, cleanPass, ReadTimeoutMs);
                    Note(logHost, "config HTTP " + cfg.Status + " in " + cfg.ElapsedMs + "ms");
                    if (cfg.Status == 401 || cfg.Status == 403)
                        throw new IOException("Enrollment rejected — check username and password (or scan a fresh QR code)");
                    if (cfg.Status < 200 || cfg.Status >= 300)
                        throw new IOException("Enrollment config failed (HTTP " + cfg.Status + ")");
                    SubjectExtras extras = ParseSubject(cfg.Body) ?? new SubjectExtras();

                    work.Step = "csr";
                    var random = new SecureRandom();
                    var gen = new RsaKeyPairGenerator();
                    gen.Init(new KeyGenerationParameters(random, 2048));
                    AsymmetricCipherKeyPair keys = gen.GenerateKeyPair();
                    var subject = BuildSubject(cleanUser, extras);
                    Note(logHost, "CSR " + subject);
                    var csr = new Pkcs10CertificationRequest(
                        "SHA256WITHRSA", subject, keys.Public, new DerSet(), keys.Private);
                    // PEM CSR body — same as TAK_Beacon EnrollmentClient.kt.
                    byte[] csrBytes = Encoding.UTF8.GetBytes(Pem("CERTIFICATE REQUEST", csr.GetEncoded()));
                    Note(logHost, "CSR PEM " + csrBytes.Length + " bytes");

                    work.Step = "sign";
                    string signPath = "/Marti/api/tls/signClient/v2?clientUid=" +
                                      Uri.EscapeDataString(cleanUser) + "&version=v2";
                    int signBudget = useAndroid ? AndroidSignTimeoutMs : SignTimeoutMs;
                    Note(logHost, "POST " + signPath + " (expect seconds; budget " + (signBudget / 1000) + "s)");
                    var heartbeat = new Thread(() =>
                    {
                        int sec = 0;
                        while (!work.Finished && sec < signBudget / 1000)
                        {
                            Thread.Sleep(5_000);
                            if (work.Finished || work.Step != "sign") return;
                            sec += 5;
                            Note(logHost, "Signing… " + sec + "s (waiting for HTTP response)");
                        }
                    })
                    { IsBackground = true, Name = "TakEnroll-Wait" };
                    heartbeat.Start();

                    var signed = Http(
                        useAndroid, logHost, enrollPort, "POST", signPath, csrBytes,
                        "text/plain; charset=utf-8", "application/json", cleanUser, cleanPass, signBudget);
                    Note(logHost, "sign HTTP " + signed.Status + " in " + signed.ElapsedMs + "ms body " +
                                  (signed.Body != null ? signed.Body.Length : 0) + " " +
                                  Compact(signed.Body, 160));

                    if (signed.Status == 401 || signed.Status == 403)
                        throw new IOException("Enrollment rejected — check username and password (or scan a fresh QR code)");
                    if (signed.Status < 200 || signed.Status >= 300)
                        throw new IOException("Enrollment failed (HTTP " + signed.Status + "). " + Compact(signed.Body, 180));

                    work.Step = "pfx";
                    var chain = ExtractCertChain(signed.Body, keys.Public);
                    if (chain == null || chain.Count == 0)
                        throw new IOException(
                            "Enrollment response had no signed certificate. " + Compact(signed.Body, 220));

                    // Mono rejects BouncyCastle's default BER indefinite-length PKCS#12.
                    var builder = new Pkcs12StoreBuilder();
                    builder.SetUseDerEncoding(true);
                    var store = builder.Build();
                    var entries = new X509CertificateEntry[chain.Count];
                    for (int i = 0; i < chain.Count; i++)
                        entries[i] = new X509CertificateEntry(chain[i]);
                    store.SetKeyEntry("tak", new AsymmetricKeyEntry(keys.Private), entries);
                    for (int i = 1; i < entries.Length; i++)
                        store.SetCertificateEntry("ca-" + i, entries[i]);
                    using (var ms = new MemoryStream())
                    {
                        store.Save(ms, LocalP12Password.ToCharArray(), new SecureRandom());
                        work.Pfx = ms.ToArray();
                    }
                    Note(logHost, "PFX " + work.Pfx.Length + " bytes (chain=" + chain.Count + ")");

                    using (var check = TakCertStore.CreateCert(work.Pfx, LocalP12Password))
                    {
                        if (!check.HasPrivateKey)
                            throw new IOException("Enrolled certificate file has no private key");
                        Note(logHost, "PFX loads subject " + check.Subject);
                    }
                    work.Ok = true;
                }
                catch (Exception ex)
                {
                    work.Ok = false;
                    work.Error = work.Step + ": " + Clean(ex);
                    TakConnectionLog.Error("enroll", logHost, work.Error);
                    Debug.LogError("[TakEnroll] " + work.Error + "\n" + ex);
                }
                finally
                {
                    work.Finished = true;
                }
            })
            { IsBackground = true, Name = "TakEnroll" };
            enrollThread.Start();
            while (!work.Finished)
                yield return null;
            Interlocked.Exchange(ref _busy, 0);

            if (!work.Ok)
            {
                onDone?.Invoke(false, work.Error ?? "Enrollment failed");
                yield break;
            }

            try
            {
                if (work.Pfx == null || work.Pfx.Length == 0)
                {
                    onDone?.Invoke(false, "Enrollment did not produce a certificate file");
                    yield break;
                }
                TakCertStore.ImportP12(serverId, work.Pfx, LocalP12Password);
            }
            catch (Exception ex)
            {
                onDone?.Invoke(false, "Could not store enrolled certificate: " + ex.Message);
                yield break;
            }

            Debug.Log($"[TakEnroll] stored cert for {cleanUser} @ {logHost}:{enrollPort}");
            onDone?.Invoke(true, null);
        }

        static void Note(string host, string message)
        {
            TakConnectionLog.Info("enroll", host, message);
        }

        /// <summary>
        /// Log TCP reachability for enroll (8446), streaming CoT (8089), and Marti HTTPS (8443).
        /// </summary>
        static void ProbeReachability(string host, int enrollPort)
        {
            var ports = new[]
            {
                (enrollPort > 0 ? enrollPort : 8446, "enroll HTTPS"),
                (8089, "CoT TLS stream"),
                (8443, "Marti HTTPS"),
            };
            var seen = new HashSet<int>();
            foreach (var (port, role) in ports)
            {
                if (!seen.Add(port)) continue;
                var sw = Stopwatch.StartNew();
                string result = TakAndroidHttps.ProbeTcp(host, port, 4_000);
                Note(host, $"TCP :{port} ({role}) → {result} ({sw.ElapsedMilliseconds}ms)");
            }
        }

        static HttpResult Http(
            bool preferAndroid, string host, int port, string method, string path,
            byte[] body, string contentType, string accept, string username, string password,
            int readTimeoutMs)
        {
            string url = "https://" + host + ":" + port + path;
            if (preferAndroid)
            {
                Note(host, method + " " + url + " via Android");
                var ar = TakAndroidHttps.Request(
                    url, method, username, password, body, contentType, accept,
                    ConnectTimeoutMs, readTimeoutMs);
                if (ar.Status > 0 && string.IsNullOrEmpty(ar.Error))
                {
                    return new HttpResult
                    {
                        Status = ar.Status,
                        Body = ar.Body ?? "",
                        ElapsedMs = ar.ElapsedMs,
                    };
                }

                string why = string.IsNullOrEmpty(ar.Error) ? ("HTTP " + ar.Status) : ar.Error;
                Note(host, "Android HTTPS failed (" + why + ") — falling back to Mono SslStream");
            }

            Note(host, method + " " + url + " via Mono SslStream");
            return Request(host, port, method, path, body, contentType, accept, username, password, readTimeoutMs);
        }

        static X509Name BuildSubject(string cn, SubjectExtras extras)
        {
            var oids = new ArrayList();
            var vals = new ArrayList();
            void Add(DerObjectIdentifier oid, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                oids.Add(oid);
                vals.Add(value.Trim());
            }
            Add(X509Name.C, extras.Country);
            Add(X509Name.ST, extras.State);
            Add(X509Name.L, extras.Locality);
            Add(X509Name.O, extras.Organization);
            Add(X509Name.OU, extras.OrganizationalUnit);
            Add(X509Name.CN, cn);
            return new X509Name(oids, vals);
        }

        static SubjectExtras ParseSubject(string xml)
        {
            var extras = new SubjectExtras();
            if (string.IsNullOrEmpty(xml)) return extras;
            foreach (Match entry in Regex.Matches(xml, @"<(?:\w+:)?nameEntry\b[^>]*>", RegexOptions.IgnoreCase))
            {
                string name = Attr(entry.Value, "name");
                string value = Attr(entry.Value, "value");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) continue;
                switch (name.Trim().ToUpperInvariant())
                {
                    case "O": extras.Organization = value; break;
                    case "OU": extras.OrganizationalUnit = value; break;
                    case "C": extras.Country = value; break;
                    case "ST": extras.State = value; break;
                    case "L": extras.Locality = value; break;
                }
            }
            return extras;
        }

        static string Attr(string tag, string key)
        {
            var m = Regex.Match(tag, key + @"\s*=\s*(""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[3].Value;
        }

        static List<Org.BouncyCastle.X509.X509Certificate> ExtractCertChain(
            string body, AsymmetricKeyParameter expectedPublic)
        {
            var found = new List<Org.BouncyCastle.X509.X509Certificate>();
            if (string.IsNullOrEmpty(body)) return found;

            void AddRaw(string raw)
            {
                byte[] der = TryDecodeCert(raw);
                if (der == null || der.Length < 64) return;
                try
                {
                    var single = new X509CertificateParser().ReadCertificate(der);
                    if (single != null)
                    {
                        AddUnique(found, single);
                        return;
                    }
                }
                catch { /* try collection */ }
                try
                {
                    foreach (Org.BouncyCastle.X509.X509Certificate c in new X509CertificateParser().ReadCertificates(der))
                        if (c != null) AddUnique(found, c);
                }
                catch { /* ignore non-cert blob */ }
            }

            foreach (Match m in Regex.Matches(body, @"-----BEGIN CERTIFICATE-----([\s\S]*?)-----END CERTIFICATE-----"))
                AddRaw(m.Groups[1].Value);

            foreach (var field in new[] { "signedCert", "certificate", "clientCert", "cert" })
            {
                var m = Regex.Match(body, "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (m.Success) AddRaw(m.Groups[1].Value);
            }

            foreach (Match m in Regex.Matches(body, "\"ca\\d*\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase))
                AddRaw(m.Groups[1].Value);

            foreach (Match m in Regex.Matches(
                body,
                @"<(?:signedCert|certificate|clientCert|cert|ca)\b[^>]*>\s*([^<]+)\s*</",
                RegexOptions.IgnoreCase))
            {
                AddRaw(m.Groups[1].Value);
            }

            if (found.Count == 0)
            {
                AddRaw(body);
                foreach (Match blob in Regex.Matches(body, @"[A-Za-z0-9+/]{200,}={0,2}"))
                    AddRaw(blob.Value);
            }

            if (found.Count <= 1 || expectedPublic == null) return found;

            // Ensure the leaf cert matching our generated RSA keypair is at index 0.
            for (int i = 0; i < found.Count; i++)
            {
                try
                {
                    if (found[i].GetPublicKey().Equals(expectedPublic))
                    {
                        if (i > 0)
                        {
                            var leaf = found[i];
                            found.RemoveAt(i);
                            found.Insert(0, leaf);
                        }
                        break;
                    }
                }
                catch { /* ignore */ }
            }
            return found;
        }

        static void AddUnique(
            List<Org.BouncyCastle.X509.X509Certificate> list,
            Org.BouncyCastle.X509.X509Certificate cert)
        {
            foreach (var existing in list)
            {
                if (existing.Equals(cert)) return;
            }
            list.Add(cert);
        }

        static byte[] TryDecodeCert(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Replace("\\n", "").Replace("\\r", "").Replace("\\/", "/")
                .Replace("\\u003d", "=").Replace("\\u002b", "+").Replace("\\u002f", "/");
            int begin = raw.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
            if (begin >= 0)
            {
                var inner = Regex.Match(raw, @"-----BEGIN CERTIFICATE-----([\s\S]*?)-----END CERTIFICATE-----");
                if (inner.Success) raw = inner.Groups[1].Value;
            }
            raw = CleanB64(raw);
            if (raw.Length < 64) return null;
            int pad = raw.Length % 4;
            if (pad != 0) raw = raw + new string('=', 4 - pad);
            try { return Convert.FromBase64String(raw); }
            catch { return null; }
        }

        static string CleanB64(string s) => Regex.Replace(s ?? "", @"\s+", "");

        static string Pem(string label, byte[] der)
        {
            string b64 = Convert.ToBase64String(der);
            var sb = new StringBuilder();
            sb.Append("-----BEGIN ").Append(label).Append("-----\n");
            for (int i = 0; i < b64.Length; i += 64)
            {
                int n = Math.Min(64, b64.Length - i);
                sb.Append(b64, i, n).Append('\n');
            }
            sb.Append("-----END ").Append(label).Append("-----\n");
            return sb.ToString();
        }

        static HttpResult Request(
            string host, int port, string method, string path, byte[] body, string contentType,
            string accept, string username, string password, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var addrs = ResolvePreferIpv4(host);
            Note(host, "DNS " + host + " → " + string.Join(", ", Array.ConvertAll(addrs, a => a.ToString())));
            Exception last = null;
            foreach (var addr in addrs)
            {
                TcpClient tcp = null;
                try
                {
                    Note(host, "TCP connect " + addr + ":" + port + "…");
                    tcp = Connect(addr, port, host);
                    tcp.NoDelay = true;
                    tcp.Client.Blocking = true;
                    tcp.ReceiveTimeout = timeoutMs;
                    tcp.SendTimeout = Math.Min(timeoutMs, ConnectTimeoutMs);
                    Note(host, "TCP connected (" + sw.ElapsedMilliseconds + "ms) — TLS…");
                    using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                    Authenticate(ssl, host);
                    Note(host, "TLS ok (" + sw.ElapsedMilliseconds + "ms) — " + method + " " + path);
                    WriteRequest(ssl, host, port, method, path, body, contentType, accept, username, password);
                    Note(host, "Request written — reading response (budget " + (timeoutMs / 1000) + "s)…");
                    var result = ReadResponse(tcp, ssl, timeoutMs, host);
                    result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                    tcp.Close();
                    return result;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Note(host, method + " " + addr + ":" + port + " failed: " + Clean(ex));
                    Debug.LogWarning($"[TakEnroll] {method} {addr}:{port} {ex.Message}");
                    try { tcp?.Close(); } catch { /* ignore */ }
                }
            }
            throw last ?? new IOException($"Enrollment connect failed ({host}:{port})");
        }

        static void WriteRequest(
            SslStream ssl, string host, int port, string method, string path, byte[] body, string contentType,
            string accept, string username, string password)
        {
            string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
            var head = new StringBuilder();
            head.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");
            head.Append("Authorization: Basic ").Append(auth).Append("\r\n");
            head.Append("Accept: ").Append(string.IsNullOrEmpty(accept) ? "*/*" : accept).Append("\r\n");
            head.Append("Connection: close\r\n");
            if (body != null)
            {
                head.Append("Content-Type: ").Append(contentType ?? "application/octet-stream").Append("\r\n");
                head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            }
            head.Append("\r\n");
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            if (body == null || body.Length == 0)
            {
                ssl.Write(headBytes, 0, headBytes.Length);
            }
            else
            {
                var all = new byte[headBytes.Length + body.Length];
                Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
                Buffer.BlockCopy(body, 0, all, headBytes.Length, body.Length);
                ssl.Write(all, 0, all.Length);
            }
            ssl.Flush();
        }

        static TcpClient Connect(IPAddress addr, int port, string host)
        {
            var tcp = new TcpClient(addr.AddressFamily);
            var ar = tcp.BeginConnect(addr, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
            {
                try { tcp.Close(); } catch { /* ignore */ }
                throw new TimeoutException($"Enrollment connect timed out ({host}/{addr}:{port})");
            }
            tcp.EndConnect(ar);
            return tcp;
        }

        static void Authenticate(SslStream ssl, string host)
        {
            Exception authEx = null;
            var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
                }
                catch (Exception ex) { authEx = ex; }
                finally { done.Set(); }
            })
            { IsBackground = true, Name = "TakEnroll-TLS" };
            thread.Start();
            if (!done.Wait(TlsTimeoutMs))
                throw new TimeoutException($"Enrollment TLS timed out ({host})");
            if (authEx != null)
                throw new AuthenticationException("Enrollment TLS failed: " + authEx.Message, authEx);
        }

        static IPAddress[] ResolvePreferIpv4(string host)
        {
            if (IPAddress.TryParse(host, out var literal))
                return new[] { literal };
            IPAddress[] all = null;
            Exception dnsEx = null;
            var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try { all = Dns.GetHostAddresses(host); }
                catch (Exception ex) { dnsEx = ex; }
                finally { done.Set(); }
            })
            { IsBackground = true, Name = "TakEnroll-DNS" };
            thread.Start();
            if (!done.Wait(10_000))
                throw new TimeoutException("DNS timed out for " + host);
            if (dnsEx != null)
                throw new IOException("DNS failed for " + host + ": " + dnsEx.Message, dnsEx);
            if (all == null || all.Length == 0)
                throw new IOException("DNS returned no addresses for " + host);
            var ipv4 = Array.FindAll(all, a => a.AddressFamily == AddressFamily.InterNetwork);
            return ipv4.Length > 0 ? ipv4 : all;
        }

        static HttpResult ReadResponse(TcpClient tcp, Stream stream, int timeoutMs, string host)
        {
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int start = Environment.TickCount;
            bool haveHeaders = false;
            bool loggedFirst = false;
            while (unchecked(Environment.TickCount - start) < timeoutMs)
            {
                int n;
                try
                {
                    n = stream.Read(buf, 0, buf.Length);
                }
                catch (IOException ex) when (ms.Length > 0 && IsTimeout(ex))
                {
                    Note(host, "Read timeout after " + ms.Length + " bytes — parsing what we have");
                    break;
                }
                if (n <= 0)
                {
                    Note(host, "Server closed after " + ms.Length + " bytes");
                    break;
                }
                if (!loggedFirst)
                {
                    loggedFirst = true;
                    Note(host, "First response byte after " +
                               unchecked(Environment.TickCount - start) + "ms");
                }
                ms.Write(buf, 0, n);
                byte[] snapshot = ms.ToArray();
                if (TryParseHttp(snapshot, true, out var partial))
                {
                    Note(host, "Complete HTTP body (" + ms.Length + " bytes)");
                    return partial;
                }
                if (!haveHeaders && IndexOf(snapshot, "\r\n\r\n") >= 0)
                {
                    haveHeaders = true;
                    Note(host, "HTTP headers received (" + ms.Length + " bytes so far)");
                    // Keep reading for the remaining overall budget; do not clamp to 4s —
                    // signClient/v2 often streams the JSON body slowly after headers.
                    int remaining = Math.Max(5_000, timeoutMs - unchecked(Environment.TickCount - start));
                    try { tcp.ReceiveTimeout = remaining; } catch { /* ignore */ }
                }
            }
            if (ms.Length == 0)
                throw new TimeoutException("Enrollment response timed out");
            if (TryParseHttp(ms.ToArray(), false, out var done))
                return done;
            throw new IOException("Malformed enrollment response (" + ms.Length + " bytes)");
        }

        static bool IsTimeout(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is TimeoutException) return true;
                if (e.Message != null && e.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true once the status line, headers, and a complete body are in hand.
        /// A complete body is Content-Length bytes, a finished chunked body, or — when
        /// <paramref name="needComplete"/> is false — whatever arrived before the server closed.
        /// </summary>
        static bool TryParseHttp(byte[] all, bool needComplete, out HttpResult result)
        {
            result = null;
            int headEnd = IndexOf(all, "\r\n\r\n");
            if (headEnd < 0) return false;
            string head = Encoding.ASCII.GetString(all, 0, headEnd);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var statusParts = lines[0].Split(' ');
            int status = statusParts.Length > 1 && int.TryParse(statusParts[1], out var st) ? st : 0;
            bool chunked = false;
            int contentLength = -1;
            bool gzip = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                    line.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    chunked = true;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring("Content-Length:".Length).Trim(), out var len))
                    contentLength = len;
                if (line.StartsWith("Content-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                    line.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                    gzip = true;
            }
            int bodyStart = headEnd + 4;
            int have = all.Length - bodyStart;
            if (needComplete)
            {
                if (chunked)
                {
                    if (have < 5 || (IndexOf(all, "0\r\n\r\n", bodyStart) < 0 &&
                                     !LooksLikeCompletePayload(DecodeChunked(Slice(all, bodyStart, have)))))
                        return false;
                }
                else if (contentLength >= 0)
                {
                    if (have < contentLength) return false;
                    have = contentLength;
                }
                else if (!LooksLikeCompletePayload(Slice(all, bodyStart, have)))
                {
                    return false;
                }
            }
            var raw = Slice(all, bodyStart, Math.Max(0, have));
            if (chunked) raw = DecodeChunked(raw);
            if (gzip && raw.Length > 2 && raw[0] == 0x1f && raw[1] == 0x8b)
            {
                using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var plain = new MemoryStream();
                gz.CopyTo(plain);
                raw = plain.ToArray();
            }
            result = new HttpResult { Status = status, Body = Encoding.UTF8.GetString(raw) };
            return true;
        }

        static byte[] Slice(byte[] src, int start, int len)
        {
            if (len <= 0 || src == null || start >= src.Length) return Array.Empty<byte>();
            int n = Math.Min(len, src.Length - start);
            var dst = new byte[n];
            Array.Copy(src, start, dst, 0, n);
            return dst;
        }

        static bool LooksLikeCompletePayload(byte[] raw)
        {
            if (raw == null || raw.Length < 16) return false;
            string s = Encoding.UTF8.GetString(raw).Trim();
            if (s.Length < 16) return false;
            if (s[0] == '{' && s[s.Length - 1] == '}')
            {
                int depth = 0;
                bool inStr = false;
                bool esc = false;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (esc) { esc = false; continue; }
                    if (c == '\\') { esc = true; continue; }
                    if (c == '"') { inStr = !inStr; continue; }
                    if (inStr) continue;
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                return depth == 0 && s.IndexOf("signedCert", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (s.EndsWith(">", StringComparison.Ordinal) &&
                (s.IndexOf("</CertificateConfig>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 s.IndexOf("</signedCert>", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }
            return false;
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
                pos = dataStart + size + 2;
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

        static string Compact(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n);
        }

        static string Clean(Exception ex)
        {
            string msg = ex?.Message ?? "Enrollment failed";
            if (msg.IndexOf("MAC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("tamper", StringComparison.OrdinalIgnoreCase) >= 0)
                return TakCertStore.PasswordMismatchMessage;
            return msg;
        }
    }
}
