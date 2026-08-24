using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace TakXr.Xr
{
    /// <summary>
    /// Applies OpenXR HMD pose to this transform. Used instead of TrackedPoseDriver
    /// so we don't depend on an Input Action asset being wired at edit time.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class XrHeadPoseDriver : MonoBehaviour
    {
        [SerializeField] bool useCenterEye = true;

        readonly List<XRNodeState> _nodes = new List<XRNodeState>();

        void OnEnable()
        {
            Application.onBeforeRender += OnBeforeRender;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;
        }

        void Update() => ApplyPose();

        void OnBeforeRender() => ApplyPose();

        void ApplyPose()
        {
            InputTracking.GetNodeStates(_nodes);
            var node = useCenterEye ? XRNode.CenterEye : XRNode.Head;
            for (int i = 0; i < _nodes.Count; i++)
            {
                var st = _nodes[i];
                if (st.nodeType != node && st.nodeType != XRNode.Head) continue;
                if (st.TryGetPosition(out var pos))
                    transform.localPosition = pos;
                if (st.TryGetRotation(out var rot))
                    transform.localRotation = rot;
                return;
            }

            // Fallback: InputDevice API
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesAtXRNode(XRNode.Head, devices);
            if (devices.Count == 0) return;
            var d = devices[0];
            if (d.TryGetFeatureValue(CommonUsages.centerEyePosition, out var p) ||
                d.TryGetFeatureValue(CommonUsages.devicePosition, out p))
                transform.localPosition = p;
            if (d.TryGetFeatureValue(CommonUsages.centerEyeRotation, out var r) ||
                d.TryGetFeatureValue(CommonUsages.deviceRotation, out r))
                transform.localRotation = r;
        }
    }
}
