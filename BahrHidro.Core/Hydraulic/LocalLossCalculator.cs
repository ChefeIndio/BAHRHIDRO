
using System;

namespace BahrHidro.Core.Hydraulic
{
    public static class LocalLossCalculator
    {
        private const double Gravity = 9.80665;

        /// <summary>
        /// Calcula a perda de carga localizada de um acessório.
        ///
        /// K: coeficiente de perda localizada (adimensional)
        /// Velocidade: m/s
        ///
        /// Resultado: metros de coluna de água.
        /// </summary>
        public static double Calculate(
            double lossCoefficientK,
            double velocityMetersPerSecond)
        {
            if (!double.IsFinite(lossCoefficientK) ||
                lossCoefficientK < 0)
                throw new ArgumentException(
                    "O coeficiente K deve ser positivo ou zero.");

            if (!double.IsFinite(velocityMetersPerSecond) ||
                velocityMetersPerSecond < 0)
                throw new ArgumentException(
                    "A velocidade deve ser positiva ou zero.");

            return lossCoefficientK *
                   Math.Pow(velocityMetersPerSecond, 2) /
                   (2.0 * Gravity);
        }

        /// <summary>
        /// Calcula a perda localizada total de vários acessórios
        /// submetidos à mesma velocidade de referência.
        /// </summary>
        public static double CalculateTotal(
            double totalLossCoefficientK,
            double velocityMetersPerSecond)
        {
            return Calculate(
                totalLossCoefficientK,
                velocityMetersPerSecond);
        }
    }
}
