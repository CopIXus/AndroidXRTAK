using System.IO;
using System.Linq;
using TakXr.Core;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TakXr.Editor
{
    /// <summary>
    /// Batchmode-friendly project bootstrap, Android XR performance defaults, APK build.
    /// </summary>
    public static class TakXrEditorMenu
    {
        const string ScenePath = "Assets/Scenes/TakXrMain.unity";
        const string ConfigPath = "Assets/Resources/AppConfig.asset";

        [MenuItem("TAKXR/Create Default Scene + Config")]
        public static void CreateDefaultScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            Directory.CreateDirectory("Assets/Resources");

            var config = AssetDatabase.LoadAssetAtPath<AppConfig>(ConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<AppConfig>();
                AssetDatabase.CreateAsset(config, ConfigPath);
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var bootstrapGo = new GameObject("TakXrBootstrap");
            var bootstrap = bootstrapGo.AddComponent<TakXrBootstrap>();
            var so = new SerializedObject(bootstrap);
            so.FindProperty("config").objectReferenceValue = config;
            so.FindProperty("startWithMap").boolValue = true;
            so.FindProperty("startFeedOnAwake").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();

            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.16f, 0.24f, 1f);
            cam.transform.position = new Vector3(0, 25, -40);
            cam.transform.LookAt(Vector3.zero);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };
            AssetDatabase.SaveAssets();
            Debug.Log($"[TakXr] Created {ScenePath} and {ConfigPath}");
        }

        [MenuItem("TAKXR/Configure Android XR Performance")]
        public static void ConfigureAndroidXrPerformance()
        {
            PlayerSettings.companyName = "CopIX";
            PlayerSettings.productName = "TAKXR";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "us.copix.takxr");
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel28;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.Android.forceSDCardPermission = false;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            // Vulkan first (Android XR), GLES3 fallback so phones don't black-screen on bad Vulkan drivers.
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                GraphicsDeviceType.Vulkan,
                GraphicsDeviceType.OpenGLES3
            });
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.MTRendering = true;
            PlayerSettings.SetMobileMTRendering(BuildTargetGroup.Android, true);

            // Both Input System + old input (XR Toolkit / desktop WASD)
            try
            {
                var prop = typeof(PlayerSettings).GetProperty("activeInputHandler");
                if (prop != null)
                {
                    var enumType = prop.PropertyType;
                    var both = System.Enum.Parse(enumType, "InputSystemPackageAndOlder");
                    prop.SetValue(null, both);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TakXr] Could not set activeInputHandler: {ex.Message}");
            }

            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 4;
            EnsureUrpAsset();
            EnsureAndroidSdkPaths();

            DefineCesium();
            Debug.Log("[TakXr] Android XR performance defaults applied (Vulkan-only, IL2CPP, ARM64, MSAA 4x).");
        }

        static void EnsureUrpAsset()
        {
            const string path = "Assets/Settings/URP-TakXr.asset";
            const string rendererPath = "Assets/Settings/URP-TakXr-Renderer.asset";
            Directory.CreateDirectory("Assets/Settings");

            var urpType = FindType(
                "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset",
                "Unity.RenderPipelines.Universal.Runtime");
            var rendererType = FindType(
                "UnityEngine.Rendering.Universal.UniversalRendererData",
                "Unity.RenderPipelines.Universal.Runtime");
            if (urpType == null || rendererType == null)
            {
                Debug.LogWarning("[TakXr] URP package not resolved yet — GraphicsSettings left on Built-in until reimport.");
                return;
            }

            var renderer = AssetDatabase.LoadAssetAtPath(rendererPath, rendererType) as ScriptableObject;
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance(rendererType) as ScriptableObject;
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }

            var asset = AssetDatabase.LoadAssetAtPath(path, urpType) as ScriptableObject;
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance(urpType) as ScriptableObject;
                AssetDatabase.CreateAsset(asset, path);
            }

            var so = new SerializedObject(asset);
            var rendererList = so.FindProperty("m_RendererDataList");
            if (rendererList != null)
            {
                rendererList.arraySize = 1;
                rendererList.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            }
            var defaultIndex = so.FindProperty("m_DefaultRendererIndex");
            if (defaultIndex != null) defaultIndex.intValue = 0;
            SetBool(so, "m_SupportsHDR", false);
            SetInt(so, "m_MSAA", 4);
            so.ApplyModifiedPropertiesWithoutUndo();

            var pipeline = asset as RenderPipelineAsset;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            // Keep Unlit/Lit always available so runtime Shader.Find works on device.
            try
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
                if (assets != null && assets.Length > 0)
                {
                    var gso = new SerializedObject(assets[0]);
                    var always = gso.FindProperty("m_AlwaysIncludedShaders");
                    if (always != null)
                    {
                        void Include(string shaderName)
                        {
                            var sh = Shader.Find(shaderName);
                            if (sh == null) return;
                            for (int i = 0; i < always.arraySize; i++)
                            {
                                if (always.GetArrayElementAtIndex(i).objectReferenceValue == sh) return;
                            }
                            always.InsertArrayElementAtIndex(always.arraySize);
                            always.GetArrayElementAtIndex(always.arraySize - 1).objectReferenceValue = sh;
                        }
                        Include("Universal Render Pipeline/Unlit");
                        Include("Universal Render Pipeline/Lit");
                        Include("Unlit/Color");
                        Include("Sprites/Default");
                        Include("UI/Default");
                        gso.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TakXr] AlwaysIncludedShaders: {ex.Message}");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[TakXr] URP asset ready at {path} with renderer {rendererPath}");
        }

        static void EnsureAndroidSdkPaths()
        {
            try
            {
                var userSdk = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "Android", "Sdk");
                var unityAndroid = Path.Combine(
                    EditorApplication.applicationContentsPath,
                    "PlaybackEngines", "AndroidPlayer");
                var unitySdk = Path.Combine(unityAndroid, "SDK");
                var unityJdk = Path.Combine(unityAndroid, "OpenJDK");
                var unityNdk = Path.Combine(unityAndroid, "NDK");

                bool SdkLooksComplete(string root) =>
                    !string.IsNullOrEmpty(root)
                    && Directory.Exists(Path.Combine(root, "platforms"))
                    && (File.Exists(Path.Combine(root, "cmdline-tools", "latest", "bin", "sdkmanager.bat"))
                        || File.Exists(Path.Combine(root, "tools", "bin", "sdkmanager.bat"))
                        || Directory.Exists(Path.Combine(root, "platform-tools")));

                // Prefer the user SDK — Unity Hub's bundled SDK is often incomplete without elevation.
                string sdk = SdkLooksComplete(userSdk) ? userSdk
                    : SdkLooksComplete(unitySdk) ? unitySdk
                    : Directory.Exists(Path.Combine(userSdk, "platforms")) ? userSdk
                    : null;

                string ndk = null;
                var userNdkRoot = Path.Combine(userSdk, "ndk");
                if (Directory.Exists(userNdkRoot))
                {
                    var versions = Directory.GetDirectories(userNdkRoot);
                    if (versions.Length > 0)
                        ndk = versions[versions.Length - 1];
                }
                if (ndk == null && Directory.Exists(unityNdk))
                    ndk = unityNdk;

                var toolsType = FindType("UnityEditor.Android.AndroidExternalToolsSettings", "UnityEditor.Android.Extensions");
                if (toolsType != null)
                {
                    void SetPath(string prop, string value)
                    {
                        if (string.IsNullOrEmpty(value) || !Directory.Exists(value)) return;
                        var p = toolsType.GetProperty(prop, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (p != null && p.CanWrite) p.SetValue(null, value);
                    }
                    SetPath("sdkRootPath", sdk);
                    SetPath("ndkRootPath", ndk);
                    SetPath("jdkRootPath", Directory.Exists(unityJdk) ? unityJdk : null);
                }

                if (!string.IsNullOrEmpty(sdk))
                    EditorPrefs.SetString("AndroidSdkRoot", sdk);
                if (!string.IsNullOrEmpty(ndk))
                    EditorPrefs.SetString("AndroidNdkRoot", ndk);
                if (Directory.Exists(unityJdk))
                    EditorPrefs.SetString("JdkPath", unityJdk);

                Debug.Log($"[TakXr] Android tools sdk={sdk} ndk={ndk} jdk={(Directory.Exists(unityJdk) ? unityJdk : "default")}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TakXr] EnsureAndroidSdkPaths: {ex.Message}");
            }
        }

        static void SetBool(SerializedObject so, string name, bool value)
        {
            var p = so.FindProperty(name);
            if (p != null) p.boolValue = value;
        }

        static void SetInt(SerializedObject so, string name, int value)
        {
            var p = so.FindProperty(name);
            if (p != null) p.intValue = value;
        }

        [MenuItem("TAKXR/Define CESIUM_AVAILABLE")]
        public static void DefineCesium()
        {
            void Ensure(BuildTargetGroup group)
            {
                var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
                if (defines.Contains("CESIUM_AVAILABLE")) return;
                defines = string.IsNullOrEmpty(defines) ? "CESIUM_AVAILABLE" : defines + ";CESIUM_AVAILABLE";
                PlayerSettings.SetScriptingDefineSymbolsForGroup(group, defines);
            }

            Ensure(BuildTargetGroup.Android);
            Ensure(BuildTargetGroup.Standalone);
            Debug.Log("[TakXr] CESIUM_AVAILABLE scripting define set for Android + Standalone.");
        }

        [MenuItem("TAKXR/Prepare Project (scene + Android XR)")]
        public static void PrepareProject()
        {
            ConfigureAndroidXrPerformance();
            CreateDefaultScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[TakXr] Project prepared for Android XR build.");
        }

        [MenuItem("TAKXR/Build Android APK")]
        public static void BuildAndroidApk()
        {
            PrepareProject();
            EnsureOpenXrReady();

            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                Debug.LogWarning("[TakXr] SwitchActiveBuildTarget(Android) returned false — continuing anyway.");

            Directory.CreateDirectory("Builds/Android");
            var outPath = "Builds/Android/TAKXR.apk";

            var scenes = EditorBuildSettings.scenes;
            if (scenes == null || scenes.Length == 0 || scenes.All(s => string.IsNullOrEmpty(s.path)))
            {
                CreateDefaultScene();
                scenes = EditorBuildSettings.scenes;
            }

            var opts = new BuildPlayerOptions
            {
                scenes = System.Array.ConvertAll(scenes, s => s.path),
                locationPathName = outPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            // OpenXR often needs a warm-up build pass after first import.
            BuildReport report = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                EnsureOpenXrReady();
                AssetDatabase.SaveAssets();
                report = BuildPipeline.BuildPlayer(opts);
                if (report.summary.result == BuildResult.Succeeded)
                    break;
                Debug.LogWarning($"[TakXr] Android build attempt {attempt} failed ({report.summary.result}); retrying after OpenXR warm-up...");
            }

            if (report == null || report.summary.result != BuildResult.Succeeded)
                throw new System.Exception($"Android build failed: {report?.summary.result}");
            Debug.Log($"[TakXr] APK built: {Path.GetFullPath(outPath)}");
        }

        static void EnsureOpenXrReady()
        {
            // Create/register OpenXR Package Settings BEFORE BuildPipeline starts.
            // Otherwise OpenXR throws: "Settings found in project but not yet loaded".
            try
            {
                var packageSettingsType = FindType(
                    "UnityEditor.XR.OpenXR.OpenXRPackageSettings",
                    "Unity.XR.OpenXR.Editor");
                if (packageSettingsType != null)
                {
                    var getOrCreate = packageSettingsType.GetMethod("GetOrCreateInstance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var instance = getOrCreate?.Invoke(null, null);
                    var getSettings = packageSettingsType.GetMethod("GetSettingsForBuildTargetGroup",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    getSettings?.Invoke(instance, new object[] { BuildTargetGroup.Android });
                    getSettings?.Invoke(instance, new object[] { BuildTargetGroup.Standalone });
                    Debug.Log("[TakXr] OpenXRPackageSettings registered for Android + Standalone");
                }

                var xrPerTargetType = FindType(
                    "UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget",
                    "Unity.XR.Management.Editor");
                if (xrPerTargetType != null)
                {
                    var getOrCreate = xrPerTargetType.GetMethod("GetOrCreate",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var perTarget = getOrCreate?.Invoke(null, null);

                    var createSettings = xrPerTargetType.GetMethod("CreateDefaultSettingsForBuildTarget",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var createManager = xrPerTargetType.GetMethod("CreateDefaultManagerSettingsForBuildTarget",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var settingsFor = xrPerTargetType.GetMethod("SettingsForBuildTarget",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var managerFor = xrPerTargetType.GetMethod("ManagerSettingsForBuildTarget",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    var androidSettings = settingsFor?.Invoke(perTarget, new object[] { BuildTargetGroup.Android });
                    if (androidSettings == null)
                        createSettings?.Invoke(perTarget, new object[] { BuildTargetGroup.Android });

                    var manager = managerFor?.Invoke(perTarget, new object[] { BuildTargetGroup.Android });
                    if (manager == null)
                    {
                        createManager?.Invoke(perTarget, new object[] { BuildTargetGroup.Android });
                        manager = managerFor?.Invoke(perTarget, new object[] { BuildTargetGroup.Android });
                    }

                    // Immersive Galaxy XR: OpenXR + init on start. Launch while wearing the
                    // headset from XR home so the runtime is available.
                    androidSettings = settingsFor?.Invoke(perTarget, new object[] { BuildTargetGroup.Android });
                    if (androidSettings != null)
                    {
                        var initProp = androidSettings.GetType().GetProperty("InitManagerOnStart");
                        initProp?.SetValue(androidSettings, true);
                        var so = new SerializedObject(androidSettings as Object);
                        var p = so.FindProperty("m_InitManagerOnStart");
                        if (p != null)
                        {
                            p.boolValue = true;
                            so.ApplyModifiedPropertiesWithoutUndo();
                        }
                    }

                    if (manager != null)
                    {
                        var openXrLoader = AssetDatabase.LoadAssetAtPath<Object>("Assets/XR/Loaders/OpenXRLoader.asset");
                        if (openXrLoader == null)
                        {
                            // Create via ScriptableObject if the asset is missing.
                            var loaderType = FindType("UnityEngine.XR.OpenXR.OpenXRLoader", "Unity.XR.OpenXR");
                            if (loaderType != null)
                            {
                                Directory.CreateDirectory("Assets/XR/Loaders");
                                openXrLoader = ScriptableObject.CreateInstance(loaderType);
                                AssetDatabase.CreateAsset(openXrLoader, "Assets/XR/Loaders/OpenXRLoader.asset");
                            }
                        }

                        var tryAdd = manager.GetType().GetMethod("TryAddLoader",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (openXrLoader != null && tryAdd != null)
                        {
                            tryAdd.Invoke(manager, new[] { openXrLoader });
                            Debug.Log("[TakXr] Android OpenXRLoader assigned for immersive XR");
                        }
                        else
                        {
                            Debug.LogWarning("[TakXr] Could not assign OpenXRLoader — immersive may stay flat");
                        }
                    }

                    EnableAndroidXrOpenXrFeatures();
                }

                AssetDatabase.SaveAssets();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TakXr] EnsureOpenXrReady: {ex}");
            }
        }

        static void EnableAndroidXrOpenXrFeatures()
        {
            try
            {
                var settings = AssetDatabase.LoadAllAssetsAtPath("Assets/XR/Settings/OpenXR Package Settings.asset");
                if (settings == null) return;
                string[] enableNames =
                {
                    "AndroidXRSupportFeature Android",
                    "OpenXRLifeCycleFeature Android",
                    "HandInteractionProfile Android",
                    "HandTracking Android",
                    "EyeGazeInteraction Android",
                    "KHRSimpleControllerProfile Android",
                    "DisplayUtilitiesFeature Android"
                };
                foreach (var obj in settings)
                {
                    if (obj == null) continue;
                    foreach (var want in enableNames)
                    {
                        if (obj.name != want) continue;
                        var so = new SerializedObject(obj);
                        var en = so.FindProperty("m_enabled");
                        if (en != null && !en.boolValue)
                        {
                            en.boolValue = true;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            Debug.Log($"[TakXr] Enabled OpenXR feature: {want}");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TakXr] EnableAndroidXrOpenXrFeatures: {ex.Message}");
            }
        }

        static System.Type FindType(string fullName, string assemblyName)
        {
            var t = System.Type.GetType($"{fullName}, {assemblyName}");
            if (t != null) return t;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrEmpty(assemblyName) && asm.GetName().Name != assemblyName) continue;
                t = asm.GetType(fullName);
                if (t != null) return t;
            }
            // Fallback: scan all assemblies
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
