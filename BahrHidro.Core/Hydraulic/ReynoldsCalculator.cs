
using System;

namespace BahrHidro.Core.Hydraulic
{
    public enum FlowRegime
    {
        Laminar,
        Transitional,
        Turbulent
    }

    public sealed record ReynoldsResult(
        double ReynoldsNumber,
        FlowRegime Regime);

    public static class ReynoldsCalculator
    {
        /// <summary>
        /// Calcula o número de Reynolds e classifica
        /// o regime de escoamento em tubulações.
        ///
        /// Velocidade: m/s
        /// Diâmetro interno: mm
        /// Viscosidade cinemática: m²/s
        /// </summary>
        public static ReynoldsResult Calculate(
            double velocityMetersPerSecond,
            double internalDiameterMm,
            double kinematicViscosityM2s)
        {
            if (!double.IsFinite(velocityMetersPerSecond) ||
                velocityMetersPerSecond < 0)
                throw new ArgumentException(
                    "A velocidade deve ser positiva ou zero.");

            if (!double.IsFinite(internalDiameterMm) ||
                internalDiameterMm <= 0)
                throw new ArgumentException(
                    "O diâmetro interno deve ser positivo.");

            if (!double.IsFinite(kinematicViscosityM2s) ||
                kinematicViscosityM2s <= 0)
                throw new ArgumentException(
                    "A viscosidade cinemática deve ser positiva.");

            double diameterMeters =
                internalDiameterMm / 1000.0;

            double reynolds =
                velocityMetersPerSecond *
                diameterMeters /
                kinematicViscosityM2s;

            FlowRegime regime;

            if (reynolds < 2300)
            {
                regime = FlowRegime.Laminar;
            }
            else if (reynolds <= 4000)
            {
                regime = FlowRegime.Transitional;
            }
            else
            {
                regime = FlowRegime.Turbulent;
            }

            return new ReynoldsResult(
                reynolds,
                regime);
        }
    }
}
