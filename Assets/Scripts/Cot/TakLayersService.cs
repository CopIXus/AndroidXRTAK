using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using TakXr.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Cot
{
    [Serializable]
    public class ChannelRow
    {
        public string name;
        public bool active;
        /// <summary>"server" (TAK group) or "layer" (imported package / mission).</summary>
        public string source;
    }

    [Serializable]
    public class PackageRow
    {
        public string hash;
        public string name;
        public bool imported;
    }

    [Serializable]
    public class MissionRow
    {
        public string name;
        public string description;
        public bool subscribed;
    }

    /// <summary>
    /// Standalone channels / data packages / Data Sync (missions) against the TAK
    /// server's Marti API — full port of the backend TakFeaturesService, running
    /// on the headset. Package and mission CoTs are injected into the shared feed
    /// tagged group "package:<name>" / "mission:<name>"; imports persist across
    /// restarts via PlayerPrefs.
    /// </summary>
    public class TakLayersService : MonoBehaviour
    {
        const string PrefsKey = "takxr.layers";
        const float MissionPollSec = 60f;

        [Serializable]
        class PersistedLayers
        {
            public List<string> packageHashes = new List<string>();
            public List<string> missionNames = new List<string>();
        }

        AppConfig _config;
        CotFeedClient _feed;
        TakDirectHub _direct;
        TakMartiClient _marti;
        string _clientUid;
        byte[] _p12;
        bool _p12LoadStarted;
        bool _p12LoadFailed;

        readonly Dictionary<string, ChannelRow> _channels = new Dictionary<string, ChannelRow>();
        readonly Dictionary<string, HashSet<string>> _layerUids = new Dictionary<string, HashSet<string>>();
        /// <summary>Layers the user switched off — their CoTs are held out of the feed.</summary>
        readonly HashSet<string> _hiddenLayers = new HashSet<string>();
        readonly Dictionary<string, List<NormalizedCot>> _layerCots = new Dictionary<string, List<NormalizedCot>>();
        readonly HashSet<string> _importedPackages = new HashSet<string>();
        readonly Dictionary<string, string> _packageNames = new Dictionary<string, string>(); // hash → name
        readonly HashSet<string> _subscribedMissions = new HashSet<string>();
        bool _restored;

        public string LastError { get; private set; }
        public bool MartiReady => _marti != null;

        static string FarFutureStale() =>
            DateTime.UtcNow.AddDays(365).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        public void Configure(AppConfig config, CotFeedClient feed, TakDirectHub direct)
        {
            _config = config;
            _feed = feed;
            _direct = direct;
            _clientUid = TakIdentity.ClientUid;
            // Load P12 ourselves — do not wait for TakDirectClient (Marti UI can open first).
            if (!_p12LoadStarted && _config != null)
            {
                _p12LoadStarted = true;
                StartCoroutine(LoadP12Coroutine());
            }
        }

        string _p12Password;

        IEnumerator LoadP12Coroutine()
        {
            // Prefer bytes already loaded by the CoT client when available.
            if (_direct != null && _direct.P12Bytes != null && _direct.P12Bytes.Length > 0)
            {
                _p12 = _direct.P12Bytes;
                _p12Password = _direct.P12Password;
                TryInitMarti("direct-client");
                yield break;
            }

            var active = TakServerDirectory.GetActive(TakServerDirectory.LoadOrSeed(_config));
            yield return TakCertStore.LoadP12Routine(_config, active?.id, (bytes, pwd) =>
            {
                _p12 = bytes;
                _p12Password = pwd;
            });

            if (_p12 == null || _p12.Length == 0)
            {
                _p12LoadFailed = true;
                LastError = "Marti cert missing";
                Debug.LogWarning("[TakLayers] " + LastError);
                yield break;
            }

            TryInitMarti("cert-store");
        }

        void TryInitMarti(string source)
        {
            if (_marti != null || _config == null || _p12 == null || _p12.Length == 0) return;
            try
            {
                string pwd = !string.IsNullOrEmpty(_p12Password)
                    ? _p12Password
                    : (_direct != null ? _direct.P12Password : _config.takClientP12Password);
                _marti = new TakMartiClient(_config.takHost, _config.takMartiPort, _p12, pwd);
                LastError = null;
                Debug.Log($"[TakLayers] Marti client ready ({_config.takHost}:{_config.takMartiPort}, uid {_clientUid}, cert={source}, {_p12.Length} B)");
            }
            catch (Exception ex)
            {
                _p12LoadFailed = true;
                LastError = ex.Message;
                Debug.LogError("[TakLayers] Marti init failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Recreate the Marti REST client against the current
        /// <see cref="AppConfig.takHost"/> / <see cref="AppConfig.takMartiPort"/>
        /// (after switching the active TAK server).
        /// </summary>
        public void RebindMartiHost()
        {
            _marti = null;
            LastError = null;
            _clientUid = TakIdentity.ClientUid;
            if (_config == null)
            {
                LastError = "No AppConfig";
                return;
            }
            // Reload cert for the newly active server (per-server P12 binding).
            _p12 = null;
            _p12Password = null;
            _p12LoadStarted = true;
            _p12LoadFailed = false;
            StartCoroutine(LoadP12Coroutine());
        }

        void Update()
        {
            // Fallback: pick up cert from TakDirectClient if our own load is still pending.
            if (_marti == null && _config != null && !_p12LoadFailed)
            {
                if ((_p12 == null || _p12.Length == 0) && _direct != null &&
                    _direct.P12Bytes != null && _direct.P12Bytes.Length > 0)
                {
                    _p12 = _direct.P12Bytes;
                    TryInitMarti("direct-client-late");
                }
                else if (_p12 != null && _p12.Length > 0)
                {
                    TryInitMarti("cached-p12");
                }
            }

            if (_marti != null && !_restored)
            {
                _restored = true;
                _ = RestorePersistedLayers();
                InvokeRepeating(nameof(PollMissions), MissionPollSec, MissionPollSec);
            }
        }

        /// <summary>Wait briefly for Marti cert/client so UI calls don't race boot.</summary>
        async Task<bool> EnsureMartiAsync(int timeoutMs = 20_000)
        {
            if (_marti != null) return true;
            if (_p12LoadFailed)
            {
                if (string.IsNullOrEmpty(LastError))
                    LastError = "Marti cert not available";
                return false;
            }
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (_marti == null && DateTime.UtcNow < deadline)
            {
                if (_p12LoadFailed) break;
                await Task.Delay(100);
            }
            if (_marti == null)
            {
                LastError = LastError ?? "Marti not ready (cert still loading)";
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Channels
        // ------------------------------------------------------------------

        public async Task<List<ChannelRow>> ListChannels()
        {
            if (await EnsureMartiAsync() && _marti != null)
            {
                try
                {
                    var groups = await _marti.GetGroups();
                    // Merge IN/OUT direction rows into one toggle per group name.
                    foreach (var g in groups)
                    {
                        if (string.IsNullOrEmpty(g.name)) continue;
                        if (_channels.TryGetValue(g.name, out var row) && row.source == "server")
                            row.active = row.active || g.active;
                        else
                            _channels[g.name] = new ChannelRow { name = g.name, active = g.active, source = "server" };
                    }
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Debug.LogWarning("[TakLayers] groups fetch failed: " + ex.Message);
                }
            }
            var list = new List<ChannelRow>(_channels.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return list;
        }

        public async Task SetChannelActive(string name, bool active)
        {
            if (!_channels.TryGetValue(name, out var row))
            {
                row = new ChannelRow { name = name, active = active, source = "layer" };
                _channels[name] = row;
            }
            row.active = active;

            if (row.source == "server" && _marti != null)
            {
                try
                {
                    // Presence before group filter: Marti associates filters with clientUid.
                    // SelfPresence must publish with the same TakIdentity.ClientUid — otherwise
                    // server-side channel filters will not match this headset. Local layer
                    // hide (package:/mission:) still works without server presence.
                    _clientUid = TakIdentity.ClientUid;
                    var groups = await _marti.GetGroups();
                    foreach (var g in groups)
                        if (g.name == name) g.active = active;
                    await _marti.SetActiveGroups(groups, _clientUid);
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Debug.LogWarning("[TakLayers] setActiveGroups failed: " + ex.Message);
                }
            }
            else
            {
                // Local layer (package:/mission:): show/hide its injected CoTs.
                if (active) ShowLayer(name); else HideLayer(name);
            }
        }

        void HideLayer(string layer)
        {
            _hiddenLayers.Add(layer);
            if (!_layerUids.TryGetValue(layer, out var uids)) return;
            foreach (var uid in uids) _feed.RemoveByUid(uid, notify: false);
            _feed.NotifyChanged();
        }

        void ShowLayer(string layer)
        {
            _hiddenLayers.Remove(layer);
            if (!_layerCots.TryGetValue(layer, out var cots)) return;
            foreach (var cot in cots) _feed.UpsertDirect(cot, notify: false);
            _feed.NotifyChanged();
        }

        // ------------------------------------------------------------------
        // Layer CoT injection
        // ------------------------------------------------------------------

        void InjectLayerCot(string layer, NormalizedCot cot)
        {
            cot.group = layer;
            cot.stale = FarFutureStale();
            if (!_layerUids.TryGetValue(layer, out var uids))
            {
                uids = new HashSet<string>();
                _layerUids[layer] = uids;
            }
            uids.Add(cot.uid);
            if (!_layerCots.TryGetValue(layer, out var cots))
            {
                cots = new List<NormalizedCot>();
                _layerCots[layer] = cots;
            }
            cots.RemoveAll(c => c.uid == cot.uid);
            cots.Add(cot);
            if (!_hiddenLayers.Contains(layer))
                _feed.UpsertDirect(cot, notify: false);
        }

        void RemoveLayer(string layer)
        {
            if (_layerUids.TryGetValue(layer, out var uids))
            {
                foreach (var uid in uids) _feed.RemoveByUid(uid, notify: false);
                _layerUids.Remove(layer);
            }
            _layerCots.Remove(layer);
            _hiddenLayers.Remove(layer);
            _channels.Remove(layer);
            _feed.NotifyChanged();
        }

        void RegisterLayerChannel(string layer)
        {
            if (!_channels.ContainsKey(layer))
                _channels[layer] = new ChannelRow { name = layer, active = true, source = "layer" };
        }

        // ------------------------------------------------------------------
        // Data packages
        // ------------------------------------------------------------------

        public async Task<List<PackageRow>> ListPackages()
        {
            var list = new List<PackageRow>();
            if (!await EnsureMartiAsync() || _marti == null) return list;
            try
            {
                var pkgs = await _marti.SearchPackages();
                foreach (var p in pkgs)
                {
                    var hash = p.Hash;
                    if (string.IsNullOrEmpty(hash)) continue;
                    var name = !string.IsNullOrEmpty(p.Name) ? p.Name : (p.UID ?? hash.Substring(0, 8));
                    _packageNames[hash] = name;
                    list.Add(new PackageRow { hash = hash, name = name, imported = _importedPackages.Contains(hash) });
                }
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogWarning("[TakLayers] package search failed: " + ex.Message);
            }
            return list;
        }

        public async Task<int> ImportPackage(string hash)
        {
            if (_marti == null) return 0;
            if (!_packageNames.ContainsKey(hash)) await ListPackages();
            var name = _packageNames.TryGetValue(hash, out var n) ? n : hash.Substring(0, 8);
            var layer = "package:" + name;

            var zipBytes = await _marti.DownloadPackage(hash);
            int count = ImportZip(zipBytes, layer);
            _importedPackages.Add(hash);
            RegisterLayerChannel(layer);
            PersistLayers();
            _feed.NotifyChanged();
            Debug.Log($"[TakLayers] imported package {name}: {count} CoTs");
            return count;
        }

        public async Task RemovePackage(string hash)
        {
            _importedPackages.Remove(hash);
            if (!_packageNames.ContainsKey(hash)) await ListPackages();
            var name = _packageNames.TryGetValue(hash, out var n) ? n : hash;
            RemoveLayer("package:" + name);
            PersistLayers();
        }

        /// <summary>Extract CoT events (.cot/.xml, not the manifest) from a data package zip.
        /// Also extracts iconset folders (iconset.xml + PNGs) into persistent map-icons.</summary>
        int ImportZip(byte[] zipBytes, string layer)
        {
            int count = 0;
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            // Pass 1: detect iconset.xml entries and extract sibling trees.
            ExtractIconsetsFromZip(zip);

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory
                var lower = entry.FullName.ToLowerInvariant();
                if (!lower.EndsWith(".cot") && !lower.EndsWith(".xml")) continue;
                if (lower.Contains("manifest") || lower.EndsWith("iconset.xml")) continue;
                string text;
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                    text = reader.ReadToEnd();
                foreach (var eventXml in TakDirectClient.SplitEvents(text))
                {
                    var cot = TakDirectClient.ParseCot(eventXml);
                    if (cot == null) continue;
                    InjectLayerCot(layer, cot);
                    count++;
                }
            }
            return count;
        }

        void ExtractIconsetsFromZip(ZipArchive zip)
        {
            var iconsetEntries = new List<ZipArchiveEntry>();
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith("iconset.xml", StringComparison.OrdinalIgnoreCase))
                    iconsetEntries.Add(e);
            }
            if (iconsetEntries.Count == 0) return;

            string root = Path.Combine(Application.persistentDataPath, "map-icons");
            Directory.CreateDirectory(root);
            foreach (var xmlEntry in iconsetEntries)
            {
                // Parent folder name becomes the iconset dir.
                string dirPart = Path.GetDirectoryName(xmlEntry.FullName.Replace('/', Path.DirectorySeparatorChar)) ?? "";
                string setName = string.IsNullOrEmpty(dirPart)
                    ? "Imported"
                    : Path.GetFileName(dirPart.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(setName)) setName = "Imported";
                string dest = Path.Combine(root, setName);
                string prefix = string.IsNullOrEmpty(dirPart) ? "" : dirPart.Replace('\\', '/') + "/";
                try
                {
                    Directory.CreateDirectory(dest);
                    foreach (var e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;
                        string full = e.FullName.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(prefix) &&
                            !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.IsNullOrEmpty(prefix) && full.Contains("/")) continue;
                        string rel = string.IsNullOrEmpty(prefix) ? e.Name : full.Substring(prefix.Length);
                        string outPath = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? dest);
                        using var es = e.Open();
                        using var fs = File.Create(outPath);
                        es.CopyTo(fs);
                    }
                    IconResolver.RegisterExtractedDir(dest);
                    Debug.Log("[TakLayers] extracted iconset " + setName);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TakLayers] iconset extract: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // Missions (Data Sync)
        // ------------------------------------------------------------------

        public async Task<List<MissionRow>> ListMissions()
        {
            var list = new List<MissionRow>();
            if (!await EnsureMartiAsync() || _marti == null) return list;
            try
            {
                var missions = await _marti.GetMissions();
                foreach (var m in missions)
                {
                    if (string.IsNullOrEmpty(m.name)) continue;
                    list.Add(new MissionRow
                    {
                        name = m.name,
                        description = m.description,
                        subscribed = _subscribedMissions.Contains(m.name),
                    });
                }
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogWarning("[TakLayers] mission list failed: " + ex.Message);
            }
            return list;
        }

        public async Task<int> SubscribeMission(string name)
        {
            if (_marti == null) return 0;
            await _marti.SubscribeMission(name, _clientUid);
            int count = await PullMissionCots(name);
            _subscribedMissions.Add(name);
            RegisterLayerChannel("mission:" + name);
            PersistLayers();
            _feed.NotifyChanged();
            Debug.Log($"[TakLayers] subscribed mission {name}: {count} CoTs");
            return count;
        }

        public async Task UnsubscribeMission(string name)
        {
            if (_marti != null) await _marti.UnsubscribeMission(name, _clientUid);
            _subscribedMissions.Remove(name);
            RemoveLayer("mission:" + name);
            PersistLayers();
        }

        async Task<int> PullMissionCots(string name)
        {
            var xml = await _marti.GetMissionCotXml(name);
            int count = 0;
            foreach (var eventXml in TakDirectClient.SplitEvents(xml))
            {
                var cot = TakDirectClient.ParseCot(eventXml);
                if (cot == null) continue;
                InjectLayerCot("mission:" + name, cot);
                count++;
            }
            return count;
        }

        async void PollMissions()
        {
            if (_marti == null || _subscribedMissions.Count == 0) return;
            foreach (var name in new List<string>(_subscribedMissions))
            {
                try
                {
                    await PullMissionCots(name);
                    _feed.NotifyChanged();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[TakLayers] mission poll {name} failed: {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        void PersistLayers()
        {
            var state = new PersistedLayers
            {
                packageHashes = new List<string>(_importedPackages),
                missionNames = new List<string>(_subscribedMissions),
            };
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(state));
            PlayerPrefs.Save();
            TakXrStateStore.Capture();
        }

        async Task RestorePersistedLayers()
        {
            var json = PlayerPrefs.GetString(PrefsKey, "");
            if (string.IsNullOrEmpty(json)) return;
            PersistedLayers state;
            try { state = JsonUtility.FromJson<PersistedLayers>(json); }
            catch { return; }
            if (state == null) return;

            foreach (var hash in state.packageHashes)
            {
                try { await ImportPackage(hash); }
                catch (Exception ex) { Debug.LogWarning($"[TakLayers] restore package {hash}: {ex.Message}"); }
            }
            foreach (var name in state.missionNames)
            {
                try { await SubscribeMission(name); }
                catch (Exception ex) { Debug.LogWarning($"[TakLayers] restore mission {name}: {ex.Message}"); }
            }
        }
    }
}
