using System;
using System.Collections;
using System.Collections.Generic;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK-style Servers panel. List rows match WinTAK / Android TAK Tracker:
    /// checkbox enables the live CoT stream, a color badge shows Connected /
    /// Connecting / Not connected / Disconnected / Error.
    /// </summary>
    public class XrServerPanel : MonoBehaviour
    {
        const float PanelW = 0.98f;
        const float PanelH = 0.84f;
        const float RowH = 0.090f;
        const int RowsPerPage = 5;

        enum Mode { List, Edit }

        class LiveRow
        {
            public string Id;
            public Renderer ToggleFill;
            public Renderer BadgeBg;
            public TextMesh Badge;
            public TextMesh Error;
        }

        AppConfig _config;
        TakDirectHub _direct;
        TakLayersService _layers;
        CotFeedClient _feed;
        Transform _cam;
        Action<string> _flash;

        Transform _root;
        TextMesh _title;
        TextMesh _summary;
        Transform _rowsRoot;
        readonly List<(Transform root, Action onClick)> _hitTargets = new List<(Transform, Action)>();
        readonly List<LiveRow> _liveRows = new List<LiveRow>();
        readonly bool[] _wasGrabbing = new bool[2];
        float _nextClickTime;
        float _nextAutoRefresh;
        bool _reconnectBusy;

        Mode _mode = Mode.List;
        int _page;
        TakServerDirectory.State _dir;
        TakServerEntry _editTarget;
        int _hostPresetIdx;
        int _cotPortIdx;
        int _martiPortIdx;
        XrKeyboardPanel _keyboard;
        XrQrScanPanel _qrScan;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrServerPanel Create()
        {
            var go = new GameObject("XrServerPanel");
            return go.AddComponent<XrServerPanel>();
        }

        public void Configure(
            AppConfig config,
            TakDirectHub direct,
            TakLayersService layers,
            CotFeedClient feed,
            Transform cam,
            Action<string> flashStatus = null)
        {
            _config = config;
            _direct = direct;
            _layers = layers;
            _feed = feed;
            _cam = cam;
            if (flashStatus != null) _flash = flashStatus;
            _dir = TakServerDirectory.LoadOrSeed(_config);
        }

        public void SetFlashStatus(Action<string> flash) => _flash = flash;

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.04f, 0.08f, 0.12f, 0.97f), 2995);

            _title = Text("Title", _root, new Vector3(-PanelW / 2f + 0.06f, PanelH / 2f - 0.048f, -0.01f),
                "SERVERS", 0.010f, new Color(0.88f, 0.95f, 1f, 1f),
                TextAnchor.MiddleLeft, TextAlignment.Left);

            _summary = Text("Summary", _root,
                new Vector3(PanelW / 2f - 0.14f, PanelH / 2f - 0.048f, -0.01f),
                "", 0.0062f, new Color(0.70f, 0.88f, 0.78f, 0.95f),
                TextAnchor.MiddleRight, TextAlignment.Right);

            _rowsRoot = new GameObject("Rows").transform;
            _rowsRoot.SetParent(_root, false);
        }

        public void Open(Transform cam)
        {
            if (cam != null) _cam = cam;
            _dir = TakServerDirectory.LoadOrSeed(_config);
            _mode = Mode.List;
            _page = 0;
            _editTarget = null;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildUi();
            RefreshStatus();
        }

        public void Hide()
        {
            _mode = Mode.List;
            _editTarget = null;
            _root.gameObject.SetActive(false);
        }

        void PlaceFacing()
        {
            if (_cam == null) return;
            var camPos = _cam.position;
            var flat = _cam.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            _root.position = camPos + flat * 1.7f + Vector3.up * 0.05f;
            _root.rotation = XrUiFacing.RotationFacingUser(_root.position, camPos);
        }

        void RebuildUi()
        {
            foreach (Transform child in _rowsRoot) Destroy(child.gameObject);
            _hitTargets.Clear();
            _liveRows.Clear();

            AddButton("Close", new Vector3(PanelW / 2f - 0.05f, PanelH / 2f - 0.05f, -0.01f),
                new Vector2(0.07f, 0.05f), "X", false, Hide);

            if (_mode == Mode.Edit)
                RebuildEditUi();
            else
                RebuildListUi();
        }

        void RebuildListUi()
        {
            _title.text = "SERVERS";

            var servers = _dir?.ServerList ?? new List<TakServerEntry>();
            string activeId = _dir?.activeId;
            int total = servers.Count;
            int maxPage = Mathf.Max(0, (total - 1) / RowsPerPage);
            _page = Mathf.Clamp(_page, 0, maxPage);
            int start = _page * RowsPerPage;

            float y = PanelH / 2f - 0.125f;
            for (int i = start; i < Mathf.Min(start + RowsPerPage, total); i++)
            {
                var entry = servers[i];
                if (entry == null) continue;
                AddServerRow(entry, entry.id == activeId, y);
                y -= RowH;
            }

            if (total == 0)
            {
                Text("Empty", _rowsRoot, new Vector3(0f, 0.10f, -0.01f),
                    "No servers yet — Add, type a host, or scan a QR", 0.0064f,
                    new Color(0.72f, 0.82f, 0.90f, 0.9f));
            }

            float by = -PanelH / 2f + 0.085f;
            if (maxPage > 0)
            {
                AddButton("Prev", new Vector3(-0.42f, by + 0.058f, -0.01f), new Vector2(0.10f, 0.040f),
                    "<", false, () => { _page = Mathf.Max(0, _page - 1); RebuildUi(); });
                Text("Page", _rowsRoot, new Vector3(-0.28f, by + 0.058f, -0.01f),
                    $"{_page + 1}/{maxPage + 1}", 0.0058f, new Color(0.80f, 0.88f, 0.95f, 0.95f));
                AddButton("Next", new Vector3(-0.14f, by + 0.058f, -0.01f), new Vector2(0.10f, 0.040f),
                    ">", false, () => { _page = Mathf.Min(maxPage, _page + 1); RebuildUi(); });
            }

            AddButton("Add", new Vector3(-0.32f, by, -0.01f), new Vector2(0.20f, 0.048f),
                "Add", false, OnAddServer);
            AddButton("Type", new Vector3(-0.06f, by, -0.01f), new Vector2(0.24f, 0.048f),
                "Type host", false, OnTypeHost);
            AddButton("Qr", new Vector3(0.24f, by, -0.01f), new Vector2(0.22f, 0.048f),
                "Scan QR", false, OnScanQr);

            RefreshStatus();
        }

        void RebuildEditUi()
        {
            if (_editTarget == null)
            {
                _mode = Mode.List;
                RebuildUi();
                return;
            }
            _title.text = "EDIT SERVER";
            if (_summary != null) _summary.text = Trunc(_editTarget.host ?? "", 22);

            var hosts = TakServerDirectory.HostChoices(_dir);
            _hostPresetIdx = IndexOfIgnoreCase(hosts, _editTarget.host);
            if (_hostPresetIdx < 0) { hosts.Insert(0, _editTarget.host ?? ""); _hostPresetIdx = 0; }

            _cotPortIdx = IndexOfInt(TakServerDirectory.CotPortPresets, _editTarget.cotPort);
            if (_cotPortIdx < 0) _cotPortIdx = 0;
            _martiPortIdx = IndexOfInt(TakServerDirectory.MartiPortPresets, _editTarget.martiPort);
            if (_martiPortIdx < 0) _martiPortIdx = 0;

            float y = 0.22f;
            LeftLabel("NameLbl", new Vector3(-PanelW / 2f + 0.06f, y, -0.01f),
                Trunc(_editTarget.displayName ?? "Server", 28), 0.008f);
            y -= 0.08f;

            // Host cycle
            LeftLabel("HostLbl", new Vector3(-PanelW / 2f + 0.06f, y, -0.01f),
                "Host  " + Trunc(_editTarget.host ?? "?", 28), 0.0072f);
            AddButton("TypeHost", new Vector3(0.22f, y, -0.01f), new Vector2(0.22f, 0.048f),
                "Type", false, OnTypeEditHost);
            y -= 0.075f;

            // CoT port
            LeftLabel("CotLbl", new Vector3(-PanelW / 2f + 0.06f, y, -0.01f),
                $"CoT port  {_editTarget.cotPort}", 0.0072f);
            AddButton("CotPrev", new Vector3(0.22f, y, -0.01f), new Vector2(0.08f, 0.048f),
                "<", false, () => CycleCotPort(-1));
            AddButton("CotNext", new Vector3(0.34f, y, -0.01f), new Vector2(0.08f, 0.048f),
                ">", false, () => CycleCotPort(+1));
            y -= 0.075f;

            // Marti port
            LeftLabel("MartiLbl", new Vector3(-PanelW / 2f + 0.06f, y, -0.01f),
                $"Marti port  {_editTarget.martiPort}", 0.0072f);
            AddButton("MartiPrev", new Vector3(0.22f, y, -0.01f), new Vector2(0.08f, 0.048f),
                "<", false, () => CycleMartiPort(-1));
            AddButton("MartiNext", new Vector3(0.34f, y, -0.01f), new Vector2(0.08f, 0.048f),
                ">", false, () => CycleMartiPort(+1));
            y -= 0.09f;

            LeftLabel("Hint", new Vector3(-PanelW / 2f + 0.06f, y, -0.01f),
                "Checkbox on the list turns the stream on or off", 0.0054f,
                new Color(0.65f, 0.78f, 0.9f, 0.85f));
            y -= 0.06f;

            string certStatus = TakCertStore.StatusLabel(_editTarget.id, _config);
            LeftLabel("CertLbl", new Vector3(-PanelW / 2f + 0.06f, y, -0.01f),
                "Cert  " + Trunc(certStatus, 36), 0.0064f,
                new Color(0.75f, 0.9f, 0.8f, 0.95f));

            float by = -PanelH / 2f + 0.085f;
            AddButton("Back", new Vector3(-0.38f, by, -0.01f), new Vector2(0.16f, 0.048f),
                "Back", false, () => { _mode = Mode.List; _editTarget = null; RebuildUi(); });
            AddButton("Marti", new Vector3(-0.16f, by, -0.01f), new Vector2(0.22f, 0.048f),
                "Use Marti", false, () => OnSetPrimary(_editTarget.id));
            AddButton("ImportCert", new Vector3(0.10f, by, -0.01f), new Vector2(0.22f, 0.048f),
                "Import cert", false, OnImportCert);
            AddButton("Done", new Vector3(0.36f, by, -0.01f), new Vector2(0.18f, 0.048f),
                "Done", true, OnEditDone);
        }

        void AddServerRow(TakServerEntry entry, bool marti, float y)
        {
            var row = new GameObject("Row_" + entry.id).transform;
            row.SetParent(_rowsRoot, false);
            row.localPosition = new Vector3(0f, y, 0f);

            Quad("Bg", row, Vector3.zero, new Vector2(PanelW - 0.08f, RowH - 0.008f),
                new Color(0.08f, 0.14f, 0.20f, 0.96f), 3000);

            string id = entry.id;
            bool want = entry.wantConnected;

            // WinTAK-style Connect checkbox (left).
            var toggle = new GameObject("Toggle").transform;
            toggle.SetParent(row, false);
            toggle.localPosition = new Vector3(-PanelW / 2f + 0.085f, 0.008f, -0.006f);
            Quad("Box", toggle, Vector3.zero, new Vector2(0.046f, 0.046f),
                new Color(0.16f, 0.24f, 0.32f, 0.98f), 3006);
            var fillR = Quad("Fill", toggle, new Vector3(0f, 0f, -0.002f), new Vector2(0.030f, 0.030f),
                want ? new Color(0.22f, 0.72f, 0.42f, 1f) : new Color(0.10f, 0.14f, 0.18f, 0.9f), 3007);
            var tcol = toggle.gameObject.AddComponent<BoxCollider>();
            tcol.size = new Vector3(0.07f, 0.07f, 0.03f);
            tcol.isTrigger = true;
            _hitTargets.Add((toggle, () => OnToggleConnect(id)));

            string host = string.IsNullOrEmpty(entry.host) ? (entry.displayName ?? "?") : entry.host;
            string title = Trunc(host, 26);
            int port = entry.cotPort > 0 ? entry.cotPort : 8089;
            string sub = $"SSL:{port}";
            if (marti) sub += "  ·  Marti";
            if (!string.IsNullOrEmpty(entry.displayName) &&
                !string.Equals(entry.displayName, entry.host, StringComparison.OrdinalIgnoreCase))
                sub = Trunc(entry.displayName, 18) + "  ·  " + sub;

            Text("Host", row, new Vector3(-PanelW / 2f + 0.13f, 0.016f, -0.008f),
                title, 0.0068f, Color.white, TextAnchor.MiddleLeft, TextAlignment.Left);
            Text("Sub", row, new Vector3(-PanelW / 2f + 0.13f, -0.012f, -0.008f),
                sub, 0.0048f, new Color(0.70f, 0.82f, 0.92f, 0.92f),
                TextAnchor.MiddleLeft, TextAlignment.Left);

            var badgeGo = new GameObject("Badge").transform;
            badgeGo.SetParent(row, false);
            badgeGo.localPosition = new Vector3(PanelW / 2f - 0.22f, 0.014f, -0.006f);
            var badgeBg = Quad("BadgeBg", badgeGo, Vector3.zero, new Vector2(0.22f, 0.032f),
                new Color(0.18f, 0.20f, 0.22f, 0.97f), 3005);
            var badgeTm = Text("BadgeLabel", badgeGo, new Vector3(0f, 0f, -0.004f),
                "Disconnected", 0.0048f, Color.white);

            var errTm = Text("Err", row, new Vector3(-PanelW / 2f + 0.13f, -0.032f, -0.008f),
                "", 0.0044f, new Color(0.95f, 0.45f, 0.42f, 0.98f),
                TextAnchor.MiddleLeft, TextAlignment.Left);

            // Tap the error line to flash the full message (row truncates).
            var errHit = new GameObject("ErrHit").transform;
            errHit.SetParent(row, false);
            errHit.localPosition = new Vector3(-0.04f, -0.032f, 0f);
            var ecol = errHit.gameObject.AddComponent<BoxCollider>();
            ecol.size = new Vector3(0.70f, 0.028f, 0.03f);
            ecol.isTrigger = true;
            _hitTargets.Add((errHit, () =>
            {
                string full = _direct != null ? _direct.ServerError(id) : null;
                if (string.IsNullOrEmpty(full)) full = TakConnectionLog.LastError;
                if (!string.IsNullOrEmpty(full)) _flash?.Invoke(full);
            }));

            var live = new LiveRow
            {
                Id = id,
                ToggleFill = fillR,
                BadgeBg = badgeBg,
                Badge = badgeTm,
                Error = errTm,
            };
            _liveRows.Add(live);
            ApplyRowStatus(live, entry);

            var nameHit = new GameObject("NameHit").transform;
            nameHit.SetParent(row, false);
            nameHit.localPosition = new Vector3(-0.04f, 0.004f, 0f);
            var ncol = nameHit.gameObject.AddComponent<BoxCollider>();
            ncol.size = new Vector3(0.52f, RowH - 0.02f, 0.03f);
            ncol.isTrigger = true;
            _hitTargets.Add((nameHit, () =>
            {
                _dir = TakServerDirectory.LoadOrSeed(_config);
                TakServerEntry found = null;
                if (_dir?.servers != null)
                {
                    foreach (var s in _dir.servers)
                        if (s != null && s.id == id) { found = s; break; }
                }
                if (found != null) OpenEdit(found);
            }));

            var del = new GameObject("Del").transform;
            del.SetParent(row, false);
            del.localPosition = new Vector3(PanelW / 2f - 0.07f, 0.0f, -0.006f);
            Quad("DelBg", del, Vector3.zero, new Vector2(0.055f, 0.040f),
                new Color(0.32f, 0.14f, 0.14f, 0.95f), 3005);
            Text("DelLabel", del, new Vector3(0f, 0f, -0.004f), "×", 0.008f,
                new Color(1f, 0.72f, 0.70f, 1f));
            var dcol = del.gameObject.AddComponent<BoxCollider>();
            dcol.size = new Vector3(0.07f, 0.05f, 0.03f);
            dcol.isTrigger = true;
            _hitTargets.Add((del, () => OnDeleteServer(id)));
        }

        void ApplyRowStatus(LiveRow live, TakServerEntry entry)
        {
            if (live == null) return;
            bool want = entry != null && entry.wantConnected;
            string raw = _direct != null ? _direct.ServerState(live.Id) : "off";
            string err = _direct != null ? _direct.ServerError(live.Id) : null;
            ClassifyStatus(raw, want, err, out var label, out var bg, out var fg);

            if (live.ToggleFill != null && live.ToggleFill.sharedMaterial != null)
                live.ToggleFill.sharedMaterial.SetColor("_Color",
                    want ? new Color(0.22f, 0.72f, 0.42f, 1f) : new Color(0.10f, 0.14f, 0.18f, 0.9f));
            if (live.BadgeBg != null && live.BadgeBg.sharedMaterial != null)
                live.BadgeBg.sharedMaterial.SetColor("_Color", bg);
            if (live.Badge != null)
            {
                live.Badge.text = label;
                live.Badge.color = fg;
            }
            if (live.Error != null)
            {
                bool showErr = want && !string.IsNullOrEmpty(err) &&
                               !string.Equals(raw, "connected", StringComparison.OrdinalIgnoreCase);
                // Keep timeout text visible even while the client is still retrying.
                if (string.Equals(raw, "connecting", StringComparison.OrdinalIgnoreCase) &&
                    (err == null || err.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) < 0))
                    showErr = false;
                live.Error.text = showErr ? Trunc(err, 56) : "";
            }
        }

        static void ClassifyStatus(string raw, bool want, string error,
            out string label, out Color bg, out Color fg)
        {
            string s = (raw ?? "off").ToLowerInvariant();
            if (s == "connected")
            {
                label = "Connected";
                bg = new Color(0.12f, 0.32f, 0.18f, 0.97f);
                fg = new Color(0.65f, 0.92f, 0.70f, 1f);
                return;
            }
            if (s == "connecting" || s == "reconnecting" || s == "loading-cert")
            {
                // Prefer showing Error badge if we already have a timeout while retrying.
                if (!string.IsNullOrEmpty(error) &&
                    error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    label = "Timeout";
                    bg = new Color(0.38f, 0.14f, 0.14f, 0.97f);
                    fg = new Color(0.95f, 0.62f, 0.60f, 1f);
                    return;
                }
                label = "Connecting";
                bg = new Color(0.10f, 0.22f, 0.38f, 0.97f);
                fg = new Color(0.56f, 0.79f, 0.98f, 1f);
                return;
            }
            if (s == "error" || s == "cert-missing")
            {
                label = "Error";
                bg = new Color(0.38f, 0.14f, 0.14f, 0.97f);
                fg = new Color(0.95f, 0.62f, 0.60f, 1f);
                return;
            }
            if (want)
            {
                label = "Not connected";
                bg = new Color(0.38f, 0.26f, 0.10f, 0.97f);
                fg = new Color(1f, 0.80f, 0.50f, 1f);
                return;
            }
            label = "Disconnected";
            bg = new Color(0.18f, 0.20f, 0.22f, 0.97f);
            fg = new Color(0.78f, 0.80f, 0.82f, 1f);
        }

        string BuildSummary()
        {
            int enabled = 0, live = _direct != null ? _direct.ConnectedCount : 0;
            var list = _dir?.ServerList;
            if (list != null)
            {
                foreach (var s in list)
                    if (s != null && s.wantConnected) enabled++;
            }
            int cots = _feed != null ? _feed.Cots.Count : 0;
            string head;
            if (enabled == 0) head = "None";
            else if (live == 0) head = "Not connected";
            else if (live == enabled) head = enabled == 1 ? "Connected" : $"{live}/{enabled} up";
            else head = $"{live}/{enabled} up";
            string err = TakConnectionLog.LastError;
            if (!string.IsNullOrEmpty(err) && live < enabled)
                return Trunc(head + "  ·  " + err, 48);
            return cots > 0 ? head + "  ·  " + cots + " CoTs" : head;
        }

        void RefreshStatus()
        {
            if (_summary != null)
                _summary.text = _reconnectBusy ? "Connecting…" : BuildSummary();

            if (_mode != Mode.List || _liveRows.Count == 0) return;
            var list = _dir?.ServerList;
            foreach (var live in _liveRows)
            {
                if (live == null) continue;
                TakServerEntry entry = null;
                if (list != null)
                {
                    foreach (var s in list)
                        if (s != null && s.id == live.Id) { entry = s; break; }
                }
                ApplyRowStatus(live, entry);
            }
        }

        void OnToggleConnect(string id)
        {
            if (_reconnectBusy || string.IsNullOrEmpty(id)) return;
            _dir = TakServerDirectory.LoadOrSeed(_config);
            TakServerEntry entry = null;
            if (_dir?.servers != null)
            {
                foreach (var s in _dir.servers)
                    if (s != null && s.id == id) { entry = s; break; }
            }
            bool on = entry != null && entry.wantConnected;
            bool live = _direct != null && _direct.IsServerSession(id);
            if (on || live)
                StartCoroutine(DisconnectOneCo(id));
            else
                StartCoroutine(ConnectOneCo(id, "Connecting to " + (entry?.host ?? "?") + "…"));
        }

        void OnAddServer()
        {
            _dir = TakServerDirectory.LoadOrSeed(_config);
            var created = TakServerDirectory.AddClone(_dir, TakServerDirectory.GetActive(_dir));
            if (created == null) return;
            _flash?.Invoke("Added " + created.displayName);
            OpenEdit(created);
        }

        void OnEditActive()
        {
            _dir = TakServerDirectory.LoadOrSeed(_config);
            var active = TakServerDirectory.GetActive(_dir);
            if (active == null)
            {
                _flash?.Invoke("No server to edit");
                return;
            }
            OpenEdit(active);
        }

        void OpenEdit(TakServerEntry entry)
        {
            _editTarget = entry;
            _mode = Mode.Edit;
            RebuildUi();
        }

        void OnImportConfigIntoEdit()
        {
            if (_editTarget == null || _config == null) return;
            _editTarget.host = _config.takHost;
            _editTarget.cotPort = _config.takPort > 0 ? _config.takPort : 8089;
            _editTarget.martiPort = _config.takMartiPort > 0 ? _config.takMartiPort : 8443;
            if (string.IsNullOrEmpty(_editTarget.displayName))
                _editTarget.displayName = _editTarget.host;
            TakServerDirectory.UpdateEntry(_dir, _editTarget);
            _flash?.Invoke("Imported AppConfig host");
            RebuildUi();
        }

        void OnImportCert()
        {
            if (_editTarget == null || _config == null) return;
            StartCoroutine(ImportCertCo(_editTarget.id));
        }

        IEnumerator ImportCertCo(string serverId)
        {
            // Prefer a staged file at persistentDataPath/tak-certs/pending.p12,
            // else bind a copy of the StreamingAssets default as the server cert.
            string pending = System.IO.Path.Combine(TakCertStore.CertsDir, "pending.p12");
            byte[] bytes = null;
            string pwd = _config.takClientP12Password;
            if (System.IO.File.Exists(pending))
            {
                bytes = System.IO.File.ReadAllBytes(pending);
                _flash?.Invoke("Importing pending.p12…");
            }
            else
            {
                yield return TakCertStore.LoadP12Routine(_config, null, (b, p) =>
                {
                    bytes = b;
                    pwd = p;
                });
            }
            if (bytes == null || bytes.Length == 0)
            {
                _flash?.Invoke("No P12 — place pending.p12 in tak-certs/");
                yield break;
            }
            TakCertStore.ImportP12(serverId, bytes, pwd);
            if (_editTarget != null)
            {
                _editTarget.certNote = "imported";
                TakServerDirectory.UpdateEntry(_dir, _editTarget);
            }
            _flash?.Invoke("Cert imported for server");
            if (_dir != null && serverId == _dir.activeId && !_reconnectBusy)
                StartCoroutine(ReconnectCo(flash: true));
            RebuildUi();
        }

        void OnUseDefaultCert()
        {
            if (_editTarget == null) return;
            TakCertStore.UseDefault(_editTarget.id, _config != null ? _config.takClientP12Password : "");
            _editTarget.certNote = "default";
            TakServerDirectory.UpdateEntry(_dir, _editTarget);
            _flash?.Invoke("Using default StreamingAssets P12");
            if (_dir != null && _editTarget.id == _dir.activeId && !_reconnectBusy)
                StartCoroutine(ReconnectCo(flash: true));
            RebuildUi();
        }

        void OnEditDone()
        {
            if (_editTarget != null)
            {
                if (string.IsNullOrEmpty(_editTarget.displayName))
                    _editTarget.displayName = _editTarget.host;
                TakServerDirectory.UpdateEntry(_dir, _editTarget);
                string savedId = _editTarget.id;
                bool reconnect = _dir != null && savedId == _dir.activeId && _editTarget.wantConnected;
                _mode = Mode.List;
                _editTarget = null;
                RebuildUi();
                if (reconnect && !_reconnectBusy)
                    StartCoroutine(ConnectOneCo(savedId, "Saved · Connecting…"));
                return;
            }
            _mode = Mode.List;
            _editTarget = null;
            RebuildUi();
            RefreshStatus();
        }

        void CycleHost(List<string> hosts, int delta)
        {
            if (_editTarget == null || hosts == null || hosts.Count == 0) return;
            _hostPresetIdx = ((_hostPresetIdx + delta) % hosts.Count + hosts.Count) % hosts.Count;
            _editTarget.host = hosts[_hostPresetIdx];
            TakServerDirectory.UpdateEntry(_dir, _editTarget);
            RebuildUi();
        }

        void CycleCotPort(int delta)
        {
            if (_editTarget == null) return;
            var presets = TakServerDirectory.CotPortPresets;
            _cotPortIdx = ((_cotPortIdx + delta) % presets.Length + presets.Length) % presets.Length;
            _editTarget.cotPort = presets[_cotPortIdx];
            TakServerDirectory.UpdateEntry(_dir, _editTarget);
            RebuildUi();
        }

        void CycleMartiPort(int delta)
        {
            if (_editTarget == null) return;
            var presets = TakServerDirectory.MartiPortPresets;
            _martiPortIdx = ((_martiPortIdx + delta) % presets.Length + presets.Length) % presets.Length;
            _editTarget.martiPort = presets[_martiPortIdx];
            TakServerDirectory.UpdateEntry(_dir, _editTarget);
            RebuildUi();
        }

        void OnSelectServer(string id) => OnToggleConnect(id);

        void OnDisconnectServer(string id)
        {
            if (_reconnectBusy || string.IsNullOrEmpty(id)) return;
            StartCoroutine(DisconnectOneCo(id));
        }

        void OnSetPrimary(string id)
        {
            _dir = TakServerDirectory.LoadOrSeed(_config);
            if (!TakServerDirectory.SetActive(_dir, id)) return;
            var next = TakServerDirectory.GetActive(_dir);
            TakServerDirectory.ApplyToConfig(_config, next);
            _layers?.RebindMartiHost();
            _flash?.Invoke("Primary: " + (next?.host ?? "?") + " (channels follow this)");
            RebuildUi();
        }

        void OnTypeHost()
        {
            if (_keyboard == null)
                _keyboard = XrKeyboardPanel.Create();
            _keyboard.Open(_cam, "SERVER HOST", "", host =>
            {
                if (string.IsNullOrWhiteSpace(host)) return;
                _dir = TakServerDirectory.LoadOrSeed(_config);
                var created = TakServerDirectory.AddFromHost(_dir, host.Trim());
                if (created == null) return;
                _flash?.Invoke("Added " + created.host);
                OpenEdit(created);
            }, maxLen: 64);
        }

        void OnTypeEditHost()
        {
            if (_editTarget == null) return;
            if (_keyboard == null)
                _keyboard = XrKeyboardPanel.Create();
            _keyboard.Open(_cam, "SERVER HOST", _editTarget.host ?? "", host =>
            {
                if (string.IsNullOrWhiteSpace(host) || _editTarget == null) return;
                var h = host.Trim();
                int colon = h.LastIndexOf(':');
                if (colon > 0 && colon < h.Length - 1 &&
                    int.TryParse(h.Substring(colon + 1), out var p) && p > 0 && p < 65536)
                {
                    _editTarget.cotPort = p;
                    h = h.Substring(0, colon);
                }
                _editTarget.host = h;
                if (string.IsNullOrEmpty(_editTarget.displayName) ||
                    _editTarget.displayName.StartsWith("Server ", StringComparison.Ordinal))
                    _editTarget.displayName = h;
                TakServerDirectory.UpdateEntry(_dir, _editTarget);
                RebuildUi();
            }, maxLen: 64);
        }

        void OnScanQr()
        {
            if (_qrScan == null)
                _qrScan = XrQrScanPanel.Create();
            _qrScan.Open(_cam, raw =>
            {
                if (string.IsNullOrEmpty(raw))
                {
                    _flash?.Invoke("QR cancelled — type host instead");
                    return;
                }
                ApplyQrPayload(raw);
            });
        }

        void ApplyQrPayload(string raw)
        {
            var parsed = TakQrParser.Parse(raw);
            if (parsed == null)
            {
                _flash?.Invoke("QR not a TAK server config");
                return;
            }
            _dir = TakServerDirectory.LoadOrSeed(_config);
            var created = TakServerDirectory.AddFromHost(
                _dir, parsed.Host, parsed.Port, parsed.MartiPort, parsed.Name);
            if (created == null) return;
            created.enrollPort = parsed.EnrollPort > 0 ? parsed.EnrollPort : 8446;
            TakServerDirectory.UpdateEntry(_dir, created);
            _flash?.Invoke("QR: " + created.host + " (using current cert)");
            if (!_reconnectBusy)
                StartCoroutine(ConnectOneCo(created.id, "Connecting to " + created.host + "…"));
        }

        IEnumerator ConnectOneCo(string id, string msg)
        {
            _reconnectBusy = true;
            if (_summary != null) _summary.text = msg ?? "Connecting…";
            _flash?.Invoke(msg ?? "Connecting…");
            if (_direct != null)
                yield return _direct.ConnectServerRoutine(id);
            _layers?.RebindMartiHost();

            // Wait for TLS outcome so QR / Connect shows a real error, not a brief flash.
            float deadline = Time.unscaledTime + 18f;
            while (Time.unscaledTime < deadline && _direct != null)
            {
                string st = _direct.ServerState(id);
                if (_direct.IsServerConnected(id))
                {
                    _flash?.Invoke("Connected");
                    break;
                }
                if (st == "error" || st == "cert-missing")
                {
                    string err = _direct.ServerError(id) ?? TakConnectionLog.LastError ?? st;
                    _flash?.Invoke(Trunc(err, 72));
                    break;
                }
                string pending = _direct.ServerError(id);
                if (!string.IsNullOrEmpty(pending) &&
                    pending.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // First timeout often already set LastError while still "connecting".
                    _flash?.Invoke(Trunc(pending, 72));
                    break;
                }
                yield return new WaitForSecondsRealtime(0.35f);
            }

            if (_direct != null && !_direct.IsServerConnected(id))
            {
                string err = _direct.ServerError(id) ?? TakConnectionLog.LastError;
                if (!string.IsNullOrEmpty(err))
                    _flash?.Invoke(Trunc(err, 72));
            }

            _reconnectBusy = false;
            RefreshStatus();
            if (IsVisible && _mode == Mode.List) RebuildUi();
        }

        IEnumerator DisconnectOneCo(string id)
        {
            _reconnectBusy = true;
            _flash?.Invoke("Disconnecting…");
            if (_direct != null)
                yield return _direct.DisconnectServerRoutine(id);
            _reconnectBusy = false;
            RefreshStatus();
            if (IsVisible && _mode == Mode.List) RebuildUi();
        }

        void OnDeleteServer(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            StartCoroutine(DeleteCo(id));
        }

        IEnumerator DeleteCo(string id)
        {
            _reconnectBusy = true;
            if (_direct != null && _direct.IsServerSession(id))
                yield return _direct.DisconnectServerRoutine(id);
            _dir = TakServerDirectory.LoadOrSeed(_config);
            if (!TakServerDirectory.Delete(_dir, id))
                _flash?.Invoke("Couldn't remove server");
            else
            {
                var next = TakServerDirectory.GetActive(_dir);
                if (next != null) TakServerDirectory.ApplyToConfig(_config, next);
                _flash?.Invoke("Server removed");
            }
            _reconnectBusy = false;
            RebuildUi();
            RefreshStatus();
        }

        IEnumerator ReconnectCo(bool flash)
        {
            _reconnectBusy = true;
            if (_summary != null) _summary.text = "Reconnecting…";
            if (flash) _flash?.Invoke("Reconnecting…");

            var active = TakServerDirectory.GetActive(TakServerDirectory.LoadOrSeed(_config));
            TakServerDirectory.ApplyToConfig(_config, active);
            _layers?.RebindMartiHost();

            if (_direct != null)
                yield return _direct.RestartClientRoutine();
            else
                yield return new WaitForSecondsRealtime(0.6f);

            yield return new WaitForSecondsRealtime(0.35f);
            _reconnectBusy = false;
            RefreshStatus();
            if (IsVisible && _mode == Mode.List) RebuildUi();
        }

        void Update()
        {
            if (!IsVisible) return;

            if (_mode == Mode.List && Time.unscaledTime >= _nextAutoRefresh)
            {
                _nextAutoRefresh = Time.unscaledTime + 0.8f;
                if (_mode == Mode.List && !_reconnectBusy) RefreshStatus();
            }

            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;
                if (!rising || Time.unscaledTime < _nextClickTime) continue;

                var hits = Physics.RaycastAll(origin, fwd, 8f, ~0, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var hit in hits)
                {
                    foreach (var (root, onClick) in _hitTargets)
                    {
                        if (root == null) continue;
                        if (hit.transform == root || hit.transform.IsChildOf(root))
                        {
                            onClick?.Invoke();
                            _nextClickTime = Time.unscaledTime + 0.35f;
                            return;
                        }
                    }
                }
            }
        }

        void LeftLabel(string name, Vector3 pos, string msg, float size, Color? color = null)
        {
            Text(name, _rowsRoot, pos, msg, size,
                color ?? new Color(0.88f, 0.94f, 1f, 0.95f),
                TextAnchor.MiddleLeft, TextAlignment.Left);
        }

        void AddButton(string name, Vector3 pos, Vector2 size, string label, bool highlighted, Action onClick)
        {
            var root = new GameObject("Ui_" + name).transform;
            root.SetParent(_rowsRoot, false);
            root.localPosition = pos;
            Quad("Bg", root, Vector3.zero, size,
                highlighted ? new Color(0.16f, 0.42f, 0.62f, 0.97f) : new Color(0.12f, 0.22f, 0.33f, 0.97f), 3000);
            Text("Label", root, new Vector3(0f, 0f, -0.006f), label, 0.007f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.03f);
            col.isTrigger = true;
            _hitTargets.Add((root, onClick));
        }

        static int IndexOfIgnoreCase(List<string> list, string value)
        {
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static int IndexOfInt(int[] arr, int value)
        {
            if (arr == null) return -1;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] == value) return i;
            return -1;
        }

        static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        static Renderer Quad(string name, Transform parent, Vector3 pos, Vector2 size, Color color, int queue)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            r.sharedMaterial.renderQueue = queue;
            return r;
        }

        static TextMesh Text(string name, Transform parent, Vector3 pos, string msg, float size, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center)
        {
            return XrText.Make(name, parent, pos, msg, size, color, anchor, alignment);
        }
    }
}
