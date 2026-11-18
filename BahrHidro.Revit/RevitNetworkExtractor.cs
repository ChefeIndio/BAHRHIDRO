using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using BahrHidro.Core.Hydraulic.Network;

namespace BahrHidro.Revit
{
    public class RevitNetworkExtractionResult
    {
        public HydraulicNetwork Network { get; } = new HydraulicNetwork("Rede BIM Revit");
        public Dictionary<Guid, List<Pipe>> PipesBySegmentId { get; } = new();
    }

    public static class RevitNetworkExtractor
    {
        private const int MaxElementsLimit = 800;

        public static RevitNetworkExtractionResult ExtractFromStartElement(Document doc, Element startElement)
        {
            var result = new RevitNetworkExtractionResult();
            ConnectorSet? startConnectors = GetConnectors(startElement);

            if (startConnectors == null || startConnectors.IsEmpty)
                throw new InvalidOperationException("O elemento selecionado não possui conectores hidráulicos.");

            var visitedElementIds = new HashSet<long> { startElement.Id.Value };
            double reservoirElevationM = GetElevationMeters(startElement, startConnectors);

            var reservoirNode = new HydraulicNode
            {
                Id = Guid.NewGuid(),
                Name = $"RES-{startElement.Id.Value}",
                Type = HydraulicNodeType.Reservoir,
                ElevationMeters = reservoirElevationM,
                DemandLitersPerSecond = 0,
                RelativeWeight = 0
            };

            result.Network.AddNode(reservoirNode);

            foreach (Connector conn in startConnectors)
            {
                if (!IsValidPipingConnector(conn)) continue;

                foreach (Connector refConn in conn.AllRefs)
                {
                    if (!IsValidPipingConnector(refConn)) continue;
                    if (refConn.Owner.Id.Value == startElement.Id.Value || visitedElementIds.Contains(refConn.Owner.Id.Value))
                        continue;

                    TraceBranch(
                        refConn.Owner,
                        refConn,
                        reservoirNode.Id,
                        result,
                        visitedElementIds);
                }
            }

            return result;
        }

        private static void TraceBranch(
            Element currentElement,
            Connector entryConnector,
            Guid upstreamNodeId,
            RevitNetworkExtractionResult result,
            HashSet<long> visitedIds)
        {
            double accumulatedLengthM = 0;
            double accumulatedK = 0;
            var segmentPipes = new List<Pipe>();

            Element? element = currentElement;
            Connector? entry = entryConnector;

            while (element != null && entry != null)
            {
                if (visitedIds.Count >= MaxElementsLimit)
                    break;

                visitedIds.Add(element.Id.Value);
                ConnectorSet? connectors = GetConnectors(element);
                if (connectors == null) break;

                // Identifica apenas saídas hidráulicas físicas conectadas
                var outgoingConnectors = new List<Connector>();
                foreach (Connector c in connectors)
                {
                    if (c.Id == entry.Id || !IsValidPipingConnector(c)) continue;

                    foreach (Connector refConn in c.AllRefs)
                    {
                        if (!IsValidPipingConnector(refConn)) continue;
                        if (refConn.Owner.Id.Value != element.Id.Value && !visitedIds.Contains(refConn.Owner.Id.Value))
                        {
                            outgoingConnectors.Add(c);
                            break;
                        }
                    }
                }

                if (element is Pipe pipe)
                {
                    segmentPipes.Add(pipe);
                    Parameter? lengthParam = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                    if (lengthParam != null)
                    {
                        accumulatedLengthM += UnitUtils.ConvertFromInternalUnits(lengthParam.AsDouble(), UnitTypeId.Meters);
                    }

                    // Caso o tubo tenha derivação direta (Tap) com 2 ou mais saídas
                    if (outgoingConnectors.Count >= 2)
                    {
                        var tapNode = new HydraulicNode
                        {
                            Id = Guid.NewGuid(),
                            Name = $"DER-{pipe.Id.Value}",
                            Type = HydraulicNodeType.Junction,
                            ElevationMeters = GetElevationMeters(pipe, connectors),
                            DemandLitersPerSecond = 0,
                            RelativeWeight = 0
                        };

                        result.Network.AddNode(tapNode);
                        RegisterSegment(result, upstreamNodeId, tapNode.Id, accumulatedLengthM, accumulatedK, segmentPipes);
                        TraceMultipleBranches(pipe.Id.Value, outgoingConnectors, tapNode.Id, result, visitedIds);
                        return;
                    }
                }
                else if (element is FamilyInstance fi)
                {
                    // Sem saídas: extremidade de consumo (aparelho sanitário ou ponto terminal)
                    if (outgoingConnectors.Count == 0)
                    {
                        var consumptionNode = new HydraulicNode
                        {
                            Id = Guid.NewGuid(),
                            Name = $"PC-{fi.Id.Value}",
                            Type = HydraulicNodeType.ConsumptionPoint,
                            ElevationMeters = GetElevationMeters(fi, connectors),
                            DemandLitersPerSecond = 0,
                            RelativeWeight = ReadFixtureWeight(fi)
                        };

                        result.Network.AddNode(consumptionNode);
                        RegisterSegment(result, upstreamNodeId, consumptionNode.Id, accumulatedLengthM, accumulatedK, segmentPipes);
                        return;
                    }

                    // 2 ou mais saídas: derivação (Tê ou Cruzeta)
                    if (outgoingConnectors.Count >= 2)
                    {
                        var junctionNode = new HydraulicNode
                        {
                            Id = Guid.NewGuid(),
                            Name = $"NO-{fi.Id.Value}",
                            Type = HydraulicNodeType.Junction,
                            ElevationMeters = GetElevationMeters(fi, connectors),
                            DemandLitersPerSecond = 0,
                            RelativeWeight = 0
                        };

                        result.Network.AddNode(junctionNode);
                        RegisterSegment(result, upstreamNodeId, junctionNode.Id, accumulatedLengthM, accumulatedK, segmentPipes);
                        TraceMultipleBranches(fi.Id.Value, outgoingConnectors, junctionNode.Id, result, visitedIds);
                        return;
                    }

                    // 1 saída: conexão em série (joelho, curva, registro, redução)
                    accumulatedK += EstimateFittingK(fi);
                }

                // Avança para o próximo elemento em série
                if (outgoingConnectors.Count == 1)
                {
                    Connector? nextEntry = null;
                    foreach (Connector refConn in outgoingConnectors[0].AllRefs)
                    {
                        if (!IsValidPipingConnector(refConn)) continue;
                        if (refConn.Owner.Id.Value != element.Id.Value && !visitedIds.Contains(refConn.Owner.Id.Value))
                        {
                            nextEntry = refConn;
                            break;
                        }
                    }

                    if (nextEntry == null) break;
                    element = nextEntry.Owner;
                    entry = nextEntry;
                }
                else
                {
                    // Tubulação terminando sem peça conectada na ponta
                    var openEndNode = new HydraulicNode
                    {
                        Id = Guid.NewGuid(),
                        Name = $"FIM-{element.Id.Value}",
                        Type = HydraulicNodeType.ConsumptionPoint,
                        ElevationMeters = GetElevationMeters(element, connectors),
                        DemandLitersPerSecond = 0,
                        RelativeWeight = 1.0
                    };

                    result.Network.AddNode(openEndNode);
                    RegisterSegment(result, upstreamNodeId, openEndNode.Id, accumulatedLengthM, accumulatedK, segmentPipes);
                    break;
                }
            }
        }

