using System;
using System.Collections.Generic;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK Emergency Alert Beacon (`b-a-o-tbl`) &amp; 9-Line CASEVAC (`b-r-f-h-c`) spatial panel.
    /// Supports periodic emergency CoT broadcast, cancellation (`cancel="true"`), and
    /// full 9-Line Medevac composition &amp; transmission.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class XrEmergencyPanel : MonoBehaviour
    {
        const float PanelW = 1.04f;
        const float PanelH = 0.78f;
        const float RepeatBeaconSec = 15f;

        static readonly string[] AlertTypes =
        {
            "911 Alert",
            "In Contact",
            "Ring The Bell",
            "Geo-fence Breached",
        };

        static readonly string[] Line2Freqs = { "30.00 FM", "243.00 AM", "121.50 VHF", "40.50 FM" };
        static readonly string[] Line3Prec = { "A - URGENT", "B - URGENT SURG", "C - PRIORITY", "D - ROUTINE" };
        static readonly string[] Line4Equip = { "A - NONE", "B - HOIST", "C - EXTRACTION", "D - VENTILATOR" };
        static readonly string[] Line5Patients = { "L:1  A:0", "L:0  A:1", "L:2  A:1", "L:1  A:3" };
        static readonly string[] Line6Sec = { "N - NO ENEMY", "P - POSSIBLE ENEMY", "E - ENEMY IN AREA", "X - ARMED ESCORT" };
        static readonly string[] Line7Mark = { "A - PANELS", "B - PYRO", "C - SMOKE", "D - NONE", "E - IR STROBE" };
        static readonly string[] Line8Nat = { "A - US/COALITION MIL", "B - US/COALITION CIV", "C - NON-COALITION", "E - EPW" };
        static readonly string[] Line9Hlz = { "FLAT / CLEAR", "SLOPE / TREES", "WIRES NEARBY", "CBRN CONTAM" };

        AppConfig _config;
        CotFeedClient _feed;
        TakDirectHub _direct;
        XrWorldRoot _world;
        DemTerrainMap _terrain;
        Transform _cam;
        Action<string> _flash;

        Transform _root;
        Transform _contentRoot;
        bool _casevacTab;
        int _alertTypeIdx;
        bool _emergencyActive;
        float _nextBeaconTime;
        int _casevacCounter;

        // 9-Line state indices
        int _idxFreq;
        int _idxPrec;
        int _idxEquip;
        int _idxPatients;
        int _idxSec;
        int _idxMark = 2; // Smoke default
        int _idxNat;
        int _idxHlz;

        readonly List<(Transform root, Action onClick)> _hitTargets = new List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        readonly bool[] _blockedByMe = new bool[2];
        float _nextClickTime;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;
        public bool IsEmergencyActive => _emergencyActive;

        public static XrEmergencyPanel Create()
        {
            var go = new GameObject("XrEmergencyPanel");
            return go.AddComponent<XrEmergencyPanel>();
        }

        public void Configure(
            AppConfig config,
            CotFeedClient feed,
            TakDirectHub direct,
            XrWorldRoot world,
            DemTerrainMap terrain,
            Transform cam,
            Action<string> flash)
        {
            _config = config;
            if (_feed != null) _feed.EmergencyReceived -= OnEmergencyReceived;
            _feed = feed;
            if (_feed != null) _feed.EmergencyReceived += OnEmergencyReceived;
            _direct = direct;
            _world = world;
            _terrain = terrain;
            _cam = cam;
            _flash = flash;
        }

        void OnDestroy()
        {
            if (_feed != null) _feed.EmergencyReceived -= OnEmergencyReceived;
        }

        void OnEmergencyReceived(NormalizedCot cot)
        {
            if (cot == null) return;
            bool cancelled = cot.detail?.emergency != null && cot.detail.emergency.cancel;
            string who = cot.detail?.emergency?.callsign ?? cot.Callsign ?? cot.uid;
            string type = cot.detail?.emergency?.type ?? "EMERGENCY";
            _flash?.Invoke(cancelled
                ? $"EMERGENCY CANCELLED: {who}"
                : $"(!) EMERGENCY ALERT [{type}] FROM {who}");
        }

        void Awake()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.11f, 0.04f, 0.05f, 0.95f), 2990);
            Quad("TopBar", _root, new Vector3(0f, PanelH / 2f - 0.008f, 0.005f),
                new Vector2(PanelW, 0.015f), new Color(1f, 0.22f, 0.20f, 0.98f), 2995);

            _contentRoot = new GameObject("Content").transform;
            _contentRoot.SetParent(_root, false);
        }

        public void ShowEmergency(Transform cam = null)
        {
            if (cam != null) _cam = cam;
            _casevacTab = false;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildUi();
        }

        public void ShowCasevac(Transform cam = null)
        {
            if (cam != null) _cam = cam;
            _casevacTab = true;
            _root.gameObject.SetActive(true);
            PlaceFacing();
            RebuildUi();
        }

        public void Toggle(Transform cam = null, bool casevac = false)
        {
            if (IsVisible) { Hide(); return; }
            if (casevac) ShowCasevac(cam);
            else ShowEmergency(cam);
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            for (int h = 0; h < 2; h++)
            {
                if (_blockedByMe[h]) XrHandPinchInput.SetUiBlocking(h, false);
                _blockedByMe[h] = false;
            }
        }

        void PlaceFacing()
        {
            XrUiFacing.PlaceInDockTray(_root, _cam, new Vector3(0f, 0.14f, 1.55f));
        }

        void RebuildUi()
        {
            foreach (Transform child in _contentRoot) Destroy(child.gameObject);
            _hitTargets.Clear();

            Text("Title", _contentRoot, new Vector3(-PanelW / 2f + 0.045f, PanelH / 2f - 0.048f, -0.01f),
                _casevacTab ? "9-LINE CASEVAC / MEDEVAC REQUEST" : "ATAK EMERGENCY BEACON (911 ALERT)",
                0.0085f, new Color(1f, 0.88f, 0.88f, 1f), TextAnchor.MiddleLeft, TextAlignment.Left);

            AddButton("TabEm", new Vector3(0.11f, PanelH / 2f - 0.048f, -0.01f),
                new Vector2(0.19f, 0.048f), "911 Beacon", !_casevacTab, () =>
                {
                    _casevacTab = false;
                    RebuildUi();
                });

            AddButton("TabCv", new Vector3(0.32f, PanelH / 2f - 0.048f, -0.01f),
                new Vector2(0.19f, 0.048f), "9-Line Med", _casevacTab, () =>
                {
                    _casevacTab = true;
                    RebuildUi();
                });

            AddButton("Close", new Vector3(PanelW / 2f - 0.055f, PanelH / 2f - 0.048f, -0.01f),
                new Vector2(0.068f, 0.048f), "X", false, Hide);

            if (_casevacTab) BuildCasevacView();
            else BuildEmergencyBeaconView();
        }

        void BuildEmergencyBeaconView()
        {
            GetViewerGeo(out double lat, out double lon, out float hae);
            string cs = TakIdentity.Callsign;

            Text("Status", _contentRoot, new Vector3(0f, 0.22f, -0.01f),
                _emergencyActive
                    ? $"ACTIVE EMERGENCY BEACON: {AlertTypes[_alertTypeIdx]}\nBroadcasting every 15s for {cs} at {lat:F5}, {lon:F5}"
                    : $"Beacon Status: STANDBY (Disarmed)\nOperator: {cs}  ·  Pos: {lat:F5}, {lon:F5} ({hae:0}m)",
                0.0076f,
                _emergencyActive ? new Color(1f, 0.38f, 0.38f, 1f) : new Color(0.88f, 0.92f, 0.96f, 0.95f));

            Text("SelectLbl", _contentRoot, new Vector3(0f, 0.11f, -0.01f),
                "SELECT EMERGENCY TYPE:", 0.0068f, new Color(1f, 0.75f, 0.75f, 0.92f));

            for (int i = 0; i < AlertTypes.Length; i++)
            {
                int idx = i;
                float x = (i % 2 == 0) ? -0.23f : 0.23f;
                float y = (i < 2) ? 0.045f : -0.025f;
                AddButton("Type_" + i, new Vector3(x, y, -0.01f), new Vector2(0.42f, 0.055f),
                    AlertTypes[i], _alertTypeIdx == idx, () =>
                    {
                        _alertTypeIdx = idx;
                        RebuildUi();
                    });
            }

            // Big Arm / Broadcast button
            AddButton("ArmBtn", new Vector3(-0.22f, -0.16f, -0.01f), new Vector2(0.42f, 0.088f),
                _emergencyActive ? "RE-SEND BEACON NOW" : "ACTIVATE 911 ALERT",
                true, ActivateEmergencyBeacon, dangerColor: true);

            // Cancel button
            AddButton("CancelBtn", new Vector3(0.22f, -0.16f, -0.01f), new Vector2(0.42f, 0.088f),
                "CANCEL EMERGENCY", false, CancelEmergencyBeacon);

            Text("Note", _contentRoot, new Vector3(0f, -0.28f, -0.01f),
                "Emits CoT type b-a-o-tbl with <emergency> detail over live TAK TLS stream.",
                0.0060f, new Color(0.80f, 0.75f, 0.78f, 0.85f));
        }

        void BuildCasevacView()
        {
            GetViewerGeo(out double lat, out double lon, out float hae);
            string cs = TakIdentity.Callsign;

            float y = PanelH / 2f - 0.12f;
            Text("L1", _contentRoot, new Vector3(-PanelW / 2f + 0.045f, y, -0.01f),
                $"LINE 1 (HLZ GRID): {lat:F5}, {lon:F5}  ({hae:0}m HAE)   ·   LINE 2 (CS): {cs}",
                0.0066f, new Color(1f, 0.92f, 0.78f, 0.98f), TextAnchor.MiddleLeft, TextAlignment.Left);

            y -= 0.060f;
            AddCycleRow("LINE 2 FREQ", Line2Freqs[_idxFreq], -0.24f, y, () => { _idxFreq = (_idxFreq + 1) % Line2Freqs.Length; RebuildUi(); });
            AddCycleRow("LINE 3 PREC", Line3Prec[_idxPrec], 0.24f, y, () => { _idxPrec = (_idxPrec + 1) % Line3Prec.Length; RebuildUi(); });

            y -= 0.066f;
            AddCycleRow("LINE 4 EQUIP", Line4Equip[_idxEquip], -0.24f, y, () => { _idxEquip = (_idxEquip + 1) % Line4Equip.Length; RebuildUi(); });
            AddCycleRow("LINE 5 PATIENTS", Line5Patients[_idxPatients], 0.24f, y, () => { _idxPatients = (_idxPatients + 1) % Line5Patients.Length; RebuildUi(); });

            y -= 0.066f;
            AddCycleRow("LINE 6 SECURITY", Line6Sec[_idxSec], -0.24f, y, () => { _idxSec = (_idxSec + 1) % Line6Sec.Length; RebuildUi(); });
            AddCycleRow("LINE 7 MARKING", Line7Mark[_idxMark], 0.24f, y, () => { _idxMark = (_idxMark + 1) % Line7Mark.Length; RebuildUi(); });

            y -= 0.066f;
            AddCycleRow("LINE 8 NATLTY", Line8Nat[_idxNat], -0.24f, y, () => { _idxNat = (_idxNat + 1) % Line8Nat.Length; RebuildUi(); });
            AddCycleRow("LINE 9 HLZ/TERR", Line9Hlz[_idxHlz], 0.24f, y, () => { _idxHlz = (_idxHlz + 1) % Line9Hlz.Length; RebuildUi(); });

            y -= 0.090f;
            AddButton("SendCasevac", new Vector3(0f, y, -0.01f), new Vector2(0.68f, 0.072f),
                "TRANSMIT 9-LINE CASEVAC (b-r-f-h-c)", true, TransmitCasevac, dangerColor: true);
        }

        void AddCycleRow(string title, string val, float x, float y, Action onCycle)
        {
            AddButton(title, new Vector3(x, y, -0.01f), new Vector2(0.45f, 0.054f),
                $"{title}: {val}", false, onCycle);
        }

        public void ActivateEmergencyBeacon()
        {
            _emergencyActive = true;
            _nextBeaconTime = Time.unscaledTime + RepeatBeaconSec;
            SendEmergencyCot(cancel: false);
            if (IsVisible) RebuildUi();
        }

        public void CancelEmergencyBeacon()
        {
            _emergencyActive = false;
            SendEmergencyCot(cancel: true);
            if (IsVisible) RebuildUi();
        }

        void SendEmergencyCot(bool cancel)
        {
            GetViewerGeo(out double lat, out double lon, out float hae);
            var now = DateTime.UtcNow;
            string selfUid = TakIdentity.ClientUid;
            string cs = TakIdentity.Callsign;
            string alertType = AlertTypes[_alertTypeIdx];

            var cot = new NormalizedCot
            {
                uid = selfUid + "-911",
                type = cancel ? "b-a-o-can" : "b-a-o-tbl",
                how = "m-g",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = cs + "-ALERT" },
                detail = new CotDetail
                {
                    markerColor = "#ff2222",
                    remarks = cancel ? $"EMERGENCY CANCELLED by {cs}" : $"EMERGENCY [{alertType}] by {cs}",
                    emergency = new CotEmergency
                    {
                        type = alertType,
                        callsign = cs,
                        cancel = cancel,
                    },
                },
            };

            string xml = CotXmlBuilder.Build(cot);
            bool sent = _direct != null && _direct.SendCot(xml);
            _feed?.UpsertDirect(cot);
            _flash?.Invoke(cancel
                ? $"Emergency Cancelled ({(sent ? "sent to TAK" : "local")})"
                : $"(!) EMERGENCY [{alertType}] BROADCAST ({(sent ? "sent to TAK" : "local")})");
        }

        void TransmitCasevac()
        {
            GetViewerGeo(out double lat, out double lon, out float hae);
            var now = DateTime.UtcNow;
            string cs = TakIdentity.Callsign;
            string cvCallsign = $"CASEVAC-{cs}-{++_casevacCounter}";

            string summary =
                $"9-LINE CASEVAC [{cvCallsign}]\n" +
                $"L1: {lat:F5}, {lon:F5} ({hae:0}m) | L2: {cs} / {Line2Freqs[_idxFreq]}\n" +
                $"L3: {Line3Prec[_idxPrec]} | L4: {Line4Equip[_idxEquip]} | L5: {Line5Patients[_idxPatients]}\n" +
                $"L6: {Line6Sec[_idxSec]} | L7: {Line7Mark[_idxMark]} | L8: {Line8Nat[_idxNat]} | L9: {Line9Hlz[_idxHlz]}";

            var cot = new NormalizedCot
            {
                uid = "takxr.casevac." + Guid.NewGuid().ToString("N").Substring(0, 10),
                type = "b-r-f-h-c",
                how = "h-g-i-g-o",
                time = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                start = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                stale = now.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                point = new CotPoint { lat = lat, lon = lon, hae = hae, ce = 10f, le = 10f },
                contact = new CotContact { callsign = cvCallsign },
                detail = new CotDetail
                {
                    markerColor = "#ff3333",
                    remarks = summary,
                },
            };

            bool sent = _direct != null && _direct.SendCot(CotXmlBuilder.Build(cot));
            _feed?.UpsertDirect(cot);
            _flash?.Invoke($"9-Line CASEVAC {cvCallsign} Transmitted!\n{(sent ? "Sent to TAK server + GeoChat" : "Placed locally")}");
            Hide();
        }

        void GetViewerGeo(out double lat, out double lon, out float hae)
        {
            lat = _config != null ? _config.originLat : 0.0;
            lon = _config != null ? _config.originLon : 0.0;
            hae = _config != null ? (float)_config.originAlt : 0f;
            if (_world != null && _cam != null && _config != null)
            {
                var local = _world.Root.InverseTransformPoint(_cam.position);
                const double mPerDegLat = 111320.0;
                double mPerDegLon = mPerDegLat * Math.Cos(_config.originLat * Math.PI / 180.0);
                if (Math.Abs(mPerDegLon) < 1e-3) mPerDegLon = mPerDegLat;
                lat = _config.originLat + local.z / mPerDegLat;
                lon = _config.originLon + local.x / mPerDegLon;
                if (_terrain != null && _terrain.TrySampleHae(lat, lon, out float demHae))
                    hae = demHae;
            }
        }

        void Update()
        {
            if (_emergencyActive && Time.unscaledTime >= _nextBeaconTime)
            {
                _nextBeaconTime = Time.unscaledTime + RepeatBeaconSec;
                SendEmergencyCot(cancel: false);
            }

            if (!IsVisible) return;
            PlaceFacing();
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
                                _nextClickTime = Time.unscaledTime + 0.28f;
                                onClick?.Invoke();
                            }
                            break;
                        }
                        if (pointingAtUi) break;
                    }
                }

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
                if (!IsVisible) return;
            }
        }

        void AddButton(string name, Vector3 pos, Vector2 size, string label, bool active, Action onClick, bool dangerColor = false)
        {
            var root = new GameObject("Btn_" + name).transform;
            root.SetParent(_contentRoot, false);
            root.localPosition = pos;
            Color bg;
            if (dangerColor)
                bg = active ? new Color(0.82f, 0.14f, 0.14f, 0.98f) : new Color(0.48f, 0.12f, 0.12f, 0.95f);
            else
                bg = active ? new Color(0.65f, 0.22f, 0.22f, 0.97f) : new Color(0.22f, 0.11f, 0.14f, 0.95f);
            Quad("Bg", root, Vector3.zero, size, bg, 3000);
            Text("Lbl", root, new Vector3(0f, 0f, -0.005f), label, 0.0060f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(size.x + 0.01f, size.y + 0.01f, 0.035f);
            col.isTrigger = true;
            _hitTargets.Add((root, onClick));
        }

        static void Quad(string name, Transform parent, Vector3 pos, Vector2 size, Color color, int queue)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", color);
            r.sharedMaterial.renderQueue = queue;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static TextMesh Text(string name, Transform parent, Vector3 pos, string msg, float size, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter,
            TextAlignment alignment = TextAlignment.Center)
        {
            return XrText.Make(name, parent, pos, msg, size, color, anchor, alignment);
        }
    }
}
