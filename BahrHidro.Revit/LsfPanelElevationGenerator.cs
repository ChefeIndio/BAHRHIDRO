using System;
using System.Collections.Generic;
using System.Linq;
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

        public static void CreateLiveHydraulicViews(
            Document doc,
            List<Pipe> allCreatedPipes,
            BoundingBoxXYZ? roofBox,
            double floorZ)
        {
            if (doc == null || allCreatedPipes == null || allCreatedPipes.Count == 0) return;

            // 1. Isola e pinta tubulações na Vista 3D Ativa (se for 3D)
            if (doc.ActiveView is View3D active3D)
            {
                var createdIds = allCreatedPipes.Select(p => p.Id).ToHashSet();
                var allElems = new FilteredElementCollector(doc, active3D.Id).ToElementIds();
                
                // Override Gráfico Normativo
                Color blue = new Color(0, 112, 192);   // AF
                Color red = new Color(255, 0, 0);      // AQ
                Color brown = new Color(139, 69, 19);  // Esgoto
                Color green = new Color(0, 176, 80);   // Ventilação
                Color cyan = new Color(0, 255, 255);   // Pluvial
                Color orange = new Color(255, 128, 0); // Alerta LSF

                ElementId solidFillId = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;

                foreach (var pipe in allCreatedPipes)
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
                    if (solidFillId != ElementId.InvalidElementId)
                    {
                        ogs.SetSurfaceForegroundPatternId(solidFillId);
                        ogs.SetSurfaceForegroundPatternColor(targetColor);
                    }
                    try { active3D.SetElementOverrides(pipe.Id, ogs); } catch { }
                }
            }

            // 2. Cria a Vista "Bahr Hidro - Isométrico 3D Vivo"
            var viewFamilyType3D = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

            if (viewFamilyType3D != null)
            {
                var existing3D = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => v.Name.Equals("Bahr Hidro - Isométrico 3D Vivo", StringComparison.OrdinalIgnoreCase));

                View3D isoView = existing3D;
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
                        
                        // Oculta paredes, pisos e coberturas na vista isolada
                        var hideCats = new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings };
                        foreach (var cat in hideCats)
                        {
                            var c = Category.GetCategory(doc, cat);
                            if (c != null && isoView.CanCategoryBeHidden(c.Id))
                                isoView.SetCategoryHidden(c.Id, true);
                        }
                    }
                    catch { }
                }
            }
        }

        public static List<ViewSection> GeneratePanelElevationsAndSheet(Document doc, IEnumerable<WallPanelDescriptor> panels, IEnumerable<Pipe> createdPipes)
        {
            var generatedViews = new List<ViewSection>();
            if (doc == null || panels == null) return generatedViews;

            var sectionVft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.Section || v.ViewFamily == ViewFamily.Detail);
            if (sectionVft == null) return generatedViews;

            var existingViewNames = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pipesList = (createdPipes ?? Enumerable.Empty<Pipe>()).Where(p => p != null && p.IsValidObject).ToList();

            int idx = 1;
            foreach (var panel in panels)
            {
                string viewName = $"Bahr Hidro - Elevação {panel.Name}";
                if (existingViewNames.Contains(viewName)) viewName = $"{viewName} ({DateTime.Now:HHmmss})";

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

                var cropBox = new BoundingBoxXYZ { Transform = tr, Min = new XYZ(-halfWidth, -(maxZ - minZ) * 0.5, -0.45 * FeetPerMeter), Max = new XYZ(+halfWidth, +(maxZ - minZ) * 0.5, +0.45 * FeetPerMeter) };

                try
                {
                    var section = ViewSection.CreateSection(doc, sectionVft.Id, cropBox);
                    section.Name = viewName;
                    section.Scale = 25;
                    section.DetailLevel = ViewDetailLevel.Fine;
                    doc.Regenerate();
                    AddAutoDimensionsToElevation(doc, section, panel, pipesList);
                    generatedViews.Add(section);
                    idx++;
                }
                catch { }
            }

            if (generatedViews.Count > 0) TryAssembleExecutiveSheet(doc, generatedViews);
            return generatedViews;
        }

        private static void AddAutoDimensionsToElevation(Document doc, ViewSection view, WallPanelDescriptor panel, List<Pipe> pipes)
        {
            var horizPipes = pipes.Where(p => {
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

            var vertPipes = pipes.Where(p => {
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
                ElementId tbId = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol)).FirstElementId();
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
