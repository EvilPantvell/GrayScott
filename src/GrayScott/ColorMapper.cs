using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Drawing;

namespace GrayScott
{
    internal enum ColorScheme { Plasma, Viridis, Thermal, Ocean, Neon, Grayscale }

    /// <summary>Отображает нормализованную концентрацию V ∈ [0, 1] в цвет RGB.</summary>
    internal static class ColorMapper
    {
        private static readonly Color[] _plasma =
        {
            C(13,8,135), C(126,3,167), C(204,71,120), C(248,149,64), C(240,249,33)
        };
        private static readonly Color[] _viridis =
        {
            C(68,1,84), C(59,82,139), C(33,145,140), C(94,201,98), C(253,231,37)
        };
        private static readonly Color[] _thermal =
        {
            C(0,0,0), C(120,0,0), C(200,80,0), C(255,200,0), C(255,255,255)
        };
        private static readonly Color[] _ocean =
        {
            C(0,0,50), C(0,60,150), C(0,160,220), C(80,220,255), C(255,255,255)
        };
        private static readonly Color[] _neon =
        {
            C(0,0,20), C(30,0,100), C(120,0,200), C(0,200,200), C(200,255,50)
        };

        public static Color Map(float t, ColorScheme scheme)
        {
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;

            switch (scheme)
            {
                case ColorScheme.Plasma: return Gradient(t, _plasma);
                case ColorScheme.Viridis: return Gradient(t, _viridis);
                case ColorScheme.Thermal: return Gradient(t, _thermal);
                case ColorScheme.Ocean: return Gradient(t, _ocean);
                case ColorScheme.Neon: return Gradient(t, _neon);
                case ColorScheme.Grayscale:
                    // +0.5f: округление вместо усечения (было без него).
                    { byte g = (byte)(t * 255f + 0.5f); return Color.FromArgb(g, g, g); }
                default: return Color.Black;
            }
        }

        private static Color Gradient(float t, Color[] stops)
        {
            float pos = t * (stops.Length - 1);
            int lo = (int)pos;
            if (lo >= stops.Length - 1) return stops[stops.Length - 1];
            float frac = pos - lo;
            Color c0 = stops[lo], c1 = stops[lo + 1];
            // +0.5f перед усечением — правильное округление вместо floor.
            // Без этого каждый канал систематически занижен на ~0.5 LSB:
            // незаметно на глаз, но некорректно и удивит на code review.
            return Color.FromArgb(
                (int)(c0.R + (c1.R - c0.R) * frac + 0.5f),
                (int)(c0.G + (c1.G - c0.G) * frac + 0.5f),
                (int)(c0.B + (c1.B - c0.B) * frac + 0.5f));
        }

        private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);
    }
}
