using System;
using System.Collections.Generic;
using System.Linq;
using BahrHidro.Core.Catalog;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed record NodeCalculationResult(Guid NodeId, string NodeName, HydraulicNodeType NodeType, double ElevationMeters, double DemandLitersPerSecond, double PiezometricHeadMeters, double AvailablePressureMca);

    public sealed record SegmentCalculationResult(
        Guid SegmentId, string SegmentName, string Connection, double FlowLitersPerSecond,
        double SelectedDiameterMm,
        double VelocityMetersPerSecond, double ReynoldsNumber, double FrictionFactor, double TotalHeadLossMeters
    );

    public sealed record NetworkCalculationResult(IReadOnlyList<NodeCalculationResult> Nodes, IReadOnlyList<SegmentCalculationResult> Segments);

    public static class HydraulicNetworkSolver
    {
        public static NetworkCalculationResult Solve(
            HydraulicNetwork network,
            double kinematicViscosityM2s = 0.000001004,
            IReadOnlyList<PipeDiameterOption>? availableDiameters = null,
            double maxVelocity = 3.0)
        {
            ArgumentNullException.ThrowIfNull(network);
            if (network.Nodes.Count == 0 || network.Segments.Count == 0)
                throw new InvalidOperationException("A rede não possui nós ou trechos suficientes para cálculo.");

            var flowResult = HydraulicBranchedFlowCalculator.Calculate(network);
            var flowBySegment = flowResult.PipeFlows.ToDictionary(f => f.SegmentId, f => f.FlowLitersPerSecond);
            var segmentResults = new List<SegmentCalculationResult>();
            var headLossBySegment = new Dictionary<Guid, double>();

            foreach (var segment in network.Segments)
            {
                double flow = flowBySegment.TryGetValue(segment.Id, out double fVal) ? fVal : 0;
                double headLoss = 0, velocity = 0, reynolds = 0, f = 0, d = 0;

                if (flow > 0)
                {
                    if (segment.InternalDiameterMm.HasValue)
                    {
                        d = segment.InternalDiameterMm.Value;
                    }
                    else
                    {
                        if (availableDiameters == null || !availableDiameters.Any())
                            throw new InvalidOperationException($"O trecho '{segment.Name}' está configurado para 'Auto', mas o catálogo não foi fornecido.");

                        var selecionado = PipeDiameterSelector.SelectByFlow(flow, availableDiameters, maxVelocity);
                        if (selecionado == null)
                            throw new InvalidOperationException($"Não há tubo no catálogo que atenda a vazão de {flow:F3} L/s no limite de {maxVelocity} m/s para o trecho '{segment.Name}'.");

                        d = selecionado.InternalDiameterMm;
                    }

                    var segCalc = HydraulicSegmentCalculator.Calculate(flow, segment.LengthMeters, d, segment.AbsoluteRoughnessMm, kinematicViscosityM2s, segment.TotalLossCoefficientK);
                    headLoss = segCalc.TotalHeadLossMeters;
                    velocity = segCalc.VelocityMetersPerSecond;
                    reynolds = segCalc.ReynoldsNumber;
                    f = segCalc.FrictionFactor;
                }
                else
                {
                    if (segment.InternalDiameterMm.HasValue)
                        d = segment.InternalDiameterMm.Value;
                    else if (availableDiameters != null && availableDiameters.Any())
                        d = availableDiameters.OrderBy(x => x.InternalDiameterMm).First().InternalDiameterMm;
                }

                headLossBySegment[segment.Id] = headLoss;
                segmentResults.Add(new SegmentCalculationResult(
                    segment.Id,
                    segment.Name,
                    $"{network.FindNode(segment.StartNodeId)?.Name ?? "?"} → {network.FindNode(segment.EndNodeId)?.Name ?? "?"}",
                    flow,
                    d,
                    velocity,
                    reynolds,
                    f,
                    headLoss));
            }

            var reservoirs = network.Nodes.Where(n => n.Type == HydraulicNodeType.Reservoir).ToList();
            if (reservoirs.Count == 0)
                throw new InvalidOperationException("A rede precisa de pelo menos um nó de Reservatório / Alimentação.");

            var piezometricHeads = new Dictionary<Guid, double>();
            var adj = new Dictionary<Guid, List<HydraulicPipeSegment>>();
            foreach (var n in network.Nodes) adj[n.Id] = new List<HydraulicPipeSegment>();
            foreach (var s in network.Segments) adj[s.StartNodeId].Add(s);

            var queue = new Queue<Guid>();
            foreach (var res in reservoirs)
            {
                piezometricHeads[res.Id] = res.ElevationMeters;
                queue.Enqueue(res.Id);
            }

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                double currentHead = piezometricHeads[currentId];
                foreach (var outgoingSegment in adj[currentId])
                {
                    double nextHead = currentHead - headLossBySegment[outgoingSegment.Id];
                    if (!piezometricHeads.ContainsKey(outgoingSegment.EndNodeId))
                    {
                        piezometricHeads[outgoingSegment.EndNodeId] = nextHead;
                        queue.Enqueue(outgoingSegment.EndNodeId);
                    }
                }
            }

            double defaultHead = reservoirs[0].ElevationMeters;
            var nodeResults = new List<NodeCalculationResult>();
            foreach (var node in network.Nodes)
            {
                double head = piezometricHeads.TryGetValue(node.Id, out double h) ? h : defaultHead;
                nodeResults.Add(new NodeCalculationResult(
                    node.Id,
                    node.Name,
                    node.Type,
                    node.ElevationMeters,
                    node.DemandLitersPerSecond,
                    head,
                    head - node.ElevationMeters));
            }

            return new NetworkCalculationResult(nodeResults.AsReadOnly(), segmentResults.AsReadOnly());
        }
    }
}
