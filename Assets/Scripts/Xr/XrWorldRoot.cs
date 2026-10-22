using UnityEngine;

namespace TakXr.Xr
{
    /// <summary>
    /// Content root that locomotion transforms (map + CoTs). Headset camera stays under XR Origin.
    /// Mirrors WebXR <c>worldGroup</c>.
    /// </summary>
    public class XrWorldRoot : MonoBehaviour
    {
        public static XrWorldRoot Instance { get; private set; }

        public Transform Root => transform;

        /// <summary>
        /// Tabletop / Wall pitch in degrees.
        /// 0 = flat on the floor (looking down);
        /// positive (e.g. +38° angled desk, +62° wall map) tilts the far horizon UP in front of the viewer
        /// so the user can stand upright and look straight ahead for a God's-eye view without neck strain.
        /// </summary>
        public float WorldPitchDeg { get; private set; }

        public const float MinPitchDeg = -60f;
        public const float MaxPitchDeg = 75f;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public static XrWorldRoot Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("XrWorldRoot");
            return go.AddComponent<XrWorldRoot>();
        }

        /// <summary>Place AOI origin ~height meters below the viewer and a bit ahead.</summary>
        public void ApplyInitialOverview(Transform camera, float heightAboveGround = 180f, float forwardMeters = 40f)
        {
            if (camera == null) return;
            var camPos = camera.position;
            var flatFwd = camera.forward;
            flatFwd.y = 0f;
            if (flatFwd.sqrMagnitude < 1e-6f) flatFwd = Vector3.forward;
            flatFwd.Normalize();
            // World local (0,0,0) = geodetic origin. Put it below/ahead of the headset.
            transform.position = camPos - Vector3.up * heightAboveGround + flatFwd * forwardMeters;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            // Tracking must match identity rotation; caller re-applies saved pitch after north-up.
            WorldPitchDeg = 0f;
        }

        public void ScaleAboutPoint(Vector3 pivot, float factor)
        {
            factor = Mathf.Clamp(transform.localScale.x * factor, 0.01f, 10f) / Mathf.Max(transform.localScale.x, 1e-6f);
            if (Mathf.Abs(factor - 1f) < 1e-6f) return;
            transform.localScale *= factor;
            transform.position = pivot + (transform.position - pivot) * factor;
        }

