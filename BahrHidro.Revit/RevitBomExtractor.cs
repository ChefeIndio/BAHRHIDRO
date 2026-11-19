using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using BahrHidro.Core.Reporting;

namespace BahrHidro.Revit
{
    public static class RevitBomExtractor
    {
        public static BimMaterialTakeoff ExtractFromDocument(Document doc, string projectName, double wasteMarginPercent)
        {
            double safeWaste = Math.Max(0, wasteMarginPercent);
            double factor = 1.0 + (safeWaste / 100.0);

            // 1. Extração de Tubos (Pipe)
            var allPipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            var pipeGroups = new Dictionary<(string Sys, string TypeName, double DiaMm), double>();

            foreach (Pipe p in allPipes)
            {
                string sysName = p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "Geral";
                string typeName = p.PipeType?.Name ?? "Tubo Padrão";

                double diaMm = 0;
                Parameter? dParam = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (dParam != null && dParam.HasValue)
                    diaMm = Math.Round(UnitUtils.ConvertFromInternalUnits(dParam.AsDouble(), UnitTypeId.Millimeters), 1);

                double lenM = 0;
                Parameter? lParam = p.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (lParam != null && lParam.HasValue)
                    lenM = UnitUtils.ConvertFromInternalUnits(lParam.AsDouble(), UnitTypeId.Meters);

                if (lenM <= 0) continue;

                var key = (sysName, typeName, diaMm);
                pipeGroups[key] = pipeGroups.TryGetValue(key, out double current) ? current + lenM : lenM;
            }

            var pipeItems = pipeGroups
                .OrderBy(kv => kv.Key.Sys)
                .ThenBy(kv => kv.Key.TypeName)
                .ThenBy(kv => kv.Key.DiaMm)
                .Select(kv =>
                {
                    double net = Math.Round(kv.Value, 2);
                    double withWaste = Math.Round(net * factor, 2);
                    int bars = (int)Math.Ceiling(withWaste / 6.0);
                    return new PipeTakeoffItem(
                        kv.Key.Sys,
                        kv.Key.TypeName,
                        kv.Key.DiaMm,
                        net,
                        safeWaste,
                        withWaste,
                        bars);
                })
                .ToList();

            // 2. Extração de Conexões (OST_PipeFitting) e Acessórios/Registros (OST_PipeAccessory)
            var fittingCategories = new[] { BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory };
            var fittings = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(fittingCategories))
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            var fittingGroups = new Dictionary<(string Sys, string Cat, string Name, string Size), int>();

            foreach (FamilyInstance fi in fittings)
            {
                string sysName = fi.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "Geral";
                string catLabel = fi.Category?.Id.Value == (long)BuiltInCategory.OST_PipeAccessory
                    ? "Acessório / Registro"
                    : "Conexão Hidráulica";

                string famName = fi.Symbol?.FamilyName ?? "";
                string typeName = fi.Name ?? "";
                string fullName = string.IsNullOrWhiteSpace(famName) ? typeName : $"{famName} — {typeName}";

                string sizeStr = fi.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "";
                if (string.IsNullOrWhiteSpace(sizeStr))
                {
                    sizeStr = ReadConnectorSizes(fi);
                }

                var key = (sysName, catLabel, fullName, sizeStr);
                fittingGroups[key] = fittingGroups.TryGetValue(key, out int qty) ? qty + 1 : 1;
            }

            var fittingItems = fittingGroups
                .OrderBy(kv => kv.Key.Sys)
                .ThenBy(kv => kv.Key.Cat)
                .ThenBy(kv => kv.Key.Name)
                .ThenBy(kv => kv.Key.Size)
                .Select(kv => new FittingTakeoffItem(
                    kv.Key.Sys,
                    kv.Key.Cat,
                    kv.Key.Name,
                    kv.Key.Size,
                    kv.Value))
                .ToList();

            return new BimMaterialTakeoff(
                string.IsNullOrWhiteSpace(projectName) ? "Projeto BIM Revit" : projectName,
                safeWaste,
                pipeItems,
                fittingItems);
        }

        private static string ReadConnectorSizes(FamilyInstance fi)
        {
            try
            {
                var conns = fi.MEPModel?.ConnectorManager?.Connectors;
                if (conns == null || conns.IsEmpty) return "--";

                var dias = new List<double>();
                foreach (Connector c in conns)
                {
                    if (c.Domain == Domain.DomainPiping && c.Shape == ConnectorProfileType.Round)
                    {
                        double mm = Math.Round(UnitUtils.ConvertFromInternalUnits(c.Radius * 2.0, UnitTypeId.Millimeters), 0);
                        if (mm > 0 && !dias.Contains(mm))
                            dias.Add(mm);
                    }
                }

                if (dias.Count == 0) return "--";
                return string.Join(" × ", dias.OrderByDescending(x => x).Select(x => $"{x:F0} mm"));
            }
            catch
            {
                return "--";
            }
        }
    }
}
