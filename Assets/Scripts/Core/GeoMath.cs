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

            public Enu(double east, double north, double up)
            {
                East = east;
                North = north;
                Up = up;
            }

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

        /// <summary>Inverse of <see cref="GeodeticToEnu"/>.</summary>
        public static Geodetic EnuToGeodetic(Enu enu, Geodetic origin)
        {
            double lat = origin.Lat * Mathf.Deg2Rad;
            double lon = origin.Lon * Mathf.Deg2Rad;
            double sinLat = System.Math.Sin(lat);
            double cosLat = System.Math.Cos(lat);
            double sinLon = System.Math.Sin(lon);
            double cosLon = System.Math.Cos(lon);
            double e = enu.East;
            double n = enu.North;
            double u = enu.Up;
            double dx = -sinLon * e - sinLat * cosLon * n + cosLat * cosLon * u;
            double dy = cosLon * e - sinLat * sinLon * n + cosLat * sinLon * u;
            double dz = cosLat * n + sinLat * u;
            var o = GeodeticToEcef(origin);
            return EcefToGeodetic(new Ecef { X = o.X + dx, Y = o.Y + dy, Z = o.Z + dz });
        }

        public static Geodetic EcefToGeodetic(Ecef ecef)
        {
            double a = Wgs84A;
            double e2 = Wgs84E2;
            double b = a * System.Math.Sqrt(1.0 - e2);
            double ep2 = (a * a - b * b) / (b * b);
            double p = System.Math.Sqrt(ecef.X * ecef.X + ecef.Y * ecef.Y);
            double th = System.Math.Atan2(a * ecef.Z, b * p);
            double lon = System.Math.Atan2(ecef.Y, ecef.X);
            double lat = System.Math.Atan2(
                ecef.Z + ep2 * b * System.Math.Pow(System.Math.Sin(th), 3),
                p - e2 * a * System.Math.Pow(System.Math.Cos(th), 3));
            double sinLat = System.Math.Sin(lat);
            double n = a / System.Math.Sqrt(1.0 - e2 * sinLat * sinLat);
            double alt = p / System.Math.Cos(lat) - n;
            return new Geodetic(lat * Mathf.Rad2Deg, lon * Mathf.Rad2Deg, alt);
        }

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
