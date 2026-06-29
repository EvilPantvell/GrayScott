using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GrayScott
{
    /// <summary>
    /// Точки пространства параметров (F, k) по классификации Пирсона
    /// (Pearson, Science 261, 1993).
    /// Откалиброваны для Du = 0.16, Dv = 0.08, dt = 1.0.
    /// При изменении Du/Dv через слайдер паттерны могут визуально отличаться.
    /// </summary>
    internal struct SimulationPreset
    {
        public readonly string Name;
        public readonly float F;
        public readonly float K;

        public SimulationPreset(string name, float f, float k)
        {
            Name = name;
            F = f;
            K = k;
        }

        public override string ToString() => Name;

        // IReadOnlyList<T> защищает элементы: в отличие от массива,
        // снаружи нельзя сделать All[i] = new SimulationPreset(...).
        public static IReadOnlyList<SimulationPreset> All { get; } =
            new SimulationPreset[]
            {
                new SimulationPreset("β — Делящиеся пятна (митоз)", 0.028f, 0.053f),

                // γ: F=0.040 (предыдущая версия) был чуть выше центра γ-области.
                // 0.037 точнее попадает в характерный «коралловый» лабиринт Пирсона.
                new SimulationPreset("γ — Лабиринт (коралл)",       0.037f, 0.060f),

                new SimulationPreset("ε — Пузыри (обратные пятна)", 0.022f, 0.059f),

                // θ: предыдущее значение F=0.025, k=0.060 попадало в α/λ-область.
                // Истинный θ (пульсирующие кольца) требует малого F.
                // Паттерн формируется медленнее — нужно ~500+ шагов до появления колец.
                new SimulationPreset("θ — Пульсары (кольца)",       0.014f, 0.054f),

                new SimulationPreset("α — Стабильные точки",        0.029f, 0.057f),
                new SimulationPreset("κ — Хаотичные структуры",     0.048f, 0.063f),
                new SimulationPreset("λ — Λ-волны",                 0.037f, 0.065f),
            };
    }
}