using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TakXr.Cot
{
    /// <summary>
    /// On-device CloudTAK iconset resolver (port of packages/backend iconResolver.ts).
    /// Loads iconset.xml + PNGs from StreamingAssets/map-icons — no LXC required.
    /// </summary>
    public static class IconResolver
    {
        const string DefaultUid = "34ae1613-9645-4222-a9d2-e5f243dea2865";
        const string GenericUid = "ad78aafb-83a6-4c07-b2b9-a897a8b6a38f";
        const string PublicSafetyAirUid = "66f14976-4b62-4023-8edb-d8d2ebeaa336";

        static readonly string[] RequiredDirs =
        {
            "Default", "Generic Icons", "Public Safety Air",
        };

        class Iconset
        {
            public string Uid;
            public string DirName;
            public string RootDir;
            public string DefaultGroup;
            public string DefaultFriendly;
            public string DefaultHostile;
            public string DefaultNeutral;
            public string DefaultUnknown;
            public readonly Dictionary<string, string> FileByBase =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<(string name, string type2525b, string group)> Icons =
                new List<(string, string, string)>();
        }

        class TypeHit
        {
            public string IconsetUid;
            public string RelPath;
            public string IconName;
            public string Type2525b;
        }

        static readonly Dictionary<string, Iconset> ByUid =
            new Dictionary<string, Iconset>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<TypeHit>> TypesByPrefix =
            new Dictionary<string, List<TypeHit>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Texture2D> TexCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;
        static string _dataRoot;

        public static bool IsReady => _loaded && ByUid.Count > 0;

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _dataRoot = Path.Combine(Application.streamingAssetsPath, "map-icons");
            // Desktop/Editor: StreamingAssets is a real folder.
            // Android jar: paths must be pre-copied at build; File.Exists fails on jar —
            // we still try persistent extract dir first.
            string persist = Path.Combine(Application.persistentDataPath, "map-icons");
            if (Directory.Exists(persist))
                _dataRoot = persist;
            else if (!Directory.Exists(_dataRoot))
            {
                Debug.LogWarning("[IconResolver] no map-icons at " + _dataRoot);
                return;
            }

            foreach (var dir in RequiredDirs)
                TryLoadIconset(dir);
            // Also load any other dirs present (extracted from packages).
            try
            {
                foreach (var d in Directory.GetDirectories(_dataRoot))
                {
                    var name = Path.GetFileName(d);
                    if (Array.IndexOf(RequiredDirs, name) >= 0) continue;
                    TryLoadIconset(name);
                }
            }
            catch { /* ignore */ }

            Debug.Log($"[IconResolver] loaded {ByUid.Count} iconsets from {_dataRoot}");
        }

        /// <summary>Register an extracted iconset folder under persistent map-icons.</summary>
        public static void RegisterExtractedDir(string absDir)
        {
            if (string.IsNullOrEmpty(absDir) || !Directory.Exists(absDir)) return;
            string persistRoot = Path.Combine(Application.persistentDataPath, "map-icons");
            Directory.CreateDirectory(persistRoot);
            string name = Path.GetFileName(absDir.TrimEnd(Path.DirectorySeparatorChar, '/'));
            string dest = Path.Combine(persistRoot, name);
            try
            {
                if (!Directory.Exists(dest))
                    CopyDir(absDir, dest);
                _dataRoot = persistRoot;
                _loaded = false;
                ByUid.Clear();
                TypesByPrefix.Clear();
                EnsureLoaded();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[IconResolver] extract register: " + ex.Message);
            }
        }

        static void CopyDir(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
        }

        static void TryLoadIconset(string dirName)
        {
            string root = Path.Combine(_dataRoot, dirName);
            string xmlPath = Path.Combine(root, "iconset.xml");
            if (!File.Exists(xmlPath)) return;
            string xml;
            try { xml = File.ReadAllText(xmlPath); }
            catch { return; }

            var header = Regex.Match(xml, @"<iconset[^>]*>", RegexOptions.IgnoreCase);
            if (!header.Success) return;
            string tag = header.Value;
            string uid = Attr(tag, "uid");
            if (string.IsNullOrEmpty(uid)) return;

            var set = new Iconset
            {
                Uid = uid,
                DirName = dirName,
                RootDir = root,
                DefaultGroup = Attr(tag, "defaultGroup"),
                DefaultFriendly = Attr(tag, "defaultFriendly"),
                DefaultHostile = Attr(tag, "defaultHostile"),
                DefaultNeutral = Attr(tag, "defaultNeutral"),
                DefaultUnknown = Attr(tag, "defaultUnknown"),
            };

            foreach (Match m in Regex.Matches(xml, @"<icon\s+([^>]+?)\/?>", RegexOptions.IgnoreCase))
            {
                string name = Attr(m.Groups[1].Value, "name");
                if (string.IsNullOrEmpty(name)) continue;
                set.Icons.Add((name, Attr(m.Groups[1].Value, "type2525b"), Attr(m.Groups[1].Value, "group")));
            }

            IndexPngs(set);
            ByUid[uid] = set;
            foreach (var icon in set.Icons)
            {
                string rel = ResolveRel(set, icon.name, icon.group);
                if (rel == null || string.IsNullOrEmpty(icon.type2525b)) continue;
                string key = icon.type2525b.ToLowerInvariant();
                if (!TypesByPrefix.TryGetValue(key, out var list))
                {
                    list = new List<TypeHit>();
                    TypesByPrefix[key] = list;
                }
                list.Add(new TypeHit
                {
                    IconsetUid = uid,
                    RelPath = rel,
                    IconName = icon.name,
                    Type2525b = key,
                });
            }
        }

        static void IndexPngs(Iconset set)
        {
            try
            {
                foreach (var f in Directory.GetFiles(set.RootDir, "*.png", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(set.RootDir.Length)
                        .TrimStart(Path.DirectorySeparatorChar, '/', '\\')
                        .Replace('\\', '/');
                    string baseName = Path.GetFileName(rel);
                    if (!set.FileByBase.ContainsKey(baseName))
                        set.FileByBase[baseName] = rel;
                }
            }
            catch { /* ignore */ }
        }

        static string ResolveRel(Iconset set, string iconName, string groupHint)
        {
            if (string.IsNullOrEmpty(iconName)) return null;
            string baseName = iconName;
            if (!baseName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                baseName += ".png";

            if (!string.IsNullOrEmpty(groupHint))
            {
                string cand = (groupHint.TrimEnd('/') + "/" + baseName).Replace('\\', '/');
                if (File.Exists(Path.Combine(set.RootDir, cand))) return cand;
            }
            if (set.FileByBase.TryGetValue(baseName, out var indexed)) return indexed;
            if (!string.IsNullOrEmpty(set.DefaultGroup))
            {
                string cand = (set.DefaultGroup.TrimEnd('/') + "/" + baseName).Replace('\\', '/');
                if (File.Exists(Path.Combine(set.RootDir, cand))) return cand;
            }
            return null;
        }

        /// <summary>
        /// Full resolution including the Default-iconset AFFILIATION fallback.
        /// NOTE: once iconsets are loaded this practically never returns null —
        /// callers deciding WHETHER a CoT should use an iconset icon must use
        /// ResolveExplicitTexture / HasExplicitIcon instead (see CotClassifier),
        /// otherwise the fallback swallows the procedural dot/glyph branches.
        /// </summary>
        public static Texture2D ResolveTexture(NormalizedCot cot) =>
            Resolve(cot, allowAffiliationDefault: true);

        /// <summary>
        /// Explicit matches only: usericon iconsetpath or a type2525b index hit.
        /// Returns null when the CoT has no real icon (no affiliation default).
        /// </summary>
        public static Texture2D ResolveExplicitTexture(NormalizedCot cot) =>
            Resolve(cot, allowAffiliationDefault: false);

        /// <summary>True when an explicit (non-default) icon resolves for this CoT.</summary>
        public static bool HasExplicitIcon(NormalizedCot cot) =>
            ResolveExplicitTexture(cot) != null;

        static Texture2D Resolve(NormalizedCot cot, bool allowAffiliationDefault)
        {
            EnsureLoaded();
            if (!IsReady || cot == null) return null;

            string type = cot.type ?? "";
            string iconsetpath = cot.detail?.userIcon?.iconsetpath;

            // Direct iconsetpath: UID/rel/path.png
            if (!string.IsNullOrEmpty(iconsetpath))
            {
                var parsed = ParseIconsetPath(iconsetpath);
                if (parsed.mode == "path" && ByUid.TryGetValue(parsed.uid, out var set))
                {
                    var tex = LoadTex(set, parsed.rel);
                    if (tex != null) return tex;
                }
                if (parsed.mode == "type" && !string.IsNullOrEmpty(parsed.cotType))
                    type = parsed.cotType;
            }

            // type2525b match
            var hit = FindBestTypeMatch(type);
            if (hit != null && ByUid.TryGetValue(hit.IconsetUid, out var hitSet))
            {
                var tex = LoadTex(hitSet, hit.RelPath);
                if (tex != null) return tex;
            }

            if (!allowAffiliationDefault) return null;

            // Affiliation default from Default iconset
            if (ByUid.TryGetValue(DefaultUid, out var def))
            {
                string name = AffiliationFromType(cot.type ?? "") switch
                {
                    "hostile" => def.DefaultHostile,
                    "neutral" => def.DefaultNeutral,
                    "unknown" => def.DefaultUnknown,
                    _ => def.DefaultFriendly,
                };
                string rel = ResolveRel(def, name, def.DefaultGroup);
                var tex = LoadTex(def, rel);
                if (tex != null) return tex;
            }
            return null;
        }

        static TypeHit FindBestTypeMatch(string cotType)
        {
            string t = (cotType ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(t)) return null;
            if (TypesByPrefix.TryGetValue(t, out var exact) && exact.Count > 0)
                return PreferDomain(exact, t);

            TypeHit best = null;
            string bestPrefix = null;
            foreach (var kv in TypesByPrefix)
            {
                if (!t.StartsWith(kv.Key, StringComparison.Ordinal)) continue;
                if (bestPrefix != null && kv.Key.Length < bestPrefix.Length) continue;
                var pick = PreferDomain(kv.Value, t);
                if (pick == null) continue;
                bestPrefix = kv.Key;
                best = pick;
            }
            return best;
        }

        static TypeHit PreferDomain(List<TypeHit> list, string cotType)
        {
            if (list == null || list.Count == 0) return null;
            bool air = CotDomain(cotType) == "air";
            string prefer = air ? PublicSafetyAirUid : GenericUid;
            foreach (var h in list)
                if (h.IconsetUid == prefer) return h;
            foreach (var h in list)
                if (h.IconsetUid == DefaultUid) return h;
            return list[0];
        }

        static string CotDomain(string cotType)
        {
            var parts = (cotType ?? "").ToLowerInvariant().Split('-');
            if (parts.Length >= 3 && parts[2] == "a") return "air";
            if (parts.Length >= 3 && parts[2] == "g") return "ground";
            return "other";
        }

        public static string AffiliationFromType(string cotType)
        {
            var parts = (cotType ?? "").Split('-');
            if (parts.Length < 2) return "unknown";
            switch (parts[1])
            {
                case "f":
                case "F":
                    return "friendly";
                case "h":
                case "H":
                    return "hostile";
                case "n":
                case "N":
                    return "neutral";
                default:
                    return "unknown";
            }
        }

        struct ParsedPath
        {
            public string mode; // path | type
            public string uid;
            public string rel;
            public string cotType;
        }

        static ParsedPath ParseIconsetPath(string raw)
        {
            raw = (raw ?? "").Trim();
            if (raw.StartsWith("COT_MAPPING_2525B/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = raw.Split('/');
                return new ParsedPath
                {
                    mode = "type",
                    cotType = parts.Length > 1 ? string.Join("-", parts, 1, parts.Length - 1) : "",
                };
            }
            int slash = raw.IndexOf('/');
            if (slash <= 0) return default;
            return new ParsedPath
            {
                mode = "path",
                uid = raw.Substring(0, slash),
                rel = raw.Substring(slash + 1).Replace('\\', '/'),
            };
        }

        static Texture2D LoadTex(Iconset set, string rel)
        {
            if (set == null || string.IsNullOrEmpty(rel)) return null;
            string key = set.Uid + ":" + rel;
            if (TexCache.TryGetValue(key, out var cached) && cached != null) return cached;
            string abs = Path.Combine(set.RootDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs)) return null;
            try
            {
                var bytes = File.ReadAllBytes(abs);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes)) return null;
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                TexCache[key] = tex;
                return tex;
            }
            catch
            {
                return null;
            }
        }

        static string Attr(string tag, string name)
        {
            var m = Regex.Match(tag, name + "=\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
