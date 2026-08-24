using System;
using System.Collections;
using System.Collections.Generic;
using TakXr.Core;
using UnityEngine;

namespace TakXr.Map
{
    /// <summary>
    /// Streaming 3D terrain: Terrarium DEM + Google Hybrid imagery (disk-cached under
    /// /sdcard/takxr/tiles when writable). Loads a wide base-zoom ring around the
    /// viewer within mapRadiusMeters, plus nested detail rings (base+2 … base+4,
    /// i.e. z14–z16 at base z12) near the viewer when the effective height above
    /// ground is low. Detail rings are aligned to their parent tiles so a fully
    /// covered parent can be hidden (no z-fighting).
    /// </summary>
    public class DemTerrainMap : MonoBehaviour
    {
        // Google hybrid (lyrs=y): satellite + roads + place labels, like ATAK's
        // "Google Hybrid" basemap. ESRI World_Imagery kept as selectable + fallback.
        const string GoogleHybridTemplate =
            "https://mt{s}.google.com/vt/lyrs=y&x={x}&y={y}&z={z}";
        const string GoogleSatelliteTemplate =
            "https://mt{s}.google.com/vt/lyrs=s&x={x}&y={y}&z={z}";
        const string GoogleRoadsTemplate =
            "https://mt{s}.google.com/vt/lyrs=m&x={x}&y={y}&z={z}";
        const string EsriTemplate =
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
        const string DemTemplate =
            "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";

        /// <summary>Basemap imagery choice (ATAK Map Manager parity).</summary>
        public enum ImageryMode
        {
            GoogleHybrid = 0,
            GoogleSatellite = 1,
            GoogleRoads = 2,
            EsriWorldImagery = 3,
        }

        // Terrarium tiles only exist through z15; finer tiles upsample the parent DEM.
        const int DemMaxZoom = 15;

        // Detail LOD thresholds (meters of effective height above ground = viewer
        // height divided by world scale). Enter a level below its threshold; leave
        // it only after climbing ExitFactor past it (hysteresis against thrash).
        const float EnterDetail1 = 8000f; // base+2 (z14)
        const float EnterDetail2 = 2500f; // base+3 (z15)
        const float EnterDetail3 = 1000f; // base+4 (z16)
        const float DetailExitFactor = 1.25f;

        // Horizontal coverage radius per detail level; tiles are kept until
        // DetailDropFactor × radius so panning doesn't churn the ring edge.
        const float RadiusDetail1 = 2400f;
        const float RadiusDetail2 = 1200f;
        const float RadiusDetail3 = 600f;
        const float DetailDropFactor = 1.9f;

        // Failed detail tiles retry after this long instead of flat-falling back.
        const float DetailRetrySeconds = 30f;

        // Lift detail meshes slightly per level so they win depth against a
        // partially covered parent (parents are hidden once fully covered).
        const float DetailLiftPerLevel = 0.35f;

        // Streaming cadence. The base ring rarely changes, so it keeps the slow
        // tick; detail rings refresh faster once the viewer is below the z15
        // threshold so refinement keeps up with low-altitude movement.
        const float BaseStreamInterval = 0.75f;
        const float DetailStreamIntervalLow = 0.3f;

        // Detail jobs behind the camera sort after everything in front of it
        // (penalty larger than any detail ring diameter, so close-behind still
        // beats far-behind).
        const float BehindPenaltyMeters = 4000f;

        // Cap coroutine starts per frame so texture decode + mesh build cost is
        // spread across frames instead of hitching one.
        const int MaxStartsPerFrame = 2;

        // LRU of decoded imagery for recently culled detail tiles, keyed
        // z/x/y, so re-entering an area repaints without a re-download.
        const int ImageryCacheMax = 60;

        static int _imgServer;
        ImageryMode _imageryMode = ImageryMode.GoogleHybrid;
        /// <summary>0–1 map brightness multiplier (ATAK Brightness tool).</summary>
        float _brightness = 1f;
        const string PrefBrightness = "takxr.mapBrightness";

        public ImageryMode CurrentImageryMode => _imageryMode;
        public float Brightness => _brightness;

        string ImageryUrl(int x, int y, int z)
        {
            if (_imageryMode == ImageryMode.EsriWorldImagery)
            {
                return EsriTemplate
                    .Replace("{z}", z.ToString())
                    .Replace("{y}", y.ToString())
                    .Replace("{x}", x.ToString());
            }

            string template = _imageryMode switch
            {
                ImageryMode.GoogleSatellite => GoogleSatelliteTemplate,
                ImageryMode.GoogleRoads => GoogleRoadsTemplate,
                _ => GoogleHybridTemplate,
            };
            _imgServer = (_imgServer + 1) & 3; // rotate mt0–mt3 like map clients do
            return template
                .Replace("{s}", _imgServer.ToString())
                .Replace("{z}", z.ToString())
                .Replace("{x}", x.ToString())
                .Replace("{y}", y.ToString());
        }

        [SerializeField] AppConfig config;
        [SerializeField] int zoom = 12;
        [SerializeField] int meshResolution = 20;
        [SerializeField] int maxConcurrent = 8;     // total inflight budget
        [SerializeField] int maxConcurrentBase = 3; // base-ring share of the budget
        [SerializeField] int maxLoadedTiles = 240;
        [SerializeField] float verticalExaggeration = 1.15f;
        [SerializeField] Transform viewer;

