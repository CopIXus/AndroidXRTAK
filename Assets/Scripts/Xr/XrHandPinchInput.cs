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
    /// Samsung Galaxy XR unified input:
    /// - Works seamlessly WITH or WITHOUT physical controllers (Google Maps XR parity).
    /// - Bare-hand tracking (`XRHandSubsystem` + OpenXR `HandInteraction` + `HandInteractionPoses` + `PalmPose`)
    /// - Eye Gaze Tracking (`EyeTrackingOpenXR` / `XR_EXT_eye_gaze_interaction`) with head-gaze fallback,
    ///   so looking at any UI button or CoT and pinching your fingers at your side selects it effortlessly.
    /// - Physical 6DoF XR controllers (trigger, grip, thumbsticks).
    /// </summary>
    public static class XrHandPinchInput
    {
        const float PinchEnter = 0.30f;
        const float PinchExit = 0.18f;
        const float TriggerEnter = 0.25f;
        // 3.2 cm pinch enter / 5.0 cm exit feels snappy and reliable for bare-hand index+thumb pinch.
        const float JointPinchEnterM = 0.032f;
        const float JointPinchExitM = 0.052f;

        static readonly bool[] _pinchLatched = new bool[2];
        static readonly bool[] _uiBlocking = new bool[2];
        static readonly Vector3[] _aimAxisLocal = { Vector3.zero, Vector3.zero };
        static readonly Vector3[] _smoothedHandPos = { Vector3.zero, Vector3.zero };
        static readonly Vector3[] _smoothedAimFwd = { Vector3.forward, Vector3.forward };
        static bool _loggedDevices;
        static float _nextFeatureLog;
        static float _nextAimLog;

        /// <summary>
        /// When true (default), bare-hand pointing blends/assists with Eye Gaze when looking at
        /// UI buttons or CoTs so users can look &amp; pinch comfortably without raising their arm.
        /// </summary>
        public static bool EyeGazeAssistEnabled { get; set; } = true;

        public static void SetUiBlocking(int handIndex, bool blocking)
        {
            if (handIndex >= 0 && handIndex < 2) _uiBlocking[handIndex] = blocking;
        }

        public static bool IsUiBlocking(int handIndex) =>
            handIndex >= 0 && handIndex < 2 && _uiBlocking[handIndex];

        /// <summary>
        /// True when a physical motion controller with a thumbstick is actively tracked on either hand.
        /// False when operating controller-free (bare hands + eye gaze).
        /// </summary>
        public static bool IsUsingPhysicalControllers()
        {
            return HasStick(0) || HasStick(1);
        }

        /// <summary>
        /// Reads the user's Eye Gaze ray in world space (OpenXR Eye Gaze Interaction),
        /// falling back to head center-eye gaze if eye tracking is calibrating or unavailable.
        /// </summary>
        public static bool TryGetEyeGazeRay(out Vector3 origin, out Vector3 forward, out bool isTrueEyeTracked)
        {
            origin = Vector3.zero;
            forward = Vector3.forward;
            isTrueEyeTracked = false;

            // 1. InputSystem EyeTrackingOpenXR device
            foreach (var d in InputSystem.devices)
            {
                var layout = d.layout ?? "";
                var name = d.displayName ?? d.name ?? "";
                if (layout.IndexOf("EyeTracking", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("Eye Tracking", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("EyeGaze", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var posCtrl = d.TryGetChildControl<Vector3Control>("posePosition")
                              ?? d.TryGetChildControl<Vector3Control>("devicePosition")
                              ?? d.TryGetChildControl<Vector3Control>("gazePosition")
                              ?? d.TryGetChildControl<Vector3Control>("pointerPosition");
                var rotCtrl = d.TryGetChildControl<QuaternionControl>("poseRotation")
                              ?? d.TryGetChildControl<QuaternionControl>("deviceRotation")
                              ?? d.TryGetChildControl<QuaternionControl>("gazeRotation")
                              ?? d.TryGetChildControl<QuaternionControl>("pointerRotation");
                if (posCtrl != null && rotCtrl != null)
                {
                    var p = posCtrl.ReadValue();
                    var r = rotCtrl.ReadValue();
                    if (p.sqrMagnitude > 0.001f || Quaternion.Angle(r, Quaternion.identity) > 0.05f)
                    {
                        var cam = Camera.main != null ? Camera.main.transform : null;
                        origin = p.sqrMagnitude > 0.001f ? p : (cam != null ? cam.position : Vector3.zero);
                        forward = (r * Vector3.forward).normalized;
                        isTrueEyeTracked = true;
                        return true;
                    }
                }
            }

            // 2. UnityEngine.XR EyeTracking device fallback
            var xrDevs = new List<XRDevice>();
            InputDevices.GetDevices(xrDevs);
            foreach (var d in xrDevs)
            {
                if ((d.characteristics & InputDeviceCharacteristics.EyeTracking) == 0 &&
                    (d.name == null || d.name.IndexOf("Eye", System.StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out Quaternion erot) &&
                    Quaternion.Angle(erot, Quaternion.identity) > 0.05f)
                {
                    var cam = Camera.main != null ? Camera.main.transform : null;
                    if (!d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out origin) ||
                        origin.sqrMagnitude < 0.001f)
                        origin = cam != null ? cam.position : Vector3.zero;
                    forward = (erot * Vector3.forward).normalized;
                    isTrueEyeTracked = true;
                    return true;
                }
            }

            // 3. Head Center-Eye fallback
            if (Camera.main != null)
            {
                origin = Camera.main.transform.position;
                forward = Camera.main.transform.forward;
                return true;
            }

            return false;
        }

        public static bool TryGetPose(int handIndex, out Vector3 worldPos, out Quaternion worldRot)
        {
            worldPos = Vector3.zero;
            worldRot = Quaternion.identity;
            MaybeLogDevices();

            bool ok = TryControllerPose(handIndex, out worldPos, out worldRot)
#if TAKXR_HAS_XR_HANDS
                      || TryXrHandPose(handIndex, out worldPos, out worldRot)
#endif
                      || TryInputSystemPose(handIndex, preferPointer: true, out worldPos, out worldRot)
                      || TryInputSystemPose(handIndex, preferPointer: false, out worldPos, out worldRot)
                      || TryXrDevicePose(handIndex, out worldPos, out worldRot)
                      || TryXrNodePose(handIndex, out worldPos, out worldRot);

            if (ok)
            {
                // Light exponential smoothing removes optical hand-tracking jitter during map drags.
                if (_smoothedHandPos[handIndex].sqrMagnitude < 1e-4f)
                    _smoothedHandPos[handIndex] = worldPos;
                else
                    _smoothedHandPos[handIndex] = Vector3.Lerp(_smoothedHandPos[handIndex], worldPos, 0.55f);
                worldPos = _smoothedHandPos[handIndex];
            }
            return ok;
        }

        public static bool IsGrabbing(int handIndex)
        {
            // 1. Physical Controller trigger/grip
            if (TryControllerGrab(handIndex, out bool ctrlPressed) && ctrlPressed)
            {
                _pinchLatched[handIndex] = true;
                return true;
            }

            // 2. XRDevice buttons / pinch features
            if (TryXrDeviceButtons(handIndex, out bool xrPressed) && xrPressed)
            {
                _pinchLatched[handIndex] = true;
                return true;
            }

            // 3. OpenXR HandInteraction / HandInteractionPoses (pinch / activate / grasp)
            if (TryHandInteractionGrab(handIndex, out bool hiPressed) && hiPressed)
            {
                _pinchLatched[handIndex] = true;
                return true;
            }

#if TAKXR_HAS_XR_HANDS
            // 4. XRHandSubsystem direct joint pinch (IndexTip ↔ ThumbTip distance)
            if (TryXrHandPinchDistance(handIndex, out float dist))
            {
                bool latched = _pinchLatched[handIndex]
                    ? dist < JointPinchExitM
                    : dist < JointPinchEnterM;
                if (latched)
                {
                    _pinchLatched[handIndex] = true;
                    return true;
                }
            }
#endif
            _pinchLatched[handIndex] = false;
            return false;
        }

        public static bool TryGetGrab(int handIndex, out Vector3 worldPos, out bool grabbing)
        {
            grabbing = IsGrabbing(handIndex);
            if (!TryGetPose(handIndex, out worldPos, out _))
            {
                // Even if the hand is resting low out of camera FOV, if the system reported a pinch
                // (or eye-gaze pinch), provide a comfortable virtual hand anchor below the headset.
                if (grabbing && Camera.main != null)
                {
                    var cam = Camera.main.transform;
                    float side = handIndex == 0 ? -0.18f : 0.18f;
                    worldPos = cam.position + cam.TransformVector(new Vector3(side, -0.25f, 0.35f));
                    return true;
                }
                return false;
            }
            return grabbing;
        }

        /// <summary>
        /// Pointing ray for UI, CoT selection, and Google Maps-style ray-anchor dragging.
        /// - When a physical controller or raised hand ray is aimed at a target, uses that ray.
        /// - When operating bare-handed and Eye Gaze is looking at a UI button or CoT (or the
        ///   hand is resting casually in the lap), seamlessly snaps/blends to Eye Gaze so
        ///   "Look + Pinch" works just like Android XR system navigation and Google Maps XR.
        /// </summary>
        public static bool TryGetAim(int handIndex, out Vector3 origin, out Vector3 forward)
        {
            origin = Vector3.zero;
            forward = Vector3.forward;

            Quaternion rot = Quaternion.identity;
            bool haveHandPose = TryControllerPose(handIndex, out origin, out rot)
                                || TryInputSystemPose(handIndex, preferPointer: true, out origin, out rot)
#if TAKXR_HAS_XR_HANDS
                                || TryXrHandAimPose(handIndex, out origin, out rot)
#endif
                                || TryInputSystemPose(handIndex, preferPointer: false, out origin, out rot)
                                || TryXrDevicePose(handIndex, out origin, out rot)
                                || TryGetPose(handIndex, out origin, out rot);

            bool haveEye = TryGetEyeGazeRay(out var eyeOrigin, out var eyeFwd, out _);

            if (!haveHandPose)
            {
                // Hand out of camera view or controller off: use Eye Gaze ray for handIndex 1 (primary)
                // so Look + Pinch or head-gaze selection always works.
                if (haveEye && handIndex == 1)
                {
                    origin = eyeOrigin;
                    forward = eyeFwd;
                    return true;
                }
                return false;
            }

            forward = ResolveAimForward(handIndex, origin, rot);

            // Bare-hand Eye-Gaze Assist: if no physical controller is held and the user's eyes
            // are looking directly at a UI collider (dock button, tray button, radial menu) while
            // the hand ray is NOT pointing at UI (e.g. hand resting low), route the aim to Eye Gaze!
            if (EyeGazeAssistEnabled && haveEye && !IsUsingPhysicalControllers())
            {
                bool handHitsUi = RayHitsTriggerUi(origin, forward);
                if (!handHitsUi && RayHitsTriggerUi(eyeOrigin, eyeFwd))
                {
                    origin = eyeOrigin;
                    forward = eyeFwd;
                    return true;
                }
            }

            if (_smoothedAimFwd[handIndex].sqrMagnitude < 1e-4f)
                _smoothedAimFwd[handIndex] = forward;
            else
                _smoothedAimFwd[handIndex] = Vector3.Slerp(_smoothedAimFwd[handIndex], forward, 0.5f).normalized;
            forward = _smoothedAimFwd[handIndex];

            if (Time.unscaledTime >= _nextAimLog)
            {
                _nextAimLog = Time.unscaledTime + 4.0f;
                Debug.Log($"[TakXr] aim[{handIndex}] origin={origin} fwd={forward} axis={_aimAxisLocal[handIndex]}");
            }

            return true;
        }

        static bool RayHitsTriggerUi(Vector3 origin, Vector3 fwd)
        {
            var hits = Physics.RaycastAll(origin, fwd, 6.5f, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider != null && hits[i].collider.isTrigger)
                    return true;
            }
            return false;
        }

        static Vector3 ResolveAimForward(int handIndex, Vector3 origin, Quaternion rot)
        {
            var cam = Camera.main != null ? Camera.main.transform : null;
            Vector3 preferred = cam != null ? cam.forward : Vector3.forward;
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
                if (cam != null)
                {
                    var fromHead = (origin - cam.position).normalized;
                    if (Vector3.Dot(world, fromHead) < -0.2f) continue;
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

#if TAKXR_HAS_XR_HANDS
            if (TryXrHandPinchDistance(handIndex, out float jDist))
                parts.Add($"XRHands d={jDist * 100f:F1}cm");
#endif

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
            if (layout.IndexOf("HandInteraction", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                layout.IndexOf("PalmPose", System.StringComparison.OrdinalIgnoreCase) >= 0)
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

            foreach (var dev in InputSystem.devices)
            {
                if (!MatchesInputSystemSide(dev, handIndex)) continue;
                found = true;
                float trigger = Mathf.Max(
                    ReadAxis(dev, "trigger"),
                    ReadAxis(dev, "triggerPressed"),
                    ReadAxis(dev, "triggerButton"),
                    ReadAxis(dev, "pointerActivateValue"),
                    ReadAxis(dev, "selectValue"));
                float grip = Mathf.Max(
                    ReadAxis(dev, "grip"),
                    ReadAxis(dev, "gripPressed"),
                    ReadAxis(dev, "gripButton"),
                    ReadAxis(dev, "graspValue"),
                    ReadAxis(dev, "pinchValue"),
                    ReadAxis(dev, "squeezeValue"));
                bool btn = ReadButton(dev, "triggerPressed") || ReadButton(dev, "triggerButton")
                           || ReadButton(dev, "gripPressed") || ReadButton(dev, "gripButton")
                           || ReadButton(dev, "primaryButton") || ReadButton(dev, "selectPressed")
                           || ReadButton(dev, "pointerActivated") || ReadButton(dev, "pinchTouched")
                           || ReadButton(dev, "pinchPressed") || ReadButton(dev, "pokePressed")
                           || ReadButton(dev, "graspFirm");
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
            bool anyDevice = false;
            float maxStrength = 0f;
            bool anyBtn = false;

            foreach (var d in InputSystem.devices)
            {
                if (!IsHandOrPalmDevice(d)) continue;
                if (!MatchesInputSystemSide(d, handIndex)) continue;
                anyDevice = true;

                float pinch = Mathf.Max(ReadAxis(d, "pinchValue"), ReadAxis(d, "selectValue"));
                float activate = ReadAxis(d, "pointerActivateValue");
                float grasp = Mathf.Max(ReadAxis(d, "graspValue"), ReadAxis(d, "squeezeValue"));
                bool btn = ReadButton(d, "pinchTouched") || ReadButton(d, "pinchPressed")
                           || ReadButton(d, "pointerActivated") || ReadButton(d, "selectPressed")
                           || ReadButton(d, "graspFirm");

                maxStrength = Mathf.Max(maxStrength, Mathf.Max(pinch, Mathf.Max(activate, grasp)));
                if (btn) anyBtn = true;
            }

            if (!anyDevice) return false;

            if (_pinchLatched[handIndex])
                pressed = anyBtn || maxStrength > PinchExit;
            else
                pressed = anyBtn || maxStrength > PinchEnter;

            if (Time.unscaledTime >= _nextFeatureLog)
            {
                _nextFeatureLog = Time.unscaledTime + 3f;
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
                if (IsHandOrPalmDevice(d)) continue;
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

        static bool IsHandOrPalmDevice(ISDevice d)
        {
            var layout = d.layout ?? "";
            var name = d.displayName ?? d.name ?? "";
            return layout.IndexOf("HandInteraction", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || layout.IndexOf("PalmPose", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Hand Interaction", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Palm Pose", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool TryInputSystemPose(int handIndex, bool preferPointer, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;

            // Scan ALL HandInteraction / HandInteractionPoses / PalmPose devices for this hand,
            // because on Galaxy XR `HandInteractionOpenXR` may have (0,0,0) while
            // `HandInteractionPosesOpenXR` or `PalmPoseInteractionOpenXR` has the live pose!
            string[] pointerPosNames = { "pointerPosition", "aimPosition", "pinchPosition", "pokePosition" };
            string[] pointerRotNames = { "pointerRotation", "aimRotation", "pinchRotation", "pokeRotation" };
            string[] devicePosNames = { "devicePosition", "gripPosition", "palmPosition" };
            string[] deviceRotNames = { "deviceRotation", "gripRotation", "palmRotation" };

            foreach (var d in InputSystem.devices)
            {
                if (!IsHandOrPalmDevice(d)) continue;
                if (!MatchesInputSystemSide(d, handIndex)) continue;

                if (preferPointer)
                {
                    for (int i = 0; i < pointerPosNames.Length; i++)
                    {
                        var pp = d.TryGetChildControl<Vector3Control>(pointerPosNames[i]);
                        var pr = d.TryGetChildControl<QuaternionControl>(pointerRotNames[i]);
                        if (pp == null || pr == null) continue;
                        pos = pp.ReadValue();
                        rot = pr.ReadValue();
                        if (pos.sqrMagnitude > 0.005f) return true;
                    }
                }

                for (int i = 0; i < devicePosNames.Length; i++)
                {
                    var dp = d.TryGetChildControl<Vector3Control>(devicePosNames[i]);
                    var dr = d.TryGetChildControl<QuaternionControl>(deviceRotNames[i]);
                    if (dp == null || dr == null) continue;
                    pos = dp.ReadValue();
                    rot = dr.ReadValue();
                    if (pos.sqrMagnitude > 0.005f) return true;
                }
            }

            return false;
        }

        static bool TryFindHandInteraction(int handIndex, out ISDevice device)
        {
            device = null;
            foreach (var d in InputSystem.devices)
            {
                if (!IsHandOrPalmDevice(d)) continue;
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
                return true;
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

            // Pinch midpoint between thumb tip and index tip is the most stable anchor for hand dragging.
            var index = hand.GetJoint(XRHandJointID.IndexTip);
            var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
            if (index.TryGetPose(out var iPose) && thumb.TryGetPose(out var tPose))
            {
                pos = (iPose.position + tPose.position) * 0.5f;
                rot = iPose.rotation;
                return true;
            }
            var palm = hand.GetJoint(XRHandJointID.Palm);
            if (palm.TryGetPose(out var pPose))
            {
                pos = pPose.position;
                rot = pPose.rotation;
                return true;
            }
            return false;
        }

        static bool TryXrHandAimPose(int handIndex, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var sub = ActiveHands();
            if (sub == null) return false;
            var hand = handIndex == 0 ? sub.leftHand : sub.rightHand;
            if (!hand.isTracked) return false;

            var indexTip = hand.GetJoint(XRHandJointID.IndexTip);
            var thumbTip = hand.GetJoint(XRHandJointID.ThumbTip);
            if (!indexTip.TryGetPose(out var iPose)) return false;

            pos = thumbTip.TryGetPose(out var tPose)
                ? (iPose.position + tPose.position) * 0.5f
                : iPose.position;

            // Shoulder-to-pinch natural ray direction (like Google Maps XR / VisionOS / Android XR)
            if (Camera.main != null)
            {
                var cam = Camera.main.transform;
                var shoulder = cam.position
                               + Vector3.down * 0.18f
                               + cam.right * (handIndex == 0 ? -0.14f : 0.14f)
                               - cam.forward * 0.05f;
                var dir = (pos - shoulder).normalized;
                if (dir.sqrMagnitude > 0.1f)
                {
                    rot = Quaternion.LookRotation(dir, Vector3.up);
                    return true;
                }
            }
            rot = iPose.rotation;
            return true;
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
                if (d.TryGetFeatureValue(new InputFeatureUsage<float>("selectValue"), out float sv) && sv > PinchEnter)
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
                    d.TryGetFeatureValue(new InputFeatureUsage<Quaternion>("pointerRotation"), out rot) &&
                    pos.sqrMagnitude > 0.005f)
                    return true;
                if (d.TryGetFeatureValue(new InputFeatureUsage<Vector3>("aimPosition"), out pos) &&
                    d.TryGetFeatureValue(new InputFeatureUsage<Quaternion>("aimRotation"), out rot) &&
                    pos.sqrMagnitude > 0.005f)
                    return true;
                if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out pos) &&
                    pos.sqrMagnitude > 0.005f)
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
                if (!st.TryGetPosition(out pos) || pos.sqrMagnitude < 0.005f) continue;
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
