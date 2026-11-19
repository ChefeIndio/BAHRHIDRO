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

        public static RevitAutoRoutingResult ExecuteRouting(UIDocument uidoc, Window ownerWindow, RevitAutoRoutingRequest request)
        {
            Document doc = uidoc.Document;
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

            // 1. Coleta os aparelhos sanitários alvo
            List<FamilyInstance> rawFixtures;
            if (request.SelectedFixturesOnly)
            {
                rawFixtures = PickFixturesFromModel(uidoc, ownerWindow);
                if (rawFixtures.Count == 0)
                {
                    return new RevitAutoRoutingResult(0, 0, 0, 0, "Operação cancelada ou nenhum aparelho selecionado.", logs);
                }
            }
            else
            {
                rawFixtures = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .ToList();

                // Se um nível foi escolhido, filtra aparelhos próximos a esse pavimento (faixa de -0,5m a +3,2m do nível)
                if (request.LevelId.HasValue)
                {
                    double minZ = baseLevel.Elevation - UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Meters);
                    double maxZ = baseLevel.Elevation + UnitUtils.ConvertToInternalUnits(3.2, UnitTypeId.Meters);
                    var levelFixtures = rawFixtures.Where(f =>
                    {
                        double z = GetElementZ(f);
                        return z >= minZ && z <= maxZ;
                    }).ToList();

                    if (levelFixtures.Count > 0)
                        rawFixtures = levelFixtures;
                }
            }

            // 2. Extrai conectores de Água Fria válidos dos aparelhos
            var targets = new List<FixtureTarget>();
            foreach (var fi in rawFixtures)
            {
                Connector? cwConn = FindColdWaterConnector(fi);
                if (cwConn == null)
                {
                    logs.Add($"Ignorado: '{fi.Symbol?.FamilyName} - {fi.Name}' (sem conector hidráulico MEP).");
                    continue;
                }

                if (cwConn.IsConnected)
                {
                    logs.Add($"Ignorado: '{fi.Symbol?.FamilyName} - {fi.Name}' (conector já está ligado a um tubo).");
                    continue;
                }

                XYZ dir = cwConn.CoordinateSystem.BasisZ;
                XYZ dirXY = new XYZ(dir.X, dir.Y, 0);
                if (dirXY.GetLength() < 0.05)
                    dirXY = XYZ.BasisY;
                else
                    dirXY = dirXY.Normalize();

                targets.Add(new FixtureTarget(fi, cwConn, cwConn.Origin, dirXY));
            }

            if (targets.Count == 0)
                throw new InvalidOperationException("Nenhum aparelho sanitário livre com conector de Água Fria foi encontrado para traçar.");

            // 3. Calcula cota Z da linha tronco principal
            double offsetFeet = UnitUtils.ConvertToInternalUnits(request.TrunkOffsetMeters, UnitTypeId.Meters);
            double trunkZ = baseLevel.Elevation + offsetFeet;
            double diameterFeet = UnitUtils.ConvertToInternalUnits(request.InitialDiameterMm, UnitTypeId.Millimeters);

            // 4. Procura reservatório ou ponto de alimentação existente
            var supplyInfo = FindSupplyConnector(doc);

            // 5. Define eixo principal da linha tronco (X ou Y)
            var stubPoints = targets
                .Select(t => t.Origin + t.OutwardDirXY * UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters))
                .ToList();

            double minX = stubPoints.Min(p => p.X);
            double maxX = stubPoints.Max(p => p.X);
            double minY = stubPoints.Min(p => p.Y);
            double maxY = stubPoints.Max(p => p.Y);

            bool trunkAlongX = (maxX - minX) >= (maxY - minY);
            double trunkConstCoord = trunkAlongX
                ? (supplyInfo.HasValue ? supplyInfo.Value.Origin.Y : stubPoints.Average(p => p.Y))
                : (supplyInfo.HasValue ? supplyInfo.Value.Origin.X : stubPoints.Average(p => p.X));

            int pipesCreated = 0;
            int fittingsCreated = 0;
            double totalLengthFeet = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Traçado Automático 3D"))
            {
                tx.Start();

                var trunkJunctions = new List<TrunkJunction>();

                // 6. Cria os ramais individuais de cada aparelho até a linha tronco
                for (int i = 0; i < targets.Count; i++)
                {
                    var t = targets[i];
                    XYZ p0 = t.Origin;
                    XYZ p1 = p0 + t.OutwardDirXY * UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters);
                    XYZ p2 = new XYZ(p1.X, p1.Y, trunkZ);

                    // Evita colisão exata de dois Tês no mesmo milímetro (espaçamento mínimo de 10 cm)
                    double rawCoord = trunkAlongX ? p2.X : p2.Y;
                    double adjustedCoord = EnsureUniqueTrunkCoord(rawCoord, trunkJunctions.Select(j => j.CoordAlongTrunk).ToList());

                    XYZ p2Adjusted = trunkAlongX
                        ? new XYZ(adjustedCoord, p2.Y, trunkZ)
                        : new XYZ(p2.X, adjustedCoord, trunkZ);

                    XYZ pTrunk = trunkAlongX
                        ? new XYZ(adjustedCoord, trunkConstCoord, trunkZ)
                        : new XYZ(trunkConstCoord, adjustedCoord, trunkZ);

                    var branchPoints = BuildCleanPolyline(new[] { p0, p1, p2, p2Adjusted, pTrunk });
                    var branchPipes = CreatePipeChain(doc, systemTypeId, pipeTypeId, baseLevel.Id, diameterFeet, branchPoints, ref fittingsCreated);

                    if (branchPipes.Count > 0)
                    {
                        pipesCreated += branchPipes.Count;
                        totalLengthFeet += branchPipes.Sum(GetPipeLengthFeet);

                        // Conecta o primeiro tubo diretamente no conector do aparelho sanitário
                        Connector? startConn = GetClosestConnector(branchPipes[0], p0);
                        if (startConn != null && !startConn.IsConnected && !t.Connector.IsConnected)
                        {
                            try { startConn.ConnectTo(t.Connector); } catch { }
                        }

                        Pipe lastBranchPipe = branchPipes[^1];
                        Connector? trunkEndConn = GetClosestConnector(lastBranchPipe, pTrunk);
                        trunkJunctions.Add(new TrunkJunction(adjustedCoord, pTrunk, trunkEndConn, false));

                        logs.Add($"Conectado: {t.Fixture.Symbol?.FamilyName} ({t.Fixture.Name}) → {branchPipes.Count} trecho(s).");
                    }
                }

                // 7. Conecta a alimentação (Reservatório ou início da linha tronco)
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
                        logs.Insert(0, $"Alimentação conectada a partir de '{supplyInfo.Value.Name}'.");
                    }
                }
                else if (trunkJunctions.Count > 0)
                {
                    // Cria trecho inicial de alimentação 0,80m antes do primeiro ramal
                    var orderedPreview = trunkJunctions.OrderBy(j => j.CoordAlongTrunk).ToList();
                    double firstCoord = orderedPreview[0].CoordAlongTrunk - UnitUtils.ConvertToInternalUnits(0.80, UnitTypeId.Meters);
                    XYZ pSupplyStart = trunkAlongX
                        ? new XYZ(firstCoord, trunkConstCoord, trunkZ)
                        : new XYZ(trunkConstCoord, firstCoord, trunkZ);

                    trunkJunctions.Add(new TrunkJunction(firstCoord, pSupplyStart, null, true));
                    logs.Insert(0, "Nenhum reservatório livre detectado: criada ponta de alimentação no início da linha tronco.");
                }

                // 8. Gera os segmentos da Linha Tronco Principal e insere Tês / Cotovelos nas junções
                var orderedJunctions = trunkJunctions.OrderBy(j => j.CoordAlongTrunk).ToList();
                var trunkSegments = new List<Pipe>();

                for (int i = 0; i < orderedJunctions.Count - 1; i++)
                {
                    XYZ a = orderedJunctions[i].Point;
                    XYZ b = orderedJunctions[i + 1].Point;
                    if (a.DistanceTo(b) < MinSegmentFeet) continue;

                    Pipe trunkPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, baseLevel.Id, a, b);
                    SetPipeDiameter(trunkPipe, diameterFeet);
                    trunkSegments.Add(trunkPipe);
                    pipesCreated++;
                    totalLengthFeet += GetPipeLengthFeet(trunkPipe);
                }

                // Liga as junções ao longo do tronco (Cotovelo nas extremidades, Tê nas derivações intermediárias)
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

                tx.Commit();
            }

            double totalLengthMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(totalLengthFeet, UnitTypeId.Meters), 2);
            string planoLabel = request.RouteFromAbove ? "Por Cima (Entreforro/Laje)" : "Por Baixo (Piso)";
            string summary = $"Traçado 3D Concluído ({planoLabel}): {targets.Count} aparelho(s) conectado(s), {pipesCreated} tubo(s) gerado(s) ({totalLengthMeters:F2} m) e {fittingsCreated} conexão(ões) inserida(s).";

            return new RevitAutoRoutingResult(
                targets.Count,
                pipesCreated,
                fittingsCreated,
                totalLengthMeters,
                summary,
                logs);
        }

        private static List<FamilyInstance> PickFixturesFromModel(UIDocument uidoc, Window ownerWindow)
        {
            var result = new List<FamilyInstance>();
            try
            {
                ownerWindow.Hide();
                IList<Reference> refs = uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new FixtureSelectionFilter(),
                    "Selecione os aparelhos sanitários para traçar Água Fria e clique em 'Concluir':");

                foreach (var r in refs)
                {
                    if (uidoc.Document.GetElement(r) is FamilyInstance fi)
                        result.Add(fi);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
            }
            finally
            {
                ownerWindow.ShowDialog();
            }
            return result;
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

        private static double GetPipeLengthFeet(Pipe pipe)
        {
            Parameter? p = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            return p != null && p.HasValue ? p.AsDouble() : 0;
        }

        private static double GetElementZ(Element elem)
        {
            BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
            return bb != null ? (bb.Min.Z + bb.Max.Z) / 2.0 : 0;
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

        private sealed record FixtureTarget(FamilyInstance Fixture, Connector Connector, XYZ Origin, XYZ OutwardDirXY);
        private sealed record TrunkJunction(double CoordAlongTrunk, XYZ Point, Connector? BranchConnector, bool IsSupply);

        private sealed class FixtureSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) =>
                elem?.Category != null && elem.Category.Id.Value == (long)BuiltInCategory.OST_PlumbingFixtures;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
