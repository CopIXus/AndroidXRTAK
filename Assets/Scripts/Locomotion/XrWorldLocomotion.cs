using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.Locomotion
{
    /// <summary>
    /// Google Maps Immersive View locomotion (Android XR help: pinch-drag to pan,
    /// two-hand spread to zoom, two-hand twist to rotate):
    /// 1. Single-Hand Pinch — ray-anchored grab. The map point under your aim sticks
    ///    1:1 to the ray (pull the world toward you). Short soft coast on release.
    /// 2. Two-Hand Pinch — spread/pinch to zoom about the focus in front of you;
    ///    steering-wheel twist to rotate; gentle dual-hand lift to tilt.
    /// 3. Controllers — left stick pans the map plane; right stick zooms/yaws; B/Y+stick tilts.
    /// </summary>
    public class XrWorldLocomotion : MonoBehaviour
    {
        const float DragThresholdM = 0.004f;
        const float StickDeadzone = 0.15f;
        /// <summary>Maps-like short coast — was 6.5 and felt like a rocket after release.</summary>
        const float InertiaDamping = 14f;
        const float InertiaMaxSpeed = 45f;
        /// <summary>Ignore sub-millimeter hand jitter before starting a pan.</summary>
        const float GrabSettleM = 0.008f;

        static readonly float[] TiltPresetDegs = { 0f, 38f, 62f };

        [SerializeField] XrWorldRoot world;
        [SerializeField] Transform cameraTransform;
        [SerializeField] CesiumMapController map;

        struct HandGrab
        {
            public bool grabbing;
            public Vector3 lastPos;
            /// <summary>Grabbed map point in world-root local space (Maps-style anchor).</summary>
            public Vector3 grabLocal;
            public bool hasGrab;
            public float moved;
        }

        readonly HandGrab[] _grabs = new HandGrab[2];
        bool _twoHand;
        Vector3 _prevMid;
        float _prevDist;
        float _prevAngle;
        float _prevAvgY;
        Vector3 _panVelocity;
        float _speedMul = 1f;
        bool _snapTurnEnabled;
        float _snapCooldown;
        bool _stickWasOut;
        float _savedPitchDeg;
        System.Action<float> _onPitchChanged;
        bool _tiltHeld;

        public float SavedPitchDeg => _savedPitchDeg;

        public void SetSpeedMultiplier(float mul) =>
            _speedMul = Mathf.Clamp(mul, 0.25f, 4f);

        public void SetSnapTurnEnabled(bool on) => _snapTurnEnabled = on;

        public void SetSavedPitch(float deg) =>
            _savedPitchDeg = Mathf.Clamp(deg, XrWorldRoot.MinPitchDeg, XrWorldRoot.MaxPitchDeg);

        public void SetPitchChangedHandler(System.Action<float> onChanged) =>
            _onPitchChanged = onChanged;

        public void Configure(XrWorldRoot worldRoot, Transform cam, CesiumMapController mapCtrl)
        {
            world = worldRoot;
            cameraTransform = cam;
            map = mapCtrl;
        }

        void Update()
        {
            if (world == null) world = XrWorldRoot.Instance;
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
            if (world == null || cameraTransform == null) return;

            bool navigated = false;
            navigated |= UpdateWorldTilt();
            navigated |= UpdateThumbsticks();
            navigated |= UpdateSnapTurn();
            navigated |= UpdatePinchAndControllers();
            navigated |= UpdateInertia();
            navigated |= UpdateKeyboardFallback();

            if (navigated) map?.NotifyMoving(true);
        }

        bool UpdateInertia()
        {
            if (_grabs[0].grabbing || _grabs[1].grabbing) return false;
            if (_panVelocity.sqrMagnitude < 0.01f)
            {
                _panVelocity = Vector3.zero;
                return false;
            }
            var mapUp = world.Root.up;
            _panVelocity = Vector3.ProjectOnPlane(_panVelocity, mapUp);
            if (_panVelocity.magnitude > InertiaMaxSpeed)
                _panVelocity = _panVelocity.normalized * InertiaMaxSpeed;
            world.Root.position += _panVelocity * Time.deltaTime;
            // Exponential decay — short Maps-like coast, not a long slide.
            float damp = Mathf.Exp(-InertiaDamping * Time.deltaTime);
            _panVelocity *= damp;
            if (_panVelocity.sqrMagnitude < 0.01f)
                _panVelocity = Vector3.zero;
            return true;
        }

        bool UpdateSnapTurn()
        {
            if (!_snapTurnEnabled || world == null || cameraTransform == null) return false;
            if (Time.unscaledTime < _snapCooldown) return false;
            if (!XrHandPinchInput.TryGetStick(1, out var stick)) return false;
            const float thresh = 0.65f;
            bool outNow = Mathf.Abs(stick.x) >= thresh;
            if (outNow && !_stickWasOut)
            {
                float dir = stick.x > 0f ? 1f : -1f;
                var pivot = world.GetFocusPivot(cameraTransform);
                world.RotateAboutMapUp(pivot, dir * 45f * Mathf.Deg2Rad);
                _snapCooldown = Time.unscaledTime + 0.28f;
                _stickWasOut = true;
                map?.NotifyMoving(true);
                return true;
            }
            if (!outNow) _stickWasOut = false;
            return false;
        }

        bool UpdateKeyboardFallback()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return false;
            float speed = MoveSpeed() * Time.deltaTime;
            MapSurfaceBasis(out var mapFwd, out var mapRight, out var mapUp);
            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed) move -= mapFwd;
            if (kb.sKey.isPressed) move += mapFwd;
            if (kb.aKey.isPressed) move += mapRight;
            if (kb.dKey.isPressed) move -= mapRight;
            if (kb.rKey.isPressed)
            {
                if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)
                {
                    ApplyPitchDelta(55f * Time.deltaTime);
                    return true;
                }
                move -= mapUp;
            }
            if (kb.fKey.isPressed)
            {
                if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)
                {
                    ApplyPitchDelta(-55f * Time.deltaTime);
                    return true;
                }
                move += mapUp;
            }
            if (kb.tKey.wasPressedThisFrame)
            {
                CycleTiltPreset();
                return true;
            }
            if (move.sqrMagnitude < 1e-6f)
            {
                if (kb.nKey.wasPressedThisFrame) OrientNorth();
                return false;
            }
            world.Root.position += move.normalized * speed;
            return true;
        }

        bool TryIntersectMapPlane(int handIndex, out Vector3 planeHit)
        {
            planeHit = Vector3.zero;
            if (world == null || !XrHandPinchInput.TryGetAim(handIndex, out var origin, out var fwd))
                return false;
            var mapUp = world.Root.up;
            float denom = Vector3.Dot(fwd, mapUp);
            if (Mathf.Abs(denom) < 0.06f) return false;
            float t = Vector3.Dot(world.Root.position - origin, mapUp) / denom;
            if (t <= 0.2f || t > 18000f) return false;
            planeHit = origin + fwd * t;
            return true;
        }

        bool UpdatePinchAndControllers()
        {
            var positions = new Vector3[2];
            var active = new System.Collections.Generic.List<int>(2);

            for (int i = 0; i < 2; i++)
            {
                bool tracked = XrHandPinchInput.TryGetPose(i, out var pos, out _);
                bool wantGrab = tracked && XrHandPinchInput.IsGrabbing(i) &&
                                !XrHandPinchInput.IsUiBlocking(i) &&
                                !TakXr.UI.XrDrawTool.IsArmed &&
                                !TakXr.UI.XrRangeMeasureTool.IsArmed &&
                                !TakXr.UI.XrElevationTool.IsArmed;

                ref var g = ref _grabs[i];
                if (wantGrab)
                {
                    if (!g.grabbing)
                    {
                        g.grabbing = true;
                        g.moved = 0f;
                        g.lastPos = pos;
                        g.hasGrab = false;
                        // Maps: lock the map point under the aim ray (or hand projection).
                        if (TryIntersectMapPlane(i, out var hit))
                        {
                            g.grabLocal = world.Root.InverseTransformPoint(hit);
                            g.hasGrab = true;
                        }
                        _panVelocity = Vector3.zero;
                    }
                    active.Add(i);
                    positions[i] = pos;
                }
                else if (g.grabbing)
                {
                    g.grabbing = false;
                    g.hasGrab = false;
                    _twoHand = false;
                }
            }

            var mapUp = world.Root.up;

            if (active.Count == 1)
            {
                _twoHand = false;
                int i = active[0];
                ref var g = ref _grabs[i];
                var pos = positions[i];
                var handDelta = pos - g.lastPos;
                g.moved += handDelta.magnitude;
                g.lastPos = pos;

                if (g.moved < GrabSettleM) return false;

                // Google Maps Immersive View: "pinch and drag … pull the world towards you".
                // Keep the grabbed map point stuck under the current aim ray — true 1:1,
                // no altitude-scaled hand gain (that was launching the map).
                if (!TryIntersectMapPlane(i, out var rayHit))
                {
                    // Fallback: gentle 1:1 hand slide on the map plane if the ray misses.
                    var fallback = Vector3.ProjectOnPlane(handDelta, mapUp);
                    if (fallback.sqrMagnitude > 1e-8f && g.moved > DragThresholdM)
                    {
                        world.Root.position += fallback;
                        CaptureSoftInertia(fallback);
                        return true;
                    }
                    return false;
                }

                if (!g.hasGrab)
                {
                    g.grabLocal = world.Root.InverseTransformPoint(rayHit);
                    g.hasGrab = true;
                    return false;
                }

                var grabbedNow = world.Root.TransformPoint(g.grabLocal);
                var worldDelta = Vector3.ProjectOnPlane(rayHit - grabbedNow, mapUp);

                // Cap one-frame jumps (tracking glitches) without altitude amplification.
                float maxStep = Mathf.Clamp(ViewerHeight() * 0.08f, 0.35f, 12f);
                if (worldDelta.magnitude > maxStep)
                    worldDelta = worldDelta.normalized * maxStep;

                if (worldDelta.sqrMagnitude > 1e-8f && g.moved > DragThresholdM)
                {
                    world.Root.position += worldDelta;
                    CaptureSoftInertia(worldDelta);
                    return true;
                }
            }
            else if (active.Count == 2)
            {
                var a = positions[active[0]];
                var b = positions[active[1]];
                var mid = (a + b) * 0.5f;
                float dist = Vector3.Distance(a, b);
                // Measure hand-to-hand angle on the horizontal plane for natural steering-wheel rotation
                float angle = Mathf.Atan2(a.x - b.x, a.z - b.z);
                float avgY = (a.y + b.y) * 0.5f;

                if (!_twoHand)
                {
                    _twoHand = true;
                    _prevMid = mid;
                    _prevDist = Mathf.Max(dist, 1e-4f);
                    _prevAngle = angle;
                    _prevAvgY = avgY;
                    _panVelocity = Vector3.zero;
                    // Clear single-hand anchors so we don't mix modes.
                    _grabs[0].hasGrab = false;
                    _grabs[1].hasGrab = false;
                }
                else
                {
                    bool moved = false;
                    var focusPivot = world.GetFocusPivot(cameraTransform);

                    // 1. Two-hand pan — 1:1 hand midpoint on the map plane (Maps dual-hand drag).
                    //    Do NOT multiply by altitude PanGain (that made dual-hand wild).
                    var midDelta = Vector3.ProjectOnPlane(mid - _prevMid, mapUp);
                    float twoHandGain = TwoHandPanGain();
                    midDelta *= twoHandGain;
                    float maxTwo = Mathf.Clamp(ViewerHeight() * 0.06f, 0.25f, 8f);
                    if (midDelta.magnitude > maxTwo)
                        midDelta = midDelta.normalized * maxTwo;
                    if (midDelta.sqrMagnitude > 1e-8f)
                    {
                        world.Root.position += midDelta;
                        moved = true;
                    }

                    // 2. Two-hand stretch / pinch zoom (Maps: move hands closer/further).
                    if (_prevDist > 0.04f && dist > 0.04f)
                    {
                        float rawFactor = dist / _prevDist;
                        // Near 1:1 with hand spacing — slight 1.1x feel, not the old 1.45x rocket zoom.
                        float factor = 1f + (rawFactor - 1f) * 1.1f;
                        if (Mathf.Abs(factor - 1f) > 0.0025f)
                        {
                            bool isTabletop = TakXr.UI.XrTacticalToolsSuite.Instance != null &&
                                              TakXr.UI.XrTacticalToolsSuite.Instance.IsTabletopActive;
                            if (isTabletop)
                            {
                                world.ScaleAboutPoint(focusPivot, factor);
                            }
                            else
                            {
                                // Hands apart (factor > 1) → zoom IN toward focus in front of eyes.
                                float zoomStep = Mathf.Clamp(factor - 1f, -0.12f, 0.12f);
                                var toPivot = cameraTransform.position - focusPivot;
                                world.Root.position += toPivot * zoomStep;
                            }
                            moved = true;
                        }
                    }

                    // 3. Two-hand twist → rotate around map surface normal (Maps steering wheel).
                    float dAngle = Mathf.DeltaAngle(_prevAngle * Mathf.Rad2Deg, angle * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    if (Mathf.Abs(dAngle) > 0.003f)
                    {
                        world.RotateAboutMapUp(focusPivot, dAngle);
                        moved = true;
                    }

                    // 4. Dual-hand lift → gentle tilt (was 165°/m — far too twitchy).
                    float dy = avgY - _prevAvgY;
                    if (Mathf.Abs(dy) > 0.004f)
                    {
                        ApplyPitchDelta(dy * 55f);
                        _onPitchChanged?.Invoke(_savedPitchDeg);
                        moved = true;
                    }

                    _prevMid = mid;
                    _prevDist = dist;
                    _prevAngle = angle;
                    _prevAvgY = avgY;
                    return moved;
                }
            }
            else _twoHand = false;

            return false;
        }

        void CaptureSoftInertia(Vector3 worldDelta)
        {
            float dt = Mathf.Max(Time.deltaTime, 0.008f);
            var sample = worldDelta / dt;
            // Light blend + hard cap so release never flings the map.
            _panVelocity = Vector3.Lerp(_panVelocity, sample, 0.22f);
            if (_panVelocity.magnitude > InertiaMaxSpeed)
                _panVelocity = _panVelocity.normalized * InertiaMaxSpeed;
        }

        bool UpdateWorldTilt()
        {
            bool held = XrHandPinchInput.IsTiltModifierHeld();
            if (!held)
            {
                if (_tiltHeld)
                {
                    _tiltHeld = false;
                    _onPitchChanged?.Invoke(_savedPitchDeg);
                }
                return false;
            }
            _tiltHeld = true;
            float dy = 0f;
            if (XrHandPinchInput.TryGetStick(0, out var left) && Mathf.Abs(left.y) > StickDeadzone)
                dy += left.y;
            if (XrHandPinchInput.TryGetStick(1, out var right) && Mathf.Abs(right.y) > StickDeadzone)
                dy += right.y;
            if (Mathf.Abs(dy) < StickDeadzone) return false;
            ApplyPitchDelta(dy * 70f * Time.deltaTime);
            return true;
        }

        void ApplyPitchDelta(float deltaDeg)
        {
            if (world == null || cameraTransform == null) return;
            world.AddWorldPitch(cameraTransform, deltaDeg);
            _savedPitchDeg = world.WorldPitchDeg;
        }

        /// <summary>
        /// Cycles upright God's-Eye tilt presets:
        /// 0° Flat Floor → 38° Angled Command Desk → 62° Upright Wall Map (look straight ahead).
        /// </summary>
        public float CycleTiltPreset()
        {
            if (world == null || cameraTransform == null) return _savedPitchDeg;
            float cur = world.WorldPitchDeg;
            float next = TiltPresetDegs[0];
            for (int i = 0; i < TiltPresetDegs.Length; i++)
            {
                if (cur < TiltPresetDegs[i] - 8f)
                {
                    next = TiltPresetDegs[i];
                    break;
                }
            }
            SetWorldTilt(next);
            return next;
        }

        public void SetWorldTilt(float targetDeg)
        {
            if (world == null || cameraTransform == null) return;
            world.SetWorldPitch(cameraTransform, targetDeg);
            _savedPitchDeg = world.WorldPitchDeg;
            _onPitchChanged?.Invoke(_savedPitchDeg);
            map?.NotifyMoving(true);
        }

        public void RestoreSavedPitch()
        {
            if (world == null || cameraTransform == null) return;
            world.SetWorldPitch(cameraTransform, _savedPitchDeg);
        }

        public void FlattenWorld()
        {
            if (world == null) return;
            world.Flatten(cameraTransform);
            _savedPitchDeg = 0f;
            _onPitchChanged?.Invoke(0f);
            map?.NotifyMoving(true);
        }

        bool UpdateThumbsticks()
        {
            bool tilting = XrHandPinchInput.IsTiltModifierHeld();
            float speed = MoveSpeed() * Time.deltaTime;
            MapSurfaceBasis(out var mapFwd, out var mapRight, out var mapUp);
            bool navigated = false;
            bool hasRight = XrHandPinchInput.HasStick(1);

            if (XrHandPinchInput.TryGetStick(0, out var left) && left.magnitude > StickDeadzone)
            {
                _panVelocity = Vector3.zero;
                if (tilting)
                {
                    if (Mathf.Abs(left.x) > StickDeadzone)
                    {
                        world.Root.position += (-mapRight * left.x) * speed;
                        navigated = true;
                    }
                }
                else
                {
                    world.Root.position += (-mapFwd * left.y - mapRight * left.x) * speed;
                    navigated = true;
                }
            }

            if (hasRight && XrHandPinchInput.TryGetStick(1, out var rstick) &&
                rstick.magnitude > StickDeadzone)
            {
                _panVelocity = Vector3.zero;
                if (!tilting && Mathf.Abs(rstick.y) > StickDeadzone)
                    world.Root.position += (-mapUp) * (rstick.y * speed);
                if (!_snapTurnEnabled && Mathf.Abs(rstick.x) > StickDeadzone)
                {
                    var pivot = world.GetFocusPivot(cameraTransform);
                    world.RotateAboutMapUp(pivot, -rstick.x * 1.8f * Time.deltaTime);
                }
                navigated = true;
            }

            return navigated;
        }

        void FlatBasis(out Vector3 flatFwd, out Vector3 right)
        {
            flatFwd = cameraTransform.forward;
            flatFwd.y = 0f;
            if (flatFwd.sqrMagnitude < 1e-6f) flatFwd = Vector3.forward;
            flatFwd.Normalize();
            right = Vector3.Cross(Vector3.up, flatFwd).normalized;
        }

        void MapSurfaceBasis(out Vector3 mapFwd, out Vector3 mapRight, out Vector3 mapUp)
        {
            mapUp = world != null ? world.Root.up : Vector3.up;
            FlatBasis(out var flatFwd, out var flatRight);

            mapRight = Vector3.ProjectOnPlane(flatRight, mapUp);
            if (mapRight.sqrMagnitude < 1e-6f) mapRight = flatRight;
            mapRight.Normalize();

            mapFwd = Vector3.Cross(mapRight, mapUp);
            if (mapFwd.sqrMagnitude < 1e-6f) mapFwd = flatFwd;
            mapFwd.Normalize();
        }

        float ViewerHeight()
        {
            var up = world.Root.up;
            float h = Vector3.Dot(cameraTransform.position - world.Root.position, up);
            return Mathf.Clamp(Mathf.Abs(h), 8f, 8000f);
        }

        /// <summary>
        /// Dual-hand midpoint pan: mild altitude feel without the old 0.72×height×5000 rocket.
        /// Single-hand pan no longer uses this — it is pure ray-anchor 1:1.
        /// </summary>
        float TwoHandPanGain() =>
            Mathf.Clamp(ViewerHeight() * 0.045f * _speedMul, 0.85f, 18f);

        /// <summary>Thumbstick / keyboard glide — calmer than the previous 1.6×height curve.</summary>
        float MoveSpeed() => Mathf.Clamp(ViewerHeight() * 0.55f * _speedMul, 4f, 900f);

        public void OrientNorth() => world?.OrientNorth(cameraTransform);

        public void TeleportForward(float meters = 120f)
        {
            if (world == null || cameraTransform == null) return;
            MapSurfaceBasis(out var mapFwd, out _, out _);
            world.Root.position -= mapFwd * meters;
            map?.NotifyMoving(true);
        }

        public void NudgeWorld(Vector3 worldDelta)
        {
            if (world == null) return;
            world.Root.position += worldDelta;
            map?.NotifyMoving(true);
        }

        public void ZoomAtCamera(float factor)
        {
            if (world == null || cameraTransform == null) return;
            var pivot = world.GetFocusPivot(cameraTransform);
            var toPivot = cameraTransform.position - pivot;
            float zoomStep = Mathf.Clamp(factor - 1f, -0.5f, 0.5f);
            world.Root.position += toPivot * zoomStep;
            map?.NotifyMoving(true);
        }

        public void ResetView()
        {
            if (world == null || cameraTransform == null) return;
            _panVelocity = Vector3.zero;
            world.Root.localScale = Vector3.one;
            world.ApplyInitialOverview(cameraTransform, 160f, 50f);
            world.OrientNorth(cameraTransform);
            RestoreSavedPitch();
            map?.NotifyMoving(true);
        }

        public void FrameWorldPoint(Vector3 worldPoint, float overviewDistM = 160f, float heightM = 50f)
        {
            if (world == null || cameraTransform == null) return;
            _panVelocity = Vector3.zero;
            float pitch = _savedPitchDeg;
            world.Flatten(cameraTransform);
            world.Root.localScale = Vector3.one;
            var camPos = cameraTransform.position;
            FlatBasis(out var flatFwd, out _);
            var desiredMarker = camPos + flatFwd * overviewDistM + Vector3.down * heightM;
            world.Root.position += desiredMarker - worldPoint;
            world.OrientNorth(cameraTransform);
            _savedPitchDeg = pitch;
            RestoreSavedPitch();
            map?.NotifyMoving(true);
        }

        public void FitWorldPoints(System.Collections.Generic.IList<Vector3> points)
        {
            if (world == null || cameraTransform == null || points == null || points.Count == 0)
            {
                ResetView();
                return;
            }
            var min = points[0];
            var max = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }
            var center = (min + max) * 0.5f;
            float span = Mathf.Max(max.x - min.x, max.z - min.z, 40f);
            float dist = Mathf.Clamp(span * 1.4f, 80f, 2500f);
            FrameWorldPoint(center, dist, Mathf.Clamp(span * 0.25f, 30f, 400f));
        }
    }
}
