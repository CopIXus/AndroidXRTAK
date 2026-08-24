using TakXr.Xr;
using UnityEngine;

namespace TakXr.Cot
{
    /// <summary>
    /// Web-parity Follow Along: move XrWorldRoot so the followed CoT stays ahead of the HMD.
    /// </summary>
    public class XrFollowController : MonoBehaviour
    {
        const float ChaseDistM = 45f;
        const float ChaseDropM = 12f;

        [SerializeField] XrWorldRoot world;
        [SerializeField] Transform cameraTransform;
        [SerializeField] CotLayerController cotLayer;

        string _followUid;
        bool _needsSnap;
        Vector3 _anchor;

        public string FollowUid => _followUid;
        public bool IsFollowing => !string.IsNullOrEmpty(_followUid);

        public void Configure(XrWorldRoot worldRoot, Transform cam, CotLayerController layer)
        {
            world = worldRoot;
            cameraTransform = cam;
            cotLayer = layer;
        }

        public void SetFollow(string uid)
        {
            _followUid = string.IsNullOrEmpty(uid) ? null : uid;
            _needsSnap = _followUid != null;
            if (_followUid != null) SnapToTarget();
        }

        public void Toggle(string uid)
        {
            if (_followUid == uid) SetFollow(null);
            else SetFollow(uid);
        }

        public void NotifyUserNavigated()
        {
            // Keep following after manual pan — re-pin on next marker update.
        }

        void LateUpdate()
        {
            if (string.IsNullOrEmpty(_followUid) || world == null || cameraTransform == null)
                return;
            if (_needsSnap)
            {
                SnapToTarget();
                return;
            }

            if (cotLayer == null || !cotLayer.TryGetMarkerWorldPos(_followUid, out var markerPos))
                return;

            // Keep marker pinned relative to previous anchor (track moves with feed).
            var delta = markerPos - _anchor;
            if (delta.sqrMagnitude > 1e-6f)
            {
                world.Root.position += delta;
                _anchor = markerPos;
            }
        }

        void SnapToTarget()
        {
            if (cotLayer == null || cameraTransform == null || world == null) return;
            if (!cotLayer.TryGetMarkerWorldPos(_followUid, out var markerWorld)) return;

            var camPos = cameraTransform.position;
            var flat = cameraTransform.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
            flat.Normalize();

            var desired = camPos + flat * ChaseDistM + Vector3.down * ChaseDropM;
            world.Root.position += desired - markerWorld;
            if (cotLayer.TryGetMarkerWorldPos(_followUid, out markerWorld))
                _anchor = markerWorld;
            _needsSnap = false;
        }
    }
}
