using System.Collections;
using System.Collections.Generic;
using TakXr.Core;
using UnityEngine;

namespace TakXr.Cot
{
    /// <summary>
    /// Owns one <see cref="TakDirectClient"/> TLS session per connected TAK server
    /// and merges their CoTs into the shared feed. SendCot broadcasts to every
    /// live stream (self SA / drawings). Marti still follows the primary
    /// (active) directory entry via AppConfig.
    /// </summary>
    public class TakDirectHub : MonoBehaviour
    {
        AppConfig _config;
        CotFeedClient _feed;
        readonly Dictionary<string, TakDirectClient> _sessions = new Dictionary<string, TakDirectClient>();
        Transform _root;

        public string State
        {
            get
            {
                int n = 0, live = 0;
                string err = null;
                foreach (var kv in _sessions)
                {
                    n++;
                    var s = kv.Value;
                    if (s != null && s.IsConnected) live++;
                    else if (err == null && s != null && !string.IsNullOrEmpty(s.LastError))
                        err = s.LastError;
                }
                if (n == 0) return "off";
                if (live > 0) return live == n ? "connected" : "connected";
                if (err != null) return "error";
                foreach (var kv in _sessions)
                    if (kv.Value != null) return kv.Value.State;
                return "off";
            }
        }

        public bool IsConnected
        {
            get
            {
                foreach (var kv in _sessions)
                    if (kv.Value != null && kv.Value.IsConnected) return true;
                return false;
            }
        }

        public int EventsReceived
        {
            get
            {
                int n = 0;
                foreach (var kv in _sessions)
                    if (kv.Value != null) n += kv.Value.EventsReceived;
                return n;
            }
        }

