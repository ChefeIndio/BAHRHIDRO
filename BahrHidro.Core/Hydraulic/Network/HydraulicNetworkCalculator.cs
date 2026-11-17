
using System;
using System.Collections.Generic;
using BahrHidro.Core.Hydraulic;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed record HydraulicPipeSegmentResult(
        Guid SegmentId,
        string SegmentName,
        HydraulicSegmentResult HydraulicResult
    );

    public static class HydraulicNetworkCalculator
    {
        public static IReadOnlyList<HydraulicPipeSegmentResult> Calculate(
            HydraulicNetwork network,
            double kinematicViscosityM2s)
        {
            ArgumentNullException.ThrowIfNull(network);

            if (!double.IsFinite(kinematicViscosityM2s) ||
                kinematicViscosityM2s <= 0)
            {
                throw new ArgumentException(
                    "A viscosidade cinemática deve ser positiva.");
            }

            var results =
                new List<HydraulicPipeSegmentResult>();

            foreach (var segment in network.Segments)
            {
                segment.Validate();

                if (!segment.FlowLitersPerSecond.HasValue)
                {
                    throw new InvalidOperationException(
                        $"O trecho '{segment.Name}' não possui vazão informada.");
                }

                HydraulicSegmentResult hydraulicResult =
                    HydraulicSegmentCalculator.Calculate(
                        flowLitersPerSecond:
                            segment.FlowLitersPerSecond.Value,

                        lengthMeters:
                            segment.LengthMeters,

                        internalDiameterMm:
                            segment.InternalDiameterMm,

                        absoluteRoughnessMm:
                            segment.AbsoluteRoughnessMm,

                        kinematicViscosityM2s:
                            kinematicViscosityM2s,

                        totalLossCoefficientK:
                            segment.TotalLossCoefficientK
                    );

                results.Add(
                    new HydraulicPipeSegmentResult(
                        segment.Id,
                        segment.Name,
                        hydraulicResult
                    )
                );
            }

            return results.AsReadOnly();
        }
    }
}