        public void RotateAboutPointY(Vector3 pivot, float angleRad)
        {
            if (Mathf.Abs(angleRad) < 1e-6f) return;
            var v = transform.position - pivot;
            v = Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, Vector3.up) * v;
            transform.position = pivot + v;
            transform.Rotate(0f, angleRad * Mathf.Rad2Deg, 0f, Space.World);
        }

        /// <summary>
        /// Rotate the Earth around its own surface normal (<c>transform.up</c>) through <paramref name="pivot"/>
        /// so yaw turning never warps a tilted God's-eye wall view into a side roll.
        /// </summary>
        public void RotateAboutMapUp(Vector3 pivot, float angleRad)
        {
            if (Mathf.Abs(angleRad) < 1e-6f) return;
            RotateAboutAxis(pivot, transform.up, angleRad);
        }

        public void OrientNorth(Transform camera)
        {
            if (camera == null) return;
            var flatFwd = camera.forward;
            flatFwd.y = 0f;
            if (flatFwd.sqrMagnitude < 1e-6f) return;
            flatFwd.Normalize();

            // Project camera forward and map north (+Z) onto the map's own tangent plane
            // so OrientNorth works cleanly whether flat (0°) or tilted up in front (38°/62°).
            var mapUp = transform.up;
            var camOnMap = Vector3.ProjectOnPlane(flatFwd, mapUp);
            if (camOnMap.sqrMagnitude < 1e-6f)
                camOnMap = Vector3.ProjectOnPlane(camera.up, mapUp);
            if (camOnMap.sqrMagnitude < 1e-6f) return;
            camOnMap.Normalize();

            var mapNorth = transform.forward;
            float signedDeg = Vector3.SignedAngle(mapNorth, camOnMap, mapUp);
            var pivot = GetFocusPivot(camera);
            RotateAboutMapUp(pivot, signedDeg * Mathf.Deg2Rad);
        }

        public void RotateAboutAxis(Vector3 pivot, Vector3 axis, float angleRad)
        {
            if (Mathf.Abs(angleRad) < 1e-6f || axis.sqrMagnitude < 1e-8f) return;
            axis.Normalize();
            var rot = Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
            transform.position = pivot + rot * (transform.position - pivot);
            transform.rotation = rot * transform.rotation;
        }

        /// <summary>
        /// Computes the ground pivot point in front of the viewer so tilting the Earth
        /// swings the terrain UP in front of the user's eyes (like tilting up a tactical
        /// drafting table) instead of swinging the floor behind the user's back.
        /// </summary>
        public Vector3 GetFocusPivot(Transform camera)
        {
            if (camera == null) return transform.position;
            var camPos = camera.position;
            var mapUp = transform.up;
            float h = Mathf.Abs(Vector3.Dot(camPos - transform.position, mapUp));

            var flatFwd = camera.forward;
            flatFwd.y = 0f;
            if (flatFwd.sqrMagnitude < 1e-6f) flatFwd = Vector3.forward;
            flatFwd.Normalize();

            // Intersect a comfortable forward-down look ray (~35° below horizon) with the map plane
            var lookDir = (flatFwd * 1.4f - Vector3.up).normalized;
            float denom = Vector3.Dot(lookDir, mapUp);
            if (Mathf.Abs(denom) > 0.12f)
            {
                float t = Vector3.Dot(transform.position - camPos, mapUp) / denom;
                if (t > 1f && t < 15000f)
                    return camPos + lookDir * t;
            }

            float lookAhead = Mathf.Clamp(h * 1.15f, 20f, 2500f);
            var pt = camPos + flatFwd * lookAhead;
            float distToPlane = Vector3.Dot(pt - transform.position, mapUp);
            return pt - mapUp * distToPlane;
        }

        /// <summary>
        /// Pitch the world around a horizontal camera-right axis through the ground focus point
        /// in front of the viewer. Positive <paramref name="deltaDeg"/> tilts the far horizon UP
        /// toward the viewer so the Earth sits in front of you while standing upright.
        /// </summary>
        public void AddWorldPitch(Transform camera, float deltaDeg)
        {
            if (camera == null) return;
            float next = Mathf.Clamp(WorldPitchDeg + deltaDeg, MinPitchDeg, MaxPitchDeg);
            float applied = next - WorldPitchDeg;
            if (Mathf.Abs(applied) < 1e-4f) return;
            WorldPitchDeg = next;
            var pivot = GetFocusPivot(camera);
            // Negative angle around camera.right lifts the far (+forward) terrain UP toward eye level.
            RotateAboutAxis(pivot, PitchAxis(camera), -applied * Mathf.Deg2Rad);
        }

        /// <summary>Set absolute pitch (used on boot restore, presets, and Flatten).</summary>
        public void SetWorldPitch(Transform camera, float pitchDeg)
        {
            if (camera == null)
            {
                WorldPitchDeg = Mathf.Clamp(pitchDeg, MinPitchDeg, MaxPitchDeg);
                return;
            }
            AddWorldPitch(camera, Mathf.Clamp(pitchDeg, MinPitchDeg, MaxPitchDeg) - WorldPitchDeg);
        }

        public void Flatten(Transform camera) => SetWorldPitch(camera, 0f);

        /// <summary>Set absolute pitch about an arbitrary world point (level horizon = 0).</summary>
        public void SetPitchAboutPoint(Vector3 pivot, float pitchDeg)
        {
            var cam = Camera.main != null ? Camera.main.transform : null;
            if (cam != null)
            {
                SetWorldPitch(cam, pitchDeg);
                return;
            }
            float target = Mathf.Clamp(pitchDeg, MinPitchDeg, MaxPitchDeg);
            float applied = target - WorldPitchDeg;
            if (Mathf.Abs(applied) < 1e-4f) return;
            WorldPitchDeg = target;
            RotateAboutAxis(pivot, Vector3.right, -applied * Mathf.Deg2Rad);
        }

        static Vector3 PitchAxis(Transform camera)
        {
            var right = camera.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            return right.normalized;
        }
    }
}
