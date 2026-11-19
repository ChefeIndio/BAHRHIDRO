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
                string viewName = $"Bahr Hidro - Elevação {panel.Name} #{idx}";
                if (existingViewNames.Contains(viewName))
                    viewName = $"{viewName} ({DateTime.Now:HHmmss})";

                double midU = (panel.MinU + panel.MaxU) * 0.5;
                double halfWidth = Math.Max(1.20 * FeetPerMeter, ((panel.MaxU - panel.MinU) * 0.5) + (0.45 * FeetPerMeter));
                double minZ = panel.FloorZ - (0.30 * FeetPerMeter);
                double maxZ = Math.Max(panel.TopZ, panel.FloorZ + 2.85 * FeetPerMeter) + (0.35 * FeetPerMeter);
                double midZ = (minZ + maxZ) * 0.5;

                // Base ortonormal destra (BasisX x BasisY = BasisZ)
                XYZ origin = panel.WallAlongX
                    ? new XYZ(midU, panel.WallPlane, midZ)
                    : new XYZ(panel.WallPlane, midU, midZ);

                XYZ basisX = panel.WallAlongX ? XYZ.BasisX : XYZ.BasisY;
                XYZ basisY = XYZ.BasisZ;
                XYZ basisZ = basisX.CrossProduct(basisY); // (0,-1,0) se WallAlongX, (1,0,0) se WallAlongY

                var tr = Transform.Identity;
                tr.Origin = origin;
                tr.BasisX = basisX;
                tr.BasisY = basisY;
                tr.BasisZ = basisZ;

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
                    section.Scale = 25; // Escala 1:25 executiva para detalhamento LSF
                    section.DetailLevel = ViewDetailLevel.Fine;

                    doc.Regenerate();
                    AddAutoDimensionsToElevation(doc, section, panel, pipesList);
                    generatedViews.Add(section);
                    idx++;
                }
                catch
                {
                }
            }

            if (generatedViews.Count > 0)
                TryAssembleExecutiveSheet(doc, generatedViews);

            return generatedViews;
        }

        private static void AddAutoDimensionsToElevation(
            Document doc,
            ViewSection view,
            WallPanelDescriptor panel,
            List<Pipe> pipes)
        {
            // Cota vertical dos trechos horizontais dentro da parede
            var horizPipes = pipes.Where(p =>
            {
                if (p.Location is not LocationCurve lc || lc.Curve is not Line line) return false;
                XYZ a = line.GetEndPoint(0);
                XYZ b = line.GetEndPoint(1);
                if (Math.Abs(a.Z - b.Z) > 0.03) return false;
                double n = panel.WallAlongX ? (a.Y + b.Y) * 0.5 : (a.X + b.X) * 0.5;
                return Math.Abs(n - panel.WallPlane) <= 0.25 * FeetPerMeter;
            }).ToList();

            if (horizPipes.Count >= 2)
            {
                try
                {
                    var refs = new ReferenceArray();
                    foreach (var hp in horizPipes.Take(6))
                        refs.Append(new Reference(hp));

                    double dimU = panel.MaxU + (0.20 * FeetPerMeter);
                    XYZ p1 = panel.WallAlongX
                        ? new XYZ(dimU, panel.WallPlane, panel.FloorZ)
                        : new XYZ(panel.WallPlane, dimU, panel.FloorZ);
                    XYZ p2 = panel.WallAlongX
                        ? new XYZ(dimU, panel.WallPlane, panel.FloorZ + 2.50 * FeetPerMeter)
                        : new XYZ(panel.WallPlane, dimU, panel.FloorZ + 2.50 * FeetPerMeter);

                    doc.Create.NewDimension(view, Line.CreateBound(p1, p2), refs);
                }
                catch { }
            }

            // Cota horizontal dos sub-ramais verticais (posicionamento de furação entre montantes)
            var vertPipes = pipes.Where(p =>
            {
                if (p.Location is not LocationCurve lc || lc.Curve is not Line line) return false;
                XYZ a = line.GetEndPoint(0);
                XYZ b = line.GetEndPoint(1);
                if (Math.Abs(a.X - b.X) > 0.03 || Math.Abs(a.Y - b.Y) > 0.03) return false;
                double n = panel.WallAlongX ? a.Y : a.X;
                return Math.Abs(n - panel.WallPlane) <= 0.25 * FeetPerMeter;
            }).ToList();

            if (vertPipes.Count >= 2)
            {
                try
                {
                    var refs = new ReferenceArray();
                    foreach (var vp in vertPipes.Take(8))
                        refs.Append(new Reference(vp));

                    double dimZ = panel.FloorZ + (2.35 * FeetPerMeter);
                    XYZ p1 = panel.WallAlongX
                        ? new XYZ(panel.MinU, panel.WallPlane, dimZ)
                        : new XYZ(panel.WallPlane, panel.MinU, dimZ);
                    XYZ p2 = panel.WallAlongX
                        ? new XYZ(panel.MaxU, panel.WallPlane, dimZ)
                        : new XYZ(panel.WallPlane, panel.MaxU, dimZ);

                    doc.Create.NewDimension(view, Line.CreateBound(p1, p2), refs);
                }
                catch { }
            }
        }

        private static void TryAssembleExecutiveSheet(Document doc, List<ViewSection> elevations)
        {
            try
            {
                ElementId titleBlockId = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .OfClass(typeof(FamilySymbol))
                    .FirstElementId();

                var sheet = ViewSheet.Create(doc, titleBlockId);
                sheet.Name = "Bahr Hidro - Projeto Executivo Hidráulico & LSF";

                double xCursor = 0.25 * FeetPerMeter;
                double yCursor = 0.55 * FeetPerMeter;

                foreach (var view in elevations.Take(3))
                {
                    if (Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    {
                        Viewport.Create(doc, sheet.Id, view.Id, new XYZ(xCursor, yCursor, 0));
                        xCursor += 0.95 * FeetPerMeter;
                    }
                }
            }
            catch
            {
            }
        }
    }
}
