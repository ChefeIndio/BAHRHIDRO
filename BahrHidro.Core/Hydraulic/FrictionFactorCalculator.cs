using System;

namespace BahrHidro.Core.Hydraulic
{
    public static class FrictionFactorCalculator
    {
        /// <summary>
        /// Calcula o fator de atrito de Darcy-Weisbach.
        ///
        /// Reynolds: adimensional
        /// Diâmetro interno: mm
        /// Rugosidade absoluta: mm
        ///
        /// - Regime laminar (Re &lt; 2300): f = 64 / Re
        /// - Regime de transição (2300 &lt;= Re &lt;= 4000): interpolação linear entre f(2300) e f(4000)
        /// - Regime turbulento (Re &gt; 4000): equação explícita de Swamee-Jain
        /// </summary>
        public static double Calculate(
            double reynoldsNumber,
            double internalDiameterMm,
            double absoluteRoughnessMm)
        {
            if (!double.IsFinite(reynoldsNumber) || reynoldsNumber <= 0)
                throw new ArgumentException("O número de Reynolds deve ser positivo.");

            if (!double.IsFinite(internalDiameterMm) || internalDiameterMm <= 0)
                throw new ArgumentException("O diâmetro interno deve ser positivo.");

            if (!double.IsFinite(absoluteRoughnessMm) || absoluteRoughnessMm < 0)
                throw new ArgumentException("A rugosidade não pode ser negativa.");

            // REGIME LAMINAR
            if (reynoldsNumber < 2300.0)
            {
                return 64.0 / reynoldsNumber;
            }

            double relativeRoughness = absoluteRoughnessMm / internalDiameterMm;

            // REGIME DE TRANSIÇÃO (Interpolação contínua entre Re = 2300 e Re = 4000)
            if (reynoldsNumber <= 4000.0)
            {
                double fLaminar2300 = 64.0 / 2300.0;
                double fTurbulent4000 = CalculateSwameeJain(4000.0, relativeRoughness);
                double t = (reynoldsNumber - 2300.0) / (4000.0 - 2300.0);
                return fLaminar2300 + t * (fTurbulent4000 - fLaminar2300);
            }

            // REGIME TURBULENTO
            return CalculateSwameeJain(reynoldsNumber, relativeRoughness);
        }

        private static double CalculateSwameeJain(double reynoldsNumber, double relativeRoughness)
        {
            double term =
                (relativeRoughness / 3.7) +
                (5.74 / Math.Pow(reynoldsNumber, 0.9));

            double logarithm = Math.Log10(term);
            double frictionFactor = 0.25 / (logarithm * logarithm);

            if (!double.IsFinite(frictionFactor) || frictionFactor <= 0)
                throw new InvalidOperationException("Falha numérica ao calcular o fator de atrito.");

            return frictionFactor;
        }
    }
}
