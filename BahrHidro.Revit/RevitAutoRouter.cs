using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitAutoRouter
    {
        private const double MinSegmentFeet = 0.065; // ~2 cm
        private const double ConnectorToleranceFeet = 0.03; // ~9 mm

        public static RevitTemplateRoutingOptions GetTemplateOptions(Document doc)
        {
            var pipeTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .OrderBy(p => p.Name)
                .Select(p => new RevitLookupItem(p.Id.Value, p.Name))
                .ToList();

            var systemTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .OrderByDescending(s => s.SystemClassification == MEPSystemClassification.DomesticColdWater)
                .ThenBy(s => s.Name)
                .Select(s => new RevitLookupItem(s.Id.Value, s.Name))
                .ToList();

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new RevitLevelLookupItem(
                    l.Id.Value,
                    l.Name,
                    Math.Round(UnitUtils.ConvertFromInternalUnits(l.Elevation, UnitTypeId.Meters), 3)))
                .ToList();

            return new RevitTemplateRoutingOptions(pipeTypes, systemTypes, levels);
        }

        public static List<DetectedRoutingPoint> DetectAllPoints(Document doc, long? levelId)
        {
            var rawPoints = new List<DetectedRoutingPoint>();

            Level? baseLevel = levelId.HasValue
                ? doc.GetElement(new ElementId(levelId.Value)) as Level
                : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();

            double baseLevelZ = baseLevel != null
                ? UnitUtils.ConvertFromInternalUnits(baseLevel.Elevation, UnitTypeId.Meters)
                : 0.0;

            CollectFromDocumentElements(doc, Transform.Identity, "Revit", baseLevelZ, isHostDocument: true, rawPoints);

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                Transform linkTransform = link.GetTotalTransform();
                string linkLabel = link.Name.Contains(".ifc", StringComparison.OrdinalIgnoreCase) ? "IFC" : "Vínculo RVT";
                CollectFromDocumentElements(linkDoc, linkTransform, linkLabel, baseLevelZ, isHostDocument: false, rawPoints);
            }

            var imports = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            foreach (var imp in imports)
            {
                CollectFromDwgImport(doc, imp, baseLevelZ, rawPoints);
            }

            var filteredByLevel = levelId.HasValue
                ? rawPoints.Where(p => p.BaseZ >= baseLevelZ - 0.60 && p.BaseZ <= baseLevelZ + 3.20).ToList()
                : rawPoints;

            if (filteredByLevel.Count == 0 && rawPoints.Count > 0)
                filteredByLevel = rawPoints;

            var deduplicated = new List<DetectedRoutingPoint>();
            foreach (var pt in filteredByLevel)
            {
                bool duplicate = deduplicated.Any(existing =>
                    existing.FixtureCategory == pt.FixtureCategory &&
                    Math.Sqrt(Math.Pow(existing.X - pt.X, 2) + Math.Pow(existing.Y - pt.Y, 2)) < 0.25);

                if (!duplicate)
                {
                    int index = deduplicated.Count(x => x.FixtureCategory == pt.FixtureCategory) + 1;
                    pt.Name = $"{pt.FixtureCategory} {index:D2}";
                    deduplicated.Add(pt);
                }
            }

            return deduplicated;
        }

        public static DetectedRoutingPoint? PickManualPoint(UIDocument uidoc, Window ownerWindow, string fixtureCategory, long? levelId)
        {
            Document doc = uidoc.Document;
            Level? baseLevel = levelId.HasValue
                ? doc.GetElement(new ElementId(levelId.Value)) as Level
                : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();

            double baseZ = baseLevel != null
                ? Math.Round(UnitUtils.ConvertFromInternalUnits(baseLevel.Elevation, UnitTypeId.Meters), 3)
                : 0.0;

            var spec = ClassifyFixture(fixtureCategory) ?? ("Ponto Hidráulico", 0.60, 0.4);

            try
            {
                ownerWindow.Hide();

                using (Transaction tx = new Transaction(doc, "Bahr Hidro - Plano de Trabalho"))
                {
                    tx.Start();
                    if (doc.ActiveView.SketchPlane == null)
                    {
                        double zFeet = baseLevel?.Elevation ?? 0.0;
                        Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, zFeet));
                        doc.ActiveView.SketchPlane = SketchPlane.Create(doc, plane);
                    }
                    tx.Commit();
                }

                XYZ picked = uidoc.Selection.PickPoint($"Clique no local onde fica '{spec.Category}' no DWG/3D:");

                return new DetectedRoutingPoint
                {
                    IsSelected = true,
                    Name = $"{spec.Category} (Manual)",
                    FixtureCategory = spec.Category,
                    SourceType = "Manual",
                    X = Math.Round(UnitUtils.ConvertFromInternalUnits(picked.X, UnitTypeId.Meters), 3),
                    Y = Math.Round(UnitUtils.ConvertFromInternalUnits(picked.Y, UnitTypeId.Meters), 3),
                    BaseZ = baseZ,
                    PointHeightMeters = spec.DefaultHeightM,
                    OutwardDirX = 0,
                    OutwardDirY = 1,
                    Weight = spec.Weight
                };
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
            finally
            {
                ownerWindow.ShowDialog();
            }
        }

        public static RevitAutoRoutingResult ExecuteRouting(Document doc, RevitAutoRoutingRequest request)
        {
            if (string.Equals(request.DisciplineMode, "HotWater", StringComparison.OrdinalIgnoreCase))
                return ExecuteHotWaterRouting(doc, request);

            if (string.Equals(request.DisciplineMode, "SanitarySewer", StringComparison.OrdinalIgnoreCase))
                return ExecuteSanitarySewerRouting(doc, request);

            if (string.Equals(request.DisciplineMode, "Ventilation", StringComparison.OrdinalIgnoreCase))
                return ExecuteVentilationRouting(doc, request);

            return ExecuteColdWaterRouting(doc, request);
        }

        // =====================================================================
        // 1. ROTEAMENTO DE ÁGUA FRIA COM DESVIO AUTOMÁTICO DE PERFIS STEEL FRAME
        // =====================================================================
        private static RevitAutoRoutingResult ExecuteColdWaterRouting(Document doc, RevitAutoRoutingRequest request)
        {
            var logs = new List<string>();
            var activePoints = request.Points.Where(p => p.IsSelected).ToList();

            if (activePoints.Count == 0)
                throw new InvalidOperationException("Nenhum ponto selecionado na lista. Detecte os pontos (IFC/DWG) ou adicione pontos manualmente antes de traçar.");

            var pipeTypeId = new ElementId(request.PipeTypeId);
            var systemTypeId = new ElementId(request.PipingSystemTypeId);

            if (doc.GetElement(pipeTypeId) is not PipeType)
                throw new InvalidOperationException("Selecione um Tipo de Tubo válido do template.");
            if (doc.GetElement(systemTypeId) is not PipingSystemType)
                throw new InvalidOperationException("Selecione um Sistema de Tubulação válido do template.");

            Level? baseLevel = request.LevelId.HasValue
                ? doc.GetElement(new ElementId(request.LevelId.Value)) as Level
                : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();

            if (baseLevel == null)
                throw new InvalidOperationException("Nenhum Nível (Level) encontrado no projeto Revit.");

            // Coleta perfis metálicos (Steel Frame) do projeto e dos vínculos RVT/IFC
            var steelObstacles = request.AvoidSteelFrameClashes
                ? CollectSteelFrameBoundingBoxes(doc)
                : new List<WorldBox>();

            if (request.AvoidSteelFrameClashes)
                logs.Add($"Proteção Steel Frame ativa: {steelObstacles.Count} perfil(is) metálico(s) mapeado(s) no modelo/vínculos.");

            double offsetFeet = UnitUtils.ConvertToInternalUnits(request.TrunkOffsetMeters, UnitTypeId.Meters);
            double trunkZ = baseLevel.Elevation + offsetFeet;
            double diameterFeet = UnitUtils.ConvertToInternalUnits(request.InitialDiameterMm, UnitTypeId.Millimeters);
            double clearanceFeet = (diameterFeet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters); // Folga 3,5cm do aço

            var supplyInfo = FindSupplyConnector(doc);

            var basePtsFeet = activePoints.Select(p => new XYZ(
                UnitUtils.ConvertToInternalUnits(p.X, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(p.Y, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(p.BaseZ + p.PointHeightMeters, UnitTypeId.Meters))).ToList();

            double minX = basePtsFeet.Min(p => p.X);
            double maxX = basePtsFeet.Max(p => p.X);
            double minY = basePtsFeet.Min(p => p.Y);
            double maxY = basePtsFeet.Max(p => p.Y);

            bool trunkAlongX = (maxX - minX) >= (maxY - minY);
            double trunkConstCoord = trunkAlongX
                ? (supplyInfo.HasValue ? supplyInfo.Value.Origin.Y : basePtsFeet.Average(p => p.Y))
                : (supplyInfo.HasValue ? supplyInfo.Value.Origin.X : basePtsFeet.Average(p => p.X));

            int pipesCreated = 0;
            int fittingsCreated = 0;
            int steelClashesAvoided = 0;
            double totalLengthFeet = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Traçado Água Fria 3D (Steel Frame)"))
            {
                tx.Start();

                var trunkJunctions = new List<TrunkJunction>();

                for (int i = 0; i < activePoints.Count; i++)
                {
                    var pt = activePoints[i];
                    XYZ dirXY = new XYZ(pt.OutwardDirX, pt.OutwardDirY, 0);
                    dirXY = dirXY.GetLength() < 0.05 ? XYZ.BasisY : dirXY.Normalize();

                    double origXFeet = UnitUtils.ConvertToInternalUnits(pt.X, UnitTypeId.Meters);
                    double origYFeet = UnitUtils.ConvertToInternalUnits(pt.Y, UnitTypeId.Meters);
                    double zPointFeet = UnitUtils.ConvertToInternalUnits(pt.BaseZ + pt.PointHeightMeters, UnitTypeId.Meters);

                    // Verifica se a descida/subida vertical bate em algum montante Steel Frame
                    double dropXFeet = origXFeet;
                    double dropYFeet = origYFeet;
                    bool wasShifted = false;

                    if (request.AvoidSteelFrameClashes && steelObstacles.Count > 0)
                    {
                        double zLow = Math.Min(zPointFeet, trunkZ);
                        double zHigh = Math.Max(zPointFeet, trunkZ);
                        wasShifted = FindClashFreeVerticalDrop(
                            origXFeet, origYFeet, zLow, zHigh, clearanceFeet, trunkAlongX, steelObstacles,
                            out dropXFeet, out dropYFeet);

                        if (wasShifted)
                        {
                            steelClashesAvoided++;
                            double shiftCm = Math.Round(UnitUtils.ConvertFromInternalUnits(
                                Math.Sqrt(Math.Pow(dropXFeet - origXFeet, 2) + Math.Pow(dropYFeet - origYFeet, 2)),
                                UnitTypeId.Centimeters), 1);
                            logs.Add($"[Steel Frame] '{pt.Name}': prumada deslocada {shiftCm:F1} cm para vão livre (zero colisão/sem furação).");
                        }
                    }

                    XYZ pWallFixture = new XYZ(origXFeet, origYFeet, zPointFeet);
                    XYZ pStub = pWallFixture + dirXY * UnitUtils.ConvertToInternalUnits(0.08, UnitTypeId.Meters);
                    XYZ pWallDropBottom = new XYZ(dropXFeet, dropYFeet, zPointFeet);
                    XYZ pWallDropTop = new XYZ(dropXFeet, dropYFeet, trunkZ);

                    double rawCoord = trunkAlongX ? pWallDropTop.X : pWallDropTop.Y;
                    double adjustedCoord = EnsureUniqueTrunkCoord(rawCoord, trunkJunctions.Select(j => j.CoordAlongTrunk).ToList());

                    XYZ pWallTopAdj = trunkAlongX
                        ? new XYZ(adjustedCoord, pWallDropTop.Y, trunkZ)
                        : new XYZ(pWallDropTop.X, adjustedCoord, trunkZ);

                    XYZ pTrunk = trunkAlongX
                        ? new XYZ(adjustedCoord, trunkConstCoord, trunkZ)
                        : new XYZ(trunkConstCoord, adjustedCoord, trunkZ);

                    var branchPoints = BuildCleanPolyline(new[] { pStub, pWallFixture, pWallDropBottom, pWallDropTop, pWallTopAdj, pTrunk });
                    var branchPipes = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, diameterFeet, branchPoints, ref fittingsCreated);

                    if (branchPipes.Count > 0)
                    {
                        pipesCreated += branchPipes.Count;
                        totalLengthFeet += branchPipes.Sum(GetPipeLengthFeet);

                        SetPipeComment(branchPipes[0], $"BahrHidro|{pt.Name}|{pt.Weight.ToString("F2", CultureInfo.InvariantCulture)}");

                        if (pt.HostElementId.HasValue && doc.GetElement(new ElementId(pt.HostElementId.Value)) is FamilyInstance hostFi)
                        {
                            Connector? cwConn = FindColdWaterConnector(hostFi);
                            Connector? startConn = GetClosestConnector(branchPipes[0], pWallFixture);
                            if (cwConn != null && !cwConn.IsConnected && startConn != null && !startConn.IsConnected)
                            {
                                try { startConn.ConnectTo(cwConn); } catch { }
                            }
                        }

                        Pipe lastBranchPipe = branchPipes[^1];
                        Connector? trunkEndConn = GetClosestConnector(lastBranchPipe, pTrunk);
                        trunkJunctions.Add(new TrunkJunction(adjustedCoord, pTrunk, trunkEndConn, false));

                        logs.Add($"Traçado AF: {pt.Name} [{pt.SourceType}] (H = {pt.PointHeightMeters:F2} m | ΣP = {pt.Weight:F2}) → {branchPipes.Count} tubo(s).");
                    }
                }

                if (supplyInfo.HasValue)
                {
                    XYZ s0 = supplyInfo.Value.Origin;
                    XYZ s1 = s0 + supplyInfo.Value.OutwardDirXY * UnitUtils.ConvertToInternalUnits(0.20, UnitTypeId.Meters);
                    XYZ s2 = new XYZ(s1.X, s1.Y, trunkZ);
                    XYZ sTrunk = trunkAlongX
                        ? new XYZ(s2.X, trunkConstCoord, trunkZ)
                        : new XYZ(trunkConstCoord, s2.Y, trunkZ);

                    double supplyCoord = trunkAlongX ? sTrunk.X : sTrunk.Y;
                    var supplyPoints = BuildCleanPolyline(new[] { s0, s1, s2, sTrunk });
                    var supplyPipes = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, diameterFeet, supplyPoints, ref fittingsCreated);

                    if (supplyPipes.Count > 0)
                    {
                        pipesCreated += supplyPipes.Count;
                        totalLengthFeet += supplyPipes.Sum(GetPipeLengthFeet);

                        Connector? sConn = GetClosestConnector(supplyPipes[0], s0);
                        if (sConn != null && !sConn.IsConnected && !supplyInfo.Value.Connector.IsConnected)
                        {
                            try { sConn.ConnectTo(supplyInfo.Value.Connector); } catch { }
                        }

                        Connector? trunkEntryConn = GetClosestConnector(supplyPipes[^1], sTrunk);
                        trunkJunctions.Add(new TrunkJunction(supplyCoord, sTrunk, trunkEntryConn, true));
                        logs.Insert(0, $"Alimentação conectada em '{supplyInfo.Value.Name}'.");
                    }
                }
                else if (trunkJunctions.Count > 0)
                {
                    var orderedPreview = trunkJunctions.OrderBy(j => j.CoordAlongTrunk).ToList();
                    double firstCoord = orderedPreview[0].CoordAlongTrunk - UnitUtils.ConvertToInternalUnits(0.80, UnitTypeId.Meters);
                    XYZ pSupplyStart = trunkAlongX
                        ? new XYZ(firstCoord, trunkConstCoord, trunkZ)
                        : new XYZ(trunkConstCoord, firstCoord, trunkZ);

                    trunkJunctions.Add(new TrunkJunction(firstCoord, pSupplyStart, null, true));
                    logs.Insert(0, "Origem da linha tronco criada a 0,80 m do primeiro ramal (pronta para ligar ao barrilete/prumada).");
                }

                ConnectTrunkSegments(doc, systemTypeId, pipeTypeId, baseLevel.Id, diameterFeet, trunkJunctions, ref pipesCreated, ref fittingsCreated, ref totalLengthFeet);
                tx.Commit();
            }

            double totalLengthMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalLengthFeet, UnitTypeId.Meters), 2);
            string planoLabel = request.RouteFromAbove ? "Por Cima (Entreforro)" : "Por Baixo (Piso)";
            string summary = $"Água Fria 3D ({planoLabel}): {activePoints.Count} ponto(s), {pipesCreated} tubo(s) ({totalLengthMeters:F2} m), {fittingsCreated} conexão(ões) e {steelClashesAvoided} desvio(s) Steel Frame.";

            return new RevitAutoRoutingResult(activePoints.Count, pipesCreated, fittingsCreated, steelClashesAvoided, totalLengthMeters, summary, logs);
        }

        // =====================================================================
        // 2. ROTEAMENTO DE ESGOTO SANITÁRIO 3D COM DECLIVIDADE (ABNT NBR 8160)
        // =====================================================================
        private static RevitAutoRoutingResult ExecuteSanitarySewerRouting(Document doc, RevitAutoRoutingRequest request)
        {
            var logs = new List<string>();
            var activePoints = request.Points.Where(p => p.IsSelected).ToList();

            if (activePoints.Count == 0)
                throw new InvalidOperationException("Nenhum ponto selecionado na lista. Detecte os pontos (IFC/DWG) antes de traçar o esgoto.");

            var pipeTypeId = new ElementId(request.PipeTypeId);
            var systemTypeId = new ElementId(request.PipingSystemTypeId);

            if (doc.GetElement(pipeTypeId) is not PipeType)
                throw new InvalidOperationException("Selecione um Tipo de Tubo de Esgoto válido do template.");
            if (doc.GetElement(systemTypeId) is not PipingSystemType)
                throw new InvalidOperationException("Selecione um Sistema Sanitário válido do template.");

            Level? baseLevel = request.LevelId.HasValue
                ? doc.GetElement(new ElementId(request.LevelId.Value)) as Level
                : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();

            if (baseLevel == null)
                throw new InvalidOperationException("Nenhum Nível (Level) encontrado no projeto Revit.");

            var steelObstacles = request.AvoidSteelFrameClashes
                ? CollectSteelFrameBoundingBoxes(doc)
                : new List<WorldBox>();

            // Subcoletor principal DN 100 mm abaixo do piso (padrão -0,22 m do nível)
            double collectorStartOffsetM = request.TrunkOffsetMeters < 0 ? request.TrunkOffsetMeters : -0.22;
            double collectorStartZ = baseLevel.Elevation + UnitUtils.ConvertToInternalUnits(collectorStartOffsetM, UnitTypeId.Meters);

            double dia100Feet = UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters);
            double dia50Feet = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);

            var ptsFeet = activePoints.Select(p => new XYZ(
                UnitUtils.ConvertToInternalUnits(p.X, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(p.Y, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(p.BaseZ, UnitTypeId.Meters))).ToList();

            double minX = ptsFeet.Min(p => p.X);
            double maxX = ptsFeet.Max(p => p.X);
            double minY = ptsFeet.Min(p => p.Y);
            double maxY = ptsFeet.Max(p => p.Y);

            bool trunkAlongX = (maxX - minX) >= (maxY - minY);
            double trunkConstCoord = trunkAlongX
                ? ptsFeet.Average(p => p.Y) + UnitUtils.ConvertToInternalUnits(0.35, UnitTypeId.Meters)
                : ptsFeet.Average(p => p.X) + UnitUtils.ConvertToInternalUnits(0.35, UnitTypeId.Meters);

            // Ordena pontos do montante (início do subcoletor) para jusante (tubo de queda)
            var orderedPoints = activePoints
                .OrderBy(p => trunkAlongX ? p.X : p.Y)
                .ToList();

            double firstAxisCoord = trunkAlongX
                ? UnitUtils.ConvertToInternalUnits(orderedPoints[0].X, UnitTypeId.Meters)
                : UnitUtils.ConvertToInternalUnits(orderedPoints[0].Y, UnitTypeId.Meters);

            int pipesCreated = 0;
            int fittingsCreated = 0;
            int steelClashesAvoided = 0;
            double totalLengthFeet = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Traçado Esgoto 3D (NBR 8160)"))
            {
                tx.Start();

                var collectorNodes = new List<(double AxisCoord, XYZ Point, Connector? BranchConn)>();

                foreach (var pt in orderedPoints)
                {
                    bool isToilet = pt.FixtureCategory.Contains("Vaso", StringComparison.OrdinalIgnoreCase) ||
                                    pt.FixtureCategory.Contains("Bacia", StringComparison.OrdinalIgnoreCase);

                    double branchDiaFeet = isToilet ? dia100Feet : dia50Feet;
                    double slope = isToilet ? 0.01 : 0.02; // 1% para DN 100, 2% para DN 50 (NBR 8160)
                    string dnLabel = isToilet ? "DN 100 (i=1%)" : "DN 50 (i=2%)";

                    double origXFeet = UnitUtils.ConvertToInternalUnits(pt.X, UnitTypeId.Meters);
                    double origYFeet = UnitUtils.ConvertToInternalUnits(pt.Y, UnitTypeId.Meters);
                    double floorZFeet = UnitUtils.ConvertToInternalUnits(pt.BaseZ, UnitTypeId.Meters);

                    // Ponto de saída no piso (vaso sai no piso 0,0m; pias/lavatórios descem da parede a 0,50m até -0,10m)
                    double startZFeet = isToilet
                        ? floorZFeet
                        : floorZFeet + UnitUtils.ConvertToInternalUnits(0.50, UnitTypeId.Meters);

                    double subFloorZFeet = floorZFeet - UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters);

                    double dropXFeet = origXFeet;
                    double dropYFeet = origYFeet;

                    if (!isToilet && request.AvoidSteelFrameClashes && steelObstacles.Count > 0)
                    {
                        double clearance = (branchDiaFeet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);
                        bool shifted = FindClashFreeVerticalDrop(
                            origXFeet, origYFeet, subFloorZFeet, startZFeet, clearance, trunkAlongX, steelObstacles,
                            out dropXFeet, out dropYFeet);
                        if (shifted)
                        {
                            steelClashesAvoided++;
                            logs.Add($"[Steel Frame] Descida de esgoto '{pt.Name}' deslocada para vão livre entre montantes.");
                        }
                    }

                    double rawCoord = trunkAlongX ? dropXFeet : dropYFeet;
                    double adjCoord = EnsureUniqueTrunkCoord(rawCoord, collectorNodes.Select(n => n.AxisCoord).ToList());

                    double distAlongCollector = Math.Max(0, adjCoord - firstAxisCoord);
                    double collectorZAtJunction = collectorStartZ - (distAlongCollector * 0.01); // Subcoletor DN 100 com 1% de queda

                    XYZ pCollector = trunkAlongX
                        ? new XYZ(adjCoord, trunkConstCoord, collectorZAtJunction)
                        : new XYZ(trunkConstCoord, adjCoord, collectorZAtJunction);

                    XYZ pSubFloor = new XYZ(dropXFeet, dropYFeet, subFloorZFeet);
                    double horizDist = Math.Sqrt(Math.Pow(pCollector.X - pSubFloor.X, 2) + Math.Pow(pCollector.Y - pSubFloor.Y, 2));
                    double slopedEndZ = Math.Max(collectorZAtJunction + UnitUtils.ConvertToInternalUnits(0.03, UnitTypeId.Meters), subFloorZFeet - (horizDist * slope));
                    XYZ pBeforeDrop = new XYZ(pCollector.X, pCollector.Y, slopedEndZ);

                    var rawPts = new List<XYZ>();
                    if (!isToilet)
                        rawPts.Add(new XYZ(origXFeet, origYFeet, startZFeet));

                    rawPts.Add(pSubFloor);
                    rawPts.Add(pBeforeDrop);
                    rawPts.Add(pCollector);

                    var branchPoints = BuildCleanPolyline(rawPts);
                    var branchPipes = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, branchDiaFeet, branchPoints, ref fittingsCreated);

                    if (branchPipes.Count > 0)
                    {
                        pipesCreated += branchPipes.Count;
                        totalLengthFeet += branchPipes.Sum(GetPipeLengthFeet);

                        Connector? endConn = GetClosestConnector(branchPipes[^1], pCollector);
                        collectorNodes.Add((adjCoord, pCollector, endConn));
                        logs.Add($"Ramal Esgoto: {pt.Name} → {dnLabel} ({branchPipes.Count} trecho(s)).");
                    }
                }

                // Cria o Subcoletor Principal DN 100 (declividade 1%) + Tubo de Queda na extremidade final
                var orderedCollector = collectorNodes.OrderBy(n => n.AxisCoord).ToList();
                if (orderedCollector.Count > 0)
                {
                    var lastNode = orderedCollector[^1];
                    double outletCoord = lastNode.AxisCoord + UnitUtils.ConvertToInternalUnits(0.60, UnitTypeId.Meters);
                    double outletZ = lastNode.Point.Z - UnitUtils.ConvertToInternalUnits(0.60 * 0.01, UnitTypeId.Meters);
                    XYZ pOutletTop = trunkAlongX
                        ? new XYZ(outletCoord, trunkConstCoord, outletZ)
                        : new XYZ(trunkConstCoord, outletCoord, outletZ);

                    orderedCollector.Add((outletCoord, pOutletTop, null));

                    var collectorPipes = new List<Pipe>();
                    for (int i = 0; i < orderedCollector.Count - 1; i++)
                    {
                        XYZ a = orderedCollector[i].Point;
                        XYZ b = orderedCollector[i + 1].Point;
                        if (a.DistanceTo(b) < MinSegmentFeet) continue;

                        Pipe cPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, baseLevel.Id, a, b);
                        SetPipeDiameter(cPipe, dia100Feet);
                        collectorPipes.Add(cPipe);
                        pipesCreated++;
                        totalLengthFeet += GetPipeLengthFeet(cPipe);
                    }

                    // Tubo de Queda vertical DN 100 na saída
                    XYZ pDropDown = new XYZ(pOutletTop.X, pOutletTop.Y, pOutletTop.Z - UnitUtils.ConvertToInternalUnits(0.80, UnitTypeId.Meters));
                    Pipe dropPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, baseLevel.Id, pOutletTop, pDropDown);
                    SetPipeDiameter(dropPipe, dia100Feet);
                    pipesCreated++;
                    totalLengthFeet += GetPipeLengthFeet(dropPipe);

                    if (collectorPipes.Count > 0)
                    {
                        Connector? c1 = GetClosestConnector(collectorPipes[^1], pOutletTop);
                        Connector? c2 = GetClosestConnector(dropPipe, pOutletTop);
                        if (c1 != null && c2 != null)
                        {
                            try { doc.Create.NewElbowFitting(c1, c2); fittingsCreated++; } catch { }
                        }
                    }

                    for (int i = 0; i < orderedCollector.Count; i++)
                    {
                        var node = orderedCollector[i];
                        if (node.BranchConn == null) continue;

                        var touching = new List<Connector>();
                        foreach (var cp in collectorPipes)
                        {
                            Connector? c = GetClosestConnector(cp, node.Point);
                            if (c != null && c.Origin.DistanceTo(node.Point) <= ConnectorToleranceFeet)
                                touching.Add(c);
                        }

                        if (touching.Count == 1)
                        {
                            try { doc.Create.NewElbowFitting(touching[0], node.BranchConn); fittingsCreated++; } catch { }
                        }
                        else if (touching.Count >= 2)
                        {
                            try { doc.Create.NewTeeFitting(touching[0], touching[1], node.BranchConn); fittingsCreated++; } catch { }
                        }
                    }

                    logs.Insert(0, "Subcoletor Principal DN 100 mm criado com declividade contínua de 1,0% e Tubo de Queda na saída.");
                }

                tx.Commit();
            }

            double totalLengthMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalLengthFeet, UnitTypeId.Meters), 2);
            string summary = $"Esgoto 3D com Declividade (NBR 8160): {activePoints.Count} aparelho(s), {pipesCreated} tubo(s) ({totalLengthMeters:F2} m), {fittingsCreated} conexão(ões) e {steelClashesAvoided} desvio(s) Steel Frame.";

            return new RevitAutoRoutingResult(activePoints.Count, pipesCreated, fittingsCreated, steelClashesAvoided, totalLengthMeters, summary, logs);
        }


        // =====================================================================
        // 3. ROTEAMENTO DE ÁGUA QUENTE 3D (ABNT NBR 7198 — AFASTADO DA ÁGUA FRIA)
        // =====================================================================
        private static RevitAutoRoutingResult ExecuteHotWaterRouting(Document doc, RevitAutoRoutingRequest request)
        {
            var logs = new List<string>();
            var hotCategories = new[] { "Chuveiro", "Lavatório", "Pia de Cozinha", "Banheira", "Bidê" };

            var hotPoints = request.Points
                .Where(p => p.IsSelected && hotCategories.Any(c => p.FixtureCategory.Contains(c, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new DetectedRoutingPoint
                {
                    IsSelected = true,
                    Name = $"{p.Name} (AQ)",
                    FixtureCategory = p.FixtureCategory,
                    SourceType = p.SourceType,
                    // Afasta 15 cm lateralmente em relação ao ponto de Água Fria para evitar colisão paralela
                    X = Math.Round(p.X + (Math.Abs(p.OutwardDirY) >= Math.Abs(p.OutwardDirX) ? 0.15 : 0.0), 3),
                    Y = Math.Round(p.Y + (Math.Abs(p.OutwardDirY) >= Math.Abs(p.OutwardDirX) ? 0.0 : 0.15), 3),
                    BaseZ = p.BaseZ,
                    PointHeightMeters = p.PointHeightMeters,
                    OutwardDirX = p.OutwardDirX,
                    OutwardDirY = p.OutwardDirY,
                    Weight = p.Weight,
                    HostElementId = null
                })
                .ToList();

            if (hotPoints.Count == 0)
                throw new InvalidOperationException("Nenhum aparelho consumidor de Água Quente (Chuveiro, Lavatório, Pia ou Banheira) foi encontrado na lista.");

            logs.Add($"Filtro NBR 7198: {hotPoints.Count} ponto(s) de Água Quente selecionado(s) com afastamento lateral de 15 cm da rede fria.");

            var hwRequest = request with
            {
                DisciplineMode = "ColdWater",
                Points = hotPoints
            };

            var baseRes = ExecuteColdWaterRouting(doc, hwRequest);
            logs.AddRange(baseRes.Logs);

            string planoLabel = request.RouteFromAbove ? "Por Cima (Entreforro)" : "Por Baixo (Piso)";
            string summary = $"Água Quente 3D ({planoLabel}): {hotPoints.Count} ponto(s), {baseRes.PipesCreated} tubo(s) ({baseRes.TotalLengthMeters:F2} m), {baseRes.FittingsCreated} conexão(ões) e {baseRes.SteelClashesAvoided} desvio(s) Steel Frame.";

            return new RevitAutoRoutingResult(
                hotPoints.Count,
                baseRes.PipesCreated,
                baseRes.FittingsCreated,
                baseRes.SteelClashesAvoided,
                baseRes.TotalLengthMeters,
                summary,
                logs);
        }

        // =====================================================================
        // 4. ROTEAMENTO DE VENTILAÇÃO SANITÁRIA 3D (ABNT NBR 8160 — DN 50 MM)
        // =====================================================================
        private static RevitAutoRoutingResult ExecuteVentilationRouting(Document doc, RevitAutoRoutingRequest request)
        {
            var logs = new List<string>();
            var activePoints = request.Points.Where(p => p.IsSelected).ToList();

            if (activePoints.Count == 0)
                throw new InvalidOperationException("Nenhum ponto selecionado. Detecte os aparelhos (IFC/DWG) antes de gerar a ventilação.");

            var pipeTypeId = new ElementId(request.PipeTypeId);
            var systemTypeId = new ElementId(request.PipingSystemTypeId);

            if (doc.GetElement(pipeTypeId) is not PipeType)
                throw new InvalidOperationException("Selecione um Tipo de Tubo válido do template.");
            if (doc.GetElement(systemTypeId) is not PipingSystemType)
                throw new InvalidOperationException("Selecione um Sistema de Ventilação/Sanitário válido do template.");

            Level? baseLevel = request.LevelId.HasValue
                ? doc.GetElement(new ElementId(request.LevelId.Value)) as Level
                : new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();

            if (baseLevel == null)
                throw new InvalidOperationException("Nenhum Nível (Level) encontrado no projeto Revit.");

            var steelObstacles = request.AvoidSteelFrameClashes
                ? CollectSteelFrameBoundingBoxes(doc)
                : new List<WorldBox>();

            // Agrupa pontos por núcleo sanitário (raio de 2,5 m) para criar 1 coluna/ramal de ventilação por ambiente
            var ventClusters = new List<DetectedRoutingPoint>();
            foreach (var pt in activePoints)
            {
                bool alreadyCovered = ventClusters.Any(v =>
                    Math.Sqrt(Math.Pow(v.X - pt.X, 2) + Math.Pow(v.Y - pt.Y, 2)) < 2.50);
                if (!alreadyCovered)
                    ventClusters.Add(pt);
            }

            double dia50Feet = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);
            double topVentZ = baseLevel.Elevation + UnitUtils.ConvertToInternalUnits(Math.Max(2.80, request.TrunkOffsetMeters), UnitTypeId.Meters);
            double bottomVentZ = baseLevel.Elevation - UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters);

            int pipesCreated = 0;
            int fittingsCreated = 0;
            int steelClashesAvoided = 0;
            double totalLengthFeet = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Ventilação Sanitária 3D (NBR 8160)"))
            {
                tx.Start();

                for (int i = 0; i < ventClusters.Count; i++)
                {
                    var anchor = ventClusters[i];
                    double xFeet = UnitUtils.ConvertToInternalUnits(anchor.X, UnitTypeId.Meters);
                    double yFeet = UnitUtils.ConvertToInternalUnits(anchor.Y, UnitTypeId.Meters);

                    // Afasta 20 cm do aparelho para subir dentro da parede
                    double safeX = xFeet + UnitUtils.ConvertToInternalUnits(0.20, UnitTypeId.Meters);
                    double safeY = yFeet;

                    if (request.AvoidSteelFrameClashes && steelObstacles.Count > 0)
                    {
                        double clearance = (dia50Feet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);
                        bool shifted = FindClashFreeVerticalDrop(
                            safeX, safeY, bottomVentZ, topVentZ, clearance, preferShiftAlongX: true, steelObstacles,
                            out double outX, out double outY);

                        if (shifted)
                        {
                            safeX = outX;
                            safeY = outY;
                            steelClashesAvoided++;
                            logs.Add($"[Steel Frame] Coluna de Ventilação {i + 1:D2} deslocada para vão livre entre montantes.");
                        }
                    }

                    XYZ pBottom = new XYZ(safeX, safeY, bottomVentZ);
                    XYZ pTop = new XYZ(safeX, safeY, topVentZ);

                    Pipe ventPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, baseLevel.Id, pBottom, pTop);
                    SetPipeDiameter(ventPipe, dia50Feet);
                    pipesCreated++;
                    totalLengthFeet += GetPipeLengthFeet(ventPipe);

                    logs.Add($"Coluna de Ventilação {i + 1:D2} (DN 50 mm): subindo de -0,15 m até +{UnitUtils.ConvertFromInternalUnits(topVentZ - baseLevel.Elevation, UnitTypeId.Meters):F2} m.");
                }

                tx.Commit();
            }

            double totalLengthMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalLengthFeet, UnitTypeId.Meters), 2);
            string summary = $"Ventilação 3D (NBR 8160): {ventClusters.Count} coluna(s) DN 50 mm criada(s) ({totalLengthMeters:F2} m) e {steelClashesAvoided} desvio(s) Steel Frame.";

            return new RevitAutoRoutingResult(ventClusters.Count, pipesCreated, fittingsCreated, steelClashesAvoided, totalLengthMeters, summary, logs);
        }

        // =====================================================================
        // MOTOR ANTI-COLISÃO LIGHT STEEL FRAME (RVT + IFC ESTRUTURAL)
        // =====================================================================
        private static List<WorldBox> CollectSteelFrameBoundingBoxes(Document doc)
        {
            var boxes = new List<WorldBox>();
            CollectSteelFromDoc(doc, Transform.Identity, boxes);

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                CollectSteelFromDoc(linkDoc, link.GetTotalTransform(), boxes);
            }

            return boxes;
        }

        private static void CollectSteelFromDoc(Document targetDoc, Transform tr, List<WorldBox> output)
        {
            var categories = new[]
            {
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_GenericModel
            };

            var elements = new FilteredElementCollector(targetDoc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element elem in elements)
            {
                if (elem.Category?.Id.Value == (long)BuiltInCategory.OST_GenericModel)
                {
                    string name = RemoverAcentos(BuildElementSearchString(elem)).ToLowerInvariant();
                    if (!name.Contains("stud") && !name.Contains("montante") && !name.Contains("perfil") &&
                        !name.Contains("steel") && !name.Contains("lsf") && !name.Contains("column") &&
                        !name.Contains("beam") && !name.Contains("member"))
                        continue;
                }

                BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
                if (bb == null) continue;

                WorldBox wb = TransformBoundingBox(bb, tr);
                double dx = UnitUtils.ConvertFromInternalUnits(wb.MaxX - wb.MinX, UnitTypeId.Meters);
                double dy = UnitUtils.ConvertFromInternalUnits(wb.MaxY - wb.MinY, UnitTypeId.Meters);
                double dz = UnitUtils.ConvertFromInternalUnits(wb.MaxZ - wb.MinZ, UnitTypeId.Meters);

                // Ignora caixas gigantes (lajes/prédio inteiro) e elementos planos demais
                if ((dx > 3.5 && dy > 3.5) || dz < 0.05) continue;

                output.Add(wb);
            }
        }

        private static WorldBox TransformBoundingBox(BoundingBoxXYZ bb, Transform tr)
        {
            XYZ[] corners =
            {
                new(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new(bb.Max.X, bb.Max.Y, bb.Max.Z)
            };

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var c in corners)
            {
                XYZ p = tr.OfPoint(c);
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            return new WorldBox(minX, minY, minZ, maxX, maxY, maxZ);
        }

        private static bool FindClashFreeVerticalDrop(
            double xFeet,
            double yFeet,
            double zMinFeet,
            double zMaxFeet,
            double clearanceFeet,
            bool preferShiftAlongX,
            List<WorldBox> obstacles,
            out double safeXFeet,
            out double safeYFeet)
        {
            safeXFeet = xFeet;
            safeYFeet = yFeet;

            if (!HasVerticalClash(xFeet, yFeet, zMinFeet, zMaxFeet, clearanceFeet, obstacles))
                return false;

            double[] offsetsCm = { 8, -8, 12, -12, 16, -16, 20, -20, 25, -25, 30, -30 };

            // 1. Tenta deslocar no eixo principal da parede (preserva alinhamento dentro do painel LSF)
            foreach (double cm in offsetsCm)
            {
                double delta = UnitUtils.ConvertToInternalUnits(cm / 100.0, UnitTypeId.Meters);
                double candX = preferShiftAlongX ? xFeet + delta : xFeet;
                double candY = preferShiftAlongX ? yFeet : yFeet + delta;

                if (!HasVerticalClash(candX, candY, zMinFeet, zMaxFeet, clearanceFeet, obstacles))
                {
                    safeXFeet = candX;
                    safeYFeet = candY;
                    return true;
                }
            }

            // 2. Tenta deslocar no eixo perpendicular
            foreach (double cm in offsetsCm)
            {
                double delta = UnitUtils.ConvertToInternalUnits(cm / 100.0, UnitTypeId.Meters);
                double candX = preferShiftAlongX ? xFeet : xFeet + delta;
                double candY = preferShiftAlongX ? yFeet + delta : yFeet;

                if (!HasVerticalClash(candX, candY, zMinFeet, zMaxFeet, clearanceFeet, obstacles))
                {
                    safeXFeet = candX;
                    safeYFeet = candY;
                    return true;
                }
            }

            return false;
        }

        private static bool HasVerticalClash(
            double xFeet,
            double yFeet,
            double zMinFeet,
            double zMaxFeet,
            double clearanceFeet,
            List<WorldBox> obstacles)
        {
            double pMinX = xFeet - clearanceFeet;
            double pMaxX = xFeet + clearanceFeet;
            double pMinY = yFeet - clearanceFeet;
            double pMaxY = yFeet + clearanceFeet;

            foreach (var box in obstacles)
            {
                bool overlapX = pMinX <= box.MaxX && pMaxX >= box.MinX;
                bool overlapY = pMinY <= box.MaxY && pMaxY >= box.MinY;
                bool overlapZ = zMinFeet <= box.MaxZ && zMaxFeet >= box.MinZ;

                if (overlapX && overlapY && overlapZ)
                    return true;
            }
            return false;
        }

        // =====================================================================
        // MÉTODOS AUXILIARES DE DETECÇÃO IFC / DWG / REVIT E CONEXÕES
        // =====================================================================
        private static void CollectFromDocumentElements(
            Document targetDoc,
            Transform transform,
            string sourceLabel,
            double fallbackBaseZ,
            bool isHostDocument,
            List<DetectedRoutingPoint> output)
        {
            var categories = new[]
            {
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_SpecialityEquipment,
                BuiltInCategory.OST_GenericModel
            };

            var filter = new ElementMulticategoryFilter(categories);
            var elements = new FilteredElementCollector(targetDoc)
                .WherePasses(filter)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element elem in elements)
            {
                string rawText = BuildElementSearchString(elem);
                var spec = ClassifyFixture(rawText);
                if (spec == null || spec.Value.Category == "Reservatório") continue;

                XYZ? originFeet = null;
                XYZ dirXY = XYZ.BasisY;

                if (elem is FamilyInstance fi)
                {
                    Connector? cwConn = isHostDocument ? FindColdWaterConnector(fi) : null;
                    if (cwConn != null)
                    {
                        if (cwConn.IsConnected) continue;
                        originFeet = transform.OfPoint(cwConn.Origin);
                        XYZ d = transform.OfVector(cwConn.CoordinateSystem.BasisZ);
                        dirXY = new XYZ(d.X, d.Y, 0);
                    }
                    else if (fi.Location is LocationPoint lp)
                    {
                        originFeet = transform.OfPoint(lp.Point);
                        XYZ facing = transform.OfVector(fi.FacingOrientation);
                        dirXY = new XYZ(facing.X, facing.Y, 0);
                    }
                }

                if (originFeet == null)
                {
                    BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
                    if (bb == null) continue;
                    XYZ centerBottom = new XYZ((bb.Min.X + bb.Max.X) / 2.0, (bb.Min.Y + bb.Max.Y) / 2.0, bb.Min.Z);
                    originFeet = transform.OfPoint(centerBottom);
                }

                dirXY = dirXY.GetLength() < 0.05 ? XYZ.BasisY : dirXY.Normalize();

                double xM = Math.Round(UnitUtils.ConvertFromInternalUnits(originFeet.X, UnitTypeId.Meters), 3);
                double yM = Math.Round(UnitUtils.ConvertFromInternalUnits(originFeet.Y, UnitTypeId.Meters), 3);
                double zM = Math.Round(UnitUtils.ConvertFromInternalUnits(originFeet.Z, UnitTypeId.Meters), 3);

                output.Add(new DetectedRoutingPoint
                {
                    IsSelected = true,
                    FixtureCategory = spec.Value.Category,
                    SourceType = sourceLabel,
                    X = xM,
                    Y = yM,
                    BaseZ = zM,
                    PointHeightMeters = spec.Value.DefaultHeightM,
                    OutwardDirX = Math.Round(dirXY.X, 4),
                    OutwardDirY = Math.Round(dirXY.Y, 4),
                    Weight = spec.Value.Weight,
                    HostElementId = isHostDocument ? elem.Id.Value : null
                });
            }
        }

        private static void CollectFromDwgImport(
            Document doc,
            ImportInstance imp,
            double fallbackBaseZ,
            List<DetectedRoutingPoint> output)
        {
            try
            {
                Options opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
                GeometryElement? geomElem = imp.get_Geometry(opt);
                if (geomElem == null) return;

                double impBaseZ = fallbackBaseZ;
                BoundingBoxXYZ? impBb = imp.get_BoundingBox(null);
                if (impBb != null)
                    impBaseZ = Math.Round(UnitUtils.ConvertFromInternalUnits(impBb.Min.Z, UnitTypeId.Meters), 3);

                foreach (GeometryObject obj in geomElem)
                {
                    if (obj is GeometryInstance topInst)
                    {
                        GeometryElement subGeom = topInst.GetInstanceGeometry();
                        if (subGeom == null) continue;

                        foreach (GeometryObject subObj in subGeom)
                        {
                            if (subObj is GeometryInstance blockInst)
                            {
                                var sbLayers = new StringBuilder();
                                if (blockInst.GraphicsStyleId != ElementId.InvalidElementId &&
                                    doc.GetElement(blockInst.GraphicsStyleId) is GraphicsStyle gs)
                                {
                                    sbLayers.Append(gs.GraphicsStyleCategory?.Name).Append(' ');
                                }
                                GeometryElement? symGeom = blockInst.GetSymbolGeometry();
                                if (symGeom != null)
                                {
                                    foreach (GeometryObject gObj in symGeom)
                                    {
                                        if (gObj.GraphicsStyleId != ElementId.InvalidElementId &&
                                            doc.GetElement(gObj.GraphicsStyleId) is GraphicsStyle innerGs)
                                        {
                                            sbLayers.Append(innerGs.GraphicsStyleCategory?.Name).Append(' ');
                                        }
                                    }
                                }

                                var spec = ClassifyFixture(sbLayers.ToString());
                                if (spec == null || spec.Value.Category == "Reservatório") continue;

                                Transform tr = blockInst.Transform;
                                XYZ pt = tr.Origin;
                                XYZ dir = tr.BasisY;
                                XYZ dirXY = new XYZ(dir.X, dir.Y, 0);
                                dirXY = dirXY.GetLength() < 0.05 ? XYZ.BasisY : dirXY.Normalize();

                                double xM = Math.Round(UnitUtils.ConvertFromInternalUnits(pt.X, UnitTypeId.Meters), 3);
                                double yM = Math.Round(UnitUtils.ConvertFromInternalUnits(pt.Y, UnitTypeId.Meters), 3);
                                double zM = Math.Round(UnitUtils.ConvertFromInternalUnits(pt.Z, UnitTypeId.Meters), 3);
                                if (Math.Abs(zM) < 0.001) zM = impBaseZ;

                                output.Add(new DetectedRoutingPoint
                                {
                                    IsSelected = true,
                                    FixtureCategory = spec.Value.Category,
                                    SourceType = "DWG",
                                    X = xM,
                                    Y = yM,
                                    BaseZ = zM,
                                    PointHeightMeters = spec.Value.DefaultHeightM,
                                    OutwardDirX = Math.Round(dirXY.X, 4),
                                    OutwardDirY = Math.Round(dirXY.Y, 4),
                                    Weight = spec.Value.Weight,
                                    HostElementId = null
                                });
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void ConnectTrunkSegments(
            Document doc,
            ElementId systemTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            double diameterFeet,
            List<TrunkJunction> trunkJunctions,
            ref int pipesCreated,
            ref int fittingsCreated,
            ref double totalLengthFeet)
        {
            var orderedJunctions = trunkJunctions.OrderBy(j => j.CoordAlongTrunk).ToList();
            var trunkSegments = new List<Pipe>();

            for (int i = 0; i < orderedJunctions.Count - 1; i++)
            {
                XYZ a = orderedJunctions[i].Point;
                XYZ b = orderedJunctions[i + 1].Point;
                if (a.DistanceTo(b) < MinSegmentFeet) continue;

                Pipe trunkPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, a, b);
                SetPipeDiameter(trunkPipe, diameterFeet);
                trunkSegments.Add(trunkPipe);
                pipesCreated++;
                totalLengthFeet += GetPipeLengthFeet(trunkPipe);
            }

            for (int i = 0; i < orderedJunctions.Count; i++)
            {
                var junction = orderedJunctions[i];
                if (junction.BranchConnector == null) continue;

                var touchingTrunkConns = new List<Connector>();
                foreach (var tp in trunkSegments)
                {
                    Connector? c = GetClosestConnector(tp, junction.Point);
                    if (c != null && c.Origin.DistanceTo(junction.Point) <= ConnectorToleranceFeet)
                        touchingTrunkConns.Add(c);
                }

                if (touchingTrunkConns.Count == 1)
                {
                    try
                    {
                        doc.Create.NewElbowFitting(touchingTrunkConns[0], junction.BranchConnector);
                        fittingsCreated++;
                    }
                    catch { }
                }
                else if (touchingTrunkConns.Count >= 2)
                {
                    try
                    {
                        doc.Create.NewTeeFitting(touchingTrunkConns[0], touchingTrunkConns[1], junction.BranchConnector);
                        fittingsCreated++;
                    }
                    catch { }
                }
            }
        }

        private static (string Category, double DefaultHeightM, double Weight)? ClassifyFixture(string rawName)
        {
            string n = RemoverAcentos(rawName).ToLowerInvariant();

            if (n.Contains("reservat") || n.Contains("caixa d") || n.Contains("cisterna") || n.Contains("water tank"))
                return ("Reservatório", 2.80, 0.0);

            if (n.Contains("valvula") && (n.Contains("bacia") || n.Contains("descarga") || n.Contains("flush")))
                return ("Bacia c/ Válvula", 1.10, 32.0);

            if (n.Contains("bacia") || n.Contains("vaso") || n.Contains("sanitar") || n.Contains("toilet") || n.Contains("water closet") || n.Contains("wc"))
                return ("Vaso Sanitário", 0.20, 0.3);

            if (n.Contains("chuveiro") || n.Contains("ducha") || n.Contains("shower"))
                return ("Chuveiro", 2.10, 0.4);

            if (n.Contains("cozinha") || n.Contains("kitchen") || n.Contains("gourmet") || n.Contains("bancada") || n.Contains("pia"))
                return ("Pia de Cozinha", 0.60, 0.7);

            if (n.Contains("tanque") || n.Contains("utility") || n.Contains("laundry"))
                return ("Tanque", 0.90, 0.7);

            if (n.Contains("maquina") || n.Contains("lavadora") || n.Contains("washing") || n.Contains("dishwasher"))
                return ("Máquina de Lavar", 0.90, 1.0);

            if (n.Contains("lavatorio") || n.Contains("cuba") || n.Contains("lavabo") || n.Contains("vanity") || n.Contains("sink") || n.Contains("basin"))
                return ("Lavatório", 0.60, 0.3);

            if (n.Contains("banheira") || n.Contains("bathtub") || n.Contains("hidro"))
                return ("Banheira", 0.55, 1.0);

            if (n.Contains("mictorio") || n.Contains("urinal"))
                return ("Mictório", 1.10, 0.3);

            if (n.Contains("bide") || n.Contains("bidet"))
                return ("Bidê", 0.20, 0.1);

            if (n.Contains("jardim") || n.Contains("torneira") || n.Contains("hose"))
                return ("Torneira Externa", 0.60, 0.4);

            return null;
        }

        private static string BuildElementSearchString(Element elem)
        {
            var sb = new StringBuilder();
            sb.Append(elem.Name).Append(' ');
            if (elem is FamilyInstance fi)
            {
                sb.Append(fi.Symbol?.FamilyName).Append(' ');
                sb.Append(fi.Symbol?.Name).Append(' ');
            }

            foreach (Parameter p in elem.Parameters)
            {
                string pName = p.Definition?.Name ?? "";
                if (pName.StartsWith("Ifc", StringComparison.OrdinalIgnoreCase) && p.StorageType == StorageType.String)
                {
                    sb.Append(p.AsString()).Append(' ');
                }
            }
            return sb.ToString();
        }

        private static List<Pipe> CreatePipeChain(
            Document doc,
            ElementId systemTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            double diameterFeet,
            List<XYZ> points,
            ref int fittingsCreated)
        {
            var createdPipes = new List<Pipe>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                if (points[i].DistanceTo(points[i + 1]) < MinSegmentFeet) continue;

                Pipe pipe = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, points[i], points[i + 1]);
                SetPipeDiameter(pipe, diameterFeet);

                if (createdPipes.Count > 0)
                {
                    Pipe prevPipe = createdPipes[^1];
                    Connector? cPrev = GetClosestConnector(prevPipe, points[i]);
                    Connector? cCurr = GetClosestConnector(pipe, points[i]);
                    if (cPrev != null && cCurr != null)
                    {
                        try
                        {
                            doc.Create.NewElbowFitting(cPrev, cCurr);
                            fittingsCreated++;
                        }
                        catch { }
                    }
                }

                createdPipes.Add(pipe);
            }
            return createdPipes;
        }

        private static List<XYZ> BuildCleanPolyline(IEnumerable<XYZ> rawPoints)
        {
            var clean = new List<XYZ>();
            foreach (var pt in rawPoints)
            {
                if (clean.Count == 0 || clean[^1].DistanceTo(pt) >= MinSegmentFeet)
                    clean.Add(pt);
            }
            return clean;
        }

        private static double EnsureUniqueTrunkCoord(double desiredCoord, List<double> existingCoords)
        {
            double step = UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters);
            double candidate = desiredCoord;
            int guard = 0;
            while (existingCoords.Any(c => Math.Abs(c - candidate) < step * 0.8) && guard < 50)
            {
                candidate += step;
                guard++;
            }
            return candidate;
        }

        private static Connector? FindColdWaterConnector(FamilyInstance fi)
        {
            ConnectorSet? conns = fi.MEPModel?.ConnectorManager?.Connectors;
            if (conns == null || conns.IsEmpty) return null;

            Connector? fallbackPiping = null;
            foreach (Connector c in conns)
            {
                if (c.Domain != Domain.DomainPiping) continue;
                if (c.PipeSystemType == PipeSystemType.DomesticColdWater)
                    return c;
                if (fallbackPiping == null && c.PipeSystemType != PipeSystemType.Sanitary && c.PipeSystemType != PipeSystemType.Vent && c.PipeSystemType != PipeSystemType.DomesticHotWater)
                    fallbackPiping = c;
            }
            return fallbackPiping;
        }

        private static (string Name, Connector Connector, XYZ Origin, XYZ OutwardDirXY)? FindSupplyConnector(Document doc)
        {
            var equipment = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            var fixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            foreach (var fi in equipment.Concat(fixtures))
            {
                string fullName = RemoverAcentos($"{fi.Symbol?.FamilyName} {fi.Name}").ToLowerInvariant();
                if (!fullName.Contains("reservat") && !fullName.Contains("caixa") &&
                    !fullName.Contains("tank") && !fullName.Contains("cisterna") &&
                    !fullName.Contains("hidrometro") && !fullName.Contains("meter"))
                    continue;

                ConnectorSet? conns = fi.MEPModel?.ConnectorManager?.Connectors;
                if (conns == null) continue;

                foreach (Connector c in conns)
                {
                    if (c.Domain == Domain.DomainPiping && !c.IsConnected)
                    {
                        XYZ dir = c.CoordinateSystem.BasisZ;
                        XYZ dirXY = new XYZ(dir.X, dir.Y, 0);
                        dirXY = dirXY.GetLength() < 0.05 ? XYZ.BasisX : dirXY.Normalize();
                        return ($"{fi.Symbol?.FamilyName} ({fi.Name})", c, c.Origin, dirXY);
                    }
                }
            }
            return null;
        }

        private static Connector? GetClosestConnector(Pipe pipe, XYZ targetPoint)
        {
            ConnectorSet? conns = pipe.ConnectorManager?.Connectors;
            if (conns == null) return null;

            Connector? best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in conns)
            {
                double d = c.Origin.DistanceTo(targetPoint);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        private static void SetPipeDiameter(Pipe pipe, double diameterFeet)
        {
            Parameter? p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (p != null && !p.IsReadOnly && diameterFeet > 0)
                p.Set(diameterFeet);
        }

        private static void SetPipeComment(Pipe pipe, string comment)
        {
            Parameter? p = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p != null && !p.IsReadOnly)
                p.Set(comment);
        }

        private static double GetPipeLengthFeet(Pipe pipe)
        {
            Parameter? p = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            return p != null && p.HasValue ? p.AsDouble() : 0;
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

        private sealed record TrunkJunction(double CoordAlongTrunk, XYZ Point, Connector? BranchConnector, bool IsSupply);
        private sealed record WorldBox(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);
    }
}
