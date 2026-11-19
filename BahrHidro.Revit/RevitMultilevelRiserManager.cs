using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitMultilevelRiserManager
    {
        private const double MinSegmentFeet = 0.065;
        private const double ConnectorToleranceFeet = 0.04;

        public static MultilevelRiserResult GenerateMultilevelRisers(Document doc)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (levels.Count < 2)
                throw new InvalidOperationException("O projeto precisa ter pelo menos 2 níveis (ex.: Térreo e Superior/Cobertura) para interligar prumadas multinível.");

            var allPipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .Where(p => !(p.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "")
                    .Contains("BahrHidroRiser", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (allPipes.Count == 0)
                throw new InvalidOperationException("Nenhuma rede horizontal/de pavimento encontrada. Gere os traçados nos pavimentos antes de interligar as prumadas multinível.");

            var walls = new List<(XYZ P0, XYZ P1)>();
            var obstacles = new List<(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)>();
            CollectWallsAndObstacles(doc, Transform.Identity, walls, obstacles);
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc != null)
                    CollectWallsAndObstacles(linkDoc, link.GetTotalTransform(), walls, obstacles);
            }

            // Agrupa tubos existentes por disciplina (ColdWater, HotWater, SanitarySewer, Ventilation)
            var byDiscipline = new Dictionary<string, List<Pipe>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in allPipes)
            {
                string disc = ClassifyPipeDiscipline(p);
                if (disc == "StormDrainage") continue; // Condutores pluviais já descem da cobertura
                if (!byDiscipline.ContainsKey(disc))
                    byDiscipline[disc] = new List<Pipe>();
                byDiscipline[disc].Add(p);
            }

            if (byDiscipline.Count == 0)
                throw new InvalidOperationException("Nenhuma rede de Água Fria, Água Quente, Esgoto ou Ventilação encontrada para interligação vertical.");

            double clusterRadiusFeet = UnitUtils.ConvertToInternalUnits(2.40, UnitTypeId.Meters);
            Level lowestLevel = levels[0];
            Level highestLevel = levels[^1];

            int risersCreated = 0;
            int pipesCreated = 0;
            int fittingsCreated = 0;
            int clashesAvoided = 0;
            var logs = new List<string>();

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Prumadas Multinível (AF / AQ / TQ / CV)"))
            {
                tx.Start();

                foreach (var kv in byDiscipline)
                {
                    string disc = kv.Key;
                    var discPipes = kv.Value;
                    var clusters = ClusterPipesXY(discPipes, clusterRadiusFeet);

                    for (int cIdx = 0; cIdx < clusters.Count; cIdx++)
                    {
                        var cluster = clusters[cIdx];
                        if (cluster.Count == 0) continue;

                        Pipe samplePipe = cluster[0];
                        ElementId pipeTypeId = samplePipe.PipeType?.Id ?? ElementId.InvalidElementId;
                        ElementId sysTypeId = samplePipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;

                        if (pipeTypeId == ElementId.InvalidElementId || sysTypeId == ElementId.InvalidElementId)
                            continue;

                        // Calcula centro XY do núcleo e alinha ao eixo da parede mais próxima
                        double avgX = cluster.Average(p => GetMidpoint(p).X);
                        double avgY = cluster.Average(p => GetMidpoint(p).Y);
                        SnapToNearestWall(avgX, avgY, walls, out double wallX, out double wallY);

                        // Soma pesos acumulados de todos os pavimentos atendidos por este núcleo
                        double totalWeight = cluster.Sum(ExtractWeightFromComment);
                        if (totalWeight <= 0) totalWeight = Math.Max(0.4, cluster.Count * 0.3);

                        double riserDiaMm = ResolveRiserDiameterMm(disc, totalWeight);
                        double riserDiaFeet = UnitUtils.ConvertToInternalUnits(riserDiaMm, UnitTypeId.Millimeters);

                        // Define cota base e cota topo da coluna conforme a disciplina normativa
                        double minPipeZ = cluster.Min(p => GetMidpoint(p).Z);
                        double maxPipeZ = cluster.Max(p => GetMidpoint(p).Z);

                        double bottomZFeet;
                        double topZFeet;
                        string riserCode;

                        if (disc == "SanitarySewer")
                        {
                            riserCode = $"TQ-{cIdx + 1:D2}";
                            bottomZFeet = lowestLevel.Elevation - UnitUtils.ConvertToInternalUnits(0.30, UnitTypeId.Meters);
                            topZFeet = Math.Max(maxPipeZ, highestLevel.Elevation - UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters));
                        }
                        else if (disc == "Ventilation")
                        {
                            riserCode = $"CV-{cIdx + 1:D2}";
                            bottomZFeet = Math.Min(minPipeZ, lowestLevel.Elevation - UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters));
                            topZFeet = highestLevel.Elevation + UnitUtils.ConvertToInternalUnits(0.60, UnitTypeId.Meters);
                        }
                        else
                        {
                            riserCode = disc == "HotWater" ? $"AQ-{cIdx + 1:D2}" : $"AF-{cIdx + 1:D2}";
                            bottomZFeet = Math.Min(minPipeZ, lowestLevel.Elevation + UnitUtils.ConvertToInternalUnits(0.30, UnitTypeId.Meters));
                            topZFeet = highestLevel.Elevation + UnitUtils.ConvertToInternalUnits(0.40, UnitTypeId.Meters);
                        }

                        if ((topZFeet - bottomZFeet) < MinSegmentFeet * 2)
                            continue;

                        // Desvia de montantes Steel Frame e portas/janelas ao longo de toda a altura
                        double clearance = (riserDiaFeet / 2.0) + UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);
                        if (FindClashFreeColumnXY(wallX, wallY, bottomZFeet, topZFeet, clearance, obstacles, out double safeX, out double safeY))
                        {
                            wallX = safeX;
                            wallY = safeY;
                            clashesAvoided++;
                        }

                        // Identifica cotas Z onde há redes horizontais nos pavimentos para criar nós de derivação
                        var floorJunctionZs = cluster
                            .Select(p => GetMidpoint(p).Z)
                            .Where(z => z > bottomZFeet + MinSegmentFeet && z < topZFeet - MinSegmentFeet)
                            .OrderBy(z => z)
                            .Aggregate(new List<double>(), (acc, z) =>
                            {
                                if (acc.Count == 0 || Math.Abs(acc[^1] - z) > UnitUtils.ConvertToInternalUnits(0.40, UnitTypeId.Meters))
                                    acc.Add(z);
                                return acc;
                            });

                        var columnNodesZ = new List<double> { bottomZFeet };
                        columnNodesZ.AddRange(floorJunctionZs);
                        columnNodesZ.Add(topZFeet);

                        var verticalSegments = new List<Pipe>();
                        for (int i = 0; i < columnNodesZ.Count - 1; i++)
                        {
                            XYZ pA = new XYZ(wallX, wallY, columnNodesZ[i]);
                            XYZ pB = new XYZ(wallX, wallY, columnNodesZ[i + 1]);
                            if (pA.DistanceTo(pB) < MinSegmentFeet) continue;

                            Pipe vPipe = Pipe.Create(doc, sysTypeId, pipeTypeId, lowestLevel.Id, pA, pB);
                            SetPipeDiameter(vPipe, riserDiaFeet);
                            SetPipeComment(vPipe, $"BahrHidroRiser|{riserCode}|Disc={disc}");
                            verticalSegments.Add(vPipe);
                            pipesCreated++;
                        }

                        if (verticalSegments.Count > 0)
                        {
                            risersCreated++;

                            // Interliga cada pavimento ao nó correspondente da prumada
                            foreach (double jZ in floorJunctionZs)
                            {
                                XYZ jPt = new XYZ(wallX, wallY, jZ);
                                Pipe? nearestFloorPipe = cluster
                                    .Where(p => Math.Abs(GetMidpoint(p).Z - jZ) <= UnitUtils.ConvertToInternalUnits(0.35, UnitTypeId.Meters))
                                    .OrderBy(p => GetMidpoint(p).DistanceTo(jPt))
                                    .FirstOrDefault();

                                if (nearestFloorPipe == null) continue;

                                XYZ floorAttachPt = GetClosestPointOnPipe(nearestFloorPipe, jPt);
                                if (floorAttachPt.DistanceTo(jPt) < MinSegmentFeet) continue;

                                Pipe tiePipe = Pipe.Create(doc, sysTypeId, pipeTypeId, lowestLevel.Id, floorAttachPt, jPt);
                                SetPipeDiameter(tiePipe, riserDiaFeet);
                                SetPipeComment(tiePipe, $"BahrHidroRiser|{riserCode}-Ramal|Disc={disc}");
                                pipesCreated++;

                                // Conecta Tê na coluna vertical
                                var touchingVert = new List<Connector>();
                                foreach (var vp in verticalSegments)
                                {
                                    Connector? vc = GetClosestConnector(vp, jPt);
                                    if (vc != null && vc.Origin.DistanceTo(jPt) <= ConnectorToleranceFeet)
                                        touchingVert.Add(vc);
                                }

                                Connector? tieEndConn = GetClosestConnector(tiePipe, jPt);
                                if (tieEndConn != null && touchingVert.Count >= 2)
                                {
                                    try
                                    {
                                        doc.Create.NewTeeFitting(touchingVert[0], touchingVert[1], tieEndConn);
                                        fittingsCreated++;
                                    }
                                    catch { }
                                }
                                else if (tieEndConn != null && touchingVert.Count == 1)
                                {
                                    try
                                    {
                                        doc.Create.NewElbowFitting(touchingVert[0], tieEndConn);
                                        fittingsCreated++;
                                    }
                                    catch { }
                                }
                            }

                            double hTotalM = Math.Round(UnitUtils.ConvertFromInternalUnits(topZFeet - bottomZFeet, UnitTypeId.Meters), 2);
                            string extraInfo = (disc == "ColdWater" || disc == "HotWater")
                                ? $"ΣP = {totalWeight:F2} → DN {riserDiaMm:F0} mm"
                                : $"DN {riserDiaMm:F0} mm";
                            logs.Add($"Prumada {riserCode} ({disc}): {extraInfo} | Altura = {hTotalM:F2} m ({floorJunctionZs.Count} pavimento(s) interligado(s)).");
                        }
                    }
                }

                tx.Commit();
            }

            if (risersCreated == 0)
                throw new InvalidOperationException("Não foi possível criar colunas verticais. Verifique se existem tubos modelados nos pavimentos.");

            string summary = $"Prumadas Multinível: {risersCreated} coluna(s) criada(s) interligando {levels.Count} nível(is), {pipesCreated} tubo(s), {fittingsCreated} conexão(ões) e {clashesAvoided} desvio(s) LSF.";
            return new MultilevelRiserResult(risersCreated, levels.Count, pipesCreated, fittingsCreated, clashesAvoided, summary, logs);
        }

        private static double ResolveRiserDiameterMm(string disc, double totalWeight)
        {
            if (disc == "SanitarySewer") return 100.0;
            if (disc == "Ventilation") return 50.0;

            // NBR 5626: Q = 0.30 * sqrt(ΣP), garantindo V <= 3.0 m/s
            double flowLs = 0.30 * Math.Sqrt(Math.Max(0.1, totalWeight));
            if (flowLs <= 0.45) return 25.0;
            if (flowLs <= 1.15) return 32.0;
            if (flowLs <= 2.10) return 40.0;
            return 50.0;
        }

        private static double ExtractWeightFromComment(Pipe p)
        {
            string comment = p.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            if (!comment.StartsWith("BahrHidro|", StringComparison.OrdinalIgnoreCase))
                return 0.0;

            var parts = comment.Split('|');
            if (parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                return w;

            return 0.0;
        }

        private static string ClassifyPipeDiscipline(Pipe p)
        {
            string comment = p.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            if (comment.Contains("Disc=HotWater", StringComparison.OrdinalIgnoreCase)) return "HotWater";
            if (comment.Contains("Disc=SanitarySewer", StringComparison.OrdinalIgnoreCase)) return "SanitarySewer";
            if (comment.Contains("Disc=Ventilation", StringComparison.OrdinalIgnoreCase)) return "Ventilation";
            if (comment.Contains("Disc=StormDrainage", StringComparison.OrdinalIgnoreCase)) return "StormDrainage";
            if (comment.Contains("Disc=ColdWater", StringComparison.OrdinalIgnoreCase)) return "ColdWater";

            string sys = RemoverAcentos(p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "").ToLowerInvariant();
            if (sys.Contains("quente") || sys.Contains("hot")) return "HotWater";
            if (sys.Contains("vent")) return "Ventilation";
            if (sys.Contains("pluv") || sys.Contains("storm")) return "StormDrainage";
            if (sys.Contains("esgoto") || sys.Contains("sanit")) return "SanitarySewer";
            return "ColdWater";
        }

        private static void CollectWallsAndObstacles(
            Document targetDoc,
            Transform tr,
            List<(XYZ P0, XYZ P1)> wallsOut,
            List<(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)> obstaclesOut)
        {
            foreach (var w in new FilteredElementCollector(targetDoc).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>())
            {
                if (w.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                wallsOut.Add((tr.OfPoint(line.GetEndPoint(0)), tr.OfPoint(line.GetEndPoint(1))));
            }

            var cats = new[]
            {
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Windows
            };

            foreach (var elem in new FilteredElementCollector(targetDoc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
            {
                BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
                if (bb == null) continue;
                XYZ[] corners =
                {
                    tr.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)),
                    tr.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)),
                    tr.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)),
                    tr.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z))
                };
                obstaclesOut.Add((
                    corners.Min(c => c.X),
                    corners.Min(c => c.Y),
                    corners.Min(c => c.Z),
                    corners.Max(c => c.X),
                    corners.Max(c => c.Y),
                    corners.Max(c => c.Z)));
            }
        }

        private static void SnapToNearestWall(double x, double y, List<(XYZ P0, XYZ P1)> walls, out double outX, out double outY)
        {
            outX = x;
            outY = y;
            double maxDist = UnitUtils.ConvertToInternalUnits(1.20, UnitTypeId.Meters);
            double bestDist = double.MaxValue;

            foreach (var w in walls)
            {
                double dx = w.P1.X - w.P0.X;
                double dy = w.P1.Y - w.P0.Y;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < 1e-6) continue;

                double t = Math.Clamp(((x - w.P0.X) * dx + (y - w.P0.Y) * dy) / lenSq, 0.0, 1.0);
                double px = w.P0.X + t * dx;
                double py = w.P0.Y + t * dy;
                double dist = Math.Sqrt(Math.Pow(x - px, 2) + Math.Pow(y - py, 2));

                if (dist < bestDist && dist <= maxDist)
                {
                    bestDist = dist;
                    outX = px;
                    outY = py;
                }
            }
        }

        private static bool FindClashFreeColumnXY(
            double x,
            double y,
            double minZ,
            double maxZ,
            double clearance,
            List<(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)> obstacles,
            out double safeX,
            out double safeY)
        {
            safeX = x;
            safeY = y;

            bool Collides(double cx, double cy)
            {
                foreach (var o in obstacles)
                {
                    if ((cx + clearance) >= o.MinX && (cx - clearance) <= o.MaxX &&
                        (cy + clearance) >= o.MinY && (cy - clearance) <= o.MaxY &&
                        maxZ >= o.MinZ && minZ <= o.MaxZ)
                        return true;
                }
                return false;
            }

            if (!Collides(x, y)) return false;

            double[] shiftsCm = { 10, -10, 20, -20, 35, -35, 50, -50, 75, -75 };
            foreach (double cm in shiftsCm)
            {
                double d = UnitUtils.ConvertToInternalUnits(cm / 100.0, UnitTypeId.Meters);
                if (!Collides(x + d, y)) { safeX = x + d; return true; }
                if (!Collides(x, y + d)) { safeY = y + d; return true; }
            }
            return false;
        }

        private static List<List<Pipe>> ClusterPipesXY(List<Pipe> pipes, double radiusFeet)
        {
            var visited = new HashSet<long>();
            var clusters = new List<List<Pipe>>();

            foreach (var seed in pipes)
            {
                if (visited.Contains(seed.Id.Value)) continue;
                var cluster = new List<Pipe>();
                var queue = new Queue<Pipe>();
                queue.Enqueue(seed);
                visited.Add(seed.Id.Value);

                while (queue.Count > 0)
                {
                    var curr = queue.Dequeue();
                    cluster.Add(curr);
                    XYZ m1 = GetMidpoint(curr);

                    foreach (var other in pipes)
                    {
                        if (visited.Contains(other.Id.Value)) continue;
                        XYZ m2 = GetMidpoint(other);
                        double distXY = Math.Sqrt(Math.Pow(m1.X - m2.X, 2) + Math.Pow(m1.Y - m2.Y, 2));
                        if (distXY <= radiusFeet)
                        {
                            visited.Add(other.Id.Value);
                            queue.Enqueue(other);
                        }
                    }
                }
                clusters.Add(cluster);
            }
            return clusters;
        }

        private static XYZ GetMidpoint(Pipe p)
        {
            return p.Location is LocationCurve lc ? lc.Curve.Evaluate(0.5, true) : XYZ.Zero;
        }

        private static XYZ GetClosestPointOnPipe(Pipe p, XYZ target)
        {
            if (p.Location is not LocationCurve lc) return GetMidpoint(p);
            XYZ a = lc.Curve.GetEndPoint(0);
            XYZ b = lc.Curve.GetEndPoint(1);
            return a.DistanceTo(target) <= b.DistanceTo(target) ? a : b;
        }

        private static Connector? GetClosestConnector(Pipe pipe, XYZ target)
        {
            ConnectorSet? conns = pipe.ConnectorManager?.Connectors;
            if (conns == null) return null;
            Connector? best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in conns)
            {
                double d = c.Origin.DistanceTo(target);
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
