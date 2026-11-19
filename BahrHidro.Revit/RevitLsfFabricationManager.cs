using System;
using System.Collections.Generic;
using System.Linq;
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

            var (spatialIndex, _, _) = RevitSpatialCache.GetOrBuild(doc);
            if (spatialIndex.Studs.Count == 0)
                return result;

            var cfg = OfficeProfileService.LoadCurrent();
            double folgaMm = cfg.FolgaFuroMm > 0 ? cfg.FolgaFuroMm : 6.0;
            double maxAlmaPct = cfg.MaxFuroAlmaPercent > 0 ? cfg.MaxFuroAlmaPercent : 50.0;
            double minGuiaM = (cfg.DistanciaMinimaGuiaCm > 0 ? cfg.DistanciaMinimaGuiaCm : 25.0) / 100.0;

            Level? baseLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

            double baseElevationFeet = baseLevel?.Elevation ?? 0.0;

            int holeSeq = 1;
            var recordedKeys = new HashSet<string>();

            foreach (Pipe pipe in pipes)
            {
                if (pipe.Location is not LocationCurve lc) continue;
                XYZ p0 = lc.Curve.GetEndPoint(0);
                XYZ p1 = lc.Curve.GetEndPoint(1);

                double horizLen = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                if (horizLen < UnitUtils.ConvertToInternalUnits(0.08, UnitTypeId.Meters))
                    continue;

                double diaMm = 25.0;
                Parameter? dParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (dParam != null && dParam.HasValue)
                    diaMm = Math.Round(UnitUtils.ConvertFromInternalUnits(dParam.AsDouble(), UnitTypeId.Millimeters), 0);

                string sysName = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "Hidráulica";
                double pipeRadiusFeet = UnitUtils.ConvertToInternalUnits((diaMm / 2.0) + (folgaMm / 2.0), UnitTypeId.Millimeters);

                var candidateStuds = spatialIndex.QueryStudsAlongSegment(p0, p1, pipeRadiusFeet);

                foreach (var stud in candidateStuds)
                {
                    if (!SegmentIntersectsStud2D(p0, p1, stud, pipeRadiusFeet, out XYZ hitPt))
                        continue;

                    if (hitPt.Z < stud.MinZ - pipeRadiusFeet || hitPt.Z > stud.MaxZ + pipeRadiusFeet)
                        continue;

                    string dedupKey = $"{pipe.Id.Value}_{stud.UniqueKey}";
                    if (recordedKeys.Contains(dedupKey))
                        continue;
                    recordedKeys.Add(dedupKey);

                    double requiredHoleMm = Math.Ceiling(diaMm + folgaMm);
                    double webWidthMm = stud.WebWidthMm;
                    double occupancyPct = Math.Round((requiredHoleMm / webWidthMm) * 100.0, 1);

                    double distBottomM = UnitUtils.ConvertFromInternalUnits(hitPt.Z - stud.MinZ, UnitTypeId.Meters);
                    double distTopM = UnitUtils.ConvertFromInternalUnits(stud.MaxZ - hitPt.Z, UnitTypeId.Meters);
                    double heightFromFloorM = Math.Round(UnitUtils.ConvertFromInternalUnits(hitPt.Z - baseElevationFeet, UnitTypeId.Meters), 3);

                    var (wallName, xFromCornerM) = ResolveWallAndXCoordinate(doc, hitPt, spatialIndex);

                    bool webOk = occupancyPct <= maxAlmaPct;
                    bool trackClearanceOk = distBottomM >= minGuiaM && distTopM >= minGuiaM;

                    string status = (webOk && trackClearanceOk)
                        ? $"APROVADO (<= {maxAlmaPct:F0}% Alma)"
                        : !webOk
                            ? $"ATENÇÃO: Furo {occupancyPct:F0}% > {maxAlmaPct:F0}% (Reforço)"
                            : $"ATENÇÃO: Próximo à guia (< {cfg.DistanciaMinimaGuiaCm:F0} cm)";

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
            var (_, wasCacheHit, elapsedMs) = RevitSpatialCache.GetOrBuild(doc);
            var holes = AnalyzeLsfPunchingHoles(doc);
            string cacheTag = wasCacheHit ? $"Cache 2D ({elapsedMs:F1} ms)" : $"Índice 2D ({elapsedMs:F1} ms)";

            if (holes.Count == 0)
            {
                return new LsfPunchingAuditResult(
                    0,
                    0,
                    0,
                    $"[{cacheTag}] Nenhum cruzamento horizontal em montantes LSF detectado (traçado 100% livre).",
                    new List<string> { "Zero perfurações necessárias nos perfis Steel Frame para o traçado atual." });
            }

            int approved = holes.Count(h => h.Status.StartsWith("APROVADO", StringComparison.OrdinalIgnoreCase));
            int warnings = holes.Count - approved;

            var details = holes.Select(h =>
                $"[{h.HoleId}] {h.WallOrKitName} | {h.StudName} | {h.SystemName} Ø{h.PipeDiameterMm:F0}mm → Furo Ø{h.RequiredHoleMm:F0}mm ({h.OccupancyPercent:F0}% alma) | X={h.PositionXFromWallStartMeters:F2}m, H={h.HeightHFromFloorMeters:F2}m → {h.Status}")
                .ToList();

            string summary = $"[{cacheTag}] Furação LSF: {holes.Count} furo(s) mapeado(s) | {approved} aprovado(s) | {warnings} ponto(s) com alerta estrutural.";
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

            var (spatialIndex, _, _) = RevitSpatialCache.GetOrBuild(doc);

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

                        XYZ p0 = lc.Curve.GetEndPoint(0);
                        XYZ p1 = lc.Curve.GetEndPoint(1);
                        double horizLen = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                        bool isEndpoint = comment.Contains("|H=", StringComparison.OrdinalIgnoreCase);

                        if (!isEndpoint && horizLen < UnitUtils.ConvertToInternalUnits(0.20, UnitTypeId.Meters))
                            continue;

                        XYZ targetPt = isEndpoint ? p0 : lc.Curve.Evaluate(0.5, true);
                        var (_, xMeters) = ResolveWallAndXCoordinate(doc, targetPt, spatialIndex);
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

        private static bool SegmentIntersectsStud2D(XYZ p0, XYZ p1, CachedVerticalStud stud, double radiusFeet, out XYZ hitPoint)
        {
            hitPoint = XYZ.Zero;
            double sMinX = stud.MinX - radiusFeet;
            double sMaxX = stud.MaxX + radiusFeet;
            double sMinY = stud.MinY - radiusFeet;
            double sMaxY = stud.MaxY + radiusFeet;

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
            SpatialSceneIndex spatialIndex)
        {
            string roomLabel = "";
            try
            {
                Room? room = doc.GetRoomAtPoint(point);
                if (room != null && !string.IsNullOrWhiteSpace(room.Name))
                    roomLabel = $" ({room.Name})";
            }
            catch { }

            var (bestWall, bestX, _) = spatialIndex.FindNearestWall(
                point.X,
                point.Y,
                UnitUtils.ConvertToInternalUnits(1.20, UnitTypeId.Meters));

            if (bestWall != null)
                return ($"{bestWall.Name}{roomLabel}", bestX);

            return ($"Painel Hidráulico{roomLabel}", 0.0);
        }
    }
}
