
using System;
using System.Collections.Generic;
using System.Linq;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed record HydraulicPipeFlowAssignment(
        Guid SegmentId,
        string SegmentName,
        double FlowLitersPerSecond
    );

    public sealed record HydraulicBranchedFlowResult(
        Guid SourceNodeId,
        string SourceNodeName,
        double TotalDemandLitersPerSecond,
        double RequiredSupplyLitersPerSecond,
        IReadOnlyList<HydraulicPipeFlowAssignment> PipeFlows
    );

    public static class HydraulicBranchedFlowCalculator
    {
        public static HydraulicBranchedFlowResult Calculate(
            HydraulicNetwork network,
            double toleranceLitersPerSecond = 0.000001)
        {
            ArgumentNullException.ThrowIfNull(network);

            if (!double.IsFinite(toleranceLitersPerSecond) ||
                toleranceLitersPerSecond < 0)
            {
                throw new ArgumentException(
                    "A tolerância deve ser positiva ou zero.");
            }

            if (network.Nodes.Count == 0)
                throw new InvalidOperationException(
                    "A rede não possui nós.");

            if (network.Segments.Count == 0)
                throw new InvalidOperationException(
                    "A rede não possui trechos.");

            foreach (var node in network.Nodes)
                node.Validate();

            foreach (var segment in network.Segments)
                segment.Validate();

            var sources = network.Nodes
                .Where(n => n.Type == HydraulicNodeType.Reservoir)
                .ToList();

            if (sources.Count != 1)
                throw new InvalidOperationException(
                    "A rede deve possuir exatamente um reservatório.");

            var source = sources[0];

            var incomingCounts = network.Nodes.ToDictionary(
                n => n.Id,
                _ => 0);

            var outgoingSegments = network.Nodes.ToDictionary(
                n => n.Id,
                _ => new List<HydraulicPipeSegment>());

            foreach (var segment in network.Segments)
            {
                if (!incomingCounts.ContainsKey(segment.StartNodeId) ||
                    !incomingCounts.ContainsKey(segment.EndNodeId))
                {
                    throw new InvalidOperationException(
                        $"O trecho '{segment.Name}' possui nós inexistentes.");
                }

                incomingCounts[segment.EndNodeId]++;

                outgoingSegments[segment.StartNodeId].Add(segment);
            }

            if (incomingCounts[source.Id] != 0)
                throw new InvalidOperationException(
                    "O reservatório não pode receber alimentação de outro trecho.");

            foreach (var node in network.Nodes)
            {
                if (node.Id != source.Id &&
                    incomingCounts[node.Id] != 1)
                {
                    throw new InvalidOperationException(
                        $"O nó '{node.Name}' deve possuir exatamente um trecho de entrada. " +
                        "Esta versão aceita apenas redes ramificadas sem malhas.");
                }
            }

            var visited = new HashSet<Guid>();
            var assignments = new List<HydraulicPipeFlowAssignment>();

            double CalculateDownstreamDemand(HydraulicNode node)
            {
                if (!visited.Add(node.Id))
                {
                    throw new InvalidOperationException(
                        $"Foi detectado um ciclo na rede próximo ao nó '{node.Name}'.");
                }

                double totalDemand = node.DemandLitersPerSecond;

                foreach (var segment in outgoingSegments[node.Id])
                {
                    var child = network.FindNode(segment.EndNodeId);

                    if (child == null)
                    {
                        throw new InvalidOperationException(
                            $"O nó final do trecho '{segment.Name}' não foi encontrado.");
                    }

                    double branchDemand =
                        CalculateDownstreamDemand(child);

                    assignments.Add(
                        new HydraulicPipeFlowAssignment(
                            segment.Id,
                            segment.Name,
                            branchDemand));

                    totalDemand += branchDemand;
                }

                return totalDemand;
            }

            double totalDemand =
                CalculateDownstreamDemand(source);

            if (visited.Count != network.Nodes.Count)
            {
                throw new InvalidOperationException(
                    "Existem nós desconectados do reservatório.");
            }

            if (source.ExternalSupplyLitersPerSecond > 0 &&
                Math.Abs(
                    source.ExternalSupplyLitersPerSecond - totalDemand)
                > toleranceLitersPerSecond)
            {
                throw new InvalidOperationException(
                    "A alimentação informada no reservatório não corresponde " +
                    "à demanda total da rede.");
            }

            return new HydraulicBranchedFlowResult(
                source.Id,
                source.Name,
                totalDemand,
                totalDemand,
                assignments.AsReadOnly());
        }
    }
}
