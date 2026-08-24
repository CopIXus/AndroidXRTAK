using System;
using TakXr.Cot;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// Web-parity in-headset CoT info panel: callsign/type/speed/course + Follow / Video / Close.
    /// </summary>
    public class XrInfoPanel : MonoBehaviour
    {
        public event Action<string, bool> FollowToggled;
        public event Action<NormalizedCot> VideoRequested;
        public event Action<NormalizedCot> GoToRequested;
        public event Action Closed;

        Transform _root;
        TextMesh _title;
        TextMesh _body;
        Transform _followBtn;
        Transform _videoBtn;
        Transform _goToBtn;
        Transform _closeBtn;
        TextMesh _followLbl;
        TextMesh _videoLbl;
        TextMesh _goToLbl;
        NormalizedCot _cot;
        bool _following;
        bool _visible;

        public bool IsVisible => _visible;
        public string CurrentUid => _cot?.uid;

        public static XrInfoPanel Create()
        {
            var go = new GameObject("XrInfoPanel");
            return go.AddComponent<XrInfoPanel>();
        }

        void Awake() => Build();

        void Build()
        {
            _root = new GameObject("Root").transform;
            _root.SetParent(transform, false);
            _root.gameObject.SetActive(false);

            Quad("Bg", _root, Vector3.zero, new Vector2(0.72f, 0.48f),
                new Color(0.05f, 0.07f, 0.1f, 0.92f));

            // XrUiFacing: +Z points away from viewer, so "in front of bg" = negative local Z.
            _title = Text("Title", _root, new Vector3(0f, 0.17f, -0.01f),
                "CALLSIGN", 0.014f, new Color(0.9f, 0.95f, 1f));
            _body = Text("Body", _root, new Vector3(0f, 0.04f, -0.01f),
                "", 0.009f, new Color(0.7f, 0.85f, 1f, 0.95f));
            _body.anchor = TextAnchor.MiddleCenter;
            _body.alignment = TextAlignment.Center;

            _followBtn = MakeBtn("Follow", new Vector3(-0.24f, -0.14f, -0.01f), "Follow",
                new Color(0.12f, 0.47f, 1f, 0.95f), out _followLbl);
            _videoBtn = MakeBtn("Video", new Vector3(-0.06f, -0.14f, -0.01f), "Video",
                new Color(0.12f, 0.62f, 0.33f, 0.95f), out _videoLbl);
            _goToBtn = MakeBtn("GoTo", new Vector3(0.12f, -0.14f, -0.01f), "Go To",
                new Color(0.45f, 0.35f, 0.12f, 0.95f), out _goToLbl);
            _closeBtn = MakeBtn("Close", new Vector3(0.28f, -0.14f, -0.01f), "X",
                new Color(0.35f, 0.2f, 0.2f, 0.95f), out _);
        }

        Transform MakeBtn(string id, Vector3 localPos, string label, Color bg, out TextMesh lbl)
        {
            var t = new GameObject("Btn_" + id).transform;
            t.SetParent(_root, false);
            t.localPosition = localPos;
            Quad("Bg", t, Vector3.zero, new Vector2(0.16f, 0.07f), bg);
            lbl = Text("Lbl", t, new Vector3(0f, 0f, -0.005f), label, 0.01f, Color.white);
            var col = t.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(0.17f, 0.08f, 0.04f);
            col.isTrigger = true;
            return t;
        }

        public void Show(NormalizedCot cot, bool following, Transform camera)
        {
            _cot = cot;
            _following = following;
            _visible = true;
            _root.gameObject.SetActive(true);
            RefreshText();
            PlaceFacing(camera);
        }

        public void Hide()
        {
            _visible = false;
            _cot = null;
            if (_root != null) _root.gameObject.SetActive(false);
            Closed?.Invoke();
        }

        public void SetFollowing(bool following)
        {
            if (_following == following) return;
            _following = following;
            if (_cot != null) RefreshText();
        }

        public void Refresh(NormalizedCot cot)
        {
            if (_cot == null || cot == null || _cot.uid != cot.uid) return;
            _cot = cot;
            RefreshText();
        }

        public void PlaceFacing(Transform camera)
        {
            if (!_visible || camera == null || _root == null) return;
            var camPos = camera.position;
            var flat = camera.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();
            _root.position = camPos + flat * 1.8f + Vector3.up * -0.05f;
            // +Z toward camera so TextMesh / button labels read correctly.
            _root.rotation = XrUiFacing.RotationFacingUser(_root.position, camPos);
        }

        /// <summary>True if the ray hit this panel (and possibly activated a button).</summary>
        public bool HandleRaySelect(Ray ray, float maxDist = 6f)
        {
            if (!_visible || _root == null || !_root.gameObject.activeInHierarchy) return false;
            if (!Physics.Raycast(ray, out var hit, maxDist, ~0, QueryTriggerInteraction.Collide)) return false;
            if (!hit.transform.IsChildOf(_root) && hit.transform != _root) return false;

            if (_followBtn != null &&
                (hit.transform == _followBtn || hit.transform.IsChildOf(_followBtn)))
            {
                _following = !_following;
                RefreshText();
                if (_cot != null) FollowToggled?.Invoke(_cot.uid, _following);
                return true;
            }
            if (_videoBtn != null && _videoBtn.gameObject.activeSelf &&
                (hit.transform == _videoBtn || hit.transform.IsChildOf(_videoBtn)))
            {
                if (_cot != null) VideoRequested?.Invoke(_cot);
                return true;
            }
            if (_goToBtn != null &&
                (hit.transform == _goToBtn || hit.transform.IsChildOf(_goToBtn)))
            {
                if (_cot != null) GoToRequested?.Invoke(_cot);
                return true;
            }
            if (_closeBtn != null &&
                (hit.transform == _closeBtn || hit.transform.IsChildOf(_closeBtn)))
            {
                Hide();
                return true;
            }
            return true; // body hit absorbs select
        }

        /// <summary>Proximity poke for hands near buttons.</summary>
        public bool HandleProximitySelect(Vector3 tip, float radius = 0.07f)
        {
            if (!_visible) return false;
            if (Near(_followBtn, tip, radius))
            {
                _following = !_following;
                RefreshText();
                if (_cot != null) FollowToggled?.Invoke(_cot.uid, _following);
                return true;
            }
            if (_videoBtn != null && _videoBtn.gameObject.activeSelf && Near(_videoBtn, tip, radius))
            {
                if (_cot != null) VideoRequested?.Invoke(_cot);
                return true;
            }
            if (Near(_goToBtn, tip, radius))
            {
                if (_cot != null) GoToRequested?.Invoke(_cot);
                return true;
            }
            if (Near(_closeBtn, tip, radius))
            {
                Hide();
                return true;
            }
            return false;
        }

        static bool Near(Transform t, Vector3 tip, float r) =>
            t != null && t.gameObject.activeInHierarchy && Vector3.Distance(tip, t.position) < r;

        void RefreshText()
        {
            if (_cot == null) return;
            if (_title != null) _title.text = _cot.Callsign ?? _cot.uid;
            float course = _cot.detail?.track != null ? _cot.detail.track.course : float.NaN;
            float speed = _cot.detail?.track != null ? _cot.detail.track.speed : float.NaN;
            string courseStr = float.IsNaN(course) ? "—" : $"{course:000}°";
            string speedStr = float.IsNaN(speed) ? "—" : $"{speed:0.0} m/s";
            string alt = "—";
            if (_cot.point != null)
            {
                float hae = _cot.point.hae;
                // TAK unknown-altitude sentinel (9999999) and other junk.
                if (float.IsFinite(hae) && Mathf.Abs(hae) < 9000000f)
                    alt = $"{hae:0} m";
                else
                    alt = "ground / unknown";
            }
            string latlon = _cot.point != null
                ? $"{_cot.point.lat:F5}, {_cot.point.lon:F5}"
                : "—";
            if (_body != null)
            {
                _body.text =
                    $"Type  {_cot.type ?? "—"}\n" +
                    $"Course  {courseStr}    Speed  {speedStr}\n" +
                    $"Alt  {alt}\n" +
                    $"{latlon}";
            }
            if (_followLbl != null) _followLbl.text = _following ? "Unfollow" : "Follow";
            bool hasVideo = _cot.detail?.video != null && !string.IsNullOrEmpty(_cot.detail.video.url);
            if (_videoBtn != null) _videoBtn.gameObject.SetActive(hasVideo);
            if (_videoLbl != null) _videoLbl.text = "Video";
        }

        static GameObject Quad(string name, Transform parent, Vector3 localPos, Vector2 size, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                var sh = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color");
                r.sharedMaterial = new Material(sh);
                if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", color);
                if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", color);
                else
                {
                    try { r.sharedMaterial.color = color; } catch { /* ignore */ }
                }
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return go;
        }

        static TextMesh Text(string name, Transform parent, Vector3 localPos, string msg, float charSize, Color color)
        {
            // Crisp raster via XrText; this panel's legacy raster was fontSize 48,
            // so scale to the helper's "as if 64" size to keep world size identical.
            return XrText.Make(name, parent, localPos, msg, charSize * 48f / 64f, color);
        }
    }
}
