using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Core
{
    /// <summary>
    /// Merges gitignored StreamingAssets/local-config.json into AppConfig at startup.
    /// Copy local-config.json.example and fill in real TAK server values on build machines only.
    /// </summary>
    [Serializable]
    public class LocalConfigData
    {
        public string backendBaseUrl;
        public string takHost;
        public int takPort;
        public int takMartiPort;
        public int takEnrollPort;
        public string takClientP12Password;
        public string takClientP12;
    }

    public static class LocalConfigLoader
    {
        const string FileName = "local-config.json";

        public static IEnumerator ApplyAsync(AppConfig config)
        {
            if (config == null) yield break;

            string path = Path.Combine(Application.streamingAssetsPath, FileName);
            string json = null;

            if (path.Contains("://") || path.StartsWith("jar:"))
            {
                using var req = UnityWebRequest.Get(path);
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    json = req.downloadHandler.text;
            }
            else if (File.Exists(path))
            {
                json = File.ReadAllText(path);
            }

            if (string.IsNullOrEmpty(json)) yield break;

            try
            {
                var data = JsonUtility.FromJson<LocalConfigData>(json);
                if (data == null) yield break;
                Apply(config, data);
                Debug.Log("[LocalConfig] applied " + FileName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[LocalConfig] parse failed: " + ex.Message);
            }
        }

        static void Apply(AppConfig config, LocalConfigData data)
        {
            if (!string.IsNullOrEmpty(data.backendBaseUrl))
                config.backendBaseUrl = data.backendBaseUrl;
            if (!string.IsNullOrEmpty(data.takHost))
                config.takHost = data.takHost;
            if (data.takPort > 0)
                config.takPort = data.takPort;
            if (data.takMartiPort > 0)
                config.takMartiPort = data.takMartiPort;
            if (data.takEnrollPort > 0)
                config.takEnrollPort = data.takEnrollPort;
            if (!string.IsNullOrEmpty(data.takClientP12Password))
                config.takClientP12Password = data.takClientP12Password;
            if (!string.IsNullOrEmpty(data.takClientP12))
                config.takClientP12 = data.takClientP12;
        }
    }
}
