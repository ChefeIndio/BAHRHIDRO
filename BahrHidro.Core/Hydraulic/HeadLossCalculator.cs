
using System;

namespace BahrHidro.Core.Hydraulic
{
    public static class HeadLossCalculator
    {
        /// <summary>
        /// Calcula a perda de carga distribuída
        /// pelo método de Darcy-Weisbach.
        ///
        /// Comprimento: metros
        /// Diâmetro interno: milímetros
        /// Velocidade: metros por segundo
        /// Fator de atrito: adimensional
        /// Resultado: metros de coluna de água
        /// </summary>
        public static double CalculateDarcyWeisbach(
            double lengthMeters,
            double internalDiameterMm,
            double velocityMetersPerSecond,
            double frictionFactor)
        {
            if (!double.IsFinite(lengthMeters) || lengthMeters < 0)
                throw new ArgumentException(
                    "O comprimento deve ser positivo ou zero.");

            if (!double.IsFinite(internalDiameterMm) ||
                internalDiameterMm <= 0)
                throw new ArgumentException(
                    "O diâmetro interno deve ser positivo.");

            if (!double.IsFinite(velocityMetersPerSecond) ||
                velocityMetersPerSecond < 0)
                throw new ArgumentException(
                    "A velocidade deve ser positiva ou zero.");

            if (!double.IsFinite(frictionFactor) ||
                frictionFactor <= 0)
                throw new ArgumentException(
                    "O fator de atrito deve ser positivo.");

            double diameterMeters = internalDiameterMm / 1000.0;

            double headLoss =
                frictionFactor *
                (lengthMeters / diameterMeters) *
                (Math.Pow(velocityMetersPerSecond, 2) / (2.0 * 9.80665));

            return headLoss;
        }
    }
}
