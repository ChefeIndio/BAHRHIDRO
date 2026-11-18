using System;
using System.Collections.Generic;

namespace BahrHidro.Core.Hydraulic.Rainwater
{
    public enum GutterShape
    {
        Rectangular,
        Semicircular
    }

    public enum GutterMaterial
    {
        PvcOrMetal,             // n = 0,011 (PVC, alumínio, zinco, cobre, fibrocimento)
        CastIronOrSmoothConcrete, // n = 0,012 (Ferro fundido, concreto alisado)
        CeramicOrRoughConcrete, // n = 0,013 (Cerâmica, concreto não-alisado)
        UnplasteredBrick        // n = 0,015 (Alvenaria sem revestimento)
    }

    public enum DownspoutExitType
    {
        SharpEdge, // Saída em aresta viva
        Funnel     // Saída com funil
    }

    public class RainwaterInput
    {
        public string RegionName { get; set; } = "Ponta Grossa - PR";
        public double IntensityMmPerHour { get; set; } = 148.0;
        public RainwaterReturnPeriod ReturnPeriod { get; set; } = RainwaterReturnPeriod.FiveYears;

        // Geometria da cobertura
        public double RoofWidthMeters { get; set; } = 10.0;        // Comprimento da calha (b)
        public double RoofProjectionMeters { get; set; } = 6.0;    // Projeção horizontal da água (a)
        public double RoofHeightMeters { get; set; } = 1.5;        // Desnível vertical da cumeeira ao beiral (h)
        public double InterceptingWallAreaM2 { get; set; } = 0.0;  // Parede vertical que deságua no telhado

        // Configuração da calha
        public GutterShape Shape { get; set; } = GutterShape.Rectangular;
        public GutterMaterial Material { get; set; } = GutterMaterial.PvcOrMetal;
        public double GutterSlopePercent { get; set; } = 0.5;      // Declividade (%) - mínimo NBR 10844: 0,5%
        public double ManualWidthMm { get; set; } = 0.0;           // 0 = dimensionamento automático
        public double ManualHeightMm { get; set; } = 0.0;          // 0 = dimensionamento automático

        // Configuração dos condutores verticais
        public int DownspoutCount { get; set; } = 1;
        public double DownspoutHeightMeters { get; set; } = 3.0;
        public double WaterDepthAtExitMm { get; set; } = 50.0;
        public DownspoutExitType ExitType { get; set; } = DownspoutExitType.SharpEdge;
    }

    public class RainwaterResult
    {
        public string RegionName { get; init; } = "";
        public double IntensityMmPerHour { get; init; }
        public double ContributionAreaM2 { get; init; }
        public double DesignFlowLitersPerMinute { get; init; }
        public double DesignFlowLitersPerSecond { get; init; }

        // Resultados da Calha
        public double ManningRoughnessN { get; init; }
        public string SelectedGutterDescription { get; init; } = "";
        public double GutterMaxCapacityLitersPerMinute { get; init; }
        public double GutterOccupancyPercentage { get; init; }
        public double GutterVelocityMetersPerSecond { get; init; }
        public bool GutterMeetsRequirement { get; init; }

        // Resultados dos Condutores Verticais
        public int DownspoutCount { get; init; }
        public double FlowPerDownspoutLitersPerMinute { get; init; }
        public int RequiredDownspoutDiameterMm { get; init; }
        public double DownspoutMaxCapacityLitersPerMinute { get; init; }
    }

    public static class RainwaterCalculator
    {
        private static readonly (double WidthMm, double HeightMm)[] CommercialRectangularGutters =
        {
            (100, 75),
            (125, 80),
            (150, 100),
            (200, 100),
            (200, 150),
            (250, 150),
            (300, 150),
            (300, 200),
            (400, 200),
            (500, 250),
            (600, 300)
        };

        private static readonly double[] CommercialSemicircularDiametersMm =
        {
            100, 125, 150, 200, 250, 300, 400
        };

