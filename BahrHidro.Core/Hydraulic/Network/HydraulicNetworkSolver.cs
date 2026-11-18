using System;
using System.Collections.Generic;
using System.Linq;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed record NodeCalculationResult(
        Guid NodeId,
        string NodeName,
        HydraulicNodeType NodeType,
        double ElevationMeters,
        double DemandLitersPerSecond,
        double PiezometricHeadMeters,
        double AvailablePressureMca
    );

    public sealed record SegmentCalculationResult(
        Guid SegmentId,
        string SegmentName,
        string Connection,
        double FlowLitersPerSecond,
        double VelocityMetersPerSecond,
        double ReynoldsNumber,
        double FrictionFactor,
        double TotalHeadLossMeters
    );

    public sealed record NetworkCalculationResult(
        IReadOnlyList<NodeCalculationResult> Nodes,
        IReadOnlyList<SegmentCalculationResult> Segments
    );

    public static class HydraulicNetworkSolver
    {
        public static NetworkCalculationResult Solve(
            HydraulicNetwork network,
            double kinematicViscosityM2s = 0.000001004)
        {
            ArgumentNullException.ThrowIfNull(network);

            if (network.Nodes.Count == 0 || network.Segments.Count == 0)
                throw new InvalidOperationException("A rede não possui nós ou trechos suficientes para cálculo.");

            // 1. Distribuir vazões de jusante para montante
            var flowResult = HydraulicBranchedFlowCalculator.Calculate(network);
            var flowBySegment = flowResult.PipeFlows.ToDictionary(f => f.SegmentId, f => f.FlowLitersPerSecond);

            var segmentResults = new List<SegmentCalculationResult>();
            var headLossBySegment = new Dictionary<Guid, double>();

            // 2. Calcular perdas de carga e física de cada trecho (usando o mesmo motor da Aba 2)
            foreach (var segment in network.Segments)
            {
                double flow = flowBySegment[segment.Id];

                double headLoss = 0;
                double velocity = 0;
                double reynolds = 0;
                double f = 0;

                // Se houver vazão passando pelo trecho, calcula a perda
                if (flow > 0)
                {
                    var segCalc = HydraulicSegmentCalculator.Calculate(
                        flowLitersPerSecond: flow,
                        lengthMeters: segment.LengthMeters,
                        internalDiameterMm: segment.InternalDiameterMm,
                        absoluteRoughnessMm: segment.AbsoluteRoughnessMm,
                        kinematicViscosityM2s: kinematicViscosityM2s,
                        totalLossCoefficientK: segment.TotalLossCoefficientK
                    );

                    headLoss = segCalc.TotalHeadLossMeters;
                    velocity = segCalc.VelocityMetersPerSecond;
                    reynolds = segCalc.ReynoldsNumber;
                    f = segCalc.FrictionFactor;
                }

                headLossBySegment[segment.Id] = headLoss;

                string startName = network.FindNode(segment.StartNodeId)?.Name ?? "?";
                string endName = network.FindNode(segment.EndNodeId)?.Name ?? "?";

                segmentResults.Add(new SegmentCalculationResult(
                    segment.Id,
                    segment.Name,
                    $"{startName} → {endName}",
                    flow,
                    velocity,
                    reynolds,
                    f,
                    headLoss
                ));
            }

            // 3. Propagar cotas piezométricas (do reservatório até os pontos de consumo)
            var reservoir = network.Nodes.Single(n => n.Type == HydraulicNodeType.Reservoir);
            var piezometricHeads = new Dictionary<Guid, double>
            {
                [reservoir.Id] = reservoir.ElevationMeters
            };

            // Construir grafo de adjacência direcional
            var adj = new Dictionary<Guid, List<HydraulicPipeSegment>>();
            foreach (var n in network.Nodes) adj[n.Id] = new List<HydraulicPipeSegment>();
            foreach (var s in network.Segments) adj[s.StartNodeId].Add(s);

            // Algoritmo de Busca em Largura (BFS) para descer as pressões
            var queue = new Queue<Guid>();
            queue.Enqueue(reservoir.Id);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                double currentHead = piezometricHeads[currentId];

                foreach (var outgoingSegment in adj[currentId])
                {
                    // Cota Piezométrica de Jusante = Cota Piezométrica de Montante - Perda de Carga do Trecho
                    double childHead = currentHead - headLossBySegment[outgoingSegment.Id];
                    piezometricHeads[outgoingSegment.EndNodeId] = childHead;
                    
                    queue.Enqueue(outgoingSegment.EndNodeId);
                }
            }

            // 4. Montar resultados dos nós, calculando a Pressão Disponível
            var nodeResults = new List<NodeCalculationResult>();
            foreach (var node in network.Nodes)
            {
                double head = piezometricHeads[node.Id];
                
                // Pressão Dinâmica Disponível = Cota Piezométrica - Cota Geométrica do Nó
                double pressure = head - node.ElevationMeters;

                nodeResults.Add(new NodeCalculationResult(
                    node.Id,
                    node.Name,
                    node.Type,
                    node.ElevationMeters,
                    node.DemandLitersPerSecond,
                    head,
                    pressure
                ));
            }

            return new NetworkCalculationResult(
                nodeResults.AsReadOnly(),
                segmentResults.AsReadOnly()
            );
        }
    }
}