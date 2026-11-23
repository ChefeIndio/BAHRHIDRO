using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.Core.Routing;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitAdvanced3DAuditProcessor
    {
        public static (ClashAudit3DResult Result, ElementId ViewId) RunStructuralAndLsfClashAudit(Document doc)
        {
            var pipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            if (pipes.Count == 0)
                throw new InvalidOperationException("Nenhuma tubulação encontrada no modelo 3D para verificar interferências.");

            var structuralTargets = CollectStructuralSolidTargets(doc);
            if (structuralTargets.Count == 0)
                throw new InvalidOperationException("Nenhum elemento estrutural (vigas, pilares, lajes, paredes ou perfis LSF) encontrado no projeto ou vínculos.");

            var clashes = new List<ClashItem3DInfo>();
            var clashingPipeIds = new HashSet<ElementId>();

            foreach (var pipe in pipes)
            {
                if (pipe.Location is not LocationCurve lc || lc.Curve == null) continue;
                BoundingBoxXYZ? pBox = pipe.get_BoundingBox(null);
                if (pBox == null) continue;

                double diaFt = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
                double diaMm = Math.Round(UnitUtils.ConvertFromInternalUnits(diaFt, UnitTypeId.Millimeters), 0);
                double marginFt = diaFt * 0.5 + UnitUtils.ConvertToInternalUnits(0.01, UnitTypeId.Meters);

                XYZ pMin = new XYZ(pBox.Min.X - marginFt, pBox.Min.Y - marginFt, pBox.Min.Z - marginFt);
                XYZ pMax = new XYZ(pBox.Max.X + marginFt, pBox.Max.Y + marginFt, pBox.Max.Z + marginFt);

                Solid? pipeSolid = GetPrimarySolid(pipe, Transform.Identity);

                foreach (var target in structuralTargets)
                {
                    if (!BoxesIntersect(pMin, pMax, target.Min, target.Max))
                        continue;

                    bool confirmed = true;
                    XYZ hitPoint = (pMin + pMax) * 0.5;

                    if (pipeSolid != null && target.WorldSolid != null)
                    {
                        try
                        {
                            Solid inter = BooleanOperationsUtils.ExecuteBooleanOperation(pipeSolid, target.WorldSolid, BooleanOperationsType.Intersect);
                            if (inter == null || inter.Volume <= 1e-7)
                                confirmed = false;
                            else
                                hitPoint = inter.ComputeCentroid();
                        }
                        catch
                        {
                            // Mantém interseção por curva x bounding box transformado
                            XYZ a = lc.Curve.GetEndPoint(0);
                            XYZ b = lc.Curve.GetEndPoint(1);
                            XYZ mid = (a + b) * 0.5;
                            if (!PointInBox(a, target.Min, target.Max) && !PointInBox(b, target.Min, target.Max) && !PointInBox(mid, target.Min, target.Max))
                                confirmed = false;
                            else
                                hitPoint = mid;
                        }
                    }

                    if (!confirmed) continue;

                    clashingPipeIds.Add(pipe.Id);
                    string sysName = pipe.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "Hidrossanitário";
                    string levelName = (doc.GetElement(pipe.ReferenceLevel?.Id ?? ElementId.InvalidElementId) as Level)?.Name ?? "Nível Base";
                    double elevM = Math.Round(UnitUtils.ConvertFromInternalUnits(hitPoint.Z, UnitTypeId.Meters), 2);
                    int sleeveDn = (int)(Math.Ceiling((diaMm + 25.0) / 25.0) * 25.0);

                    clashes.Add(new ClashItem3DInfo(
                        clashes.Count + 1,
                        pipe.Id.Value,
                        sysName,
                        (int)diaMm,
                        target.CategoryLabel,
                        target.ElementDescription,
                        target.SourceModel,
                        levelName,
                        elevM,
                        $"Passagem Ø {sleeveDn} mm"));

                    if (clashes.Count >= 250) break;
                }
                if (clashes.Count >= 250) break;
            }

            ElementId viewId = ElementId.InvalidElementId;
            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Compatibilização 3D & Clash LSF"))
            {
                tx.Start();
                View3D? view = GetOrCreateDedicated3DView(doc, "Bahr Hidro - Compatibilização 3D & Clash");
                if (view != null)
                {
                    viewId = view.Id;
                    var ogsNormal = new OverrideGraphicSettings();
                    ogsNormal.SetSurfaceTransparency(70);

                    var ogsClash = new OverrideGraphicSettings();
                    ogsClash.SetProjectionLineColor(new Color(220, 38, 38));
                    ogsClash.SetProjectionLineWeight(6);
                    ogsClash.SetSurfaceTransparency(0);
                    FillPatternElement? solidFill = new FilteredElementCollector(doc)
                        .OfClass(typeof(FillPatternElement))
                        .Cast<FillPatternElement>()
                        .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);
                    if (solidFill != null)
                    {
                        ogsClash.SetSurfaceForegroundPatternId(solidFill.Id);
                        ogsClash.SetSurfaceForegroundPatternColor(new Color(239, 68, 68));
                    }

                    foreach (var p in pipes)
                    {
                        view.SetElementOverrides(p.Id, clashingPipeIds.Contains(p.Id) ? ogsClash : ogsNormal);
                    }
                }
                tx.Commit();
            }

            string summary = clashes.Count == 0
                ? $"Compatibilização 3D concluída: 0 interferências encontradas entre {pipes.Count} trechos e {structuralTargets.Count} elementos estruturais/LSF."
                : $"Compatibilização 3D concluída: {clashes.Count} interferência(s) detectada(s) em {clashingPipeIds.Count} tubo(s) contra Estrutura/LSF. Vista 'Bahr Hidro - Compatibilização 3D & Clash' atualizada.";

            return (new ClashAudit3DResult(pipes.Count, structuralTargets.Count, clashes.Count, clashes, summary), viewId);
        }

        public static (HydraulicDiagnostic3DResult Result, ElementId ViewId) ApplyHydraulicDiagnosticColorMap(
            Document doc,
            NetworkCalculationResult? coldWaterResult)
        {
            var pipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            if (pipes.Count == 0)
                throw new InvalidOperationException("Nenhuma tubulação encontrada no modelo 3D.");

            int okCount = 0;
            int velWarnCount = 0;
            int pressCritCount = 0;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Mapa Visual 3D Diagnóstico Hidráulico"))
            {
                tx.Start();
                View3D? view = GetOrCreateDedicated3DView(doc, "Bahr Hidro - Diagnóstico Hidráulico 3D");
                if (view == null)
                    throw new InvalidOperationException("Não foi possível criar a Vista 3D de Diagnóstico Hidráulico.");

                FillPatternElement? solidFill = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

                OverrideGraphicSettings MakeOgs(byte r, byte g, byte b)
                {
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(new Color(r, g, b));
                    ogs.SetProjectionLineWeight(5);
                    if (solidFill != null)
                    {
                        ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                        ogs.SetSurfaceForegroundPatternColor(new Color(r, g, b));
                    }
                    return ogs;
                }

                var ogsGreen = MakeOgs(22, 163, 74);
                var ogsAmber = MakeOgs(245, 158, 11);
                var ogsRed = MakeOgs(220, 38, 38);
                var ogsNeutral = new OverrideGraphicSettings();
                ogsNeutral.SetSurfaceTransparency(65);

                double globalMaxStatic = 0;
                double globalMinDynamic = 999;
                if (coldWaterResult != null)
                {
                    try
                    {
                        var nodesProp = coldWaterResult.GetType().GetProperty("Nodes");
                        if (nodesProp?.GetValue(coldWaterResult) is System.Collections.IEnumerable nodes)
                        {
                            foreach (var n in nodes)
                            {
                                if (n == null) continue;
                                var t = n.GetType();
                                if ((t.GetProperty("StaticPressureMca")?.GetValue(n)) is double st && st > globalMaxStatic)
                                    globalMaxStatic = st;
                                if ((t.GetProperty("ResidualPressureMca")?.GetValue(n) ?? t.GetProperty("AvailablePressureMca")?.GetValue(n)) is double dyn && dyn < globalMinDynamic)
                                    globalMinDynamic = dyn;
                            }
                        }
                    }
                    catch { }
                }

                foreach (var p in pipes)
                {
                    string sys = Normalize(p.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "");
                    bool isWater = sys.Contains("fria") || sys.Contains("quente") || sys.Contains("cold") || sys.Contains("hot") || sys.Contains("agua");
                    if (!isWater)
                    {
                        view.SetElementOverrides(p.Id, ogsNeutral);
                        continue;
                    }

                    double vel = p.get_Parameter(BuiltInParameter.RBS_PIPE_VELOCITY_PARAM)?.AsDouble() ?? 0.0;
                    double velMs = UnitUtils.ConvertFromInternalUnits(vel, UnitTypeId.MetersPerSecond);
                    string comment = p.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";

                    bool critPress = (globalMaxStatic > 40.0 && p.Location is LocationCurve lc &&
                                      UnitUtils.ConvertFromInternalUnits(lc.Curve.GetEndPoint(0).Z, UnitTypeId.Meters) < 3.2) ||
                                     (globalMinDynamic < 1.0 && globalMinDynamic > 0) ||
                                     comment.Contains("VRP", StringComparison.OrdinalIgnoreCase) ||
                                     comment.Contains("INSUFICIENTE", StringComparison.OrdinalIgnoreCase);

                    bool highVel = velMs > 3.0 || comment.Contains("VEL", StringComparison.OrdinalIgnoreCase);

                    if (critPress)
                    {
                        view.SetElementOverrides(p.Id, ogsRed);
                        pressCritCount++;
                    }
                    else if (highVel)
                    {
                        view.SetElementOverrides(p.Id, ogsAmber);
                        velWarnCount++;
                    }
                    else
                    {
                        view.SetElementOverrides(p.Id, ogsGreen);
                        okCount++;
                    }
                }

                tx.Commit();

                string summary = $"Mapa Visual 3D gerado na vista 'Bahr Hidro - Diagnóstico Hidráulico 3D': " +
                                 $"{okCount} trechos conformes (Verde), {velWarnCount} com velocidade > 3,0 m/s (Amarelo) e {pressCritCount} com pressão crítica/VRP (Vermelho).";

                return (new HydraulicDiagnostic3DResult(okCount, velWarnCount, pressCritCount, summary), view.Id);
            }
        }

        public static string ExtractSpatialWetAreaJson(Document doc)
        {
            string projectName = doc.Title ?? "Projeto_Revit_2026";
            var fixtures = new List<SpatialFixtureInfo>();

            var rawFixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            foreach (var fi in rawFixtures)
            {
                BoundingBoxXYZ? bb = fi.get_BoundingBox(null);
                XYZ center = bb != null ? (bb.Min + bb.Max) * 0.5 : (fi.Location as LocationPoint)?.Point ?? XYZ.Zero;
                string fam = fi.Symbol?.FamilyName ?? "";
                string typ = fi.Symbol?.Name ?? fi.Name ?? "";
                string lvl = (doc.GetElement(fi.LevelId) as Level)?.Name ?? "Nível 1";

                var conns = new List<SpatialConnectorInfo>();
                if (fi.MEPModel?.ConnectorManager?.Connectors != null)
                {
                    foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                    {
                        if (c.Domain != Domain.DomainPiping) continue;
                        double dMm = Math.Round(UnitUtils.ConvertFromInternalUnits(c.Radius * 2.0, UnitTypeId.Millimeters), 0);
                        XYZ dir = c.CoordinateSystem?.BasisZ ?? XYZ.BasisZ;
                        conns.Add(new SpatialConnectorInfo(
                            c.PipeSystemType.ToString(),
                            c.Direction.ToString(),
                            dMm,
                            ToMetersPoint(c.Origin),
                            new SpatialPoint3D(Math.Round(dir.X, 4), Math.Round(dir.Y, 4), Math.Round(dir.Z, 4))));
                    }
                }

                fixtures.Add(new SpatialFixtureInfo(
                    fi.Id.Value,
                    fam,
                    typ,
                    ClassifyFixtureRole($"{fam} {typ}"),
                    lvl,
                    ToMetersPoint(center),
                    conns));
            }

            var anchors = new List<SpatialRiserAnchorInfo>();
            var pipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            foreach (var p in pipes)
            {
                if (p.Location is not LocationCurve lc || lc.Curve == null) continue;
                XYZ a = lc.Curve.GetEndPoint(0);
                XYZ b = lc.Curve.GetEndPoint(1);
                double dxy = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
                double dz = Math.Abs(b.Z - a.Z);
                if (dz >= UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Meters) && dxy <= 0.15)
                {
                    double dMm = Math.Round(UnitUtils.ConvertFromInternalUnits(
                        p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0.08, UnitTypeId.Millimeters), 0);
                    string sys = p.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "Prumada";
                    XYZ bot = a.Z <= b.Z ? a : b;
                    XYZ top = a.Z > b.Z ? a : b;
                    anchors.Add(new SpatialRiserAnchorInfo(p.Id.Value, sys, dMm, ToMetersPoint(bot), ToMetersPoint(top)));
                }
            }

            var ctx = new SpatialWetAreaContext(projectName, DateTime.UtcNow.ToString("O"), fixtures, anchors);
            return AutoRoutingSerializer.SerializeContext(ctx);
        }

        public static int ExecuteAutoRoutingPlanJson(Document doc, string planJson)
        {
            var plan = AutoRoutingSerializer.DeserializePlan(planJson)
                       ?? throw new InvalidOperationException("JSON do plano de traçado inválido ou vazio.");

            if (plan.Segments.Count == 0)
                throw new InvalidOperationException("O plano de traçado não contém segmentos para criar no 3D.");

            Level? baseLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault() ?? throw new InvalidOperationException("Nenhum nível encontrado no projeto.");

            PipeType? pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault() ?? throw new InvalidOperationException("Nenhum tipo de tubulação (PipeType) carregado no template.");

            var sysTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .ToList();

            if (sysTypes.Count == 0)
                throw new InvalidOperationException("Nenhum tipo de sistema de tubulação encontrado.");

            int createdPipes = 0;

            using (Transaction tx = new Transaction(doc, $"Bahr Hidro - Executar Traçado 3D ({plan.PlanName})"))
            {
                tx.Start();

                foreach (var seg in plan.Segments)
                {
                    if (seg.WaypointsMeters.Count < 2) continue;

                    PipingSystemType sysType = MatchSystemType(sysTypes, seg.SystemClassification);
                    double diaFt = UnitUtils.ConvertToInternalUnits(seg.DiameterMm > 0 ? seg.DiameterMm : 25.0, UnitTypeId.Millimeters);
                    var segPipes = new List<Pipe>();

                    for (int i = 0; i < seg.WaypointsMeters.Count - 1; i++)
                    {
                        XYZ p0 = FromMetersPoint(seg.WaypointsMeters[i]);
                        XYZ p1 = FromMetersPoint(seg.WaypointsMeters[i + 1]);
                        if (p0.DistanceTo(p1) < 0.01) continue;

                        Pipe created = Pipe.Create(doc, sysType.Id, pipeType.Id, baseLevel.Id, p0, p1);
                        Parameter? diaParam = created.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diaParam != null && !diaParam.IsReadOnly)
                            diaParam.Set(diaFt);

                        segPipes.Add(created);
                        createdPipes++;
                    }

                    // Conecta automaticamente trechos consecutivos com cotovelo
                    for (int i = 0; i < segPipes.Count - 1; i++)
                    {
                        TryConnectTwoPipes(doc, segPipes[i], segPipes[i + 1]);
                    }
                }

                tx.Commit();
            }

            return createdPipes;
        }

        private static void TryConnectTwoPipes(Document doc, Pipe p1, Pipe p2)
        {
            try
            {
                Connector? best1 = null, best2 = null;
                double minDist = double.MaxValue;
                foreach (Connector c1 in p1.ConnectorManager.Connectors)
                {
                    foreach (Connector c2 in p2.ConnectorManager.Connectors)
                    {
                        double d = c1.Origin.DistanceTo(c2.Origin);
                        if (d < minDist)
                        {
                            minDist = d;
                            best1 = c1;
                            best2 = c2;
                        }
                    }
                }
                if (best1 != null && best2 != null && minDist < 0.05)
                {
                    doc.Create.NewElbowFitting(best1, best2);
                }
            }
            catch { }
        }

        private static PipingSystemType MatchSystemType(List<PipingSystemType> types, string classification)
        {
            string norm = Normalize(classification);
            if (norm.Contains("quente") || norm.Contains("hot"))
                return types.FirstOrDefault(t => t.SystemClassification == MEPSystemClassification.DomesticHotWater) ?? types[0];
            if (norm.Contains("esgoto") || norm.Contains("sanit"))
                return types.FirstOrDefault(t => t.SystemClassification == MEPSystemClassification.Sanitary) ?? types[0];
            if (norm.Contains("ventil"))
                return types.FirstOrDefault(t => t.SystemClassification == MEPSystemClassification.Vent) ?? types[0];
            return types.FirstOrDefault(t => t.SystemClassification == MEPSystemClassification.DomesticColdWater) ?? types[0];
        }

        private static View3D? GetOrCreateDedicated3DView(Document doc, string viewName)
        {
            View3D? existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.DetailLevel = ViewDetailLevel.Fine;
                existing.DisplayStyle = DisplayStyle.ShadingWithEdges;
                return existing;
            }

            ViewFamilyType? vft3d = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

            if (vft3d == null) return null;
            View3D created = View3D.CreateIsometric(doc, vft3d.Id);
            created.Name = viewName;
            created.DetailLevel = ViewDetailLevel.Fine;
            created.DisplayStyle = DisplayStyle.ShadingWithEdges;
            return created;
        }

        private sealed record StructuralTarget(
            string CategoryLabel,
            string ElementDescription,
            string SourceModel,
            XYZ Min,
            XYZ Max,
            Solid? WorldSolid);

        private static List<StructuralTarget> CollectStructuralSolidTargets(Document hostDoc)
        {
            var targets = new List<StructuralTarget>();
            var cats = new[]
            {
                (BuiltInCategory.OST_StructuralFraming, "Viga / Perfil LSF"),
                (BuiltInCategory.OST_StructuralColumns, "Pilar / Montante LSF"),
                (BuiltInCategory.OST_Floors, "Laje / Piso Estrutural"),
                (BuiltInCategory.OST_Walls, "Parede / Painel LSF")
            };

            void ScanDocument(Document d, Transform xf, string sourceName)
            {
                foreach (var (bic, label) in cats)
                {
                    var elems = new FilteredElementCollector(d)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType()
                        .ToList();

                    foreach (var el in elems)
                    {
                        if (bic == BuiltInCategory.OST_Walls && el is Wall w)
                        {
                            string wName = Normalize($"{w.Name} {w.WallType?.Name}");
                            bool isStructuralOrLsf = w.StructuralUsage != Autodesk.Revit.DB.Structure.StructuralWallUsage.NonBearing ||
                                                     wName.Contains("lsf") || wName.Contains("steel") || wName.Contains("estrut") || wName.Contains("concreto");
                            if (!isStructuralOrLsf) continue;
                        }

                        BoundingBoxXYZ? bb = el.get_BoundingBox(null);
                        if (bb == null) continue;

                        XYZ p0 = xf.OfPoint(bb.Min);
                        XYZ p1 = xf.OfPoint(bb.Max);
                        XYZ min = new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Min(p0.Z, p1.Z));
                        XYZ max = new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), Math.Max(p0.Z, p1.Z));
                        Solid? solid = GetPrimarySolid(el, xf);

                        targets.Add(new StructuralTarget(label, el.Name ?? label, sourceName, min, max, solid));
                    }
                }
            }

            ScanDocument(hostDoc, Transform.Identity, "Modelo Atual");

            var links = new FilteredElementCollector(hostDoc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc != null)
                {
                    ScanDocument(linkDoc, link.GetTotalTransform(), link.Name);
                }
            }

            return targets;
        }

        private static Solid? GetPrimarySolid(Element el, Transform xf)
        {
            try
            {
                var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
                GeometryElement? geom = el.get_Geometry(opt);
                if (geom == null) return null;

                Solid? best = null;
                foreach (GeometryObject obj in geom)
                {
                    if (obj is Solid s && s.Volume > 1e-6)
                    {
                        if (best == null || s.Volume > best.Volume) best = s;
                    }
                    else if (obj is GeometryInstance gi)
                    {
                        foreach (GeometryObject instObj in gi.GetInstanceGeometry())
                        {
                            if (instObj is Solid isld && isld.Volume > 1e-6)
                            {
                                if (best == null || isld.Volume > best.Volume) best = isld;
                            }
                        }
                    }
                }

                if (best == null) return null;
                return xf.IsIdentity ? best : SolidUtils.CreateTransformed(best, xf);
            }
            catch
            {
                return null;
            }
        }

        private static bool BoxesIntersect(XYZ aMin, XYZ aMax, XYZ bMin, XYZ bMax) =>
            aMin.X <= bMax.X && aMax.X >= bMin.X &&
            aMin.Y <= bMax.Y && aMax.Y >= bMin.Y &&
            aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

        private static bool PointInBox(XYZ p, XYZ bMin, XYZ bMax) =>
            p.X >= bMin.X && p.X <= bMax.X &&
            p.Y >= bMin.Y && p.Y <= bMax.Y &&
            p.Z >= bMin.Z && p.Z <= bMax.Z;

        private static SpatialPoint3D ToMetersPoint(XYZ pt) =>
            new(
                Math.Round(UnitUtils.ConvertFromInternalUnits(pt.X, UnitTypeId.Meters), 4),
                Math.Round(UnitUtils.ConvertFromInternalUnits(pt.Y, UnitTypeId.Meters), 4),
                Math.Round(UnitUtils.ConvertFromInternalUnits(pt.Z, UnitTypeId.Meters), 4));

        private static XYZ FromMetersPoint(SpatialPoint3D pt) =>
            new(
                UnitUtils.ConvertToInternalUnits(pt.XMeters, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(pt.YMeters, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(pt.ZMeters, UnitTypeId.Meters));

        private static string ClassifyFixtureRole(string raw)
        {
            string n = Normalize(raw);
            if (n.Contains("bacia") || n.Contains("vaso") || n.Contains("sanit")) return "BaciaSanitaria";
            if (n.Contains("lavat") || n.Contains("cuba")) return "Lavatorio";
            if (n.Contains("chuveiro") || n.Contains("ducha")) return "Chuveiro";
            if (n.Contains("pia") || n.Contains("cozinha")) return "PiaCozinha";
            if (n.Contains("tanque") || n.Contains("maquina")) return "AreaServico";
            if (n.Contains("ralo") || n.Contains("sifonada")) return "CaixaSifonada";
            return "AparelhoSanitario";
        }

        private static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string formD = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char ch in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
    }
}
