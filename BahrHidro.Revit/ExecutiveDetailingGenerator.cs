using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace BahrHidro.Revit
{
    public static class ExecutiveDetailingGenerator
    {
        private const double FeetPerMeter = 3.280839895013123;
        private const double MmToFeet = 1.0 / 304.8;

        public static int AuditAndMarkLsfPenetrations(Document doc, IEnumerable<Pipe> pipes, double floorZ)
        {
            if (doc == null || pipes == null) return 0;

            var studs = new List<BoundingBoxXYZ>();
            var cats = new[]
            {
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns
            };

            foreach (var cat in cats)
            {
                foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType().OfCategory(cat))
                {
                    var bb = el.get_BoundingBox(null);
                    if (bb != null && (bb.Max.Z - bb.Min.Z) > 1.5 * FeetPerMeter)
                        studs.Add(bb);
                }
            }

            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ldoc = link.GetLinkDocument();
                if (ldoc == null) continue;
                Transform tr = link.GetTotalTransform();
                foreach (var cat in cats)
                {
                    foreach (var el in new FilteredElementCollector(ldoc).WhereElementIsNotElementType().OfCategory(cat))
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb == null) continue;
                        XYZ p1 = tr.OfPoint(bb.Min);
                        XYZ p2 = tr.OfPoint(bb.Max);
                        var worldBb = new BoundingBoxXYZ
                        {
                            Min = new XYZ(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Min(p1.Z, p2.Z)),
                            Max = new XYZ(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), Math.Max(p1.Z, p2.Z))
                        };
                        if ((worldBb.Max.Z - worldBb.Min.Z) > 1.5 * FeetPerMeter)
                            studs.Add(worldBb);
                    }
                }
            }

            if (studs.Count == 0) return 0;

            int crossings = 0;
            const double minEdgeDistFeet = 0.15 * FeetPerMeter; // 150mm das guias LSF
            const double maxHoleMm = 38.0; // Furo máximo alma Ue 90x40

            foreach (var pipe in pipes.Where(p => p != null && p.IsValidObject))
            {
                if (pipe.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                XYZ a = line.GetEndPoint(0);
                XYZ b = line.GetEndPoint(1);
                if (Math.Abs(a.Z - b.Z) > 0.05) continue; // Apenas trechos horizontais

                double diamMm = (pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0.0) / MmToFeet;
                double pipeZ = (a.Z + b.Z) * 0.5;
                double minX = Math.Min(a.X, b.X);
                double maxX = Math.Max(a.X, b.X);
                double minY = Math.Min(a.Y, b.Y);
                double maxY = Math.Max(a.Y, b.Y);

                int hitCount = 0;
                bool edgeViolation = false;

                foreach (var st in studs)
                {
                    bool overlapX = maxX >= st.Min.X - 0.05 && minX <= st.Max.X + 0.05;
                    bool overlapY = maxY >= st.Min.Y - 0.05 && minY <= st.Max.Y + 0.05;
                    bool overlapZ = pipeZ >= st.Min.Z && pipeZ <= st.Max.Z;

                    if (overlapX && overlapY && overlapZ)
                    {
                        hitCount++;
                        if ((pipeZ - st.Min.Z) < minEdgeDistFeet || (st.Max.Z - pipeZ) < minEdgeDistFeet)
                            edgeViolation = true;
                    }
                }

                if (hitCount > 0)
                {
                    crossings += hitCount;
                    var cmtParam = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (cmtParam != null && !cmtParam.IsReadOnly)
                    {
                        string baseCmt = cmtParam.AsString() ?? "BahrHidro";
                        string status = (diamMm > maxHoleMm || edgeViolation)
                            ? $"[ALERTA LSF: {hitCount} furo(s), DN{(int)diamMm}mm]"
                            : $"[OK LSF: {hitCount} furo(s) c/ Anel Isolador]";
                        if (!baseCmt.Contains("LSF:"))
                            cmtParam.Set($"{baseCmt} | {status}");
                    }
                }
            }

            return crossings;
        }

        public static void CreateHydraulicSchedules(Document doc)
        {
            if (doc == null) return;

            const string pipeScheduleName = "Smart Hidro - Quantitativo de Tubulações (NBR 5626 / 8160)";
            const string fittingScheduleName = "Smart Hidro - Quantitativo de Conexões e Registros";

            var existingNames = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Select(v => v.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!existingNames.Contains(pipeScheduleName))
            {
                try
                {
                    var vs = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_PipeCurves));
                    vs.Name = pipeScheduleName;
                    var def = vs.Definition;
                    def.IsItemized = false;

                    AddFieldIfAvailable(def, BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM, false);
                    AddFieldIfAvailable(def, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, false);
                    AddFieldIfAvailable(def, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, false);
                    AddFieldIfAvailable(def, BuiltInParameter.CURVE_ELEM_LENGTH, true);
                }
                catch { }
            }

            if (!existingNames.Contains(fittingScheduleName))
            {
                try
                {
                    var vs = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_PipeFitting));
                    vs.Name = fittingScheduleName;
                    var def = vs.Definition;
                    def.IsItemized = false;

                    AddFieldIfAvailable(def, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM, false);
                    AddFieldIfAvailable(def, BuiltInParameter.RBS_CALCULATED_SIZE, false);
                    def.ShowGrandTotal = true;
                    def.ShowGrandTotalCount = true;
                }
                catch { }
            }
        }

        public static int TagPipesInView(Document doc, View view)
        {
            if (doc == null || view == null || view.IsTemplate) return 0;

            if (view is View3D v3d && !v3d.IsLocked)
            {
                if (v3d.CanSaveOrientation())
                    v3d.SaveOrientationAndLock();
                else
                    return 0;
            }

            var pipes = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .OfClass(typeof(Pipe))
                .Cast<Pipe>()
                .ToList();

            int tagged = 0;
            foreach (var pipe in pipes)
            {
                if (pipe.Location is not LocationCurve lc) continue;
                XYZ mid = lc.Curve.Evaluate(0.5, true);
                try
                {
                    IndependentTag.Create(
                        doc,
                        view.Id,
                        new Reference(pipe),
                        false,
                        TagMode.TM_ADDBY_CATEGORY,
                        TagOrientation.Horizontal,
                        mid);
                    tagged++;
                }
                catch
                {
                    break; // Para caso o template IFC não tenha família de Tag carregada
                }
            }

            return tagged;
        }

        private static void AddFieldIfAvailable(ScheduleDefinition def, BuiltInParameter bip, bool calculateTotals)
        {
            var targetId = new ElementId(bip);
            var sf = def.GetSchedulableFields().FirstOrDefault(f => f.ParameterId == targetId);
            if (sf == null) return;

            var field = def.AddField(sf);
            if (calculateTotals)
            {
                field.DisplayType = ScheduleFieldDisplayType.Totals;
                def.ShowGrandTotal = true;
            }
        }
    }
}
