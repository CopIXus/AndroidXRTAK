using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using ISCommon = UnityEngine.InputSystem.CommonUsages;
using ISDevice = UnityEngine.InputSystem.InputDevice;
using XRDevice = UnityEngine.XR.InputDevice;
#if TAKXR_HAS_XR_HANDS
using UnityEngine.XR.Hands;
#endif

namespace TakXr.Xr
{
    /// <summary>
    /// Samsung Galaxy XR input: OpenXR Hand Interaction + XR controllers.
    /// Aim picks the pose axis that points with the controller (HI palm +Z is often "up").
    /// Grab accepts pinch OR physical trigger/grip (HI-only path was swallowing triggers).
    /// </summary>
    public static class XrHandPinchInput
    {
        const float PinchEnter = 0.35f;
        const float PinchExit = 0.22f;
        const float TriggerEnter = 0.25f;
        const float JointPinchEnterM = 0.045f;
        const float JointPinchExitM = 0.07f;

        static readonly bool[] _pinchLatched = new bool[2];
        static readonly bool[] _uiBlocking = new bool[2];
        static readonly Vector3[] _aimAxisLocal = { Vector3.zero, Vector3.zero };
        static bool _loggedDevices;
        static float _nextFeatureLog;
        static float _nextAimLog;

        public static void SetUiBlocking(int handIndex, bool blocking)
        {
            if (handIndex >= 0 && handIndex < 2) _uiBlocking[handIndex] = blocking;
        }

        public static bool IsUiBlocking(int handIndex) =>
            handIndex >= 0 && handIndex < 2 && _uiBlocking[handIndex];

        public static bool TryGetPose(int handIndex, out Vector3 worldPos, out Quaternion worldRot)
        {
            worldPos = Vector3.zero;
            worldRot = Quaternion.identity;
            MaybeLogDevices();

            if (TryControllerPose(handIndex, out worldPos, out worldRot))
                return true;
            if (TryInputSystemPose(handIndex, preferPointer: false, out worldPos, out worldRot))
                return true;
            if (TryXrDevicePose(handIndex, out worldPos, out worldRot))
                return true;
#if TAKXR_HAS_XR_HANDS
            if (TryXrHandPose(handIndex, out worldPos, out worldRot))
                return true;
#endif
            return TryXrNodePose(handIndex, out worldPos, out worldRot);
        }

        public static bool IsGrabbing(int handIndex)
        {
            // Controllers first — Galaxy XR physical trigger must work even when
            // a Hand Interaction device is also present.
            if (TryControllerGrab(handIndex, out bool ctrlPressed) && ctrlPressed)
            {
                _pinchLatched[handIndex] = true;
                return true;
            }

            if (TryXrDeviceButtons(handIndex, out bool xrPressed) && xrPressed)
            {
                _pinchLatched[handIndex] = true;
                return true;
            }

            if (TryHandInteractionGrab(handIndex, out bool hiPressed))
            {
                if (hiPressed)
                {
                    _pinchLatched[handIndex] = true;
                    return true;
                }
            }

#if TAKXR_HAS_XR_HANDS
            if (TryXrHandPinchDistance(handIndex, out float dist))
            {
                if (_pinchLatched[handIndex])
                    _pinchLatched[handIndex] = dist < JointPinchExitM;
                else
                    _pinchLatched[handIndex] = dist < JointPinchEnterM;
                if (_pinchLatched[handIndex]) return true;
            }
#endif
            _pinchLatched[handIndex] = false;
            return false;
        }

        public static bool TryGetGrab(int handIndex, out Vector3 worldPos, out bool grabbing)
        {
            grabbing = false;
            worldPos = Vector3.zero;
            if (!TryGetPose(handIndex, out worldPos, out _))
                return false;
            grabbing = IsGrabbing(handIndex);
            return grabbing;
        }

        /// <summary>
        /// Pointing ray for select / UI. Chooses the pose local axis that best matches
        /// where the user is looking (fixes Samsung HI poses that aim +Y / "straight up").
        /// </summary>
        public static bool TryGetAim(int handIndex, out Vector3 origin, out Vector3 forward)
        {
            origin = Vector3.zero;
            forward = Vector3.forward;

            Quaternion rot;
            bool havePose = TryControllerPose(handIndex, out origin, out rot)
                            || TryInputSystemPose(handIndex, preferPointer: true, out origin, out rot)
                            || TryInputSystemPose(handIndex, preferPointer: false, out origin, out rot)
                            || TryXrDevicePose(handIndex, out origin, out rot)
                            || TryGetPose(handIndex, out origin, out rot);
            if (!havePose) return false;

            forward = ResolveAimForward(handIndex, origin, rot);

            if (Time.unscaledTime >= _nextAimLog)
            {
                _nextAimLog = Time.unscaledTime + 2.5f;
                Debug.Log($"[TakXr] aim[{handIndex}] origin={origin} fwd={forward} axis={_aimAxisLocal[handIndex]}");
            }

            return true;
        }

