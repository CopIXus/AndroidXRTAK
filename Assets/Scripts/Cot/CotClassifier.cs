using UnityEngine;

namespace TakXr.Cot
{
    /// <summary>Marker archetype a CoT renders as. See CotClassifier.Classify.</summary>
    public enum MarkerKind
    {
        /// <summary>VRTAK XR observer (takv platform VRTAK-XR) — upright
        /// standing-person billboard with a flat gaze wedge on the ground.</summary>
        Observer,
        /// <summary>Video stream / sensor point — procedural camera disc, flat.</summary>
        Video,
        /// <summary>dCFS dispatch callsign — orange "C" badge, flat.</summary>
        Dcfs,
        /// <summary>ATAK team member — colored team dot with heading, flat.</summary>
        TeamMember,
        /// <summary>Fixed-wing aircraft — procedural silhouette, flat.</summary>
        AircraftFixed,
        /// <summary>Rotary-wing aircraft — procedural silhouette, flat.</summary>
        AircraftRotary,
        /// <summary>On-device iconset PNG (usericon path or explicit 2525b type match).</summary>
        LocalIcon,
        /// <summary>Remote http(s) iconUrl (backend/non-direct feeds only).</summary>
        RemoteIcon,
        /// <summary>Fallback affiliation / marker-color dot, flat.</summary>
        Dot,
    }

    /// <summary>
    /// Single source of truth for CoT → marker classification.
    ///
    /// ============================ CONTRACT WARNING ============================
    /// The branch ORDER below is a contract shared with the web XR client and is
    /// asserted by SelfTest() on every app launch (called from TakXrBootstrap).
    /// If you reorder, add, or weaken a branch, SelfTest will Debug.LogError on
    /// first launch instead of silently shipping broken icons. Update the test
    /// table together with any deliberate contract change.
    ///
    /// Priority order (highest first):
    ///   1. Observer   — takv platform VRTAK-XR, OR observer type (b-m-p-s-p-loc)
    ///                   with our "takxr-" uid prefix. MUST come before Video:
    ///                   VRTAK observers publish the sensor-point type which the
    ///                   Video branch would otherwise swallow, and they also carry
    ///                   team + endpoint which TeamMember would swallow.
    ///   2. Video      — detail.video.url non-empty OR type starts "b-m-p-s-p-loc"
    ///                   (excluding VRTAK observers)
    ///   3. dCFS       — callsign matches /^dCFS:/i
    ///   4. TeamMember — ATAK team name or contact endpoint present
    ///   5. Aircraft   — third dash token of type == "A" (rotary when a later token == "H")
    ///   6. LocalIcon  — on-device IconResolver PNG (usericon path / 2525b type), explicit only
    ///   7. RemoteIcon — absolute http(s) iconUrl
    ///   8. Dot        — affiliation / marker-color dot
    /// ==========================================================================
    /// </summary>
    public static class CotClassifier
    {
        /// <summary>
        /// Production classification: probes IconResolver for an explicit local icon.
        /// The IconResolver affiliation-DEFAULT fallback deliberately does NOT count
        /// as a local icon — otherwise every CoT would classify LocalIcon and the
        /// Dot branch (web affiliation-dot parity) would be dead code.
        /// </summary>
        public static MarkerKind Classify(NormalizedCot cot) =>
            Classify(cot, cot != null && IconResolver.HasExplicitIcon(cot));

        /// <summary>
        /// Pure core — no file I/O, no Unity state. <paramref name="localIconAvailable"/>
        /// injects the IconResolver probe so SelfTest stays deterministic.
        /// </summary>
        public static MarkerKind Classify(NormalizedCot cot, bool localIconAvailable)
        {
            if (cot == null) return MarkerKind.Dot;
            if (IsObserverCot(cot)) return MarkerKind.Observer;
            if (IsVideoCot(cot)) return MarkerKind.Video;
            if (IsDcfsCot(cot)) return MarkerKind.Dcfs;
            if (IsTeamMemberCot(cot)) return MarkerKind.TeamMember;
            if (IsAircraftCot(cot, out bool rotary))
                return rotary ? MarkerKind.AircraftRotary : MarkerKind.AircraftFixed;
            if (localIconAvailable) return MarkerKind.LocalIcon;
            if (!string.IsNullOrEmpty(cot.iconUrl)) return MarkerKind.RemoteIcon;
            return MarkerKind.Dot;
        }

