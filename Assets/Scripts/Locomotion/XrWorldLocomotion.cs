using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.Locomotion
{
    /// <summary>
    /// Google Maps immersive-style locomotion: stick fly, trigger/pinch drag-pan,
    /// two-hand stretch zoom, and upright God's-Eye Wall/Desk tilt.
    /// Moves <see cref="XrWorldRoot"/> along its own surface plane (<c>Root.up</c>)
    /// so controller navigation stays natural whether the Earth is flat below you (0°)
    /// or tilted up in front of you (38° Desk / 62° Wall View).
    /// </summary>
    public class XrWorldLocomotion : MonoBehaviour
    {
        const float DragThresholdM = 0.003f;
        const float StickDeadzone = 0.12f;

        static readonly float[] TiltPresetDegs = { 0f, 38f, 62f };

        [SerializeField] XrWorldRoot world;
        [SerializeField] Transform cameraTransform;
        [SerializeField] CesiumMapController map;

        struct HandGrab
        {
            public bool grabbing;
            public Vector3 lastPos;
            public float moved;
        }

        readonly HandGrab[] _grabs = new HandGrab[2];
        bool _twoHand;
        Vector3 _prevMid;
        float _prevDist;
        float _prevAngle;
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
            navigated |= UpdateKeyboardFallback();

            if (navigated) map?.NotifyMoving(true);
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
                                !TakXr.UI.XrElevationTool.IsArmed; // tool pinches must not drag the map

                ref var g = ref _grabs[i];
                if (wantGrab)
                {
                    if (!g.grabbing)
                    {
                        g.grabbing = true;
                        g.moved = 0f;
                        g.lastPos = pos;
                    }
                    active.Add(i);
                    positions[i] = pos;
                }
                else if (g.grabbing)
                {
                    g.grabbing = false;
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
                var delta = pos - g.lastPos;
                g.moved += delta.magnitude;
                if (delta.sqrMagnitude > 1e-10f && g.moved > DragThresholdM)
                {
                    // Pan strictly along the Earth's surface plane (Root.up) so dragging
                    // when the map is tilted in front of you slides the map without changing altitude.
                    var planeDelta = Vector3.ProjectOnPlane(delta, mapUp);
                    world.Root.position += planeDelta * gain;
                    g.lastPos = pos;
                    return true;
                }
                g.lastPos = pos;
            }
            else if (active.Count == 2)
            {
                var a = positions[active[0]];
                var b = positions[active[1]];
                var mid = (a + b) * 0.5f;
                float dist = Vector3.Distance(a, b);
                float angle = Mathf.Atan2(a.x - b.x, a.z - b.z);

                if (!_twoHand)
                {
                    _twoHand = true;
                    _prevMid = mid;
                    _prevDist = Mathf.Max(dist, 1e-4f);
                    _prevAngle = angle;
                }
                else
                {
                    bool moved = false;
                    var midDelta = Vector3.ProjectOnPlane(mid - _prevMid, mapUp);
                    if (midDelta.sqrMagnitude > 0f)
                    {
                        world.Root.position += midDelta * gain;
                        moved = true;
                    }
                    if (_prevDist > 1e-4f && dist > 1e-4f)
                    {
                        float factor = dist / _prevDist;
                        if (Mathf.Abs(factor - 1f) > 1e-4f)
                        {
                            world.ScaleAboutPoint(mid, factor);
                            moved = true;
                        }
                    }
                    float dAngle = Mathf.DeltaAngle(_prevAngle * Mathf.Rad2Deg, angle * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    if (Mathf.Abs(dAngle) > 1e-4f)
                    {
                        world.RotateAboutMapUp(mid, dAngle);
                        moved = true;
                    }

                    _prevMid = mid;
                    _prevDist = dist;
                    _prevAngle = angle;
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

            // Left stick: pan across the Earth's surface plane (mapFwd / mapRight).
            // When the Earth is tilted up in front of you, pushing the stick forward
            // slides the terrain across the tilted plane rather than driving it into your face!
            if (XrHandPinchInput.TryGetStick(0, out var left) && left.magnitude > StickDeadzone)
            {
                if (tilting)
                {
                    // Y is consumed by world tilt; keep strafe on X.
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

            // Right stick: zoom/altitude along map surface normal (mapUp) + yaw around mapUp.
            if (hasRight && XrHandPinchInput.TryGetStick(1, out var rstick) &&
                rstick.magnitude > StickDeadzone)
            {
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

        /// <summary>
        /// Computes camera-aligned forward and right vectors projected onto the Earth's
        /// current surface plane (<c>world.Root.up</c>). At 0° pitch this equals horizontal
        /// ground movement; at 38°–62° pitch it glides smoothly along the tilted map wall.
        /// </summary>
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

        /// <summary>
        /// Height above the map plane along map-local up. Stable when the world
        /// is pitched — unlike camera.y - origin.y which jumps with tilt.
        /// </summary>
        float ViewerHeight()
        {
            var up = world.Root.up;
            float h = Vector3.Dot(cameraTransform.position - world.Root.position, up);
            return Mathf.Clamp(Mathf.Abs(h), 8f, 8000f);
        }

        float PanGain() => Mathf.Clamp(ViewerHeight() * 0.45f, 2f, 4000f);

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
            world.ScaleAboutPoint(pivot, factor);
            map?.NotifyMoving(true);
        }

        public void ResetView()
        {
            if (world == null || cameraTransform == null) return;
            world.Root.localScale = Vector3.one;
            world.ApplyInitialOverview(cameraTransform, 160f, 50f);
            world.OrientNorth(cameraTransform);
            RestoreSavedPitch();
            map?.NotifyMoving(true);
        }

        /// <summary>Frame the world so <paramref name="worldPoint"/> sits at a comfortable overview.</summary>
        public void FrameWorldPoint(Vector3 worldPoint, float overviewDistM = 160f, float heightM = 50f)
        {
            if (world == null || cameraTransform == null) return;
            float pitch = _savedPitchDeg;
            world.Flatten(cameraTransform);
            world.Root.localScale = Vector3.one;
            var camPos = cameraTransform.position;
            FlatBasis(out var flatFwd, out _);
            // Place world so target is ahead of the HMD at overviewDist.
            var desiredMarker = camPos + flatFwd * overviewDistM + Vector3.down * heightM;
            world.Root.position += desiredMarker - worldPoint;
            world.OrientNorth(cameraTransform);
            _savedPitchDeg = pitch;
            RestoreSavedPitch();
            map?.NotifyMoving(true);
        }

        /// <summary>Scale/position world so all given marker world positions fit in view.</summary>
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
