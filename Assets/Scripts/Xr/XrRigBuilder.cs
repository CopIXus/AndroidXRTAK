using Unity.XR.CoreUtils;
using UnityEngine;

namespace TakXr.Xr
{
    /// <summary>Builds XR Origin + tracked HMD camera at runtime.</summary>
    public static class XrRigBuilder
    {
        public static XROrigin EnsureRig(out Camera camera)
        {
            var existing = Object.FindFirstObjectByType<XROrigin>();
            if (existing != null && existing.Camera != null)
            {
                camera = existing.Camera;
                EnsureHeadDriver(camera);
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 300000f;
                return existing;
            }

            var originGo = new GameObject("XR Origin");
            var origin = originGo.AddComponent<XROrigin>();

            var offsetGo = new GameObject("Camera Offset");
            offsetGo.transform.SetParent(originGo.transform, false);

            var camGo = Camera.main != null ? Camera.main.gameObject : new GameObject("Main Camera");
            if (Camera.main == null)
            {
                camGo.AddComponent<Camera>();
                camGo.tag = "MainCamera";
                if (camGo.GetComponent<AudioListener>() == null)
                    camGo.AddComponent<AudioListener>();
            }

            camGo.transform.SetParent(offsetGo.transform, false);
            camGo.transform.localPosition = Vector3.zero;
            camGo.transform.localRotation = Quaternion.identity;

            camera = camGo.GetComponent<Camera>();
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 300000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.35f, 0.55f, 0.75f, 1f);

            origin.Camera = camera;
            origin.CameraFloorOffsetObject = offsetGo;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;

            EnsureHeadDriver(camera);
            return origin;
        }

        static void EnsureHeadDriver(Camera camera)
        {
            if (camera == null) return;
            if (camera.GetComponent<XrHeadPoseDriver>() == null)
                camera.gameObject.AddComponent<XrHeadPoseDriver>();
        }
    }
}
