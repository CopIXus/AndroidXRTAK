using TakXr.Xr;
using UnityEngine;
using UnityEngine.UI;

namespace TakXr.UI
{
    /// <summary>Pinch/trigger ray against world-space UI (toolbar buttons).</summary>
    public class XrUiPointer : MonoBehaviour
    {
        [SerializeField] float maxDistance = 6f;
        [SerializeField] LayerMask mask = ~0;

        bool[] _wasGrabbing = new bool[2];

        void Update()
        {
            for (int i = 0; i < 2; i++)
            {
                XrHandPinchInput.TryGetGrab(i, out var pos, out bool grabbing);
                if (!grabbing)
                {
                    _wasGrabbing[i] = false;
                    continue;
                }

                // Aim: use hand forward from node rotation when available.
                var dir = EstimateAim(i, pos);
                if (Physics.Raycast(pos, dir, out var hit, maxDistance, mask, QueryTriggerInteraction.Collide))
                {
                    var btn = hit.collider.GetComponentInParent<Button>();
                    if (btn != null && !_wasGrabbing[i])
                        btn.onClick.Invoke();
                }

                _wasGrabbing[i] = true;
            }
        }

        static Vector3 EstimateAim(int handIndex, Vector3 pos)
        {
            // Prefer camera-forward bias if hand pose aim is unknown.
            var cam = Camera.main;
            if (cam != null)
            {
                var to = (pos - cam.transform.position);
                if (to.sqrMagnitude > 1e-4f)
                {
                    // Point from camera through hand tip roughly forward.
                    return Vector3.Lerp(cam.transform.forward, to.normalized, 0.35f).normalized;
                }
                return cam.transform.forward;
            }
            return Vector3.forward;
        }
    }
}
