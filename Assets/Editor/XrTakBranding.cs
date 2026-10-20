using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace TakXr.Editor
{
    /// <summary>
    /// Brands the Android launcher as XRTAK and assigns the transparent shield icon.
    /// </summary>
    public static class XrTakBranding
    {
        const string MasterIconPath = "Assets/Brand/XRTAK-icon.png";
        const string ForegroundIconPath = "Assets/Brand/XRTAK-icon-foreground.png";

        [MenuItem("TAKXR/Apply XRTAK Branding (name + icon)")]
        public static void Apply()
        {
            EnsureIconImportSettings(MasterIconPath);
            EnsureIconImportSettings(ForegroundIconPath);
            foreach (var size in new[] { 36, 48, 72, 96, 108, 144, 162, 192, 216, 324, 432 })
            {
                string p = $"Assets/Brand/XRTAK-android-{size}.png";
                if (File.Exists(p))
                    EnsureIconImportSettings(p);
            }

            AssetDatabase.ImportAsset(MasterIconPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            PlayerSettings.companyName = "CopIX";
            PlayerSettings.productName = "XRTAK";
            // Keep stable package id so existing headset installs can update in place.
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "us.copix.takxr");

            var master = AssetDatabase.LoadAssetAtPath<Texture2D>(MasterIconPath);
            if (master == null)
            {
                Debug.LogError("[XRTAK] Missing icon at " + MasterIconPath);
                return;
            }

            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { master }, IconKind.Any);
            PlayerSettings.SetIcons(NamedBuildTarget.Android, new[] { master }, IconKind.Any);

            AssignAndroidPlatformIcons(master);
            AssetDatabase.SaveAssets();
            Debug.Log("[XRTAK] productName=XRTAK, launcher icon assigned (transparent shield).");
        }

        static void AssignAndroidPlatformIcons(Texture2D master)
        {
            var platform = NamedBuildTarget.Android;
            var kinds = PlayerSettings.GetSupportedIconKinds(platform);
            if (kinds == null || kinds.Length == 0) return;

            var foreground = AssetDatabase.LoadAssetAtPath<Texture2D>(ForegroundIconPath) ?? master;

            foreach (var kind in kinds)
            {
                var icons = PlayerSettings.GetPlatformIcons(platform, kind);
                if (icons == null || icons.Length == 0) continue;

                string kindName = kind.ToString();
                bool adaptive = kindName.IndexOf("Adaptive", System.StringComparison.OrdinalIgnoreCase) >= 0;

                for (int i = 0; i < icons.Length; i++)
                {
                    int w = icons[i].width;
                    Texture2D sized = LoadSized(w) ?? (adaptive ? foreground : master);
                    // Adaptive icons accept layer 0 = background (optional), layer 1 = foreground.
                    // For a transparent shield, use the same art as foreground and a clear/dark plate.
                    if (adaptive)
                    {
                        var bg = MakeSolidTexture(w, new Color(0.05f, 0.06f, 0.08f, 1f));
                        icons[i].SetTextures(new[] { bg, sized });
                    }
                    else
                    {
                        icons[i].SetTexture(sized);
                    }
                }

                PlayerSettings.SetPlatformIcons(platform, kind, icons);
            }
        }

        static Texture2D LoadSized(int width)
        {
            string path = $"Assets/Brand/XRTAK-android-{width}.png";
            if (!File.Exists(path)) return null;
            EnsureIconImportSettings(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Texture2D MakeSolidTexture(int size, Color color)
        {
            string path = $"Assets/Brand/XRTAK-adaptive-bg-{size}.png";
            if (!File.Exists(path))
            {
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var fill = Enumerable.Repeat(color, size * size).ToArray();
                tex.SetPixels(fill);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            EnsureIconImportSettings(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static void EnsureIconImportSettings(string assetPath)
        {
            if (!File.Exists(assetPath)) return;
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;
            bool dirty = false;
            if (importer.textureType != TextureImporterType.Default)
            {
                importer.textureType = TextureImporterType.Default;
                dirty = true;
            }
            if (!importer.alphaIsTransparency)
            {
                importer.alphaIsTransparency = true;
                dirty = true;
            }
            if (importer.npotScale != TextureImporterNPOTScale.None)
            {
                importer.npotScale = TextureImporterNPOTScale.None;
                dirty = true;
            }
            if (importer.mipmapEnabled)
            {
                importer.mipmapEnabled = false;
                dirty = true;
            }
            if (importer.maxTextureSize < 1024)
            {
                importer.maxTextureSize = 1024;
                dirty = true;
            }
            if (dirty)
            {
                importer.SaveAndReimport();
            }
        }

    }
}
