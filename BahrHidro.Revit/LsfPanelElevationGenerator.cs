using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace BahrHidro.Revit
{
    public sealed class WallPanelDescriptor
    {
        public string Name { get; set; } = "Painel LSF";
        public bool WallAlongX { get; set; }
        public double WallPlane { get; set; }
        public double MinU { get; set; }
        public double MaxU { get; set; }
        public double FloorZ { get; set; }
        public double TopZ { get; set; }
        public List<double> StudUs { get; set; } = new();
    }

    public static class LsfPanelElevationGenerator
    {
        private const double FeetPerMeter = 3.280839895013123;
        private const double MmToFeet = 1.0 / 304.8;

        public static void CreateLiveHydraulicViews(
            Document doc,
            List<Pipe> allCreatedPipes,
            BoundingBoxXYZ? roofBox,
            double floorZ)
        {
            if (doc == null || allCreatedPipes == null || allCreatedPipes.Count == 0) return;

            var validPipes = allCreatedPipes.Where(p => p != null && p.IsValidObject).ToList();
            if (validPipes.Count == 0) return;

            // 1. Aplica cores normativas na Vista Ativa (seja Planta ou 3D)
            if (doc.ActiveView != null && !doc.ActiveView.IsTemplate)
                ApplyNormativeColorsToView(doc, doc.ActiveView, validPipes);

            // 2. Cria ou atualiza a Planta Baixa Hidráulica Executiva com cores e Tags
            ViewPlan? hydraulicPlan = CreateOrUpdateHydraulicFloorPlan(doc, validPipes, floorZ);

            // 3. Cria ou atualiza o Isométrico 3D Vivo isolado, colorido, enquadrado e tagueado
            var viewFamilyType3D = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

            if (viewFamilyType3D != null)
            {
                var isoView = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => v.Name.Equals("Bahr Hidro - Isométrico 3D Vivo", StringComparison.OrdinalIgnoreCase));

                if (isoView == null)
                {
                    try
                    {
                        isoView = View3D.CreateIsometric(doc, viewFamilyType3D.Id);
                        isoView.Name = "Bahr Hidro - Isométrico 3D Vivo";
                    }
                    catch { }
                }

                if (isoView != null)
                {
                    try
                    {
                        isoView.DetailLevel = ViewDetailLevel.Fine;
                        isoView.DisplayStyle = DisplayStyle.Shading;

                        var hideCats = new[]
                        {
                            BuiltInCategory.OST_Walls,
                            BuiltInCategory.OST_Floors,
                            BuiltInCategory.OST_Roofs,
                            BuiltInCategory.OST_Ceilings
                        };
                        foreach (var cat in hideCats)
                        {
                            var c = Category.GetCategory(doc, cat);
                            if (c != null && isoView.CanCategoryBeHidden(c.Id))
                                isoView.SetCategoryHidden(c.Id, true);
                        }

                        var netBox = ComputePipesBoundingBox(validPipes, 0.50 * FeetPerMeter);
                        if (netBox != null)
                            isoView.SetSectionBox(netBox);

                        ApplyNormativeColorsToView(doc, isoView, validPipes);
                        doc.Regenerate();
                        ExecutiveDetailingGenerator.TagPipesInView(doc, isoView);
                    }
                    catch { }
                }
            }

            if (hydraulicPlan != null)
            {
                doc.Regenerate();
                ExecutiveDetailingGenerator.TagPipesInView(doc, hydraulicPlan);
            }
        }

        private static ViewPlan? CreateOrUpdateHydraulicFloorPlan(Document doc, List<Pipe> pipes, double floorZ)
        {
            const string planName = "Bahr Hidro - Planta Hidráulica Executiva";
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(planName, StringComparison.OrdinalIgnoreCase));

            ViewPlan? targetPlan = existing;
            if (targetPlan == null)
            {
                var basePlan = (doc.ActiveView as ViewPlan) ?? new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                    .OrderBy(v => Math.Abs((v.GenLevel?.Elevation ?? 0.0) - floorZ))
                    .FirstOrDefault();

                if (basePlan != null && basePlan.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                {
                    try
                    {
                        ElementId dupId = basePlan.Duplicate(ViewDuplicateOption.Duplicate);
                        targetPlan = doc.GetElement(dupId) as ViewPlan;
                        if (targetPlan != null)
                            targetPlan.Name = planName;
                    }
                    catch { }
                }
            }

            if (targetPlan != null)
            {
                try
                {
                    targetPlan.DetailLevel = ViewDetailLevel.Fine;
                    targetPlan.DisplayStyle = DisplayStyle.HLR;
                    ApplyNormativeColorsToView(doc, targetPlan, pipes);
                }
                catch { }
            }

            return targetPlan;
        }

        private static void ApplyNormativeColorsToView(Document doc, View view, List<Pipe> pipes)
        {
            Color blue = new Color(0, 112, 192);    // Água Fria
            Color red = new Color(220, 38, 38);     // Água Quente
            Color brown = new Color(139, 69, 19);   // Esgoto Sanitário
            Color green = new Color(22, 163, 74);   // Ventilação
            Color cyan = new Color(6, 182, 212);    // Águas Pluviais
            Color orange = new Color(249, 115, 22); // Alerta LSF

            ElementId solidFillId = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;

            foreach (var pipe in pipes)
            {
                string cmt = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                Color targetColor = blue;
                if (cmt.Contains("ALERTA LSF")) targetColor = orange;
                else if (cmt.Contains("AQ |")) targetColor = red;
                else if (cmt.Contains("VENT |")) targetColor = green;
                else if (cmt.Contains("ESG |")) targetColor = brown;
                else if (cmt.Contains("AP |")) targetColor = cyan;

                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(targetColor);
                ogs.SetProjectionLineWeight(5);
                if (solidFillId != ElementId.InvalidElementId)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFillId);
                    ogs.SetSurfaceForegroundPatternColor(targetColor);
                }
                try { view.SetElementOverrides(pipe.Id, ogs); } catch { }
            }
        }

        private static BoundingBoxXYZ? ComputePipesBoundingBox(List<Pipe> pipes, double paddingFeet)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            foreach (var p in pipes)
            {
                if (p.Location is not LocationCurve lc) continue;
                XYZ a = lc.Curve.GetEndPoint(0);
                XYZ b = lc.Curve.GetEndPoint(1);
                minX = Math.Min(minX, Math.Min(a.X, b.X));
                minY = Math.Min(minY, Math.Min(a.Y, b.Y));
                minZ = Math.Min(minZ, Math.Min(a.Z, b.Z));
                maxX = Math.Max(maxX, Math.Max(a.X, b.X));
                maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
                maxZ = Math.Max(maxZ, Math.Max(a.Z, b.Z));
                any = true;
            }

            if (!any) return null;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX - paddingFeet, minY - paddingFeet, minZ - paddingFeet),
                Max = new XYZ(maxX + paddingFeet, maxY + paddingFeet, maxZ + paddingFeet)
            };
        }

        public static List<ViewSection> GeneratePanelElevationsAndSheet(
            Document doc,
            IEnumerable<WallPanelDescriptor> panels,
            IEnumerable<Pipe> createdPipes)
        {
            var generatedViews = new List<ViewSection>();
            if (doc == null || panels == null) return generatedViews;

            var sectionVft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Section || v.ViewFamily == ViewFamily.Detail);
            if (sectionVft == null) return generatedViews;

            var existingViewNames = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Select(v => v.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pipesList = (createdPipes ?? Enumerable.Empty<Pipe>())
                .Where(p => p != null && p.IsValidObject)
                .ToList();

            int idx = 1;
            foreach (var panel in panels)
            {
                string viewName = $"Bahr Hidro - Elevação {panel.Name}";
                if (existingViewNames.Contains(viewName))
                    viewName = $"{viewName} ({DateTime.Now:HHmmss})";

                double midU = (panel.MinU + panel.MaxU) * 0.5;
                double halfWidth = Math.Max(1.20 * FeetPerMeter, ((panel.MaxU - panel.MinU) * 0.5) + (0.45 * FeetPerMeter));
                double minZ = panel.FloorZ - (0.30 * FeetPerMeter);
                double maxZ = Math.Max(panel.TopZ, panel.FloorZ + 2.85 * FeetPerMeter) + (0.35 * FeetPerMeter);
                double midZ = (minZ + maxZ) * 0.5;

                XYZ origin = panel.WallAlongX ? new XYZ(midU, panel.WallPlane, midZ) : new XYZ(panel.WallPlane, midU, midZ);
                XYZ basisX = panel.WallAlongX ? XYZ.BasisX : XYZ.BasisY;
                XYZ basisY = XYZ.BasisZ;
                XYZ basisZ = basisX.CrossProduct(basisY);

                var tr = Transform.Identity;
                tr.Origin = origin; tr.BasisX = basisX; tr.BasisY = basisY; tr.BasisZ = basisZ;

                var cropBox = new BoundingBoxXYZ
                {
                    Transform = tr,
                    Min = new XYZ(-halfWidth, -(maxZ - minZ) * 0.5, -0.45 * FeetPerMeter),
                    Max = new XYZ(+halfWidth, +(maxZ - minZ) * 0.5, +0.45 * FeetPerMeter)
                };

                try
                {
                    var section = ViewSection.CreateSection(doc, sectionVft.Id, cropBox);
                    section.Name = viewName;
                    section.Scale = 25;
                    section.DetailLevel = ViewDetailLevel.Fine;
                    ApplyNormativeColorsToView(doc, section, pipesList);
                    doc.Regenerate();
                    AddAutoDimensionsToElevation(doc, section, panel, pipesList);
                    ExecutiveDetailingGenerator.TagPipesInView(doc, section);
                    generatedViews.Add(section);
                    idx++;
                }
                catch { }
            }

            // Seção Geral cortando toda a rede hidráulica do modelo
            var overallBox = ComputePipesBoundingBox(pipesList, 0.60 * FeetPerMeter);
            if (overallBox != null)
            {
                try
                {
                    string overallSecName = "Bahr Hidro - Seção Geral da Rede";
                    if (existingViewNames.Contains(overallSecName))
                        overallSecName = $"{overallSecName} ({DateTime.Now:HHmmss})";

                    XYZ center = (overallBox.Min + overallBox.Max) * 0.5;
                    double spanX = Math.Max(1.5 * FeetPerMeter, (overallBox.Max.X - overallBox.Min.X) * 0.5);
                    double spanY = Math.Max(1.5 * FeetPerMeter, (overallBox.Max.Y - overallBox.Min.Y) * 0.5);
                    double spanZ = Math.Max(1.8 * FeetPerMeter, (overallBox.Max.Z - overallBox.Min.Z) * 0.5);

                    var tr = Transform.Identity;
                    tr.Origin = center;
                    tr.BasisX = XYZ.BasisX;
                    tr.BasisY = XYZ.BasisZ;
                    tr.BasisZ = XYZ.BasisX.CrossProduct(XYZ.BasisZ);

                    var secBox = new BoundingBoxXYZ
                    {
                        Transform = tr,
                        Min = new XYZ(-spanX, -spanZ, -spanY),
                        Max = new XYZ(+spanX, +spanZ, +spanY)
                    };

                    var overallSection = ViewSection.CreateSection(doc, sectionVft.Id, secBox);
                    overallSection.Name = overallSecName;
                    overallSection.Scale = 50;
                    overallSection.DetailLevel = ViewDetailLevel.Fine;
                    ApplyNormativeColorsToView(doc, overallSection, pipesList);
                    generatedViews.Add(overallSection);
                }
                catch { }
            }

            if (generatedViews.Count > 0)
                TryAssembleExecutiveSheet(doc, generatedViews);

            ExportLsfDrillingCsv(doc, panels, pipesList);
            return generatedViews;
        }

        private static void ExportLsfDrillingCsv(Document doc, IEnumerable<WallPanelDescriptor> panels, List<Pipe> pipes)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BahrHidro",
                    "Logs");
                Directory.CreateDirectory(logDir);

                var sb = new StringBuilder();
                sb.AppendLine("Painel;Eixo_U_m;Plano_Parede_m;Cota_Furo_Z_m;Altura_Desde_Guia_Inf_cm;DN_Tubo_mm;Diametro_Furo_Anel_mm;Sistema;Status_Estrutural");

                foreach (var panel in panels)
                {
                    foreach (double studU in panel.StudUs)
                    {
                        foreach (var pipe in pipes)
                        {
                            if (pipe.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                            XYZ a = line.GetEndPoint(0);
                            XYZ b = line.GetEndPoint(1);
                            if (Math.Abs(a.Z - b.Z) > 0.04) continue;

                            double pN = panel.WallAlongX ? (a.Y + b.Y) * 0.5 : (a.X + b.X) * 0.5;
                            if (Math.Abs(pN - panel.WallPlane) > 0.15 * FeetPerMeter) continue;

                            double uMin = panel.WallAlongX ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y);
                            double uMax = panel.WallAlongX ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
                            if (studU < uMin - 0.02 || studU > uMax + 0.02) continue;

                            double zFeet = (a.Z + b.Z) * 0.5;
                            double heightFromFloorCm = ((zFeet - panel.FloorZ) / FeetPerMeter) * 100.0;
                            double dnMm = (pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0.0) / MmToFeet;
                            double holeMm = Math.Round(dnMm + 6.0, 0); // +6mm para anel isolador polimérico
                            string cmt = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "AF";
                            string sys = cmt.Contains("AQ |") ? "Água Quente" : (cmt.Contains("ESG |") ? "Esgoto" : "Água Fria");
                            string status = (heightFromFloorCm >= 15.0 && holeMm <= 38.0) ? "OK (Anel Isolador)" : "ALERTA ESTRUTURAL";

                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "{0};{1:F2};{2:F2};{3:F2};{4:F1};{5:F0};{6:F0};{7};{8}",
                                panel.Name,
                                studU / FeetPerMeter,
                                panel.WallPlane / FeetPerMeter,
                                zFeet / FeetPerMeter,
                                heightFromFloorCm,
                                dnMm,
                                holeMm,
                                sys,
                                status));
                        }
                    }
                }

                File.WriteAllText(Path.Combine(logDir, "Furacao_LSF_Obra.csv"), sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static void AddAutoDimensionsToElevation(Document doc, ViewSection view, WallPanelDescriptor panel, List<Pipe> pipes)
        {
            var horizPipes = pipes.Where(p =>
            {
                if (p.Location is not LocationCurve lc || lc.Curve is not Line line) return false;
                XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                if (Math.Abs(a.Z - b.Z) > 0.03) return false;
                double n = panel.WallAlongX ? (a.Y + b.Y) * 0.5 : (a.X + b.X) * 0.5;
                return Math.Abs(n - panel.WallPlane) <= 0.25 * FeetPerMeter;
            }).ToList();

            if (horizPipes.Count >= 2)
            {
                try
                {
                    var refs = new ReferenceArray();
                    foreach (var hp in horizPipes.Take(6)) refs.Append(new Reference(hp));
                    double dimU = panel.MaxU + (0.20 * FeetPerMeter);
                    XYZ p1 = panel.WallAlongX ? new XYZ(dimU, panel.WallPlane, panel.FloorZ) : new XYZ(panel.WallPlane, dimU, panel.FloorZ);
                    XYZ p2 = panel.WallAlongX ? new XYZ(dimU, panel.WallPlane, panel.FloorZ + 2.50 * FeetPerMeter) : new XYZ(panel.WallPlane, dimU, panel.FloorZ + 2.50 * FeetPerMeter);
                    doc.Create.NewDimension(view, Line.CreateBound(p1, p2), refs);
                }
                catch { }
            }

            var vertPipes = pipes.Where(p =>
            {
                if (p.Location is not LocationCurve lc || lc.Curve is not Line line) return false;
                XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                if (Math.Abs(a.X - b.X) > 0.03 || Math.Abs(a.Y - b.Y) > 0.03) return false;
                double n = panel.WallAlongX ? a.Y : a.X;
                return Math.Abs(n - panel.WallPlane) <= 0.25 * FeetPerMeter;
            }).ToList();

            if (vertPipes.Count >= 2)
            {
                try
                {
                    var refs = new ReferenceArray();
                    foreach (var vp in vertPipes.Take(8)) refs.Append(new Reference(vp));
                    double dimZ = panel.FloorZ + (2.35 * FeetPerMeter);
                    XYZ p1 = panel.WallAlongX ? new XYZ(panel.MinU, panel.WallPlane, dimZ) : new XYZ(panel.WallPlane, panel.MinU, dimZ);
                    XYZ p2 = panel.WallAlongX ? new XYZ(panel.MaxU, panel.WallPlane, dimZ) : new XYZ(panel.WallPlane, panel.MaxU, dimZ);
                    doc.Create.NewDimension(view, Line.CreateBound(p1, p2), refs);
                }
                catch { }
            }
        }

        private static void TryAssembleExecutiveSheet(Document doc, List<ViewSection> elevations)
        {
            try
            {
                ElementId tbId = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilySymbol))
                    .FirstElementId();

                var sheet = ViewSheet.Create(doc, tbId);
                sheet.Name = "Bahr Hidro - Projeto Executivo & Seções";
                double xC = 0.25 * FeetPerMeter, yC = 0.55 * FeetPerMeter;
                foreach (var view in elevations.Take(3))
                {
                    if (Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    {
                        Viewport.Create(doc, sheet.Id, view.Id, new XYZ(xC, yC, 0));
                        xC += 0.95 * FeetPerMeter;
                    }
                }
            }
            catch { }
        }
    }
}