        private static void TraceMultipleBranches(
            long ownerId,
            List<Connector> outgoingConnectors,
            Guid junctionNodeId,
            RevitNetworkExtractionResult result,
            HashSet<long> visitedIds)
        {
            foreach (Connector outConn in outgoingConnectors)
            {
                foreach (Connector nextRef in outConn.AllRefs)
                {
                    if (!IsValidPipingConnector(nextRef)) continue;
                    if (nextRef.Owner.Id.Value != ownerId && !visitedIds.Contains(nextRef.Owner.Id.Value))
                    {
                        TraceBranch(nextRef.Owner, nextRef, junctionNodeId, result, visitedIds);
                    }
                }
            }
        }

        private static bool IsValidPipingConnector(Connector conn)
        {
            if (conn == null || !conn.IsConnected) return false;
            if (conn.Domain != Domain.DomainPiping) return false;
            if (conn.ConnectorType == ConnectorType.Logical) return false;
            return true;
        }

        private static void RegisterSegment(
            RevitNetworkExtractionResult result,
            Guid startNodeId,
            Guid endNodeId,
            double lengthMeters,
            double totalK,
            List<Pipe> pipes)
        {
            var segmentId = Guid.NewGuid();
            var segment = new HydraulicPipeSegment
            {
                Id = segmentId,
                Name = $"T-{result.Network.Segments.Count + 1}",
                StartNodeId = startNodeId,
                EndNodeId = endNodeId,
                LengthMeters = Math.Max(0.10, lengthMeters),
                InternalDiameterMm = null, // Modo Auto (seleciona pelo catálogo)
                AbsoluteRoughnessMm = 0.001,
                TotalLossCoefficientK = totalK
            };

            result.Network.AddSegment(segment);
            result.PipesBySegmentId[segmentId] = new List<Pipe>(pipes);
        }

        private static ConnectorSet? GetConnectors(Element element)
        {
            if (element is MEPCurve curve) return curve.ConnectorManager?.Connectors;
            if (element is FamilyInstance fi) return fi.MEPModel?.ConnectorManager?.Connectors;
            return null;
        }

        private static double GetElevationMeters(Element element, ConnectorSet connectors)
        {
            foreach (Connector c in connectors)
            {
                if (c.Domain == Domain.DomainPiping && c.ConnectorType != ConnectorType.Logical)
                    return UnitUtils.ConvertFromInternalUnits(c.Origin.Z, UnitTypeId.Meters);
            }

            BoundingBoxXYZ? bb = element.get_BoundingBox(null);
            if (bb != null)
                return UnitUtils.ConvertFromInternalUnits((bb.Min.Z + bb.Max.Z) / 2.0, UnitTypeId.Meters);

            return 0;
        }

        private static double ReadFixtureWeight(FamilyInstance fi)
        {
            Parameter? weightParam = fi.LookupParameter("BahrHidro_Peso") ?? fi.LookupParameter("Peso");
            if (weightParam != null && weightParam.StorageType == StorageType.Double && weightParam.AsDouble() > 0)
                return weightParam.AsDouble();

            return 1.0;
        }

        private static double EstimateFittingK(FamilyInstance fi)
        {
            string name = $"{fi.Symbol?.FamilyName} {fi.Name}".ToLowerInvariant();
            if (name.Contains("registro") || name.Contains("gaveta") || name.Contains("esfera")) return 0.3;
            if (name.Contains("joelho") || name.Contains("90")) return 0.9;
            if (name.Contains("curva") || name.Contains("45")) return 0.4;
            return 0.5;
        }
    }
}