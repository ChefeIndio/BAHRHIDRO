using System;
using System.Collections.Generic;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed record HydraulicNetworkResult(
        Guid SegmentId,
        string SegmentName,
        HydraulicSegmentResult HydraulicResult
    );

    public static class HydraulicNetworkCalculator
    {
        public static IReadOnlyList<HydraulicNetworkResult> Calculate(
            HydraulicNetwork network,
            double kinematicViscosityM2s = 0.000001004)
        {
            ArgumentNullException.ThrowIfNull(network);

            var results = new List<HydraulicNetworkResult>();

            foreach (var segment in network.Segments)
            {
                // CORREÇÃO: Garante que a classe antiga extrai o valor de forma segura (.Value) 
                // para não causar conflito com o novo sistema de Auto-Dimensionamento.
                double flow = segment.FlowLitersPerSecond ?? 
                    throw new InvalidOperationException($"Vazão ausente no trecho {segment.Name}");
                    
                double diameter = segment.InternalDiameterMm ?? 
                    throw new InvalidOperationException($"Diâmetro ausente no trecho {segment.Name}");

                var result = HydraulicSegmentCalculator.Calculate(
                    flow,
                    segment.LengthMeters,
                    diameter,
                    segment.AbsoluteRoughnessMm,
                    kinematicViscosityM2s,
                    segment.TotalLossCoefficientK);

                results.Add(new HydraulicNetworkResult(segment.Id, segment.Name, result));
            }

            return results.AsReadOnly();
        }
    }
}