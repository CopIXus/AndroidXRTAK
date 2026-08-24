# TAKXR Android XR (Samsung Galaxy XR)

Native Unity 6 + OpenXR Android XR APK â€” a standalone VR ATAK/COP client.
Connects **directly** to a TAK Server (CoT TLS + Marti REST) with an enrolled
client certificate. The LXC web backend is optional (`allowBackendFallback`,
default **off**).

Sibling of `T:\CopIX\TAKXR`. Product brief: [`../TAKXR/docs/XR-APK-SPIKE.md`](../TAKXR/docs/XR-APK-SPIKE.md).

**GitHub:** [CopIXus/AndroidXRTAK](https://github.com/CopIXus/AndroidXRTAK) — public source + Release APKs (no enrolled cert in the published APK).

## Stack

| Layer | Package / setting |
|---|---|
| Engine | Unity **6000.3.20f1** (or newer 6.3) |
| XR | OpenXR + **Android XR** + Hand Interaction |
| Render | URP + **Vulkan only**, MSAA 2x, HDR off |
| Map | **DEM (Terrarium) + ESRI/Google** via UnityWebRequest (Cesium kept disabled); disk cache under `/sdcard/takxr/tiles` when writable |
| Data | **Direct TAK** CoT stream (8089) + Marti REST (8443); optional LXC fallback |
| Icons | StreamingAssets `map-icons/` (Default + Public Safety Air + Generic Icons) |
| Interaction | Pinch-drag pan, 2-hand zoom, sticks, snap-turn, tap-select, Follow, video |

## One-time setup

```powershell
winget install Unity.UnityHub
.\ci\install-unity-editor.ps1
.\ci\ensure-android-sdk.ps1
.\ci\fetch-cesium.ps1   # optional; Cesium kept disabled on-device
```

Open this folder in Unity Hub â†’ **Add** â†’ `T:\CopIX\TAKXR-AndroidXR`.

Menu **TAKXR â†’ Prepare Project (scene + Android XR)** creates the scene, URP asset, OpenXR loader, and Vulkan/IL2CPP/ARM64 defaults.


## Server connection (local only — never commit)

**Do not commit** enrolled certs, passwords, or real TAK server hostnames to GitHub.

On your build machine only:

```powershell
copy Assets\StreamingAssets\local-config.json.example Assets\StreamingAssets\local-config.json
# Edit local-config.json with your TAK host, ports, and P12 password
# Place enrolled PKCS#12 at Assets\StreamingAssets\takclient.p12
```

Before pushing to GitHub:

```powershell
.\ci\verify-no-secrets.ps1
git config core.hooksPath .githooks
```

Per-server certs: Servers panel → **Import cert** (`persistentDataPath/tak-certs/pending.p12`). On-device P12 import is the supported enrollment path.

## Build APK

Network shares are flaky for Unity Library â€” mirror to `C:\Temp\TAKXR-AndroidXR`:

```powershell
.\ci\build-android.ps1 -UseLocalMirror
```

Output: `Builds/Android/TAKXR.apk` (`us.copix.takxr`).

**Build notes:** StreamingAssets `map-icons/` adds ~1.2 MB. Unity regenerates
`.meta` for new scripts/icons on first open â€” commit metas after the first
import. New scripts: `TakIdentity`, `SelfPresence`, `TakCertStore`,
`AppLifecycleHost`, `XrSettingsPanel`, `IconResolver`.


## GitHub Release APK (CI)

Release builds run on a **self-hosted Windows runner** with Unity installed:

```powershell
# One-time on your Unity build PC:
.\ci\setup-github-runner.ps1 -Token <token-from-github-runners-new>
# keep .\run.cmd running (or install as a service)

# Publish:
git tag v0.1.2
git push origin v0.1.2
```

Or Actions → **Release APK** → Run workflow. Public Release APKs have no TAK cert baked in.
## Sideload to Galaxy XR

Download `TAKXR.apk` from [Releases](https://github.com/CopIXus/AndroidXRTAK/releases), or build locally:

```powershell
.\ci\build-android.ps1 -UseLocalMirror
.\ci\sideload-apk.ps1 -Serial <device>
# Activity:
adb shell am start -n us.copix.takxr/com.unity3d.player.UnityPlayerGameActivity
```

Connection failures (e.g. QR add â†’ TCP timeout) show under the server row and as a flash toast. Tools â†’ **Diagnostics** dumps recent TAK connect log lines (also in `adb logcat -s Unity`).

## Map tile cache (ATAK-style)

DEM + basemap PNGs are stored under **`/sdcard/takxr/tiles/`** when external storage is writable (survives updates; may survive uninstall depending on Android scoped-storage policy). Otherwise falls back to app `persistentDataPath/takxr/tiles` (survives updates only). Soft cap ~512 MB with oldest-first prune.

Cesiumâ€™s built-in SQLite cache cannot use a shared `/takxr` folder and is unused while Cesium is disabled.

## Headset controls

| Input | Action |
|---|---|
| Hold B/Y + stick Y | Pitch the world: stick forward raises the map in front of you, back tilts it down (release to keep; Settings â†’ Flatten) |
| Pinch / trigger + drag | Pan the map (world moves) |
| Two-hand pinch stretch / twist | Zoom / rotate |
| Left stick | Fly / strafe |
| Right stick | Altitude; yaw (or snap-turn 45Â° when enabled in Settings) |
| Short pinch tap on marker | Radial menu (Details / Follow / Go To / Video / R&B / Delete) |
| Tools â†’ hamburger | Opens Tools menu (Maps, Layers, Routes, Point Drop, Servers, Settings, â€¦) |

## Identity & channels

Self SA publishes every ~7s over the Direct stream (`TakIdentity.ClientUid`).
Marti `SetActiveGroups` uses the **same** clientUid. Server-side channel filters
require matching presence; local package/mission layer hide works without it.

## FPS targets

| Mode | Target |
|---|---|
| Idle / settled map | â‰¥ 72 |
| Flying, map ON, ~100 CoTs | â‰¥ 45 sustained |

## Scripts layout

```
Assets/Scripts/
  Core/        AppConfig, TakIdentity, TakCertStore, TakServerDirectory, TakXrStateStore, TakQrParser, TakXrBootstrap, AppLifecycleHost
  Cot/         Direct hub + sessions, Marti feed, SelfPresence, IconResolver, markers, Follow, COP
  Map/         DemTerrainMap (primary), CesiumMapController (disabled)
  Locomotion/  XrWorldLocomotion (incl. snap-turn + world tilt)
  UI/          XrChromeHud, XrSettingsPanel, XrServerPanel, XrQrScanPanel, XrLayersPanel, draw/info/video
  Xr/          Rig, hands, head pose, world root
```

## Notes

- Prefer local mirror builds (`C:\Temp\â€¦`) when the project lives on `T:` / UNC.
- Ensure `.meta` files are committed so GUIDs stay stable across machines.
- Cesium for Unity remains in the project for a possible future TLS fix; runtime map is DEM+imagery.
- `allowBackendFallback` (Settings) re-enables LXC health/snapshot fallback â€” leave off for true standalone.
