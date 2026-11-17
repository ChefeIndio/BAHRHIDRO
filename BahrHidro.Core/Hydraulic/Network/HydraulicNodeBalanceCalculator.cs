
using System;
using System.Collections.Generic;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed record HydraulicNodeBalanceResult(
        Guid NodeId,
        string NodeName,
        double IncomingFlowLitersPerSecond,
        double OutgoingFlowLitersPerSecond,
        double DemandLitersPerSecond,
        double ExternalSupplyLitersPerSecond,
        double BalanceErrorLitersPerSecond,
        bool IsBalanced
    );

    public static class HydraulicNodeBalanceCalculator
    {
        public static IReadOnlyList<HydraulicNodeBalanceResult> Calculate(
            HydraulicNetwork network,
            double toleranceLitersPerSecond = 0.000001)
        {
            ArgumentNullException.ThrowIfNull(network);

            if (!double.IsFinite(toleranceLitersPerSecond) ||
                toleranceLitersPerSecond < 0)
            {
                throw new ArgumentException(
                    "A tolerancia deve ser positiva ou zero.");
            }

            var results =
                new List<HydraulicNodeBalanceResult>();

            foreach (var node in network.Nodes)
            {
                node.Validate();

                double incomingFlow = 0.0;
                double outgoingFlow = 0.0;

                foreach (var segment in network.Segments)
                {
                    segment.Validate();

                    if (!segment.FlowLitersPerSecond.HasValue)
                    {
                        throw new InvalidOperationException(
                            $"O trecho '{segment.Name}' nao possui vazao informada.");
                    }

                    double flow =
                        segment.FlowLitersPerSecond.Value;

                    if (segment.EndNodeId == node.Id)
                        incomingFlow += flow;

                    if (segment.StartNodeId == node.Id)
                        outgoingFlow += flow;
                }

                double balanceError =
                    incomingFlow +
                    node.ExternalSupplyLitersPerSecond -
                    outgoingFlow -
                    node.DemandLitersPerSecond;

                bool isBalanced =
                    Math.Abs(balanceError) <= toleranceLitersPerSecond;

                results.Add(
                    new HydraulicNodeBalanceResult(
                        node.Id,
                        node.Name,
                        incomingFlow,
                        outgoingFlow,
                        node.DemandLitersPerSecond,
                        node.ExternalSupplyLitersPerSecond,
                        balanceError,
                        isBalanced
                    )
                );
            }

            return results.AsReadOnly();
        }
    }
}