        static Vector3 ResolveAimForward(int handIndex, Vector3 origin, Quaternion rot)
        {
            var cam = Camera.main != null ? Camera.main.transform : null;
            // Prefer camera look so pointing at the map (looking down) works.
            Vector3 preferred = cam != null ? cam.forward : Vector3.forward;
            // Soften: also consider flat look so "controllers out" prefers horizontal.
            var flat = preferred;
            flat.y = 0f;
            if (flat.sqrMagnitude > 1e-4f)
                preferred = Vector3.Slerp(preferred.normalized, flat.normalized, 0.35f).normalized;

            Vector3[] locals =
            {
                Vector3.forward, Vector3.back,
                Vector3.up, Vector3.down,
                Vector3.right, Vector3.left
            };

            // Sticky axis once chosen (avoids ray flicker) unless clearly wrong.
            var sticky = _aimAxisLocal[handIndex];
            if (sticky.sqrMagnitude > 0.5f)
            {
                var stickyWorld = (rot * sticky).normalized;
                if (Vector3.Dot(stickyWorld, preferred) > 0.25f)
                    return stickyWorld;
            }

            float bestDot = -2f;
            Vector3 bestLocal = Vector3.forward;
            Vector3 bestWorld = rot * Vector3.forward;
            foreach (var local in locals)
            {
                var world = (rot * local).normalized;
                // Prefer aiming away from the user's head (not back at face).
                if (cam != null)
                {
                    var fromHead = (origin - cam.position).normalized;
                    if (Vector3.Dot(world, fromHead) < -0.2f) continue; // pointing back at head-ish
                }
                float d = Vector3.Dot(world, preferred);
                if (d > bestDot)
                {
                    bestDot = d;
                    bestLocal = local;
                    bestWorld = world;
                }
            }

            _aimAxisLocal[handIndex] = bestLocal;
            return bestWorld;
        }