        private static readonly int[] CommercialDownspoutDiametersMm =
        {
            75, 100, 150, 200, 250, 300
        };

        public static RainwaterResult Calculate(RainwaterInput input)
        {
            if (input.IntensityMmPerHour <= 0)
                throw new ArgumentException("A intensidade pluviométrica deve ser maior que zero.");
            if (input.RoofWidthMeters <= 0 || input.RoofProjectionMeters <= 0)
                throw new ArgumentException("As dimensões da cobertura devem ser maiores que zero.");
            if (input.GutterSlopePercent <= 0)
                throw new ArgumentException("A declividade da calha deve ser maior que zero.");

            int downspouts = Math.Max(1, input.DownspoutCount);
            double n = GetManningN(input.Material);
            double slope = input.GutterSlopePercent / 100.0; // Converte % para m/m

            // 1. Área de Contribuição (ABNT NBR 10844 - Item 5.3)
            double roofArea = (input.RoofProjectionMeters + (Math.Max(0, input.RoofHeightMeters) / 2.0)) * input.RoofWidthMeters;
            double wallContribution = 0.5 * Math.Max(0, input.InterceptingWallAreaM2);
            double totalAreaM2 = roofArea + wallContribution;

            // 2. Vazão de Projeto (Q = I * A / 60 em L/min)
            double flowLpm = (input.IntensityMmPerHour * totalAreaM2) / 60.0;
            double flowLps = flowLpm / 60.0;

            // 3. Dimensionamento / Verificação da Calha (Manning-Strickler)
            string gutterDesc;
            double gutterCapLpm;
            double gutterVelocity;

            if (input.Shape == GutterShape.Rectangular)
            {
                if (input.ManualWidthMm > 0 && input.ManualHeightMm > 0)
                {
                    (gutterCapLpm, gutterVelocity) = ComputeRectangularCapacity(input.ManualWidthMm, input.ManualHeightMm, n, slope);
                    gutterDesc = $"Retangular {input.ManualWidthMm:F0} × {input.ManualHeightMm:F0} mm";
                }
                else
                {
                    (double w, double h) chosen = CommercialRectangularGutters[^1];
                    gutterCapLpm = 0;
                    gutterVelocity = 0;

                    foreach (var option in CommercialRectangularGutters)
                    {
                        var (cap, vel) = ComputeRectangularCapacity(option.WidthMm, option.HeightMm, n, slope);
                        chosen = option;
                        gutterCapLpm = cap;
                        gutterVelocity = vel;
                        if (cap >= flowLpm) break;
                    }

                    gutterDesc = $"Retangular {chosen.w:F0} × {chosen.h:F0} mm";
                }
            }
            else
            {
                if (input.ManualWidthMm > 0)
                {
                    (gutterCapLpm, gutterVelocity) = ComputeSemicircularCapacity(input.ManualWidthMm, n, slope);
                    gutterDesc = $"Semicircular DN {input.ManualWidthMm:F0} mm";
                }
                else
                {
                    double chosenD = CommercialSemicircularDiametersMm[^1];
                    gutterCapLpm = 0;
                    gutterVelocity = 0;

                    foreach (double d in CommercialSemicircularDiametersMm)
                    {
                        var (cap, vel) = ComputeSemicircularCapacity(d, n, slope);
                        chosenD = d;
                        gutterCapLpm = cap;
                        gutterVelocity = vel;
                        if (cap >= flowLpm) break;
                    }

                    gutterDesc = $"Semicircular DN {chosenD:F0} mm";
                }
            }

            double occupancy = gutterCapLpm > 0 ? (flowLpm / gutterCapLpm) * 100.0 : 100.0;
            bool meetsGutter = gutterCapLpm >= flowLpm;

            // 4. Dimensionamento dos Condutores Verticais (ABNT NBR 10844 - Mínimo DN 75)
            double flowPerDownspout = flowLpm / downspouts;
            int chosenDn = CommercialDownspoutDiametersMm[^1];
            double downspoutCap = 0;

            foreach (int dn in CommercialDownspoutDiametersMm)
            {
                double cap = ComputeDownspoutCapacity(dn, input.WaterDepthAtExitMm, input.DownspoutHeightMeters, input.ExitType);
                chosenDn = dn;
                downspoutCap = cap;
                if (cap >= flowPerDownspout) break;
            }

            return new RainwaterResult
            {
                RegionName = input.RegionName,
                IntensityMmPerHour = input.IntensityMmPerHour,
                ContributionAreaM2 = totalAreaM2,
                DesignFlowLitersPerMinute = flowLpm,
                DesignFlowLitersPerSecond = flowLps,
                ManningRoughnessN = n,
                SelectedGutterDescription = gutterDesc,
                GutterMaxCapacityLitersPerMinute = gutterCapLpm,
                GutterOccupancyPercentage = occupancy,
                GutterVelocityMetersPerSecond = gutterVelocity,
                GutterMeetsRequirement = meetsGutter,
                DownspoutCount = downspouts,
                FlowPerDownspoutLitersPerMinute = flowPerDownspout,
                RequiredDownspoutDiameterMm = chosenDn,
                DownspoutMaxCapacityLitersPerMinute = downspoutCap
            };
        }

