using System;
using System.IO;
using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// Dual-write backup of PlayerPrefs into persistentDataPath so settings,
    /// servers, and world tilt survive APK updates even if prefs are empty
    /// on first launch of a new build.
    /// </summary>
    public static class TakXrStateStore
    {
        const string FileName = "takxr-state.json";

        [Serializable]
        public class Blob
        {
            public string backendUrl;
            public float moveSpeed = 1f;
            public float iconScale = 1.5f;
            public float textScale = 1.5f;
            public int snapTurn;
            public int allowBackendFallback;
            public float worldPitchDeg;
            public string identityJson;
            public string serversJson;
            public string certBindingsJson;
            public string layersJson;
        }

        static string FilePath =>
            Path.Combine(Application.persistentDataPath, FileName);

        public static void RestorePrefsIfEmpty()
        {
            // If servers (or identity) already live in PlayerPrefs, this is a
            // normal launch after an update — prefs survived. Only hydrate
            // from the file when prefs look wiped.
            bool prefsEmpty = !PlayerPrefs.HasKey(TakServerDirectory.PrefsKey)
                              && !PlayerPrefs.HasKey("takxr.identity")
                              && !PlayerPrefs.HasKey("takxr.worldPitchDeg");
            if (!prefsEmpty) return;

            var blob = TryRead();
            if (blob == null) return;

            if (!string.IsNullOrEmpty(blob.serversJson))
                PlayerPrefs.SetString(TakServerDirectory.PrefsKey, blob.serversJson);
            if (!string.IsNullOrEmpty(blob.identityJson))
                PlayerPrefs.SetString("takxr.identity", blob.identityJson);
            if (!string.IsNullOrEmpty(blob.certBindingsJson))
                PlayerPrefs.SetString("takxr.certBindings", blob.certBindingsJson);
            if (!string.IsNullOrEmpty(blob.layersJson))
                PlayerPrefs.SetString("takxr.layers", blob.layersJson);
            if (!string.IsNullOrEmpty(blob.backendUrl))
                PlayerPrefs.SetString("takxr.backendUrl", blob.backendUrl);
            PlayerPrefs.SetFloat("takxr.moveSpeed", blob.moveSpeed);
            PlayerPrefs.SetFloat("takxr.cotIconScale", blob.iconScale);
            PlayerPrefs.SetFloat("takxr.cotTextScale", blob.textScale);
            PlayerPrefs.SetInt("takxr.snapTurn", blob.snapTurn);
            PlayerPrefs.SetInt("takxr.allowBackendFallback", blob.allowBackendFallback);
            PlayerPrefs.SetFloat("takxr.worldPitchDeg", blob.worldPitchDeg);
            PlayerPrefs.Save();
            Debug.Log("[TakXrState] restored PlayerPrefs from " + FilePath);
        }

        public static void Capture()
        {
            try
            {
                var blob = new Blob
                {
                    backendUrl = PlayerPrefs.GetString("takxr.backendUrl", ""),
                    moveSpeed = PlayerPrefs.GetFloat("takxr.moveSpeed", 1f),
                    iconScale = PlayerPrefs.GetFloat("takxr.cotIconScale", 1.5f),
                    textScale = PlayerPrefs.GetFloat("takxr.cotTextScale", 1.5f),
                    snapTurn = PlayerPrefs.GetInt("takxr.snapTurn", 0),
                    allowBackendFallback = PlayerPrefs.GetInt("takxr.allowBackendFallback", 0),
                    worldPitchDeg = PlayerPrefs.GetFloat("takxr.worldPitchDeg", 0f),
                    identityJson = PlayerPrefs.GetString("takxr.identity", ""),
                    serversJson = PlayerPrefs.GetString(TakServerDirectory.PrefsKey, ""),
                    certBindingsJson = PlayerPrefs.GetString("takxr.certBindings", ""),
                    layersJson = PlayerPrefs.GetString("takxr.layers", ""),
                };
                Directory.CreateDirectory(Application.persistentDataPath);
                File.WriteAllText(FilePath, JsonUtility.ToJson(blob, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakXrState] save failed: " + ex.Message);
            }
        }

        static Blob TryRead()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrEmpty(json)) return null;
                return JsonUtility.FromJson<Blob>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TakXrState] load failed: " + ex.Message);
                return null;
            }
        }
    }
}
