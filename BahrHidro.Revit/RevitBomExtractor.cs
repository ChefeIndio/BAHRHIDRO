using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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

            var allPipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            var fittingCategories = new[] { BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory };
            var fittings = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(fittingCategories))
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            // Mapeia agrupamentos espaciais (Kits) caso não existam Rooms nativos no modelo
            var spatialCenters = new List<(XYZ Pt, string KitName)>();
            double clusterRadiusFeet = UnitUtils.ConvertToInternalUnits(2.50, UnitTypeId.Meters);

            string ResolveKitName(XYZ point)
            {
                try
                {
                    Room? room = doc.GetRoomAtPoint(point);
                    if (room != null && !string.IsNullOrWhiteSpace(room.Name))
                        return $"Kit {room.Name}";
                }
                catch { }

                foreach (var sc in spatialCenters)
                {
                    double distXY = Math.Sqrt(Math.Pow(sc.Pt.X - point.X, 2) + Math.Pow(sc.Pt.Y - point.Y, 2));
                    if (distXY <= clusterRadiusFeet && Math.Abs(sc.Pt.Z - point.Z) <= UnitUtils.ConvertToInternalUnits(3.5, UnitTypeId.Meters))
                        return sc.KitName;
                }

                string newKit = $"Kit Ambiente {spatialCenters.Count + 1:D2}";
                spatialCenters.Add((point, newKit));
                return newKit;
            }

            // 1. Tubos Globais + Kits
            var pipeGroups = new Dictionary<(string Sys, string TypeName, double DiaMm), double>();
            var kitPipeGroups = new Dictionary<(string Kit, string Sys, string TypeName, double DiaMm), double>();

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

                XYZ midPt = p.Location is LocationCurve lc ? lc.Curve.Evaluate(0.5, true) : XYZ.Zero;
                string kitName = ResolveKitName(midPt);
                var kKey = (kitName, sysName, typeName, diaMm);
                kitPipeGroups[kKey] = kitPipeGroups.TryGetValue(kKey, out double kCur) ? kCur + lenM : lenM;
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

            // 2. Conexões e Registros Globais + Kits
            var fittingGroups = new Dictionary<(string Sys, string Cat, string Name, string Size), int>();
            var kitFittingGroups = new Dictionary<(string Kit, string Sys, string Cat, string Name, string Size), int>();

            foreach (FamilyInstance fi in fittings)
            {
                string sysName = fi.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "Geral";
                string catLabel = fi.Category?.Id.Value == (long)BuiltInCategory.OST_PipeAccessory
                    ? "Registro / Acessório"
                    : "Conexão Hidráulica";

                string famName = fi.Symbol?.FamilyName ?? "";
                string typeName = fi.Name ?? "";
                string fullName = string.IsNullOrWhiteSpace(famName) ? typeName : $"{famName} — {typeName}";

                string sizeStr = fi.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? "";
                if (string.IsNullOrWhiteSpace(sizeStr))
                    sizeStr = ReadConnectorSizes(fi);

                var key = (sysName, catLabel, fullName, sizeStr);
                fittingGroups[key] = fittingGroups.TryGetValue(key, out int qty) ? qty + 1 : 1;

                XYZ locPt = fi.Location is LocationPoint lp ? lp.Point : XYZ.Zero;
                string kitName = ResolveKitName(locPt);
                var kKey = (kitName, sysName, catLabel, fullName, sizeStr);
                kitFittingGroups[kKey] = kitFittingGroups.TryGetValue(kKey, out int kQty) ? kQty + 1 : 1;
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

            // 3. Consolida Lista de Kits por Ambiente
            var kitItems = new List<KitTakeoffItem>();
            foreach (var kv in kitPipeGroups.OrderBy(x => x.Key.Kit).ThenBy(x => x.Key.Sys).ThenBy(x => x.Key.DiaMm))
            {
                double withWaste = Math.Round(kv.Value * factor, 2);
                kitItems.Add(new KitTakeoffItem(
                    kv.Key.Kit,
                    kv.Key.Sys,
                    "Tubulação",
                    kv.Key.TypeName,
                    $"DN {kv.Key.DiaMm:F0} mm",
                    withWaste,
                    "m"));
            }

            foreach (var kv in kitFittingGroups.OrderBy(x => x.Key.Kit).ThenBy(x => x.Key.Sys).ThenBy(x => x.Key.Name))
            {
                kitItems.Add(new KitTakeoffItem(
                    kv.Key.Kit,
                    kv.Key.Sys,
                    kv.Key.Cat,
                    kv.Key.Name,
                    kv.Key.Size,
                    kv.Value,
                    "un"));
            }

            var lsfHoles = RevitLsfFabricationManager.AnalyzeLsfPunchingHoles(doc);

            return new BimMaterialTakeoff(
                string.IsNullOrWhiteSpace(projectName) ? "Projeto BIM Revit" : projectName,
                safeWaste,
                pipeItems,
                fittingItems,
                kitItems,
                lsfHoles);
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