        public string LastError
        {
            get
            {
                foreach (var kv in _sessions)
                    if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.LastError))
                        return kv.Value.LastError;
                return null;
            }
        }

        public int SessionCount => _sessions.Count;

        public int ConnectedCount
        {
            get
            {
                int n = 0;
                foreach (var kv in _sessions)
                    if (kv.Value != null && kv.Value.IsConnected) n++;
                return n;
            }
        }

        public byte[] P12Bytes
        {
            get
            {
                var primary = PrimarySession();
                if (primary != null && primary.P12Bytes != null && primary.P12Bytes.Length > 0)
                    return primary.P12Bytes;
                foreach (var kv in _sessions)
                    if (kv.Value != null && kv.Value.P12Bytes != null && kv.Value.P12Bytes.Length > 0)
                        return kv.Value.P12Bytes;
                return null;
            }
        }

        public string P12Password
        {
            get
            {
                var primary = PrimarySession();
                if (primary != null) return primary.P12Password;
                foreach (var kv in _sessions)
                    if (kv.Value != null) return kv.Value.P12Password;
                return _config != null ? _config.takClientP12Password : "";
            }
        }

        public void Configure(AppConfig config, CotFeedClient feed)
        {
            _config = config;
            _feed = feed;
            if (_root == null)
            {
                var go = new GameObject("TakDirectSessions");
                go.transform.SetParent(transform, false);
                _root = go.transform;
            }
        }

        public bool SendCot(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return false;
            bool any = false;
            foreach (var kv in _sessions)
            {
                if (kv.Value != null && kv.Value.SendCot(xml))
                    any = true;
            }
            return any;
        }

        public bool IsServerConnected(string serverId)
        {
            if (string.IsNullOrEmpty(serverId)) return false;
            return _sessions.TryGetValue(serverId, out var c) && c != null && c.IsConnected;
        }

        public bool IsServerSession(string serverId) =>
            !string.IsNullOrEmpty(serverId) && _sessions.ContainsKey(serverId);

        public string ServerState(string serverId)
        {
            if (string.IsNullOrEmpty(serverId) || !_sessions.TryGetValue(serverId, out var c) || c == null)
                return "off";
            return c.State ?? "off";
        }

        public int ServerEvents(string serverId)
        {
            if (string.IsNullOrEmpty(serverId) || !_sessions.TryGetValue(serverId, out var c) || c == null)
                return 0;
            return c.EventsReceived;
        }

        public string ServerError(string serverId)
        {
            if (string.IsNullOrEmpty(serverId) || !_sessions.TryGetValue(serverId, out var c) || c == null)
                return null;
            return c.LastError;
        }

        /// <summary>Boot: open a session for every directory entry with wantConnected.</summary>
        public void StartClient()
        {
            if (_config == null || !_config.takDirectEnabled) return;
            StartCoroutine(StartWantedRoutine());
        }

        public IEnumerator StartWantedRoutine()
        {
            var dir = TakServerDirectory.LoadOrSeed(_config);
            var list = dir?.ServerList;
            if (list == null || list.Count == 0) yield break;
            foreach (var entry in list)
            {
                if (entry == null || !entry.wantConnected) continue;
                yield return ConnectServerRoutine(entry.id);
            }
        }

        public IEnumerator RestartClientRoutine()
        {
            StopClient();
            yield return new WaitForSecondsRealtime(0.35f);
            yield return StartWantedRoutine();
        }

        public void StopClient()
        {
            var ids = new List<string>(_sessions.Keys);
            foreach (var id in ids)
                StopSession(id, removeTracks: false);
        }

        public IEnumerator ConnectServerRoutine(string serverId)
        {
            if (string.IsNullOrEmpty(serverId) || _config == null) yield break;
            var dir = TakServerDirectory.LoadOrSeed(_config);
            TakServerEntry entry = null;
            if (dir?.servers != null)
            {
                foreach (var s in dir.servers)
                    if (s != null && s.id == serverId) { entry = s; break; }
            }
            if (entry == null) yield break;

            TakServerDirectory.SetWantConnected(dir, serverId, true);
            // Marti follows the active (primary) entry. Connecting another
            // stream must not steal channels — only claim primary when none.
            var active = TakServerDirectory.GetActive(dir);
            if (active == null || !active.wantConnected)
            {
                TakServerDirectory.SetActive(dir, serverId);
                TakServerDirectory.ApplyToConfig(_config, entry);
            }

            if (_sessions.TryGetValue(serverId, out var existing) && existing != null)
            {
                yield return existing.RestartClientRoutine();
                yield break;
            }

            var go = new GameObject("TakDirect:" + entry.host);
            go.transform.SetParent(_root != null ? _root : transform, false);
            var client = go.AddComponent<TakDirectClient>();
            client.Configure(_config, _feed);
            client.BindServer(entry.id, entry.host, entry.cotPort > 0 ? entry.cotPort : 8089);
            _sessions[serverId] = client;
            client.StartClient();
        }

        public IEnumerator DisconnectServerRoutine(string serverId)
        {
            var dir = TakServerDirectory.LoadOrSeed(_config);
            TakServerDirectory.SetWantConnected(dir, serverId, false);
            StopSession(serverId, removeTracks: true);
            yield return null;
        }

        void StopSession(string serverId, bool removeTracks)
        {
            if (string.IsNullOrEmpty(serverId)) return;
            if (_sessions.TryGetValue(serverId, out var client) && client != null)
            {
                client.StopClient();
                Destroy(client.gameObject);
            }
            _sessions.Remove(serverId);
            if (removeTracks)
                _feed?.RemoveByServer(serverId);
        }

        TakDirectClient PrimarySession()
        {
            var dir = TakServerDirectory.LoadOrSeed(_config);
            var active = TakServerDirectory.GetActive(dir);
            if (active != null && _sessions.TryGetValue(active.id, out var c) && c != null)
                return c;
            foreach (var kv in _sessions)
                if (kv.Value != null) return kv.Value;
            return null;
        }

        void OnDestroy() => StopClient();
    }
}
