using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BahrHidro.Core.Reporting;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitLsfFabricationManager
    {
        public static List<LsfPunchingHoleItem> AnalyzeLsfPunchingHoles(Document doc)
        {
            var result = new List<LsfPunchingHoleItem>();

            var pipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            if (pipes.Count == 0)
                return result;

            Level? baseLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

            double baseElevationFeet = baseLevel?.Elevation ?? 0.0;

            // Coleta montantes verticais LSF e paredes do modelo atual + vínculos IFC/RVT
            var studs = new List<VerticalStudInfo>();
            var walls = new List<WallPanelInfo>();

            CollectStudsAndWalls(doc, Transform.Identity, "Modelo", studs, walls);

            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                CollectStudsAndWalls(linkDoc, link.GetTotalTransform(), link.Name, studs, walls);
            }

            if (studs.Count == 0)
                return result;

            int holeSeq = 1;
            var recordedKeys = new HashSet<string>();

            foreach (Pipe pipe in pipes)
            {
                if (pipe.Location is not LocationCurve lc) continue;
                XYZ p0 = lc.Curve.GetEndPoint(0);
                XYZ p1 = lc.Curve.GetEndPoint(1);

                double horizLen = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                if (horizLen < UnitUtils.ConvertToInternalUnits(0.08, UnitTypeId.Meters))
                    continue; // Ignora prumadas estritamente verticais

                double diaMm = 25.0;
                Parameter? dParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (dParam != null && dParam.HasValue)
                    diaMm = Math.Round(UnitUtils.ConvertFromInternalUnits(dParam.AsDouble(), UnitTypeId.Millimeters), 0);

                string sysName = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "Hidráulica";
                double pipeRadiusFeet = UnitUtils.ConvertToInternalUnits((diaMm / 2.0) + 3.0, UnitTypeId.Millimeters);

                foreach (var stud in studs)
                {
                    if (!SegmentIntersectsStud2D(p0, p1, stud, pipeRadiusFeet, out XYZ hitPt))
                        continue;

                    double studMinZ = stud.MinZ;
                    double studMaxZ = stud.MaxZ;
                    if (hitPt.Z < studMinZ - pipeRadiusFeet || hitPt.Z > studMaxZ + pipeRadiusFeet)
                        continue;

                    string dedupKey = $"{pipe.Id.Value}_{stud.UniqueKey}";
                    if (recordedKeys.Contains(dedupKey))
                        continue;
                    recordedKeys.Add(dedupKey);

                    double requiredHoleMm = Math.Ceiling(diaMm + 6.0); // Folga padrão de 6 mm para bucha/grommet LSF
                    double webWidthMm = stud.WebWidthMm;
                    double occupancyPct = Math.Round((requiredHoleMm / webWidthMm) * 100.0, 1);

                    double distBottomM = UnitUtils.ConvertFromInternalUnits(hitPt.Z - studMinZ, UnitTypeId.Meters);
                    double distTopM = UnitUtils.ConvertFromInternalUnits(studMaxZ - hitPt.Z, UnitTypeId.Meters);
                    double heightFromFloorM = Math.Round(UnitUtils.ConvertFromInternalUnits(hitPt.Z - baseElevationFeet, UnitTypeId.Meters), 3);

                    var (wallName, xFromCornerM) = ResolveWallAndXCoordinate(doc, hitPt, walls);

                    bool webOk = occupancyPct <= 50.0;
                    bool trackClearanceOk = distBottomM >= 0.25 && distTopM >= 0.25;

                    string status = (webOk && trackClearanceOk)
                        ? "APROVADO (<= 50% Alma)"
                        : !webOk
                            ? $"ATENÇÃO: Furo {occupancyPct:F0}% > 50% (Reforço)"
                            : "ATENÇÃO: Próximo à guia (< 25 cm)";

                    result.Add(new LsfPunchingHoleItem(
                        $"FUR-{holeSeq:D2}",
                        wallName,
                        stud.DisplayName,
                        sysName,
                        diaMm,
                        requiredHoleMm,
                        webWidthMm,
                        occupancyPct,
                        xFromCornerM,
                        heightFromFloorM,
                        status));

                    holeSeq++;
                }
            }

            return result;
        }

        public static LsfPunchingAuditResult RunLsfPunchingAudit(Document doc)
        {
            var holes = AnalyzeLsfPunchingHoles(doc);
            if (holes.Count == 0)
            {
                return new LsfPunchingAuditResult(
                    0,
                    0,
                    0,
                    "Nenhum cruzamento horizontal de tubulação em montantes LSF foi detectado (traçado 100% livre de furação).",
                    new List<string> { "Zero perfurações necessárias nos perfis Steel Frame para o traçado atual." });
            }

            int approved = holes.Count(h => h.Status.StartsWith("APROVADO", StringComparison.OrdinalIgnoreCase));
            int warnings = holes.Count - approved;

            var details = holes.Select(h =>
                $"[{h.HoleId}] {h.WallOrKitName} | {h.StudName} | {h.SystemName} Ø{h.PipeDiameterMm:F0}mm → Furo Ø{h.RequiredHoleMm:F0}mm ({h.OccupancyPercent:F0}% alma) | X={h.PositionXFromWallStartMeters:F2}m, H={h.HeightHFromFloorMeters:F2}m → {h.Status}")
                .ToList();

            string summary = $"Auditoria Furação LSF: {holes.Count} furo(s) mapeado(s) | {approved} aprovado(s) | {warnings} ponto(s) com alerta estrutural.";
            return new LsfPunchingAuditResult(holes.Count, approved, warnings, summary, details);
        }

        public static (int ViewsAnnotated, int CalloutsPlaced, string Summary) ApplyFabricationCoordinatesToElevations(UIDocument uidoc)
        {
            Document doc = uidoc.Document;

            var elevationViews = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection))
                .Cast<ViewSection>()
                .Where(v => !v.IsTemplate && v.Name.StartsWith("BahrHidro - Parede Hidráulica", StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.Name)
                .ToList();

            if (elevationViews.Count == 0)
            {
                RevitDocumentationGenerator.GenerateHydraulicWallElevations(uidoc);
                elevationViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSection))
                    .Cast<ViewSection>()
                    .Where(v => !v.IsTemplate && v.Name.StartsWith("BahrHidro - Parede Hidráulica", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(v => v.Name)
                    .ToList();
            }

            if (elevationViews.Count == 0)
                throw new InvalidOperationException("Nenhuma vista de Elevação de Parede Hidráulica (1:20) encontrada.");

            ElementId defaultTextTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (defaultTextTypeId == ElementId.InvalidElementId)
            {
                var anyTextType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstOrDefault();
                if (anyTextType != null) defaultTextTypeId = anyTextType.Id;
            }

            if (defaultTextTypeId == ElementId.InvalidElementId)
                throw new InvalidOperationException("Nenhum tipo de nota de texto (TextNoteType) encontrado no template.");

            Level? baseLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();
            double baseElevationFeet = baseLevel?.Elevation ?? 0.0;

            var studs = new List<VerticalStudInfo>();
            var walls = new List<WallPanelInfo>();
            CollectStudsAndWalls(doc, Transform.Identity, "Modelo", studs, walls);
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc != null)
                    CollectStudsAndWalls(linkDoc, link.GetTotalTransform(), link.Name, studs, walls);
            }

            int viewsAnnotated = 0;
            int calloutsPlaced = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Cotas de Fabricação X e H (1:20)"))
            {
                tx.Start();

                foreach (ViewSection elev in elevationViews)
                {
                    var pipesInView = new FilteredElementCollector(doc, elev.Id)
                        .OfClass(typeof(Pipe))
                        .WhereElementIsNotElementType()
                        .Cast<Pipe>()
                        .ToList();

                    if (pipesInView.Count == 0) continue;

                    bool placedInThisView = false;
                    XYZ viewRight = elev.RightDirection;
                    XYZ viewUp = elev.UpDirection;

                    foreach (Pipe pipe in pipesInView)
                    {
                        if (pipe.Location is not LocationCurve lc) continue;
                        string comment = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";

                        // Anota todas as saídas de aparelhos (|H=) e todos os trechos horizontais dentro da parede
                        XYZ p0 = lc.Curve.GetEndPoint(0);
                        XYZ p1 = lc.Curve.GetEndPoint(1);
                        double horizLen = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                        bool isEndpoint = comment.Contains("|H=", StringComparison.OrdinalIgnoreCase);

                        if (!isEndpoint && horizLen < UnitUtils.ConvertToInternalUnits(0.20, UnitTypeId.Meters))
                            continue;

                        XYZ targetPt = isEndpoint ? p0 : lc.Curve.Evaluate(0.5, true);
                        var (wallName, xMeters) = ResolveWallAndXCoordinate(doc, targetPt, walls);
                        double hMeters = Math.Round(UnitUtils.ConvertFromInternalUnits(targetPt.Z - baseElevationFeet, UnitTypeId.Meters), 2);

                        double diaMm = 25;
                        Parameter? dParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (dParam != null && dParam.HasValue)
                            diaMm = Math.Round(UnitUtils.ConvertFromInternalUnits(dParam.AsDouble(), UnitTypeId.Millimeters), 0);

                        string label = isEndpoint
                            ? $"PONTO Ø{diaMm:F0}mm\nX = {xMeters:F2} m | H = {hMeters:F2} m"
                            : $"EIXO Ø{diaMm:F0}mm\nX = {xMeters:F2} m | H = {hMeters:F2} m";

                        XYZ textOrigin = targetPt + (viewRight * UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters)) + (viewUp * UnitUtils.ConvertToInternalUnits(0.12, UnitTypeId.Meters));

                        try
                        {
                            TextNote.Create(doc, elev.Id, textOrigin, label, defaultTextTypeId);
                            calloutsPlaced++;
                            placedInThisView = true;
                        }
                        catch { }
                    }

                    if (placedInThisView)
                        viewsAnnotated++;
                }

                tx.Commit();
            }

            if (elevationViews.Count > 0)
            {
                try { uidoc.ActiveView = elevationViews[0]; } catch { }
            }

            string summary = $"Cotas de Fabricação (1:20): {calloutsPlaced} chamada(s) técnica(s) (X e H) inserida(s) em {viewsAnnotated} vista(s) de parede.";
            return (viewsAnnotated, calloutsPlaced, summary);
        }

        private static void CollectStudsAndWalls(
            Document targetDoc,
            Transform tr,
            string sourceLabel,
            List<VerticalStudInfo> studsOut,
            List<WallPanelInfo> wallsOut)
        {
            var wallElems = new FilteredElementCollector(targetDoc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .ToList();

            int wallIdx = wallsOut.Count + 1;
            foreach (var w in wallElems)
            {
                if (w.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                XYZ p0 = tr.OfPoint(line.GetEndPoint(0));
                XYZ p1 = tr.OfPoint(line.GetEndPoint(1));
                double lenM = UnitUtils.ConvertFromInternalUnits(p0.DistanceTo(p1), UnitTypeId.Meters);
                if (lenM < 0.25) continue;

                string mark = w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                string wName = !string.IsNullOrWhiteSpace(mark) ? $"Painel {mark}" : $"Parede P-{wallIdx:D2}";
                wallsOut.Add(new WallPanelInfo(wName, p0, p1, lenM));
                wallIdx++;
            }

            var categories = new[]
            {
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_GenericModel
            };

            var elements = new FilteredElementCollector(targetDoc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element elem in elements)
            {
                string rawName = BuildElementName(elem);
                if (elem.Category?.Id.Value == (long)BuiltInCategory.OST_GenericModel)
                {
                    string lower = RemoverAcentos(rawName).ToLowerInvariant();
                    if (!lower.Contains("stud") && !lower.Contains("montante") && !lower.Contains("perfil") &&
                        !lower.Contains("steel") && !lower.Contains("lsf") && !lower.Contains("column"))
                        continue;
                }

                BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
                if (bb == null) continue;

                XYZ[] corners =
                {
                    tr.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)),
                    tr.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)),
                    tr.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)),
                    tr.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z))
                };

                double minX = corners.Min(c => c.X);
                double maxX = corners.Max(c => c.X);
                double minY = corners.Min(c => c.Y);
                double maxY = corners.Max(c => c.Y);
                double minZ = corners.Min(c => c.Z);
                double maxZ = corners.Max(c => c.Z);

                double dxM = UnitUtils.ConvertFromInternalUnits(maxX - minX, UnitTypeId.Meters);
                double dyM = UnitUtils.ConvertFromInternalUnits(maxY - minY, UnitTypeId.Meters);
                double dzM = UnitUtils.ConvertFromInternalUnits(maxZ - minZ, UnitTypeId.Meters);

                // Filtra montantes verticais (altura > 1,20 m e seção transversal <= 0,35 m)
                if (dzM < 1.20 || dxM > 0.35 || dyM > 0.35) continue;

                double webMm = Math.Round(Math.Clamp(Math.Max(dxM, dyM) * 1000.0, 60.0, 200.0), 0);
                if (webMm < 70) webMm = 90.0; // Padrão LSF 90 mm se perfil fino

                studsOut.Add(new VerticalStudInfo(
                    $"{sourceLabel}_{elem.Id.Value}",
                    rawName,
                    minX, maxX, minY, maxY, minZ, maxZ,
                    webMm));
            }
        }

        private static bool SegmentIntersectsStud2D(XYZ p0, XYZ p1, VerticalStudInfo stud, double radiusFeet, out XYZ hitPoint)
        {
            hitPoint = XYZ.Zero;
            double sMinX = stud.MinX - radiusFeet;
            double sMaxX = stud.MaxX + radiusFeet;
            double sMinY = stud.MinY - radiusFeet;
            double sMaxY = stud.MaxY + radiusFeet;

            // Amostra 25 pontos ao longo do trecho para capturar com precisão o ponto exato de cruzamento e cota Z
            const int steps = 25;
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;
                XYZ pt = p0 + (p1 - p0) * t;
                if (pt.X >= sMinX && pt.X <= sMaxX && pt.Y >= sMinY && pt.Y <= sMaxY)
                {
                    hitPoint = pt;
                    return true;
                }
            }
            return false;
        }

        private static (string WallOrKitName, double XFromStartMeters) ResolveWallAndXCoordinate(
            Document doc,
            XYZ point,
            List<WallPanelInfo> walls)
        {
            string roomLabel = "";
            try
            {
                Room? room = doc.GetRoomAtPoint(point);
                if (room != null && !string.IsNullOrWhiteSpace(room.Name))
                    roomLabel = $" ({room.Name})";
            }
            catch { }

            double bestDist = double.MaxValue;
            WallPanelInfo? bestWall = null;
            double bestX = 0.0;

            foreach (var w in walls)
            {
                double dx = w.P1.X - w.P0.X;
                double dy = w.P1.Y - w.P0.Y;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < 1e-6) continue;

                double t = Math.Clamp(((point.X - w.P0.X) * dx + (point.Y - w.P0.Y) * dy) / lenSq, 0.0, 1.0);
                double projX = w.P0.X + t * dx;
                double projY = w.P0.Y + t * dy;
                double dist = Math.Sqrt(Math.Pow(point.X - projX, 2) + Math.Pow(point.Y - projY, 2));

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestWall = w;
                    bestX = Math.Round(t * w.LengthMeters, 2);
                }
            }

            if (bestWall != null && bestDist <= UnitUtils.ConvertToInternalUnits(1.20, UnitTypeId.Meters))
                return ($"{bestWall.Name}{roomLabel}", bestX);

            return ($"Painel Hidráulico{roomLabel}", 0.0);
        }

        private static string BuildElementName(Element elem)
        {
            if (elem is FamilyInstance fi && !string.IsNullOrWhiteSpace(fi.Symbol?.FamilyName))
                return $"{fi.Symbol.FamilyName} ({fi.Name})";
            return elem.Name;
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

        private sealed record VerticalStudInfo(
            string UniqueKey,
            string DisplayName,
            double MinX,
            double MaxX,
            double MinY,
            double MaxY,
            double MinZ,
            double MaxZ,
            double WebWidthMm);

        private sealed record WallPanelInfo(string Name, XYZ P0, XYZ P1, double LengthMeters);
    }
}
