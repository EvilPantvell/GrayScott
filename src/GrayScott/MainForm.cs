using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace GrayScott
{
    internal sealed class MainForm : Form
    {
        // ── Константы симуляции ───────────────────────────────────────────────
        private const int SimW = 256, SimH = 256;   // размер расчётной сетки
        private const int DispW = 512, DispH = 512;   // начальный размер отображаемой картинки
        private const int PanelW = 252;                // начальная ширина панели управления

        // Параметры NumericUpDown для Du (от 0.080 до 0.200, шаг 0.001)
        private const decimal DU_MIN = 0.080m;
        private const decimal DU_MAX = 0.200m;
        private const decimal DU_STEP = 0.001m;

        // Параметры для F (0.010 .. 0.060, шаг 0.001)
        private const decimal F_MIN = 0.010m;
        private const decimal F_MAX = 0.060m;
        private const decimal F_STEP = 0.001m;

        // Параметры для k (0.040 .. 0.070, шаг 0.001)
        private const decimal K_MIN = 0.040m;
        private const decimal K_MAX = 0.070m;
        private const decimal K_STEP = 0.001m;

        // ── Ядро ─────────────────────────────────────────────────────────────
        private readonly ReactionDiffusionEngine _engine;
        private readonly Bitmap _renderTarget;
        private readonly Timer _timer;
        private readonly Stopwatch _tickWatch = new Stopwatch();

        // ── Состояние ────────────────────────────────────────────────────────
        private bool _paused;
        private bool _suppressSliders;      // блокирует события при программном обновлении
        private bool _mouseDown;
        private int _stepsPerFrame = 8;
        private int _userStepsPerFrame = 8;   // пользовательское значение скорости

        private ColorScheme _colorScheme = ColorScheme.Plasma;

        // ── FPS ──────────────────────────────────────────────────────────────
        private int _frameCounter;
        private DateTime _lastFpsTime = DateTime.Now;

        // ── Контролы ─────────────────────────────────────────────────────────
        // Основной контейнер с подвижной границей
        private SplitContainer _splitContainer;
        private PictureBox _pic;
        private ComboBox _cbPreset, _cbColor;
        // NumericUpDown вместо TrackBar для компактности и точности
        private NumericUpDown _nudF, _nudK, _nudSpeed, _nudDu;
        private Label _lblF, _lblK, _lblSpeed, _lblDu, _lblStats;
        private Button _btnReset, _btnPause, _btnSave;

        // ── Полноэкранный режим ──────────────────────────────────────────────
        private bool _fullscreen;
        private FormBorderStyle _savedBorderStyle;
        private Rectangle _savedBounds;
        private bool _savedPanelVisible;

        // ─────────────────────────────────────────────────────────────────────
        public MainForm()
        {
            // Включаем масштабирование под текущий DPI, чтобы на HiDPI-экранах
            // не получать размытую картинку.
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);

            _engine = new ReactionDiffusionEngine(SimW, SimH);
            _renderTarget = new Bitmap(SimW, SimH, PixelFormat.Format32bppArgb);

            SuspendLayout();   // замораживаем перерисовку до окончания построения UI

            // ── Настройка формы ─────────────────────────────────────────────
            Text = "Морфогенез Грея–Скотта  ·  Реакционно-диффузионная система";
            // Разрешаем изменение размера и максимизацию — теперь окно можно тянуть.
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            BackColor = Color.FromArgb(14, 14, 21);
            DoubleBuffered = true;   // убирает мерцание при перерисовке
            // Стартовый размер окна: ширина = картинка + панель + зазоры, высота = макс. из них
            ClientSize = new Size(DispW + PanelW + 36, Math.Max(DispH, 600) + 20);

            // ── SplitContainer: подвижная граница между анимацией и панелью ──
            _splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,       // заполняет всё клиентское пространство формы
                Orientation = Orientation.Vertical,
                // Фиксируем панель управления? Нет, пусть обе панели растягиваются.
                FixedPanel = FixedPanel.None,
                // Начальное расстояние от левого края до разделителя: ширина картинки + небольшой отступ
                SplitterDistance = DispW + 10,
                // Минимальные размеры панелей, чтобы ничего не схлопнулось
                Panel1MinSize = 200,
                Panel2MinSize = 120
            };
            Controls.Add(_splitContainer);

            // Левая панель (Panel1) — область анимации
            BuildPictureBox();      // _pic помещается в Panel1
            // Правая панель (Panel2) — панель управления
            BuildSidebar();         // все контролы строятся на Panel2

            WireEvents();

            // Устанавливаем индекс после подписки на события, чтобы сработал
            // SelectedIndexChanged → ApplyPreset → Reset() с правильными параметрами.
            _cbPreset.SelectedIndex = 0;

            // Клавиатурные сокращения: F11 — fullscreen, Escape — выход из fullscreen.
            KeyPreview = true;
            KeyDown += HandleKeyDown;

            ResumeLayout(false);
            PerformLayout();

            _timer = new Timer { Interval = 16 }; // ~62 FPS
            _timer.Tick += OnTick;
            _timer.Start();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Обработчик клавиш
        // ─────────────────────────────────────────────────────────────────────
        private void HandleKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && _fullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Полноэкранный режим
        // ─────────────────────────────────────────────────────────────────────
        private void ToggleFullscreen()
        {
            if (_fullscreen)
            {
                // ── Возврат из fullscreen ────────────────────────────────────
                FormBorderStyle = _savedBorderStyle;
                WindowState = FormWindowState.Normal;
                Bounds = _savedBounds;

                // Показываем панель управления, если она была видна
                _splitContainer.Panel2Collapsed = !_savedPanelVisible;

                // Восстанавливаем SplitContainer (он снова заполнит форму)
                _splitContainer.Dock = DockStyle.Fill;
                // Убираем TopMost, если было
                TopMost = false;

                _fullscreen = false;
            }
            else
            {
                // ── Переход в fullscreen ─────────────────────────────────────
                _savedBorderStyle = FormBorderStyle;
                _savedBounds = Bounds;
                _savedPanelVisible = !_splitContainer.Panel2Collapsed; // видна ли сейчас панель

                // Скрываем панель управления — в fullscreen только анимация
                _splitContainer.Panel2Collapsed = true;

                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;

                // SplitContainer продолжает заполнять форму (Dock = Fill)
                // Левая панель (с картинкой) автоматически растянется.
                // TopMost можно не ставить, и так всё окно перекроет.

                _fullscreen = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PictureBox (левая панель SplitContainer)
        // ─────────────────────────────────────────────────────────────────────
        private void BuildPictureBox()
        {
            _pic = new PictureBox
            {
                // Заполняет всю левую панель
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Cursor = Cursors.Cross,
                // Размер будет определяться панелью, но зададим SizeMode для масштабирования
                SizeMode = PictureBoxSizeMode.Zoom   // сохраняет пропорции, вписывая в контрол
            };
            _pic.Paint += (s, e) => PicPaint(e);
            _pic.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) { _mouseDown = true; PerturbField(e.Location); }
            };
            _pic.MouseMove += (s, e) => { if (_mouseDown) PerturbField(e.Location); };
            _pic.MouseUp += (s, e) => _mouseDown = false;

            // Помещаем в левую панель SplitContainer
            _splitContainer.Panel1.Controls.Add(_pic);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Панель управления (правая панель SplitContainer)
        // ─────────────────────────────────────────────────────────────────────
        private void BuildSidebar()
        {
            // Панель, которая будет внутри правой части SplitContainer
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(19, 19, 31),
                AutoScroll = true,
                BorderStyle = BorderStyle.None
            };
            _splitContainer.Panel2.Controls.Add(panel);

            int y = 8;
            var fontBold = new Font("Segoe UI", 12f, FontStyle.Bold);
            var fontItalic = new Font("Segoe UI", 8f, FontStyle.Italic);
            var fontConsolas = new Font("Consolas", 8f);

            // Заголовок
            AddLabel(panel, "Система Грея–Скотта", ref y, fontBold,
                     Color.FromArgb(72, 180, 255), center: true);
            AddLabel(panel, "Морфогенез по Тьюрингу (1952)", ref y, fontItalic,
                     Color.FromArgb(105, 105, 150), center: true);
            y += 6;

            // Пресеты
            AddLabel(panel, "Пресет (сбрасывает симуляцию):", ref y, null,
                     Color.FromArgb(185, 185, 210));
            _cbPreset = CreateComboBox(panel, ref y);
            foreach (var p in SimulationPreset.All) _cbPreset.Items.Add(p);
            y += 4;

            // Feed rate (F) — теперь NumericUpDown
            _lblF = AddLabel(panel, $"F = {_engine.F:F4}  (feed rate)", ref y, null,
                             Color.FromArgb(255, 210, 90));
            _nudF = CreateNumericUpDown(panel, ref y, F_MIN, F_MAX, F_STEP, (decimal)_engine.F);
            y += 2;

            // Kill rate (k)
            _lblK = AddLabel(panel, $"k = {_engine.K:F4}  (kill rate)", ref y, null,
                             Color.FromArgb(255, 165, 90));
            _nudK = CreateNumericUpDown(panel, ref y, K_MIN, K_MAX, K_STEP, (decimal)_engine.K);
            y += 4;

            // Скорость симуляции
            _lblSpeed = AddLabel(panel, $"Скорость: {_stepsPerFrame} шаг/кадр", ref y, null,
                                 Color.FromArgb(160, 215, 160));
            _nudSpeed = CreateNumericUpDown(panel, ref y, 1, 16, 1, _stepsPerFrame);
            y += 2;

            // Коэффициенты диффузии
            _lblDu = AddLabel(panel,
                              $"Du = {_engine.Du:F3}  Dv = {_engine.Dv:F3}  (Du/Dv = 2)",
                              ref y, null, Color.FromArgb(150, 200, 255));
            _nudDu = CreateNumericUpDown(panel, ref y, DU_MIN, DU_MAX, DU_STEP, (decimal)_engine.Du);
            y += 4;

            // Цветовая схема
            AddLabel(panel, "Цветовая схема:", ref y, null, Color.FromArgb(185, 185, 210));
            _cbColor = CreateComboBox(panel, ref y);
            foreach (ColorScheme cs in Enum.GetValues(typeof(ColorScheme)))
                _cbColor.Items.Add(cs.ToString());
            _cbColor.SelectedIndex = 0;
            y += 6;

            // Кнопки
            _btnReset = CreateButton(panel, "Сброс", 5, y, 77);
            _btnPause = CreateButton(panel, "Пауза", 89, y, 77);
            _btnSave = CreateButton(panel, "PNG...", 173, y, 73);
            y += 36;

            // Статистика
            _lblStats = new Label
            {
                Location = new Point(5, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(90, 210, 110),
                Font = fontConsolas,
                Text = "Шаги: 0"
            };
            panel.Controls.Add(_lblStats);
            y += _lblStats.PreferredHeight + 4;

            // Подсказки
            AddLabel(panel, "ЛКМ по полю — добавить возмущение", ref y, fontItalic,
                     Color.FromArgb(85, 85, 125));
            AddLabel(panel, "F/k вручную — без сброса;  пресет — со сбросом.", ref y, fontItalic,
                     Color.FromArgb(75, 75, 115));
            y += 6;

            // Уравнения
            var eqLabel = new Label
            {
                Text = "∂u/∂t = Dᵤ·∇²u − u·v² + F·(1−u)\r\n"
                           + "∂v/∂t = Dᵥ·∇²v + u·v² − (F+k)·v\r\n"
                           + "\r\n"
                           + "∇²u = Σ₄соседей − 4u   [5-pt, h=1]",
                Location = new Point(5, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 160, 80),
                Font = fontConsolas
            };
            panel.Controls.Add(eqLabel);
            y += eqLabel.PreferredHeight + 8;

            // Ссылки
            AddLabel(panel, "Gray & Scott, Chem. Eng. Sci. (1984)", ref y, fontItalic,
                     Color.FromArgb(65, 65, 100));
            AddLabel(panel, "Pearson, Science 261 (1993)", ref y, fontItalic,
                     Color.FromArgb(65, 65, 100));
            AddLabel(panel, "Turing, Phil. Trans. R. Soc. (1952)", ref y, fontItalic,
                     Color.FromArgb(65, 65, 100));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Подписка на события
        // ─────────────────────────────────────────────────────────────────────
        private void WireEvents()
        {
            _cbPreset.SelectedIndexChanged += (s, e) =>
            {
                if (!_suppressSliders && _cbPreset.SelectedItem is SimulationPreset p)
                    ApplyPreset(p);
            };

            // Обработчики NumericUpDown
            _nudF.ValueChanged += (s, e) =>
            {
                if (_suppressSliders) return;
                _engine.F = (float)_nudF.Value;
                _lblF.Text = $"F = {_engine.F:F4}  (feed rate)";
            };
            _nudK.ValueChanged += (s, e) =>
            {
                if (_suppressSliders) return;
                _engine.K = (float)_nudK.Value;
                _lblK.Text = $"k = {_engine.K:F4}  (kill rate)";
            };
            _nudSpeed.ValueChanged += (s, e) =>
            {
                if (_suppressSliders) return;
                _stepsPerFrame = (int)_nudSpeed.Value;
                _userStepsPerFrame = _stepsPerFrame;
                _lblSpeed.Text = $"Скорость: {_stepsPerFrame} шаг/кадр";
            };
            _nudDu.ValueChanged += (s, e) =>
            {
                if (_suppressSliders) return;
                float newDu = (float)_nudDu.Value;
                _engine.Du = newDu;
                _engine.Dv = newDu * 0.5f;   // жёсткое отношение 2:1
                _lblDu.Text = $"Du = {_engine.Du:F3}  Dv = {_engine.Dv:F3}  (Du/Dv = 2)";
            };

            _cbColor.SelectedIndexChanged += (s, e) =>
            {
                if (Enum.TryParse(_cbColor.SelectedItem?.ToString(), out ColorScheme cs))
                    _colorScheme = cs;
            };

            _btnReset.Click += (s, e) => _engine.Reset();
            _btnPause.Click += (s, e) => TogglePause();
            _btnSave.Click += (s, e) => SaveSnapshot();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Применение пресета
        // ─────────────────────────────────────────────────────────────────────
        private void ApplyPreset(SimulationPreset p)
        {
            _suppressSliders = true;

            _engine.F = p.F;
            _engine.K = p.K;
            _engine.Du = ReactionDiffusionEngine.DefaultDu;
            _engine.Dv = ReactionDiffusionEngine.DefaultDv;

            // Устанавливаем NumericUpDown
            _nudF.Value = (decimal)p.F;
            _nudK.Value = (decimal)p.K;
            _nudDu.Value = (decimal)_engine.Du;

            _lblF.Text = $"F = {p.F:F4}  (feed rate)";
            _lblK.Text = $"k = {p.K:F4}  (kill rate)";
            _lblDu.Text = $"Du = {_engine.Du:F3}  Dv = {_engine.Dv:F3}  (Du/Dv = 2)";

            _suppressSliders = false;
            _engine.Reset();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Главный цикл (~60 FPS)
        // ─────────────────────────────────────────────────────────────────────
        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (!_paused)
                {
                    _tickWatch.Restart();
                    _engine.Step(_stepsPerFrame);
                    _tickWatch.Stop();

                    long stepMs = _tickWatch.ElapsedMilliseconds;
                    long tooSlowMs = (long)(_timer.Interval * 0.80);
                    long easyMs = (long)(_timer.Interval * 0.25);

                    // Автокоррекция скорости
                    if (stepMs > tooSlowMs && _stepsPerFrame > 1)
                        SetSpeed(Math.Max(1, _stepsPerFrame - 1), isAuto: true);
                    else if (stepMs < easyMs && _stepsPerFrame < _userStepsPerFrame)
                        SetSpeed(Math.Min(_userStepsPerFrame, _stepsPerFrame + 1), isAuto: true);

                    _frameCounter++;
                }

                _engine.RenderInto(_renderTarget, _colorScheme);
                _pic.Invalidate();

                double elapsed = (DateTime.Now - _lastFpsTime).TotalSeconds;
                if (elapsed >= 0.8)
                {
                    _lblStats.Text = _paused
                        ? $"Шаги: {_engine.StepCount:N0}   [ПАУЗА]"
                        : $"Шаги: {_engine.StepCount:N0}   FPS: {_frameCounter / elapsed:F0}";
                    _frameCounter = 0;
                    _lastFpsTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                _timer.Stop();
                MessageBox.Show(
                    $"Критическая ошибка:\n{ex.Message}",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetSpeed(int steps, bool isAuto)
        {
            _stepsPerFrame = steps;
            _suppressSliders = true;
            _nudSpeed.Value = steps;
            _suppressSliders = false;
            _lblSpeed.Text = isAuto
                ? $"Скорость: {steps} шаг/кадр (авт.)"
                : $"Скорость: {steps} шаг/кадр";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Отрисовка PictureBox
        // ─────────────────────────────────────────────────────────────────────
        private void PicPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode =
                System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            // Рисуем на весь контрол с сохранением пропорций, но Zoom уже масштабирует.
            // Так как _pic.SizeMode = Zoom, DrawImage в событии Paint не обязателен,
            // однако оставим для прямого контроля (можно и убрать, но не страшно).
            e.Graphics.DrawImage(_renderTarget, 0, 0, _pic.Width, _pic.Height);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Возмущение от мыши
        // ─────────────────────────────────────────────────────────────────────
        private void PerturbField(Point mousePos)
        {
            int sx = mousePos.X * SimW / _pic.Width;
            int sy = mousePos.Y * SimH / _pic.Height;
            _engine.Perturb(sx, sy, 12);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Пауза / продолжение
        // ─────────────────────────────────────────────────────────────────────
        private void TogglePause()
        {
            _paused = !_paused;
            _btnPause.Text = _paused ? "Продолжить" : "Пауза";
            _btnPause.BackColor = _paused
                ? Color.FromArgb(55, 22, 22)
                : Color.FromArgb(32, 32, 52);
            _frameCounter = 0;
            _lastFpsTime = DateTime.Now;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Сохранение снимка
        // ─────────────────────────────────────────────────────────────────────
        private void SaveSnapshot()
        {
            _timer.Stop();
            try
            {
                using (var dlg = new SaveFileDialog
                {
                    Title = "Сохранить снимок",
                    Filter = "PNG Image (*.png)|*.png",
                    FileName = $"GrayScott_step{_engine.StepCount}.png"
                })
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    using (var bmp = new Bitmap(DispW, DispH, PixelFormat.Format32bppArgb))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode =
                            System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(_renderTarget, 0, 0, DispW, DispH);
                        bmp.Save(dlg.FileName, ImageFormat.Png);
                    }
                }
            }
            finally
            {
                if (!_paused) _timer.Start();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  UI-хелперы
        // ─────────────────────────────────────────────────────────────────────
        private static Label AddLabel(Control parent, string text, ref int y,
            Font font, Color color, bool center = false)
        {
            var lbl = new Label
            {
                Text = text,
                Location = new Point(5, y),
                AutoSize = true,
                ForeColor = color,
                Font = font ?? new Font("Segoe UI", 8.5f),
                TextAlign = center
                    ? ContentAlignment.MiddleCenter
                    : ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lbl);
            y += lbl.PreferredHeight + 2;
            return lbl;
        }

        // Новый хелпер для NumericUpDown
        private static NumericUpDown CreateNumericUpDown(Control parent, ref int y,
            decimal min, decimal max, decimal step, decimal initial)
        {
            var nud = new NumericUpDown
            {
                Location = new Point(5, y),
                Size = new Size(90, 22),
                Minimum = min,
                Maximum = max,
                Increment = step,
                DecimalPlaces = (step < 1m) ? 4 : 0,  // если шаг дробный, показываем 4 знака
                Value = initial,
                BackColor = Color.FromArgb(28, 28, 46),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            parent.Controls.Add(nud);
            y += nud.Height + 4;
            return nud;
        }

        private static ComboBox CreateComboBox(Control parent, ref int y)
        {
            var cb = new ComboBox
            {
                Location = new Point(5, y),
                Size = new Size(242, 22),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(28, 28, 46),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            parent.Controls.Add(cb);
            y += cb.Height + 6;
            return cb;
        }

        private static Button CreateButton(Control parent, string text, int x, int y, int w)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(32, 32, 52),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 105);
            parent.Controls.Add(btn);
            return btn;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Закрытие формы и освобождение ресурсов
        // ─────────────────────────────────────────────────────────────────────
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                _renderTarget?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
