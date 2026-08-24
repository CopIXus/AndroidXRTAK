using TakXr.Core;
using TakXr.Map;
using TakXr.Xr;
using UnityEngine;

namespace TakXr.UI
{
    /// <summary>Persists headset settings via PlayerPrefs (DEM map + feed caps).</summary>
    public class SettingsPanelRuntime : MonoBehaviour
    {
        const string PrefUrl = "takxr.backendUrl";
        const string PrefMoveSpeed = "takxr.moveSpeed";
        /// <summary>Legacy combined icon+text scale — migration seed only.</summary>
        const string PrefCotScale = "takxr.cotScale";
        const string PrefIconScale = "takxr.cotIconScale";
        const string PrefTextScale = "takxr.cotTextScale";
        const string PrefSnapTurn = "takxr.snapTurn";
        const string PrefFallback = "takxr.allowBackendFallback";
        const string PrefWorldPitch = "takxr.worldPitchDeg";

        static readonly float[] ScaleSteps = { 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f, 5f };

        [SerializeField] AppConfig config;
        [SerializeField] DemTerrainMap terrain;

        public float MoveSpeedMultiplier { get; private set; } = 1f;
        /// <summary>Marker ICON size multiplier (0.5 … 5). Applied to angular glyph scale.</summary>
        public float IconScaleMultiplier { get; private set; } = 1f;
        /// <summary>Callsign TEXT size multiplier (0.5 … 5). Applied to label sizing.</summary>
        public float TextScaleMultiplier { get; private set; } = 1f;
        public bool SnapTurnEnabled { get; private set; }
        public float WorldPitchDeg { get; private set; }

        public void Configure(AppConfig cfg, DemTerrainMap dem)
        {
            config = cfg;
            terrain = dem;
            Load();
        }

        public void Load()
        {
            if (config == null) return;
            TakXrStateStore.RestorePrefsIfEmpty();
            if (PlayerPrefs.HasKey(PrefUrl))
                config.backendBaseUrl = PlayerPrefs.GetString(PrefUrl, config.backendBaseUrl);
            if (PlayerPrefs.HasKey(PrefFallback))
                config.allowBackendFallback = PlayerPrefs.GetInt(PrefFallback, 0) != 0;
            MoveSpeedMultiplier = PlayerPrefs.GetFloat(PrefMoveSpeed, 1f);
            // Icon/text scale split from the old single "CoT scale" — seed both
            // from the legacy key on first run so upgrades keep the user's size.
            float legacy = PlayerPrefs.GetFloat(PrefCotScale, 1.5f);
            IconScaleMultiplier = Mathf.Clamp(PlayerPrefs.GetFloat(PrefIconScale, legacy), 0.5f, 5f);
            TextScaleMultiplier = Mathf.Clamp(PlayerPrefs.GetFloat(PrefTextScale, legacy), 0.5f, 5f);
            SnapTurnEnabled = PlayerPrefs.GetInt(PrefSnapTurn, 0) != 0;
            WorldPitchDeg = Mathf.Clamp(PlayerPrefs.GetFloat(PrefWorldPitch, 0f),
                XrWorldRoot.MinPitchDeg, XrWorldRoot.MaxPitchDeg);
            TakIdentity.Load();
        }

        public void Save()
        {
            if (config == null) return;
            PlayerPrefs.SetString(PrefUrl, config.backendBaseUrl);
            PlayerPrefs.SetFloat(PrefMoveSpeed, MoveSpeedMultiplier);
            PlayerPrefs.SetFloat(PrefIconScale, IconScaleMultiplier);
            PlayerPrefs.SetFloat(PrefTextScale, TextScaleMultiplier);
            PlayerPrefs.SetInt(PrefSnapTurn, SnapTurnEnabled ? 1 : 0);
            PlayerPrefs.SetInt(PrefFallback, config.allowBackendFallback ? 1 : 0);
            PlayerPrefs.SetFloat(PrefWorldPitch, WorldPitchDeg);
            PlayerPrefs.Save();
            TakIdentity.Save();
            TakXrStateStore.Capture();
        }

        public void CycleMoveSpeed()
        {
            float[] steps = { 0.5f, 1f, 1.5f, 2.5f };
            int idx = 0;
            for (int i = 0; i < steps.Length; i++)
                if (MoveSpeedMultiplier <= steps[i] + 0.01f) { idx = (i + 1) % steps.Length; break; }
            MoveSpeedMultiplier = steps[idx];
            Save();
        }

        /// <summary>Cycle marker icon size: 0.5× → 0.75× → 1× → 1.5× → 2× → 3× → 4× → 5×.</summary>
        public void CycleIconScale()
        {
            IconScaleMultiplier = NextScaleStep(IconScaleMultiplier);
            Save();
        }

        /// <summary>Cycle callsign text size: 0.5× → 0.75× → 1× → 1.5× → 2× → 3× → 4× → 5×.</summary>
        public void CycleTextScale()
        {
            TextScaleMultiplier = NextScaleStep(TextScaleMultiplier);
            Save();
        }

        static float NextScaleStep(float current)
        {
            int idx = 0;
            for (int i = 0; i < ScaleSteps.Length; i++)
                if (current <= ScaleSteps[i] + 0.01f) { idx = (i + 1) % ScaleSteps.Length; break; }
            return ScaleSteps[idx];
        }

        public void ToggleSnapTurn()
        {
            SnapTurnEnabled = !SnapTurnEnabled;
            Save();
        }

        public void SetSnapTurn(bool on)
        {
            SnapTurnEnabled = on;
            Save();
        }

        public void SetWorldPitch(float deg)
        {
            WorldPitchDeg = Mathf.Clamp(deg, XrWorldRoot.MinPitchDeg, XrWorldRoot.MaxPitchDeg);
            Save();
        }

        public void RebuildTerrain() => terrain?.Rebuild();

        void OnApplicationPause(bool pause)
        {
            if (pause) Save();
        }

        void OnApplicationQuit() => Save();
    }
}