        class TileRec
        {
            public GameObject Go;
            public int X, Y, Z;
            public bool Loaded;    // mesh built + imagery attempt finished
            public bool Covering;  // counted toward hiding its parent tile
            public Material Mat;   // owned material (destroyed on removal)
            public Texture2D Tex;  // owned imagery (cached or destroyed on removal)
        }

        class CachedImage
        {
            public string Key;
            public Texture2D Tex;
        }

        readonly Dictionary<string, TileRec> _tiles = new Dictionary<string, TileRec>();
        readonly Dictionary<long, float> _heightCache = new Dictionary<long, float>(); // base zoom only
        readonly Queue<TileJob> _queue = new Queue<TileJob>();
        readonly List<TileJob> _detailQueue = new List<TileJob>(); // drained before _queue, sorted per frame
        readonly List<(float score, TileJob job)> _detailScratch = new List<(float, TileJob)>();
        readonly HashSet<string> _queued = new HashSet<string>();
        readonly Dictionary<string, int> _childLoaded = new Dictionary<string, int>(); // parent key → textured children
        readonly Dictionary<string, float> _retryAt = new Dictionary<string, float>();
        readonly Dictionary<string, LinkedListNode<CachedImage>> _imgCache =
            new Dictionary<string, LinkedListNode<CachedImage>>();
        readonly LinkedList<CachedImage> _imgLru = new LinkedList<CachedImage>(); // first = most recent
        int _inflight;
        int _inflightBase;
        float _nextStream;
        float _nextDetailStream;
        float _lastAgl = float.MaxValue; // effective AGL from the last detail tick
        int _detailZoom; // 0 = no detail, else finest active detail zoom
        static Material _unlitTemplate;

        struct TileJob
        {
            public int X, Y, Z;
            public GameObject Go;
            public string Key;
        }

        public int LoadedCount { get; private set; }
        public bool IsReady => LoadedCount > 0;

        /// <summary>
        /// Fired when a tile finishes loading (mesh + imagery attempt). Height
        /// samples over that area just changed — CotLayerController listens and
        /// re-clamps markers instead of waiting for its next periodic sync.
        /// </summary>
        public event Action TerrainChanged;

