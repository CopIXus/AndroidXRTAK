using System.Collections.Generic;
using TakXr.Core;
using TakXr.Map;
using UnityEngine;

namespace TakXr.Cot
{
    /// <summary>
    /// Syncs feed CoTs into scene markers. Ground tracks beyond ~100 mi from the
    /// viewer collapse into numbered cluster bubbles (web XR parity); approaching
    /// unclusters them. No hard marker count cap — far CoTs stay represented.
    /// </summary>
    public class CotLayerController : MonoBehaviour
    {
        // Generous floor over the sampled surface — point samples on ridges are
        // uncertain; CotMarkerView additionally lifts flat discs by half their
        // world size every frame so a disc edge can't dig into a slope.
        const float GroundClearanceM = 12f;
        const float FallbackClearanceM = 6f;
        // Angular separation (degrees) below which callsigns are treated as overlapping.
        const float LabelCrowdAngleDeg = 1.6f;
        const int LabelAvoidEveryNFrames = 2;
        // Cap stack so labels don't climb into the sky and hide ground pins.
        const int MaxLabelStack = 4;
        const float MaxStackWorldM = 55f;
        // Re-evaluate clustering when the viewer moves this far (metres).
        const float ClusterRecheckMoveM = 400f;

        [SerializeField] AppConfig config;
        [SerializeField] CotFeedClient feed;
        [SerializeField] Transform markersRoot;
        [SerializeField] Transform clustersRoot;
        [SerializeField] Camera xrCamera;
        [SerializeField] DemTerrainMap terrain;

        readonly Dictionary<string, CotMarkerView> _markers = new Dictionary<string, CotMarkerView>();
        readonly Dictionary<string, GameObject> _clusters = new Dictionary<string, GameObject>();
        readonly HashSet<string> _clusteredUids = new HashSet<string>();
        readonly List<CotMarkerView> _labelScratch = new List<CotMarkerView>(128);
        readonly List<int> _stackScratch = new List<int>(128);
        readonly List<bool> _crowdScratch = new List<bool>(128);
        GeoMath.Geodetic _origin;
        bool _dirty = true;
        float _nextForceSync;
        float _nextBudgetLog;
        int _frame;
        Vector3 _lastClusterCamLocal = new Vector3(float.NaN, 0f, 0f);

        public void Configure(AppConfig cfg, CotFeedClient feedClient, Camera cam)
        {
            config = cfg;
            feed = feedClient;
            xrCamera = cam;
            _origin = new GeoMath.Geodetic(cfg.originLat, cfg.originLon, cfg.originAlt);
            if (markersRoot == null)
            {
                var m = new GameObject("Markers");
                m.transform.SetParent(transform, false);
                markersRoot = m.transform;
            }
            if (clustersRoot == null)
            {
                var c = new GameObject("Clusters");
                c.transform.SetParent(transform, false);
                clustersRoot = c.transform;
            }
            feed.Changed += () => _dirty = true;
        }

        public void SetTerrain(DemTerrainMap dem)
        {
            if (terrain != null) terrain.TerrainChanged -= OnTerrainChanged;
            terrain = dem;
            // Re-clamp markers as soon as the tile under them finishes loading,
            // instead of waiting for the next 2 s force-sync.
            if (terrain != null) terrain.TerrainChanged += OnTerrainChanged;
        }

        void OnTerrainChanged()
        {
            _dirty = true;
            // Immediate height pass so markers don't wait a frame under new detail.
            ReclampExistingMarkers();
        }

        void ReclampExistingMarkers()
        {
            if (terrain == null || feed == null) return;
            foreach (var kv in _markers)
            {
                var view = kv.Value;
                if (view == null || view.Cot == null) continue;
                var cot = view.Cot;
                if (!terrain.TrySampleHae(cot.point.lat, cot.point.lon, out float demHae))
                    continue;
                var groundEnu = GeoMath.GeodeticToEnu(
                    new GeoMath.Geodetic(cot.point.lat, cot.point.lon, demHae), _origin);
                float groundLocalY = GeoMath.EnuToUnity(groundEnu).y;
                view.SetGroundClamp(groundLocalY, true);
                var lp = view.transform.localPosition;
                float minY = groundLocalY + GroundClearanceM + 4f;
                if (lp.y < minY)
                {
                    lp.y = minY;
                    view.transform.localPosition = lp;
                }
            }
        }

