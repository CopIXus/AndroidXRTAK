using UnityEngine;

namespace TakXr.UI
{
    /// <summary>
    /// Quarantined: superseded by <see cref="XrChromeHud"/>. Kept as an empty
    /// MonoBehaviour so existing .meta GUIDs stay valid; do not Create() or wire.
    /// </summary>
    [System.Obsolete("Use XrChromeHud instead")]
    public class XrToolbarHud : MonoBehaviour
    {
        void Awake() => enabled = false;
    }
}
