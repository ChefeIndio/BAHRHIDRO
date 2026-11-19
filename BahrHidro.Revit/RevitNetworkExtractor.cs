using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using BahrHidro.Core.Hydraulic.Network;

namespace BahrHidro.Revit
{
    public class RevitNetworkExtractionResult
    {
        public HydraulicNetwork Network { get; } = new HydraulicNetwork("Rede BIM Revit");
        public Dictionary<Guid, List<Pipe>> PipesBySegmentId { get; } = new();
        public Dictionary<Guid, double> OriginalDiametersMmBySegmentId { get; } = new();
        public string DetectionSummary { get; set; } = "";
    }

    public static class RevitNetworkExtractor
    {
        private const int MaxElementsLimit = 2500;

        public static RevitNetworkExtractionResult ExtractAutomaticFromDocument(Document doc)
        {
            var allPipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            if (allPipes.Count == 0)
                throw new InvalidOperationException("Nenhuma tubulação (Pipe) foi encontrada no modelo 3D aberto.");

            // Filtra preferencialmente tubos de Água Fria (DomesticColdWater)
            var coldWaterPipes = allPipes.Where(IsColdWaterPipe).ToList();
            var targetPipes = coldWaterPipes.Count > 0 ? coldWaterPipes : allPipes;

            // Agrupa tubos em sub-redes conectadas (componentes conexos)
            var visitedPipeIds = new HashSet<long>();
            var components = new List<List<Element>>();

            foreach (Pipe seedPipe in targetPipes)
            {
                if (visitedPipeIds.Contains(seedPipe.Id.Value))
                    continue;

                var compElements = CollectConnectedComponent(seedPipe, visitedPipeIds);
                if (compElements.Count > 0)
                    components.Add(compElements);
            }

            if (components.Count == 0)
                throw new InvalidOperationException("Não foi possível identificar conexões hidráulicas válidas nas tubulações do modelo.");

            // Seleciona a maior rede conectada de água fria do projeto
            var mainComponent = components
                .OrderByDescending(c => c.OfType<Pipe>().Count())
                .ThenByDescending(c => c.Count)
                .First();

            Element startElement = FindBestSupplySource(mainComponent);
            var result = ExtractFromStartElementInternal(doc, startElement, mainComponent);

            int totalPipesCount = result.PipesBySegmentId.Values.Sum(list => list.Count);
            int totalFixtures = result.Network.Nodes.Count(n => n.Type == HydraulicNodeType.ConsumptionPoint);
            double totalWeight = result.Network.Nodes.Sum(n => n.RelativeWeight);

            result.DetectionSummary =
                $"Detecção Automática 3D: {totalPipesCount} tubo(s) agrupados em {result.Network.Segments.Count} trecho(s), " +
                $"{totalFixtures} ponto(s) de consumo (ΣP = {totalWeight:F2})" +
                (components.Count > 1 ? $" | Sub-rede principal selecionada ({components.Count} redes detectadas no modelo)." : ".");

            return result;
        }

        public static RevitNetworkExtractionResult ExtractFromStartElement(Document doc, Element startElement)
        {
            var result = ExtractFromStartElementInternal(doc, startElement, allowedElements: null);
            int totalPipesCount = result.PipesBySegmentId.Values.Sum(list => list.Count);
            int totalFixtures = result.Network.Nodes.Count(n => n.Type == HydraulicNodeType.ConsumptionPoint);
            double totalWeight = result.Network.Nodes.Sum(n => n.RelativeWeight);

            result.DetectionSummary =
                $"Leitura a partir de '{startElement.Name}': {totalPipesCount} tubo(s) em {result.Network.Segments.Count} trecho(s), " +
                $"{totalFixtures} ponto(s) de consumo (ΣP = {totalWeight:F2}).";

            return result;
        }

        private static bool IsColdWaterPipe(Pipe pipe)
        {
            try
            {
                if (pipe.MEPSystem is PipingSystem ps)
                {
                    if (ps.SystemType == PipeSystemType.DomesticColdWater)
                        return true;
                    if (ps.SystemType == PipeSystemType.Sanitary ||
                        ps.SystemType == PipeSystemType.Vent ||
                        ps.SystemType == PipeSystemType.DomesticHotWater)
                        return false;
                }

                Parameter? sysParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                string sysName = RemoverAcentos(sysParam?.AsValueString() ?? "").ToLowerInvariant();
                if (sysName.Contains("fria") || sysName.Contains("cold") || sysName.Contains("af"))
                    return true;
                if (sysName.Contains("esgoto") || sysName.Contains("sanit") || sysName.Contains("vent") ||
                    sysName.Contains("quente") || sysName.Contains("hot") || sysName.Contains("pluv") || sysName.Contains("storm"))
                    return false;
            }
            catch { }

            return false;
        }