        void OnDestroy()
        {
            if (terrain != null) terrain.TerrainChanged -= OnTerrainChanged;
        }

        public void SetOrigin(GeoMath.Geodetic origin)
        {
            _origin = origin;
            _dirty = true;
        }

        public bool TryGetMarkerWorldPos(string uid, out Vector3 worldPos)
        {
            worldPos = Vector3.zero;
            if (string.IsNullOrEmpty(uid) || !_markers.TryGetValue(uid, out var view) || view == null)
                return false;
            worldPos = view.transform.position;
            return true;
        }

        public bool TryGetMarker(string uid, out CotMarkerView view) =>
            _markers.TryGetValue(uid, out view) && view != null;

        /// <summary>Raycast markers (and optionally clusters). Returns CoT uid.</summary>
        public bool TryRaycastSelect(Ray ray, float maxDist, out string uid)
        {
            uid = null;
            var hits = Physics.RaycastAll(ray, maxDist, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                var view = hit.transform.GetComponentInParent<CotMarkerView>();
                if (view == null || string.IsNullOrEmpty(view.Uid)) continue;
                uid = view.Uid;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Soft pick: closest marker whose billboard is within <paramref name="radiusM"/>
        /// of the ray (helps when icons are small on terrain).
        /// </summary>
        public bool TrySphereSelect(Ray ray, float maxDist, float radiusM, out string uid)
        {
            uid = null;
            float best = float.MaxValue;
            string bestUid = null;
            foreach (var kv in _markers)
            {
                var view = kv.Value;
                if (view == null) continue;
                var p = view.transform.position;
                var to = p - ray.origin;
                float along = Vector3.Dot(to, ray.direction);
                if (along < 0f || along > maxDist) continue;
                var closest = ray.origin + ray.direction * along;
                float dist = Vector3.Distance(closest, p);
                if (dist > radiusM) continue;
                if (dist < best)
                {
                    best = dist;
                    bestUid = view.Uid;
                }
            }
            if (bestUid == null) return false;
            uid = bestUid;
            return true;
        }

        public int VisibleMarkerCount => _markers.Count;

        /// <summary>World positions of currently visible markers (for Fit tracks).</summary>
        public void CollectVisibleWorldPositions(List<Vector3> into)
        {
            if (into == null) return;
            into.Clear();
            foreach (var kv in _markers)
            {
                if (kv.Value != null) into.Add(kv.Value.transform.position);
            }
        }

        void Update()
        {
            _frame++;
            if (Time.unscaledTime >= _nextForceSync)
            {
                // Near-ground detail LOD changes often — reclamp more often so CoTs
                // don't linger under freshly refined DEM meshes.
                float agl = terrain != null ? terrain.ViewerAglMeters : 5000f;
                _nextForceSync = Time.unscaledTime + (agl < 2500f ? 0.45f : 2f);
                _dirty = true;
            }

            if (xrCamera == null && Camera.main != null) xrCamera = Camera.main;
            if (xrCamera != null && markersRoot != null)
            {
                var camLocal = markersRoot.InverseTransformPoint(xrCamera.transform.position);
                if (float.IsNaN(_lastClusterCamLocal.x) ||
                    HorizontalDelta(camLocal, _lastClusterCamLocal) >= ClusterRecheckMoveM)
                {
                    _lastClusterCamLocal = camLocal;
                    _dirty = true;
                }
            }

            if (_dirty)
            {
                _dirty = false;
                Sync();
            }

            if (xrCamera == null) return;
            foreach (var kv in _markers)
                kv.Value.FaceCamera(xrCamera.transform);
            foreach (var kv in _clusters)
            {
                if (kv.Value == null) continue;
                var label = kv.Value.transform.Find("Count");
                if (label != null)
                    label.rotation = Quaternion.LookRotation(
                        label.position - xrCamera.transform.position, Vector3.up);
            }

            if ((_frame % LabelAvoidEveryNFrames) == 0)
                ResolveLabelCrowding();
        }

        static float HorizontalDelta(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public void ForceSync() => _dirty = true;

        void Sync()
        {
            if (feed == null || config == null) return;
            if (xrCamera == null && Camera.main != null) xrCamera = Camera.main;

            // Distance is from the viewer (not session origin) so flying toward a
            // bubble unclusters it — same rule as the web XR scene.
            double camEast = 0, camNorth = 0;
            if (xrCamera != null && markersRoot != null)
            {
                var camLocal = markersRoot.InverseTransformPoint(xrCamera.transform.position);
                camEast = camLocal.x;
                camNorth = camLocal.z;
                _lastClusterCamLocal = camLocal;
            }

            float enterM = config.ClusterEnterMeters;
            float exitM = config.ClusterExitMeters;
            float grid = Mathf.Max(0.05f, config.clusterGridDegrees);
            int minCount = Mathf.Max(2, config.clusterMinCount);

            var buckets = new Dictionary<string, List<(NormalizedCot cot, float dist)>>();
            var keep = new HashSet<string>();
            var nextClustered = new HashSet<string>();
            int liveShown = 0;

            foreach (var cot in feed.Cots.Values)
            {
                if (cot?.point == null || string.IsNullOrEmpty(cot.uid)) continue;
                var enu = GeoMath.GeodeticToEnu(
                    new GeoMath.Geodetic(cot.point.lat, cot.point.lon, 0),
                    _origin);
                float dist = Mathf.Sqrt(
                    (float)((enu.East - camEast) * (enu.East - camEast) +
                            (enu.North - camNorth) * (enu.North - camNorth)));
                bool air = cot.IsAirborne;
                bool wasClustered = _clusteredUids.Contains(cot.uid);
                bool farEnough = !air &&
                    (dist >= enterM || (wasClustered && dist >= exitM));

                if (farEnough)
                {
                    int latCell = Mathf.FloorToInt((float)(cot.point.lat / grid));
                    int lonCell = Mathf.FloorToInt((float)(cot.point.lon / grid));
                    var key = latCell + ":" + lonCell;
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        list = new List<(NormalizedCot, float)>();
                        buckets[key] = list;
                    }
                    list.Add((cot, dist));
                }
                else
                {
                    keep.Add(cot.uid);
                    UpsertMarker(cot);
                    liveShown++;
                }
            }

            // Sparse far cells (< minCount): keep as individual markers so nothing
            // important disappears over the horizon. Dense cells → one bubble.
            foreach (var kv in buckets)
            {
                if (kv.Value.Count < minCount)
                {
                    foreach (var (cot, _) in kv.Value)
                    {
                        keep.Add(cot.uid);
                        UpsertMarker(cot);
                        liveShown++;
                    }
                    continue;
                }

                foreach (var (cot, _) in kv.Value)
                    nextClustered.Add(cot.uid);
            }

            foreach (var uid in new List<string>(_markers.Keys))
            {
                if (keep.Contains(uid)) continue;
                Destroy(_markers[uid].gameObject);
                _markers.Remove(uid);
            }

            _clusteredUids.Clear();
            foreach (var uid in nextClustered) _clusteredUids.Add(uid);

            RebuildClusters(buckets, minCount);

            if (Time.unscaledTime >= _nextBudgetLog)
            {
                _nextBudgetLog = Time.unscaledTime + 10f;
                Debug.Log($"[CotLayer] feed={feed.Cots.Count} shown={liveShown} " +
                          $"clustered={_clusteredUids.Count} bubbles={_clusters.Count} " +
                          $"enterMi={enterM / 1609.344f:0} exitMi={exitM / 1609.344f:0}");
            }
        }

        void UpsertMarker(NormalizedCot cot)
        {
            var hae = GeoMath.ResolveRenderHae(cot.point.hae, config.allowHighAltitudeCots, out var ground);

            // Sample DEM ground height once: used both for unknown-altitude CoTs and
            // to clamp markers whose reported altitude is below the rendered terrain.
            // TrySampleHae prefers a mesh point sample (exaggeration-matched).
            float demHae = 0f;
            bool haveGround = terrain != null &&
                terrain.TrySampleHae(cot.point.lat, cot.point.lon, out demHae);
            if (haveGround && ground)
            {
                hae = demHae;
                ground = false;
            }

            var enu = GeoMath.GeodeticToEnu(
                new GeoMath.Geodetic(cot.point.lat, cot.point.lon, ground ? _origin.Alt : hae),
                _origin);
            var pos = GeoMath.EnuToUnity(enu);
            if (ground) pos.y = Mathf.Max(pos.y, 1.5f);

            // Never render below the map: many live CoTs report GPS altitudes under the
            // DEM surface. Clamp above the (point-sampled) terrain at that lat/lon.
            // The mesh sample already matches the RENDERED surface (exaggeration +
            // detail lift baked into vertices — see DemTerrainMap.TrySampleHae).
            float groundLocalY = 0f; // base plane when no DEM sample yet
            if (haveGround)
            {
                var groundEnu = GeoMath.GeodeticToEnu(
                    new GeoMath.Geodetic(cot.point.lat, cot.point.lon, demHae), _origin);
                groundLocalY = GeoMath.EnuToUnity(groundEnu).y;
                pos.y = Mathf.Max(pos.y, groundLocalY + GroundClearanceM);
            }
            else
            {
                // No DEM sample yet (tiles still loading) — never sink below the base
                // plane; TerrainChanged + the 2s re-sync lift markers onto real
                // terrain once it loads.
                pos.y = Mathf.Max(pos.y, FallbackClearanceM);
            }

            if (!_markers.TryGetValue(cot.uid, out var view))
            {
                var go = new GameObject($"COT:{cot.Callsign}");
                go.transform.SetParent(markersRoot, false);
                view = go.AddComponent<CotMarkerView>();
                _markers[cot.uid] = view;
            }

            // LOCAL position under XrWorldRoot (moves with map locomotion).
            if (ground) pos.y += 6f;
            else pos.y += 4f;
            view.transform.localPosition = pos;
            // Per-frame floor enforced in CotMarkerView.UpdateAngularScale: flat
            // discs also lift by half their (distance-dependent) world size.
            view.SetGroundClamp(groundLocalY, haveGround);
            view.BindTerrainSample(terrain, _origin, cot.point.lat, cot.point.lon);
            view.Bind(cot, config, xrCamera != null ? xrCamera.transform : null);
        }

        /// <summary>
        /// Cheap O(n²) callsign stacking: when markers crowd in camera angular
        /// space, push lower-priority labels upward (with a light lateral spiral).
        /// Ground glyphs NEVER move — CotMarkerView draws a leader line from the
        /// pin to the offset label so clusters still show map positions.
        /// </summary>
        void ResolveLabelCrowding()
        {
            _labelScratch.Clear();
            foreach (var kv in _markers)
            {
                var v = kv.Value;
                if (v != null && v.HasLabel) _labelScratch.Add(v);
            }
            int n = _labelScratch.Count;
            if (n == 0) return;

            _labelScratch.Sort((a, b) =>
            {
                int c = b.LabelPriority.CompareTo(a.LabelPriority);
                if (c != 0) return c;
                // Closer to camera wins when priority ties.
                float da = (a.transform.position - xrCamera.transform.position).sqrMagnitude;
                float db = (b.transform.position - xrCamera.transform.position).sqrMagnitude;
                return da.CompareTo(db);
            });

            _stackScratch.Clear();
            _crowdScratch.Clear();
            for (int i = 0; i < n; i++)
            {
                _stackScratch.Add(0);
                _crowdScratch.Add(false);
            }

            var camPos = xrCamera.transform.position;
            float cosThresh = Mathf.Cos(LabelCrowdAngleDeg * Mathf.Deg2Rad);

            for (int i = 0; i < n; i++)
            {
                var a = _labelScratch[i];
                var dirA = a.LabelAnchorWorld - camPos;
                float magA = dirA.magnitude;
                if (magA < 1e-3f) continue;
                dirA /= magA;

                int stack = 0;
                for (int j = 0; j < i; j++)
                {
                    var b = _labelScratch[j];
                    var dirB = b.LabelAnchorWorld - camPos;
                    float magB = dirB.magnitude;
                    if (magB < 1e-3f) continue;
                    dirB /= magB;
                    if (Vector3.Dot(dirA, dirB) < cosThresh) continue;
                    // Same angular cluster — sit above the higher-priority label's stack.
                    stack = Mathf.Max(stack, _stackScratch[j] + 1);
                    // Both ends of the pair are grouped (incl. the stack-0 leader)
                    // — they render at reduced scale so the pile stays readable.
                    _crowdScratch[i] = true;
                    _crowdScratch[j] = true;
                }
                stack = Mathf.Min(stack, MaxLabelStack);
                _stackScratch[i] = stack;

                if (stack <= 0)
                {
                    a.ClearLabelAvoidanceOffset();
                    continue;
                }

                // Modest spacing — prefer short stacks + leader lines over sky columns.
                float spacing = Mathf.Max(a.LabelWorldHeight * 1.05f, magA * 0.012f);
                spacing = Mathf.Min(spacing, 18f);
                float lateral = spacing * 0.4f * Mathf.Sin(stack * 1.2f);
                var right = Vector3.Cross(Vector3.up, dirA);
                if (right.sqrMagnitude < 1e-6f) right = xrCamera.transform.right;
                else right.Normalize();
                float upM = Mathf.Min(stack * spacing, MaxStackWorldM);
                var worldOff = Vector3.up * upM + right * lateral;
                a.SetLabelAvoidanceOffset(a.transform.InverseTransformVector(worldOff));
            }

            // Apply group shrink after both loop directions marked pairs.
            for (int i = 0; i < n; i++)
                _labelScratch[i].SetCrowded(_crowdScratch[i]);
        }

        void RebuildClusters(
            Dictionary<string, List<(NormalizedCot cot, float dist)>> buckets,
            int minCount)
        {
            foreach (var kv in _clusters) Destroy(kv.Value);
            _clusters.Clear();

            foreach (var kv in buckets)
            {
                if (kv.Value.Count < minCount) continue;

                double lat = 0, lon = 0;
                foreach (var (c, _) in kv.Value)
                {
                    lat += c.point.lat;
                    lon += c.point.lon;
                }
                lat /= kv.Value.Count;
                lon /= kv.Value.Count;
                int count = kv.Value.Count;

                var enu = GeoMath.GeodeticToEnu(new GeoMath.Geodetic(lat, lon, 0), _origin);
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Cluster:{count}";
                go.transform.SetParent(clustersRoot, false);
                go.transform.localPosition = GeoMath.EnuToUnity(enu) + Vector3.up * 40f;
                float scale = 35f + Mathf.Min(count, 120) * 0.6f;
                go.transform.localScale = Vector3.one * scale;
                var rend = go.GetComponent<Renderer>();
                if (rend != null)
                {
                    var sh = Shader.Find("Universal Render Pipeline/Lit")
                             ?? Shader.Find("Universal Render Pipeline/Unlit")
                             ?? Shader.Find("Unlit/Color")
                             ?? Shader.Find("Sprites/Default")
                             ?? Shader.Find("Standard");
                    if (sh != null)
                    {
                        try { rend.material = new Material(sh); }
                        catch { /* keep CreatePrimitive default */ }
                    }
                    var col = new Color(1f, 0.55f, 0.1f, 0.45f);
                    if (rend.material != null)
                    {
                        if (rend.material.HasProperty("_BaseColor")) rend.material.SetColor("_BaseColor", col);
                        else if (rend.material.HasProperty("_Color")) rend.material.SetColor("_Color", col);
                        else
                        {
                            try { rend.material.color = col; } catch { /* ignore */ }
                        }
                    }
                }
                var colider = go.GetComponent<Collider>();
                if (colider != null) colider.enabled = false;

                // Count label (web-style numbered bubble).
                var labelGo = new GameObject("Count");
                labelGo.transform.SetParent(go.transform, false);
                labelGo.transform.localPosition = new Vector3(0f, 0.6f, 0f);
                labelGo.transform.localScale = Vector3.one * (2.2f / Mathf.Max(scale, 1f));
                var tm = labelGo.AddComponent<TextMesh>();
                tm.text = count.ToString();
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.characterSize = 0.35f;
                tm.fontSize = 120;
                tm.color = Color.white;
                tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                          ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

                _clusters[kv.Key] = go;
            }
        }
    }
}
