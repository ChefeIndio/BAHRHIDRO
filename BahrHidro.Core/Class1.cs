
namespace BahrHidro.Core
{
    public class PipeSizing
    {
        /// <summary>
        /// Calcula o diâmetro interno mínimo necessário
        /// com base na vazão e velocidade limite.
        /// </summary>
        public static double CalculateMinimumDiameter(
            double flowLitersPerSecond,
            double maximumVelocity)
        {
            if (flowLitersPerSecond <= 0)
                throw new ArgumentException(
                    "A vazão deve ser maior que zero.");

            if (maximumVelocity <= 0)
                throw new ArgumentException(
                    "A velocidade deve ser maior que zero.");

            // Converter vazão de L/s para m³/s
            double flow = flowLitersPerSecond / 1000.0;

            // Equação da continuidade:
            // Q = A * v
            // D = sqrt(4Q / (pi * v))

            double diameterMeters = Math.Sqrt(
                (4.0 * flow) /
                (Math.PI * maximumVelocity));

            // Converter metros para milímetros
            return diameterMeters * 1000.0;
        }
    }
}
