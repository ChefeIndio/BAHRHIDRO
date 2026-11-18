using System;
using System.Collections.Generic;
using System.Linq;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed record HydraulicPipeFlowAssignment(
        Guid SegmentId,
        string SegmentName,
        double FlowLitersPerSecond,
        double AccumulatedWeight
    );

    public sealed record HydraulicBranchedFlowResult(
        Guid SourceNodeId,
        string SourceNodeName,
        double TotalContinuousDemand,
        double TotalWeight,
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

            if (network.Nodes.Count == 0) throw new InvalidOperationException("A rede não possui nós.");
            if (network.Segments.Count == 0) throw new InvalidOperationException("A rede não possui trechos.");

            foreach (var node in network.Nodes) node.Validate();
            foreach (var segment in network.Segments) segment.Validate();

            var sources = network.Nodes.Where(n => n.Type == HydraulicNodeType.Reservoir).ToList();
            if (sources.Count != 1) throw new InvalidOperationException("A rede deve possuir exatamente um reservatório.");

            var source = sources[0];

            var incomingCounts = network.Nodes.ToDictionary(n => n.Id, _ => 0);
            var outgoingSegments = network.Nodes.ToDictionary(n => n.Id, _ => new List<HydraulicPipeSegment>());

            foreach (var segment in network.Segments)
            {
                if (!incomingCounts.ContainsKey(segment.StartNodeId) || !incomingCounts.ContainsKey(segment.EndNodeId))
                    throw new InvalidOperationException($"O trecho '{segment.Name}' possui nós inexistentes.");
                
                incomingCounts[segment.EndNodeId]++;
                outgoingSegments[segment.StartNodeId].Add(segment);
            }

            if (incomingCounts[source.Id] != 0) throw new InvalidOperationException("O reservatório não pode receber alimentação de água.");
            
            foreach (var node in network.Nodes)
            {
                if (node.Id != source.Id && incomingCounts[node.Id] != 1)
                    throw new InvalidOperationException($"O nó '{node.Name}' deve possuir apenas um trecho de entrada.");
            }

            var visited = new HashSet<Guid>();
            var assignments = new List<HydraulicPipeFlowAssignment>();

            // Função recursiva: retorna a (Demanda Contínua, Peso Total) de jusante para montante
            (double continuous, double weight) CalculateDownstreamDemand(HydraulicNode node)
            {
                if (!visited.Add(node.Id)) throw new InvalidOperationException($"Ciclo detectado próximo ao nó '{node.Name}'.");

                double totalContinuous = node.DemandLitersPerSecond;
                double totalWeight = node.RelativeWeight;

                foreach (var segment in outgoingSegments[node.Id])
                {
                    var child = network.FindNode(segment.EndNodeId);
                    var (childContinuous, childWeight) = CalculateDownstreamDemand(child);

                    // Aplica a inteligência da ABNT NBR 5626: Q = 0.3 * raiz(Soma dos Pesos)
                    double flowFromWeight = childWeight > 0 ? 0.3 * Math.Sqrt(childWeight) : 0;
                    
                    // A vazão final do trecho é a Demanda Contínua (fixa) + a Vazão Provável
                    double segmentFlow = childContinuous + flowFromWeight;

                    // Se o utilizador digitou uma vazão fixa (bypass), ela sobrepõe o cálculo
                    if (segment.FlowLitersPerSecond.HasValue)
                        segmentFlow = segment.FlowLitersPerSecond.Value;

                    assignments.Add(new HydraulicPipeFlowAssignment(
                        segment.Id,
                        segment.Name,
                        segmentFlow,
                        childWeight));

                    totalContinuous += childContinuous;
                    totalWeight += childWeight;
                }

                return (totalContinuous, totalWeight);
            }

            var (totalContinuous, totalWeight) = CalculateDownstreamDemand(source);

            if (visited.Count != network.Nodes.Count) throw new InvalidOperationException("Existem nós desconectados.");

            double requiredSupply = totalContinuous + (totalWeight > 0 ? 0.3 * Math.Sqrt(totalWeight) : 0);

            return new HydraulicBranchedFlowResult(
                source.Id,
                source.Name,
                totalContinuous,
                totalWeight,
                requiredSupply,
                assignments.AsReadOnly());
        }
    }
}