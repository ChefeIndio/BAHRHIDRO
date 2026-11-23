using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using BahrHidro.Core.Hydraulic.Pumping;
using BahrHidro.Core.Hydraulic.SewageTreatment;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitParametricEquipmentPlacer
    {
        public static (EquipmentPlacement3DResult Result, List<ElementId> CreatedIds) PlaceReservoirsIn3D(
            Document doc,
            ReservoirPumpingResult res)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (levels.Count == 0)
                throw new InvalidOperationException("Nenhum nível encontrado no projeto Revit.");

            Level lowestLevel = levels.First();
            Level highestLevel = levels.Last();

            var (anchorX, anchorY) = GetExternalPlacementAnchor(doc, offsetMetersX: 2.0, offsetMetersY: 0.0);
            var createdIds = new List<ElementId>();
            var pt = new CultureInfo("pt-BR");

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Inserir Reservatórios Dimensionados 3D (NBR 5626)"))
            {
                tx.Start();

                if (res.CommercialLowerReservoirLiters > 0)
                {
                    double volM3 = res.CommercialLowerReservoirLiters / 1000.0;
                    double heightM = volM3 <= 3.0 ? 1.50 : (volM3 <= 10.0 ? 2.00 : 2.50);
                    double diaM = Math.Max(1.00, Math.Round(Math.Sqrt((4.0 * volM3) / (Math.PI * heightM)), 2));
                    double zBaseM = UnitUtils.ConvertFromInternalUnits(lowestLevel.Elevation, UnitTypeId.Meters) - heightM;

                    string name = $"Reservatório Inferior {res.CommercialLowerReservoirLiters.ToString("N0", pt)} L (Ø {diaM:F2}m × H {heightM:F2}m)";
                    string notes = $"NBR 5626 | Volume Comercial: {res.CommercialLowerReservoirLiters} L | Calculado: {res.RequiredLowerLiters:N0} L | Sucção DN {res.SuctionNominalDiameterMm} mm";

                    var ds = CreateCylinderFixture(doc, anchorX, anchorY, zBaseM, diaM, heightM, "RES-INF", name, notes);
                    createdIds.Add(ds.Id);
                }

                if (res.CommercialUpperReservoirLiters > 0)
                {
                    double volM3 = res.CommercialUpperReservoirLiters / 1000.0;
                    double heightM = volM3 <= 3.0 ? 1.40 : (volM3 <= 10.0 ? 1.90 : 2.30);
                    double diaM = Math.Max(1.00, Math.Round(Math.Sqrt((4.0 * volM3) / (Math.PI * heightM)), 2));
                    double zBaseM = UnitUtils.ConvertFromInternalUnits(highestLevel.Elevation, UnitTypeId.Meters) + 0.15;

                    string name = $"Reservatório Superior {res.CommercialUpperReservoirLiters.ToString("N0", pt)} L (Ø {diaM:F2}m × H {heightM:F2}m)";
                    string notes = $"NBR 5626 | Volume Comercial: {res.CommercialUpperReservoirLiters} L (inclui RTI) | Recalque DN {res.DischargeNominalDiameterMm} mm | AMT {res.TotalDynamicHeadAmtMeters:F2} m.c.a.";

                    var ds = CreateCylinderFixture(doc, anchorX + 3.2, anchorY, zBaseM, diaM, heightM, "RES-SUP", name, notes);
                    createdIds.Add(ds.Id);
                }

                tx.Commit();
            }

            string summary = $"Reservatórios inseridos no 3D: Inferior ({res.CommercialLowerReservoirLiters:N0} L) e Superior ({res.CommercialUpperReservoirLiters:N0} L) com dimensões paramétricas NBR 5626.";
            return (new EquipmentPlacement3DResult(createdIds.Count, summary), createdIds);
        }

        public static (EquipmentPlacement3DResult Result, List<ElementId> CreatedIds) PlaceSewageTreatmentTrainIn3D(
            Document doc,
            SewageTreatmentResult res)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (levels.Count == 0)
                throw new InvalidOperationException("Nenhum nível encontrado no projeto Revit.");

            double groundZM = UnitUtils.ConvertFromInternalUnits(levels.First().Elevation, UnitTypeId.Meters);
            var (cursorX, baseY) = GetExternalPlacementAnchor(doc, offsetMetersX: 2.5, offsetMetersY: 1.5);
            var createdIds = new List<ElementId>();
            var pt = new CultureInfo("pt-BR");

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Inserir Caixa de Gordura, Fossa, Filtro e Sumidouro 3D"))
            {
                tx.Start();

                // 1. Caixa de Gordura (NBR 8160)
                if (res.GreaseTrapVolumeLiters > 0)
                {
                    if (res.GreaseTrapModelCode.StartsWith("CGE", StringComparison.OrdinalIgnoreCase))
                    {
                        double volM3 = Math.Max(0.25, res.GreaseTrapVolumeLiters / 1000.0);
                        double hM = 0.85;
                        double wM = Math.Max(0.60, Math.Round(Math.Sqrt(volM3 / (2.0 * 0.65)), 2));
                        double lM = Math.Round(2.0 * wM, 2);

                        string name = $"Caixa de Gordura {res.GreaseTrapModelCode} ({wM:F2}m × {lM:F2}m × H {hM:F2}m)";
                        string notes = $"NBR 8160 | {res.GreaseTrapDescription} | Saída DN {res.GreaseTrapOutletDnMm} mm";
                        var ds = CreatePrismFixture(doc, cursorX, baseY, groundZM - hM, wM, lM, hM, "CG-01", name, notes);
                        createdIds.Add(ds.Id);
                        cursorX += lM + 1.50;
                    }
                    else
                    {
                        double diaM = res.GreaseTrapModelCode.StartsWith("CGP") ? 0.30 :
                                      (res.GreaseTrapModelCode.StartsWith("CGS") ? 0.40 : 0.60);
                        double hM = res.GreaseTrapModelCode.StartsWith("CGD") ? 0.80 : 0.55;

                        string name = $"Caixa de Gordura {res.GreaseTrapModelCode} (Ø {diaM:F2}m × H {hM:F2}m)";
                        string notes = $"NBR 8160 | Volume {res.GreaseTrapVolumeLiters:F0} L | Saída DN {res.GreaseTrapOutletDnMm} mm";
                        var ds = CreateCylinderFixture(doc, cursorX, baseY, groundZM - hM, diaM, hM, "CG-01", name, notes);
                        createdIds.Add(ds.Id);
                        cursorX += diaM + 1.50;
                    }
                }

                // 2. Tanque Séptico / Fossa Séptica (NBR 7229)
                {
                    double diaM = res.SepticTankDiameterMeters;
                    double totalHM = Math.Round(res.SepticTankUsefulDepthMeters + 0.35, 2);
                    cursorX += diaM * 0.5;

                    string name = $"Tanque Séptico {res.SepticTankUsefulVolumeLiters.ToString("N0", pt)} L (Ø {diaM:F2}m × H {totalHM:F2}m)";
                    string notes = $"NBR 7229 | Vol. Útil: {res.SepticTankUsefulVolumeLiters:N0} L | Detenção T={res.DetentionTimeDays:F2}d | Fator Lodo K={res.SludgeAccumulationFactorK}d";
                    var ds = CreateCylinderFixture(doc, cursorX, baseY, groundZM - totalHM, diaM, totalHM, "TS-01", name, notes);
                    createdIds.Add(ds.Id);
                    cursorX += diaM * 0.5 + 1.50;
                }

                // 3. Filtro Anaeróbio (NBR 13969)
                {
                    double diaM = res.AnaerobicFilterDiameterMeters;
                    double totalHM = res.AnaerobicFilterTotalHeightMeters;
                    cursorX += diaM * 0.5;

                    string name = $"Filtro Anaeróbio {res.AnaerobicFilterUsefulVolumeLiters.ToString("N0", pt)} L (Ø {diaM:F2}m × H {totalHM:F2}m)";
                    string notes = $"NBR 13969 | Vol. Útil: {res.AnaerobicFilterUsefulVolumeLiters:N0} L | Leito Brita 4/5 H=1,20 m";
                    var ds = CreateCylinderFixture(doc, cursorX, baseY, groundZM - totalHM, diaM, totalHM, "FA-01", name, notes);
                    createdIds.Add(ds.Id);
                    cursorX += diaM * 0.5 + 2.00;
                }

                // 4. Sumidouro(s) (NBR 13969)
                int units = Math.Max(1, res.SumidouroUnitsCount);
                double sumDiaM = res.SumidouroDiameterMeters;
                double sumTotalHM = Math.Round(res.SumidouroUsefulDepthMeters + 0.30, 2);
                double spacingM = Math.Max(sumDiaM * 3.0, sumDiaM + 1.50);

                for (int i = 0; i < units; i++)
                {
                    double unitY = baseY + i * spacingM;
                    double unitX = cursorX + sumDiaM * 0.5;
                    string tag = $"SUM-{i + 1:00}";
                    string name = $"Sumidouro {tag} (Ø {sumDiaM:F2}m × H_útil {res.SumidouroUsefulDepthMeters:F2}m)";
                    string notes = $"NBR 13969 | Área Infiltração Total: {res.RequiredInfiltrationAreaM2:F2} m² (Ci={res.SoilInfiltrationCoeffLpm2Day:F0} L/m².d) | Unidade {i + 1}/{units}";
                    var ds = CreateCylinderFixture(doc, unitX, unitY, groundZM - sumTotalHM, sumDiaM, sumTotalHM, tag, name, notes);
                    createdIds.Add(ds.Id);
                }

                tx.Commit();
            }

            string summary = $"Sistema de tratamento inserido no 3D: {createdIds.Count} unidade(s) paramétrica(s) (Caixa de Gordura, Tanque Séptico, Filtro Anaeróbio e {res.SumidouroUnitsCount} Sumidouro(s)).";
            return (new EquipmentPlacement3DResult(createdIds.Count, summary), createdIds);
        }

        private static (double Xm, double Ym) GetExternalPlacementAnchor(Document doc, double offsetMetersX, double offsetMetersY)
        {
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.get_BoundingBox(null) != null);

            double maxX = double.MinValue;
            double minY = double.MaxValue;
            bool found = false;

            foreach (var el in collector)
            {
                if (el is View || el is Level) continue;
                BoundingBoxXYZ? bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                if (bb.Max.X > maxX) maxX = bb.Max.X;
                if (bb.Min.Y < minY) minY = bb.Min.Y;
                found = true;
            }

            if (!found || maxX < -10000 || minY > 10000)
                return (offsetMetersX, offsetMetersY);

            double xM = UnitUtils.ConvertFromInternalUnits(maxX, UnitTypeId.Meters) + offsetMetersX;
            double yM = UnitUtils.ConvertFromInternalUnits(minY, UnitTypeId.Meters) + offsetMetersY;
            return (Math.Round(xM, 2), Math.Round(yM, 2));
        }

        private static DirectShape CreateCylinderFixture(
            Document doc,
            double centerXM,
            double centerYM,
            double baseZM,
            double diameterM,
            double heightM,
            string mark,
            string elementName,
            string comments)
        {
            double cx = UnitUtils.ConvertToInternalUnits(centerXM, UnitTypeId.Meters);
            double cy = UnitUtils.ConvertToInternalUnits(centerYM, UnitTypeId.Meters);
            double cz = UnitUtils.ConvertToInternalUnits(baseZM, UnitTypeId.Meters);
            double r = UnitUtils.ConvertToInternalUnits(diameterM * 0.5, UnitTypeId.Meters);
            double h = UnitUtils.ConvertToInternalUnits(heightM, UnitTypeId.Meters);

            XYZ center = new XYZ(cx, cy, cz);
            Arc arc1 = Arc.Create(center, r, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY);
            Arc arc2 = Arc.Create(center, r, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY);

            var loop = new CurveLoop();
            loop.Append(arc1);
            loop.Append(arc2);

            Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h);
            DirectShape ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_PlumbingFixtures));
            ds.SetShape(new List<GeometryObject> { solid });
            ds.Name = elementName;

            ApplyMetadata(ds, mark, elementName, comments);
            return ds;
        }

        private static DirectShape CreatePrismFixture(
            Document doc,
            double centerXM,
            double centerYM,
            double baseZM,
            double widthXM,
            double lengthYM,
            double heightM,
            string mark,
            string elementName,
            string comments)
        {
            double cx = UnitUtils.ConvertToInternalUnits(centerXM, UnitTypeId.Meters);
            double cy = UnitUtils.ConvertToInternalUnits(centerYM, UnitTypeId.Meters);
            double cz = UnitUtils.ConvertToInternalUnits(baseZM, UnitTypeId.Meters);
            double hx = UnitUtils.ConvertToInternalUnits(widthXM * 0.5, UnitTypeId.Meters);
            double hy = UnitUtils.ConvertToInternalUnits(lengthYM * 0.5, UnitTypeId.Meters);
            double h = UnitUtils.ConvertToInternalUnits(heightM, UnitTypeId.Meters);

            XYZ p0 = new XYZ(cx - hx, cy - hy, cz);
            XYZ p1 = new XYZ(cx + hx, cy - hy, cz);
            XYZ p2 = new XYZ(cx + hx, cy + hy, cz);
            XYZ p3 = new XYZ(cx - hx, cy + hy, cz);

            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));

            Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h);
            DirectShape ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_PlumbingFixtures));
            ds.SetShape(new List<GeometryObject> { solid });
            ds.Name = elementName;

            ApplyMetadata(ds, mark, elementName, comments);
            return ds;
        }

        private static void ApplyMetadata(DirectShape ds, string mark, string description, string comments)
        {
            try { ds.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark); } catch { }
            try { ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comments); } catch { }
            try { ds.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Set(description); } catch { }
        }
    }
}
