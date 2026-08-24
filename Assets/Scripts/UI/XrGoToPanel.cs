using System;
using TakXr.Core;
using TakXr.Cot;
using TakXr.Locomotion;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// ATAK GoTo: enter lat,lon (or lat lon) and jump the world view there.
    /// </summary>
    public class XrGoToPanel : MonoBehaviour
    {
        const float PanelW = 0.72f;
        const float PanelH = 0.42f;

        AppConfig _config;
        DemTerrainMap _terrain;
        XrWorldLocomotion _loco;
        XrWorldRoot _world;
        CotLayerController _cotLayer;
        Transform _cam;
        Action<string> _flash;
        XrKeyboardPanel _keyboard;

        Transform _root;
        TextMesh _title;
        TextMesh _value;
        TextMesh _hint;
        string _coords = "";
        readonly System.Collections.Generic.List<(Transform root, Action onClick)> _hits =
            new System.Collections.Generic.List<(Transform, Action)>();
        readonly bool[] _wasGrabbing = new bool[2];
        float _nextClick;

        public bool IsVisible => _root != null && _root.gameObject.activeSelf;

        public static XrGoToPanel Create()
        {
            var go = new GameObject("XrGoToPanel");
            return go.AddComponent<XrGoToPanel>();
        }

        public void Configure(AppConfig config, DemTerrainMap terrain, XrWorldLocomotion loco,
            XrWorldRoot world, CotLayerController cotLayer, Transform cam, Action<string> flash)
        {
            _config = config;
            _terrain = terrain;
            _loco = loco;
            _world = world;
            _cotLayer = cotLayer;
            _cam = cam;
            _flash = flash;
        }

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);
            Quad("Bg", _root, new Vector3(0f, 0f, 0.01f), new Vector2(PanelW, PanelH),
                new Color(0.04f, 0.05f, 0.08f, 0.96f), 2995);
            _title = Text("Title", _root, new Vector3(0f, 0.16f, -0.01f),
                "GO TO", 0.008f, new Color(0.85f, 0.95f, 1f, 1f));
            _value = Text("Value", _root, new Vector3(0f, 0.04f, -0.01f),
                "", 0.006f, new Color(1f, 1f, 1f, 0.98f));
            _hint = Text("Hint", _root, new Vector3(0f, -0.06f, -0.01f),
                "lat, lon  ·  tap Edit", 0.0036f, new Color(0.7f, 0.8f, 0.9f, 0.75f));
            AddBtn("Edit", new Vector3(-0.18f, -0.14f, -0.01f), EditCoords);
            AddBtn("Go", new Vector3(0.0f, -0.14f, -0.01f), Jump);
            AddBtn("Close", new Vector3(0.18f, -0.14f, -0.01f), Hide);
        }

        public void Open(Transform cam)
        {
            if (cam != null) _cam = cam;
            if (string.IsNullOrEmpty(_coords) && _config != null)
                _coords = $"{_config.originLat:F5}, {_config.originLon:F5}";
            if (_value != null) _value.text = _coords;
            _root.gameObject.SetActive(true);
            PlaceFacing();
        }

        public void Hide()
        {
            _keyboard?.Hide();
            if (_root != null) _root.gameObject.SetActive(false);
        }

        void EditCoords()
        {
            if (_keyboard == null) _keyboard = XrKeyboardPanel.Create();
            _keyboard.Open(_cam, "Coordinates", _coords, s =>
            {
                _coords = (s ?? "").Trim();
                if (_value != null) _value.text = _coords;
            });
        }

        void Jump()
        {
            if (!TryParse(_coords, out double lat, out double lon))
            {
                _flash?.Invoke("GoTo: enter lat, lon");
                return;
            }
            if (_config == null || _world == null) return;
            float alt = (float)_config.originAlt;
            if (_terrain != null && _terrain.TrySampleHae(lat, lon, out var demHae))
                alt = demHae;
            _config.originLat = lat;
            _config.originLon = lon;
            _config.originAlt = alt;
            var origin = new GeoMath.Geodetic(lat, lon, alt);
            _cotLayer?.SetOrigin(origin);
            _terrain?.Rebuild();
            if (_loco != null && _world != null)
            {
                var local = Vector3.up * 40f;
                var worldPos = _world.Root.TransformPoint(local);
                _loco.FrameWorldPoint(worldPos, overviewDistM: 200f, heightM: 60f);
            }
            _flash?.Invoke($"GoTo {lat:F5}, {lon:F5}");
            Hide();
        }

        static bool TryParse(string s, out double lat, out double lon)
        {
            lat = lon = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Replace(';', ',').Replace('\t', ' ');
            string[] parts;
            if (s.Contains(","))
                parts = s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            else
                parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out lat)) return false;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out lon)) return false;
            return lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
        }

        void PlaceFacing()
        {
            if (_cam == null || _root == null) return;
            var pos = _cam.position + _cam.TransformDirection(new Vector3(0f, -0.05f, 1.6f));
            _root.position = pos;
            _root.rotation = XrUiFacing.RotationFacingUser(pos, _cam.position);
        }

        void LateUpdate()
        {
            if (!IsVisible) return;
            PlaceFacing();
            for (int h = 0; h < 2; h++)
            {
                bool aimOk = TakXr.Xr.XrHandPinchInput.TryGetAim(h, out var origin, out var fwd);
                bool grabbing = aimOk && TakXr.Xr.XrHandPinchInput.IsGrabbing(h);
                bool rising = grabbing && !_wasGrabbing[h];
                _wasGrabbing[h] = grabbing;
                if (!aimOk || !rising || Time.unscaledTime < _nextClick) continue;
                var hits = Physics.RaycastAll(origin, fwd, 6f, ~0, QueryTriggerInteraction.Collide);
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var hit in hits)
                {
                    foreach (var t in _hits)
                    {
                        if (t.root == null) continue;
                        if (hit.transform == t.root || hit.transform.IsChildOf(t.root))
                        {
                            t.onClick?.Invoke();
                            _nextClick = Time.unscaledTime + 0.35f;
                            return;
                        }
                    }
                }
            }
        }

        void AddBtn(string label, Vector3 localPos, Action onClick)
        {
            var root = new GameObject("Btn_" + label).transform;
            root.SetParent(_root, false);
            root.localPosition = localPos;
            Quad("Bg", root, new Vector3(0f, 0f, 0.004f), new Vector2(0.15f, 0.06f),
                new Color(0.12f, 0.16f, 0.22f, 0.95f), 3000);
            Text("L", root, new Vector3(0f, 0f, -0.004f), label, 0.004f, Color.white);
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.16f, 0.07f, 0.08f);
            _hits.Add((root, onClick));
        }

        static GameObject Quad(string name, Transform parent, Vector3 localPos, Vector2 size, Color color, int rq)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", color);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
            r.sharedMaterial.renderQueue = rq;
            return go;
        }

        static TextMesh Text(string name, Transform parent, Vector3 localPos, string msg, float cs, Color c) =>
            XrText.Make(name, parent, localPos, msg, cs, c);
    }
}