        /// <summary>
        /// VRTAK XR observer: &lt;takv platform="VRTAK-XR"/&gt; is authoritative;
        /// as a fallback (takv stripped by a relay), the observer sensor-point
        /// type combined with our "takxr-" uid prefix. A WinTAK/ATAK takv or a
        /// generic sensor point never matches — those stay Video/other kinds.
        /// </summary>
        public static bool IsObserverCot(NormalizedCot cot)
        {
            var platform = cot?.detail?.takv?.platform;
            if (!string.IsNullOrEmpty(platform)
                && platform.StartsWith("VRTAK", System.StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrEmpty(cot?.type)
                && cot.type.StartsWith("b-m-p-s-p-loc")
                && !string.IsNullOrEmpty(cot.uid)
                && cot.uid.StartsWith("takxr-", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Video marker: explicit stream URL, or sensor-point type that is NOT a
        /// VRTAK observer (observers share the b-m-p-s-p-loc prefix but must not
        /// steal the camera glyph).
        /// </summary>
        public static bool IsVideoCot(NormalizedCot cot)
        {
            if (cot == null) return false;
            if (cot.detail?.video != null && !string.IsNullOrEmpty(cot.detail.video.url))
                return true;
            if (IsObserverCot(cot)) return false;
            return !string.IsNullOrEmpty(cot.type) && cot.type.StartsWith("b-m-p-s-p-loc");
        }

        /// <summary>Web isDcfsMarkerCot parity: callsign matches /^dCFS:/i.</summary>
        public static bool IsDcfsCot(NormalizedCot cot)
        {
            var cs = cot?.Callsign;
            if (string.IsNullOrEmpty(cs)) return false;
            return cs.TrimStart().StartsWith("dCFS:", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Web isTeamMemberCot parity: ATAK team name or contact endpoint.</summary>
        public static bool IsTeamMemberCot(NormalizedCot cot) =>
            !string.IsNullOrEmpty(cot?.detail?.team?.name)
            || !string.IsNullOrEmpty(cot?.contact?.endpoint);

        /// <summary>
        /// Aircraft CoT: "a-?-A-…" (third dash token is the Air battle dimension).
        /// Rotary when any later token is "H" (e.g. a-f-A-C-H helicopters).
        /// </summary>
        public static bool IsAircraftCot(NormalizedCot cot, out bool rotary)
        {
            rotary = false;
            if (string.IsNullOrEmpty(cot?.type)) return false;
            var parts = cot.type.Split('-');
            if (parts.Length < 3
                || !string.Equals(parts[0], "a", System.StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[2], "A", System.StringComparison.OrdinalIgnoreCase))
                return false;
            for (int i = 3; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], "H", System.StringComparison.OrdinalIgnoreCase))
                {
                    rotary = true;
                    break;
                }
            }
            return true;
        }

        // ------------------------------------------------------------ self-test

        struct Case
        {
            public string Name;
            public NormalizedCot Cot;
            public bool LocalIcon;
            public MarkerKind Expected;
        }

        /// <summary>
        /// Contract test for the classification order, run at every app start
        /// (TakXrBootstrap). Logs "[CotClassifier] self-test PASS" or a loud
        /// Debug.LogError per failing case. Returns true when all cases pass.
        /// </summary>
        public static bool SelfTest()
        {
            var cases = new[]
            {
                new Case
                {
                    Name = "VRTAK observer sensor point (takv) beats video (order 1 > 2)",
                    Cot = Make(type: "b-m-p-s-p-loc", takvPlatform: "VRTAK-XR",
                        team: "Cyan", endpoint: "*:-1:stcp"),
                    Expected = MarkerKind.Observer,
                },
                new Case
                {
                    Name = "VRTAK takv on atom type beats team dot (order 1 > 4)",
                    Cot = Make(type: "a-f-G-U-C", takvPlatform: "VRTAK-XR",
                        team: "Cyan", endpoint: "*:-1:stcp"),
                    Expected = MarkerKind.Observer,
                },
                new Case
                {
                    Name = "observer by takxr uid + observer type (takv stripped by relay)",
                    Cot = Make(type: "b-m-p-s-p-loc", uid: "takxr-0a1b2c3d4e5f"),
                    Expected = MarkerKind.Observer,
                },
                new Case
                {
                    Name = "foreign takv (ATAK) sensor point stays Video, not Observer",
                    Cot = Make(type: "b-m-p-s-p-loc", takvPlatform: "ATAK"),
                    Expected = MarkerKind.Video,
                },
                new Case
                {
                    Name = "video sensor with stream URL",
                    Cot = Make(type: "b-m-p-s-p-loc", videoUrl: "rtsp://10.0.0.5/cam1"),
                    Expected = MarkerKind.Video,
                },
                new Case
                {
                    Name = "video sensor point without URL (b-m-p-s-p-loc-video)",
                    Cot = Make(type: "b-m-p-s-p-loc-video"),
                    Expected = MarkerKind.Video,
                },
                new Case
                {
                    Name = "ground unit with stream URL beats unit branches",
                    Cot = Make(type: "a-f-G-U-C", videoUrl: "https://vid/stream.m3u8", team: "Cyan"),
                    Expected = MarkerKind.Video,
                },
                new Case
                {
                    Name = "dCFS callsign",
                    Cot = Make(type: "a-f-G", callsign: "dCFS: Engine 4"),
                    Expected = MarkerKind.Dcfs,
                },
                new Case
                {
                    Name = "dCFS callsign WITH video URL → video wins (order 2 > 3)",
                    Cot = Make(type: "a-f-G", callsign: "dCFS: Cam 7", videoUrl: "rtsp://x/1"),
                    Expected = MarkerKind.Video,
                },
                new Case
                {
                    Name = "team member by __group team name",
                    Cot = Make(type: "a-f-G-U-C", callsign: "JCPD_Ludlow_942", team: "Dark Blue"),
                    Expected = MarkerKind.TeamMember,
                },
                new Case
                {
                    Name = "team member by contact endpoint only",
                    Cot = Make(type: "a-f-G-E-V", endpoint: "*:-1:stcp"),
                    Expected = MarkerKind.TeamMember,
                },
                new Case
                {
                    Name = "fixed-wing aircraft",
                    Cot = Make(type: "a-f-A-M-F"),
                    Expected = MarkerKind.AircraftFixed,
                },
                new Case
                {
                    Name = "rotary aircraft",
                    Cot = Make(type: "a-f-A-C-H"),
                    Expected = MarkerKind.AircraftRotary,
                },
                new Case
                {
                    Name = "aircraft WITH endpoint → team dot wins (order 4 > 5)",
                    Cot = Make(type: "a-f-A-M-H", endpoint: "*:-1:stcp"),
                    Expected = MarkerKind.TeamMember,
                },
                new Case
                {
                    Name = "usericon package pin resolves local icon",
                    Cot = Make(type: "b-m-p-w", iconsetpath: "6d31/pins/camera.png"),
                    LocalIcon = true,
                    Expected = MarkerKind.LocalIcon,
                },
                new Case
                {
                    Name = "ground unknown with explicit 2525b icon match",
                    Cot = Make(type: "a-u-G"),
                    LocalIcon = true,
                    Expected = MarkerKind.LocalIcon,
                },
                new Case
                {
                    Name = "ground unknown, no icon → affiliation dot",
                    Cot = Make(type: "a-u-G"),
                    Expected = MarkerKind.Dot,
                },
                new Case
                {
                    Name = "plain b-m marker, no icon → dot",
                    Cot = Make(type: "b-m-p-w"),
                    Expected = MarkerKind.Dot,
                },
                new Case
                {
                    Name = "remote iconUrl only → RemoteIcon (after LocalIcon miss)",
                    Cot = Make(type: "b-m-p-w", iconUrl: "https://lxc/icons/pin.png"),
                    Expected = MarkerKind.RemoteIcon,
                },
            };

            int failures = 0;
            foreach (var c in cases)
            {
                var got = Classify(c.Cot, c.LocalIcon);
                if (got == c.Expected) continue;
                failures++;
                Debug.LogError(
                    $"[CotClassifier] SELF-TEST FAIL: \"{c.Name}\" — expected {c.Expected}, got {got}. " +
                    "Classification branch order broke — see CONTRACT WARNING in CotClassifier.cs.");
            }

            if (failures == 0)
            {
                Debug.Log($"[CotClassifier] self-test PASS ({cases.Length} cases)");
                return true;
            }
            Debug.LogError($"[CotClassifier] self-test FAILED: {failures}/{cases.Length} cases");
            return false;
        }

        static NormalizedCot Make(
            string type = null, string callsign = null, string videoUrl = null,
            string team = null, string endpoint = null, string iconsetpath = null,
            string iconUrl = null, string uid = null, string takvPlatform = null)
        {
            var cot = new NormalizedCot
            {
                uid = uid ?? "selftest-" + (type ?? "x"),
                type = type,
                iconUrl = iconUrl,
                detail = new CotDetail(),
            };
            if (takvPlatform != null)
                cot.detail.takv = new CotTakv { platform = takvPlatform };
            if (callsign != null || endpoint != null)
                cot.contact = new CotContact { callsign = callsign, endpoint = endpoint };
            if (videoUrl != null)
                cot.detail.video = new CotVideo { url = videoUrl };
            if (team != null)
                cot.detail.team = new CotTeam { name = team };
            if (iconsetpath != null)
                cot.detail.userIcon = new CotUserIcon { iconsetpath = iconsetpath };
            return cot;
        }
    }
}
