using System;
using System.Collections.Generic;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Locomotion;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// XR Settings panel: callsign (typed edit), team, marker caps,
    /// icon/text size, move speed, backend fallback + snap-turn toggles.
    /// Persists via SettingsPanelRuntime / TakIdentity.
    /// </summary>
    public class XrSettingsPanel : MonoBehaviour
    {
        const float PanelW = 0.92f;
        const float PanelH = 0.88f;
        const int CallsignMaxLen = 24;

        AppConfig _config;
        SettingsPanelRuntime _settings;
        XrWorldLocomotion _loco;
        Transform _cam;
        Action<string> _flash;
        Action _onOriginHere;
        Action _onFitTracks;
        SelfPresence _selfPresence;
        /// <summary>System (OS) keyboard session, when the platform supports one.</summary>
        TouchScreenKeyboard _sysKeyboard;
        float _sysKeyboardOpenedAt;
        /// <summary>Procedural in-world keyboard, lazily created fallback.</summary>
        XrKeyboardPanel _keyboard;

        Transform _root;
        Transform _rowsRoot;
        TextMesh _title;
        readonly List<(Transform root, Action onClick)> _hitTargets = new List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        float _nextClickTime;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrSettingsPanel Create()
        {
            var go = new GameObject("XrSettingsPanel");
            return go.AddComponent<XrSettingsPanel>();
        }

        public void Configure(
            AppConfig config,
            SettingsPanelRuntime settings,
            XrWorldLocomotion loco,
            Transform cam,
            Action<string> flash = null,
            Action onOriginHere = null,
            Action onFitTracks = null,
            SelfPresence selfPresence = null)
        {
            _config = config;
            _settings = settings;
            _loco = loco;
            _cam = cam;
            if (flash != null) _flash = flash;
            _onOriginHere = onOriginHere;
            _onFitTracks = onFitTracks;
            _selfPresence = selfPresence;
        }

        public void SetFlashStatus(Action<string> flash) => _flash = flash;

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.05f, 0.10f, 0.16f, 0.96f), 2995);
            _title = Text("Title", _root, new Vector3(0f, PanelH / 2f - 0.05f, -0.01f),
                "SETTINGS", 0.011f, new Color(0.85f, 0.95f, 1f, 1f));
            _rowsRoot = new GameObject("Rows").transform;
            _rowsRoot.SetParent(_root, false);
        }

        public void Open(Transform cam)
        {
            if (cam != null) _cam = cam;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildUi();
        }

        public void Hide() => _root.gameObject.SetActive(false);

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

            AddButton("Close", new Vector3(PanelW / 2f - 0.05f, PanelH / 2f - 0.05f, -0.01f),
                new Vector2(0.07f, 0.05f), "X", false, Hide);

            var id = TakIdentity.Load();
            float y = PanelH / 2f - 0.14f;

            // Callsign is free text only (no preset "<>" cycling) — tap the value
            // or the Edit button to type one on the keyboard.
            RowLabel(ref y, "Callsign  " + Trunc(id.callsign, 16));
            AddHitRegion("CsTap", new Vector3(-0.17f, y + 0.055f, -0.01f),
                new Vector2(0.5f, 0.05f), EditCallsign);
            AddButton("CsEdit", new Vector3(0.36f, y + 0.055f, -0.01f), new Vector2(0.15f, 0.045f),
                "Edit", false, EditCallsign);

            RowLabel(ref y, "Team  " + id.team);
            AddButton("TeamPrev", new Vector3(0.22f, y + 0.055f, -0.01f), new Vector2(0.08f, 0.045f),
                "<", false, () => { TakIdentity.CycleTeam(-1); RebuildUi(); });
            AddButton("TeamNext", new Vector3(0.34f, y + 0.055f, -0.01f), new Vector2(0.08f, 0.045f),
                ">", false, () => { TakIdentity.CycleTeam(+1); RebuildUi(); });

            // Read-only: self publishes as a VR observer (sensor point), not a
            // ground unit — see TakIdentity.ObserverCotType.
            RowLabel(ref y, "Self type  " + (id.cotType == TakIdentity.ObserverCotType
                ? TakIdentity.ObserverTypeLabel : id.cotType));

            float iconScale = _settings != null ? _settings.IconScaleMultiplier : 1f;
            float textScale = _settings != null ? _settings.TextScaleMultiplier : 1f;
            float spd = _settings != null ? _settings.MoveSpeedMultiplier : 1f;
            bool fallback = _config != null && _config.allowBackendFallback;
            bool snap = _settings != null && _settings.SnapTurnEnabled;

            RowCycle(ref y, $"Icon size  x{iconScale:0.##}", () =>
            {
                _settings?.CycleIconScale();
                CotMarkerView.ScaleMultiplier = _settings != null ? _settings.IconScaleMultiplier : 1f;
                RebuildUi();
            });
            RowCycle(ref y, $"Text size  x{textScale:0.##}", () =>
            {
                _settings?.CycleTextScale();
                CotMarkerView.LabelScaleMultiplier = _settings != null ? _settings.TextScaleMultiplier : 1f;
                RebuildUi();
            });
            RowCycle(ref y, $"Move speed  x{spd:0.#}", () =>
            {
                _settings?.CycleMoveSpeed();
                _loco?.SetSpeedMultiplier(_settings != null ? _settings.MoveSpeedMultiplier : 1f);
                RebuildUi();
            });
            RowCycle(ref y, $"Backend fallback  {(fallback ? "ON" : "OFF")}", () =>
            {
                if (_config != null)
                {
                    _config.allowBackendFallback = !_config.allowBackendFallback;
                    _settings?.Save();
                }
                RebuildUi();
            });
            RowCycle(ref y, $"Snap turn  {(snap ? "ON" : "OFF")}", () =>
            {
                _settings?.ToggleSnapTurn();
                _loco?.SetSnapTurnEnabled(_settings != null && _settings.SnapTurnEnabled);
                RebuildUi();
            });

            float by = -PanelH / 2f + 0.12f;
            AddButton("Origin", new Vector3(-0.32f, by, -0.01f), new Vector2(0.26f, 0.05f),
                "Origin here", false, () =>
                {
                    _onOriginHere?.Invoke();
                    _flash?.Invoke("Origin set to viewer");
                    Hide();
                });
            AddButton("Flatten", new Vector3(0f, by, -0.01f), new Vector2(0.26f, 0.05f),
                "Flatten", false, () =>
                {
                    _loco?.FlattenWorld();
                    _settings?.SetWorldPitch(0f);
                    _flash?.Invoke("World flattened");
                    RebuildUi();
                });
            AddButton("Fit", new Vector3(0.32f, by, -0.01f), new Vector2(0.26f, 0.05f),
                "Fit tracks", false, () =>
                {
                    _onFitTracks?.Invoke();
                    _flash?.Invoke("Fit tracks");
                    Hide();
                });
        }

        void RowLabel(ref float y, string msg)
        {
            Text("L", _rowsRoot, new Vector3(-PanelW / 2f + 0.06f, y, -0.01f),
                msg, 0.007f, new Color(0.88f, 0.94f, 1f, 0.95f),
                TextAnchor.MiddleLeft, TextAlignment.Left);
            y -= 0.07f;
        }

        void RowCycle(ref float y, string msg, Action onClick)
        {
            LeftLabel(msg, new Vector3(-PanelW / 2f + 0.06f, y, -0.01f));
            AddButton("Cyc" + y, new Vector3(0.28f, y, -0.01f), new Vector2(0.22f, 0.045f),
                "Cycle", false, onClick);
            y -= 0.065f;
        }

        void LeftLabel(string msg, Vector3 pos)
        {
            Text("Lbl", _rowsRoot, pos, msg, 0.0066f,
                new Color(0.88f, 0.94f, 1f, 0.95f),
                TextAnchor.MiddleLeft, TextAlignment.Left);
        }

        // ---------------- typed callsign edit ----------------

        /// <summary>
        /// Free-text callsign: try the OS keyboard first (overlays fine on some
        /// XR platforms), fall back to the procedural XR keyboard panel.
        /// </summary>
        void EditCallsign()
        {
            if (TouchScreenKeyboard.isSupported)
            {
                _sysKeyboard = TouchScreenKeyboard.Open(
                    TakIdentity.Callsign, TouchScreenKeyboardType.ASCIICapable,
                    false, false, false, false, "Callsign");
                if (_sysKeyboard != null)
                {
                    _sysKeyboardOpenedAt = Time.unscaledTime;
                    return; // polled in Update
                }
            }
            OpenFallbackKeyboard();
        }

        void OpenFallbackKeyboard()
        {
            if (_keyboard == null)
                _keyboard = XrKeyboardPanel.Create();
            _keyboard.Open(_cam, "CALLSIGN", TakIdentity.Callsign, CommitCallsign);
        }

        /// <summary>Poll the OS keyboard session; commit on Done, fall back to the
        /// XR panel if the system keyboard never became active.</summary>
        void PollSystemKeyboard()
        {
            if (_sysKeyboard == null) return;
            switch (_sysKeyboard.status)
            {
                case TouchScreenKeyboard.Status.Done:
                    var text = _sysKeyboard.text;
                    _sysKeyboard = null;
                    CommitCallsign(text);
                    break;
                case TouchScreenKeyboard.Status.Canceled:
                case TouchScreenKeyboard.Status.LostFocus:
                    _sysKeyboard = null;
                    break;
                case TouchScreenKeyboard.Status.Visible:
                    // Reported supported but never actually showed (seen on some XR
                    // runtimes) — after a grace period switch to the in-world panel.
                    if (!_sysKeyboard.active && !TouchScreenKeyboard.visible
                        && Time.unscaledTime - _sysKeyboardOpenedAt > 1.5f)
                    {
                        _sysKeyboard = null;
                        OpenFallbackKeyboard();
                    }
                    break;
            }
        }

        void CommitCallsign(string raw)
        {
            var v = (raw ?? "").Trim();
            if (v.Length > CallsignMaxLen) v = v.Substring(0, CallsignMaxLen);
            if (string.IsNullOrEmpty(v)) return;
            TakIdentity.SetCallsign(v);
            _selfPresence?.PublishOnce();
            _flash?.Invoke("Callsign: " + v);
            if (IsVisible) RebuildUi();
        }

        void Update()
        {
            PollSystemKeyboard();
            if (!IsVisible) return;
            // Suspend our own click handling while a callsign edit session is up —
            // otherwise rays through the XR keyboard would also hit rows behind it.
            if (_sysKeyboard != null || (_keyboard != null && _keyboard.IsVisible)) return;
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

        void AddButton(string name, Vector3 pos, Vector2 size, string label, bool highlighted, Action onClick)
        {
            var root = new GameObject("Ui_" + name).transform;
            root.SetParent(_rowsRoot, false);
            root.localPosition = pos;
            Quad("Bg", root, Vector3.zero, size,
                highlighted ? new Color(0.16f, 0.42f, 0.62f, 0.97f) : new Color(0.12f, 0.22f, 0.33f, 0.97f), 3000);
            Text("Label", root, new Vector3(0f, 0f, -0.006f), label, 0.0065f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.03f);
            col.isTrigger = true;
            _hitTargets.Add((root, onClick));
        }

        /// <summary>Invisible ray-click target (no visuals) — used to make the
        /// callsign text itself tappable without adding button chrome.</summary>
        void AddHitRegion(string name, Vector3 pos, Vector2 size, Action onClick)
        {
            var root = new GameObject("Ui_" + name).transform;
            root.SetParent(_rowsRoot, false);
            root.localPosition = pos;
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x, size.y, 0.03f);
            col.isTrigger = true;
            _hitTargets.Add((root, onClick));
        }

        static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        static void Quad(string name, Transform parent, Vector3 pos, Vector2 size, Color color, int queue)
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
        }

        static TextMesh Text(string name, Transform parent, Vector3 pos, string msg, float size, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center)
        {
            return XrText.Make(name, parent, pos, msg, size, color, anchor, alignment);
        }
    }
}
