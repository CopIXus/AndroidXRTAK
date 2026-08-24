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
        /// Tabletop pitch in degrees. 0 = flat on the floor; positive pitches the
        /// near edge up (look forward at the map); negative pitches the far edge
        /// up (look down past the floor).
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

        public void OrientNorth(Transform camera)
        {
            if (camera == null) return;
            var flatFwd = camera.forward;
            flatFwd.y = 0f;
            if (flatFwd.sqrMagnitude < 1e-6f) return;
            flatFwd.Normalize();
            // Unity ENU: +Z = north. Align world +Z with camera forward.
            float yaw = Mathf.Atan2(flatFwd.x, flatFwd.z);
            RotateAboutPointY(camera.position, -yaw);
        }

        public void RotateAboutAxis(Vector3 pivot, Vector3 axis, float angleRad)
        {
            if (Mathf.Abs(angleRad) < 1e-6f || axis.sqrMagnitude < 1e-8f) return;
            axis.Normalize();
            var rot = Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
            transform.position = pivot + rot * (transform.position - pivot);
            transform.rotation = rot * transform.rotation;
        }

        /// <summary>Pitch the world toward the viewer around a horizontal camera-right axis.</summary>
        public void AddWorldPitch(Transform camera, float deltaDeg)
        {
            if (camera == null) return;
            float next = Mathf.Clamp(WorldPitchDeg + deltaDeg, MinPitchDeg, MaxPitchDeg);
            float applied = next - WorldPitchDeg;
            if (Mathf.Abs(applied) < 1e-4f) return;
            WorldPitchDeg = next;
            RotateAboutAxis(camera.position, PitchAxis(camera), applied * Mathf.Deg2Rad);
        }

        /// <summary>Set absolute pitch (used on boot restore and Flatten).</summary>
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

        static Vector3 PitchAxis(Transform camera)
        {
            var right = camera.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            return right.normalized;
        }
    }
}
