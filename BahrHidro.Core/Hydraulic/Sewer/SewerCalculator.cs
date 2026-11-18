using System;

namespace BahrHidro.Core.Hydraulic.Sewer
{
    public sealed record SewerSizingResult(
        int TotalUhc,
        int RequiredDiameterMm,
        double MinimumSlopePercentage,
        string ComponentType // Ramal de Descarga, Ramal de Esgoto ou Subcoletor
    );

    public static class SewerCalculator
    {
        public static SewerSizingResult CalculateBranch(int totalUhc, int minimumFixtureDiameter)
        {
            if (totalUhc <= 0) throw new ArgumentException("A UHC total deve ser maior que zero.");

            int diameter = 40;
            double slope = 2.0; // 2% é padrão para diâmetros pequenos (até 75mm)

            // Lógica baseada nas tabelas da NBR 8160 para Ramais de Esgoto
            if (totalUhc <= 3) diameter = 40;
            else if (totalUhc <= 6) diameter = 50;
            else if (totalUhc <= 20) diameter = 75;
            else if (totalUhc <= 160) { diameter = 100; slope = 1.0; } // 1% para diâmetros >= 100mm
            else if (totalUhc <= 620) { diameter = 150; slope = 0.5; }
            else { diameter = 200; slope = 0.5; }

            // O tubo nunca pode ser menor que a saída da maior peça ligada a ele
            if (diameter < minimumFixtureDiameter)
            {
                diameter = minimumFixtureDiameter;
                if (diameter >= 100) slope = 1.0;
            }

            return new SewerSizingResult(totalUhc, diameter, slope, "Ramal de Esgoto");
        }
    }
}