using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlayNext.UI
{
    /// <summary>Accent color helpers: parsing, picking a color from game art, and readable text on top of it.</summary>
    public static class AccentColors
    {
        public static readonly Color Fallback = Color.FromRgb(0xE6, 0x00, 0x23);

        public static bool TryParse(string hex, out Color color)
        {
            color = Fallback;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            var s = hex.Trim().TrimStart('#');
            if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>Black or white, whichever reads better on <paramref name="c"/>.</summary>
        public static Color OnColor(Color c)
        {
            double L(byte ch) { var v = ch / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            var lum = 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
            return lum > 0.42 ? Color.FromRgb(0x10, 0x12, 0x18) : Colors.White;
        }

        public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        /// <summary>A light tint of the color, for text and icons on the dark background.</summary>
        public static Color Light(Color c)
        {
            ToHsv(c, out var h, out var s, out var v);
            return FromHsv(h, Math.Min(s, 0.45), Math.Max(v, 0.97));
        }

        /// <summary>
        /// The most prominent vivid color in an image (hue buckets weighted by saturation and brightness),
        /// tuned to work as a button color. Null if the art is basically gray.
        /// </summary>
        public static Color? FromImage(BitmapSource image)
        {
            if (image == null) return null;
            try
            {
                BitmapSource src = image;
                if (src.PixelWidth > 64)
                {
                    var scale = 64.0 / src.PixelWidth;
                    src = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                }
                var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                bgra.CopyPixels(px, stride, 0);

                const int buckets = 24;
                var weight = new double[buckets];
                var sumH = new double[buckets];
                var sumS = new double[buckets];
                var sumV = new double[buckets];
                for (int i = 0; i < px.Length; i += 4)
                {
                    var c = Color.FromRgb(px[i + 2], px[i + 1], px[i]);
                    ToHsv(c, out var hue, out var sat, out var val);
                    if (sat < 0.25 || val < 0.22 || val > 0.98) continue;
                    var wgt = sat * sat * val;
                    var b = (int)(hue / 360.0 * buckets) % buckets;
                    weight[b] += wgt; sumH[b] += hue * wgt; sumS[b] += sat * wgt; sumV[b] += val * wgt;
                }
                int best = -1;
                double bestW = 0;
                for (int b = 0; b < buckets; b++)
                {
                    // Include neighbours so a hue split across two buckets still wins.
                    var wsum = weight[b] + 0.5 * (weight[(b + 1) % buckets] + weight[(b + buckets - 1) % buckets]);
                    if (wsum > bestW) { bestW = wsum; best = b; }
                }
                if (best < 0 || weight[best] <= 0 || bestW < (w * h) * 0.01) return null;
                var hh = sumH[best] / weight[best];
                var ss = sumS[best] / weight[best];
                var vv = sumV[best] / weight[best];
                // Make it a confident UI color: saturated, bright but not neon.
                return FromHsv(hh, Clamp(ss, 0.55, 0.85), Clamp(vv, 0.72, 0.92));
            }
            catch
            {
                return null;
            }
        }

        private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

        public static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) { h = 0; return; }
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        public static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}
