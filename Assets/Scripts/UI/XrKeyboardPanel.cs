using System;
using System.Collections.Generic;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// Procedural in-world XR keyboard — the fallback text editor for platforms
    /// where TouchScreenKeyboard never shows (see XrSettingsPanel.EditCallsign).
    /// Rows A–Z / 0–9 / dash+underscore plus Del / Clear / Cancel / Done, built
    /// from dark plates + XrText labels with the same ray+pinch interaction as
    /// XrLayersPanel/XrSettingsPanel (trigger colliders, rising-edge grab) and
    /// XrRadialMenu-style SetUiBlocking so pinches don't leak into locomotion.
    /// </summary>
    // After XrChromeHud so our SetUiBlocking while aiming at keys is not stomped
    // by the chrome's per-frame blocking re-evaluation (XrRadialMenu pattern).
    [DefaultExecutionOrder(60)]
    public class XrKeyboardPanel : MonoBehaviour
    {
        const float PanelW = 1.00f;
        const float PanelH = 0.60f;
        const float KeyPitch = 0.094f;
        const float KeySize = 0.082f;
        const int DefaultMaxLen = 24;

        static readonly string[] KeyRows =
        {
            "1234567890",
            "QWERTYUIOP",
            "ASDFGHJKL",
            "ZXCVBNM-_.:",
        };

        Transform _cam;
        Action<string> _onCommit;
        string _value = "";
        string _title = "TEXT";
        int _maxLen = DefaultMaxLen;

        Transform _root;
        TextMesh _titleText;
        TextMesh _valueText;
        Transform _rowsRoot;
        readonly List<(Transform root, Action onClick)> _hitTargets = new List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        readonly bool[] _blockedByMe = new bool[2];
        float _nextClickTime;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrKeyboardPanel Create()
        {
            var go = new GameObject("XrKeyboardPanel");
            return go.AddComponent<XrKeyboardPanel>();
        }

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.05f, 0.10f, 0.16f, 0.96f), 2995);
            _titleText = Text("Title", _root, new Vector3(-PanelW / 2f + 0.05f, PanelH / 2f - 0.045f, -0.01f),
                _title, 0.009f, new Color(0.85f, 0.95f, 1f, 1f),
                TextAnchor.MiddleLeft, TextAlignment.Left);

            // Value readout on its own plate.
            Quad("ValueBg", _root, new Vector3(0f, PanelH / 2f - 0.115f, 0.0f),
                new Vector2(PanelW - 0.10f, 0.062f), new Color(0.02f, 0.05f, 0.09f, 0.97f), 3000);
            _valueText = Text("Value", _root, new Vector3(0f, PanelH / 2f - 0.115f, -0.006f),
                "", 0.0095f, new Color(1f, 1f, 0.95f, 1f));

            _rowsRoot = new GameObject("Rows").transform;
            _rowsRoot.SetParent(_root, false);
        }

        /// <summary>Show the keyboard seeded with <paramref name="initial"/>;
        /// Done invokes <paramref name="onCommit"/> with the edited text.</summary>
        public void Open(Transform cam, string title, string initial, Action<string> onCommit, int maxLen = 0)
        {
            if (cam != null) _cam = cam;
            _title = string.IsNullOrEmpty(title) ? "TEXT" : title;
            _maxLen = maxLen > 0 ? maxLen : DefaultMaxLen;
            _value = initial ?? "";
            if (_value.Length > _maxLen) _value = _value.Substring(0, _maxLen);
            _onCommit = onCommit;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildUi();
        }

        public void Hide()
        {
            _root.gameObject.SetActive(false);
            for (int h = 0; h < 2; h++)
            {
                if (_blockedByMe[h]) XrHandPinchInput.SetUiBlocking(h, false);
                _blockedByMe[h] = false;
            }
        }

        void PlaceFacing()
        {
            if (_cam == null) return;
            var camPos = _cam.position;
            var flat = _cam.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            // Slightly closer + lower than the settings panel so both stay usable.
            _root.position = camPos + flat * 1.45f - Vector3.up * 0.18f;
            _root.rotation = XrUiFacing.RotationFacingUser(_root.position, camPos);
        }

        void RebuildUi()
        {
            foreach (Transform child in _rowsRoot) Destroy(child.gameObject);
            _hitTargets.Clear();

            if (_titleText != null) _titleText.text = _title;
            UpdateValueText();

            AddButton("Close", new Vector3(PanelW / 2f - 0.05f, PanelH / 2f - 0.045f, -0.01f),
                new Vector2(0.07f, 0.05f), "X", false, Hide);

            float y = PanelH / 2f - 0.20f;
            foreach (var row in KeyRows)
            {
                float x0 = -(row.Length - 1) * KeyPitch / 2f;
                for (int i = 0; i < row.Length; i++)
                {
                    char c = row[i];
                    AddButton("Key" + c, new Vector3(x0 + i * KeyPitch, y, -0.01f),
                        new Vector2(KeySize, KeySize * 0.72f), c.ToString(), false,
                        () => AppendChar(c));
                }
                y -= 0.075f;
            }

            // Action row: Clear / Del / Cancel / Done.
            AddButton("Clear", new Vector3(-0.36f, y, -0.01f), new Vector2(0.18f, 0.055f),
                "Clear", false, () => { _value = ""; UpdateValueText(); });
            AddButton("Del", new Vector3(-0.14f, y, -0.01f), new Vector2(0.18f, 0.055f),
                "< Del", false, Backspace);
            AddButton("Cancel", new Vector3(0.10f, y, -0.01f), new Vector2(0.18f, 0.055f),
                "Cancel", false, Hide);
            AddButton("Done", new Vector3(0.34f, y, -0.01f), new Vector2(0.22f, 0.055f),
                "Done", true, CommitAndClose);
        }

        void AppendChar(char c)
        {
            if (_value.Length >= _maxLen) return;
            _value += c;
            UpdateValueText();
        }

        void Backspace()
        {
            if (_value.Length == 0) return;
            _value = _value.Substring(0, _value.Length - 1);
            UpdateValueText();
        }

        void CommitAndClose()
        {
            var commit = _onCommit;
            var value = _value;
            Hide();
            commit?.Invoke(value);
        }

        void UpdateValueText()
        {
            if (_valueText != null) _valueText.text = _value + "_";
        }

        // ---------------- input (XrChromeHud.PollUiSelect pattern) ----------------

        void Update()
        {
            if (!IsVisible) return;
            for (int h = 0; h < 2; h++)
            {
                bool aimOk = XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;

                bool pointingAtUi = false;
                if (aimOk)
                {
                    var hits = Physics.RaycastAll(origin, fwd, 8f, ~0, QueryTriggerInteraction.Collide);
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    foreach (var hit in hits)
                    {
                        foreach (var (root, onClick) in _hitTargets)
                        {
                            if (root == null) continue;
                            if (hit.transform != root && !hit.transform.IsChildOf(root)) continue;
                            pointingAtUi = true;
                            if (rising && Time.unscaledTime >= _nextClickTime)
                            {
                                onClick?.Invoke();
                                _nextClickTime = Time.unscaledTime + 0.22f;
                            }
                            break;
                        }
                        if (pointingAtUi) break;
                    }
                }

                // Keep pinches aimed at the keyboard from leaking into locomotion.
                bool block = pointingAtUi && grabbing;
                if (block)
                {
                    XrHandPinchInput.SetUiBlocking(h, true);
                    _blockedByMe[h] = true;
                }
                else if (_blockedByMe[h])
                {
                    XrHandPinchInput.SetUiBlocking(h, false);
                    _blockedByMe[h] = false;
                }

                if (!IsVisible) return; // a click may have closed the panel
            }
        }

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

        void AddButton(string name, Vector3 pos, Vector2 size, string label, bool highlighted, Action onClick)
        {
            var root = new GameObject("Ui_" + name).transform;
            root.SetParent(_rowsRoot, false);
            root.localPosition = pos;
            Quad("Bg", root, Vector3.zero, size,
                highlighted ? new Color(0.16f, 0.42f, 0.62f, 0.97f) : new Color(0.12f, 0.22f, 0.33f, 0.97f), 3000);
            Text("Label", root, new Vector3(0f, 0f, -0.006f), label, 0.007f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.006f, size.y + 0.006f, 0.03f);
            col.isTrigger = true;
            _hitTargets.Add((root, onClick));
        }

        static TextMesh Text(string name, Transform parent, Vector3 pos, string msg, float size, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center)
        {
            return XrText.Make(name, parent, pos, msg, size, color, anchor, alignment);
        }
    }
}
