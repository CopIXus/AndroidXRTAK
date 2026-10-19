using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.Locomotion
{
    /// <summary>
    /// Google Maps XR-style fluid locomotion (works identically with bare hands+eyes OR controllers):
    /// 1. Single-Hand Pinch / Trigger Grab-and-Pull:
    ///    - Locks onto the terrain point you are looking/pointing at and pulls the Earth
    ///      with your hand + ray motion (with smooth release momentum/inertia like Google Maps).
    /// 2. Two-Hand Pinch / Trigger Manipulation:
    ///    - Pull hands apart / push together to Zoom / Scale about the focus point in front of you.
    ///    - Twist hands horizontally like a steering wheel to Rotate the map around its surface normal.
    ///    - Lift / lower both hands together to smoothly Tilt the Earth up in front of you (0° to 62° Wall View)
    ///      without needing any controller buttons!
    /// 3. Physical Controller Thumbsticks (when controllers are held):
    ///    - Left stick glides along the tilted map surface plane; Right stick zooms &amp; yaws; B/Y + stick tilts.
    /// </summary>
    public class XrWorldLocomotion : MonoBehaviour
    {
        const float DragThresholdM = 0.0025f;
        const float StickDeadzone = 0.12f;
        const float InertiaDamping = 6.5f;

        static readonly float[] TiltPresetDegs = { 0f, 38f, 62f };

        [SerializeField] XrWorldRoot world;
        [SerializeField] Transform cameraTransform;
        [SerializeField] CesiumMapController map;

        struct HandGrab
        {
            public bool grabbing;
            public Vector3 lastPos;
            public Vector3 lastRayHit;
            public bool hasRayHit;
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
            if (_panVelocity.sqrMagnitude < 0.04f)
            {
                _panVelocity = Vector3.zero;
                return false;
            }
            var mapUp = world.Root.up;
            _panVelocity = Vector3.ProjectOnPlane(_panVelocity, mapUp);
            world.Root.position += _panVelocity * Time.deltaTime;
            _panVelocity = Vector3.Lerp(_panVelocity, Vector3.zero, Mathf.Clamp01(InertiaDamping * Time.deltaTime));
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
                        g.hasRayHit = TryIntersectMapPlane(i, out g.lastRayHit);
                        _panVelocity = Vector3.zero;
                    }
                    active.Add(i);
                    positions[i] = pos;
                }
                else if (g.grabbing)
                {
                    g.grabbing = false;
                    g.hasRayHit = false;
                    _twoHand = false;
                }
            }

            float gain = PanGain();
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

                // Combine direct 3D hand translation with subtle ray-anchor pull (Google Maps XR feel):
                // moving your hand in 3D space slides the Earth 1:1 with altitude-scaled gain,
                // and sweeping your wrist/ray across the map adds responsive long-range reach.
                var worldDelta = Vector3.ProjectOnPlane(handDelta, mapUp) * gain;

                if (TryIntersectMapPlane(i, out var curRayHit))
                {
                    if (g.hasRayHit)
                    {
                        var rayDelta = Vector3.ProjectOnPlane(curRayHit - g.lastRayHit, mapUp);
                        // Blend 35% ray delta clamped so wrist micro-jitter never jumps the map
                        float maxStep = ViewerHeight() * 0.18f;
                        if (rayDelta.magnitude > maxStep)
                            rayDelta = rayDelta.normalized * maxStep;
                        worldDelta += rayDelta * 0.35f;
                    }
                    g.lastRayHit = curRayHit;
                    g.hasRayHit = true;
                }

                if (worldDelta.sqrMagnitude > 1e-8f && g.moved > DragThresholdM)
                {
                    world.Root.position += worldDelta;
                    float dt = Mathf.Max(Time.deltaTime, 0.004f);
                    _panVelocity = Vector3.Lerp(_panVelocity, worldDelta / dt, 0.45f);
                    // Re-sample ray anchor after moving the world so it doesn't feed back
                    g.hasRayHit = TryIntersectMapPlane(i, out g.lastRayHit);
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
                }
                else
                {
                    bool moved = false;
                    var focusPivot = world.GetFocusPivot(cameraTransform);

                    // 1. Two-hand Pan along map plane
                    var midDelta = Vector3.ProjectOnPlane(mid - _prevMid, mapUp);
                    if (midDelta.sqrMagnitude > 1e-8f)
                    {
                        world.Root.position += midDelta * gain;
                        moved = true;
                    }

                    // 2. Two-hand Stretch / Pinch Zoom about the map focus point in front of the viewer
                    //    (so zooming with your hands in your lap zooms the map in front of your eyes,
                    //    not the air around your waist!)
                    if (_prevDist > 0.03f && dist > 0.03f)
                    {
                        float rawFactor = dist / _prevDist;
                        // Exaggerate stretch slightly (1.45x) so comfortable hand motion zooms quickly
                        float factor = 1f + (rawFactor - 1f) * 1.45f;
                        if (Mathf.Abs(factor - 1f) > 0.0015f)
                        {
                            // In standard 1:1 DEM mode, zoom by moving the world along mapUp (altitude)
                            // so DEM LOD rings refine naturally; in Tabletop mode, scale the miniature.
                            bool isTabletop = TakXr.UI.XrTacticalToolsSuite.Instance != null &&
                                              TakXr.UI.XrTacticalToolsSuite.Instance.IsTabletopActive;
                            if (isTabletop)
                            {
                                world.ScaleAboutPoint(focusPivot, factor);
                            }
                            else
                            {
                                // Pull hands apart (factor > 1) -> zoom IN (move closer to focusPivot)
                                float zoomStep = Mathf.Clamp(factor - 1f, -0.25f, 0.25f);
                                var toPivot = cameraTransform.position - focusPivot;
                                world.Root.position += toPivot * (zoomStep * 1.35f);
                            }
                            moved = true;
                        }
                    }

                    // 3. Two-hand Twist -> Rotate map around its surface normal
                    float dAngle = Mathf.DeltaAngle(_prevAngle * Mathf.Rad2Deg, angle * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    if (Mathf.Abs(dAngle) > 0.002f)
                    {
                        world.RotateAboutMapUp(focusPivot, dAngle);
                        moved = true;
                    }

                    // 4. Controller-free Two-Hand Vertical Lift/Lower -> Smoothly Tilt the Earth!
                    //    Lifting both pinched hands upward tilts the far horizon UP into Wall View;
                    //    lowering both hands flattens it back toward 0°.
                    float dy = avgY - _prevAvgY;
                    if (Mathf.Abs(dy) > 0.0015f)
                    {
                        ApplyPitchDelta(dy * 165f);
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

        float PanGain() => Mathf.Clamp(ViewerHeight() * 0.72f, 3f, 5000f);

        float MoveSpeed() => Mathf.Clamp(ViewerHeight() * 1.6f * _speedMul, 8f, 12000f);

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
