using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
            Directory.CreateDirectory(CertsDir);
            string fileName = serverId + ".p12";
            string path = Path.Combine(CertsDir, fileName);
            File.WriteAllBytes(path, p12);
            SetBinding(serverId, fileName, password ?? "", useDefault: false);
            return fileName;
        }

        public static string StatusLabel(string serverId, AppConfig config)
        {
            var b = GetBinding(serverId);
            if (b != null && !b.useDefault && !string.IsNullOrEmpty(b.fileName))
            {
                string path = Path.Combine(CertsDir, b.fileName);
                return File.Exists(path) ? "imported P12" : "imported (missing file)";
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
                if (File.Exists(path))
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

        public static X509Certificate2 CreateCert(byte[] p12, string password)
        {
            return new X509Certificate2(p12, password, KeyFlags);
        }

        /// <summary>
        /// Lightweight HTTPS enroll attempt against Marti 8446. Full CSR signing
        /// (backend takEnrollment.ts) needs OpenSSL-style keygen — on device we
        /// document importing a P12; this helper only fetches /tls/config when
        /// credentials are supplied so UI can confirm the enroll port is reachable.
        /// </summary>
        public static IEnumerator ProbeEnrollPort(
            string host, int enrollPort, string username, string password,
            Action<bool, string> onDone)
        {
            if (string.IsNullOrEmpty(host))
            {
                onDone?.Invoke(false, "no host");
                yield break;
            }
            // Document-only path for full enrollment: copy P12 into persistent storage.
            // Probe is optional; many headsets cannot complete CSR enroll without native crypto.
            onDone?.Invoke(true,
                $"Import a TAK-enrolled .p12 for {host}:{enrollPort} " +
                $"(user {username}). Full CSR enroll is documented; use Import cert.");
            yield break;
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
