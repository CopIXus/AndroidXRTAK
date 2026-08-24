using System.Globalization;
using System.Text;

namespace TakXr.Cot
{
    /// <summary>
    /// NormalizedCot → CoT XML for publishing to the TAK server (port of the
    /// backend cotBuilder). Emits contact, remarks, drawing vertices as
    /// &lt;link point/&gt;, ellipse shape, ATAK ARGB colors and &lt;archive/&gt;.
    /// </summary>
    public static class CotXmlBuilder
    {
        public static string Build(NormalizedCot cot)
        {
            var d = cot.detail;
            var sb = new StringBuilder(512);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<event version=\"2.0\" uid=\"").Append(Esc(cot.uid))
              .Append("\" type=\"").Append(Esc(cot.type))
              .Append("\" how=\"").Append(Esc(string.IsNullOrEmpty(cot.how) ? "h-g-i-g-o" : cot.how))
              .Append("\" time=\"").Append(Esc(cot.time))
              .Append("\" start=\"").Append(Esc(string.IsNullOrEmpty(cot.start) ? cot.time : cot.start))
              .Append("\" stale=\"").Append(Esc(cot.stale)).Append("\">");

            sb.Append("<point lat=\"").Append(F(cot.point.lat))
              .Append("\" lon=\"").Append(F(cot.point.lon))
              .Append("\" hae=\"").Append(F(cot.point.hae))
              .Append("\" ce=\"").Append(F(cot.point.ce))
              .Append("\" le=\"").Append(F(cot.point.le)).Append("\"/>");

            sb.Append("<detail>");
            if (!string.IsNullOrEmpty(cot.contact?.callsign))
            {
                sb.Append("<contact callsign=\"").Append(Esc(cot.contact.callsign));
                if (!string.IsNullOrEmpty(cot.contact.endpoint))
                    sb.Append("\" endpoint=\"").Append(Esc(cot.contact.endpoint));
                sb.Append("\"/>");
            }
            if (!string.IsNullOrEmpty(d?.team?.name))
            {
                sb.Append("<__group name=\"").Append(Esc(d.team.name))
                  .Append("\" role=\"").Append(Esc(string.IsNullOrEmpty(d.team.role) ? "Team Member" : d.team.role))
                  .Append("\"/>");
            }
            if (!string.IsNullOrEmpty(d?.remarks))
                sb.Append("<remarks>").Append(Esc(d.remarks)).Append("</remarks>");

            // TAK client/platform tag (<takv platform="VRTAK-XR" .../>) — lets
            // other clients identify VR observers.
            if (!string.IsNullOrEmpty(d?.takv?.platform))
            {
                sb.Append("<takv platform=\"").Append(Esc(d.takv.platform));
                if (!string.IsNullOrEmpty(d.takv.version))
                    sb.Append("\" version=\"").Append(Esc(d.takv.version));
                if (!string.IsNullOrEmpty(d.takv.device))
                    sb.Append("\" device=\"").Append(Esc(d.takv.device));
                if (!string.IsNullOrEmpty(d.takv.os))
                    sb.Append("\" os=\"").Append(Esc(d.takv.os));
                sb.Append("\"/>");
            }

            // TAK sensor detail — ATAK/CloudTAK draw an FOV cone from azimuth/fov/range.
            if (d?.sensor != null && (d.sensor.fov > 0f || d.sensor.range > 0f))
            {
                sb.Append("<sensor azimuth=\"").Append(F(d.sensor.azimuth))
                  .Append("\" fov=\"").Append(F(d.sensor.fov))
                  .Append("\" range=\"").Append(F(d.sensor.range))
                  .Append("\" elevation=\"").Append(F(d.sensor.elevation)).Append("\"/>");
            }

            // Custom icon (e.g. Generic Icons man.png) — ATAK honors this over the
            // default 2525/affiliation glyph when present.
            if (!string.IsNullOrEmpty(d?.userIcon?.iconsetpath))
                sb.Append("<usericon iconsetpath=\"").Append(Esc(d.userIcon.iconsetpath)).Append("\"/>");

            bool hasShape = d?.shapePoints != null && d.shapePoints.Count >= 2;
            bool hasEllipse = d?.ellipse != null && d.ellipse.major > 0f;

            if (hasShape)
            {
                foreach (var p in d.shapePoints)
                    sb.Append("<link point=\"").Append(F(p.lat)).Append(',')
                      .Append(F(p.lon)).Append(',').Append(F(p.hae)).Append("\"/>");
            }
            if (hasEllipse)
            {
                sb.Append("<shape><ellipse major=\"").Append(F(d.ellipse.major))
                  .Append("\" minor=\"").Append(F(d.ellipse.minor))
                  .Append("\" angle=\"").Append(F(d.ellipse.angle)).Append("\"/></shape>");
            }
            if (!string.IsNullOrEmpty(d?.strokeColor))
            {
                sb.Append("<strokeColor value=\"").Append(CssToArgbInt(d.strokeColor, 0xFF)).Append("\"/>");
                sb.Append("<strokeWeight value=\"3.0\"/>");
            }
            if (!string.IsNullOrEmpty(d?.fillColor))
                sb.Append("<fillColor value=\"").Append(CssToArgbInt(d.fillColor, 0x66)).Append("\"/>");
            // Persist drawings across ATAK restarts, like ATAK's own drawing tools.
            if (hasShape || hasEllipse)
                sb.Append("<archive/>");
            sb.Append("</detail></event>");
            return sb.ToString();
        }

        /// <summary>'#rrggbb' → ATAK's signed 32-bit ARGB int.</summary>
        public static int CssToArgbInt(string css, int alpha)
        {
            int rgb = 0xFFFFFF;
            if (!string.IsNullOrEmpty(css))
            {
                var hex = css.TrimStart('#');
                if (hex.Length == 6 &&
                    int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                    rgb = v;
            }
            return ((alpha & 0xFF) << 24) | rgb;
        }

        /// <summary>ATAK signed 32-bit ARGB int → '#rrggbb' (alpha dropped).</summary>
        public static string ArgbIntToCss(int argb)
        {
            return "#" +
                   ((argb >> 16) & 0xFF).ToString("X2") +
                   ((argb >> 8) & 0xFF).ToString("X2") +
                   (argb & 0xFF).ToString("X2");
        }

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
