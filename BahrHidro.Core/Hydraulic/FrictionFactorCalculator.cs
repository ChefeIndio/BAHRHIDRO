
using System;

namespace BahrHidro.Core.Hydraulic
{
    public static class FrictionFactorCalculator
    {
        /// <summary>
        /// Calcula o fator de atrito de Darcy.
        ///
        /// Reynolds: adimensional
        /// Diâmetro interno: mm
        /// Rugosidade absoluta: mm
        ///
        /// Para regime laminar utiliza f = 64/Re.
        /// Para regime turbulento utiliza Swamee-Jain.
        ///
        /// O regime de transição não é calculado.
        /// </summary>
        public static double Calculate(
            double reynoldsNumber,
            double internalDiameterMm,
            double absoluteRoughnessMm)
        {
            if (!double.IsFinite(reynoldsNumber) ||
                reynoldsNumber <= 0)
                throw new ArgumentException(
                    "O número de Reynolds deve ser positivo.");

            if (!double.IsFinite(internalDiameterMm) ||
                internalDiameterMm <= 0)
                throw new ArgumentException(
                    "O diâmetro interno deve ser positivo.");

            if (!double.IsFinite(absoluteRoughnessMm) ||
                absoluteRoughnessMm < 0)
                throw new ArgumentException(
                    "A rugosidade não pode ser negativa.");

            // REGIME LAMINAR
            if (reynoldsNumber < 2300)
            {
                return 64.0 / reynoldsNumber;
            }

            // REGIME DE TRANSIÇÃO
            if (reynoldsNumber <= 4000)
            {
                throw new InvalidOperationException(
                    "O escoamento está na região de transição. " +
                    "O fator de atrito não será estimado automaticamente.");
            }

            // REGIME TURBULENTO
            double relativeRoughness =
                absoluteRoughnessMm / internalDiameterMm;

            // Equação explícita de Swamee-Jain
            double term =
                (relativeRoughness / 3.7) +
                (5.74 / Math.Pow(reynoldsNumber, 0.9));

            double logarithm = Math.Log10(term);

            double frictionFactor =
                0.25 / Math.Pow(logarithm, 2);

            if (!double.IsFinite(frictionFactor) ||
                frictionFactor <= 0)
            {
                throw new InvalidOperationException(
                    "Não foi possível calcular o fator de atrito.");
            }

            return frictionFactor;
        }
    }
}
