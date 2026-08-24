using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// Single source of truth for facing Unity TextMesh / Quad UI at the viewer.
    ///
    /// CONVENTION (do not flip this again — verified on-device):
    /// TextMesh glyphs and Quad primitives are readable when viewed from their
    /// local -Z side. Their font/sprite shaders are double-sided (Cull Off), so
    /// facing them the wrong way doesn't hide them — it shows a MIRRORED image.
    ///
    /// Therefore UI must be rotated so local +Z points AWAY from the camera:
    ///   rotation = LookRotation(uiPos - camPos)
    /// and child content that should sit "in front" (toward the viewer) uses
    /// NEGATIVE local-Z offsets.
    ///
    /// Empirical proof: the 2026-07-29 build that used LookRotation(camPos - uiPos)
    /// ("toward") mirrored every label and icon in the headset.
    /// </summary>
    public static class XrUiFacing
    {
        public static void FaceUser(Transform t, Transform camera)
        {
            if (t == null || camera == null) return;
            var away = t.position - camera.position;
            if (away.sqrMagnitude < 1e-8f) return;
            t.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        public static Quaternion RotationFacingUser(Vector3 uiPos, Vector3 camPos)
        {
            var away = uiPos - camPos;
            if (away.sqrMagnitude < 1e-8f) return Quaternion.identity;
            return Quaternion.LookRotation(away.normalized, Vector3.up);
        }
    }
}