        private static (double CapacityLpm, double VelocityMs) ComputeRectangularCapacity(
            double widthMm, double totalHeightMm, double n, double slope)
        {
            // Lâmina d'água útil = 75% da altura total (25% de borda livre de segurança)
            double b = widthMm / 1000.0;
            double y = (totalHeightMm * 0.75) / 1000.0;

            double areaS = b * y;
            double perimeterP = b + (2.0 * y);
            double hydraulicRadius = areaS / perimeterP;

            double velocityMs = (1.0 / n) * Math.Pow(hydraulicRadius, 2.0 / 3.0) * Math.Sqrt(slope);
            double capacityLpm = 60000.0 * areaS * velocityMs;

            return (capacityLpm, velocityMs);
        }

        private static (double CapacityLpm, double VelocityMs) ComputeSemicircularCapacity(
            double diameterMm, double n, double slope)
        {
            // Meia seção plena (padrão NBR 10844 para calhas semicirculares)
            double d = diameterMm / 1000.0;
            double areaS = (Math.PI * d * d) / 8.0;
            double perimeterP = (Math.PI * d) / 2.0;
            double hydraulicRadius = d / 4.0;

            double velocityMs = (1.0 / n) * Math.Pow(hydraulicRadius, 2.0 / 3.0) * Math.Sqrt(slope);
            double capacityLpm = 60000.0 * areaS * velocityMs;

            return (capacityLpm, velocityMs);
        }

        private static double ComputeDownspoutCapacity(
            int diameterMm, double waterDepthMm, double heightMeters, DownspoutExitType exitType)
        {
            double hWater = Math.Max(15.0, waterDepthMm);
            double hPipe = Math.Max(1.0, heightMeters);

            // Formulação calibrada pelos ábacos da ABNT NBR 10844 (Figuras 3 e 4)
            double baseCapacity = 0.000377 * Math.Pow(diameterMm, 2.5) * Math.Sqrt(hWater) * Math.Pow(hPipe / 3.0, 0.15);
            return exitType == DownspoutExitType.Funnel ? baseCapacity * 1.28 : baseCapacity;
        }

        public static double GetManningN(GutterMaterial material) => material switch
        {
            GutterMaterial.PvcOrMetal => 0.011,
            GutterMaterial.CastIronOrSmoothConcrete => 0.012,
            GutterMaterial.CeramicOrRoughConcrete => 0.013,
            GutterMaterial.UnplasteredBrick => 0.015,
            _ => 0.011
        };
    }
}