using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TakXr.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Cot
{
    /// <summary>
    /// HTTPS snapshot + WebSocket live feed matching packages/frontend cotFeedClient.ts.
    /// </summary>
    public class CotFeedClient : MonoBehaviour
    {
        [SerializeField] AppConfig config;

        readonly Dictionary<string, NormalizedCot> _cots = new Dictionary<string, NormalizedCot>();
        readonly Dictionary<string, HashSet<string>> _uidServers = new Dictionary<string, HashSet<string>>();
        readonly Queue<Action> _mainThread = new Queue<Action>();
        ClientWebSocket _ws;
        CancellationTokenSource _cts;
        bool _closedByUser;
        float _reconnectDelay = 1f;

        public IReadOnlyDictionary<string, NormalizedCot> Cots => _cots;
        public event Action Changed;
        public event Action<string> ConnectionStateChanged;
        public string ConnectionState { get; private set; } = "closed";

        /// <summary>
        /// True when a TakDirectClient owns live unit CoTs. Backend snapshots then only
        /// MERGE package/mission pins (video cameras) instead of replacing the store, and
        /// the WS is skipped entirely — the app works with no backend at all.
        /// </summary>
        public bool DirectMode { get; private set; }

        public void Configure(AppConfig cfg) => config = cfg;

        public void StartFeed()
        {
            _closedByUser = false;
            StartCoroutine(Bootstrap());
            // Snapshot poll keeps CoTs fresh if WSS stalls on device.
            CancelInvoke(nameof(PollSnapshot));
            InvokeRepeating(nameof(PollSnapshot), 8f, 8f);
        }

        /// <summary>Direct-TAK mode: fully standalone. Units come from the TLS stream,
        /// packages/missions from the Marti API — the backend is never contacted.</summary>
        public void StartDirectMode()
        {
            _closedByUser = false;
            DirectMode = true;
            SetState("direct");
            CancelInvoke(nameof(PollSnapshot));
        }

        /// <summary>Leave direct mode (direct TAK unavailable) — backend becomes authoritative.</summary>
        public void ExitDirectMode()
        {
            DirectMode = false;
            CancelInvoke(nameof(PollSnapshot));
        }

        /// <summary>Upsert a CoT parsed from the direct TAK stream (main thread).</summary>
        public void UpsertDirect(NormalizedCot cot, bool notify = true)
        {
            if (cot == null || string.IsNullOrEmpty(cot.uid)) return;
            if (!string.IsNullOrEmpty(cot.sourceServerId))
            {
                if (!_uidServers.TryGetValue(cot.uid, out var set) || set == null)
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _uidServers[cot.uid] = set;
                }
                set.Add(cot.sourceServerId);
            }
            _cots[cot.uid] = cot;
            if (notify) Changed?.Invoke();
        }

        public void NotifyChanged() => Changed?.Invoke();

        public void RemoveByUid(string uid, bool notify = true)
        {
            if (string.IsNullOrEmpty(uid)) return;
            _uidServers.Remove(uid);
            if (_cots.Remove(uid) && notify) Changed?.Invoke();
        }

        /// <summary>
        /// Drop live tracks that only came from <paramref name="serverId"/>.
        /// UIDs still sourced by another connected server are kept.
        /// Package/mission pins are never removed here.
        /// </summary>
        public void RemoveByServer(string serverId, bool notify = true)
        {
            if (string.IsNullOrEmpty(serverId)) return;
            var kill = new List<string>();
            foreach (var kv in _cots)
            {
                var c = kv.Value;
                if (c?.group != null &&
                    (c.group.StartsWith("package:", StringComparison.OrdinalIgnoreCase) ||
                     c.group.StartsWith("mission:", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!_uidServers.TryGetValue(kv.Key, out var set) || set == null)
                {
                    if (c != null && c.sourceServerId == serverId) kill.Add(kv.Key);
                    continue;
                }
                set.Remove(serverId);
                if (set.Count == 0) kill.Add(kv.Key);
            }
            if (kill.Count == 0) return;
            foreach (var uid in kill)
            {
                _cots.Remove(uid);
                _uidServers.Remove(uid);
            }
            if (notify) Changed?.Invoke();
        }

        /// <summary>Drop direct-fed CoTs whose stale time has passed (plus grace).
        /// Package/mission pins are exempt — they are reference data, not live tracks.</summary>
        public void SweepStaleDirect(double graceSeconds)
        {
            var now = DateTime.UtcNow;
            List<string> dead = null;
            foreach (var kv in _cots)
            {
                var c = kv.Value;
                if (c?.group != null &&
                    (c.group.StartsWith("package:", StringComparison.OrdinalIgnoreCase) ||
                     c.group.StartsWith("mission:", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (string.IsNullOrEmpty(c?.stale)) continue;
                if (!DateTime.TryParse(c.stale, null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var staleAt))
                    continue;
                if ((now - staleAt).TotalSeconds > graceSeconds)
                    (dead ??= new List<string>()).Add(kv.Key);
            }
            if (dead == null) return;
            foreach (var uid in dead)
            {
                _cots.Remove(uid);
                _uidServers.Remove(uid);
            }
            Changed?.Invoke();
        }

        public void RefreshSnapshot() => StartCoroutine(FetchSnapshot());

        void PollSnapshot()
        {
            if (_closedByUser) return;
            if (DirectMode || ConnectionState != "open")
                StartCoroutine(FetchSnapshot());
        }

        public void StopFeed()
        {
            _closedByUser = true;
            CancelInvoke(nameof(PollSnapshot));
            StopAllCoroutines();
            _ = CloseWsAsync();
            SetState("closed");
        }

        IEnumerator Bootstrap()
        {
            yield return FetchSnapshot();
            _ = ConnectWsLoop();
        }

        IEnumerator FetchSnapshot()
        {
            // Direct standalone: never contact the LXC backend.
            if (DirectMode && (config == null || !config.allowBackendFallback))
                yield break;
            // Note: WS loop owns ConnectionState; the snapshot poll must not stomp it.
            using var req = UnityWebRequest.Get(config.CotSnapshotUrl);
            req.timeout = 30;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[CotFeed] snapshot failed: {req.error}");
                yield break;
            }

            try
            {
                var wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var list = JsonUtility.FromJson<CotListWrapper>(wrapped);
                // Never wipe the store while DirectMode owns live units — even if a
                // stray snapshot somehow started after StartFeed.
                if (DirectMode)
                {
                    // Merge only backend-local layers (package/mission pins e.g. cameras);
                    // live units are owned by the direct TAK stream.
                    int merged = 0;
                    if (list?.items != null)
                    {
                        foreach (var c in list.items)
                        {
                            if (c == null || string.IsNullOrEmpty(c.uid) || c.group == null) continue;
                            if (!c.group.StartsWith("package:", StringComparison.OrdinalIgnoreCase) &&
                                !c.group.StartsWith("mission:", StringComparison.OrdinalIgnoreCase))
                                continue;
                            _cots[c.uid] = c;
                            merged++;
                        }
                    }
                    if (merged > 0) Debug.Log($"[CotFeed] merged {merged} package pins from backend");
                }
                else
                {
                    _cots.Clear();
                    if (list?.items != null)
                    {
                        foreach (var c in list.items)
                        {
                            if (c != null && !string.IsNullOrEmpty(c.uid))
                                _cots[c.uid] = c;
                        }
                    }
                    Debug.Log($"[CotFeed] snapshot {_cots.Count} tracks");
                }
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CotFeed] snapshot parse: {ex.Message}");
            }
        }

        async Task ConnectWsLoop()
        {
            while (!_closedByUser)
            {
                SetState("connecting");
                _cts = new CancellationTokenSource();
                _ws = new ClientWebSocket();
                try
                {
                    await _ws.ConnectAsync(new Uri(config.WsUrl), _cts.Token);
                    _reconnectDelay = 1f;
                    RunOnMain(() =>
                    {
                        SetState("open");
                        Debug.Log($"[CotFeed] ws open {config.WsUrl}");
                    });
                    await ReceiveLoop(_cts.Token);
                }
                catch (Exception ex)
                {
                    if (!_closedByUser)
                        Debug.LogWarning($"[CotFeed] ws: {ex.Message}");
                }
                finally
                {
                    await CloseWsAsync();
                    RunOnMain(() => SetState("closed"));
                }

                if (_closedByUser) break;
                await Task.Delay(TimeSpan.FromSeconds(_reconnectDelay));
                _reconnectDelay = Mathf.Min(_reconnectDelay * 1.5f, 15f);
            }
        }

        async Task ReceiveLoop(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var text = sb.ToString();
                RunOnMain(() => HandleMessage(text));
            }
        }

        void HandleMessage(string json)
        {
            if (json.Contains("\"type\":\"snapshot\""))
            {
                StartCoroutine(FetchSnapshot());
                return;
            }

            if (json.Contains("\"type\":\"remove\""))
            {
                var uid = ExtractJsonString(json, "uid");
                if (!string.IsNullOrEmpty(uid) && _cots.Remove(uid))
                    Changed?.Invoke();
                return;
            }

            var cotJson = ExtractObject(json, "cot");
            if (string.IsNullOrEmpty(cotJson)) return;
            try
            {
                var cot = JsonUtility.FromJson<NormalizedCot>(cotJson);
                if (cot == null || string.IsNullOrEmpty(cot.uid)) return;
                _cots[cot.uid] = cot;
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CotFeed] cot parse: {ex.Message}");
            }
        }

        static string ExtractJsonString(string json, string key)
        {
            var marker = $"\"{key}\":\"";
            var i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            i += marker.Length;
            var j = json.IndexOf('"', i);
            return j < 0 ? null : json.Substring(i, j - i);
        }

        static string ExtractObject(string json, string key)
        {
            var marker = $"\"{key}\":";
            var i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            i += marker.Length;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '{') return null;
            int depth = 0;
            int start = i;
            for (int p = i; p < json.Length; p++)
            {
                if (json[p] == '{') depth++;
                else if (json[p] == '}')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, p - start + 1);
                }
            }
            return null;
        }

        void SetState(string state)
        {
            if (ConnectionState == state) return;
            ConnectionState = state;
            ConnectionStateChanged?.Invoke(state);
        }

        void RunOnMain(Action a)
        {
            lock (_mainThread) _mainThread.Enqueue(a);
        }

        void Update()
        {
            lock (_mainThread)
            {
                while (_mainThread.Count > 0)
                    _mainThread.Dequeue()?.Invoke();
            }
        }

        async Task CloseWsAsync()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            if (_ws == null) return;
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* ignore */ }
            try { _ws.Dispose(); } catch { /* ignore */ }
            _ws = null;
        }

        void OnDestroy() => StopFeed();
    }
}
