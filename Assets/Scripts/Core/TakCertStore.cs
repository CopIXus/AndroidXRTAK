using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Core
{
    /// <summary>
    /// PKCS#12 store: default StreamingAssets cert + per-server imported P12 under
    /// persistentDataPath. Shared by TakDirectClient and TakMartiClient.
    /// </summary>
    public static class TakCertStore
    {
        public const X509KeyStorageFlags KeyFlags =
            X509KeyStorageFlags.Exportable |
            X509KeyStorageFlags.MachineKeySet |
            X509KeyStorageFlags.PersistKeySet;

        public const string PasswordMismatchMessage =
            "Cert password does not match this P12. The file was not tampered with — Enroll with server, port, username, and password.";

        public class CertMaterial
        {
            public byte[] P12;
            public string Password;
            public X509Certificate2 Certificate;
            public string Error;
        }

        [Serializable]
        class EnrolledKeyFile
        {
            public string certDer;
            public string modulus;
            public string exponent;
            public string d;
            public string p;
            public string q;
            public string dp;
            public string dq;
            public string inverseQ;
        }

        const string PrefsBindings = "takxr.certBindings";

        [Serializable]
        public class Binding
        {
            public string serverId;
            public string fileName;
            public string password;
            public bool useDefault;
        }

        [Serializable]
        class BindingList
        {
            public Binding[] items = Array.Empty<Binding>();
        }

        public static string CertsDir =>
            Path.Combine(Application.persistentDataPath, "tak-certs");

        public static Binding GetBinding(string serverId)
        {
            if (string.IsNullOrEmpty(serverId)) return null;
            var list = LoadBindings();
            foreach (var b in list.items)
                if (b != null && b.serverId == serverId) return b;
            return null;
        }

        public static bool HasCertForServer(string serverId)
        {
            if (string.IsNullOrEmpty(serverId)) return false;
            var b = GetBinding(serverId);
            if (b != null)
            {
                if (b.useDefault) return true;
                if (!string.IsNullOrEmpty(b.fileName) &&
                    File.Exists(Path.Combine(CertsDir, b.fileName)))
                    return true;
            }
            if (File.Exists(Path.Combine(CertsDir, serverId + ".p12"))) return true;
            if (File.Exists(Path.Combine(CertsDir, serverId + ".takkey"))) return true;
            return false;
        }

        public static void SetBinding(string serverId, string fileName, string password, bool useDefault)
        {
            if (string.IsNullOrEmpty(serverId)) return;
            var list = LoadBindings();
            var items = new System.Collections.Generic.List<Binding>();
            if (list.items != null)
            {
                foreach (var b in list.items)
                    if (b != null && b.serverId != serverId) items.Add(b);
            }
            items.Add(new Binding
            {
                serverId = serverId,
                fileName = fileName ?? "",
                password = password ?? "",
                useDefault = useDefault,
            });
            list.items = items.ToArray();
            PlayerPrefs.SetString(PrefsBindings, JsonUtility.ToJson(list));
            PlayerPrefs.Save();
            TakXrStateStore.Capture();
        }

        public static void UseDefault(string serverId, string defaultPassword = "")
        {
            SetBinding(serverId, "", defaultPassword, useDefault: true);
        }

        /// <summary>Import raw P12 bytes for a server entry; returns relative file name.</summary>
        public static string ImportP12(string serverId, byte[] p12, string password)
        {
            if (string.IsNullOrEmpty(serverId) || p12 == null || p12.Length == 0)
                return null;
            byte[] normalized = TryNormalizePkcs12(p12, password) ?? p12;
            Directory.CreateDirectory(CertsDir);
            string fileName = serverId + ".p12";
            string path = Path.Combine(CertsDir, fileName);
            File.WriteAllBytes(path, normalized);
            SetBinding(serverId, fileName, password ?? "", useDefault: false);
            return fileName;
        }

        /// <summary>
        /// Store a signed cert plus its RSA key when a PKCS#12 round-trip is not
        /// usable on this runtime (Mono reports that as an invalid MAC).
        /// </summary>
        public static void ImportEnrolledKey(string serverId, byte[] certDer, RSAParameters key)
        {
            if (string.IsNullOrEmpty(serverId) || certDer == null || certDer.Length == 0)
                return;
            Directory.CreateDirectory(CertsDir);
            var file = new EnrolledKeyFile
            {
                certDer = Convert.ToBase64String(certDer),
                modulus = Convert.ToBase64String(key.Modulus),
                exponent = Convert.ToBase64String(key.Exponent),
                d = Convert.ToBase64String(key.D),
                p = Convert.ToBase64String(key.P),
                q = Convert.ToBase64String(key.Q),
                dp = Convert.ToBase64String(key.DP),
                dq = Convert.ToBase64String(key.DQ),
                inverseQ = Convert.ToBase64String(key.InverseQ),
            };
            string fileName = serverId + ".enrolled.json";
            File.WriteAllText(Path.Combine(CertsDir, fileName), JsonUtility.ToJson(file));
            SetBinding(serverId, fileName, "", useDefault: false);
        }

        public static string StatusLabel(string serverId, AppConfig config)
        {
            var b = GetBinding(serverId);
            if (b != null && !b.useDefault && !string.IsNullOrEmpty(b.fileName))
            {
                string path = Path.Combine(CertsDir, b.fileName);
                if (!File.Exists(path)) return "imported (missing file)";
                return path.EndsWith(".enrolled.json", StringComparison.OrdinalIgnoreCase)
                    ? "enrolled"
                    : "imported P12";
            }
            string def = config != null ? config.takClientP12 : "takclient.p12";
            return "default (" + def + ")";
        }

        public static bool HasImported(string serverId)
        {
            var b = GetBinding(serverId);
            if (b == null || b.useDefault || string.IsNullOrEmpty(b.fileName)) return false;
            return File.Exists(Path.Combine(CertsDir, b.fileName));
        }

        /// <summary>Resolve P12 bytes + password for the active (or given) server.</summary>
        public static IEnumerator LoadP12Routine(AppConfig config, string serverId, Action<byte[], string> onDone)
        {
            byte[] data = null;
            string password = config != null ? config.takClientP12Password : "";

            var binding = GetBinding(serverId);
            if (binding != null && !binding.useDefault && !string.IsNullOrEmpty(binding.fileName))
            {
                string path = Path.Combine(CertsDir, binding.fileName);
                if (File.Exists(path) &&
                    !path.EndsWith(".enrolled.json", StringComparison.OrdinalIgnoreCase))
                {
                    data = File.ReadAllBytes(path);
                    password = string.IsNullOrEmpty(binding.password) ? password : binding.password;
                    onDone?.Invoke(data, password);
                    yield break;
                }
            }

            // Default StreamingAssets P12
            string saName = config != null ? config.takClientP12 : "takclient.p12";
            string saPath = Path.Combine(Application.streamingAssetsPath, saName);
            if (saPath.Contains("://") || saPath.StartsWith("jar:"))
            {
                using var req = UnityWebRequest.Get(saPath);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    data = req.downloadHandler.data;
            }
            else if (File.Exists(saPath))
            {
                data = File.ReadAllBytes(saPath);
            }

            onDone?.Invoke(data, password);
        }

        /// <summary>Open the server's client cert. <see cref="CertMaterial.Error"/> is set instead of throwing.</summary>
        public static IEnumerator LoadRoutine(AppConfig config, string serverId, Action<CertMaterial> onDone)
        {
            var mat = new CertMaterial();
            var binding = GetBinding(serverId);
            if (binding != null && !binding.useDefault && !string.IsNullOrEmpty(binding.fileName))
            {
                string path = Path.Combine(CertsDir, binding.fileName);
                if (File.Exists(path))
                {
                    try
                    {
                        if (path.EndsWith(".enrolled.json", StringComparison.OrdinalIgnoreCase))
                            mat.Certificate = OpenEnrolled(path);
                        else
                        {
                            mat.P12 = File.ReadAllBytes(path);
                            mat.Password = string.IsNullOrEmpty(binding.password)
                                ? (config != null ? config.takClientP12Password : "")
                                : binding.password;
                            mat.Certificate = CreateCert(mat.P12, mat.Password);
                        }
                    }
                    catch (Exception ex)
                    {
                        mat.Error = DescribeCertError(ex);
                        mat.Certificate = null;
                    }
                    onDone?.Invoke(mat);
                    yield break;
                }
            }

            byte[] data = null;
            string password = config != null ? config.takClientP12Password : "";
            string saName = config != null ? config.takClientP12 : "takclient.p12";
            string saPath = Path.Combine(Application.streamingAssetsPath, saName);
            if (saPath.Contains("://") || saPath.StartsWith("jar:"))
            {
                using var req = UnityWebRequest.Get(saPath);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    data = req.downloadHandler.data;
            }
            else if (File.Exists(saPath))
            {
                data = File.ReadAllBytes(saPath);
            }

            mat.P12 = data;
            mat.Password = password;
            if (data == null || data.Length == 0)
            {
                mat.Error = "No client certificate yet — enter server, port, username, and password, then Enroll";
                onDone?.Invoke(mat);
                yield break;
            }
            try
            {
                mat.Certificate = CreateCert(data, password);
            }
            catch (Exception ex)
            {
                mat.Error = DescribeCertError(ex);
                mat.Certificate = null;
            }
            onDone?.Invoke(mat);
        }

        public static X509Certificate2 CreateCert(byte[] p12, string password)
        {
            string pw = password ?? "";
            try
            {
                var direct = new X509Certificate2(p12, pw, KeyFlags);
                if (direct.HasPrivateKey) return direct;
            }
            catch (Exception ex)
            {
                // Mono's ASN.1 / PKCS#12 loader rejects both BER indefinite-length P12
                // ("Unable to decode certificate.") and modern AES-256 P12 ("Invalid MAC").
                // Re-encode through BouncyCastle into definite-length DER + 3DES/SHA1.
                var normalized = TryNormalizePkcs12(p12, pw);
                if (normalized != null)
                {
                    var converted = new X509Certificate2(normalized, pw, KeyFlags);
                    if (converted.HasPrivateKey) return converted;
                }
                if (IsPasswordOrMac(ex))
                    throw new CryptographicException(PasswordMismatchMessage, ex);
                throw;
            }

            var fallback = TryNormalizePkcs12(p12, pw);
            if (fallback != null)
                return new X509Certificate2(fallback, pw, KeyFlags);
            return new X509Certificate2(p12, pw, KeyFlags);
        }

        static byte[] TryNormalizePkcs12(byte[] p12, string password)
        {
            if (p12 == null || p12.Length == 0) return null;
            try
            {
                char[] pw = (password ?? "").ToCharArray();
                var src = new Pkcs12Store();
                using (var inMs = new MemoryStream(p12))
                    src.Load(inMs, pw);

                var builder = new Pkcs12StoreBuilder();
                builder.SetUseDerEncoding(true);
                var dst = builder.Build();
                foreach (string alias in src.Aliases)
                {
                    if (src.IsKeyEntry(alias))
                    {
                        var key = src.GetKey(alias);
                        var chain = src.GetCertificateChain(alias);
                        if (chain == null || chain.Length == 0)
                        {
                            var single = src.GetCertificate(alias);
                            if (single != null) chain = new[] { single };
                        }
                        if (key != null && chain != null && chain.Length > 0)
                            dst.SetKeyEntry(alias, key, chain);
                    }
                    else if (src.IsCertificateEntry(alias))
                    {
                        var cert = src.GetCertificate(alias);
                        if (cert != null) dst.SetCertificateEntry(alias, cert);
                    }
                }
                using (var outMs = new MemoryStream())
                {
                    dst.Save(outMs, pw, new SecureRandom());
                    return outMs.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        public static string DescribeCertError(Exception ex)
        {
            if (ex == null) return "Certificate failed to load";
            if (IsPasswordOrMac(ex)) return PasswordMismatchMessage;
            string msg = ex.Message ?? "Certificate failed to load";
            return msg.StartsWith("cert:", StringComparison.Ordinal) ? msg : msg;
        }

        static bool IsPasswordOrMac(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                string msg = e.Message ?? "";
                if (msg.IndexOf("MAC", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (msg.IndexOf("tamper", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (msg.IndexOf("password is not correct", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (msg.IndexOf("network password", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static X509Certificate2 OpenEnrolled(string path)
        {
            var file = JsonUtility.FromJson<EnrolledKeyFile>(File.ReadAllText(path));
            if (file == null || string.IsNullOrEmpty(file.certDer) || string.IsNullOrEmpty(file.d))
                throw new IOException("Enrolled key file is incomplete");
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Convert.FromBase64String(file.modulus),
                Exponent = Convert.FromBase64String(file.exponent),
                D = Convert.FromBase64String(file.d),
                P = Convert.FromBase64String(file.p),
                Q = Convert.FromBase64String(file.q),
                DP = Convert.FromBase64String(file.dp),
                DQ = Convert.FromBase64String(file.dq),
                InverseQ = Convert.FromBase64String(file.inverseQ),
            });
            var leaf = new X509Certificate2(Convert.FromBase64String(file.certDer));
            var withKey = leaf.CopyWithPrivateKey(rsa);
            if (!withKey.HasPrivateKey)
                throw new IOException("Enrolled certificate has no private key");
            return withKey;
        }

        static BindingList LoadBindings()
        {
            var json = PlayerPrefs.GetString(PrefsBindings, "");
            if (string.IsNullOrEmpty(json)) return new BindingList();
            try
            {
                var list = JsonUtility.FromJson<BindingList>(json);
                return list ?? new BindingList();
            }
            catch
            {
                return new BindingList();
            }
        }
    }
}