        private static List<Element> CollectConnectedComponent(Element seed, HashSet<long> globalVisitedPipes)
        {
            var component = new List<Element>();
            var visitedLocal = new HashSet<long>();
            var queue = new Queue<Element>();

            queue.Enqueue(seed);
            visitedLocal.Add(seed.Id.Value);

            while (queue.Count > 0 && component.Count < MaxElementsLimit)
            {
                Element current = queue.Dequeue();
                component.Add(current);
                if (current is Pipe)
                    globalVisitedPipes.Add(current.Id.Value);

                ConnectorSet? conns = GetConnectors(current);
                if (conns == null) continue;

                foreach (Connector c in conns)
                {
                    if (!IsValidPipingConnector(c)) continue;
                    foreach (Connector refConn in c.AllRefs)
                    {
                        if (!IsValidPipingConnector(refConn)) continue;
                        Element neighbor = refConn.Owner;
                        if (neighbor == null || visitedLocal.Contains(neighbor.Id.Value)) continue;

                        visitedLocal.Add(neighbor.Id.Value);
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return component;
        }

        private static Element FindBestSupplySource(List<Element> component)
        {
            // 1. Procura reservatório / caixa d'água / equipamento conectado
            foreach (Element elem in component)
            {
                if (elem is FamilyInstance fi)
                {
                    string fullName = RemoverAcentos($"{fi.Symbol?.FamilyName} {fi.Name}").ToLowerInvariant();
                    if (fullName.Contains("reservat") || fullName.Contains("caixa") ||
                        fullName.Contains("tank") || fullName.Contains("cisterna") ||
                        fullName.Contains("barrilete") || fullName.Contains("hidrometro") ||
                        fullName.Contains("meter"))
                    {
                        return fi;
                    }
                }
            }

            // 2. Procura extremidade aberta (ponta solta) na maior cota Z (saída da caixa d'água ou prumada)
            Element? highestOpenEnd = null;
            double highestOpenZ = double.MinValue;

            foreach (Element elem in component)
            {
                ConnectorSet? conns = GetConnectors(elem);
                if (conns == null) continue;

                int connectedCount = 0;
                int totalPipingConns = 0;
                foreach (Connector c in conns)
                {
                    if (c.Domain != Domain.DomainPiping) continue;
                    if (c.ConnectorType != ConnectorType.End && c.ConnectorType != ConnectorType.Curve && c.ConnectorType != ConnectorType.Physical)
                        continue;

                    totalPipingConns++;
                    if (IsValidPipingConnector(c))
                        connectedCount++;
                }

                if (totalPipingConns > connectedCount && connectedCount > 0)
                {
                    double z = GetElevationMeters(elem, conns);
                    if (z > highestOpenZ)
                    {
                        highestOpenZ = z;
                        highestOpenEnd = elem;
                    }
                }
            }

            if (highestOpenEnd != null)
                return highestOpenEnd;

            // 3. Fallback: elemento na cota mais elevada da rede
            return component
                .OrderByDescending(e =>
                {
                    var c = GetConnectors(e);
                    return c != null ? GetElevationMeters(e, c) : double.MinValue;
                })
                .First();
        }

        private static RevitNetworkExtractionResult ExtractFromStartElementInternal(
            Document doc,
            Element startElement,
            List<Element>? allowedElements)
        {
            var result = new RevitNetworkExtractionResult();
            ConnectorSet? startConnectors = GetConnectors(startElement);

            if (startConnectors == null || startConnectors.IsEmpty)
                throw new InvalidOperationException("O elemento inicial não possui conectores hidráulicos válidos.");

            HashSet<long>? allowedIds = allowedElements != null
                ? new HashSet<long>(allowedElements.Select(e => e.Id.Value))
                : null;

            var visitedElementIds = new HashSet<long> { startElement.Id.Value };
            double reservoirElevationM = GetElevationMeters(startElement, startConnectors);

            // Se o elemento inicial for um tubo (ex: saída do reservatório), eleva 0,50m de lâmina d'água mínima
            if (startElement is Pipe)
                reservoirElevationM += 0.50;

            var reservoirNode = new HydraulicNode
            {
                Id = Guid.NewGuid(),
                Name = startElement is Pipe ? $"ALIM-{startElement.Id.Value}" : $"RES-{startElement.Id.Value}",
                Type = HydraulicNodeType.Reservoir,
                ElevationMeters = Math.Round(reservoirElevationM, 3),
                DemandLitersPerSecond = 0,
                RelativeWeight = 0
            };

            result.Network.AddNode(reservoirNode);

            // Se o elemento inicial for um tubo, inclui ele mesmo no primeiro trecho
            if (startElement is Pipe initialPipe)
            {
                foreach (Connector conn in startConnectors)
                {
                    if (!IsValidPipingConnector(conn)) continue;
                    foreach (Connector refConn in conn.AllRefs)
                    {
                        if (!IsValidPipingConnector(refConn)) continue;
                        if (refConn.Owner.Id.Value == startElement.Id.Value) continue;
                        if (allowedIds != null && !allowedIds.Contains(refConn.Owner.Id.Value)) continue;

                        visitedElementIds.Remove(startElement.Id.Value);
                        TraceBranch(startElement, conn, reservoirNode.Id, result, visitedElementIds, allowedIds);
                        if (result.Network.Segments.Count > 0)
                            return result;
                    }
                }
            }

            foreach (Connector conn in startConnectors)
            {
                if (!IsValidPipingConnector(conn)) continue;

                foreach (Connector refConn in conn.AllRefs)
                {
                    if (!IsValidPipingConnector(refConn)) continue;
                    long ownerId = refConn.Owner.Id.Value;
                    if (ownerId == startElement.Id.Value || visitedElementIds.Contains(ownerId))
                        continue;
                    if (allowedIds != null && !allowedIds.Contains(ownerId))
                        continue;

                    TraceBranch(
                        refConn.Owner,
                        refConn,
                        reservoirNode.Id,
                        result,
                        visitedElementIds,
                        allowedIds);
                }
            }

            if (result.Network.Segments.Count == 0)
                throw new InvalidOperationException("Nenhum trecho conectado foi encontrado a partir da origem detectada.");

            return result;
        }

        private static void TraceBranch(
            Element currentElement,
            Connector entryConnector,
            Guid upstreamNodeId,
            RevitNetworkExtractionResult result,
            HashSet<long> visitedIds,
            HashSet<long>? allowedIds)
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

                var outgoingConnectors = new List<Connector>();
                foreach (Connector c in connectors)
                {
                    if (c.Id == entry.Id || !IsValidPipingConnector(c)) continue;

                    foreach (Connector refConn in c.AllRefs)
                    {
                        if (!IsValidPipingConnector(refConn)) continue;
                        long neighborId = refConn.Owner.Id.Value;
                        if (neighborId != element.Id.Value && !visitedIds.Contains(neighborId))
                        {
                            if (allowedIds == null || allowedIds.Contains(neighborId))
                            {
                                outgoingConnectors.Add(c);
                                break;
                            }
                        }
                    }
                }

                if (element is Pipe pipe)
                {
                    segmentPipes.Add(pipe);
                    Parameter? lengthParam = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                    if (lengthParam != null && lengthParam.HasValue)
                    {
                        accumulatedLengthM += UnitUtils.ConvertFromInternalUnits(lengthParam.AsDouble(), UnitTypeId.Meters);
                    }

                    if (outgoingConnectors.Count >= 2)
                    {
                        var tapNode = new HydraulicNode
                        {
                            Id = Guid.NewGuid(),
                            Name = $"DER-{pipe.Id.Value}",
                            Type = HydraulicNodeType.Junction,
                            ElevationMeters = Math.Round(GetElevationMeters(pipe, connectors), 3),
                            DemandLitersPerSecond = 0,
                            RelativeWeight = 0
                        };

                        result.Network.AddNode(tapNode);
                        RegisterSegment(result, upstreamNodeId, tapNode.Id, accumulatedLengthM, accumulatedK, segmentPipes);
                        TraceMultipleBranches(pipe.Id.Value, outgoingConnectors, tapNode.Id, result, visitedIds, allowedIds);
                        return;
                    }
                }
                else if (element is FamilyInstance fi)
                {
                    bool isFixture = fi.Category != null && fi.Category.Id.Value == (long)BuiltInCategory.OST_PlumbingFixtures;

                    if (outgoingConnectors.Count == 0 || isFixture)
                    {
                        double peso = ReadFixtureWeight(fi);
                        var consumptionNode = new HydraulicNode
                        {
                            Id = Guid.NewGuid(),
                            Name = $"PC-{fi.Id.Value}",
                            Type = HydraulicNodeType.ConsumptionPoint,
                            ElevationMeters = Math.Round(GetElevationMeters(fi, connectors), 3),
                            DemandLitersPerSecond = 0,
                            RelativeWeight = peso
                        };

                        result.Network.AddNode(consumptionNode);
                        RegisterSegment(result, upstreamNodeId, consumptionNode.Id, accumulatedLengthM, accumulatedK, segmentPipes);

                        if (outgoingConnectors.Count > 0)
                            TraceMultipleBranches(fi.Id.Value, outgoingConnectors, consumptionNode.Id, result, visitedIds, allowedIds);
                        return;
                    }

                    if (outgoingConnectors.Count >= 2)
                    {
                        var junctionNode = new HydraulicNode
                        {
                            Id = Guid.NewGuid(),
                            Name = $"NO-{fi.Id.Value}",
                            Type = HydraulicNodeType.Junction,
                            ElevationMeters = Math.Round(GetElevationMeters(fi, connectors), 3),
                            DemandLitersPerSecond = 0,
                            RelativeWeight = 0
                        };

                        result.Network.AddNode(junctionNode);
                        RegisterSegment(result, upstreamNodeId, junctionNode.Id, accumulatedLengthM, accumulatedK, segmentPipes);
                        TraceMultipleBranches(fi.Id.Value, outgoingConnectors, junctionNode.Id, result, visitedIds, allowedIds);
                        return;
                    }

                    accumulatedK += EstimateFittingK(fi);
                }

                if (outgoingConnectors.Count == 1)
                {
                    Connector? nextEntry = null;
                    foreach (Connector refConn in outgoingConnectors[0].AllRefs)
                    {
                        if (!IsValidPipingConnector(refConn)) continue;
                        long neighborId = refConn.Owner.Id.Value;
                        if (neighborId != element.Id.Value && !visitedIds.Contains(neighborId))
                        {
                            if (allowedIds == null || allowedIds.Contains(neighborId))
                            {
                                nextEntry = refConn;
                                break;
                            }
                        }
                    }

                    if (nextEntry == null) break;
                    element = nextEntry.Owner;
                    entry = nextEntry;
                }
                else
                {
                    var openEndNode = new HydraulicNode
                    {
                        Id = Guid.NewGuid(),
                        Name = $"FIM-{element.Id.Value}",
                        Type = HydraulicNodeType.ConsumptionPoint,
                        ElevationMeters = Math.Round(GetElevationMeters(element, connectors), 3),
                        DemandLitersPerSecond = 0,
                        RelativeWeight = 0.4
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
            HashSet<long> visitedIds,
            HashSet<long>? allowedIds)
        {
            foreach (Connector outConn in outgoingConnectors)
            {
                foreach (Connector nextRef in outConn.AllRefs)
                {
                    if (!IsValidPipingConnector(nextRef)) continue;
                    long neighborId = nextRef.Owner.Id.Value;
                    if (neighborId != ownerId && !visitedIds.Contains(neighborId))
                    {
                        if (allowedIds == null || allowedIds.Contains(neighborId))
                        {
                            TraceBranch(nextRef.Owner, nextRef, junctionNodeId, result, visitedIds, allowedIds);
                        }
                    }
                }
            }
        }

        private static bool IsValidPipingConnector(Connector conn)
        {
            if (conn == null) return false;

            if (conn.ConnectorType != ConnectorType.End &&
                conn.ConnectorType != ConnectorType.Curve &&
                conn.ConnectorType != ConnectorType.Physical)
                return false;

            if (conn.Domain != Domain.DomainPiping) return false;

            return conn.IsConnected;
        }

        private static void RegisterSegment(
            RevitNetworkExtractionResult result,
            Guid startNodeId,
            Guid endNodeId,
            double lengthMeters,
            double totalK,
            List<Pipe> pipes)
        {
            if (startNodeId == endNodeId) return;

            var segmentId = Guid.NewGuid();
            var segment = new HydraulicPipeSegment
            {
                Id = segmentId,
                Name = $"T-{result.Network.Segments.Count + 1}",
                StartNodeId = startNodeId,
                EndNodeId = endNodeId,
                LengthMeters = Math.Max(0.10, Math.Round(lengthMeters, 3)),
                InternalDiameterMm = null,
                AbsoluteRoughnessMm = 0.001,
                TotalLossCoefficientK = Math.Round(totalK, 2)
            };

            double originalDiameterMm = 0;
            if (pipes.Count > 0)
            {
                var diametros = new List<double>();
                foreach (var p in pipes)
                {
                    try
                    {
                        Parameter? dParam = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (dParam != null && dParam.HasValue)
                        {
                            diametros.Add(UnitUtils.ConvertFromInternalUnits(dParam.AsDouble(), UnitTypeId.Millimeters));
                        }
                    }
                    catch { }
                }
                if (diametros.Count > 0)
                    originalDiameterMm = Math.Round(diametros.Average(), 2);
            }

            result.Network.AddSegment(segment);
            result.PipesBySegmentId[segmentId] = new List<Pipe>(pipes);
            result.OriginalDiametersMmBySegmentId[segmentId] = originalDiameterMm;
        }

        private static ConnectorSet? GetConnectors(Element element)
        {
            try
            {
                if (element is MEPCurve curve) return curve.ConnectorManager?.Connectors;
                if (element is FamilyInstance fi) return fi.MEPModel?.ConnectorManager?.Connectors;
            }
            catch { }
            return null;
        }

        private static double GetElevationMeters(Element element, ConnectorSet connectors)
        {
            foreach (Connector c in connectors)
            {
                if ((c.ConnectorType == ConnectorType.End ||
                     c.ConnectorType == ConnectorType.Curve ||
                     c.ConnectorType == ConnectorType.Physical) &&
                    c.Domain == Domain.DomainPiping)
                {
                    return UnitUtils.ConvertFromInternalUnits(c.Origin.Z, UnitTypeId.Meters);
                }
            }

            BoundingBoxXYZ? bb = element.get_BoundingBox(null);
            if (bb != null)
                return UnitUtils.ConvertFromInternalUnits((bb.Min.Z + bb.Max.Z) / 2.0, UnitTypeId.Meters);

            return 0;
        }

        // Tabela automática de Pesos Relativos conforme ABNT NBR 5626
        private static double ReadFixtureWeight(FamilyInstance fi)
        {
            try
            {
                Parameter? weightParam = fi.LookupParameter("BahrHidro_Peso") ?? fi.LookupParameter("Peso");
                if (weightParam != null && weightParam.StorageType == StorageType.Double && weightParam.AsDouble() > 0)
                    return weightParam.AsDouble();
            }
            catch { }

            string nome = RemoverAcentos($"{fi.Symbol?.FamilyName} {fi.Name}").ToLowerInvariant();

            if (nome.Contains("valvula") && (nome.Contains("bacia") || nome.Contains("descarga") || nome.Contains("flush")))
                return 32.0;
            if (nome.Contains("bacia") || nome.Contains("vaso") || nome.Contains("sanitaria") || nome.Contains("toilet") || nome.Contains("closet") || nome.Contains("wc"))
                return 0.3;
            if (nome.Contains("banheira") || nome.Contains("tub") || nome.Contains("hidro"))
                return 1.0;
            if (nome.Contains("maquina") || nome.Contains("lavadora") || nome.Contains("washing") || nome.Contains("dishwasher") || nome.Contains("laundry"))
                return 1.0;
            if (nome.Contains("cozinha") || nome.Contains("kitchen") || nome.Contains("gourmet") || nome.Contains("pia"))
                return 0.7;
            if (nome.Contains("tanque") || nome.Contains("utility"))
                return 0.7;
            if (nome.Contains("chuveiro") || nome.Contains("ducha") || nome.Contains("shower"))
                return 0.4;
            if (nome.Contains("jardim") || nome.Contains("externa") || nome.Contains("hose") || nome.Contains("bibb"))
                return 0.4;
            if (nome.Contains("lavatorio") || nome.Contains("cuba") || nome.Contains("lavabo") || nome.Contains("vanity"))
                return 0.3;
            if (nome.Contains("mictorio") || nome.Contains("urinal"))
                return 0.3;
            if (nome.Contains("bide") || nome.Contains("bidet"))
                return 0.1;

            return 0.4;
        }

        // Estimativa automática de coeficiente K conforme ABNT NBR 5626
        private static double EstimateFittingK(FamilyInstance fi)
        {
            string name = RemoverAcentos($"{fi.Symbol?.FamilyName} {fi.Name}").ToLowerInvariant();
            if (name.Contains("esfera") || name.Contains("ball")) return 0.1;
            if (name.Contains("gaveta") || name.Contains("gate")) return 0.2;
            if (name.Contains("45")) return 0.2;
            if (name.Contains("curva") && name.Contains("90")) return 0.4;
            if (name.Contains("joelho") || name.Contains("elbow")) return 0.9;
            if (name.Contains("te") || name.Contains("tee")) return 0.9;
            if (name.Contains("retencao") || name.Contains("check")) return 2.5;
            if (name.Contains("pressao") || name.Contains("globo") || name.Contains("globe")) return 10.0;
            return 0.5;
        }

        private static string RemoverAcentos(string texto)
        {
            var normalized = texto.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