        public void Configure(AppConfig cfg)
        {
            config = cfg;
            _brightness = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefBrightness, 1f));
            TakTileDiskCache.EnsureInit();
            EnsureSeedAroundOrigin();
        }

        /// <summary>
        /// ATAK Brightness tool: multiply imagery tint (0–1). Persisted via PlayerPrefs.
        /// </summary>
        public void SetBrightness(float value01)
        {
            _brightness = Mathf.Clamp01(value01);
            PlayerPrefs.SetFloat(PrefBrightness, _brightness);
            PlayerPrefs.Save();
            ApplyBrightnessToLoadedTiles();
        }

        void ApplyBrightnessToLoadedTiles()
        {
            var tint = new Color(_brightness, _brightness, _brightness, 1f);
            foreach (var kv in _tiles)
            {
                var mat = kv.Value.Mat;
                if (mat == null) continue;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
            }
        }

        public void SetViewer(Transform cam) => viewer = cam;

        void OnDestroy() => ClearImageryCache();

        public void Rebuild()
        {
            foreach (var kv in _tiles)
                DestroyTileAssets(kv.Value, cacheImagery: false);
            _tiles.Clear();
            _queue.Clear();
            _detailQueue.Clear();
            _queued.Clear();
            _heightCache.Clear();
            _childLoaded.Clear();
            _retryAt.Clear();
            ClearImageryCache();
            _detailZoom = 0;
            _lastAgl = float.MaxValue;
            _nextStream = 0f;
            _nextDetailStream = 0f;
            LoadedCount = 0;
            EnsureSeedAroundOrigin();
        }

        /// <summary>
        /// Sample terrain HAE at a point. Prefers bilinear height from the finest
        /// loaded tile mesh (matches the rendered surface, including vertical
        /// exaggeration and detail lift). Falls back to base-zoom tile mean with
        /// exaggeration applied so CoT clamps don't sink under hills.
        /// </summary>
        public bool TrySampleHae(double lat, double lon, out float hae)
        {
            hae = (float)(config != null ? config.originAlt : 0);
            if (config == null) return false;

            // Point sample from the rendered mesh — accurate on hills/ridges where
            // a per-tile MEAN underestimates local peaks and markers clip into DEM.
            if (TrySampleMeshLocalY(lat, lon, out float localY))
            {
                // CotLayerController converts HAE→ENU→Unity Y without exaggeration.
                // Mesh verts already bake exaggeration + detail lift into local Y, so
                // return an effective HAE that reproduces that surface Y.
                hae = (float)config.originAlt + localY;
                return true;
            }

            LatLonToTile(lat, lon, zoom, out int tx, out int ty);
            long key = Pack(tx, ty);
            if (_heightCache.TryGetValue(key, out float center))
            {
                // Mean HAE → effective (exaggerated) so clamp stays above the mesh.
                hae = (float)config.originAlt +
                      (center - (float)config.originAlt) * verticalExaggeration;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Bilinear-sample local Unity Y from the finest loaded tile mesh covering
        /// (lat, lon). Returns false when no mesh is available yet.
        /// </summary>
        bool TrySampleMeshLocalY(double lat, double lon, out float localY)
        {
            localY = 0f;
            TileRec best = null;
            int bestZ = -1;
            foreach (var kv in _tiles)
            {
                var rec = kv.Value;
                if (rec == null || rec.Go == null) continue;
                if (rec.Z < bestZ) continue;
                TileBoundsToLatLon(rec.X, rec.Y, rec.Z,
                    out double north, out double west, out double south, out double east);
                if (lat > north || lat < south || lon < west || lon > east) continue;
                var mf = rec.Go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                best = rec;
                bestZ = rec.Z;
            }
            if (best == null) return false;

            var mesh = best.Go.GetComponent<MeshFilter>().sharedMesh;
            var verts = mesh.vertices;
            int n = verts.Length;
            // Regular grid built by BuildHeightMesh: (res+1)^2 verts.
            int side = Mathf.RoundToInt(Mathf.Sqrt(n));
            if (side < 2 || side * side != n) return false;
            int res = side - 1;

            TileBoundsToLatLon(best.X, best.Y, best.Z,
                out double nLat, out double wLon, out double sLat, out double eLon);
            double latSpan = nLat - sLat;
            double lonSpan = eLon - wLon;
            if (Math.Abs(latSpan) < 1e-12 || Math.Abs(lonSpan) < 1e-12) return false;

            // Same UV convention as BuildHeightMesh: u west→east, v north→south row j.
            float u = (float)((lon - wLon) / lonSpan);
            float v = (float)((nLat - lat) / latSpan);
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);

            float gx = u * res;
            float gy = v * res;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(gx), 0, res - 1);
            int j0 = Mathf.Clamp(Mathf.FloorToInt(gy), 0, res - 1);
            float fx = gx - i0;
            float fy = gy - j0;

            float y00 = verts[j0 * side + i0].y;
            float y10 = verts[j0 * side + (i0 + 1)].y;
            float y01 = verts[(j0 + 1) * side + i0].y;
            float y11 = verts[(j0 + 1) * side + (i0 + 1)].y;
            localY = Mathf.Lerp(
                Mathf.Lerp(y00, y10, fx),
                Mathf.Lerp(y01, y11, fx),
                fy);
            return true;
        }

        void EnsureSeedAroundOrigin()
        {
            if (config == null) return;
            EnqueueAround(config.originLat, config.originLon, seedRing: 3);
        }

        void Update()
        {
            DrainQueues();

            float now = Time.unscaledTime;
            bool baseDue = now >= _nextStream;
            bool detailDue = now >= _nextDetailStream;
            if ((baseDue || detailDue) && config != null)
            {
                ApproximateViewerLatLon(out double lat, out double lon);
                if (baseDue)
                {
                    _nextStream = now + BaseStreamInterval;
                    EnqueueAround(lat, lon, seedRing: -1);
                    CullFarTiles(lat, lon);
                }
                if (detailDue)
                {
                    UpdateDetailRings(lat, lon); // refreshes _lastAgl
                    _nextDetailStream = now + (_lastAgl < EnterDetail2
                        ? DetailStreamIntervalLow
                        : BaseStreamInterval);
                }
            }
        }

        void DrainQueues()
        {
            if (_detailQueue.Count == 0 && _queue.Count == 0) return;
            SortDetailQueue();

            int starts = 0;
            while (starts < MaxStartsPerFrame && _inflight < maxConcurrent &&
                   (_detailQueue.Count > 0 || _queue.Count > 0))
            {
                bool detail = _detailQueue.Count > 0;
                TileJob job;
                if (detail)
                {
                    // Sorted worst-first, so the best job pops off the end.
                    job = _detailQueue[_detailQueue.Count - 1];
                    _detailQueue.RemoveAt(_detailQueue.Count - 1);
                }
                else
                {
                    if (_inflightBase >= Mathf.Min(maxConcurrentBase, maxConcurrent))
                        break; // base share used up; detail queue is empty anyway
                    job = _queue.Dequeue();
                }
                _queued.Remove(job.Key);
                if (job.Go == null) continue; // culled while queued
                _inflight++;
                if (!detail) _inflightBase++;
                starts++;
                StartCoroutine(LoadTile(job));
            }
        }

        /// <summary>
        /// Order pending detail jobs so the closest tiles in front of the
        /// camera load first (worst score at index 0, best at the end).
        /// </summary>
        void SortDetailQueue()
        {
            if (_detailQueue.Count < 2) return;
            ApproximateViewerLatLon(out double lat, out double lon);

            float fe = 0f, fn = 1f; // camera forward in map-local east/north
            if (viewer != null)
            {
                var f = transform.InverseTransformDirection(viewer.forward);
                float m = Mathf.Sqrt(f.x * f.x + f.z * f.z);
                if (m > 1e-4f) { fe = f.x / m; fn = f.z / m; }
            }

            _detailScratch.Clear();
            foreach (var job in _detailQueue)
            {
                TileOffsetMeters(job.Z, job.X, job.Y, lat, lon, out float east, out float north);
                float d = Mathf.Sqrt(east * east + north * north);
                if (east * fe + north * fn < 0f) d += BehindPenaltyMeters;
                _detailScratch.Add((d, job));
            }
            _detailScratch.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < _detailScratch.Count; i++)
                _detailQueue[i] = _detailScratch[i].job;
        }

        void ApproximateViewerLatLon(out double lat, out double lon)
        {
            lat = config.originLat;
            lon = config.originLon;
            if (viewer == null && Camera.main != null) viewer = Camera.main.transform;
            if (viewer == null) return;

            // Viewer is in Unity world; map lives under this transform (world root).
            var local = transform.InverseTransformPoint(viewer.position);
            const double mPerDegLat = 111320.0;
            double mPerDegLon = mPerDegLat * Math.Cos(config.originLat * Math.PI / 180.0);
            if (Math.Abs(mPerDegLon) < 1e-3) mPerDegLon = mPerDegLat;
            lat = config.originLat + local.z / mPerDegLat;
            lon = config.originLon + local.x / mPerDegLon;
        }

        void EnqueueAround(double lat, double lon, int seedRing)
        {
            LatLonToTile(lat, lon, zoom, out int cx, out int cy);
            float radiusM = config != null ? config.mapRadiusMeters : 100f * 1609.344f;
            // Approx tile width at mid-lat zoom.
            double tileM = 156543.03392 * Math.Cos(lat * Math.PI / 180.0) / Math.Pow(2, zoom);
            int ring = seedRing >= 0
                ? seedRing
                : Mathf.Clamp(Mathf.CeilToInt((float)(radiusM / tileM)) + 1, 2, 14);

            for (int dy = -ring; dy <= ring; dy++)
            for (int dx = -ring; dx <= ring; dx++)
            {
                if (dx * dx + dy * dy > ring * ring + ring) continue;
                EnqueueTile(zoom, cx + dx, cy + dy, detail: false);
            }
        }

        void EnqueueTile(int z, int x, int y, bool detail)
        {
            if (x < 0 || y < 0) return;
            int maxIndex = (1 << z) - 1;
            if (x > maxIndex || y > maxIndex) return;
            string key = TileKey(z, x, y);
            if (_tiles.ContainsKey(key) || _queued.Contains(key)) return;
            if (_retryAt.TryGetValue(key, out float retry))
            {
                if (Time.unscaledTime < retry) return;
                _retryAt.Remove(key);
            }
            var go = new GameObject("Terrain:" + key);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var placeholder = MakeTerrainMaterial(null, new Color(0.2f, 0.25f, 0.22f, 1f));
            mr.sharedMaterial = placeholder;
            // Detail tiles stay invisible until imagery arrives so the DEM mesh
            // never flashes untextured above the (still visible) parent tile.
            if (detail) mr.enabled = false;
            _tiles[key] = new TileRec { Go = go, X = x, Y = y, Z = z, Mat = placeholder };
            _queued.Add(key);
            var job = new TileJob { X = x, Y = y, Z = z, Go = go, Key = key };
            if (detail) _detailQueue.Add(job);
            else _queue.Enqueue(job);
        }

        // ---------------------------------------------------------------- LOD

        float DetailEnterHeight(int z)
        {
            if (z == zoom + 2) return EnterDetail1;
            if (z == zoom + 3) return EnterDetail2;
            return EnterDetail3;
        }

        float DetailRadius(int z)
        {
            if (z == zoom + 2) return RadiusDetail1;
            if (z == zoom + 3) return RadiusDetail2;
            return RadiusDetail3;
        }

        /// <summary>Viewer AGL used for detail LOD — CoT layer uses this to sync faster near ground.</summary>
        public float ViewerAglMeters
        {
            get
            {
                ApproximateViewerLatLon(out double lat, out double lon);
                return EffectiveHeightAboveGround(lat, lon);
            }
        }

        /// <summary>
        /// Effective height above ground in map meters: local (scale-corrected)
        /// height above the origin-altitude plane minus terrain height under
        /// the viewer. TrySampleHae returns an effective (mesh-matched) altitude
        /// whose offset from originAlt already includes vertical exaggeration.
        /// </summary>
        float EffectiveHeightAboveGround(double lat, double lon)
        {
            if (viewer == null || config == null) return float.MaxValue;
            float y = transform.InverseTransformPoint(viewer.position).y;
            if (TrySampleHae(lat, lon, out float groundHae))
                y -= groundHae - (float)config.originAlt;
            return Mathf.Max(y, 8f);
        }

        int PickDetailZoom(float h)
        {
            int target = h < EnterDetail3 ? zoom + 4
                       : h < EnterDetail2 ? zoom + 3
                       : h < EnterDetail1 ? zoom + 2
                       : 0;
            if (target >= _detailZoom) return target; // descending → refine immediately
            // Climbing: keep the current level until clearly past its threshold.
            float exit = DetailEnterHeight(_detailZoom) * DetailExitFactor;
            return h > exit ? target : _detailZoom;
        }

        int DetailParentZoom(int z) => z == zoom + 2 ? zoom : z - 1;

        /// <summary>
        /// True when this detail tile is a child of the parent tile directly
        /// under the viewer. Those children are always loaded and never dropped
        /// (while their zoom is active) so the parent's covering set completes
        /// and the parent can be hidden.
        /// </summary>
        bool IsAnchorChild(TileRec rec, double lat, double lon)
        {
            int pz = DetailParentZoom(rec.Z);
            int shift = rec.Z - pz;
            LatLonToTile(lat, lon, pz, out int pvx, out int pvy);
            return (rec.X >> shift) == pvx && (rec.Y >> shift) == pvy;
        }

        bool DetailTileProtected(TileRec rec, double lat, double lon)
        {
            if (_detailZoom == 0 || rec.Z > _detailZoom) return false; // viewer climbed
            if (TileCenterDistanceMeters(rec.Z, rec.X, rec.Y, lat, lon) <=
                DetailRadius(rec.Z) * DetailDropFactor)
                return true;
            return IsAnchorChild(rec, lat, lon);
        }

        void UpdateDetailRings(double lat, double lon)
        {
            _lastAgl = EffectiveHeightAboveGround(lat, lon);
            _detailZoom = PickDetailZoom(_lastAgl);

            // Drop tiles whose zoom is no longer active (viewer climbed) and
            // tiles that drifted out of their ring (viewer panned).
            List<string> drop = null;
            foreach (var kv in _tiles)
            {
                var rec = kv.Value;
                if (rec.Z <= zoom) continue;
                if (DetailTileProtected(rec, lat, lon)) continue;
                (drop ??= new List<string>()).Add(kv.Key);
            }
            if (drop != null)
                foreach (var key in drop) RemoveTile(key);

            for (int z = zoom + 2; z <= _detailZoom; z++)
                EnqueueDetailRing(lat, lon, z);
        }

        void EnqueueDetailRing(double lat, double lon, int z)
        {
            // 1) Anchor: every child of the parent tile under the viewer, so the
            //    parent's covering set can complete and the parent gets hidden.
            int pz = DetailParentZoom(z);
            int shift = z - pz;
            int sub = 1 << shift;
            LatLonToTile(lat, lon, pz, out int pvx, out int pvy);
            for (int sy = 0; sy < sub; sy++)
            for (int sx = 0; sx < sub; sx++)
                EnqueueTile(z, (pvx << shift) + sx, (pvy << shift) + sy, detail: true);

            // 2) Ring: tiles at this zoom whose center lies within the radius,
            //    so the viewer never stands next to a hard LOD edge.
            float radius = DetailRadius(z);
            double dLat = radius / 111320.0;
            double dLon = radius / Math.Max(111320.0 * Math.Cos(lat * Math.PI / 180.0), 1e-3);
            LatLonToTile(lat + dLat, lon - dLon, z, out int x0, out int y0);
            LatLonToTile(lat - dLat, lon + dLon, z, out int x1, out int y1);
            for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                if (TileCenterDistanceMeters(z, tx, ty, lat, lon) > radius) continue;
                EnqueueTile(z, tx, ty, detail: true);
            }
        }

        static string TileKey(int z, int x, int y) => z + "/" + x + "/" + y;

        bool TryParent(int z, int x, int y, out string parentKey, out int required)
        {
            // Active zoom ladder: base → base+2 (16 children) → base+3 → base+4.
            if (z == zoom + 2)
            {
                parentKey = TileKey(zoom, x >> 2, y >> 2);
                required = 16;
                return true;
            }
            if (z == zoom + 3 || z == zoom + 4)
            {
                parentKey = TileKey(z - 1, x >> 1, y >> 1);
                required = 4;
                return true;
            }
            parentKey = null;
            required = 0;
            return false;
        }

        int RequiredChildren(int z)
        {
            if (z == zoom) return 16;
            if (z == zoom + 2 || z == zoom + 3) return 4;
            return int.MaxValue;
        }

        void OnTileLoaded(TileRec rec, bool textured)
        {
            if (rec.Loaded) return;
            rec.Loaded = true;
            LoadedCount++;
            TerrainChanged?.Invoke();

            if (textured && TryParent(rec.Z, rec.X, rec.Y, out string pk, out int req))
            {
                rec.Covering = true;
                _childLoaded.TryGetValue(pk, out int count);
                count++;
                _childLoaded[pk] = count;
                if (count >= req && _tiles.TryGetValue(pk, out var parent) && parent.Go != null)
                    parent.Go.SetActive(false);
            }

            // If this tile is itself a parent whose covering set already loaded
            // (children arrived first), hide it immediately.
            if (rec.Go != null &&
                _childLoaded.TryGetValue(TileKey(rec.Z, rec.X, rec.Y), out int mine) &&
                mine >= RequiredChildren(rec.Z))
                rec.Go.SetActive(false);
        }

        void RemoveTile(string key)
        {
            if (!_tiles.TryGetValue(key, out var rec)) return;
            _tiles.Remove(key);
            DestroyTileAssets(rec, cacheImagery: rec.Z > zoom);
            if (!rec.Loaded) return;
            if (LoadedCount > 0) LoadedCount--;

            if (rec.Covering && TryParent(rec.Z, rec.X, rec.Y, out string pk, out int req) &&
                _childLoaded.TryGetValue(pk, out int count))
            {
                count--;
                if (count <= 0) _childLoaded.Remove(pk);
                else _childLoaded[pk] = count;
                if (count < req && _tiles.TryGetValue(pk, out var parent) && parent.Go != null)
                    parent.Go.SetActive(true); // coverage broken → re-show parent
            }
        }

        void CullFarTiles(double lat, double lon)
        {
            if (_tiles.Count <= maxLoadedTiles) return;
            var scored = new List<(string key, float score)>(_tiles.Count);
            foreach (var kv in _tiles)
            {
                var rec = kv.Value;
                float d = TileCenterDistanceMeters(rec.Z, rec.X, rec.Y, lat, lon);
                // Stale detail tiles go first; active detail rings sit near the
                // viewer so plain distance keeps them out of the cull window.
                if (rec.Z > zoom && !DetailTileProtected(rec, lat, lon))
                    d += 1e8f;
                scored.Add((kv.Key, d));
            }
            scored.Sort((a, b) => b.score.CompareTo(a.score));
            int remove = _tiles.Count - maxLoadedTiles;
            for (int i = 0; i < remove && i < scored.Count; i++)
                RemoveTile(scored[i].key);
        }

        float TileCenterDistanceMeters(int z, int x, int y, double lat, double lon)
        {
            TileOffsetMeters(z, x, y, lat, lon, out float east, out float north);
            return Mathf.Sqrt(east * east + north * north);
        }

        /// <summary>East/north offset in meters from (lat, lon) to the tile center.</summary>
        void TileOffsetMeters(int z, int x, int y, double lat, double lon,
            out float east, out float north)
        {
            TileBoundsToLatLon(x, y, z, out double n, out double w, out double s, out double e);
            double cLat = (n + s) * 0.5;
            double cLon = (w + e) * 0.5;
            const double mPerDegLat = 111320.0;
            double mPerDegLon = mPerDegLat * Math.Cos(lat * Math.PI / 180.0);
            east = (float)((cLon - lon) * mPerDegLon);
            north = (float)((cLat - lat) * mPerDegLat);
        }

        /// <summary>
        /// Destroy a tile's GameObject, mesh, and material. Detail imagery goes
        /// into the LRU cache instead of being destroyed; everything else is
        /// destroyed outright.
        /// </summary>
        void DestroyTileAssets(TileRec rec, bool cacheImagery)
        {
            if (rec.Go != null)
            {
                var mf = rec.Go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
                Destroy(rec.Go);
            }
            if (rec.Mat != null) Destroy(rec.Mat);
            if (rec.Tex != null)
            {
                if (cacheImagery) CacheImagery(TileKey(rec.Z, rec.X, rec.Y), rec.Tex);
                else Destroy(rec.Tex);
            }
            rec.Mat = null;
            rec.Tex = null;
        }

        // -------------------------------------------------------- imagery cache

        void CacheImagery(string key, Texture2D tex)
        {
            if (tex == null) return;
            if (_imgCache.TryGetValue(key, out var existing))
            {
                if (existing.Value.Tex != null && existing.Value.Tex != tex)
                    Destroy(existing.Value.Tex);
                _imgLru.Remove(existing);
                _imgCache.Remove(key);
            }
            _imgCache[key] = _imgLru.AddFirst(new CachedImage { Key = key, Tex = tex });
            while (_imgCache.Count > ImageryCacheMax)
            {
                var oldest = _imgLru.Last;
                _imgLru.RemoveLast();
                _imgCache.Remove(oldest.Value.Key);
                if (oldest.Value.Tex != null) Destroy(oldest.Value.Tex);
            }
        }

        /// <summary>Remove and return a cached texture; ownership passes to the caller.</summary>
        bool TryTakeCachedImagery(string key, out Texture2D tex)
        {
            tex = null;
            if (!_imgCache.TryGetValue(key, out var node)) return false;
            tex = node.Value.Tex;
            _imgLru.Remove(node);
            _imgCache.Remove(key);
            return tex != null;
        }

        void ClearImageryCache()
        {
            foreach (var entry in _imgLru)
                if (entry.Tex != null) Destroy(entry.Tex);
            _imgLru.Clear();
            _imgCache.Clear();
        }

        // ------------------------------------------------------------- loading

        IEnumerator LoadTile(TileJob job)
        {
            // _inflight/_inflightBase were incremented by DrainQueues.
            bool isBase = job.Z <= zoom;
            try
            {
                TakTileDiskCache.EnsureInit();

                // Terrarium ends at z15: finer tiles sample a sub-window of the
                // parent DEM tile instead (imagery still fetched at the tile's zoom).
                int demZ = Math.Min(job.Z, DemMaxZoom);
                int demShift = job.Z - demZ;
                int demX = job.X >> demShift;
                int demY = job.Y >> demShift;
                string demUrl = DemTemplate
                    .Replace("{z}", demZ.ToString())
                    .Replace("{x}", demX.ToString())
                    .Replace("{y}", demY.ToString());

                Texture2D demTex = null;
                string demKey = TakTileDiskCache.DemKey(demZ, demX, demY);
                yield return TakTileDiskCache.FetchTexture(demUrl, demKey, readable: true, 30,
                    t => demTex = t);
                if (demTex == null)
                {
                    Debug.LogWarning($"[DemTerrainMap] DEM fail {job.Z}/{job.X}/{job.Y}");
                    if (job.Z > zoom)
                    {
                        // Detail tile: never flat-fallback (would float at origin
                        // altitude over the base mesh) — drop and retry later.
                        _retryAt[job.Key] = Time.unscaledTime + DetailRetrySeconds;
                        RemoveTile(job.Key);
                    }
                    else
                    {
                        BuildFlatFallback(job);
                    }
                    yield break;
                }

                if (job.Go == null)
                {
                    Destroy(demTex);
                    yield break;
                }

                float meanHae = BuildHeightMesh(job, demTex, demShift);
                if (job.Z == zoom)
                    _heightCache[Pack(job.X, job.Y)] = meanHae; // base zoom only: TrySampleHae contract
                Destroy(demTex);

                // Mesh is live — reclamps CoTs even before imagery arrives.
                if (_tiles.TryGetValue(job.Key, out var early) && early.Go == job.Go && !early.Loaded)
                    TerrainChanged?.Invoke();

                // Recently culled detail tile re-entering the ring: repaint from
                // the LRU cache without a network round-trip.
                bool textured = false;
                if (job.Z > zoom && TryTakeCachedImagery(job.Key, out var cached))
                    textured = ApplyImagery(job, cached);

                if (!textured)
                {
                    string imgUrl = ImageryUrl(job.X, job.Y, job.Z);
                    string imgKey = TakTileDiskCache.ImageryKey(_imageryMode.ToString(), job.Z, job.X, job.Y);
                    Texture2D imgTex = null;
                    yield return TakTileDiskCache.FetchTexture(imgUrl, imgKey, readable: false, 30,
                        t => imgTex = t);
                    if (imgTex != null && job.Go != null)
                        textured = ApplyImagery(job, imgTex);
                }

                // Google basemap unavailable → fall back to ESRI satellite (skip if
                // already on ESRI — primary URL already tried that template).
                if (!textured && job.Go != null && _imageryMode != ImageryMode.EsriWorldImagery)
                {
                    string esriUrl = EsriTemplate
                        .Replace("{z}", job.Z.ToString())
                        .Replace("{y}", job.Y.ToString())
                        .Replace("{x}", job.X.ToString());
                    string esriKey = TakTileDiskCache.ImageryKey("EsriWorldImagery", job.Z, job.X, job.Y);
                    Texture2D esriTex = null;
                    yield return TakTileDiskCache.FetchTexture(esriUrl, esriKey, readable: false, 30,
                        t => esriTex = t);
                    if (esriTex != null && job.Go != null)
                        textured = ApplyImagery(job, esriTex);
                    else
                        Debug.LogWarning($"[DemTerrainMap] imagery fail {job.Z}/{job.X}/{job.Y}");
                }

                if (job.Z > zoom && !textured)
                {
                    // Untextured detail would show a green patch above the base
                    // imagery — drop it and retry later.
                    _retryAt[job.Key] = Time.unscaledTime + DetailRetrySeconds;
                    RemoveTile(job.Key);
                    yield break;
                }

                if (_tiles.TryGetValue(job.Key, out var rec) && rec.Go == job.Go)
                    OnTileLoaded(rec, textured);
            }
            finally
            {
                _inflight--;
                if (isBase) _inflightBase--;
            }
        }

        /// <summary>
        /// Swap the tile onto a textured material, taking ownership of the
        /// texture (previous material/texture are destroyed) and un-hiding
        /// detail tiles that were invisible while waiting for imagery.
        /// </summary>
        bool ApplyImagery(TileJob job, Texture2D tex)
        {
            if (tex == null) return false;
            if (job.Go == null || !_tiles.TryGetValue(job.Key, out var rec) || rec.Go != job.Go)
            {
                Destroy(tex);
                return false;
            }
            var r = job.Go.GetComponent<Renderer>();
            var tint = new Color(_brightness, _brightness, _brightness, 1f);
            var mat = r != null ? MakeTerrainMaterial(tex, tint) : null;
            if (mat == null)
            {
                Destroy(tex);
                return false;
            }
            if (rec.Mat != null) Destroy(rec.Mat);
            if (rec.Tex != null && rec.Tex != tex) Destroy(rec.Tex);
            rec.Mat = mat;
            rec.Tex = tex;
            r.sharedMaterial = mat;
            r.enabled = true;
            return true;
        }

        static Material MakeTerrainMaterial(Texture tex, Color tint)
        {
            var mat = new Material(GetUnlit());
            if (mat == null) return null;
            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);
            mat.doubleSidedGI = true;
            return mat;
        }

        float BuildHeightMesh(TileJob job, Texture2D dem, int demShift)
        {
            TileBoundsToLatLon(job.X, job.Y, job.Z,
                out double north, out double west, out double south, out double east);

            var origin = new GeoMath.Geodetic(config.originLat, config.originLon, config.originAlt);
            int res = Mathf.Clamp(meshResolution, 4, 64);
            int n = res + 1;
            var verts = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            var tris = new int[res * res * 6];

            // When the DEM tile is coarser than this tile (z16 over z15 DEM),
            // sample only our sub-window of the parent DEM.
            int subMask = (1 << demShift) - 1;
            float subScale = 1f / (1 << demShift);
            float subU0 = (job.X & subMask) * subScale;
            float subV0 = (job.Y & subMask) * subScale;
            float lift = job.Z > zoom ? (job.Z - zoom) * DetailLiftPerLevel : 0f;

            float sum = 0f;
            int samples = 0;
            int demW = dem.width;
            int demH = dem.height;
            Color32[] pixels = dem.GetPixels32();

            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)res;
                float v = j / (float)res;
                double lon = west + (east - west) * u;
                double lat = north + (south - north) * v;

                float du = subU0 + u * subScale;
                float dv = subV0 + v * subScale;
                int px = Mathf.Clamp(Mathf.RoundToInt(du * (demW - 1)), 0, demW - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt((1f - dv) * (demH - 1)), 0, demH - 1);
                Color32 c = pixels[py * demW + px];
                float hae = DecodeTerrarium(c.r, c.g, c.b);
                double alt = config.originAlt + (hae - config.originAlt) * verticalExaggeration;

                var enu = GeoMath.GeodeticToEnu(new GeoMath.Geodetic(lat, lon, alt), origin);
                int idx = j * n + i;
                var p = GeoMath.EnuToUnity(enu);
                p.y += lift;
                verts[idx] = p;
                uvs[idx] = new Vector2(u, 1f - v);
                sum += hae;
                samples++;
            }

            int t = 0;
            for (int j = 0; j < res; j++)
            for (int i = 0; i < res; i++)
            {
                int i0 = j * n + i;
                int i1 = i0 + 1;
                int i2 = i0 + n;
                int i3 = i2 + 1;
                tris[t++] = i0; tris[t++] = i1; tris[t++] = i3;
                tris[t++] = i0; tris[t++] = i3; tris[t++] = i2;
            }

            var mesh = new Mesh { name = $"Dem_{job.Z}_{job.X}_{job.Y}" };
            mesh.indexFormat = verts.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var mf = job.Go.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = mesh;

            return samples > 0 ? sum / samples : (float)config.originAlt;
        }

        void BuildFlatFallback(TileJob job)
        {
            if (job.Go == null) return;
            TileBoundsToLatLon(job.X, job.Y, job.Z,
                out double north, out double west, out double south, out double east);
            var origin = new GeoMath.Geodetic(config.originLat, config.originLon, config.originAlt);
            var nw = GeoMath.EnuToUnity(GeoMath.GeodeticToEnu(new GeoMath.Geodetic(north, west, config.originAlt), origin));
            var ne = GeoMath.EnuToUnity(GeoMath.GeodeticToEnu(new GeoMath.Geodetic(north, east, config.originAlt), origin));
            var se = GeoMath.EnuToUnity(GeoMath.GeodeticToEnu(new GeoMath.Geodetic(south, east, config.originAlt), origin));
            var sw = GeoMath.EnuToUnity(GeoMath.GeodeticToEnu(new GeoMath.Geodetic(south, west, config.originAlt), origin));

            var mesh = new Mesh();
            mesh.vertices = new[] { nw, ne, se, sw };
            mesh.uv = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            job.Go.GetComponent<MeshFilter>().sharedMesh = mesh;
            StartCoroutine(LoadImageryOnly(job));
        }

        IEnumerator LoadImageryOnly(TileJob job)
        {
            string imgUrl = ImageryUrl(job.X, job.Y, job.Z);
            string imgKey = TakTileDiskCache.ImageryKey(_imageryMode.ToString(), job.Z, job.X, job.Y);
            Texture2D imgTex = null;
            yield return TakTileDiskCache.FetchTexture(imgUrl, imgKey, readable: false, 30, t => imgTex = t);
            if (imgTex != null && job.Go != null && ApplyImagery(job, imgTex))
            {
                MarkLoaded(job, textured: true);
                yield break;
            }

            if (_imageryMode == ImageryMode.EsriWorldImagery) yield break;

            string esriUrl = EsriTemplate
                .Replace("{z}", job.Z.ToString())
                .Replace("{y}", job.Y.ToString())
                .Replace("{x}", job.X.ToString());
            string esriKey = TakTileDiskCache.ImageryKey("EsriWorldImagery", job.Z, job.X, job.Y);
            Texture2D esriTex = null;
            yield return TakTileDiskCache.FetchTexture(esriUrl, esriKey, readable: false, 30, t => esriTex = t);
            if (esriTex != null && job.Go != null &&
                ApplyImagery(job, esriTex))
            {
                MarkLoaded(job, textured: true);
            }
        }

        void MarkLoaded(TileJob job, bool textured)
        {
            if (_tiles.TryGetValue(job.Key, out var rec) && rec.Go == job.Go)
                OnTileLoaded(rec, textured);
        }

        static float DecodeTerrarium(byte r, byte g, byte b) =>
            r * 256f + g + b / 256f - 32768f;

        static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

        static Material GetUnlit()
        {
            if (_unlitTemplate != null) return _unlitTemplate;
            var sh = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Texture")
                     ?? Shader.Find("Unlit/Color")
                     ?? Shader.Find("Sprites/Default")
                     ?? Shader.Find("UI/Default");
            _unlitTemplate = sh != null ? new Material(sh) : null;
            return _unlitTemplate;
        }

        static void LatLonToTile(double lat, double lon, int z, out int x, out int y)
        {
            double n = Math.Pow(2, z);
            x = (int)Math.Floor((lon + 180.0) / 360.0 * n);
            double latRad = lat * Math.PI / 180.0;
            y = (int)Math.Floor((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);
        }

        static void TileBoundsToLatLon(int x, int y, int z,
            out double north, out double west, out double south, out double east)
        {
            double n = Math.Pow(2, z);
            west = x / n * 360.0 - 180.0;
            east = (x + 1) / n * 360.0 - 180.0;
            north = TileYToLat(y, n);
            south = TileYToLat(y + 1, n);
        }

        static double TileYToLat(int y, double n)
        {
            double t = Math.PI - 2.0 * Math.PI * y / n;
            return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(t) - Math.Exp(-t)));
        }

        // ---------------------------------------------------- basemap switcher
        // Additive API for the Maps picker (XrBasemapPanel). Kept at file bottom
        // to minimize merge conflict with height-sampling work elsewhere in this class.

        /// <summary>
        /// Switch imagery template and rebuild tiles so the new basemap paints in.
        /// No-op when the mode is already active.
        /// </summary>
        public void SetImageryMode(ImageryMode mode)
        {
            if (_imageryMode == mode) return;
            _imageryMode = mode;
            Rebuild();
        }
    }
}
