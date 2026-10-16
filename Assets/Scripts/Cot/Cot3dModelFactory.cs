using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TakXr.Cot
{
    /// <summary>
    /// Builds true 3D volumetric GameObjects for every CoT MarkerKind:
    /// - TeamMember / Dot: 3D lit spheres ("balls") with equatorial contrast ring,
    ///   3D directional course cone, and ground anchor pin.
    /// - AircraftFixed: 3D fixed-wing airplane (fuselage, cockpit canopy, swept wings,
    ///   horizontal tailplane, vertical tail fin) oriented to 3D heading.
    /// - AircraftRotary: 3D helicopter (cabin, cockpit visor, skids, tail boom,
    ///   spinning 3D main & tail rotors) oriented to 3D heading.
    /// - Video: 3D spherical gimbal camera pod with lens barrel and live beacon sphere.
    /// - Observer: 3D standing operator pawn + 3D translucent FOV sensor cone.
    /// - Dcfs / LocalIcon / RemoteIcon: 3D extruded relief mesh generated directly
    ///   from the 2D icon texture alpha + 3D affiliation orb base & ring frame.
    /// </summary>
    public static class Cot3dModelFactory
    {
        static Shader _litShader;
        static Shader _unlitShader;
        static Mesh _unitSphereMesh;
        static Mesh _unitCubeMesh;
        static Mesh _unitCylinderMesh;
        static Mesh _coneMesh;
        static Mesh _ringMesh;
        static Mesh _fixedWingMesh;
        static Mesh _heliBodyMesh;
        static Mesh _heliRotorMesh;
        static Mesh _fovConeMesh;

        static readonly Dictionary<string, Material> MatCache = new Dictionary<string, Material>();
        static readonly Dictionary<int, Mesh> ExtrudedIconMeshCache = new Dictionary<int, Mesh>();

        public static Shader LitShader
        {
            get
            {
                if (_litShader == null)
                {
                    _litShader = Shader.Find("Universal Render Pipeline/Lit")
                                 ?? Shader.Find("Standard")
                                 ?? Shader.Find("Universal Render Pipeline/Unlit")
                                 ?? Shader.Find("Sprites/Default");
                }
                return _litShader;
            }
        }

        public static Shader UnlitShader
        {
            get
            {
                if (_unlitShader == null)
                {
                    _unlitShader = Shader.Find("Universal Render Pipeline/Unlit")
                                   ?? Shader.Find("Sprites/Default")
                                   ?? Shader.Find("Unlit/Color");
                }
                return _unlitShader;
            }
        }

        /// <summary>
        /// Shared lit material with subtle self-illumination so 3D spheres and aircraft
        /// show crisp 3D shading under the sun light while remaining vivid on the shadow side.
        /// </summary>
        public static Material GetLitMaterial(Color color, float smoothness = 0.62f, float metallic = 0.15f, float emissionMul = 0.28f)
        {
            string key = $"lit:{ColorUtility.ToHtmlStringRGBA(color)}:{smoothness:F2}:{metallic:F2}:{emissionMul:F2}";
            if (MatCache.TryGetValue(key, out var mat) && mat != null)
                return mat;

            mat = new Material(LitShader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);

            if (emissionMul > 0.001f && mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                var em = new Color(color.r * emissionMul, color.g * emissionMul, color.b * emissionMul, 1f);
                mat.SetColor("_EmissionColor", em);
            }
            mat.renderQueue = 2995;
            MatCache[key] = mat;
            return mat;
        }

        public static Material GetTranslucentMaterial(Color color, int queue = 3001)
        {
            string key = $"trans:{ColorUtility.ToHtmlStringRGBA(color)}:{queue}";
            if (MatCache.TryGetValue(key, out var mat) && mat != null)
                return mat;

            var sh = Shader.Find("Sprites/Default") ?? UnlitShader;
            mat = new Material(sh);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.renderQueue = queue;
            MatCache[key] = mat;
            return mat;
        }

        // ------------------------------------------------------------------
        // 1. 3D UNIT SPHERE ("BALL") — TeamMember & Dot CoTs
        // ------------------------------------------------------------------

        public static Transform BuildUnitBall(Transform parent, Color color, float courseDeg, bool showStem = true)
        {
            var root = new GameObject("Ball3D").transform;
            root.SetParent(parent, false);

            // Core 3D sphere ("ball")
            var sphere = CreateMeshPart("CoreSphere", root, GetSphereMesh(),
                GetLitMaterial(color, smoothness: 0.72f, metallic: 0.18f, emissionMul: 0.32f),
                Vector3.zero, Quaternion.identity, Vector3.one * 0.62f);

            // Inner dark contrast halo + white equatorial ring so the ball pops over any terrain
            CreateMeshPart("EqRingWhite", root, GetRingMesh(),
                GetLitMaterial(new Color(0.97f, 0.99f, 1f, 1f), 0.5f, 0.1f, 0.45f),
                Vector3.zero, Quaternion.identity, new Vector3(0.68f, 0.08f, 0.68f));

            CreateMeshPart("BaseCap", root, GetSphereMesh(),
                GetLitMaterial(new Color(0.08f, 0.11f, 0.15f, 1f), 0.4f, 0.2f, 0.1f),
                new Vector3(0f, -0.24f, 0f), Quaternion.identity, new Vector3(0.36f, 0.16f, 0.36f));

            // 3D ground pedestal stem + ground contact dot
            if (showStem)
                AddGroundStem(root, color, -0.30f, -0.52f);

            // 3D directional course cone when course is known
            if (!float.IsNaN(courseDeg) && courseDeg >= 0f)
            {
                var headingPivot = new GameObject("HeadingPivot").transform;
                headingPivot.SetParent(root, false);
                headingPivot.localRotation = Quaternion.Euler(0f, courseDeg, 0f);

                // Forward nose cone (+Z is North at yaw 0)
                CreateMeshPart("CourseCone", headingPivot, GetConeMesh(),
                    GetLitMaterial(color, 0.7f, 0.2f, 0.38f),
                    new Vector3(0f, 0f, 0.48f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.24f, 0.30f, 0.24f));

                CreateMeshPart("CourseConeCollar", headingPivot, GetRingMesh(),
                    GetLitMaterial(Color.white, 0.6f, 0.1f, 0.5f),
                    new Vector3(0f, 0f, 0.34f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.25f, 0.06f, 0.25f));
            }

            return root;
        }

        // ------------------------------------------------------------------
        // 2. 3D FIXED-WING AIRPLANE — AircraftFixed CoTs
        // ------------------------------------------------------------------

        public static Transform BuildFixedWingAirplane(Transform parent, Color color, float courseDeg)
        {
            var root = new GameObject("Airplane3D").transform;
            root.SetParent(parent, false);

            float yaw = (!float.IsNaN(courseDeg) && courseDeg >= 0f) ? courseDeg : 0f;
            var airframe = new GameObject("Airframe").transform;
            airframe.SetParent(root, false);
            airframe.localRotation = Quaternion.Euler(0f, yaw, 0f);

            var bodyMat = GetLitMaterial(color, smoothness: 0.75f, metallic: 0.28f, emissionMul: 0.30f);
            var trimMat = GetLitMaterial(Color.white, smoothness: 0.65f, metallic: 0.2f, emissionMul: 0.45f);
            var canopyMat = GetLitMaterial(new Color(0.10f, 0.22f, 0.35f, 1f), smoothness: 0.92f, metallic: 0.4f, emissionMul: 0.2f);
            var darkMat = GetLitMaterial(new Color(0.08f, 0.10f, 0.14f, 1f), smoothness: 0.5f, metallic: 0.3f, emissionMul: 0.1f);

            // Unified 3D swept-wing jet/aircraft mesh (fuselage, swept main wings, tailplane, vertical fin)
            CreateMeshPart("JetMesh", airframe, GetFixedWingMesh(), bodyMat,
                Vector3.zero, Quaternion.identity, Vector3.one);

            // Contrasting 3D cockpit canopy bubble on top of forward fuselage
            CreateMeshPart("Canopy", airframe, GetSphereMesh(), canopyMat,
                new Vector3(0f, 0.085f, 0.14f), Quaternion.identity, new Vector3(0.12f, 0.09f, 0.28f));

            // White identification stripes on wings + wingtip pods
            CreateMeshPart("WingStripeL", airframe, GetCubeMesh(), trimMat,
                new Vector3(-0.34f, 0.015f, -0.04f), Quaternion.Euler(0f, -22f, 0f), new Vector3(0.18f, 0.028f, 0.06f));
            CreateMeshPart("WingStripeR", airframe, GetCubeMesh(), trimMat,
                new Vector3(0.34f, 0.015f, -0.04f), Quaternion.Euler(0f, 22f, 0f), new Vector3(0.18f, 0.028f, 0.06f));

            // Twin under-wing/rear engine nacelles
            CreateMeshPart("EngineL", airframe, GetCylinderMesh(), darkMat,
                new Vector3(-0.14f, -0.04f, -0.10f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.07f, 0.16f, 0.07f));
            CreateMeshPart("EngineR", airframe, GetCylinderMesh(), darkMat,
                new Vector3(0.14f, -0.04f, -0.10f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.07f, 0.16f, 0.07f));

            // Subtle 3D altitude reference ring below aircraft
            CreateMeshPart("AirRing", root, GetRingMesh(),
                GetTranslucentMaterial(new Color(color.r, color.g, color.b, 0.55f)),
                new Vector3(0f, -0.22f, 0f), Quaternion.identity, new Vector3(0.62f, 0.03f, 0.62f));

            return root;
        }

        // ------------------------------------------------------------------
        // 3. 3D ROTARY-WING HELICOPTER — AircraftRotary CoTs
        // ------------------------------------------------------------------

        public static Transform BuildRotaryHelicopter(Transform parent, Color color, float courseDeg)
        {
            var root = new GameObject("Helicopter3D").transform;
            root.SetParent(parent, false);

            float yaw = (!float.IsNaN(courseDeg) && courseDeg >= 0f) ? courseDeg : 0f;
            var airframe = new GameObject("Airframe").transform;
            airframe.SetParent(root, false);
            airframe.localRotation = Quaternion.Euler(0f, yaw, 0f);

            var bodyMat = GetLitMaterial(color, smoothness: 0.72f, metallic: 0.25f, emissionMul: 0.30f);
            var glassMat = GetLitMaterial(new Color(0.10f, 0.24f, 0.38f, 1f), smoothness: 0.9f, metallic: 0.35f, emissionMul: 0.2f);
            var rotorMat = GetLitMaterial(new Color(0.92f, 0.96f, 1f, 1f), smoothness: 0.6f, metallic: 0.3f, emissionMul: 0.35f);
            var skidMat = GetLitMaterial(new Color(0.15f, 0.18f, 0.22f, 1f), smoothness: 0.5f, metallic: 0.4f, emissionMul: 0.15f);

            // 3D cabin + tail boom + vertical fin mesh
            CreateMeshPart("HeliBody", airframe, GetHeliBodyMesh(), bodyMat,
                Vector3.zero, Quaternion.identity, Vector3.one);

            // Nose cockpit glass
            CreateMeshPart("CockpitGlass", airframe, GetSphereMesh(), glassMat,
                new Vector3(0f, 0.03f, 0.22f), Quaternion.identity, new Vector3(0.23f, 0.17f, 0.24f));

            // Landing skids
            CreateMeshPart("SkidL", airframe, GetCubeMesh(), skidMat,
                new Vector3(-0.13f, -0.16f, 0.08f), Quaternion.identity, new Vector3(0.03f, 0.025f, 0.42f));
            CreateMeshPart("SkidR", airframe, GetCubeMesh(), skidMat,
                new Vector3(0.13f, -0.16f, 0.08f), Quaternion.identity, new Vector3(0.03f, 0.025f, 0.42f));
            CreateMeshPart("StrutF", airframe, GetCubeMesh(), skidMat,
                new Vector3(0f, -0.11f, 0.18f), Quaternion.identity, new Vector3(0.26f, 0.025f, 0.025f));
            CreateMeshPart("StrutR", airframe, GetCubeMesh(), skidMat,
                new Vector3(0f, -0.11f, -0.02f), Quaternion.identity, new Vector3(0.26f, 0.025f, 0.025f));

            // Main rotor mast + spinning 4-blade 3D rotor assembly
            CreateMeshPart("RotorMast", airframe, GetCylinderMesh(), skidMat,
                new Vector3(0f, 0.15f, 0.06f), Quaternion.identity, new Vector3(0.06f, 0.07f, 0.06f));

            var mainRotor = CreateMeshPart("MainRotor", airframe, GetHeliRotorMesh(), rotorMat,
                new Vector3(0f, 0.21f, 0.06f), Quaternion.identity, Vector3.one);

            // Translucent rotor blur disc so it reads immediately as a helicopter at any range
            CreateMeshPart("RotorDisc", airframe, GetCylinderMesh(),
                GetTranslucentMaterial(new Color(color.r, color.g, color.b, 0.28f)),
                new Vector3(0f, 0.205f, 0.06f), Quaternion.identity, new Vector3(0.88f, 0.006f, 0.88f));

            // Tail rotor
            var tailRotor = CreateMeshPart("TailRotor", airframe, GetHeliRotorMesh(), rotorMat,
                new Vector3(0.045f, 0.08f, -0.44f), Quaternion.Euler(0f, 0f, 90f), Vector3.one * 0.26f);

            var spinner = root.gameObject.AddComponent<CotRotorSpinner>();
            spinner.Bind(mainRotor, tailRotor);

            return root;
        }

        // ------------------------------------------------------------------
        // 4. 3D VIDEO / SENSOR TURRET POD — Video CoTs
        // ------------------------------------------------------------------

        public static Transform BuildVideoPod(Transform parent, float sensorAzimuthDeg = float.NaN)
        {
            var root = new GameObject("VideoPod3D").transform;
            root.SetParent(parent, false);

            var podColor = new Color(0.12f, 0.18f, 0.26f, 1f);
            var cyanColor = new Color(0.25f, 0.85f, 1f, 1f);
            var liveRed = new Color(1f, 0.22f, 0.22f, 1f);

            // Spherical gimbal housing
            CreateMeshPart("GimbalSphere", root, GetSphereMesh(),
                GetLitMaterial(podColor, 0.8f, 0.35f, 0.2f),
                Vector3.zero, Quaternion.identity, Vector3.one * 0.62f);

            // Cyan equatorial sensor ring
            CreateMeshPart("CyanRing", root, GetRingMesh(),
                GetLitMaterial(cyanColor, 0.7f, 0.2f, 0.55f),
                Vector3.zero, Quaternion.identity, new Vector3(0.70f, 0.09f, 0.70f));

            // Aimed lens turret
            float yaw = (!float.IsNaN(sensorAzimuthDeg) && sensorAzimuthDeg >= 0f) ? sensorAzimuthDeg : 0f;
            var turret = new GameObject("LensTurret").transform;
            turret.SetParent(root, false);
            turret.localRotation = Quaternion.Euler(0f, yaw, 0f);

            CreateMeshPart("LensBarrel", turret, GetCylinderMesh(),
                GetLitMaterial(Color.white, 0.7f, 0.25f, 0.4f),
                new Vector3(0f, 0.02f, 0.30f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.24f, 0.14f, 0.24f));

            CreateMeshPart("LensGlass", turret, GetSphereMesh(),
                GetLitMaterial(cyanColor, 0.95f, 0.1f, 0.65f),
                new Vector3(0f, 0.02f, 0.40f), Quaternion.identity, new Vector3(0.18f, 0.18f, 0.08f));

            // Live red beacon sphere on top of the pod
            CreateMeshPart("LiveBeacon", root, GetSphereMesh(),
                GetLitMaterial(liveRed, 0.85f, 0.1f, 0.75f),
                new Vector3(0f, 0.36f, 0f), Quaternion.identity, Vector3.one * 0.18f);

            AddGroundStem(root, cyanColor, -0.30f, -0.52f);
            return root;
        }

        // ------------------------------------------------------------------
        // 5. 3D OBSERVER PAWN + 3D FOV CONE — Observer CoTs
        // ------------------------------------------------------------------

        public static Transform BuildObserverPawn(Transform parent, Color color, float azimuthDeg)
        {
            var root = new GameObject("ObserverPawn3D").transform;
            root.SetParent(parent, false);

            var bodyMat = GetLitMaterial(color, 0.68f, 0.18f, 0.34f);
            var whiteMat = GetLitMaterial(Color.white, 0.6f, 0.1f, 0.45f);
            var visorMat = GetLitMaterial(new Color(0.08f, 0.15f, 0.25f, 1f), 0.9f, 0.4f, 0.25f);

            // Base pedestal ring
            CreateMeshPart("Pedestal", root, GetCylinderMesh(), whiteMat,
                new Vector3(0f, -0.32f, 0f), Quaternion.identity, new Vector3(0.52f, 0.04f, 0.52f));

            // Torso (tapered capsule/sphere)
            CreateMeshPart("Torso", root, GetSphereMesh(), bodyMat,
                new Vector3(0f, -0.06f, 0f), Quaternion.identity, new Vector3(0.36f, 0.46f, 0.26f));

            // Head sphere
            CreateMeshPart("Head", root, GetSphereMesh(), bodyMat,
                new Vector3(0f, 0.28f, 0f), Quaternion.identity, Vector3.one * 0.24f);

            // XR Headset visor on head (+Z is gaze direction)
            var headYaw = new GameObject("GazeYaw").transform;
            headYaw.SetParent(root, false);
            float yaw = (!float.IsNaN(azimuthDeg) && azimuthDeg >= 0f) ? azimuthDeg : 0f;
            headYaw.localRotation = Quaternion.Euler(0f, yaw, 0f);

            CreateMeshPart("XrVisor", headYaw, GetCubeMesh(), visorMat,
                new Vector3(0f, 0.29f, 0.11f), Quaternion.identity, new Vector3(0.20f, 0.08f, 0.10f));

            // 3D volumetric translucent gaze FOV cone
            if (!float.IsNaN(azimuthDeg) && azimuthDeg >= 0f)
            {
                var coneCol = new Color(color.r, color.g, color.b, 0.34f);
                CreateMeshPart("FovCone3D", headYaw, GetFovConeMesh(),
                    GetTranslucentMaterial(coneCol, 2998),
                    new Vector3(0f, -0.26f, 0f), Quaternion.identity, Vector3.one);
            }

            return root;
        }

        // ------------------------------------------------------------------
        // 6. 3D EXTRUDED ICON TOKEN / BALL — Dcfs, LocalIcon, RemoteIcon
        // ------------------------------------------------------------------

        /// <summary>
        /// Renders any 2D icon Texture2D as a true 3D object:
        /// - A 3D affiliation base sphere/puck with a bevelled 3D ring frame
        /// - A true 3D extruded relief mesh built from the icon's alpha silhouette
        ///   (front face, back face, and 3D extruded depth sidewalls) so the icon
        ///   has real 3D thickness and volume from any angle in XR.
        /// </summary>
        public static Transform BuildExtrudedIconObject(Transform parent, Texture2D iconTex, Color frameColor, float courseDeg = float.NaN)
        {
            var root = new GameObject("Icon3D").transform;
            root.SetParent(parent, false);

            // 1. 3D spherical base core ("ball" foundation) so from any angle (incl. nadir)
            //    the marker has solid 3D presence on the map.
            CreateMeshPart("BaseBall", root, GetSphereMesh(),
                GetLitMaterial(new Color(0.10f, 0.14f, 0.20f, 1f), 0.65f, 0.25f, 0.18f),
                Vector3.zero, Quaternion.identity, new Vector3(0.66f, 0.66f, 0.32f));

            // 2. 3D metallic affiliation/team outer torus ring framing the icon
            CreateMeshPart("FrameRing", root, GetRingMesh(),
                GetLitMaterial(frameColor, 0.72f, 0.25f, 0.38f),
                Vector3.zero, Quaternion.Euler(90f, 0f, 0f), new Vector3(0.74f, 0.11f, 0.74f));

            // 3. 3D extruded icon relief mesh (front + back + 3D extruded perimeter walls)
            if (iconTex != null)
            {
                var reliefMesh = GetOrBuildExtrudedIconMesh(iconTex, thickness: 0.18f);
                var reliefGo = new GameObject("ExtrudedRelief");
                reliefGo.transform.SetParent(root, false);
                reliefGo.transform.localPosition = Vector3.zero;
                reliefGo.transform.localScale = new Vector3(0.58f, 0.58f, 1f);

                var mf = reliefGo.AddComponent<MeshFilter>();
                mf.sharedMesh = reliefMesh;
                var mr = reliefGo.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;

                var sh = Shader.Find("Sprites/Default") ?? UnlitShader;
                var iconMat = new Material(sh);
                iconMat.mainTexture = iconTex;
                if (iconMat.HasProperty("_BaseMap")) iconMat.SetTexture("_BaseMap", iconTex);
                if (iconMat.HasProperty("_Color")) iconMat.SetColor("_Color", Color.white);
                if (iconMat.HasProperty("_BaseColor")) iconMat.SetColor("_BaseColor", Color.white);
                iconMat.renderQueue = 3001;
                mr.sharedMaterial = iconMat;
            }

            // 4. Optional 3D course pointer when moving
            if (!float.IsNaN(courseDeg) && courseDeg >= 0f)
            {
                var headingPivot = new GameObject("HeadingPivot").transform;
                headingPivot.SetParent(root, false);
                headingPivot.localRotation = Quaternion.Euler(0f, courseDeg, 0f);
                CreateMeshPart("CourseCone", headingPivot, GetConeMesh(),
                    GetLitMaterial(frameColor, 0.7f, 0.2f, 0.38f),
                    new Vector3(0f, 0f, 0.48f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.20f, 0.26f, 0.20f));
            }

            AddGroundStem(root, frameColor, -0.34f, -0.54f);
            return root;
        }

        static void AddGroundStem(Transform parent, Color color, float topY, float bottomY)
        {
            float midY = (topY + bottomY) * 0.5f;
            float halfH = Mathf.Abs(topY - bottomY) * 0.5f;
            CreateMeshPart("GroundStem", parent, GetCylinderMesh(),
                GetLitMaterial(Color.white, 0.5f, 0.1f, 0.4f),
                new Vector3(0f, midY, 0f), Quaternion.identity, new Vector3(0.045f, halfH, 0.045f));

            CreateMeshPart("GroundAnchorBall", parent, GetSphereMesh(),
                GetLitMaterial(color, 0.65f, 0.2f, 0.4f),
                new Vector3(0f, bottomY, 0f), Quaternion.identity, Vector3.one * 0.13f);
        }

        static Transform CreateMeshPart(string name, Transform parent, Mesh mesh, Material mat,
            Vector3 localPos, Quaternion localRot, Vector3 localScale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = localScale;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        // ------------------------------------------------------------------
        // PROCEDURAL 3D MESH GENERATORS
        // ------------------------------------------------------------------

        static Mesh GetSphereMesh()
        {
            if (_unitSphereMesh != null) return _unitSphereMesh;
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _unitSphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(temp);
            return _unitSphereMesh;
        }

        static Mesh GetCubeMesh()
        {
            if (_unitCubeMesh != null) return _unitCubeMesh;
            var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _unitCubeMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(temp);
            return _unitCubeMesh;
        }

        static Mesh GetCylinderMesh()
        {
            if (_unitCylinderMesh != null) return _unitCylinderMesh;
            var temp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _unitCylinderMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(temp);
            return _unitCylinderMesh;
        }

        static Mesh GetConeMesh()
        {
            if (_coneMesh != null) return _coneMesh;
            const int segs = 18;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();

            // Tip at +Y (0.5), base circle at -Y (-0.5), radius 0.5
            for (int i = 0; i < segs; i++)
            {
                float a0 = (i / (float)segs) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)segs) * Mathf.PI * 2f;
                var p0 = new Vector3(Mathf.Cos(a0) * 0.5f, -0.5f, Mathf.Sin(a0) * 0.5f);
                var p1 = new Vector3(Mathf.Cos(a1) * 0.5f, -0.5f, Mathf.Sin(a1) * 0.5f);
                var tip = new Vector3(0f, 0.5f, 0f);
                AddTri(verts, norms, tris, p0, tip, p1);
                AddTri(verts, norms, tris, new Vector3(0f, -0.5f, 0f), p0, p1);
            }

            _coneMesh = new Mesh { name = "TakXrCone3D" };
            _coneMesh.SetVertices(verts);
            _coneMesh.SetNormals(norms);
            _coneMesh.SetTriangles(tris, 0);
            _coneMesh.RecalculateBounds();
            return _coneMesh;
        }

        static Mesh GetRingMesh()
        {
            if (_ringMesh != null) return _ringMesh;
            // 3D bevelled torus/collar ring around Y axis, outer radius 0.5, inner radius 0.38, height 1.0
            const int segs = 24;
            const float rOut = 0.50f;
            const float rIn = 0.38f;
            const float yTop = 0.5f;
            const float yBot = -0.5f;

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();

            for (int i = 0; i < segs; i++)
            {
                float a0 = (i / (float)segs) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)segs) * Mathf.PI * 2f;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0);
                float c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);

                var oTop0 = new Vector3(c0 * rOut, yTop, s0 * rOut);
                var oTop1 = new Vector3(c1 * rOut, yTop, s1 * rOut);
                var oBot0 = new Vector3(c0 * rOut, yBot, s0 * rOut);
                var oBot1 = new Vector3(c1 * rOut, yBot, s1 * rOut);

                var iTop0 = new Vector3(c0 * rIn, yTop, s0 * rIn);
                var iTop1 = new Vector3(c1 * rIn, yTop, s1 * rIn);
                var iBot0 = new Vector3(c0 * rIn, yBot, s0 * rIn);
                var iBot1 = new Vector3(c1 * rIn, yBot, s1 * rIn);

                // Outer wall
                AddQuad(verts, norms, tris, oBot0, oTop0, oTop1, oBot1);
                // Inner wall
                AddQuad(verts, norms, tris, iBot1, iTop1, iTop0, iBot0);
                // Top rim
                AddQuad(verts, norms, tris, iTop0, iTop1, oTop1, oTop0);
                // Bottom rim
                AddQuad(verts, norms, tris, oBot0, oBot1, iBot1, iBot0);
            }

            _ringMesh = new Mesh { name = "TakXrRing3D" };
            _ringMesh.SetVertices(verts);
            _ringMesh.SetNormals(norms);
            _ringMesh.SetTriangles(tris, 0);
            _ringMesh.RecalculateBounds();
            return _ringMesh;
        }

        /// <summary>
        /// True 3D swept-wing aircraft mesh (+Z = nose forward, +Y = dorsal up, +X = starboard right).
        /// </summary>
        static Mesh GetFixedWingMesh()
        {
            if (_fixedWingMesh != null) return _fixedWingMesh;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();

            // 1. Fuselage stations along Z (from tail -0.44 to nose tip +0.52)
            float[] zSt = { -0.44f, -0.32f, -0.08f, 0.20f, 0.36f, 0.52f };
            float[] rxSt = { 0.032f, 0.065f, 0.095f, 0.090f, 0.058f, 0.004f };
            float[] rySt = { 0.032f, 0.060f, 0.085f, 0.082f, 0.052f, 0.004f };
            const int fSegs = 12;

            for (int s = 0; s < zSt.Length - 1; s++)
            {
                for (int i = 0; i < fSegs; i++)
                {
                    float a0 = (i / (float)fSegs) * Mathf.PI * 2f;
                    float a1 = ((i + 1) / (float)fSegs) * Mathf.PI * 2f;
                    var p00 = new Vector3(Mathf.Cos(a0) * rxSt[s], Mathf.Sin(a0) * rySt[s], zSt[s]);
                    var p01 = new Vector3(Mathf.Cos(a1) * rxSt[s], Mathf.Sin(a1) * rySt[s], zSt[s]);
                    var p10 = new Vector3(Mathf.Cos(a0) * rxSt[s + 1], Mathf.Sin(a0) * rySt[s + 1], zSt[s + 1]);
                    var p11 = new Vector3(Mathf.Cos(a1) * rxSt[s + 1], Mathf.Sin(a1) * rySt[s + 1], zSt[s + 1]);
                    AddQuad(verts, norms, tris, p00, p10, p11, p01);
                }
            }

            // 2. Swept 3D Main Wings (thick airfoil slab)
            AddSymmetricalWing(verts, norms, tris,
                rootLeadZ: 0.16f, rootTrailZ: -0.14f, rootX: 0.06f, rootHalfThick: 0.026f,
                tipLeadZ: -0.08f, tipTrailZ: -0.22f, tipX: 0.54f, tipHalfThick: 0.012f,
                dihedralY: 0.015f);

            // 3. Swept 3D Horizontal Tailplane (stabilizers)
            AddSymmetricalWing(verts, norms, tris,
                rootLeadZ: -0.26f, rootTrailZ: -0.42f, rootX: 0.03f, rootHalfThick: 0.016f,
                tipLeadZ: -0.36f, tipTrailZ: -0.45f, tipX: 0.24f, tipHalfThick: 0.008f,
                dihedralY: 0.01f);

            // 4. 3D Vertical Tail Fin (Rudder)
            AddVerticalFin(verts, norms, tris,
                baseLeadZ: -0.18f, baseTrailZ: -0.43f, baseY: 0.04f, baseHalfThick: 0.018f,
                topLeadZ: -0.34f, topTrailZ: -0.45f, topY: 0.26f, topHalfThick: 0.008f);

            _fixedWingMesh = new Mesh { name = "TakXrFixedWing3D" };
            _fixedWingMesh.SetVertices(verts);
            _fixedWingMesh.SetNormals(norms);
            _fixedWingMesh.SetTriangles(tris, 0);
            _fixedWingMesh.RecalculateBounds();
            return _fixedWingMesh;
        }

        static Mesh GetHeliBodyMesh()
        {
            if (_heliBodyMesh != null) return _heliBodyMesh;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();

            // Rounded helicopter cabin + tapered tail boom (+Z forward)
            float[] zSt = { -0.46f, -0.38f, -0.12f, -0.04f, 0.14f, 0.30f, 0.38f };
            float[] rxSt = { 0.022f, 0.030f, 0.055f, 0.145f, 0.155f, 0.115f, 0.03f };
            float[] rySt = { 0.022f, 0.032f, 0.055f, 0.125f, 0.135f, 0.100f, 0.03f };
            float[] yOff = { 0.03f, 0.025f, 0.02f, 0.0f, 0.0f, -0.01f, -0.02f };
            const int segs = 12;

            for (int s = 0; s < zSt.Length - 1; s++)
            {
                for (int i = 0; i < segs; i++)
                {
                    float a0 = (i / (float)segs) * Mathf.PI * 2f;
                    float a1 = ((i + 1) / (float)segs) * Mathf.PI * 2f;
                    var p00 = new Vector3(Mathf.Cos(a0) * rxSt[s], yOff[s] + Mathf.Sin(a0) * rySt[s], zSt[s]);
                    var p01 = new Vector3(Mathf.Cos(a1) * rxSt[s], yOff[s] + Mathf.Sin(a1) * rySt[s], zSt[s]);
                    var p10 = new Vector3(Mathf.Cos(a0) * rxSt[s + 1], yOff[s + 1] + Mathf.Sin(a0) * rySt[s + 1], zSt[s + 1]);
                    var p11 = new Vector3(Mathf.Cos(a1) * rxSt[s + 1], yOff[s + 1] + Mathf.Sin(a1) * rySt[s + 1], zSt[s + 1]);
                    AddQuad(verts, norms, tris, p00, p10, p11, p01);
                }
            }

            // Tail vertical fin
            AddVerticalFin(verts, norms, tris,
                baseLeadZ: -0.36f, baseTrailZ: -0.46f, baseY: 0.02f, baseHalfThick: 0.016f,
                topLeadZ: -0.41f, topTrailZ: -0.47f, topY: 0.16f, topHalfThick: 0.008f);

            _heliBodyMesh = new Mesh { name = "TakXrHeliBody3D" };
            _heliBodyMesh.SetVertices(verts);
            _heliBodyMesh.SetNormals(norms);
            _heliBodyMesh.SetTriangles(tris, 0);
            _heliBodyMesh.RecalculateBounds();
            return _heliBodyMesh;
        }

        static Mesh GetHeliRotorMesh()
        {
            if (_heliRotorMesh != null) return _heliRotorMesh;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();

            // 4 crossed 3D rotor blades + center hub
            AddBox(verts, norms, tris, Vector3.zero, new Vector3(0.92f, 0.018f, 0.055f));
            AddBox(verts, norms, tris, Vector3.zero, new Vector3(0.055f, 0.018f, 0.92f));
            AddBox(verts, norms, tris, Vector3.zero, new Vector3(0.11f, 0.045f, 0.11f));

            _heliRotorMesh = new Mesh { name = "TakXrHeliRotor3D" };
            _heliRotorMesh.SetVertices(verts);
            _heliRotorMesh.SetNormals(norms);
            _heliRotorMesh.SetTriangles(tris, 0);
            _heliRotorMesh.RecalculateBounds();
            return _heliRotorMesh;
        }

        static Mesh GetFovConeMesh()
        {
            if (_fovConeMesh != null) return _fovConeMesh;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();

            // 3D wedge/cone from origin out to +Z (range 0.72), ±30° horizontal spread
            var origin = new Vector3(0f, 0.04f, 0.05f);
            const int arcSegs = 8;
            const float halfAng = 28f * Mathf.Deg2Rad;
            const float r = 0.68f;
            const float h = 0.10f;

            for (int i = 0; i < arcSegs; i++)
            {
                float a0 = Mathf.Lerp(-halfAng, halfAng, i / (float)arcSegs);
                float a1 = Mathf.Lerp(-halfAng, halfAng, (i + 1) / (float)arcSegs);
                var top0 = new Vector3(Mathf.Sin(a0) * r, h, Mathf.Cos(a0) * r);
                var top1 = new Vector3(Mathf.Sin(a1) * r, h, Mathf.Cos(a1) * r);
                var bot0 = new Vector3(Mathf.Sin(a0) * r, -0.02f, Mathf.Cos(a0) * r);
                var bot1 = new Vector3(Mathf.Sin(a1) * r, -0.02f, Mathf.Cos(a1) * r);

                AddTri(verts, norms, tris, origin, top0, top1);
                AddTri(verts, norms, tris, origin, bot1, bot0);
                AddQuad(verts, norms, tris, bot0, top0, top1, bot1);
            }

            _fovConeMesh = new Mesh { name = "TakXrFovCone3D" };
            _fovConeMesh.SetVertices(verts);
            _fovConeMesh.SetNormals(norms);
            _fovConeMesh.SetTriangles(tris, 0);
            _fovConeMesh.RecalculateBounds();
            return _fovConeMesh;
        }

        /// <summary>
        /// Builds a true 3D extruded relief mesh from a 2D Texture2D's alpha channel:
        /// - Front face at -Z (facing viewer in XrUiFacing convention)
        /// - Back face at +Z (so it reads from behind too)
        /// - Extruded 3D perimeter sidewalls around every opaque pixel boundary!
        /// Falls back to a 3D bevelled slab if the texture is non-readable.
        /// </summary>
        static Mesh GetOrBuildExtrudedIconMesh(Texture2D tex, float thickness)
        {
            int id = tex.GetInstanceID();
            if (ExtrudedIconMeshCache.TryGetValue(id, out var cached) && cached != null)
                return cached;

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            float halfZ = thickness * 0.5f;
            bool builtVoxels = false;

            if (tex.isReadable)
            {
                try
                {
                    const int G = 28; // 28x28 volumetric alpha grid
                    var mask = new bool[G, G];
                    int opaqueCount = 0;
                    for (int y = 0; y < G; y++)
                    for (int x = 0; x < G; x++)
                    {
                        float u = (x + 0.5f) / G;
                        float v = (y + 0.5f) / G;
                        bool op = tex.GetPixelBilinear(u, v).a > 0.22f;
                        mask[x, y] = op;
                        if (op) opaqueCount++;
                    }

                    if (opaqueCount > 4)
                    {
                        builtVoxels = true;
                        for (int y = 0; y < G; y++)
                        for (int x = 0; x < G; x++)
                        {
                            if (!mask[x, y]) continue;
                            float u0 = x / (float)G, u1 = (x + 1) / (float)G;
                            float v0 = y / (float)G, v1 = (y + 1) / (float)G;
                            float x0 = u0 - 0.5f, x1 = u1 - 0.5f;
                            float y0 = v0 - 0.5f, y1 = v1 - 0.5f;
                            var uvMid = new Vector2((u0 + u1) * 0.5f, (v0 + v1) * 0.5f);

                            // Front face (-Z faces viewer in XrUiFacing)
                            AddUvQuad(verts, norms, uvs, tris,
                                new Vector3(x0, y0, -halfZ), new Vector3(x0, y1, -halfZ),
                                new Vector3(x1, y1, -halfZ), new Vector3(x1, y0, -halfZ),
                                new Vector2(u0, v0), new Vector2(u0, v1),
                                new Vector2(u1, v1), new Vector2(u1, v0));

                            // Back face (+Z)
                            AddUvQuad(verts, norms, uvs, tris,
                                new Vector3(x1, y0, halfZ), new Vector3(x1, y1, halfZ),
                                new Vector3(x0, y1, halfZ), new Vector3(x0, y0, halfZ),
                                new Vector2(u1, v0), new Vector2(u1, v1),
                                new Vector2(u0, v1), new Vector2(u0, v0));

                            // 3D perimeter sidewalls where neighbor is transparent
                            if (x == 0 || !mask[x - 1, y])
                                AddUvQuad(verts, norms, uvs, tris,
                                    new Vector3(x0, y0, halfZ), new Vector3(x0, y1, halfZ),
                                    new Vector3(x0, y1, -halfZ), new Vector3(x0, y0, -halfZ),
                                    uvMid, uvMid, uvMid, uvMid);
                            if (x == G - 1 || !mask[x + 1, y])
                                AddUvQuad(verts, norms, uvs, tris,
                                    new Vector3(x1, y0, -halfZ), new Vector3(x1, y1, -halfZ),
                                    new Vector3(x1, y1, halfZ), new Vector3(x1, y0, halfZ),
                                    uvMid, uvMid, uvMid, uvMid);
                            if (y == 0 || !mask[x, y - 1])
                                AddUvQuad(verts, norms, uvs, tris,
                                    new Vector3(x0, y0, halfZ), new Vector3(x0, y0, -halfZ),
                                    new Vector3(x1, y0, -halfZ), new Vector3(x1, y0, halfZ),
                                    uvMid, uvMid, uvMid, uvMid);
                            if (y == G - 1 || !mask[x, y + 1])
                                AddUvQuad(verts, norms, uvs, tris,
                                    new Vector3(x0, y1, -halfZ), new Vector3(x0, y1, halfZ),
                                    new Vector3(x1, y1, halfZ), new Vector3(x1, y1, -halfZ),
                                    uvMid, uvMid, uvMid, uvMid);
                        }
                    }
                }
                catch
                {
                    builtVoxels = false;
                }
            }

            if (!builtVoxels)
            {
                // Fallback 3D double-sided slab with sidewalls
                AddUvQuad(verts, norms, uvs, tris,
                    new Vector3(-0.5f, -0.5f, -halfZ), new Vector3(-0.5f, 0.5f, -halfZ),
                    new Vector3(0.5f, 0.5f, -halfZ), new Vector3(0.5f, -0.5f, -halfZ),
                    new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0));
                AddUvQuad(verts, norms, uvs, tris,
                    new Vector3(0.5f, -0.5f, halfZ), new Vector3(0.5f, 0.5f, halfZ),
                    new Vector3(-0.5f, 0.5f, halfZ), new Vector3(-0.5f, -0.5f, halfZ),
                    new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), new Vector2(0, 0));
            }

            var mesh = new Mesh { name = "ExtrudedIcon3D_" + tex.name };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            ExtrudedIconMeshCache[id] = mesh;
            return mesh;
        }

        // ------------------------------------------------------------------
        // GEOMETRY HELPERS
        // ------------------------------------------------------------------

        static void AddSymmetricalWing(List<Vector3> v, List<Vector3> n, List<int> t,
            float rootLeadZ, float rootTrailZ, float rootX, float rootHalfThick,
            float tipLeadZ, float tipTrailZ, float tipX, float tipHalfThick,
            float dihedralY)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                float s = side;
                var rLT = new Vector3(s * rootX, rootHalfThick, rootLeadZ);
                var rLB = new Vector3(s * rootX, -rootHalfThick, rootLeadZ);
                var rTT = new Vector3(s * rootX, rootHalfThick * 0.5f, rootTrailZ);
                var rTB = new Vector3(s * rootX, -rootHalfThick * 0.5f, rootTrailZ);

                var tLT = new Vector3(s * tipX, dihedralY + tipHalfThick, tipLeadZ);
                var tLB = new Vector3(s * tipX, dihedralY - tipHalfThick, tipLeadZ);
                var tTT = new Vector3(s * tipX, dihedralY + tipHalfThick * 0.5f, tipTrailZ);
                var tTB = new Vector3(s * tipX, dihedralY - tipHalfThick * 0.5f, tipTrailZ);

                if (side > 0)
                {
                    AddQuad(v, n, t, rTT, rLT, tLT, tTT); // top
                    AddQuad(v, n, t, rLB, rTB, tTB, tLB); // bottom
                    AddQuad(v, n, t, rLB, tLB, tLT, rLT); // leading edge
                    AddQuad(v, n, t, tTB, rTB, rTT, tTT); // trailing edge
                    AddQuad(v, n, t, tLB, tTB, tTT, tLT); // wingtip cap
                }
                else
                {
                    AddQuad(v, n, t, tTT, tLT, rLT, rTT); // top
                    AddQuad(v, n, t, tLB, tTB, rTB, rLB); // bottom
                    AddQuad(v, n, t, rLT, tLT, tLB, rLB); // leading edge
                    AddQuad(v, n, t, tTT, rTT, rTB, tTB); // trailing edge
                    AddQuad(v, n, t, tLT, tTT, tTB, tLB); // wingtip cap
                }
            }
        }

        static void AddVerticalFin(List<Vector3> v, List<Vector3> n, List<int> t,
            float baseLeadZ, float baseTrailZ, float baseY, float baseHalfThick,
            float topLeadZ, float topTrailZ, float topY, float topHalfThick)
        {
            var bL_R = new Vector3(baseHalfThick, baseY, baseLeadZ);
            var bL_L = new Vector3(-baseHalfThick, baseY, baseLeadZ);
            var bT_R = new Vector3(baseHalfThick * 0.6f, baseY, baseTrailZ);
            var bT_L = new Vector3(-baseHalfThick * 0.6f, baseY, baseTrailZ);

            var tL_R = new Vector3(topHalfThick, topY, topLeadZ);
            var tL_L = new Vector3(-topHalfThick, topY, topLeadZ);
            var tT_R = new Vector3(topHalfThick * 0.6f, topY, topTrailZ);
            var tT_L = new Vector3(-topHalfThick * 0.6f, topY, topTrailZ);

            AddQuad(v, n, t, bT_R, tT_R, tL_R, bL_R); // starboard face
            AddQuad(v, n, t, bL_L, tL_L, tT_L, bT_L); // port face
            AddQuad(v, n, t, bL_R, tL_R, tL_L, bL_L); // leading edge
            AddQuad(v, n, t, bT_L, tT_L, tT_R, bT_R); // trailing edge
            AddQuad(v, n, t, tT_R, tT_L, tL_L, tL_R); // top cap
        }

        static void AddBox(List<Vector3> v, List<Vector3> n, List<int> t, Vector3 center, Vector3 size)
        {
            var h = size * 0.5f;
            var p000 = center + new Vector3(-h.x, -h.y, -h.z);
            var p001 = center + new Vector3(-h.x, -h.y,  h.z);
            var p010 = center + new Vector3(-h.x,  h.y, -h.z);
            var p011 = center + new Vector3(-h.x,  h.y,  h.z);
            var p100 = center + new Vector3( h.x, -h.y, -h.z);
            var p101 = center + new Vector3( h.x, -h.y,  h.z);
            var p110 = center + new Vector3( h.x,  h.y, -h.z);
            var p111 = center + new Vector3( h.x,  h.y,  h.z);

            AddQuad(v, n, t, p010, p011, p111, p110); // +Y
            AddQuad(v, n, t, p001, p000, p100, p101); // -Y
            AddQuad(v, n, t, p100, p110, p111, p101); // +X
            AddQuad(v, n, t, p001, p011, p010, p000); // -X
            AddQuad(v, n, t, p101, p111, p011, p001); // +Z
            AddQuad(v, n, t, p000, p010, p110, p100); // -Z
        }

        static void AddTri(List<Vector3> v, List<Vector3> n, List<int> t, Vector3 a, Vector3 b, Vector3 c)
        {
            int idx = v.Count;
            var norm = Vector3.Cross(b - a, c - a).normalized;
            v.Add(a); v.Add(b); v.Add(c);
            n.Add(norm); n.Add(norm); n.Add(norm);
            t.Add(idx); t.Add(idx + 1); t.Add(idx + 2);
        }

        static void AddQuad(List<Vector3> v, List<Vector3> n, List<int> t,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int idx = v.Count;
            var norm = Vector3.Cross(b - a, c - a).normalized;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            n.Add(norm); n.Add(norm); n.Add(norm); n.Add(norm);
            t.Add(idx); t.Add(idx + 1); t.Add(idx + 2);
            t.Add(idx); t.Add(idx + 2); t.Add(idx + 3);
        }

        static void AddUvQuad(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector2 uva, Vector2 uvb, Vector2 uvc, Vector2 uvd)
        {
            int idx = v.Count;
            var norm = Vector3.Cross(b - a, c - a).normalized;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            n.Add(norm); n.Add(norm); n.Add(norm); n.Add(norm);
            uv.Add(uva); uv.Add(uvb); uv.Add(uvc); uv.Add(uvd);
            t.Add(idx); t.Add(idx + 1); t.Add(idx + 2);
            t.Add(idx); t.Add(idx + 2); t.Add(idx + 3);
        }
    }

    /// <summary>Spins 3D helicopter main and tail rotors smoothly.</summary>
    public sealed class CotRotorSpinner : MonoBehaviour
    {
        Transform _mainRotor;
        Transform _tailRotor;

        public void Bind(Transform mainRotor, Transform tailRotor)
        {
            _mainRotor = mainRotor;
            _tailRotor = tailRotor;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (_mainRotor != null)
                _mainRotor.Rotate(0f, 780f * dt, 0f, Space.Self);
            if (_tailRotor != null)
                _tailRotor.Rotate(1100f * dt, 0f, 0f, Space.Self);
        }
    }
}
