using System;
using System.Collections;
using System.Collections.Generic;
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
using UnityEngine.Networking;

namespace TakXr.Core
{
    /// <summary>
    /// On-device TAK Server quick-enroll. Same flow as the sibling backend
    /// (takEnrollment.ts) and ATAK/WinTAK/iTAK username+password enrollment:
    /// read the server DN template, POST a CSR to signClient/v2, store the
    /// signed client certificate for the CoT stream.
    /// </summary>
    public static class TakEnrollmentClient
    {
        const int ConnectTimeoutMs = 15_000;
        const int TlsTimeoutMs = 20_000;
        const int ReadTimeoutMs = 20_000;
        /// <summary>TAK Server often holds the sign connection for over a minute before it closes.</summary>
        const int SignTimeoutMs = 180_000;
        static int _busy;
        /// <summary>Local PKCS#12 password. The TAK username/password is never used as the file password.</summary>
        public const string LocalP12Password = "takxr-enroll";

        sealed class TrustAllCertHandler : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData) => true;
        }

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
        /// Uses UnityWebRequest (native HTTPS) for TAK Server /Marti/api/tls/config and
        /// /Marti/api/tls/signClient/v2 (matching TAK_Beacon's EnrollmentClient.kt), with
        /// BouncyCastle RSA-2048 CSR + definite-length DER PKCS#12 on a background thread.
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
            string basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(cleanUser + ":" + password));

            // 1. GET /Marti/api/tls/config
            Note(logHost, $"GET tls/config {logHost}:{enrollPort} user={cleanUser}");
            int cfgStatus = 0;
            string cfgBody = null;
            string cfgUrl = $"https://{logHost}:{enrollPort}/Marti/api/tls/config";
            using (var cfgReq = UnityWebRequest.Get(cfgUrl))
            {
                cfgReq.certificateHandler = new TrustAllCertHandler();
                cfgReq.disposeCertificateHandlerOnDispose = true;
                cfgReq.timeout = 15;
                cfgReq.SetRequestHeader("Authorization", "Basic " + basicAuth);
                cfgReq.SetRequestHeader("Accept", "application/xml");
                yield return cfgReq.SendWebRequest();
                cfgStatus = (int)cfgReq.responseCode;
                cfgBody = cfgReq.downloadHandler != null ? cfgReq.downloadHandler.text : null;
                if (cfgStatus <= 0 && cfgReq.result != UnityWebRequest.Result.Success)
                {
                    Note(logHost, "config UnityWebRequest fallback: " + cfgReq.error);
                }
            }

            if (cfgStatus == 401 || cfgStatus == 403)
            {
                string msg = "Enrollment rejected — check username and password (or scan a fresh QR code)";
                TakConnectionLog.Error("enroll", logHost, msg);
                Interlocked.Exchange(ref _busy, 0);
                onDone?.Invoke(false, msg);
                yield break;
            }

            SubjectExtras extras = null;
            if (cfgStatus >= 200 && cfgStatus < 300)
            {
                Note(logHost, "config HTTP " + cfgStatus);
                extras = ParseSubject(cfgBody);
            }

            // 2. Generate 2048-bit RSA keypair + CSR on background thread (and fallback GET if needed).
            AsymmetricCipherKeyPair keys = null;
            string csrBase64 = null;
            string prepErr = null;
            bool prepDone = false;
            var prepThread = new Thread(() =>
            {
                try
                {
                    if (extras == null)
                    {
                        var cfg = Request(
                            logHost, enrollPort, "GET", "/Marti/api/tls/config",
                            null, null, "application/xml", cleanUser, password);
                        Note(logHost, "config(tcp) HTTP " + cfg.Status);
                        if (cfg.Status == 401 || cfg.Status == 403)
                            throw new IOException("Enrollment rejected — check username and password (or scan a fresh QR code)");
                        extras = cfg.Status >= 200 && cfg.Status < 300
                            ? ParseSubject(cfg.Body)
                            : new SubjectExtras();
                    }
                    var random = new SecureRandom();
                    var gen = new RsaKeyPairGenerator();
                    gen.Init(new KeyGenerationParameters(random, 2048));
                    keys = gen.GenerateKeyPair();
                    var subject = BuildSubject(cleanUser, extras);
                    Note(logHost, "CSR " + subject);
                    var csr = new Pkcs10CertificationRequest(
                        "SHA256WITHRSA", subject, keys.Public, new DerSet(), keys.Private);
                    // Raw base64 without PEM headers or line breaks, matching TAK_Beacon EnrollmentClient.kt.
                    csrBase64 = Convert.ToBase64String(csr.GetEncoded());
                }
                catch (Exception ex)
                {
                    prepErr = "csr: " + Clean(ex);
                    TakConnectionLog.Error("enroll", logHost, prepErr);
                    Debug.LogError("[TakEnroll] " + prepErr + "\n" + ex);
                }
                finally
                {
                    prepDone = true;
                }
            })
            { IsBackground = true, Name = "TakEnroll-CSR" };
            prepThread.Start();
            while (!prepDone)
                yield return null;

            if (prepErr != null || keys == null || string.IsNullOrEmpty(csrBase64))
            {
                Interlocked.Exchange(ref _busy, 0);
                onDone?.Invoke(false, prepErr ?? "CSR generation failed");
                yield break;
            }

            // 3. POST /Marti/api/tls/signClient/v2?clientUid=<user>&version=v2
            // TAK Server often holds this connection 1–2 minutes while signing.
            // A short UnityWebRequest timeout aborts after the CSR was already accepted,
            // consumes one-time QR tokens, then a retry gets HTTP 401.
            string signPath = "/Marti/api/tls/signClient/v2?clientUid=" +
                              Uri.EscapeDataString(cleanUser) + "&version=v2";
            string signUrl = $"https://{logHost}:{enrollPort}{signPath}";
            Note(logHost, "POST " + signPath + " (waiting up to " + (SignTimeoutMs / 1000) + "s for signed cert)");
            int signStatus = 0;
            string signBody = null;
            string signTransportError = null;
            byte[] csrBytes = Encoding.UTF8.GetBytes(csrBase64);
            using (var postReq = new UnityWebRequest(signUrl, UnityWebRequest.kHttpVerbPOST))
            {
                postReq.uploadHandler = new UploadHandlerRaw(csrBytes)
                {
                    contentType = "text/plain; charset=utf-8",
                };
                postReq.downloadHandler = new DownloadHandlerBuffer();
                postReq.certificateHandler = new TrustAllCertHandler();
                postReq.disposeCertificateHandlerOnDispose = true;
                postReq.timeout = SignTimeoutMs / 1000; // seconds
                postReq.SetRequestHeader("Authorization", "Basic " + basicAuth);
                postReq.SetRequestHeader("Accept", "application/json");
                postReq.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
                yield return postReq.SendWebRequest();
                signStatus = (int)postReq.responseCode;
                signBody = postReq.downloadHandler != null ? postReq.downloadHandler.text : null;
                if (signStatus <= 0 && postReq.result != UnityWebRequest.Result.Success)
                {
                    signTransportError = postReq.error ?? postReq.result.ToString();
                    Note(logHost, "sign UnityWebRequest: " + signTransportError);
                }
            }

            // 4. Package DER PKCS#12 on background thread.
            // Only fall back to a raw TLS socket when Unity never opened the POST
            // (DNS/connect/SSL). Never re-POST after a timeout — that burns QR tokens.
            var work = new Work { Step = "pfx" };
            var pfxThread = new Thread(() =>
            {
                try
                {
                    bool canSocketFallback = signStatus <= 0 &&
                        !string.IsNullOrEmpty(signTransportError) &&
                        LooksLikePreSendFailure(signTransportError);
                    if (signStatus <= 0 && canSocketFallback)
                    {
                        work.Step = "sign";
                        Note(logHost, "sign socket fallback after pre-send failure");
                        var signed = Request(
                            logHost, enrollPort, "POST", signPath, csrBytes,
                            "text/plain; charset=utf-8", "application/json", cleanUser, password);
                        signStatus = signed.Status;
                        signBody = signed.Body;
                    }
                    else if (signStatus <= 0)
                    {
                        throw new IOException(
                            "Signing timed out or failed after the CSR was sent (" +
                            (signTransportError ?? "no response") +
                            "). Generate a fresh QR code (or re-enter password) and try again.");
                    }

                    Note(logHost, "sign HTTP " + signStatus + " body " +
                                  (signBody != null ? signBody.Length : 0) + " " + Compact(signBody, 160));

                    if (signStatus == 401 || signStatus == 403)
                        throw new IOException("Enrollment rejected — check username and password (or scan a fresh QR code)");
                    if (signStatus < 200 || signStatus >= 300)
                    {
                        string snippet = Compact(signBody, 180);
                        throw new IOException(
                            $"Enrollment failed (HTTP {signStatus}). {snippet}");
                    }

                    var chain = ExtractCertChain(signBody, keys.Public);
                    if (chain == null || chain.Count == 0)
                        throw new IOException(
                            "Enrollment response had no signed certificate. " + Compact(signBody, 220));

                    work.Step = "pfx";
                    // Mono's ASN.1 reader rejects BouncyCastle's default BER indefinite-length
                    // Pkcs12Store with "Unable to decode certificate." SetUseDerEncoding(true)
                    // writes definite-length DER that Unity/Mono X509Certificate2 loads cleanly.
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
            { IsBackground = true, Name = "TakEnroll-PFX" };
            pfxThread.Start();
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
        /// True when UnityWebRequest failed before the CSR body was likely accepted
        /// by TAK Server (safe to retry over a raw TLS socket).
        /// </summary>
        static bool LooksLikePreSendFailure(string error)
        {
            if (string.IsNullOrEmpty(error)) return false;
            string e = error.ToLowerInvariant();
            if (e.Contains("timeout") || e.Contains("timed out")) return false;
            return e.Contains("cannot resolve") ||
                   e.Contains("name resolution") ||
                   e.Contains("could not resolve") ||
                   e.Contains("connection refused") ||
                   e.Contains("failed to connect") ||
                   e.Contains("unable to connect") ||
                   e.Contains("network is unreachable") ||
                   e.Contains("no route to host") ||
                   e.Contains("ssl") ||
                   e.Contains("tls") ||
                   e.Contains("certificate");
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
            string accept, string username, string password)
        {
            var addrs = ResolvePreferIpv4(host);
            Exception last = null;
            foreach (var addr in addrs)
            {
                TcpClient tcp = null;
                try
                {
                    tcp = Connect(addr, port, host);
                    tcp.NoDelay = true;
                    tcp.Client.Blocking = true;
                    int timeoutMs = method == "POST" ? SignTimeoutMs : ReadTimeoutMs;
                    tcp.ReceiveTimeout = timeoutMs;
                    tcp.SendTimeout = timeoutMs;
                    using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                    Authenticate(ssl, host);
                    WriteRequest(ssl, host, port, method, path, body, contentType, accept, username, password);
                    var result = ReadResponse(tcp, ssl, timeoutMs);
                    tcp.Close();
                    return result;
                }
                catch (Exception ex)
                {
                    last = ex;
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

        static HttpResult ReadResponse(TcpClient tcp, Stream stream, int timeoutMs)
        {
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int waited = 0;
            bool shortenedTimeout = false;
            while (waited < timeoutMs)
            {
                int n;
                try
                {
                    n = stream.Read(buf, 0, buf.Length);
                }
                catch (IOException ex) when (ms.Length > 0 && IsTimeout(ex))
                {
                    break;
                }
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                byte[] snapshot = ms.ToArray();
                if (TryParseHttp(snapshot, true, out var partial))
                    return partial;
                if (!shortenedTimeout && IndexOf(snapshot, "\r\n\r\n") >= 0)
                {
                    // Once headers arrive, don't let a keep-alive server stall for 3 minutes
                    // if it omitted Content-Length.
                    shortenedTimeout = true;
                    try { tcp.ReceiveTimeout = 4000; } catch { /* ignore */ }
                }
                waited += 1;
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
