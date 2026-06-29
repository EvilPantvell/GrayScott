using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GrayScott
{
    /// <summary>
    /// Реализация модели Грея–Скотта (P. Gray, K. Scott, 1984).
    ///
    /// Уравнения в частных производных:
    ///   ∂u/∂t = Du·∇²u − u·v²  + F·(1−u)       ...(1)
    ///   ∂v/∂t = Dv·∇²v + u·v²  − (F+k)·v        ...(2)
    ///
    /// Дискретный лапласиан (5-точечный шаблон, h = 1):
    ///   ∇²u(i,j) = u(i−1,j) + u(i+1,j) + u(i,j−1) + u(i,j+1) − 4·u(i,j)
    ///
    /// Метод: явная схема Эйлера, Δt = 1.0.
    /// Граничные условия: периодические (тор ℝ²/ℤ²).
    ///
    /// Устойчивость (критерий фон Неймана, 2D): Δt·Du ≤ 0.25.
    /// При Du = 0.16: 0.16 &lt; 0.25 ✓ (запас 36%).
    ///
    /// Эталонные параметры Пирсона (Science 261, 1993): Du=0.16, Dv=0.08.
    /// </summary>
    internal sealed class ReactionDiffusionEngine
    {
        // Публичные константы — MainForm использует их в ApplyPreset для сброса.
        public const float DefaultDu = 0.16f;
        public const float DefaultDv = 0.08f;

        public readonly int Width, Height;

        // Битовые маски для периодического wrap: & вместо %.
        // Требование: Width и Height обязаны быть степенями двойки.
        private readonly int _wMask, _hMask;

        // Двойная буферизация: _u/_v — текущий кадр, _un/_vn — следующий.
        private float[] _u, _v, _un, _vn;

        // LUT — uint, не int. С int: (int)0xFF000000 = -16777216 (отрицательное!),
        // (-16777216) >> 24 = -1 (знаковое расширение), (byte)(-1) = 255.
        // Результат случайно верный, но код требует пояснений — с uint всё очевидно.
        private readonly uint[] _colorLUT = new uint[1024];
        private ColorScheme _lutScheme = (ColorScheme)(-1); // «обязательно пересобрать»

        // Переиспользуемый буфер пикселей: не выделяем память каждый кадр.
        private byte[] _pixelBuf;

        public float Du { get; set; } = DefaultDu;
        public float Dv { get; set; } = DefaultDv;
        public float F { get; set; } = 0.028f;
        public float K { get; set; } = 0.053f;
        public float Dt { get; set; } = 1.00f;
        public long StepCount { get; private set; }

        private readonly Random _rng = new Random();

        public ReactionDiffusionEngine(int w, int h)
        {
            if (w <= 0 || h <= 0)
                throw new ArgumentOutOfRangeException($"Размеры ({w}×{h}) должны быть положительными.");
            if ((w & (w - 1)) != 0 || (h & (h - 1)) != 0)
                throw new ArgumentException(
                    $"Width ({w}) и Height ({h}) должны быть степенями двойки (требуется для битовых масок).");

            Width = w; Height = h;
            _wMask = w - 1; _hMask = h - 1;
            int n = w * h;
            _u = new float[n]; _v = new float[n];
            _un = new float[n]; _vn = new float[n];

            // Reset() намеренно НЕ вызывается здесь: он будет вызван из ApplyPreset
            // уже после установки нужных F/K/Du/Dv. Иначе получаем двойную
            // инициализацию: конструктор съедает часть RNG-состояния, и начальные
            // паттерны никогда не воспроизводятся в «чистом» виде.
        }

        /// <summary>
        /// Сбрасывает поля в состояние равновесия (u=1, v=0) и засевает
        /// 30 случайных «зародышей» реакции.
        /// </summary>
        public void Reset()
        {
            StepCount = 0;
            int n = Width * Height;
            for (int i = 0; i < n; i++) { _u[i] = 1f; _v[i] = 0f; }

            for (int s = 0; s < 30; s++)
            {
                int cx = _rng.Next(8, Width - 8);
                int cy = _rng.Next(8, Height - 8);
                int r = _rng.Next(3, 8);
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int x = cx + dx, y = cy + dy;
                        // uint-каст объединяет проверки x < 0 и x >= Width в одну.
                        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) continue;
                        int i = y * Width + x;
                        _u[i] = 0.50f + Noise();
                        _v[i] = 0.25f + Noise();
                    }
            }

            // Синхронизируем буферы «следующего кадра».
            // Без этого _un/_vn содержат нули (аллокация), и если
            // кто-то когда-либо добавит чтение из обоих буферов одновременно,
            // получит артефакт. Дешевле скопировать сейчас, чем искать потом.
            Array.Copy(_u, _un, n);
            Array.Copy(_v, _vn, n);
        }

        private float Noise() => (float)(_rng.NextDouble() * 0.10 - 0.05);

        /// <summary>
        /// Выполняет <paramref name="count"/> шагов интегрирования методом Эйлера.
        ///
        /// Потокобезопасность: метод НЕ потокобезопасен. Весь цикл симуляции должен
        /// выполняться на одном потоке (сейчас — UI-поток через WinForms Timer).
        /// При переносе в фоновый поток нужна синхронизация доступа к _u/_v/_un/_vn.
        /// </summary>
        public void Step(int count)
        {
            for (int s = 0; s < count; s++)
            {
                // Кэшируем свойства в локальных переменных — JIT видит их как
                // финальные значения и держит в регистрах, не перечитывая из памяти.
                float f = F, k = K, du = Du, dv = Dv, dt = Dt;
                float fk = f + k;

                // ── Внутренние клетки: индексная арифметика без взятия по модулю ──
                for (int y = 1; y < Height - 1; y++)
                {
                    int row = y * Width;
                    for (int x = 1; x < Width - 1; x++)
                    {
                        int i = row + x;
                        float u = _u[i], v = _v[i];
                        float uvv = u * v * v;  // автокаталитическая реакция U + 2V → 3V
                        float lu = _u[i - 1] + _u[i + 1]
                                  + _u[i - Width] + _u[i + Width] - 4f * u;
                        float lv = _v[i - 1] + _v[i + 1]
                                  + _v[i - Width] + _v[i + Width] - 4f * v;
                        _un[i] = Saturate(u + dt * (du * lu - uvv + f * (1f - u)));
                        _vn[i] = Saturate(v + dt * (dv * lv + uvv - fk * v));
                    }
                }

                // ── Граничные клетки: периодические условия (тор) ─────────────
                // Явные проходы верхней и нижней строк, затем левого и правого столбцов.
                for (int x = 0; x < Width; x++)
                {
                    ProcessBorderCell(x, 0, f, fk, du, dv, dt);
                    ProcessBorderCell(x, Height - 1, f, fk, du, dv, dt);
                }
                for (int y = 1; y < Height - 1; y++)
                {
                    ProcessBorderCell(0, y, f, fk, du, dv, dt);
                    ProcessBorderCell(Width - 1, y, f, fk, du, dv, dt);
                }

                // Обмен буферами: O(1) — переставляем ссылки, не копируем данные.
                var tu = _u; _u = _un; _un = tu;
                var tv = _v; _v = _vn; _vn = tv;
                StepCount++;
            }
        }

        private void ProcessBorderCell(int x, int y,
            float f, float fk, float du, float dv, float dt)
        {
            int i = y * Width + x;
            float u = _u[i], v = _v[i], uvv = u * v * v;

            // Битовые маски: работают при размерах-степенях двойки (гарантируется конструктором).
            int left = y * Width + ((x - 1) & _wMask);
            int right = y * Width + ((x + 1) & _wMask);
            int up = ((y - 1) & _hMask) * Width + x;
            int down = ((y + 1) & _hMask) * Width + x;

            float lu = _u[left] + _u[right] + _u[up] + _u[down] - 4f * u;
            float lv = _v[left] + _v[right] + _v[up] + _v[down] - 4f * v;

            _un[i] = Saturate(u + dt * (du * lu - uvv + f * (1f - u)));
            _vn[i] = Saturate(v + dt * (dv * lv + uvv - fk * v));
        }

        // Два условных перехода — без какой-либо магии «без ветвлений».
        // Зажим нужен: Perturb пишет шумовые значения напрямую в _u/_v минуя этот метод,
        // а у краёв параметрического пространства реакционные члены могут дать выброс.
        private static float Saturate(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);

        private void RebuildLutIfNeeded(ColorScheme scheme)
        {
            if (scheme == _lutScheme) return;
            _lutScheme = scheme;
            for (int i = 0; i < 1024; i++)
            {
                Color c = ColorMapper.Map(i / 1023f, scheme);
                // uint: сдвиг >> 24 логический (заполняет нулями), а не арифметический.
                // Байтовый порядок в LUT: A=бит24-31, R=16-23, G=8-15, B=0-7.
                _colorLUT[i] = (255u << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            }
        }

        /// <summary>
        /// Рендерит поле V → Bitmap через LockBits + Marshal.Copy.
        /// <paramref name="brightness"/> масштабирует рабочий диапазон V ≈ [0, 0.35] → [0, 1].
        /// </summary>
        public void RenderInto(Bitmap bmp, ColorScheme scheme, float brightness = 3.0f)
        {
            RebuildLutIfNeeded(scheme);

            BitmapData bd = bmp.LockBits(
                new Rectangle(0, 0, Width, Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                // Math.Abs: Stride отрицателен для bottom-up DIB.
                // new Bitmap(w, h, fmt) всегда top-down (Stride > 0), но защита не лишняя
                // и не стоит ничего в горячем пути.
                int stride = Math.Abs(bd.Stride);
                int bufLen = stride * Height;

                if (_pixelBuf == null || _pixelBuf.Length != bufLen)
                    _pixelBuf = new byte[bufLen];

                float scale = brightness * 1023f;

                for (int y = 0; y < Height; y++)
                {
                    int outRow = y * stride;
                    int simRow = y * Width;
                    for (int x = 0; x < Width; x++)
                    {
                        float v = _v[simRow + x];
                        // Perturb пишет напрямую в _u/_v, минуя Saturate в Step.
                        // Единственный путь, где v < 0 теоретически возможен.
                        if (v < 0f) v = 0f;

                        int lutIdx = (int)(v * scale);
                        // uint-каст объединяет > 1023 и < 0 в одну проверку.
                        if ((uint)lutIdx > 1023u) lutIdx = 1023;

                        uint argb = _colorLUT[lutIdx];
                        int dest = outRow + x * 4;
                        // Байтовый порядок Format32bppArgb в памяти: B G R A.
                        _pixelBuf[dest] = (byte)argb;         // B
                        _pixelBuf[dest + 1] = (byte)(argb >> 8);  // G
                        _pixelBuf[dest + 2] = (byte)(argb >> 16); // R
                        _pixelBuf[dest + 3] = (byte)(argb >> 24); // A = 255
                    }
                }
                Marshal.Copy(_pixelBuf, 0, bd.Scan0, bufLen);
            }
            finally
            {
                // finally — обязательно: без него второй LockBits бросит
                // InvalidOperationException("Bitmap region is already locked").
                bmp.UnlockBits(bd);
            }
        }

        /// <summary>Вносит круговое возмущение; используется для рисования мышью.</summary>
        public void Perturb(int cx, int cy, int radius = 10)
        {
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (dx * dx + dy * dy > radius * radius) continue;
                    int x = (cx + dx) & _wMask;
                    int y = (cy + dy) & _hMask;
                    int i = y * Width + x;
                    _u[i] = 0.50f + Noise();
                    _v[i] = 0.25f + Noise();
                }
        }
    }
}