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
    public sealed class SanitaryExecutionReport
    {
        public int ZonesCount { get; set; }
        public int PipesCreated { get; set; }
        public int TotalUhc { get; set; }
        public double MaxCollectorDnMm { get; set; } = 100.0;
        public List<string> ZoneDetails { get; set; } = new();
    }

    public static class SanitarySewerRoutingEngine
    {
        private const double FeetPerMeter = 3.280839895013123;
        private const double MmToFeet = 1.0 / 304.8;

        public static List<Pipe> GenerateSanitaryNetworkOnly(
            Document doc,
            out SanitaryExecutionReport report,
            ElementId? preferredSewerPipeTypeId = null)
        {
            report = new SanitaryExecutionReport();
            var allSanitaryPipes = new List<Pipe>();
            if (doc == null) return allSanitaryPipes;

            var fixtures = SmartHydraulicRoutingEngine.DiscoverSemanticFixtures(doc);
            var sewerFixtures = fixtures.Where(f => f.NeedsSewer).ToList();
            if (sewerFixtures.Count == 0) return allSanitaryPipes;

            double globalFloorZ = sewerFixtures.Min(f => f.BaseZ);
            double roofZ = SmartHydraulicRoutingEngine.DiscoverRoofElevationPublic(doc, globalFloorZ, out _);

            var zones = SmartHydraulicRoutingEngine.BuildHydraulicWallZonesPublic(doc, sewerFixtures);
            if (zones.Count == 0) return allSanitaryPipes;

            ElementId sanSysTypeId = PipeNetworkHelper.GetOrCreateSystemTypeId(doc, MEPSystemClassification.Sanitary, "Esgoto Sanitário - Smart Hidro");
            ElementId ventSysTypeId = PipeNetworkHelper.GetOrCreateSystemTypeId(doc, MEPSystemClassification.Vent, "Ventilação Sanitária - Smart Hidro");
            ElementId sewerPipeTypeId = ResolveSewerPipeTypeId(doc, preferredSewerPipeTypeId);
            ElementId levelId = ResolveNearestLevelId(doc, globalFloorZ);

            if (sanSysTypeId == ElementId.InvalidElementId || sewerPipeTypeId == ElementId.InvalidElementId || levelId == ElementId.InvalidElementId)
                return allSanitaryPipes;

            bool ownTransaction = !doc.IsModifiable;
            Transaction? tx = null;

            try
            {
                if (ownTransaction)
                {
                    tx = new Transaction(doc, "Smart Hidro - Rede de Esgoto Sanitário e Ventilação (NBR 8160)");
                    tx.Start();
                }

                PipeNetworkHelper.PurgePreviousBahrHidroElements(doc, purgeWater: false, purgeSanitaryAndStorm: true);
                FamilyLoader.EnsureEssentialFittings(doc);
                PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, sewerPipeTypeId);

                // Alinha tubos de queda entre pavimentos sobrepostos
                SmartHydraulicRoutingEngine.ConsolidateVerticalDropsPublic(zones);
                var existingWaterStacks = PipeNetworkHelper.GetExistingVerticalStackLocations(doc);
                foreach (var z in zones)
                {
                    bool hitsWater = existingWaterStacks.Any(st =>
                        Math.Abs((z.WallAlongX ? st.Y : st.X) - z.WallPlane) <= 0.25 * FeetPerMeter &&
                        Math.Abs((z.WallAlongX ? st.X : st.Y) - z.DropU) <= 0.25 * FeetPerMeter);
                    if (hitsWater)
                    {
                        z.DropU = SmartHydraulicRoutingEngine.ChooseFreeStudBayPublic(z.StudUs, z.DropU + 0.35 * FeetPerMeter, z.DropU + 1.50 * FeetPerMeter, z.DropU + 0.50 * FeetPerMeter);
                    }
                }

                // Ordena zonas do pavimento mais alto para o térreo para conectar tubos de queda ao subcoletor
                var orderedZones = zones.OrderByDescending(z => z.FloorZ).ToList();
                var stackFootPoints = new List<(XYZ Point, int Uhc, double DnMm)>();

                foreach (var zone in orderedZones)
                {
                    if (zone.SewerFixtures.Count == 0) continue;

                    var zonePipes = RouteZoneSanitaryAndVent(
                        doc,
                        sanSysTypeId,
                        ventSysTypeId,
                        sewerPipeTypeId,
                        levelId,
                        zone,
                        globalFloorZ,
                        roofZ,
                        out int zoneUhc,
                        out double zoneCollectorDn,
                        out XYZ stackBottomPt,
                        out string zoneSummary);

                    allSanitaryPipes.AddRange(zonePipes);
                    report.TotalUhc += zoneUhc;
                    report.MaxCollectorDnMm = Math.Max(report.MaxCollectorDnMm, zoneCollectorDn);
                    report.ZoneDetails.Add(zoneSummary);
                    stackFootPoints.Add((stackBottomPt, zoneUhc, zoneCollectorDn));
                }

                // Subcoletor Predial no nível térreo interligando todos os tubos de queda até a saída externa (i = 1%)
                if (stackFootPoints.Count > 0)
                {
                    double buildingCollectorDn = SizePrimaryCollectorByUhc(report.TotalUhc);
                    report.MaxCollectorDnMm = Math.Max(report.MaxCollectorDnMm, buildingCollectorDn);

                    XYZ prevPt = stackFootPoints[0].Point;
                    for (int i = 1; i < stackFootPoints.Count; i++)
                    {
                        XYZ nextPt = stackFootPoints[i].Point;
                        double dist = prevPt.DistanceTo(nextPt);
                        if (dist > 0.15)
                        {
                            XYZ slopedNext = new XYZ(nextPt.X, nextPt.Y, Math.Min(nextPt.Z, prevPt.Z - 0.01 * dist));
                            AddSanitaryPipe(doc, allSanitaryPipes, sanSysTypeId, sewerPipeTypeId, levelId, prevPt, slopedNext, buildingCollectorDn,
                                $"ESG-SUB | Subcoletor Predial Trecho #{i} (i=1%) | UHC={report.TotalUhc}");
                            prevPt = slopedNext;
                        }
                    }

                    // Saída final do subcoletor para caixa de inspeção externa (1,50 m além do último nó, i = 1%)
                    XYZ exitPt = new XYZ(prevPt.X + (1.50 * FeetPerMeter), prevPt.Y, prevPt.Z - (0.015 * FeetPerMeter));
                    AddSanitaryPipe(doc, allSanitaryPipes, sanSysTypeId, sewerPipeTypeId, levelId, prevPt, exitPt, buildingCollectorDn,
                        $"ESG-SUB | Saída Caixa Inspeção Externa (i=1%) | UHC={report.TotalUhc}");
                }

                doc.Regenerate();
                PipeFittingAutoConnector.ConnectPipesWithElbows(doc, allSanitaryPipes);

                report.ZonesCount = orderedZones.Count;
                report.PipesCreated = allSanitaryPipes.Count;

                CreateDedicatedSanitaryViews(doc, allSanitaryPipes, globalFloorZ);
                LsfPanelElevationGenerator.CreateLiveHydraulicViews(doc, allSanitaryPipes, null, globalFloorZ);
                ExecutiveDetailingGenerator.CreateHydraulicSchedules(doc);
                WriteSanitaryMemorial(doc, report);

                if (ownTransaction && tx != null)
                    tx.Commit();
            }
            catch
            {
                if (ownTransaction && tx != null && tx.HasStarted())
                    tx.RollBack();
                throw;
            }

            return allSanitaryPipes;
        }

        private static List<Pipe> RouteZoneSanitaryAndVent(
            Document doc,
            ElementId sanSysTypeId,
            ElementId ventSysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            HydraulicWallZone zone,
            double globalFloorZ,
            double roofZ,
            out int zoneUhc,
            out double collectorDnMm,
            out XYZ stackBottomPt,
            out string zoneSummary)
        {
            var pipes = new List<Pipe>();
            XYZ MakePt(double u, double n, double z) => zone.WallAlongX ? new XYZ(u, n, z) : new XYZ(n, u, z);

            double subFloorZ = zone.FloorZ - (0.14 * FeetPerMeter);
            var toilet = zone.SewerFixtures.FirstOrDefault(f => f.Role == HydraulicFixtureRole.Toilet);
            var kitchenSinks = zone.SewerFixtures.Where(f => f.Role == HydraulicFixtureRole.KitchenSink).ToList();
            var secondary = zone.SewerFixtures
                .Where(f => f.Role != HydraulicFixtureRole.Toilet && f.Role != HydraulicFixtureRole.KitchenSink)
                .ToList();

            zoneUhc = zone.SewerFixtures.Sum(f => f.UhC);
            collectorDnMm = SizePrimaryCollectorByUhc(zoneUhc);

            // 1. Ramais Exclusivos de Gordura (Pia de Cozinha / Gourmet -> Caixa de Gordura externa, i = 2%)
            foreach (var ks in kitchenSinks)
            {
                double kU = zone.WallAlongX ? ks.Center.X : ks.Center.Y;
                double kN = zone.WallAlongX ? ks.Center.Y : ks.Center.X;
                double dirSign = kN >= zone.WallPlane ? 1.0 : -1.0;
                double kOutZ = zone.FloorZ + (0.55 * FeetPerMeter);

                XYZ pOut = MakePt(kU, zone.WallPlane + (dirSign * 0.08 * FeetPerMeter), kOutZ);
                XYZ pWall = MakePt(kU, zone.WallPlane, kOutZ);
                XYZ pDrop = MakePt(kU, zone.WallPlane, subFloorZ + 0.03 * FeetPerMeter);
                XYZ pGreaseTrap = MakePt(kU, zone.WallPlane - (dirSign * 1.60 * FeetPerMeter), subFloorZ - 0.01 * FeetPerMeter);

                AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pOut, pWall, 50.0, $"ESG-GORD | Saída {ks.Name} | UHC={ks.UhC}");
                AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pWall, pDrop, 50.0, $"ESG-GORD | Descida Parede {ks.Name} | UHC={ks.UhC}");
                AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pDrop, pGreaseTrap, 50.0, $"ESG-GORD | Ramal Caixa de Gordura (i=2%) | UHC={ks.UhC}");
            }

            // 2. Ramais Secundários (i = 2%) -> Caixa Sifonada (150x150x50)
            var drainNode = secondary.FirstOrDefault(f => f.Role == HydraulicFixtureRole.FloorDrain);
            double csU = drainNode != null
                ? (zone.WallAlongX ? drainNode.Center.X : drainNode.Center.Y)
                : (secondary.Count > 0 ? secondary.Average(f => zone.WallAlongX ? f.Center.X : f.Center.Y) : zone.DropU);
            double csN = drainNode != null
                ? (zone.WallAlongX ? drainNode.Center.Y : drainNode.Center.X)
                : zone.WallPlane + (0.35 * FeetPerMeter);

            XYZ pCaixaSifonada = MakePt(csU, csN, subFloorZ);

            foreach (var f in secondary)
            {
                if (f.Role == HydraulicFixtureRole.FloorDrain) continue;

                double u = zone.WallAlongX ? f.Center.X : f.Center.Y;
                double fixN = zone.WallAlongX ? f.Center.Y : f.Center.X;
                double dirSign = fixN >= zone.WallPlane ? 1.0 : -1.0;
                double stubN = zone.WallPlane + (dirSign * 0.08 * FeetPerMeter);

                double fixtureOutZ = f.Role switch
                {
                    HydraulicFixtureRole.Sink => zone.FloorZ + (0.50 * FeetPerMeter),
                    HydraulicFixtureRole.LaundryTub => zone.FloorZ + (0.75 * FeetPerMeter),
                    HydraulicFixtureRole.WashingMachine => zone.FloorZ + (0.75 * FeetPerMeter),
                    _ => zone.FloorZ + (0.45 * FeetPerMeter)
                };

                double branchDn = f.Role == HydraulicFixtureRole.WashingMachine ? 50.0 : 40.0;
                XYZ pFixOut = MakePt(u, stubN, fixtureOutZ);
                XYZ pWallEntry = MakePt(u, zone.WallPlane, fixtureOutZ);
                XYZ pWallDrop = MakePt(u, zone.WallPlane, subFloorZ + 0.03 * FeetPerMeter);

                AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pFixOut, pWallEntry, branchDn, $"ESG-SEC | Saída {f.Name} | UHC={f.UhC}");
                AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pWallEntry, pWallDrop, branchDn, $"ESG-SEC | Descida {f.Name} | UHC={f.UhC}");

                double distU = Math.Abs(csU - u);
                double distN = Math.Abs(csN - zone.WallPlane);
                if (distU > 0.12 && distN > 0.12)
                {
                    double chamfer = Math.Min(distU, distN) * 0.75;
                    double midU = u + (Math.Sign(csU - u) * (distU - chamfer));
                    XYZ pMid = MakePt(midU, zone.WallPlane, subFloorZ + 0.015);
                    AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pWallDrop, pMid, branchDn, $"ESG-SEC | Ramal Reto {f.Name} (i=2%)");
                    AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pMid, pCaixaSifonada, branchDn, $"ESG-SEC | Entrada 45° Caixa Sifonada {f.Name}");
                }
                else
                {
                    AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pWallDrop, pCaixaSifonada, branchDn, $"ESG-SEC | Ramal Caixa Sifonada {f.Name} (i=2%)");
                }
            }

            // 3. Coletor Principal (DN 100, i = 1%) + Tubo de Queda Vertical (no vão livre LSF)
            double collectorStartU = toilet != null ? (zone.WallAlongX ? toilet.Center.X : toilet.Center.Y) : csU;
            double collectorN = toilet != null ? (zone.WallAlongX ? toilet.Center.Y : toilet.Center.X) : csN;
            double stackU = zone.DropU;

            XYZ pCollectorStart = MakePt(collectorStartU, collectorN, subFloorZ - 0.02 * FeetPerMeter);
            double horizRun = Math.Abs(stackU - collectorStartU);
            double collectorEndZ = subFloorZ - 0.02 * FeetPerMeter - Math.Max(0.015, 0.01 * horizRun);
            XYZ pStackTop = MakePt(stackU, zone.WallPlane, collectorEndZ);

            if (toilet != null)
            {
                XYZ pToiletFloor = MakePt(collectorStartU, collectorN, zone.FloorZ);
                AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pToiletFloor, pCollectorStart, 100.0, "ESG-PRI | Saída Vaso Sanitário | UHC=6");
            }

            AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pCollectorStart, pStackTop, collectorDnMm,
                $"ESG-PRI | Ramal Principal Parede {zone.Index} (i=1%) | UHC={zoneUhc}");

            // Saída DN 50 da Caixa Sifonada com junção a 45° no ramal principal
            if (secondary.Count > 0)
            {
                double joinU = collectorStartU + (Math.Sign(stackU - collectorStartU) * Math.Max(0.35 * FeetPerMeter, horizRun * 0.5));
                XYZ pJoinMain = MakePt(joinU, collectorN, (pCollectorStart.Z + collectorEndZ) * 0.5);
                AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pCaixaSifonada, pJoinMain, 50.0,
                    "ESG-SEC | Saída Caixa Sifonada DN50 -> Junção 45° (i=2%)");
            }

            // Tubo de Queda vertical descendo pelo vão livre LSF até o nível do subcoletor térreo
            double groundCollectorZ = globalFloorZ - (0.22 * FeetPerMeter);
            stackBottomPt = MakePt(stackU, zone.WallPlane, Math.Min(collectorEndZ, groundCollectorZ));
            if ((collectorEndZ - stackBottomPt.Z) > 0.20)
            {
                AddSanitaryPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pStackTop, stackBottomPt, collectorDnMm,
                    $"ESG-PRI | Tubo de Queda Vertical P{zone.Index} | UHC={zoneUhc}");
            }

            // 4. Ventilação Sanitária (NBR 8160): sobe no vão livre LSF até +0,40m acima do telhado
            if (ventSysTypeId != ElementId.InvalidElementId)
            {
                double ventU = SmartHydraulicRoutingEngine.ChooseFreeStudBayPublic(
                    zone.StudUs, csU - 0.50 * FeetPerMeter, csU + 0.50 * FeetPerMeter, csU);
                double highestRimZ = zone.FloorZ + (1.15 * FeetPerMeter); // >= 15 cm acima da borda do lavatório/tanque

                XYZ pVentOrigin = secondary.Count > 0 ? pCaixaSifonada : pCollectorStart;
                XYZ pVentWallBase = MakePt(ventU, zone.WallPlane, subFloorZ);
                XYZ pVentRim = MakePt(ventU, zone.WallPlane, highestRimZ);
                XYZ pVentRoof = MakePt(ventU, zone.WallPlane, roofZ + (0.40 * FeetPerMeter));

                AddSanitaryPipe(doc, pipes, ventSysTypeId, pipeTypeId, levelId, pVentOrigin, pVentWallBase, 50.0, "VENT | Ramal Ventilação Sub-piso (i=1%)");
                AddSanitaryPipe(doc, pipes, ventSysTypeId, pipeTypeId, levelId, pVentWallBase, pVentRim, 50.0, "VENT | Subida Ventilação Parede LSF");
                AddSanitaryPipe(doc, pipes, ventSysTypeId, pipeTypeId, levelId, pVentRim, pVentRoof, 50.0, "VENT | Coluna Ventilação Acima Cobertura");
            }

            zoneSummary = $"Parede {zone.Index} (Cota {zone.FloorZ / FeetPerMeter:F2}m): {zone.SewerFixtures.Count} aparelhos | UHC={zoneUhc} | Coletor DN{(int)collectorDnMm} (i=1%) + Secundários (i=2%) + Ventilação DN50";
            return pipes;
        }

        private static double SizePrimaryCollectorByUhc(int totalUhc)
        {
            // Tabela NBR 8160 para subcoletores / coletores prediais com declividade i = 1%
            if (totalUhc <= 20) return 100.0;
            if (totalUhc <= 160) return 150.0;
            return 200.0;
        }

        private static void CreateDedicatedSanitaryViews(Document doc, List<Pipe> sanitaryPipes, double floorZ)
        {
            if (sanitaryPipes.Count == 0) return;

            Color brown = new Color(139, 69, 19);    // Esgoto Primário / Subcoletor
            Color ochre = new Color(217, 119, 6);    // Esgoto Secundário / Gordura
            Color green = new Color(22, 163, 74);    // Ventilação

            ElementId solidFillId = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;

            void PaintSanitary(View v)
            {
                foreach (var p in sanitaryPipes)
                {
                    string cmt = p.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    Color c = brown;
                    if (cmt.Contains("VENT |")) c = green;
                    else if (cmt.Contains("ESG-SEC") || cmt.Contains("ESG-GORD")) c = ochre;

                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(c);
                    ogs.SetProjectionLineWeight(5);
                    if (solidFillId != ElementId.InvalidElementId)
                    {
                        ogs.SetSurfaceForegroundPatternId(solidFillId);
                        ogs.SetSurfaceForegroundPatternColor(c);
                    }
                    try { v.SetElementOverrides(p.Id, ogs); } catch { }
                }
            }

            if (doc.ActiveView != null && !doc.ActiveView.IsTemplate)
                PaintSanitary(doc.ActiveView);

            // 1. Planta Baixa de Esgoto e Ventilação (com faixa de vista estendida para baixo para mostrar sob o piso!)
            const string sanPlanName = "Smart Hidro - Planta de Esgoto e Ventilação";
            var sanPlan = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(sanPlanName, StringComparison.OrdinalIgnoreCase));

            if (sanPlan == null)
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
                        sanPlan = doc.GetElement(basePlan.Duplicate(ViewDuplicateOption.Duplicate)) as ViewPlan;
                        if (sanPlan != null)
                            sanPlan.Name = sanPlanName;
                    }
                    catch { }
                }
            }

            if (sanPlan != null)
            {
                try
                {
                    sanPlan.DetailLevel = ViewDetailLevel.Fine;
                    sanPlan.DisplayStyle = DisplayStyle.HLR;
                    var vr = sanPlan.GetViewRange();
                    vr.SetOffset(PlanViewPlane.BottomClipPlane, -0.60 * FeetPerMeter);
                    vr.SetOffset(PlanViewPlane.ViewDepthPlane, -0.80 * FeetPerMeter);
                    sanPlan.SetViewRange(vr);
                    PaintSanitary(sanPlan);
                }
                catch { }
            }

            // 2. Isométrico 3D exclusivo de Esgoto e Ventilação
            var vft3D = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

            if (vft3D != null)
            {
                const string isoName = "Smart Hidro - Isométrico Esgoto 3D";
                var isoView = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => v.Name.Equals(isoName, StringComparison.OrdinalIgnoreCase));

                if (isoView == null)
                {
                    try
                    {
                        isoView = View3D.CreateIsometric(doc, vft3D.Id);
                        isoView.Name = isoName;
                    }
                    catch { }
                }

                if (isoView != null)
                {
                    try
                    {
                        isoView.DetailLevel = ViewDetailLevel.Fine;
                        isoView.DisplayStyle = DisplayStyle.Shading;
                        foreach (var cat in new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Ceilings })
                        {
                            var c = Category.GetCategory(doc, cat);
                            if (c != null && isoView.CanCategoryBeHidden(c.Id))
                                isoView.SetCategoryHidden(c.Id, true);
                        }
                        PaintSanitary(isoView);
                    }
                    catch { }
                }
            }
        }

        private static void WriteSanitaryMemorial(Document doc, SanitaryExecutionReport report)
        {
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BahrHidro", "Logs");
                Directory.CreateDirectory(logDir);

                var sb = new StringBuilder();
                sb.AppendLine("======================================================================");
                sb.AppendLine($"SMART HIDRO BIM — MEMORIAL DE ESGOTO SANITÁRIO E VENTILAÇÃO (NBR 8160)");
                sb.AppendLine($"Projeto: {doc.Title} | Data: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
                sb.AppendLine("======================================================================");
                sb.AppendLine($"Zonas / Prumadas Sanitárias: {report.ZonesCount}");
                sb.AppendLine($"Segmentos de Tubulação Lançados: {report.PipesCreated}");
                sb.AppendLine($"Contribuição Total Acumulada: {report.TotalUhc} UHC");
                sb.AppendLine($"Diâmetro do Subcoletor Predial (i = 1%): DN {(int)report.MaxCollectorDnMm}");
                sb.AppendLine();
                sb.AppendLine("--- DETALHAMENTO POR ZONA MOLHADA ---");
                foreach (string d in report.ZoneDetails)
                    sb.AppendLine($" * {d}");

                File.WriteAllText(Path.Combine(logDir, "Memorial_Esgoto_NBR8160.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static void AddSanitaryPipe(
            Document doc, List<Pipe> list, ElementId sysTypeId, ElementId pipeTypeId, ElementId levelId,
            XYZ p1, XYZ p2, double diameterMm, string tagComment)
        {
            if (p1.DistanceTo(p2) < 0.02) return;
            var pipe = Pipe.Create(doc, sysTypeId, pipeTypeId, levelId, p1, p2);
            if (pipe == null) return;

            var diamParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (diamParam != null && !diamParam.IsReadOnly)
                diamParam.Set(diameterMm * MmToFeet);

            var cmtParam = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (cmtParam != null && !cmtParam.IsReadOnly)
                cmtParam.Set($"BahrHidro | {tagComment}");

            var markParam = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (markParam != null && !markParam.IsReadOnly)
                markParam.Set($"DN{(int)diameterMm}");

            list.Add(pipe);
        }

        private static ElementId ResolveSewerPipeTypeId(Document doc, ElementId? preferred)
        {
            if (preferred != null && preferred != ElementId.InvalidElementId && doc.GetElement(preferred) is PipeType)
                return preferred;

            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .ToList();

            var sewerType = types.FirstOrDefault(t =>
            {
                string n = t.Name.ToLowerInvariant();
                return n.Contains("esgoto") || n.Contains("sanit") || n.Contains("serie normal") || n.Contains("sn");
            });

            return (sewerType ?? types.FirstOrDefault())?.Id ?? ElementId.InvalidElementId;
        }

        private static ElementId ResolveNearestLevelId(Document doc, double targetZ)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - targetZ))
                .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
        }
    }
}
