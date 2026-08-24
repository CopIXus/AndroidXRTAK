using System;
using System.Collections;
using System.Collections.Generic;
using TakXr.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace TakXr.Map
{
    /// <summary>
    /// Satellite map via ESRI World Imagery XYZ tiles downloaded with UnityWebRequest.
    /// Bypasses Cesium native curl (TLS failures on Galaxy XR) — same imagery source as web lite XR.
    /// </summary>
    public class EsriTileMap : MonoBehaviour
    {
        const string Template =
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";

        [SerializeField] AppConfig config;
        [SerializeField] int zoom = 13;
        [SerializeField] int halfTiles = 8; // (2*half+1)^2 tiles around origin
        [SerializeField] int maxConcurrent = 6;

        readonly Dictionary<string, GameObject> _tiles = new Dictionary<string, GameObject>();
        readonly Queue<TileJob> _queue = new Queue<TileJob>();
        int _inflight;
        bool _built;
        static Material _unlit;

        struct TileJob
        {
            public int X, Y, Z;
            public GameObject Go;
        }

        public int LoadedCount { get; private set; }
        public bool IsReady => LoadedCount > 0;

        public void Configure(AppConfig cfg)
        {
            config = cfg;
            if (!_built) BuildGrid();
        }

        public void Clear()
        {
            foreach (var kv in _tiles)
                if (kv.Value != null) Destroy(kv.Value);
            _tiles.Clear();
            _queue.Clear();
            LoadedCount = 0;
            _built = false;
            _inflight = 0;
        }

        public void Rebuild()
        {
            Clear();
            BuildGrid();
        }

        void BuildGrid()
        {
            if (config == null) return;
            _built = true;
            LatLonToTile(config.originLat, config.originLon, zoom, out int cx, out int cy);

            for (int dy = -halfTiles; dy <= halfTiles; dy++)
            for (int dx = -halfTiles; dx <= halfTiles; dx++)
            {
                int tx = cx + dx;
                int ty = cy + dy;
                if (tx < 0 || ty < 0) continue;
                string key = zoom + "/" + tx + "/" + ty;
                if (_tiles.ContainsKey(key)) continue;

                var go = CreateTileQuad(tx, ty, zoom);
                go.name = "Esri:" + key;
                go.transform.SetParent(transform, false);
                _tiles[key] = go;
                _queue.Enqueue(new TileJob { X = tx, Y = ty, Z = zoom, Go = go });
            }

            Debug.Log($"[EsriTileMap] queued {_queue.Count} tiles z={zoom} origin={config.originLat:F4},{config.originLon:F4}");
        }

        void Update()
        {
            while (_inflight < maxConcurrent && _queue.Count > 0)
            {
                var job = _queue.Dequeue();
                StartCoroutine(LoadTile(job));
            }
        }

        IEnumerator LoadTile(TileJob job)
        {
            _inflight++;
            string url = Template
                .Replace("{z}", job.Z.ToString())
                .Replace("{y}", job.Y.ToString())
                .Replace("{x}", job.X.ToString());

            using var req = UnityWebRequestTexture.GetTexture(url, nonReadable: true);
            req.timeout = 25;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success && job.Go != null)
            {
                var tex = DownloadHandlerTexture.GetContent(req);
                var r = job.Go.GetComponent<Renderer>();
                if (r != null && tex != null)
                {
                    var mat = new Material(GetUnlit());
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                    if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
                    r.sharedMaterial = mat;
                    LoadedCount++;
                }
            }
            else
            {
                Debug.LogWarning($"[EsriTileMap] tile fail {job.Z}/{job.X}/{job.Y}: {req.error}");
            }

            _inflight--;
        }

        GameObject CreateTileQuad(int x, int y, int z)
        {
            TileBoundsToLatLon(x, y, z, out double north, out double west, out double south, out double east);
            var origin = new GeoMath.Geodetic(config.originLat, config.originLon, config.originAlt);

            // ENU corners (NW, NE, SE, SW) — Unity: +X east, +Y up, +Z north
            var nw = GeoMath.EnuToUnity(GeoMath.GeodeticToEnu(new GeoMath.Geodetic(north, west, config.originAlt), origin));
            var ne = GeoMath.EnuToUnity(GeoMath.GeodeticToEnu(new GeoMath.Geodetic(north, east, config.originAlt), origin));
            var se = GeoMath.EnuToUnity(GeoMath.GeodeticToEnu(new GeoMath.Geodetic(south, east, config.originAlt), origin));
            var sw = GeoMath.EnuToUnity(GeoMath.GeodeticToEnu(new GeoMath.Geodetic(south, west, config.originAlt), origin));

            // Ground plane (CoT markers sit ~12 m above).
            nw.y = ne.y = se.y = sw.y = 0f;

            var go = new GameObject();
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(GetUnlit());
            if (mr.sharedMaterial.HasProperty("_BaseColor"))
                mr.sharedMaterial.SetColor("_BaseColor", new Color(0.15f, 0.2f, 0.18f, 1f));

            var mesh = new Mesh { name = "EsriTile" };
            mesh.vertices = new[] { nw, ne, se, sw };
            mesh.uv = new[]
            {
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(1, 0), new Vector2(0, 0)
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            return go;
        }

        static Material GetUnlit()
        {
            if (_unlit != null) return _unlit;
            var sh = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Texture")
                     ?? Shader.Find("Unlit/Color")
                     ?? Shader.Find("Sprites/Default")
                     ?? Shader.Find("UI/Default");
            _unlit = sh != null ? new Material(sh) : null;
            return _unlit;
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
    }
}