        public static bool TryGetStick(int handIndex, out Vector2 stick)
        {
            stick = Vector2.zero;

            foreach (var d in InputSystem.devices)
            {
                if (!MatchesInputSystemSide(d, handIndex)) continue;
                if (ReadStickControl(d, out stick) && stick.sqrMagnitude > 1e-6f)
                    return true;
            }

            // XRController / tracked devices that aren't Hand Interaction.
            foreach (var d in InputSystem.devices)
            {
                if (d is not XRController && !LooksLikeController(d)) continue;
                if (!MatchesInputSystemSide(d, handIndex)) continue;
                if (ReadStickControl(d, out stick) && stick.sqrMagnitude > 1e-6f)
                    return true;
            }

            var devices = new List<XRDevice>();
            InputDevices.GetDevices(devices);
            foreach (var d in devices)
            {
                if (!MatchesXrSide(d, handIndex)) continue;
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out stick) &&
                    stick.sqrMagnitude > 1e-6f)
                    return true;
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondary2DAxis, out stick) &&
                    stick.sqrMagnitude > 1e-6f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// B/Y (secondaryButton) or menu — hold then use the stick to pitch the world.
        /// Keyboard Left/Right Shift is the editor fallback.
        /// </summary>
        public static bool IsTiltModifierHeld()
        {
            for (int h = 0; h < 2; h++)
            {
                if (IsTiltModifierHeld(h)) return true;
            }
            var kb = Keyboard.current;
            if (kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed))
                return true;
            return false;
        }

        public static bool IsTiltModifierHeld(int handIndex)
        {
            foreach (var d in InputSystem.devices)
            {
                if (!MatchesInputSystemSide(d, handIndex)) continue;
                if (ReadButton(d, "secondaryButton") || ReadButton(d, "menuButton") ||
                    ReadButton(d, "secondaryPressed"))
                    return true;
            }
            var devices = new List<XRDevice>();
            InputDevices.GetDevices(devices);
            foreach (var d in devices)
            {
                if (!MatchesXrSide(d, handIndex)) continue;
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool sb) && sb)
                    return true;
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.menuButton, out bool mb) && mb)
                    return true;
            }
            return false;
        }

        public static bool HasStick(int handIndex)
        {
            foreach (var d in InputSystem.devices)
            {
                if (!MatchesInputSystemSide(d, handIndex)) continue;
                if (HasStickControl(d)) return true;
            }
            var devices = new List<XRDevice>();
            InputDevices.GetDevices(devices);
            foreach (var d in devices)
            {
                if (!MatchesXrSide(d, handIndex)) continue;
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out _))
                    return true;
            }
            return false;
        }

        public static string DebugGrabState(int handIndex)
        {
            var parts = new List<string>();
            if (TryFindHandInteraction(handIndex, out var hi) && hi != null)
            {
                parts.Add($"HI p={ReadAxis(hi, "pinchValue"):F2} a={ReadAxis(hi, "pointerActivateValue"):F2} " +
                          $"g={ReadAxis(hi, "graspValue"):F2}");
            }
            else parts.Add("no-HI");

            if (TryFindControllerDevice(handIndex, out var c) && c != null)
            {
                parts.Add($"CTRL trig={ReadAxis(c, "trigger"):F2}/{ReadAxis(c, "triggerPressed"):F2} " +
                          $"grip={ReadAxis(c, "grip"):F2} stick={ReadStickDebug(c)}");
            }
            else parts.Add("no-CTRL");

            return string.Join(" | ", parts);
        }

        static string ReadStickDebug(ISDevice d)
        {
            return ReadStickControl(d, out var s) ? s.ToString() : "none";
        }

        static bool LooksLikeController(ISDevice d)
        {
            var layout = d.layout ?? "";
            var name = ((d.displayName ?? "") + " " + (d.name ?? "")).ToLowerInvariant();
            if (layout.IndexOf("HandInteraction", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return layout.IndexOf("XRController", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || layout.IndexOf("Controller", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.Contains("controller")
                   || name.Contains("touch")
                   || name.Contains("oculus")
                   || name.Contains("android xr");
        }

        static bool TryControllerPose(int handIndex, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (!TryFindControllerDevice(handIndex, out var d) || d == null)
                return false;

            var pp = d.TryGetChildControl<Vector3Control>("pointerPosition")
                     ?? d.TryGetChildControl<Vector3Control>("devicePosition")
                     ?? d.TryGetChildControl<Vector3Control>("gripPosition");
            var pr = d.TryGetChildControl<QuaternionControl>("pointerRotation")
                     ?? d.TryGetChildControl<QuaternionControl>("deviceRotation")
                     ?? d.TryGetChildControl<QuaternionControl>("gripRotation");
            if (pp == null || pr == null) return false;
            pos = pp.ReadValue();
            rot = pr.ReadValue();
            return pos.sqrMagnitude > 0.01f;
        }

        static bool TryControllerGrab(int handIndex, out bool pressed)
        {
            pressed = false;
            bool found = false;

            // Any Input System device on this hand with a trigger/grip (not HI-only).
            foreach (var dev in InputSystem.devices)
            {
                if (!MatchesInputSystemSide(dev, handIndex)) continue;
                found = true;
                float trigger = Mathf.Max(
                    ReadAxis(dev, "trigger"),
                    ReadAxis(dev, "triggerPressed"),
                    ReadAxis(dev, "triggerButton"),
                    ReadAxis(dev, "pointerActivateValue"));
                float grip = Mathf.Max(
                    ReadAxis(dev, "grip"),
                    ReadAxis(dev, "gripPressed"),
                    ReadAxis(dev, "gripButton"),
                    ReadAxis(dev, "graspValue"),
                    ReadAxis(dev, "pinchValue"));
                bool btn = ReadButton(dev, "triggerPressed") || ReadButton(dev, "triggerButton")
                           || ReadButton(dev, "gripPressed") || ReadButton(dev, "gripButton")
                           || ReadButton(dev, "primaryButton")
                           || ReadButton(dev, "pointerActivated") || ReadButton(dev, "pinchTouched")
                           || ReadButton(dev, "graspFirm");
                // secondaryButton (B/Y) is the world-tilt modifier — not grab.
                if (btn || trigger > TriggerEnter || grip > TriggerEnter)
                {
                    pressed = true;
                    return true;
                }
            }

            return found;
        }

        static bool TryHandInteractionGrab(int handIndex, out bool pressed)
        {
            pressed = false;
            if (!TryFindHandInteraction(handIndex, out var d) || d == null)
                return false;

            float pinch = ReadAxis(d, "pinchValue");
            float activate = ReadAxis(d, "pointerActivateValue");
            float grasp = ReadAxis(d, "graspValue");
            bool btn = ReadButton(d, "pinchTouched") || ReadButton(d, "pointerActivated") ||
                       ReadButton(d, "graspFirm");

            float strength = Mathf.Max(pinch, Mathf.Max(activate, grasp));
            if (_pinchLatched[handIndex])
                pressed = btn || strength > PinchExit;
            else
                pressed = btn || strength > PinchEnter;

            if (Time.unscaledTime >= _nextFeatureLog)
            {
                _nextFeatureLog = Time.unscaledTime + 2f;
                Debug.Log($"[TakXr] grab[{handIndex}] {DebugGrabState(handIndex)} hi={pressed}");
            }

            return true;
        }

        static bool TryFindControllerDevice(int handIndex, out ISDevice device)
        {
            device = null;
            foreach (var d in InputSystem.devices)
            {
                if (!LooksLikeController(d) && d is not XRController) continue;
                if (!MatchesInputSystemSide(d, handIndex)) continue;
                device = d;
                return true;
            }
            return false;
        }

        static bool HasStickControl(ISDevice d)
        {
            string[] names = { "thumbstick", "joystick", "primary2DAxis", "secondary2DAxis", "touchpad", "trackpad" };
            foreach (var n in names)
                if (d.TryGetChildControl<Vector2Control>(n) != null) return true;
            return false;
        }

        static void MaybeLogDevices()
        {
            if (_loggedDevices) return;
            _loggedDevices = true;
            var xr = new List<XRDevice>();
            InputDevices.GetDevices(xr);
            Debug.Log($"[TakXr] XR InputDevices ({xr.Count}):");
            foreach (var d in xr)
                Debug.Log($"[TakXr]  · XR {d.name} | {d.characteristics}");
            Debug.Log($"[TakXr] InputSystem devices ({InputSystem.devices.Count}):");
            foreach (var d in InputSystem.devices)
                Debug.Log($"[TakXr]  · IS '{d.displayName}' layout={d.layout} usages={UsagesString(d)}");
        }

        static string UsagesString(ISDevice d)
        {
            var parts = new List<string>();
            foreach (var u in d.usages) parts.Add(u.ToString());
            return string.Join(",", parts);
        }

        static bool TryInputSystemPose(int handIndex, bool preferPointer, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (!TryFindHandInteraction(handIndex, out var d) || d == null)
                return false;

            if (preferPointer)
            {
                var pp = d.TryGetChildControl<Vector3Control>("pointerPosition");
                var pr = d.TryGetChildControl<QuaternionControl>("pointerRotation");
                if (pp != null && pr != null)
                {
                    pos = pp.ReadValue();
                    rot = pr.ReadValue();
                    // Reject origin/identity junk that made rays vanish or stick at world zero.
                    if (pos.sqrMagnitude > 0.01f) return true;
                }
            }

            var dp = d.TryGetChildControl<Vector3Control>("devicePosition")
                     ?? d.TryGetChildControl<Vector3Control>("gripPosition");
            var dr = d.TryGetChildControl<QuaternionControl>("deviceRotation")
                     ?? d.TryGetChildControl<QuaternionControl>("gripRotation");
            if (dp == null || dr == null) return false;
            pos = dp.ReadValue();
            rot = dr.ReadValue();
            return pos.sqrMagnitude > 0.01f;
        }

        static bool TryFindHandInteraction(int handIndex, out ISDevice device)
        {
            device = null;
            foreach (var d in InputSystem.devices)
            {
                var layout = d.layout ?? "";
                var name = d.displayName ?? d.name ?? "";
                bool isHi = layout.IndexOf("HandInteraction", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("Hand Interaction", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isHi) continue;
                if (!MatchesInputSystemSide(d, handIndex)) continue;
                device = d;
                return true;
            }
            return false;
        }

        static bool MatchesInputSystemSide(ISDevice d, int handIndex)
        {
            bool wantLeft = handIndex == 0;
            foreach (var u in d.usages)
            {
                if (wantLeft && u == ISCommon.LeftHand) return true;
                if (!wantLeft && u == ISCommon.RightHand) return true;
            }
            var n = ((d.displayName ?? "") + " " + (d.name ?? "")).ToLowerInvariant();
            if (wantLeft && n.Contains("left")) return true;
            if (!wantLeft && n.Contains("right")) return true;
            return false;
        }

        static bool ReadStickControl(ISDevice d, out Vector2 stick)
        {
            stick = Vector2.zero;
            string[] names = { "thumbstick", "joystick", "primary2DAxis", "secondary2DAxis", "touchpad", "trackpad" };
            foreach (var n in names)
            {
                var c = d.TryGetChildControl<Vector2Control>(n);
                if (c == null) continue;
                stick = c.ReadValue();
                return true; // control exists even at zero
            }
            return false;
        }

        static float ReadAxis(ISDevice d, string name)
        {
            var c = d.TryGetChildControl<AxisControl>(name);
            if (c != null) return c.ReadValue();
            var b = d.TryGetChildControl<ButtonControl>(name);
            if (b != null) return b.ReadValue();
            return 0f;
        }

        static bool ReadButton(ISDevice d, string name)
        {
            var c = d.TryGetChildControl<ButtonControl>(name);
            return c != null && c.isPressed;
        }

#if TAKXR_HAS_XR_HANDS
        static XRHandSubsystem ActiveHands()
        {
            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            for (int i = 0; i < subsystems.Count; i++)
                if (subsystems[i] != null && subsystems[i].running)
                    return subsystems[i];
            return null;
        }

        static bool TryXrHandPose(int handIndex, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var sub = ActiveHands();
            if (sub == null) return false;
            var hand = handIndex == 0 ? sub.leftHand : sub.rightHand;
            if (!hand.isTracked) return false;
            var tip = hand.GetJoint(XRHandJointID.IndexTip);
            if (tip.TryGetPose(out var tipPose))
            {
                pos = tipPose.position;
                rot = tipPose.rotation;
                return true;
            }
            return false;
        }

        static bool TryXrHandPinchDistance(int handIndex, out float dist)
        {
            dist = float.MaxValue;
            var sub = ActiveHands();
            if (sub == null) return false;
            var hand = handIndex == 0 ? sub.leftHand : sub.rightHand;
            if (!hand.isTracked) return false;
            var index = hand.GetJoint(XRHandJointID.IndexTip);
            var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
            if (!index.TryGetPose(out var iPose) || !thumb.TryGetPose(out var tPose))
                return false;
            dist = Vector3.Distance(iPose.position, tPose.position);
            return true;
        }
#endif

        static bool TryXrDeviceButtons(int handIndex, out bool pressed)
        {
            pressed = false;
            var devices = new List<XRDevice>();
            InputDevices.GetDevices(devices);
            foreach (var d in devices)
            {
                if (!MatchesXrSide(d, handIndex)) continue;
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool tb) && tb)
                { pressed = true; return true; }
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool gb) && gb)
                { pressed = true; return true; }
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float t) && t > TriggerEnter)
                { pressed = true; return true; }
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float g) && g > TriggerEnter)
                { pressed = true; return true; }
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool pb) && pb)
                { pressed = true; return true; }
                if (d.TryGetFeatureValue(new InputFeatureUsage<float>("pinchValue"), out float pv) && pv > PinchEnter)
                { pressed = true; return true; }
                if (d.TryGetFeatureValue(new InputFeatureUsage<float>("pointerActivateValue"), out float av) && av > PinchEnter)
                { pressed = true; return true; }
            }
            return false;
        }

        static bool TryXrDevicePose(int handIndex, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var devices = new List<XRDevice>();
            InputDevices.GetDevices(devices);
            foreach (var d in devices)
            {
                if (!MatchesXrSide(d, handIndex)) continue;
                if (d.TryGetFeatureValue(new InputFeatureUsage<Vector3>("pointerPosition"), out pos) &&
                    d.TryGetFeatureValue(new InputFeatureUsage<Quaternion>("pointerRotation"), out rot))
                    return true;
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out pos))
                {
                    d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out rot);
                    return true;
                }
            }
            return false;
        }

        static bool TryXrNodePose(int handIndex, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var node = handIndex == 0 ? XRNode.LeftHand : XRNode.RightHand;
            var states = new List<XRNodeState>();
            InputTracking.GetNodeStates(states);
            foreach (var st in states)
            {
                if (st.nodeType != node) continue;
                if (!st.TryGetPosition(out pos)) continue;
                st.TryGetRotation(out rot);
                return true;
            }
            return false;
        }

        static bool MatchesXrSide(XRDevice d, int handIndex)
        {
            bool wantLeft = handIndex == 0;
            var chars = d.characteristics;
            if (wantLeft && (chars & InputDeviceCharacteristics.Left) != 0) return true;
            if (!wantLeft && (chars & InputDeviceCharacteristics.Right) != 0) return true;
            var name = (d.name ?? "").ToLowerInvariant();
            if (wantLeft && name.Contains("left")) return true;
            if (!wantLeft && name.Contains("right")) return true;
            return false;
        }
    }
}
