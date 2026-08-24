#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace TakXr.Editor
{
    /// <summary>
    /// One-shot menu to import TextMeshPro essentials so XrText can use SDF fonts.
    /// Unity 6 embeds TMP in com.unity.ugui — Window → TextMeshPro → Import TMP Essential Resources.
    /// </summary>
    public static class TmpEssentialsImport
    {
        [MenuItem("TakXR/Import TextMeshPro Essentials")]
        public static void Import()
        {
            // Prefer the built-in TMP importer when present.
            var type = System.Type.GetType(
                "TMPro.EditorUtilities.TMP_PackageResourceImporter, Unity.TextMeshPro.Editor");
            if (type != null)
            {
                var method = type.GetMethod("ImportResources",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    method.Invoke(null, new object[] { true, false, false });
                    Debug.Log("[TakXR] TMP Essential Resources import requested.");
                    return;
                }
            }
            EditorUtility.DisplayDialog(
                "TextMeshPro",
                "Open Window → TextMeshPro → Import TMP Essential Resources, then add\n" +
                "  UNITY_TEXTMESHPRO\n" +
                "to Player Settings → Scripting Define Symbols so XrText uses SDF.",
                "OK");
        }
    }
}
#endif
