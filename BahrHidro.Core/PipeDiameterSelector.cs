
using System;
using System.Collections.Generic;
using System.Linq;

namespace BahrHidro.Core
{
    public sealed record PipeDiameterOption(
        string Description,
        double InternalDiameterMm);

    public static class PipeDiameterSelector
    {
        /// <summary>
        /// Seleciona o menor diâmetro interno disponível
        /// que atende ao diâmetro mínimo calculado.
        /// </summary>
        public static PipeDiameterOption? Select(
            double minimumDiameterMm,
            IEnumerable<PipeDiameterOption> catalog)
        {
            if (!double.IsFinite(minimumDiameterMm) ||
                minimumDiameterMm <= 0)
            {
                throw new ArgumentException(
                    "O diâmetro mínimo deve ser positivo e finito.");
            }

            ArgumentNullException.ThrowIfNull(catalog);

            var options = catalog.ToList();

            if (options.Count == 0)
                throw new ArgumentException(
                    "O catálogo está vazio.");

            if (options.Any(x =>
                x == null ||
                string.IsNullOrWhiteSpace(x.Description) ||
                !double.IsFinite(x.InternalDiameterMm) ||
                x.InternalDiameterMm <= 0))
            {
                throw new ArgumentException(
                    "O catálogo contém dados inválidos.");
            }

            return options
                .OrderBy(x => x.InternalDiameterMm)
                .FirstOrDefault(
                    x => x.InternalDiameterMm >= minimumDiameterMm);
        }

        /// <summary>
        /// Seleciona automaticamente o diâmetro comercial
        /// a partir da vazão e da velocidade máxima adotada.
        /// </summary>
        public static PipeDiameterOption? SelectByFlow(
            double flowLitersPerSecond,
            IEnumerable<PipeDiameterOption> catalog,
            double maximumVelocityMetersPerSecond = 2.0)
        {
            if (!double.IsFinite(flowLitersPerSecond) ||
                flowLitersPerSecond <= 0)
            {
                throw new ArgumentException(
                    "A vazão deve ser positiva e finita.");
            }

            if (!double.IsFinite(maximumVelocityMetersPerSecond) ||
                maximumVelocityMetersPerSecond <= 0)
            {
                throw new ArgumentException(
                    "A velocidade máxima deve ser positiva e finita.");
            }

            ArgumentNullException.ThrowIfNull(catalog);

            // Conversão de litros por segundo para metros cúbicos por segundo.
            double flowM3s = flowLitersPerSecond / 1000.0;

            // Equação da continuidade:
            // Q = A * V
            // D = raiz quadrada de (4 * Q) / (pi * V)

            double minimumDiameterMeters = Math.Sqrt(
                (4.0 * flowM3s) /
                (Math.PI * maximumVelocityMetersPerSecond));

            double minimumDiameterMm =
                minimumDiameterMeters * 1000.0;

            return Select(minimumDiameterMm, catalog);
        }

        /// <summary>
        /// Calcula a velocidade real da água no tubo escolhido.
        /// </summary>
        public static double CalculateActualVelocity(
            double flowLitersPerSecond,
            double internalDiameterMm)
        {
            if (!double.IsFinite(flowLitersPerSecond) ||
                flowLitersPerSecond <= 0)
            {
                throw new ArgumentException(
                    "A vazão deve ser positiva e finita.");
            }

            if (!double.IsFinite(internalDiameterMm) ||
                internalDiameterMm <= 0)
            {
                throw new ArgumentException(
                    "O diâmetro interno deve ser positivo e finito.");
            }

            double flowM3s = flowLitersPerSecond / 1000.0;
            double diameterMeters = internalDiameterMm / 1000.0;

            double area = Math.PI *
                          Math.Pow(diameterMeters, 2) / 4.0;

            return flowM3s / area;
        }
    }
}
