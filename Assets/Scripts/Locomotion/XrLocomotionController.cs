using TakXr.Core;
using TakXr.Map;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TakXr.Locomotion
{
    /// <summary>
    /// Continuous fly + snap turn + north-up for XR / desktop preview.
    /// Works with XR controllers via Input System or WASD/mouse in Editor.
    /// </summary>
    public class XrLocomotionController : MonoBehaviour
    {
        [SerializeField] AppConfig config;
        [SerializeField] Transform rigRoot;
        [SerializeField] Transform cameraTransform;
        [SerializeField] CesiumMapController map;
        [SerializeField] float moveSpeed = 25f;
        [SerializeField] float flySpeed = 20f;
        [SerializeField] float snapTurnDegrees = 30f;
        [SerializeField] float snapTurnCooldown = 0.35f;

        float _nextSnapTime;
        Vector2 _move;
        Vector2 _look;
        bool _northPressed;
        bool _teleportPressed;
        [SerializeField] float teleportDistance = 80f;

        public void Configure(AppConfig cfg, Transform rig, Transform cam, CesiumMapController mapCtrl)
        {
            config = cfg;
            rigRoot = rig;
            cameraTransform = cam;
            map = mapCtrl;
        }

        void Update()
        {
            if (rigRoot == null) return;
            if (cameraTransform == null) cameraTransform = Camera.main != null ? Camera.main.transform : null;

            ReadInput();

            bool moving = _move.sqrMagnitude > 0.01f;
            map?.NotifyMoving(moving);

            if (moving && cameraTransform != null)
            {
                var flatFwd = cameraTransform.forward;
                flatFwd.y = 0f;
                if (flatFwd.sqrMagnitude < 1e-6f) flatFwd = Vector3.forward;
                flatFwd.Normalize();
                var right = Vector3.Cross(Vector3.up, flatFwd).normalized;
                var delta = (flatFwd * _move.y + right * _move.x) * moveSpeed * Time.deltaTime;
                // Vertical from look.y when holding trigger analog as climb in editor (R/F)
                delta += Vector3.up * _look.y * flySpeed * Time.deltaTime;
                rigRoot.position += delta;
            }

            if (Mathf.Abs(_look.x) > 0.7f && Time.time >= _nextSnapTime)
            {
                float dir = Mathf.Sign(_look.x);
                rigRoot.Rotate(0f, dir * snapTurnDegrees, 0f, Space.World);
                _nextSnapTime = Time.time + snapTurnCooldown;
            }

            if (_northPressed) OrientNorth();
            if (_teleportPressed && cameraTransform != null)
            {
                var flatFwd = cameraTransform.forward;
                flatFwd.y = 0f;
                if (flatFwd.sqrMagnitude < 1e-6f) flatFwd = Vector3.forward;
                flatFwd.Normalize();
                TeleportTo(cameraTransform.position + flatFwd * teleportDistance);
            }
        }

        void ReadInput()
        {
            _move = Vector2.zero;
            _look = Vector2.zero;
            _northPressed = false;
            _teleportPressed = false;

            // Keyboard / mouse fallback (desktop XR preview & Editor)
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed) _move.y += 1;
                if (kb.sKey.isPressed) _move.y -= 1;
                if (kb.dKey.isPressed) _move.x += 1;
                if (kb.aKey.isPressed) _move.x -= 1;
                if (kb.rKey.isPressed) _look.y += 1;
                if (kb.fKey.isPressed) _look.y -= 1;
                if (kb.qKey.wasPressedThisFrame) _look.x = -1;
                if (kb.eKey.wasPressedThisFrame) _look.x = 1;
                if (kb.nKey.wasPressedThisFrame) _northPressed = true;
                if (kb.tKey.wasPressedThisFrame) _teleportPressed = true;
            }

            // XR controller sticks when available (left move, right turn, A/South = teleport)
            var gamepad = Gamepad.current;
            if (gamepad != null)
            {
                var lm = gamepad.leftStick.ReadValue();
                var rm = gamepad.rightStick.ReadValue();
                if (lm.sqrMagnitude > _move.sqrMagnitude) _move = lm;
                if (Mathf.Abs(rm.x) > Mathf.Abs(_look.x)) _look.x = rm.x;
                if (gamepad.buttonNorth.wasPressedThisFrame) _northPressed = true;
                if (gamepad.buttonSouth.wasPressedThisFrame) _teleportPressed = true;
            }
        }

        public void OrientNorth()
        {
            if (rigRoot == null) return;
            var euler = rigRoot.rotation.eulerAngles;
            rigRoot.rotation = Quaternion.Euler(0f, 0f, 0f);
            Debug.Log("[Locomotion] orient north");
        }

        public void TeleportTo(Vector3 worldPosition)
        {
            if (rigRoot == null) return;
            map?.NotifyMoving(true);
            var cam = cameraTransform != null ? cameraTransform : Camera.main?.transform;
            Vector3 offset = Vector3.zero;
            if (cam != null) offset = rigRoot.position - cam.position;
            // Keep camera XZ at target; preserve height offset of cam within rig
            var target = worldPosition;
            if (cam != null)
            {
                var camLocal = rigRoot.InverseTransformPoint(cam.position);
                rigRoot.position = new Vector3(target.x - camLocal.x, target.y, target.z - camLocal.z);
            }
            else
            {
                rigRoot.position = target;
            }
        }
    }
}
