using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
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
                .OrderByDescending(s => (s.SystemClassification == MEPSystemClassification.DomesticColdWater || s.SystemClassification == MEPSystemClassification.OtherPipe || s.SystemClassification == MEPSystemClassification.UndefinedSystemClassification))
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
                ownerWindow.Show();
                ownerWindow.Activate();
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

            if (string.Equals(request.DisciplineMode, "StormDrainage", StringComparison.OrdinalIgnoreCase))
                return ExecuteStormDrainageRouting(doc, request);

            return ExecuteColdWaterRouting(doc, request);
        }

        // =====================================================================
        // 1. ÁGUA FRIA 3D GUIADA POR PAREDES + DESVIO DE PORTAS/JANELAS E LSF
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

            var context = request.AvoidSteelFrameClashes
                ? BuildArchitecturalAndSteelContext(doc)
                : new RoutingSpatialContext(new List<WorldBox>(), new List<WorldBox>(), new List<WallAxisSegment>());

            if (request.AvoidSteelFrameClashes)
            {
                logs.Add($"Contexto BIM: {context.Walls.Count} parede(s), {context.OpeningObstacles.Count} porta(s)/janela(s) e {context.SteelObstacles.Count} perfil(is) Steel Frame mapeados.");
            }

            double offsetFeet = UnitUtils.ConvertToInternalUnits(request.TrunkOffsetMeters, UnitTypeId.Meters);
            double trunkZ = baseLevel.Elevation + offsetFeet;
            double diameterFeet = UnitUtils.ConvertToInternalUnits(request.InitialDiameterMm, UnitTypeId.Millimeters);
            double clearanceFeet = (diameterFeet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);

            var supplyInfo = FindSupplyConnector(doc);

            // Alinha cada ponto ao eixo da parede mais próxima (se existir parede até 85 cm)
            var resolvedTargets = new List<(DetectedRoutingPoint Pt, double WallXFeet, double WallYFeet, XYZ OutDir, bool SnappedToWall)>();
            foreach (var pt in activePoints)
            {
                double rawX = UnitUtils.ConvertToInternalUnits(pt.X, UnitTypeId.Meters);
                double rawY = UnitUtils.ConvertToInternalUnits(pt.Y, UnitTypeId.Meters);
                double rawZ = UnitUtils.ConvertToInternalUnits(pt.BaseZ + pt.PointHeightMeters, UnitTypeId.Meters);

                XYZ defaultDir = new XYZ(pt.OutwardDirX, pt.OutwardDirY, 0);
                defaultDir = defaultDir.GetLength() < 0.05 ? XYZ.BasisY : defaultDir.Normalize();

                if (request.AvoidSteelFrameClashes &&
                    TrySnapPointToNearestWall(rawX, rawY, rawZ, defaultDir, context.Walls, out double snapX, out double snapY, out XYZ wallOutDir))
                {
                    resolvedTargets.Add((pt, snapX, snapY, wallOutDir, true));
                }
                else
                {
                    resolvedTargets.Add((pt, rawX, rawY, defaultDir, false));
                }
            }

            int snappedCount = resolvedTargets.Count(r => r.SnappedToWall);
            if (snappedCount > 0)
                logs.Add($"[Paredes] {snappedCount} ponto(s) alinhado(s) automaticamente ao eixo interno das paredes hidráulicas.");

            double minX = resolvedTargets.Min(p => p.WallXFeet);
            double maxX = resolvedTargets.Max(p => p.WallXFeet);
            double minY = resolvedTargets.Min(p => p.WallYFeet);
            double maxY = resolvedTargets.Max(p => p.WallYFeet);

            bool trunkAlongX = (maxX - minX) >= (maxY - minY);
            double trunkConstCoord = trunkAlongX
                ? (supplyInfo.HasValue ? supplyInfo.Value.Origin.Y : resolvedTargets.Average(p => p.WallYFeet))
                : (supplyInfo.HasValue ? supplyInfo.Value.Origin.X : resolvedTargets.Average(p => p.WallXFeet));

            int pipesCreated = 0;
            int fittingsCreated = 0;
            int clashesAvoided = 0;
            double totalLengthFeet = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Traçado Água Fria 3D (Paredes & LSF)"))
            {
                tx.Start();

                var trunkJunctions = new List<TrunkJunction>();

                foreach (var item in resolvedTargets)
                {
                    var pt = item.Pt;
                    double wallXFeet = item.WallXFeet;
                    double wallYFeet = item.WallYFeet;
                    XYZ dirXY = item.OutDir;
                    double zPointFeet = UnitUtils.ConvertToInternalUnits(pt.BaseZ + pt.PointHeightMeters, UnitTypeId.Meters);

                    double dropXFeet = wallXFeet;
                    double dropYFeet = wallYFeet;

                    if (request.AvoidSteelFrameClashes && (context.SteelObstacles.Count > 0 || context.OpeningObstacles.Count > 0))
                    {
                        double zLow = Math.Min(zPointFeet, trunkZ);
                        double zHigh = Math.Max(zPointFeet, trunkZ);
                        bool wasShifted = FindSafeVerticalDrop(
                            wallXFeet, wallYFeet, zLow, zHigh, clearanceFeet, trunkAlongX,
                            context.SteelObstacles, context.OpeningObstacles,
                            out dropXFeet, out dropYFeet, out string clashReason);

                        if (wasShifted)
                        {
                            clashesAvoided++;
                            double shiftCm = Math.Round(UnitUtils.ConvertFromInternalUnits(
                                Math.Sqrt(Math.Pow(dropXFeet - wallXFeet, 2) + Math.Pow(dropYFeet - wallYFeet, 2)),
                                UnitTypeId.Centimeters), 1);
                            logs.Add($"[{clashReason}] '{pt.Name}': prumada deslocada {shiftCm:F1} cm dentro da parede para vão livre.");
                        }
                    }

                    XYZ pWallFixture = new XYZ(wallXFeet, wallYFeet, zPointFeet);
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
                        for (int bpIdx = 1; bpIdx < branchPipes.Count; bpIdx++)
                            SetPipeComment(branchPipes[bpIdx], $"BahrHidroAuto|Disc={request.DisciplineMode}");
                        pipesCreated += branchPipes.Count;
                        totalLengthFeet += branchPipes.Sum(GetPipeLengthFeet);

                        SetPipeComment(branchPipes[0], $"BahrHidro|{pt.Name}|{pt.Weight.ToString("F2", CultureInfo.InvariantCulture)}|H={pt.PointHeightMeters.ToString("F2", CultureInfo.InvariantCulture)}m|Disc={request.DisciplineMode}");

                        // Inserção automática de Registo de Pressão na descida de Chuveiro (H = 1,10 m)
                        if (request.InsertValvesAutomatically &&
                            (pt.FixtureCategory.Contains("Chuveiro", StringComparison.OrdinalIgnoreCase) ||
                             pt.FixtureCategory.Contains("Ducha", StringComparison.OrdinalIgnoreCase)))
                        {
                            double valveZFeet = baseLevel.Elevation + UnitUtils.ConvertToInternalUnits(1.10, UnitTypeId.Meters);
                            Pipe? verticalPipe = branchPipes.FirstOrDefault(bp => IsVerticalPipeCoveringZ(bp, valveZFeet));
                            if (verticalPipe != null && TryInsertValveOnPipe(doc, verticalPipe, new XYZ(dropXFeet, dropYFeet, valveZFeet), baseLevel, preferPressureValve: true, out string valveName))
                            {
                                fittingsCreated++;
                                logs.Add($"[NBR 5626] Registo de Pressão ('{valveName}') inserido a H = 1,10 m para '{pt.Name}'.");
                            }
                        }

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
                    logs.Insert(0, "Origem da linha tronco criada a 0,80 m do primeiro ramal.");
                }

                var createdTrunkSegments = ConnectTrunkSegments(doc, systemTypeId, pipeTypeId, baseLevel.Id, diameterFeet, trunkJunctions, ref pipesCreated, ref fittingsCreated, ref totalLengthFeet);

                // Inserção automática de Registo de Gaveta no início da distribuição do núcleo/pavimento
                if (request.InsertValvesAutomatically && createdTrunkSegments.Count > 0)
                {
                    Pipe firstTrunk = createdTrunkSegments[0];
                    if (firstTrunk.Location is LocationCurve tlc && tlc.Curve.Length >= UnitUtils.ConvertToInternalUnits(0.30, UnitTypeId.Meters))
                    {
                        XYZ gatePos = tlc.Curve.Evaluate(0.25, normalized: true);
                        if (TryInsertValveOnPipe(doc, firstTrunk, gatePos, baseLevel, preferPressureValve: false, out string gateName))
                        {
                            fittingsCreated++;
                            logs.Add($"[NBR 5626] Registo de Gaveta de manutenção ('{gateName}') inserido na linha de distribuição.");
                        }
                    }
                }

                tx.Commit();
            }

            double totalLengthMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalLengthFeet, UnitTypeId.Meters), 2);
            string planoLabel = request.RouteFromAbove ? "Por Cima (Entreforro)" : "Por Baixo (Piso)";
            string summary = $"Água Fria 3D ({planoLabel}): {activePoints.Count} ponto(s), {pipesCreated} tubo(s) ({totalLengthMeters:F2} m), {fittingsCreated} conexão(ões) e {clashesAvoided} desvio(s) de estrutura/esquadrias.";

            return new RevitAutoRoutingResult(activePoints.Count, pipesCreated, fittingsCreated, clashesAvoided, totalLengthMeters, summary, logs);
        }

        // =====================================================================
        // 2. ESGOTO SANITÁRIO 3D COM CAIXA SIFONADA E DIMENSIONAMENTO POR UHC
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

            var context = request.AvoidSteelFrameClashes
                ? BuildArchitecturalAndSteelContext(doc)
                : new RoutingSpatialContext(new List<WorldBox>(), new List<WorldBox>(), new List<WallAxisSegment>());

            // Cálculo total de UHC conforme ABNT NBR 8160
            int totalUhc = activePoints.Sum(GetFixtureUhc);
            bool hasToilet = activePoints.Any(IsToiletFixture);

            // Dimensionamento normativo do subcoletor pela soma de UHC (NBR 8160)
            double subcollectorDnMm;
            double subcollectorSlope;
            if (hasToilet)
            {
                subcollectorDnMm = totalUhc <= 160 ? 100.0 : 150.0;
                subcollectorSlope = 0.01; // 1% para DN >= 100 mm
            }
            else
            {
                if (totalUhc <= 6) { subcollectorDnMm = 50.0; subcollectorSlope = 0.02; }
                else if (totalUhc <= 20) { subcollectorDnMm = 75.0; subcollectorSlope = 0.02; }
                else { subcollectorDnMm = 100.0; subcollectorSlope = 0.01; }
            }

            logs.Add($"Dimensionamento NBR 8160: Soma = {totalUhc} UHC → Subcoletor DN {subcollectorDnMm:F0} mm (declividade {(subcollectorSlope * 100):F1}%).");

            double collectorStartOffsetM = request.TrunkOffsetMeters < 0 ? request.TrunkOffsetMeters : -0.24;
            double collectorStartZ = baseLevel.Elevation + UnitUtils.ConvertToInternalUnits(collectorStartOffsetM, UnitTypeId.Meters);

            double diaSubcollectorFeet = UnitUtils.ConvertToInternalUnits(subcollectorDnMm, UnitTypeId.Millimeters);
            double dia100Feet = UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters);
            double dia50Feet = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);
            double dia40Feet = UnitUtils.ConvertToInternalUnits(40.0, UnitTypeId.Millimeters);

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
                ? ptsFeet.Average(p => p.Y) + UnitUtils.ConvertToInternalUnits(0.45, UnitTypeId.Meters)
                : ptsFeet.Average(p => p.X) + UnitUtils.ConvertToInternalUnits(0.45, UnitTypeId.Meters);

            double firstAxisCoord = trunkAlongX ? minX : minY;

            // Separa aparelhos em 3 grupos normativos:
            // A) Vasos Sanitários (DN 100 direto no subcoletor)
            // B) Águas Servidas de Banheiro (Chuveiro, Lavatório, Bidê, Banheira -> Caixa Sifonada DN 40 -> Saída DN 50)
            // C) Cozinha / Lavanderia (Pia, Tanque, Máquina -> DN 50 direto no subcoletor/caixa de gordura)
            var toiletPoints = activePoints.Where(IsToiletFixture).ToList();
            var bathroomGreyPoints = activePoints.Where(IsBathroomGreywaterFixture).ToList();
            var kitchenLaundryPoints = activePoints
                .Except(toiletPoints)
                .Except(bathroomGreyPoints)
                .ToList();

            FamilySymbol? siphonedBoxSymbol = FindSiphonedBoxFamilySymbol(doc);

            int pipesCreated = 0;
            int fittingsCreated = 0;
            int clashesAvoided = 0;
            double totalLengthFeet = 0;

            var greyClusters = ClusterPointsByDistance(bathroomGreyPoints, 3.0);

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Esgoto 3D (Caixa Sifonada + UHC NBR 8160)"))
            {
                tx.Start();

                if (siphonedBoxSymbol != null && !siphonedBoxSymbol.IsActive)
                    siphonedBoxSymbol.Activate();

                var collectorNodes = new List<(double AxisCoord, XYZ Point, Connector? BranchConn)>();

                // --- GRUPO A: VASOS SANITÁRIOS (DN 100 mm, i = 1%) ---
                foreach (var pt in toiletPoints)
                {
                    int uhc = GetFixtureUhc(pt);
                    double origXFeet = UnitUtils.ConvertToInternalUnits(pt.X, UnitTypeId.Meters);
                    double origYFeet = UnitUtils.ConvertToInternalUnits(pt.Y, UnitTypeId.Meters);
                    double floorZFeet = UnitUtils.ConvertToInternalUnits(pt.BaseZ, UnitTypeId.Meters);
                    double subFloorZFeet = floorZFeet - UnitUtils.ConvertToInternalUnits(0.12, UnitTypeId.Meters);

                    double rawCoord = trunkAlongX ? origXFeet : origYFeet;
                    double adjCoord = EnsureUniqueTrunkCoord(rawCoord, collectorNodes.Select(n => n.AxisCoord).ToList());

                    double distAlongCollector = Math.Max(0, adjCoord - firstAxisCoord);
                    double collectorZAtJunction = collectorStartZ - (distAlongCollector * subcollectorSlope);

                    XYZ pCollector = trunkAlongX
                        ? new XYZ(adjCoord, trunkConstCoord, collectorZAtJunction)
                        : new XYZ(trunkConstCoord, adjCoord, collectorZAtJunction);

                    XYZ pFloor = new XYZ(origXFeet, origYFeet, floorZFeet);
                    XYZ pSubFloor = new XYZ(origXFeet, origYFeet, subFloorZFeet);
                    double horizDist = Math.Sqrt(Math.Pow(pCollector.X - pSubFloor.X, 2) + Math.Pow(pCollector.Y - pSubFloor.Y, 2));
                    double slopedEndZ = Math.Max(collectorZAtJunction + UnitUtils.ConvertToInternalUnits(0.03, UnitTypeId.Meters), subFloorZFeet - (horizDist * 0.01));
                    XYZ pBeforeDrop = new XYZ(pCollector.X, pCollector.Y, slopedEndZ);

                    var branchPoints = BuildCleanPolyline(new[] { pFloor, pSubFloor, pBeforeDrop, pCollector });
                    var branchPipes = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, dia100Feet, branchPoints, ref fittingsCreated);

                    if (branchPipes.Count > 0)
                    {
                        pipesCreated += branchPipes.Count;
                        totalLengthFeet += branchPipes.Sum(GetPipeLengthFeet);
                        Connector? endConn = GetClosestConnector(branchPipes[^1], pCollector);
                        collectorNodes.Add((adjCoord, pCollector, endConn));
                        logs.Add($"Ramal Primário: {pt.Name} ({uhc} UHC) → DN 100 mm (i = 1,0%).");
                    }
                }

                // --- GRUPO B: ÁGUAS SERVIDAS DE BANHEIRO -> CAIXA SIFONADA (DN 40 -> CS -> DN 50) ---
                for (int cIdx = 0; cIdx < greyClusters.Count; cIdx++)
                {
                    var cluster = greyClusters[cIdx];
                    double csXFeet = cluster.Average(p => UnitUtils.ConvertToInternalUnits(p.X, UnitTypeId.Meters)) + UnitUtils.ConvertToInternalUnits(0.25, UnitTypeId.Meters);
                    double csYFeet = cluster.Average(p => UnitUtils.ConvertToInternalUnits(p.Y, UnitTypeId.Meters)) + UnitUtils.ConvertToInternalUnits(0.25, UnitTypeId.Meters);
                    double floorZFeet = UnitUtils.ConvertToInternalUnits(cluster[0].BaseZ, UnitTypeId.Meters);
                    double csBottomZFeet = floorZFeet - UnitUtils.ConvertToInternalUnits(0.14, UnitTypeId.Meters);

                    XYZ pBoxBottom = new XYZ(csXFeet, csYFeet, csBottomZFeet);
                    XYZ pBoxTop = new XYZ(csXFeet, csYFeet, floorZFeet);

                    // Se houver família de Caixa Sifonada no template, insere no piso
                    if (siphonedBoxSymbol != null)
                    {
                        try
                        {
                            doc.Create.NewFamilyInstance(pBoxTop, siphonedBoxSymbol, baseLevel, StructuralType.NonStructural);
                        }
                        catch { }
                    }

                    PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, pipeTypeId);
                    // Corpo vertical da Caixa Sifonada (DN 100 mm)
                    Pipe csBody = Pipe.Create(doc, systemTypeId, pipeTypeId, baseLevel.Id, pBoxTop, pBoxBottom);
                    SetPipeDiameter(csBody, dia100Feet);
                    SetPipeComment(csBody, $"BahrHidro|Caixa Sifonada CS-{cIdx + 1:D2}|DN100x100x50|Disc={request.DisciplineMode}");
                    pipesCreated++;
                    totalLengthFeet += GetPipeLengthFeet(csBody);

                    int clusterUhc = cluster.Sum(GetFixtureUhc);
                    logs.Add($"[NBR 8160] Caixa Sifonada CS-{cIdx + 1:D2} (DN 100×100×50) criada para {cluster.Count} aparelho(s) ({clusterUhc} UHC).");

                    // Liga cada aparelho do banheiro na Caixa Sifonada com DN 40 mm (i = 2%)
                    foreach (var pt in cluster)
                    {
                        int uhc = GetFixtureUhc(pt);
                        double origXFeet = UnitUtils.ConvertToInternalUnits(pt.X, UnitTypeId.Meters);
                        double origYFeet = UnitUtils.ConvertToInternalUnits(pt.Y, UnitTypeId.Meters);

                        bool isFloorDrainFixture = pt.FixtureCategory.Contains("Chuveiro", StringComparison.OrdinalIgnoreCase) ||
                                                   pt.FixtureCategory.Contains("Banheira", StringComparison.OrdinalIgnoreCase);

                        double startZFeet = isFloorDrainFixture
                            ? floorZFeet
                            : floorZFeet + UnitUtils.ConvertToInternalUnits(0.50, UnitTypeId.Meters);

                        double dropXFeet = origXFeet;
                        double dropYFeet = origYFeet;

                        if (!isFloorDrainFixture && request.AvoidSteelFrameClashes)
                        {
                            if (TrySnapPointToNearestWall(origXFeet, origYFeet, startZFeet, XYZ.BasisY, context.Walls, out double sx, out double sy, out _))
                            {
                                dropXFeet = sx;
                                dropYFeet = sy;
                            }

                            double clearance = (dia40Feet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);
                            if (FindSafeVerticalDrop(dropXFeet, dropYFeet, csBottomZFeet, startZFeet, clearance, trunkAlongX,
                                context.SteelObstacles, context.OpeningObstacles, out double safeX, out double safeY, out string reason))
                            {
                                dropXFeet = safeX;
                                dropYFeet = safeY;
                                clashesAvoided++;
                                logs.Add($"[{reason}] Descida '{pt.Name}' deslocada para vão livre.");
                            }
                        }

                        double subFloorStartZ = floorZFeet - UnitUtils.ConvertToInternalUnits(0.08, UnitTypeId.Meters);
                        XYZ pSubFloor = new XYZ(dropXFeet, dropYFeet, subFloorStartZ);
                        double horizDist = Math.Sqrt(Math.Pow(pBoxBottom.X - pSubFloor.X, 2) + Math.Pow(pBoxBottom.Y - pSubFloor.Y, 2));
                        double entryZ = Math.Max(csBottomZFeet + UnitUtils.ConvertToInternalUnits(0.02, UnitTypeId.Meters), subFloorStartZ - (horizDist * 0.02));
                        XYZ pBoxEntry = new XYZ(pBoxBottom.X, pBoxBottom.Y, entryZ);

                        var rawPts = new List<XYZ>();
                        if (!isFloorDrainFixture)
                            rawPts.Add(new XYZ(dropXFeet, dropYFeet, startZFeet));
                        rawPts.Add(pSubFloor);
                        rawPts.Add(pBoxEntry);

                        var greyBranch = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, dia40Feet, BuildCleanPolyline(rawPts), ref fittingsCreated);
                        if (greyBranch.Count > 0)
                        {
                            pipesCreated += greyBranch.Count;
                            totalLengthFeet += greyBranch.Sum(GetPipeLengthFeet);
                            logs.Add($"  ↳ Ramal Descarga: {pt.Name} ({uhc} UHC) → Caixa Sifonada CS-{cIdx + 1:D2} em DN 40 mm (i = 2,0%).");
                        }
                    }

                    // Saída da Caixa Sifonada em DN 50 mm (i = 2%) até o Subcoletor Principal
                    double rawCsCoord = trunkAlongX ? pBoxBottom.X : pBoxBottom.Y;
                    double adjCsCoord = EnsureUniqueTrunkCoord(rawCsCoord, collectorNodes.Select(n => n.AxisCoord).ToList());

                    double distAlongCollector = Math.Max(0, adjCsCoord - firstAxisCoord);
                    double collectorZAtJunction = collectorStartZ - (distAlongCollector * subcollectorSlope);

                    XYZ pCollector = trunkAlongX
                        ? new XYZ(adjCsCoord, trunkConstCoord, collectorZAtJunction)
                        : new XYZ(trunkConstCoord, adjCsCoord, collectorZAtJunction);

                    double outHoriz = Math.Sqrt(Math.Pow(pCollector.X - pBoxBottom.X, 2) + Math.Pow(pCollector.Y - pBoxBottom.Y, 2));
                    double outEndZ = Math.Max(collectorZAtJunction + UnitUtils.ConvertToInternalUnits(0.02, UnitTypeId.Meters), csBottomZFeet - (outHoriz * 0.02));
                    XYZ pBeforeDrop = new XYZ(pCollector.X, pCollector.Y, outEndZ);

                    var csOutletPts = BuildCleanPolyline(new[] { pBoxBottom, pBeforeDrop, pCollector });
                    var csOutletPipes = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, dia50Feet, csOutletPts, ref fittingsCreated);

                    if (csOutletPipes.Count > 0)
                    {
                        pipesCreated += csOutletPipes.Count;
                        totalLengthFeet += csOutletPipes.Sum(GetPipeLengthFeet);
                        Connector? endConn = GetClosestConnector(csOutletPipes[^1], pCollector);
                        collectorNodes.Add((adjCsCoord, pCollector, endConn));
                    }
                }

                // --- GRUPO C: COZINHA / LAVANDERIA (DN 50 mm, i = 2% direto no Subcoletor) ---
                foreach (var pt in kitchenLaundryPoints)
                {
                    int uhc = GetFixtureUhc(pt);
                    double origXFeet = UnitUtils.ConvertToInternalUnits(pt.X, UnitTypeId.Meters);
                    double origYFeet = UnitUtils.ConvertToInternalUnits(pt.Y, UnitTypeId.Meters);
                    double floorZFeet = UnitUtils.ConvertToInternalUnits(pt.BaseZ, UnitTypeId.Meters);
                    double startZFeet = floorZFeet + UnitUtils.ConvertToInternalUnits(0.55, UnitTypeId.Meters);
                    double subFloorZFeet = floorZFeet - UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters);

                    double dropXFeet = origXFeet;
                    double dropYFeet = origYFeet;

                    if (request.AvoidSteelFrameClashes)
                    {
                        if (TrySnapPointToNearestWall(origXFeet, origYFeet, startZFeet, XYZ.BasisY, context.Walls, out double sx, out double sy, out _))
                        {
                            dropXFeet = sx;
                            dropYFeet = sy;
                        }

                        double clearance = (dia50Feet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);
                        if (FindSafeVerticalDrop(dropXFeet, dropYFeet, subFloorZFeet, startZFeet, clearance, trunkAlongX,
                            context.SteelObstacles, context.OpeningObstacles, out double safeX, out double safeY, out string reason))
                        {
                            dropXFeet = safeX;
                            dropYFeet = safeY;
                            clashesAvoided++;
                            logs.Add($"[{reason}] Descida '{pt.Name}' deslocada para vão livre.");
                        }
                    }

                    double rawCoord = trunkAlongX ? dropXFeet : dropYFeet;
                    double adjCoord = EnsureUniqueTrunkCoord(rawCoord, collectorNodes.Select(n => n.AxisCoord).ToList());

                    double distAlongCollector = Math.Max(0, adjCoord - firstAxisCoord);
                    double collectorZAtJunction = collectorStartZ - (distAlongCollector * subcollectorSlope);

                    XYZ pCollector = trunkAlongX
                        ? new XYZ(adjCoord, trunkConstCoord, collectorZAtJunction)
                        : new XYZ(trunkConstCoord, adjCoord, collectorZAtJunction);

                    XYZ pWallStart = new XYZ(dropXFeet, dropYFeet, startZFeet);
                    XYZ pSubFloor = new XYZ(dropXFeet, dropYFeet, subFloorZFeet);
                    double horizDist = Math.Sqrt(Math.Pow(pCollector.X - pSubFloor.X, 2) + Math.Pow(pCollector.Y - pSubFloor.Y, 2));
                    double slopedEndZ = Math.Max(collectorZAtJunction + UnitUtils.ConvertToInternalUnits(0.02, UnitTypeId.Meters), subFloorZFeet - (horizDist * 0.02));
                    XYZ pBeforeDrop = new XYZ(pCollector.X, pCollector.Y, slopedEndZ);

                    var branchPoints = BuildCleanPolyline(new[] { pWallStart, pSubFloor, pBeforeDrop, pCollector });
                    var branchPipes = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, dia50Feet, branchPoints, ref fittingsCreated);

                    if (branchPipes.Count > 0)
                    {
                        pipesCreated += branchPipes.Count;
                        totalLengthFeet += branchPipes.Sum(GetPipeLengthFeet);
                        Connector? endConn = GetClosestConnector(branchPipes[^1], pCollector);
                        collectorNodes.Add((adjCoord, pCollector, endConn));
                        logs.Add($"Ramal Cozinha/Serviço: {pt.Name} ({uhc} UHC) → DN 50 mm (i = 2,0%).");
                    }
                }

                // --- SUBCOLETOR PRINCIPAL DIMENSIONADO POR UHC + TUBO DE QUEDA ---
                var orderedCollector = collectorNodes.OrderBy(n => n.AxisCoord).ToList();
                if (orderedCollector.Count > 0)
                {
                    var lastNode = orderedCollector[^1];
                    double outletCoord = lastNode.AxisCoord + UnitUtils.ConvertToInternalUnits(0.60, UnitTypeId.Meters);
                    double outletZ = lastNode.Point.Z - UnitUtils.ConvertToInternalUnits(0.60 * subcollectorSlope, UnitTypeId.Meters);
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

                        PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, pipeTypeId);
                        Pipe cPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, baseLevel.Id, a, b);
                        SetPipeDiameter(cPipe, diaSubcollectorFeet);
                        SetPipeComment(cPipe, $"BahrHidroAuto|Disc={request.DisciplineMode}");
                        collectorPipes.Add(cPipe);
                        pipesCreated++;
                        totalLengthFeet += GetPipeLengthFeet(cPipe);
                    }
                    PipeFittingAutoConnector.ConnectPipesWithElbows(doc, collectorPipes);


                    XYZ pDropDown = new XYZ(pOutletTop.X, pOutletTop.Y, pOutletTop.Z - UnitUtils.ConvertToInternalUnits(0.80, UnitTypeId.Meters));
                    PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, pipeTypeId);
                    Pipe dropPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, baseLevel.Id, pOutletTop, pDropDown);
                    SetPipeDiameter(dropPipe, diaSubcollectorFeet);
                    SetPipeComment(dropPipe, $"BahrHidroAuto|Disc={request.DisciplineMode}");
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
                }

                tx.Commit();
            }

            double totalLengthMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalLengthFeet, UnitTypeId.Meters), 2);
            string summary = $"Esgoto 3D NBR 8160 ({totalUhc} UHC → Subcoletor DN {subcollectorDnMm:F0}): {activePoints.Count} aparelho(s), {greyClusters.Count} Caixa(s) Sifonada(s), {pipesCreated} tubo(s) ({totalLengthMeters:F2} m).";

            return new RevitAutoRoutingResult(activePoints.Count, pipesCreated, fittingsCreated, clashesAvoided, totalLengthMeters, summary, logs);
        }

        // =====================================================================
        // 3. ÁGUA QUENTE 3D (ABNT NBR 7198 — AFASTADA DA ÁGUA FRIA)
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
                DisciplineMode = "HotWater",
                Points = hotPoints
            };

            var baseRes = ExecuteColdWaterRouting(doc, hwRequest);
            logs.AddRange(baseRes.Logs);

            string planoLabel = request.RouteFromAbove ? "Por Cima (Entreforro)" : "Por Baixo (Piso)";
            string summary = $"Água Quente 3D ({planoLabel}): {hotPoints.Count} ponto(s), {baseRes.PipesCreated} tubo(s) ({baseRes.TotalLengthMeters:F2} m), {baseRes.FittingsCreated} conexão(ões) e {baseRes.SteelClashesAvoided} desvio(s).";

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
        // 4. VENTILAÇÃO SANITÁRIA 3D (ABNT NBR 8160 — DN 50 MM)
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

            var context = request.AvoidSteelFrameClashes
                ? BuildArchitecturalAndSteelContext(doc)
                : new RoutingSpatialContext(new List<WorldBox>(), new List<WorldBox>(), new List<WallAxisSegment>());

            var ventClusters = ClusterPointsByDistance(activePoints, 2.50).Select(c => c[0]).ToList();

            double dia50Feet = UnitUtils.ConvertToInternalUnits(50.0, UnitTypeId.Millimeters);
            double topVentZ = baseLevel.Elevation + UnitUtils.ConvertToInternalUnits(Math.Max(2.80, request.TrunkOffsetMeters), UnitTypeId.Meters);
            double bottomVentZ = baseLevel.Elevation - UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters);

            int pipesCreated = 0;
            int fittingsCreated = 0;
            int clashesAvoided = 0;
            double totalLengthFeet = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Ventilação Sanitária 3D (NBR 8160)"))
            {
                tx.Start();

                for (int i = 0; i < ventClusters.Count; i++)
                {
                    var anchor = ventClusters[i];
                    double xFeet = UnitUtils.ConvertToInternalUnits(anchor.X, UnitTypeId.Meters);
                    double yFeet = UnitUtils.ConvertToInternalUnits(anchor.Y, UnitTypeId.Meters);

                    double safeX = xFeet + UnitUtils.ConvertToInternalUnits(0.20, UnitTypeId.Meters);
                    double safeY = yFeet;

                    if (request.AvoidSteelFrameClashes)
                    {
                        if (TrySnapPointToNearestWall(safeX, safeY, baseLevel.Elevation + 1.0, XYZ.BasisY, context.Walls, out double sx, out double sy, out _))
                        {
                            safeX = sx;
                            safeY = sy;
                        }

                        double clearance = (dia50Feet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);
                        bool shifted = FindSafeVerticalDrop(
                            safeX, safeY, bottomVentZ, topVentZ, clearance, preferShiftAlongX: true,
                            context.SteelObstacles, context.OpeningObstacles,
                            out double outX, out double outY, out string reason);

                        if (shifted)
                        {
                            safeX = outX;
                            safeY = outY;
                            clashesAvoided++;
                            logs.Add($"[{reason}] Coluna de Ventilação {i + 1:D2} deslocada para vão livre.");
                        }
                    }

                    XYZ pBottom = new XYZ(safeX, safeY, bottomVentZ);
                    XYZ pTop = new XYZ(safeX, safeY, topVentZ);

                    PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, pipeTypeId);
                    Pipe ventPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, baseLevel.Id, pBottom, pTop);
                    SetPipeDiameter(ventPipe, dia50Feet);
                    SetPipeComment(ventPipe, $"BahrHidroAuto|Disc={request.DisciplineMode}");
                    pipesCreated++;
                    totalLengthFeet += GetPipeLengthFeet(ventPipe);

                    logs.Add($"Coluna de Ventilação {i + 1:D2} (DN 50 mm): subindo de -0,15 m até +{UnitUtils.ConvertFromInternalUnits(topVentZ - baseLevel.Elevation, UnitTypeId.Meters):F2} m.");
                }

                tx.Commit();
            }

            double totalLengthMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalLengthFeet, UnitTypeId.Meters), 2);
            string summary = $"Ventilação 3D (NBR 8160): {ventClusters.Count} coluna(s) DN 50 mm criada(s) ({totalLengthMeters:F2} m) e {clashesAvoided} desvio(s).";

            return new RevitAutoRoutingResult(ventClusters.Count, pipesCreated, fittingsCreated, clashesAvoided, totalLengthMeters, summary, logs);
        }

        // =====================================================================
        // 5. ÁGUAS PLUVIAIS 3D (ABNT NBR 10844 — CONDUTORES)
        // =====================================================================
        private static RevitAutoRoutingResult ExecuteStormDrainageRouting(Document doc, RevitAutoRoutingRequest request)
        {
            var logs = new List<string>();
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

            var context = request.AvoidSteelFrameClashes
                ? BuildArchitecturalAndSteelContext(doc)
                : new RoutingSpatialContext(new List<WorldBox>(), new List<WorldBox>(), new List<WallAxisSegment>());

            var roofBoxes = new List<WorldBox>();
            CollectRoofsFromDoc(doc, Transform.Identity, roofBoxes);

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                CollectRoofsFromDoc(linkDoc, link.GetTotalTransform(), roofBoxes);
            }

            double minX, maxX, minY, maxY, eaveZFeet;
            double roofAreaM2 = 0;

            if (roofBoxes.Count > 0)
            {
                minX = roofBoxes.Min(b => b.MinX);
                maxX = roofBoxes.Max(b => b.MaxX);
                minY = roofBoxes.Min(b => b.MinY);
                maxY = roofBoxes.Max(b => b.MaxY);
                eaveZFeet = roofBoxes.Min(b => b.MinZ);

                double dxM = UnitUtils.ConvertFromInternalUnits(maxX - minX, UnitTypeId.Meters);
                double dyM = UnitUtils.ConvertFromInternalUnits(maxY - minY, UnitTypeId.Meters);
                roofAreaM2 = Math.Round(dxM * dyM, 2);
                logs.Add($"Cobertura detectada ({roofBoxes.Count} elemento(s)): projeção de {dxM:F2} m × {dyM:F2} m ({roofAreaM2:F2} m²).");
            }
            else if (request.Points.Count > 0)
            {
                double eaveMargin = UnitUtils.ConvertToInternalUnits(0.60, UnitTypeId.Meters);
                minX = request.Points.Min(p => UnitUtils.ConvertToInternalUnits(p.X, UnitTypeId.Meters)) - eaveMargin;
                maxX = request.Points.Max(p => UnitUtils.ConvertToInternalUnits(p.X, UnitTypeId.Meters)) + eaveMargin;
                minY = request.Points.Min(p => UnitUtils.ConvertToInternalUnits(p.Y, UnitTypeId.Meters)) - eaveMargin;
                maxY = request.Points.Max(p => UnitUtils.ConvertToInternalUnits(p.Y, UnitTypeId.Meters)) + eaveMargin;
                eaveZFeet = baseLevel.Elevation + UnitUtils.ConvertToInternalUnits(Math.Max(2.80, request.TrunkOffsetMeters), UnitTypeId.Meters);

                double dxM = UnitUtils.ConvertFromInternalUnits(maxX - minX, UnitTypeId.Meters);
                double dyM = UnitUtils.ConvertFromInternalUnits(maxY - minY, UnitTypeId.Meters);
                roofAreaM2 = Math.Round(dxM * dyM, 2);
                logs.Add($"Nenhuma cobertura explícita encontrada: projeção estimada pela planta ({dxM:F2} m × {dyM:F2} m).");
            }
            else
            {
                throw new InvalidOperationException("Nenhuma cobertura (Telhado) ou ponto de referência encontrado para posicionar os condutores pluviais.");
            }

            double insetFeet = UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters);
            if ((maxX - minX) <= insetFeet * 3 || (maxY - minY) <= insetFeet * 3)
                insetFeet = 0;

            var corners = new[]
            {
                (Name: "Condutor Pluvial AP-01 (SO)", X: minX + insetFeet, Y: minY + insetFeet, OutDir: new XYZ(-1, -1, 0).Normalize()),
                (Name: "Condutor Pluvial AP-02 (SE)", X: maxX - insetFeet, Y: minY + insetFeet, OutDir: new XYZ(1, -1, 0).Normalize()),
                (Name: "Condutor Pluvial AP-03 (NO)", X: minX + insetFeet, Y: maxY - insetFeet, OutDir: new XYZ(-1, 1, 0).Normalize()),
                (Name: "Condutor Pluvial AP-04 (NE)", X: maxX - insetFeet, Y: maxY - insetFeet, OutDir: new XYZ(1, 1, 0).Normalize())
            };

            double diaMm = request.InitialDiameterMm >= 75 ? request.InitialDiameterMm : (roofAreaM2 > 120 ? 100.0 : 75.0);
            double diaFeet = UnitUtils.ConvertToInternalUnits(diaMm, UnitTypeId.Millimeters);
            double groundZFeet = baseLevel.Elevation - UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters);
            double topZFeet = Math.Max(eaveZFeet, baseLevel.Elevation + UnitUtils.ConvertToInternalUnits(2.50, UnitTypeId.Meters));

            int pipesCreated = 0;
            int fittingsCreated = 0;
            int clashesAvoided = 0;
            double totalLengthFeet = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Condutores Pluviais 3D (NBR 10844)"))
            {
                tx.Start();

                foreach (var c in corners)
                {
                    double safeX = c.X;
                    double safeY = c.Y;

                    if (request.AvoidSteelFrameClashes)
                    {
                        double clearance = (diaFeet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);
                        bool shifted = FindSafeVerticalDrop(
                            safeX, safeY, groundZFeet, topZFeet, clearance, preferShiftAlongX: true,
                            context.SteelObstacles, context.OpeningObstacles,
                            out double outX, out double outY, out string reason);
                        if (shifted)
                        {
                            safeX = outX;
                            safeY = outY;
                            clashesAvoided++;
                            logs.Add($"[{reason}] {c.Name} deslocado para vão livre.");
                        }
                    }

                    XYZ pTop = new XYZ(safeX, safeY, topZFeet);
                    XYZ pBottom = new XYZ(safeX, safeY, groundZFeet);
                    XYZ pDischarge = pBottom + c.OutDir * UnitUtils.ConvertToInternalUnits(0.30, UnitTypeId.Meters);

                    var chainPts = BuildCleanPolyline(new[] { pTop, pBottom, pDischarge });
                    var created = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, diaFeet, chainPts, ref fittingsCreated);

                    if (created.Count > 0)
                    {
                        pipesCreated += created.Count;
                        totalLengthFeet += created.Sum(GetPipeLengthFeet);
                        logs.Add($"{c.Name}: DN {diaMm:F0} mm criado (H = {UnitUtils.ConvertFromInternalUnits(topZFeet - groundZFeet, UnitTypeId.Meters):F2} m).");
                    }
                }

                tx.Commit();
            }

            double totalLengthMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalLengthFeet, UnitTypeId.Meters), 2);
            string summary = $"Águas Pluviais 3D (NBR 10844): 4 condutores DN {diaMm:F0} mm criados ({totalLengthMeters:F2} m), {fittingsCreated} curva(s) e {clashesAvoided} desvio(s).";

            return new RevitAutoRoutingResult(4, pipesCreated, fittingsCreated, clashesAvoided, totalLengthMeters, summary, logs);
        }

        // =====================================================================
        // MOTOR ESPACIAL: PAREDES, ABERTURAS (PORTAS/JANELAS) E STEEL FRAME
        // =====================================================================
        private static RoutingSpatialContext BuildArchitecturalAndSteelContext(Document doc)
        {
            var steel = new List<WorldBox>();
            var openings = new List<WorldBox>();
            var walls = new List<WallAxisSegment>();

            CollectContextFromDoc(doc, Transform.Identity, steel, openings, walls);

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                CollectContextFromDoc(linkDoc, link.GetTotalTransform(), steel, openings, walls);
            }

            return new RoutingSpatialContext(steel, openings, walls);
        }

        private static void CollectContextFromDoc(
            Document targetDoc,
            Transform tr,
            List<WorldBox> steelOut,
            List<WorldBox> openingsOut,
            List<WallAxisSegment> wallsOut)
        {
            // 1. Coleta Eixos das Paredes (OST_Walls)
            var wallElems = new FilteredElementCollector(targetDoc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .ToList();

            foreach (var w in wallElems)
            {
                if (w.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                BoundingBoxXYZ? bb = w.get_BoundingBox(null);
                if (bb == null) continue;

                WorldBox wb = TransformBoundingBox(bb, tr);
                XYZ p0 = tr.OfPoint(line.GetEndPoint(0));
                XYZ p1 = tr.OfPoint(line.GetEndPoint(1));

                wallsOut.Add(new WallAxisSegment(p0.X, p0.Y, p1.X, p1.Y, wb.MinZ, wb.MaxZ, w.Width));
            }

            // 2. Coleta Portas e Janelas (OST_Doors, OST_Windows) com margem lateral de 10 cm
            double openingMarginFeet = UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters);
            var openingCats = new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows };
            var openingElems = new FilteredElementCollector(targetDoc)
                .WherePasses(new ElementMulticategoryFilter(openingCats))
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var op in openingElems)
            {
                BoundingBoxXYZ? bb = op.get_BoundingBox(null);
                if (bb == null) continue;
                WorldBox wb = TransformBoundingBox(bb, tr);
                openingsOut.Add(new WorldBox(
                    wb.MinX - openingMarginFeet,
                    wb.MinY - openingMarginFeet,
                    wb.MinZ,
                    wb.MaxX + openingMarginFeet,
                    wb.MaxY + openingMarginFeet,
                    wb.MaxZ));
            }

            // 3. Coleta Perfis Steel Frame (OST_StructuralFraming, OST_StructuralColumns, GenericModel metálico)
            var steelCats = new[]
            {
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_GenericModel
            };

            var steelElems = new FilteredElementCollector(targetDoc)
                .WherePasses(new ElementMulticategoryFilter(steelCats))
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element elem in steelElems)
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

                if ((dx > 3.5 && dy > 3.5) || dz < 0.05) continue;

                steelOut.Add(wb);
            }
        }

        private static bool TrySnapPointToNearestWall(
            double xFeet,
            double yFeet,
            double zFeet,
            XYZ defaultOutDir,
            List<WallAxisSegment> walls,
            out double snappedXFeet,
            out double snappedYFeet,
            out XYZ outwardDir)
        {
            snappedXFeet = xFeet;
            snappedYFeet = yFeet;
            outwardDir = defaultOutDir;

            if (walls.Count == 0) return false;

            double maxSearchDistFeet = UnitUtils.ConvertToInternalUnits(0.85, UnitTypeId.Meters);
            double bestDist = double.MaxValue;
            double bestProjX = xFeet;
            double bestProjY = yFeet;

            foreach (var w in walls)
            {
                if (zFeet < w.MinZ - 1.5 || zFeet > w.MaxZ + 1.5) continue;

                double dx = w.X2 - w.X1;
                double dy = w.Y2 - w.Y1;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < 1e-6) continue;

                double t = Math.Clamp(((xFeet - w.X1) * dx + (yFeet - w.Y1) * dy) / lenSq, 0.0, 1.0);
                double projX = w.X1 + t * dx;
                double projY = w.Y1 + t * dy;

                double dist = Math.Sqrt(Math.Pow(xFeet - projX, 2) + Math.Pow(yFeet - projY, 2));
                if (dist < bestDist && dist <= maxSearchDistFeet)
                {
                    bestDist = dist;
                    bestProjX = projX;
                    bestProjY = projY;
                }
            }

            if (bestDist > maxSearchDistFeet)
                return false;

            snappedXFeet = bestProjX;
            snappedYFeet = bestProjY;

            XYZ toFixture = new XYZ(xFeet - bestProjX, yFeet - bestProjY, 0);
            if (toFixture.GetLength() >= 0.05)
                outwardDir = toFixture.Normalize();

            return true;
        }

        private static bool FindSafeVerticalDrop(
            double xFeet,
            double yFeet,
            double zMinFeet,
            double zMaxFeet,
            double clearanceFeet,
            bool preferShiftAlongX,
            List<WorldBox> steelObstacles,
            List<WorldBox> openingObstacles,
            out double safeXFeet,
            out double safeYFeet,
            out string reason)
        {
            safeXFeet = xFeet;
            safeYFeet = yFeet;
            reason = "";

            bool hitsOpening = HasVerticalClash(xFeet, yFeet, zMinFeet, zMaxFeet, clearanceFeet, openingObstacles);
            bool hitsSteel = HasVerticalClash(xFeet, yFeet, zMinFeet, zMaxFeet, clearanceFeet, steelObstacles);

            if (!hitsOpening && !hitsSteel)
                return false;

            reason = hitsOpening ? "Vão Porta/Janela" : "Steel Frame";

            // Deslocamentos progressivos (até 90 cm para contornar larguras de portas/janelas)
            double[] offsetsCm = { 8, -8, 15, -15, 25, -25, 35, -35, 45, -45, 60, -60, 75, -75, 90, -90 };

            foreach (double cm in offsetsCm)
            {
                double delta = UnitUtils.ConvertToInternalUnits(cm / 100.0, UnitTypeId.Meters);
                double candX = preferShiftAlongX ? xFeet + delta : xFeet;
                double candY = preferShiftAlongX ? yFeet : yFeet + delta;

                if (!HasVerticalClash(candX, candY, zMinFeet, zMaxFeet, clearanceFeet, openingObstacles) &&
                    !HasVerticalClash(candX, candY, zMinFeet, zMaxFeet, clearanceFeet, steelObstacles))
                {
                    safeXFeet = candX;
                    safeYFeet = candY;
                    return true;
                }
            }

            foreach (double cm in offsetsCm)
            {
                double delta = UnitUtils.ConvertToInternalUnits(cm / 100.0, UnitTypeId.Meters);
                double candX = preferShiftAlongX ? xFeet : xFeet + delta;
                double candY = preferShiftAlongX ? yFeet + delta : yFeet;

                if (!HasVerticalClash(candX, candY, zMinFeet, zMaxFeet, clearanceFeet, openingObstacles) &&
                    !HasVerticalClash(candX, candY, zMinFeet, zMaxFeet, clearanceFeet, steelObstacles))
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
        // AUXILIARES NBR 8160 (UHC E CAIXA SIFONADA) E DETECÇÃO BIM
        // =====================================================================
        private static int GetFixtureUhc(DetectedRoutingPoint pt)
        {
            string cat = RemoverAcentos(pt.FixtureCategory).ToLowerInvariant();
            if (cat.Contains("vaso") || cat.Contains("bacia")) return 6;
            if (cat.Contains("cozinha") || cat.Contains("pia")) return 3;
            if (cat.Contains("tanque")) return 3;
            if (cat.Contains("maquina") || cat.Contains("lavadora")) return 3;
            if (cat.Contains("chuveiro") || cat.Contains("ducha")) return 2;
            if (cat.Contains("banheira")) return 2;
            if (cat.Contains("lavatorio") || cat.Contains("cuba") || cat.Contains("bide")) return 1;
            return 2;
        }

        private static bool IsToiletFixture(DetectedRoutingPoint pt)
        {
            string cat = RemoverAcentos(pt.FixtureCategory).ToLowerInvariant();
            return cat.Contains("vaso") || cat.Contains("bacia");
        }

        private static bool IsBathroomGreywaterFixture(DetectedRoutingPoint pt)
        {
            string cat = RemoverAcentos(pt.FixtureCategory).ToLowerInvariant();
            return cat.Contains("chuveiro") || cat.Contains("ducha") ||
                   cat.Contains("lavatorio") || cat.Contains("cuba") ||
                   cat.Contains("bide") || cat.Contains("banheira");
        }

        private static FamilySymbol? FindSiphonedBoxFamilySymbol(Document doc)
        {
            var cats = new[] { BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PlumbingFixtures };
            var symbols = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(cats))
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .ToList();

            return symbols.FirstOrDefault(s =>
            {
                string n = RemoverAcentos($"{s.FamilyName} {s.Name}").ToLowerInvariant();
                return n.Contains("sifonada") || n.Contains("caixa sif") || n.Contains("ralo sif");
            });
        }

        private static List<List<DetectedRoutingPoint>> ClusterPointsByDistance(List<DetectedRoutingPoint> points, double maxDistMeters)
        {
            var visited = new HashSet<int>();
            var clusters = new List<List<DetectedRoutingPoint>>();

            for (int i = 0; i < points.Count; i++)
            {
                if (visited.Contains(i)) continue;
                var cluster = new List<DetectedRoutingPoint>();
                var queue = new Queue<int>();
                queue.Enqueue(i);
                visited.Add(i);

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    var p1 = points[idx];
                    cluster.Add(p1);

                    for (int j = 0; j < points.Count; j++)
                    {
                        if (visited.Contains(j)) continue;
                        var p2 = points[j];
                        double d = Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
                        if (d <= maxDistMeters && Math.Abs(p1.BaseZ - p2.BaseZ) <= 1.5)
                        {
                            visited.Add(j);
                            queue.Enqueue(j);
                        }
                    }
                }
                clusters.Add(cluster);
            }

            return clusters;
        }

        private static void CollectRoofsFromDoc(Document targetDoc, Transform tr, List<WorldBox> output)
        {
            var roofs = new FilteredElementCollector(targetDoc)
                .OfCategory(BuiltInCategory.OST_Roofs)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var r in roofs)
            {
                BoundingBoxXYZ? bb = r.get_BoundingBox(null);
                if (bb == null) continue;
                output.Add(TransformBoundingBox(bb, tr));
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

        private static List<Pipe> ConnectTrunkSegments(
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

                PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, pipeTypeId);
                Pipe trunkPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, a, b);
                SetPipeDiameter(trunkPipe, diameterFeet);
                SetPipeComment(trunkPipe, $"BahrHidroAuto|Disc={GetSystemTagFromPipe(trunkPipe)}");
                trunkSegments.Add(trunkPipe);
                pipesCreated++;
                totalLengthFeet += GetPipeLengthFeet(trunkPipe);
            }
            PipeFittingAutoConnector.ConnectPipesWithElbows(doc, trunkSegments);


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
            return trunkSegments;
        }

        private static bool IsVerticalPipeCoveringZ(Pipe pipe, double targetZFeet)
        {
            if (pipe.Location is not LocationCurve lc) return false;
            XYZ p0 = lc.Curve.GetEndPoint(0);
            XYZ p1 = lc.Curve.GetEndPoint(1);
            double horiz = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
            if (horiz > 0.05) return false;

            double minZ = Math.Min(p0.Z, p1.Z) + UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters);
            double maxZ = Math.Max(p0.Z, p1.Z) - UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters);
            return targetZFeet >= minZ && targetZFeet <= maxZ;
        }

        private static bool TryInsertValveOnPipe(
            Document doc,
            Pipe pipe,
            XYZ breakPoint,
            Level baseLevel,
            bool preferPressureValve,
            out string valveFamilyName)
        {
            valveFamilyName = "";
            try
            {
                var symbols = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeAccessory)
                    .WhereElementIsElementType()
                    .OfType<FamilySymbol>()
                    .ToList();

                if (symbols.Count == 0) return false;

                FamilySymbol? chosen = preferPressureValve
                    ? symbols.FirstOrDefault(s => RemoverAcentos($"{s.FamilyName} {s.Name}").ToLowerInvariant().Contains("pressao")) ??
                      symbols.FirstOrDefault(s => RemoverAcentos($"{s.FamilyName} {s.Name}").ToLowerInvariant().Contains("chuveiro")) ??
                      symbols.FirstOrDefault(s => RemoverAcentos($"{s.FamilyName} {s.Name}").ToLowerInvariant().Contains("registro")) ??
                      symbols.FirstOrDefault()
                    : symbols.FirstOrDefault(s => RemoverAcentos($"{s.FamilyName} {s.Name}").ToLowerInvariant().Contains("gaveta")) ??
                      symbols.FirstOrDefault(s => RemoverAcentos($"{s.FamilyName} {s.Name}").ToLowerInvariant().Contains("esfera")) ??
                      symbols.FirstOrDefault(s => RemoverAcentos($"{s.FamilyName} {s.Name}").ToLowerInvariant().Contains("registro")) ??
                      symbols.FirstOrDefault();

                if (chosen == null) return false;
                if (!chosen.IsActive) chosen.Activate();

                if (pipe.Location is not LocationCurve lc) return false;
                XYZ pStart = lc.Curve.GetEndPoint(0);
                XYZ pEnd = lc.Curve.GetEndPoint(1);
                XYZ pipeDir = (pEnd - pStart).Normalize();

                ElementId secondId = PlumbingUtils.BreakCurve(doc, pipe.Id, breakPoint);
                if (doc.GetElement(secondId) is not Pipe secondPipe) return false;

                FamilyInstance valve = doc.Create.NewFamilyInstance(breakPoint, chosen, baseLevel, StructuralType.NonStructural);

                var valveConns = new List<Connector>();
                if (valve.MEPModel?.ConnectorManager?.Connectors != null)
                {
                    foreach (Connector c in valve.MEPModel.ConnectorManager.Connectors)
                    {
                        if (c.Domain == Domain.DomainPiping)
                            valveConns.Add(c);
                    }
                }

                if (valveConns.Count >= 2)
                {
                    XYZ connDir = (valveConns[1].Origin - valveConns[0].Origin);
                    if (connDir.GetLength() > 1e-5)
                    {
                        connDir = connDir.Normalize();
                        XYZ rotAxis = connDir.CrossProduct(pipeDir);
                        double angle = connDir.AngleTo(pipeDir);
                        if (rotAxis.GetLength() > 1e-5 && angle > 1e-4)
                        {
                            Line axisLine = Line.CreateBound(breakPoint, breakPoint + rotAxis.Normalize());
                            ElementTransformUtils.RotateElement(doc, valve.Id, axisLine, angle);
                        }
                    }

                    Connector? p1Conn = GetClosestConnector(pipe, breakPoint);
                    Connector? p2Conn = GetClosestConnector(secondPipe, breakPoint);
                    Connector vConn1 = valveConns.OrderBy(c => c.Origin.DistanceTo(pStart)).First();
                    Connector vConn2 = valveConns.OrderBy(c => c.Origin.DistanceTo(pEnd)).First();

                    if (p1Conn != null && !p1Conn.IsConnected && !vConn1.IsConnected)
                        try { p1Conn.ConnectTo(vConn1); } catch { }
                    if (p2Conn != null && !p2Conn.IsConnected && !vConn2.IsConnected)
                        try { p2Conn.ConnectTo(vConn2); } catch { }
                }

                valveFamilyName = $"{chosen.FamilyName} ({chosen.Name})";
                return true;
            }
            catch
            {
                return false;
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

                PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, pipeTypeId);
                Pipe pipe = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, points[i], points[i + 1]);
                SetPipeDiameter(pipe, diameterFeet);
                SetPipeComment(pipe, $"BahrHidroAuto|Disc={GetSystemTagFromPipe(pipe)}");

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
            PipeFittingAutoConnector.ConnectPipesWithElbows(doc, createdPipes);

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
        private sealed record WallAxisSegment(double X1, double Y1, double X2, double Y2, double MinZ, double MaxZ, double Width);
        private sealed record RoutingSpatialContext(List<WorldBox> SteelObstacles, List<WorldBox> OpeningObstacles, List<WallAxisSegment> Walls);
    
        private static string GetSystemTagFromPipe(Pipe pipe)
        {
            string sys = RemoverAcentos(pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "").ToLowerInvariant();
            if (sys.Contains("quente") || sys.Contains("hot")) return "HotWater";
            if (sys.Contains("vent")) return "Ventilation";
            if (sys.Contains("pluv") || sys.Contains("storm")) return "StormDrainage";
            if (sys.Contains("esgoto") || sys.Contains("sanit")) return "SanitarySewer";
            return "ColdWater";
        }

}
}
