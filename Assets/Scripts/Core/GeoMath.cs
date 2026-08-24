using UnityEngine;

namespace TakXr.Core
{
    /// <summary>
    /// WGS84 geodetic / ECEF / ENU math (port of packages/shared/src/enu.ts).
    /// Unity ENU mapping used by this app: +X east, +Y up, +Z north.
    /// </summary>
    public static class GeoMath
    {
        public const double Wgs84A = 6378137.0;
        public const double Wgs84F = 1.0 / 298.257223563;
        public static readonly double Wgs84E2 = Wgs84F * (2.0 - Wgs84F);

        public const float UnknownHaeSentinel = 9999999f;
        public const float HighAltitudeCeilingM = 482803f; // 300 miles

        public struct Geodetic
        {
            public double Lat;
            public double Lon;
            public double Alt;

            public Geodetic(double lat, double lon, double alt)
            {
                Lat = lat;
                Lon = lon;
                Alt = alt;
            }
        }

        public struct Ecef
        {
            public double X, Y, Z;
        }

        public struct Enu
        {
            public double East, North, Up;

            public float HorizontalDistance =>
                Mathf.Sqrt((float)(East * East + North * North));
        }

        public static Ecef GeodeticToEcef(Geodetic g)
        {
            double lat = g.Lat * Mathf.Deg2Rad;
            double lon = g.Lon * Mathf.Deg2Rad;
            double sinLat = System.Math.Sin(lat);
            double cosLat = System.Math.Cos(lat);
            double sinLon = System.Math.Sin(lon);
            double cosLon = System.Math.Cos(lon);
            double n = Wgs84A / System.Math.Sqrt(1.0 - Wgs84E2 * sinLat * sinLat);
            return new Ecef
            {
                X = (n + g.Alt) * cosLat * cosLon,
                Y = (n + g.Alt) * cosLat * sinLon,
                Z = (n * (1.0 - Wgs84E2) + g.Alt) * sinLat
            };
        }

        public static Enu GeodeticToEnu(Geodetic target, Geodetic origin)
        {
            var t = GeodeticToEcef(target);
            var o = GeodeticToEcef(origin);
            double dx = t.X - o.X;
            double dy = t.Y - o.Y;
            double dz = t.Z - o.Z;

            double lat = origin.Lat * Mathf.Deg2Rad;
            double lon = origin.Lon * Mathf.Deg2Rad;
            double sinLat = System.Math.Sin(lat);
            double cosLat = System.Math.Cos(lat);
            double sinLon = System.Math.Sin(lon);
            double cosLon = System.Math.Cos(lon);

            return new Enu
            {
                East = -sinLon * dx + cosLon * dy,
                North = -sinLat * cosLon * dx - sinLat * sinLon * dy + cosLat * dz,
                Up = cosLat * cosLon * dx + cosLat * sinLon * dy + sinLat * dz
            };
        }

        /// <summary>Unity world position: +X east, +Y up, +Z north.</summary>
        public static Vector3 EnuToUnity(Enu enu) =>
            new Vector3((float)enu.East, (float)enu.Up, (float)enu.North);

        public static float ResolveRenderHae(float hae, bool allowHighAltitude, out bool clampToGround)
        {
            if (!float.IsFinite(hae) || hae == 0f || hae >= UnknownHaeSentinel)
            {
                clampToGround = true;
                return 0f;
            }

            clampToGround = false;
            if (!allowHighAltitude && hae > HighAltitudeCeilingM)
                return HighAltitudeCeilingM;
            return hae;
        }
    }
}
