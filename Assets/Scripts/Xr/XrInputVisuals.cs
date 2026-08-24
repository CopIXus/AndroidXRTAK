using UnityEngine;

namespace TakXr.Xr
{
    /// <summary>
    /// Controller/hand rays follow true pointer pose so users can aim at CoTs and UI.
    /// </summary>
    public class XrInputVisuals : MonoBehaviour
    {
        [SerializeField] float rayLength = 8f;

        Transform _left;
        Transform _right;
        LineRenderer _leftRay;
        LineRenderer _rightRay;
        Transform _leftTip;
        Transform _rightTip;

        public static XrInputVisuals Ensure(Transform parent)
        {
            var existing = parent.GetComponentInChildren<XrInputVisuals>();
            if (existing != null) return existing;
            var go = new GameObject("XrInputVisuals");
            go.transform.SetParent(parent, false);
            return go.AddComponent<XrInputVisuals>();
        }

        void Awake()
        {
            _left = MakeHand("LeftHand", new Color(0.43f, 0.78f, 1f), out _leftTip);
            _right = MakeHand("RightHand", new Color(1f, 0.63f, 0.38f), out _rightTip);
            _leftRay = MakeRay(_left, new Color(0.43f, 0.78f, 1f, 0.85f));
            _rightRay = MakeRay(_right, new Color(1f, 0.63f, 0.38f, 0.85f));
        }

        Transform MakeHand(string name, Color color, out Transform tip)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);

            var palm = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            palm.name = "Palm";
            palm.transform.SetParent(root, false);
            palm.transform.localScale = Vector3.one * 0.018f;
            Object.Destroy(palm.GetComponent<Collider>());
            Tint(palm, color);

            var tipGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tipGo.name = "Tip";
            tipGo.transform.SetParent(root, false);
            tipGo.transform.localPosition = new Vector3(0f, 0f, rayLength);
            tipGo.transform.localScale = Vector3.one * 0.012f;
            Object.Destroy(tipGo.GetComponent<Collider>());
            Tint(tipGo, color);
            tip = tipGo.transform;

            root.gameObject.SetActive(false);
            return root;
        }

        LineRenderer MakeRay(Transform hand, Color color)
        {
            var go = new GameObject("Ray");
            go.transform.SetParent(hand, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.startWidth = 0.004f;
            lr.endWidth = 0.001f;
            var sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("UI/Default");
            lr.material = sh != null ? new Material(sh) : null;
            lr.startColor = color;
            lr.endColor = new Color(color.r, color.g, color.b, 0.15f);
            return lr;
        }

        static void Tint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Color");
            if (sh == null) return;
            r.sharedMaterial = new Material(sh);
            if (r.sharedMaterial.HasProperty("_BaseColor")) r.sharedMaterial.SetColor("_BaseColor", c);
            if (r.sharedMaterial.HasProperty("_Color")) r.sharedMaterial.SetColor("_Color", c);
            else
            {
                try { r.sharedMaterial.color = c; } catch { /* ignore */ }
            }
        }

        void LateUpdate()
        {
            UpdateHand(0, _left, _leftRay, _leftTip);
            UpdateHand(1, _right, _rightRay, _rightTip);
        }

        void UpdateHand(int index, Transform hand, LineRenderer ray, Transform tip)
        {
            bool ok = XrHandPinchInput.TryGetAim(index, out var origin, out var forward);
            if (!ok)
            {
                hand.gameObject.SetActive(false);
                ray.enabled = false;
                return;
            }

            hand.gameObject.SetActive(true);
            hand.position = origin;
            hand.rotation = Quaternion.LookRotation(forward, Vector3.up);

            bool grabbing = XrHandPinchInput.IsGrabbing(index);
            float hitDist = rayLength;
            if (Physics.Raycast(origin, forward, out var hit, rayLength, ~0, QueryTriggerInteraction.Collide))
                hitDist = hit.distance;

            if (tip != null)
            {
                tip.localPosition = new Vector3(0f, 0f, hitDist);
                tip.localScale = Vector3.one * (grabbing ? 0.02f : 0.012f);
            }

            ray.enabled = true;
            ray.startWidth = grabbing ? 0.006f : 0.0035f;
            ray.SetPosition(0, origin);
            ray.SetPosition(1, origin + forward.normalized * hitDist);
        }
    }
}
