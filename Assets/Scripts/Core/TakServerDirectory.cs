using System;
using System.Collections.Generic;
using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// One saved TAK server (CoT stream + Marti), matching ATAK's multi-server list.
    /// </summary>
    [Serializable]
    public class TakServerEntry
    {
        public string id;
        public string displayName;
        public string host;
        public int cotPort = 8089;
        public int martiPort = 8443;
        public int enrollPort = 8446;
        public string note;
        /// <summary>Optional display-only: last known cert status label.</summary>
        public string certNote;
        /// <summary>When true, keep a live CoT TLS session to this host (multi-connect).</summary>
        public bool wantConnected;

        public string EndpointLabel =>
            $"{(string.IsNullOrEmpty(host) ? "?" : host)}:{cotPort}";
    }

    /// <summary>
    /// PlayerPrefs-backed list of TAK servers + active selection.
    /// Seeds from <see cref="AppConfig"/> on first run and keeps config fields
    /// in sync with the active entry.
    /// </summary>
    public static class TakServerDirectory
    {
        public const string PrefsKey = "takxr.servers";

        /// <summary>XR-friendly host cycle list (plus any previously saved hosts).</summary>
        public static readonly string[] BuiltInHostPresets = Array.Empty<string>();

        public static readonly int[] CotPortPresets = { 8089, 8088, 8087 };
        public static readonly int[] MartiPortPresets = { 8443, 8444, 8089 };

        [Serializable]
        public class State
        {
            public string activeId;
            public TakServerEntry[] servers = Array.Empty<TakServerEntry>();

            public List<TakServerEntry> ServerList
            {
                get
                {
                    var list = new List<TakServerEntry>();
                    if (servers == null) return list;
                    foreach (var s in servers)
                        if (s != null) list.Add(s);
                    return list;
                }
                set
                {
                    servers = value == null || value.Count == 0
                        ? Array.Empty<TakServerEntry>()
                        : value.ToArray();
                }
            }
        }

        static State _cache;

        public static State LoadOrSeed(AppConfig config)
        {
            if (_cache != null) return _cache;

            var json = PlayerPrefs.GetString(PrefsKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    _cache = JsonUtility.FromJson<State>(json);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TakServers] prefs parse failed: " + ex.Message);
                }
            }

            if (_cache == null) _cache = new State();
            if (_cache.servers == null) _cache.servers = Array.Empty<TakServerEntry>();

            if (_cache.servers.Length == 0 && config != null && !string.IsNullOrEmpty(config.takHost))
            {
                var seed = MakeEntry(
                    displayName: config.takHost,
                    host: config.takHost,
                    cotPort: config.takPort > 0 ? config.takPort : 8089,
                    martiPort: config.takMartiPort > 0 ? config.takMartiPort : 8443,
                    note: "Seeded from AppConfig");
                _cache.servers = new[] { seed };
                _cache.activeId = seed.id;
                Save(_cache);
            }

            EnsureActiveValid(_cache);
            EnsureWantConnected(_cache);
            return _cache;
        }

        public static void Save(State state)
        {
            if (state == null) return;
            _cache = state;
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(state));
            PlayerPrefs.Save();
            TakXrStateStore.Capture();
        }

        public static TakServerEntry GetActive(State state)
        {
            if (state?.servers == null || state.servers.Length == 0) return null;
            if (!string.IsNullOrEmpty(state.activeId))
            {
                foreach (var s in state.servers)
                    if (s != null && s.id == state.activeId) return s;
            }
            return state.servers[0];
        }

        public static void ApplyToConfig(AppConfig config, TakServerEntry entry)
        {
            if (config == null || entry == null) return;
            config.takHost = entry.host ?? "";
            config.takPort = entry.cotPort > 0 ? entry.cotPort : 8089;
            config.takMartiPort = entry.martiPort > 0 ? entry.martiPort : 8443;
        }

        /// <summary>Load prefs (or seed), apply active host/ports onto config.</summary>
        public static TakServerEntry EnsureApplied(AppConfig config)
        {
            var state = LoadOrSeed(config);
            var active = GetActive(state);
            ApplyToConfig(config, active);
            return active;
        }

        public static bool SetActive(State state, string id)
        {
            if (state?.servers == null) return false;
            foreach (var s in state.servers)
            {
                if (s == null || s.id != id) continue;
                state.activeId = id;
                Save(state);
                return true;
            }
            return false;
        }

        public static TakServerEntry AddClone(State state, TakServerEntry template, string displayName = null)
        {
            if (state == null) return null;
            var list = state.ServerList;

            var src = template ?? GetActive(state);
            int n = list.Count + 1;
            var entry = MakeEntry(
                displayName: string.IsNullOrEmpty(displayName) ? $"Server {n}" : displayName,
                host: src != null && !string.IsNullOrEmpty(src.host) ? src.host : "",
                cotPort: src != null && src.cotPort > 0 ? src.cotPort : 8089,
                martiPort: src != null && src.martiPort > 0 ? src.martiPort : 8443,
                note: "");
            list.Add(entry);
            state.ServerList = list;
            Save(state);
            return entry;
        }

        public static TakServerEntry AddFromConfig(State state, AppConfig config)
        {
            if (state == null || config == null) return null;
            var list = state.ServerList;

            foreach (var s in list)
            {
                if (s == null) continue;
                if (string.Equals(s.host, config.takHost, StringComparison.OrdinalIgnoreCase) &&
                    s.cotPort == config.takPort && s.martiPort == config.takMartiPort)
                    return s;
            }

            var entry = MakeEntry(
                displayName: string.IsNullOrEmpty(config.takHost) ? "Imported" : config.takHost,
                host: config.takHost,
                cotPort: config.takPort > 0 ? config.takPort : 8089,
                martiPort: config.takMartiPort > 0 ? config.takMartiPort : 8443,
                note: "Imported from AppConfig");
            list.Add(entry);
            state.ServerList = list;
            Save(state);
            return entry;
        }

        public static bool Delete(State state, string id)
        {
            if (state?.servers == null || string.IsNullOrEmpty(id)) return false;
            var list = state.ServerList;
            int removed = list.RemoveAll(s => s != null && s.id == id);
            if (removed <= 0) return false;
            if (state.activeId == id)
                state.activeId = list.Count > 0 ? list[0].id : null;
            state.ServerList = list;
            Save(state);
            return true;
        }

        public static void UpdateEntry(State state, TakServerEntry entry)
        {
            if (state == null || entry == null) return;
            Save(state);
        }

        public static List<string> HostChoices(State state)
        {
            var list = new List<string>();
            void Add(string h)
            {
                if (string.IsNullOrEmpty(h)) return;
                foreach (var x in list)
                    if (string.Equals(x, h, StringComparison.OrdinalIgnoreCase)) return;
                list.Add(h);
            }
            foreach (var p in BuiltInHostPresets) Add(p);
            if (state?.servers != null)
            {
                foreach (var s in state.servers)
                    if (s != null) Add(s.host);
            }
            return list;
        }

        static void EnsureWantConnected(State state)
        {
            if (state?.servers == null || state.servers.Length == 0) return;
            bool any = false;
            foreach (var s in state.servers)
                if (s != null && s.wantConnected) { any = true; break; }
            if (any) return;
            var active = GetActive(state);
            if (active == null) return;
            active.wantConnected = true;
            Save(state);
        }

        public static void SetWantConnected(State state, string id, bool want)
        {
            if (state?.servers == null || string.IsNullOrEmpty(id)) return;
            foreach (var s in state.servers)
            {
                if (s == null || s.id != id) continue;
                s.wantConnected = want;
                Save(state);
                return;
            }
        }

        public static TakServerEntry AddFromHost(
            State state, string host, int cotPort = 8089, int martiPort = 8443, string displayName = null)
        {
            if (state == null || string.IsNullOrEmpty(host)) return null;
            host = host.Trim();
            int parsedPort = cotPort;
            // Allow "host:8089"
            int colon = host.LastIndexOf(':');
            if (colon > 0 && colon < host.Length - 1)
            {
                var portPart = host.Substring(colon + 1);
                if (int.TryParse(portPart, out var p) && p > 0 && p < 65536)
                {
                    parsedPort = p;
                    host = host.Substring(0, colon);
                }
            }

            var list = state.ServerList;
            foreach (var s in list)
            {
                if (s == null) continue;
                if (string.Equals(s.host, host, StringComparison.OrdinalIgnoreCase) && s.cotPort == parsedPort)
                {
                    if (!string.IsNullOrEmpty(displayName)) s.displayName = displayName;
                    Save(state);
                    return s;
                }
            }

            var entry = MakeEntry(
                displayName: string.IsNullOrEmpty(displayName) ? host : displayName,
                host: host,
                cotPort: parsedPort > 0 ? parsedPort : 8089,
                martiPort: martiPort > 0 ? martiPort : 8443,
                note: "");
            list.Add(entry);
            state.ServerList = list;
            Save(state);
            return entry;
        }

        static void EnsureActiveValid(State state)
        {
            if (state.servers == null || state.servers.Length == 0) return;
            if (GetActive(state) != null &&
                !string.IsNullOrEmpty(state.activeId))
            {
                foreach (var s in state.servers)
                    if (s != null && s.id == state.activeId) return;
            }
            state.activeId = state.servers[0].id;
            Save(state);
        }

        static TakServerEntry MakeEntry(string displayName, string host, int cotPort, int martiPort, string note)
        {
            return new TakServerEntry
            {
                id = Guid.NewGuid().ToString("N"),
                displayName = displayName,
                host = host,
                cotPort = cotPort,
                martiPort = martiPort,
                note = note ?? "",
            };
        }
    }
}
