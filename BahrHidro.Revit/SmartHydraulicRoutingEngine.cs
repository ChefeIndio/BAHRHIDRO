using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;

namespace BahrHidro.Revit
{
    public enum HydraulicFixtureRole
    {
        Unknown,
        WaterTank,
        HotWaterHeater,
        Shower,
        FloorDrain,
        Toilet,
        Sink,
        LaundryTub,
        WashingMachine,
        Accessory
    }

    public sealed class SemanticFixtureNode
    {
        public ElementId SourceId { get; set; } = ElementId.InvalidElementId;
        public string Name { get; set; } = string.Empty;
        public HydraulicFixtureRole Role { get; set; } = HydraulicFixtureRole.Unknown;
        public XYZ Center { get; set; } = XYZ.Zero;
        public double BaseZ { get; set; }
        public double FeedElevationOffsetMeters { get; set; } = 0.60;
        public double? IntermediateValveOffsetMeters { get; set; }
        public double ColdWaterDiameterMm { get; set; } = 20.0;
        public double RelativeWeight { get; set; } = 0.3;
        public double SewerDiameterMm { get; set; } = 40.0;
        public int UhC { get; set; } = 1;
        public bool NeedsColdWater { get; set; } = true;
        public bool NeedsHotWater { get; set; } = false;
        public bool NeedsSewer { get; set; } = true;
    }

    public sealed class WallOpeningObstacle
    {
        public double MinU { get; set; }
        public double MaxU { get; set; }
        public double BottomZ { get; set; }
        public double TopZ { get; set; }
    }

    public sealed class HydraulicWallZone
    {
        public int Index { get; set; }
        public bool WallAlongX { get; set; }
        public double WallPlane { get; set; }
        public double FloorZ { get; set; }
        public double DropU { get; set; }
        public bool HasDiagonalBracing { get; set; }
        public List<double> StudUs { get; set; } = new();
        public List<WallOpeningObstacle> Openings { get; set; } = new();
        public List<SemanticFixtureNode> ColdWaterFixtures { get; set; } = new();
        public List<SemanticFixtureNode> HotWaterFixtures { get; set; } = new();
        public List<SemanticFixtureNode> SewerFixtures { get; set; } = new();
    }

    public static class SmartHydraulicRoutingEngine
    {
        private const double FeetPerMeter = 3.280839895013123;
        private const double MmToFeet = 1.0 / 304.8;

        public static List<Pipe> GenerateOrthogonalLsfNetwork(Document doc, ElementId? preferredPipeTypeId = null)
        {
            var allCreated = new List<Pipe>();
            if (doc == null) return allCreated;

            var fixtures = DiscoverSemanticFixtures(doc);
            var activeFixtures = fixtures.Where(f => f.NeedsColdWater || f.NeedsHotWater || f.NeedsSewer).ToList();
            if (activeFixtures.Count == 0) return allCreated;

            var tank = fixtures.FirstOrDefault(f => f.Role == HydraulicFixtureRole.WaterTank);
            var heater = fixtures.FirstOrDefault(f => f.Role == HydraulicFixtureRole.HotWaterHeater);
            double globalFloorZ = activeFixtures.Min(f => f.BaseZ);
            double roofZ = DiscoverRoofElevation(doc, globalFloorZ, out BoundingBoxXYZ? roofBox);

            var zones = BuildHydraulicWallZones(doc, activeFixtures);
            if (zones.Count == 0) return allCreated;

            ElementId cwSysTypeId = PipeNetworkHelper.GetOrCreateColdWaterSystemTypeId(doc);
            ElementId hwSysTypeId = PipeNetworkHelper.GetOrCreateHotWaterSystemTypeId(doc);
            ElementId sanSysTypeId = PipeNetworkHelper.GetOrCreateSystemTypeId(doc, MEPSystemClassification.Sanitary, "Esgoto Sanitário - Bahr Hidro");
            ElementId ventSysTypeId = PipeNetworkHelper.GetOrCreateSystemTypeId(doc, MEPSystemClassification.Vent, "Ventilação Sanitária - Bahr Hidro");

            ElementId cwPipeTypeId = ResolvePipeTypeId(doc, preferredPipeTypeId, isHotWater: false);
            ElementId hwPipeTypeId = ResolvePipeTypeId(doc, null, isHotWater: true);
            if (hwPipeTypeId == ElementId.InvalidElementId) hwPipeTypeId = cwPipeTypeId;

            ElementId levelId = ResolveNearestLevelId(doc, globalFloorZ);
            if (cwPipeTypeId == ElementId.InvalidElementId || levelId == ElementId.InvalidElementId)
                return allCreated;

            string pipeTypeName = NormalizeText(doc.GetElement(cwPipeTypeId)?.Name ?? string.Empty);
            bool explicitPex = pipeTypeName.Contains("pex") || pipeTypeName.Contains("multicamada") || pipeTypeName.Contains("ponto a ponto");

            bool ownTransaction = !doc.IsModifiable;
            Transaction? tx = null;
            HydraulicVerificationSummary? cwPhysics = null;
            HydraulicVerificationSummary? hwPhysics = null;
            string rainSummary = "Sem cobertura detectada para águas pluviais.";
            var strategyLog = new List<string>();

            try
            {
                if (ownTransaction)
                {
                    tx = new Transaction(doc, "Bahr Hidro - Traçado Completo 3D (AF + AQ + Esgoto + Pluvial)");
                    tx.Start();
                }

                FamilyLoader.EnsureEssentialFittings(doc);
                PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, cwPipeTypeId);
                if (hwPipeTypeId != cwPipeTypeId)
                    PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, hwPipeTypeId);

                double sourceZ = tank != null
                    ? Math.Max(tank.BaseZ, globalFloorZ + 2.65 * FeetPerMeter)
                    : globalFloorZ + 2.80 * FeetPerMeter;

                // =========================================================
                // 1. REDE DE ÁGUA FRIA (NBR 5626)
                // =========================================================
                var cwPipes = new List<Pipe>();
                var cwZones = zones.Where(z => z.ColdWaterFixtures.Count > 0).ToList();

                if (cwZones.Count > 0 && cwSysTypeId != ElementId.InvalidElementId)
                {
                    double totalBuildingWeight = cwZones.SelectMany(z => z.ColdWaterFixtures).Sum(f => f.RelativeWeight);
                    double mainHeaderDn = totalBuildingWeight > 3.5 ? 32.0 : 25.0;

                    var firstZone = cwZones[0];
                    XYZ firstDropTop = firstZone.WallAlongX
                        ? new XYZ(firstZone.DropU, firstZone.WallPlane, sourceZ)
                        : new XYZ(firstZone.WallPlane, firstZone.DropU, sourceZ);

                    if (tank != null)
                    {
                        XYZ pTankOut = new XYZ(tank.Center.X, tank.Center.Y, sourceZ);
                        XYZ pTankElbow = new XYZ(firstDropTop.X, tank.Center.Y, sourceZ);
                        TryAddPipe(doc, cwPipes, cwSysTypeId, cwPipeTypeId, levelId, pTankOut, pTankElbow, 32.0, $"AF | Barrilete Mestre 1 | Peso={totalBuildingWeight:F1}");
                        TryAddPipe(doc, cwPipes, cwSysTypeId, cwPipeTypeId, levelId, pTankElbow, firstDropTop, 32.0, $"AF | Barrilete Mestre 2 | Peso={totalBuildingWeight:F1}");
                    }

                    XYZ prevHeaderPt = firstDropTop;
                    for (int zi = 0; zi < cwZones.Count; zi++)
                    {
                        var zone = cwZones[zi];
                        XYZ zoneTopDrop = zone.WallAlongX
                            ? new XYZ(zone.DropU, zone.WallPlane, sourceZ)
                            : new XYZ(zone.WallPlane, zone.DropU, sourceZ);

                        if (zi > 0)
                        {
                            XYZ cornerPt = new XYZ(zoneTopDrop.X, prevHeaderPt.Y, sourceZ);
                            TryAddPipe(doc, cwPipes, cwSysTypeId, cwPipeTypeId, levelId, prevHeaderPt, cornerPt, mainHeaderDn, $"AF | Distribuição Forro #{zi}.1 | Peso={totalBuildingWeight:F1}");
                            TryAddPipe(doc, cwPipes, cwSysTypeId, cwPipeTypeId, levelId, cornerPt, zoneTopDrop, mainHeaderDn, $"AF | Distribuição Forro #{zi}.2 | Peso={totalBuildingWeight:F1}");
                            prevHeaderPt = zoneTopDrop;
                        }

                        bool usePexManifold = explicitPex || zone.HasDiagonalBracing;
                        if (usePexManifold)
                        {
                            string reason = zone.HasDiagonalBracing ? "Contraventamento X LSF detectado" : "Perfil PEX/Manifold";
                            strategyLog.Add($"Parede {zone.Index}: PEX Ponto-a-Ponto c/ Manifold ({reason})");
                            RoutePexManifoldInWallZone(doc, cwPipes, cwSysTypeId, cwPipeTypeId, levelId, zone, sourceZ);
                        }
                        else
                        {
                            strategyLog.Add($"Parede {zone.Index}: Barrilete AF (Z=0,45m) + AQ (Z=0,55m) ({zone.Openings.Count} vãos desviados)");
                            RouteColdWaterInWallZone(doc, cwPipes, cwSysTypeId, cwPipeTypeId, levelId, zone, sourceZ);
                        }
                    }

                    var criticalCw = cwZones
                        .SelectMany(z => z.ColdWaterFixtures)
                        .OrderByDescending(f => f.BaseZ + f.FeedElevationOffsetMeters * FeetPerMeter)
                        .First();

                    double critZ = criticalCw.BaseZ + criticalCw.FeedElevationOffsetMeters * FeetPerMeter;
                    cwPhysics = HydraulicPhysicsVerifier.VerifyAndOptimizeColdWater(doc, cwPipes, sourceZ, critZ, criticalCw.Name, "Água Fria");

                    doc.Regenerate();
                    PipeFittingAutoConnector.ConnectPipesWithElbows(doc, cwPipes);
                    allCreated.AddRange(cwPipes);
                }

                // =========================================================
                // 2. REDE DE ÁGUA QUENTE (CPVC / PPR / PEX — NBR 7198)
                // =========================================================
                var hwPipes = new List<Pipe>();
                var hwZones = zones.Where(z => z.HotWaterFixtures.Count > 0).ToList();

                if (hwZones.Count > 0 && hwSysTypeId != ElementId.InvalidElementId)
                {
                    double totalHwWeight = hwZones.SelectMany(z => z.HotWaterFixtures).Sum(f => f.RelativeWeight);
                    double hwHeaderDn = totalHwWeight > 3.0 ? 28.0 : 22.0;
                    double hwSourceZ = heater != null
                        ? Math.Max(heater.BaseZ + 1.50 * FeetPerMeter, globalFloorZ + 2.55 * FeetPerMeter)
                        : sourceZ - (0.08 * FeetPerMeter);

                    var firstHwZone = hwZones[0];
                    double firstHwDropU = ChooseFreeStudBayU(
                        firstHwZone.StudUs,
                        firstHwZone.DropU + 0.30 * FeetPerMeter,
                        firstHwZone.DropU + 1.20 * FeetPerMeter,
                        firstHwZone.DropU + 0.40 * FeetPerMeter);

                    XYZ firstHwDropTop = firstHwZone.WallAlongX
                        ? new XYZ(firstHwDropU, firstHwZone.WallPlane, hwSourceZ)
                        : new XYZ(firstHwZone.WallPlane, firstHwDropU, hwSourceZ);

                    if (heater != null)
                    {
                        XYZ pHeaterOut = new XYZ(heater.Center.X, heater.Center.Y, hwSourceZ);
                        XYZ pHeaterElbow = new XYZ(firstHwDropTop.X, heater.Center.Y, hwSourceZ);
                        TryAddPipe(doc, hwPipes, hwSysTypeId, hwPipeTypeId, levelId, pHeaterOut, pHeaterElbow, 28.0, $"AQ | Saída Aquecedor 1 | Peso={totalHwWeight:F1}");
                        TryAddPipe(doc, hwPipes, hwSysTypeId, hwPipeTypeId, levelId, pHeaterElbow, firstHwDropTop, 28.0, $"AQ | Saída Aquecedor 2 | Peso={totalHwWeight:F1}");
                    }

                    XYZ prevHwHeader = firstHwDropTop;
                    for (int zi = 0; zi < hwZones.Count; zi++)
                    {
                        var zone = hwZones[zi];
                        double zoneHwDropU = zi == 0
                            ? firstHwDropU
                            : ChooseFreeStudBayU(zone.StudUs, zone.DropU + 0.30 * FeetPerMeter, zone.DropU + 1.20 * FeetPerMeter, zone.DropU + 0.40 * FeetPerMeter);

                        XYZ zoneHwTop = zone.WallAlongX
                            ? new XYZ(zoneHwDropU, zone.WallPlane, hwSourceZ)
                            : new XYZ(zone.WallPlane, zoneHwDropU, hwSourceZ);

                        if (zi > 0)
                        {
                            XYZ cornerPt = new XYZ(zoneHwTop.X, prevHwHeader.Y, hwSourceZ);
                            TryAddPipe(doc, hwPipes, hwSysTypeId, hwPipeTypeId, levelId, prevHwHeader, cornerPt, hwHeaderDn, $"AQ | Distribuição Forro #{zi}.1 | Peso={totalHwWeight:F1}");
                            TryAddPipe(doc, hwPipes, hwSysTypeId, hwPipeTypeId, levelId, cornerPt, zoneHwTop, hwHeaderDn, $"AQ | Distribuição Forro #{zi}.2 | Peso={totalHwWeight:F1}");
                            prevHwHeader = zoneHwTop;
                        }

                        RouteHotWaterInWallZone(doc, hwPipes, hwSysTypeId, hwPipeTypeId, levelId, zone, zoneHwDropU, hwSourceZ);
                    }

                    var criticalHw = hwZones
                        .SelectMany(z => z.HotWaterFixtures)
                        .OrderByDescending(f => f.BaseZ + f.FeedElevationOffsetMeters * FeetPerMeter)
                        .First();

                    double critHwZ = criticalHw.BaseZ + criticalHw.FeedElevationOffsetMeters * FeetPerMeter;
                    hwPhysics = HydraulicPhysicsVerifier.VerifyAndOptimizeColdWater(doc, hwPipes, hwSourceZ, critHwZ, criticalHw.Name, "Água Quente");

                    doc.Regenerate();
                    PipeFittingAutoConnector.ConnectPipesWithElbows(doc, hwPipes);
                    allCreated.AddRange(hwPipes);
                }

                // =========================================================
                // 3. REDE DE ESGOTO + VENTILAÇÃO SANITÁRIA POR ZONA (NBR 8160)
                // =========================================================
                foreach (var zone in zones.Where(z => z.SewerFixtures.Count > 0))
                {
                    var sewerAndVent = GenerateSanitaryAndVentForZone(
                        doc, sanSysTypeId, ventSysTypeId, cwPipeTypeId, levelId, zone, roofZ);
                    allCreated.AddRange(sewerAndVent);
                }

                // =========================================================
                // 4. ÁGUAS PLUVIAIS — CALHAS E CONDUTORES VERTICAIS (NBR 10844)
                // =========================================================
                if (roofBox != null && sanSysTypeId != ElementId.InvalidElementId)
                {
                    var rainPipes = GenerateStormwaterRainLeaders(
                        doc, sanSysTypeId, cwPipeTypeId, levelId, roofBox, globalFloorZ, out rainSummary);
                    allCreated.AddRange(rainPipes);
                }

                // =========================================================
                // 5. AUDITORIA LSF, QUANTITATIVOS E ELEVAÇÕES EXECUTIVAS
                // =========================================================
                int lsfCrossings = ExecutiveDetailingGenerator.AuditAndMarkLsfPenetrations(doc, allCreated, globalFloorZ);
                ExecutiveDetailingGenerator.CreateHydraulicSchedules(doc);

                var panelDescriptors = zones.Select(z =>
                {
                    var allZoneFixtures = z.ColdWaterFixtures.Concat(z.HotWaterFixtures).Concat(z.SewerFixtures).ToList();
                    double minU = allZoneFixtures.Count > 0
                        ? allZoneFixtures.Min(f => z.WallAlongX ? f.Center.X : f.Center.Y)
                        : z.DropU - 1.0 * FeetPerMeter;
                    double maxU = allZoneFixtures.Count > 0
                        ? allZoneFixtures.Max(f => z.WallAlongX ? f.Center.X : f.Center.Y)
                        : z.DropU + 1.0 * FeetPerMeter;

                    return new WallPanelDescriptor
                    {
                        Name = $"Painel LSF {z.Index}",
                        WallAlongX = z.WallAlongX,
                        WallPlane = z.WallPlane,
                        MinU = Math.Min(minU, z.DropU),
                        MaxU = Math.Max(maxU, z.DropU),
                        FloorZ = z.FloorZ,
                        TopZ = roofZ,
                        StudUs = z.StudUs
                    };
                }).ToList();

                LsfPanelElevationGenerator.GeneratePanelElevationsAndSheet(doc, panelDescriptors, allCreated);
                WriteExecutionAuditLog(doc, zones.Count, allCreated.Count, lsfCrossings, cwPhysics, hwPhysics, rainSummary, strategyLog);

                if (ownTransaction && tx != null)
                    tx.Commit();
            }
            catch
            {
                if (ownTransaction && tx != null && tx.HasStarted())
                    tx.RollBack();
                throw;
            }

            return allCreated;
        }

        private static void RouteHotWaterInWallZone(
            Document doc,
            List<Pipe> hwPipes,
            ElementId hwSysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            HydraulicWallZone zone,
            double hwDropU,
            double hwSourceZ)
        {
            XYZ MakePt(double u, double n, double z) => zone.WallAlongX ? new XYZ(u, n, z) : new XYZ(n, u, z);

            double zoneWeight = zone.HotWaterFixtures.Sum(f => f.RelativeWeight);
            double trunkDn = zoneWeight > 3.0 ? 28.0 : 22.0;
            // Cota Z = 0.55m (10 cm acima do barrilete de Água Fria Z = 0.45m para zero colisão na parede LSF!)
            double hwTrunkZ = zone.FloorZ + (0.55 * FeetPerMeter);

            XYZ pTopDrop = MakePt(hwDropU, zone.WallPlane, hwSourceZ);
            XYZ pBottomDrop = MakePt(hwDropU, zone.WallPlane, hwTrunkZ);
            TryAddPipe(doc, hwPipes, hwSysTypeId, pipeTypeId, levelId, pTopDrop, pBottomDrop, trunkDn, $"AQ | Prumada Parede {zone.Index} | Peso={zoneWeight:F1}");

            TryPlaceValveOnWall(doc, levelId, MakePt(hwDropU, zone.WallPlane, zone.FloorZ + 1.80 * FeetPerMeter), "gaveta");

            // Padrão misturador brasileiro (150mm entre eixos): Água Quente à ESQUERDA (-0.075m)
            var fixtureNodes = zone.HotWaterFixtures.Select(c =>
            {
                double baseU = zone.WallAlongX ? c.Center.X : c.Center.Y;
                double hotU = baseU - (0.075 * FeetPerMeter);
                return new { Fixture = c, U = hotU };
            }).OrderBy(x => x.U).ToList();

            var allTrunkUs = new List<double> { hwDropU };
            allTrunkUs.AddRange(fixtureNodes.Select(x => x.U));
            allTrunkUs = DeduplicateSorted(allTrunkUs, 0.03);

            for (int i = 0; i < allTrunkUs.Count - 1; i++)
            {
                RouteHorizontalSegmentAvoidingOpenings(
                    doc, hwPipes, hwSysTypeId, pipeTypeId, levelId,
                    zone, allTrunkUs[i], allTrunkUs[i + 1], hwTrunkZ, trunkDn,
                    $"AQ | Ramal Tronco P{zone.Index} #{i + 1} | Peso={zoneWeight:F1}");
            }

            foreach (var item in fixtureNodes)
            {
                var f = item.Fixture;
                double u = item.U;
                double fixN = zone.WallAlongX ? f.Center.Y : f.Center.X;
                double dirSign = fixN >= zone.WallPlane ? 1.0 : -1.0;
                double stubN = zone.WallPlane + (dirSign * 0.08 * FeetPerMeter);

                double targetZ = f.IntermediateValveOffsetMeters.HasValue
                    ? zone.FloorZ + (f.IntermediateValveOffsetMeters.Value * FeetPerMeter) // Misturador no registro do chuveiro (1.10m)
                    : zone.FloorZ + (f.FeedElevationOffsetMeters * FeetPerMeter);

                XYZ pBaseNode = MakePt(u, zone.WallPlane, hwTrunkZ);
                XYZ pEndWall = MakePt(u, zone.WallPlane, targetZ);
                XYZ pOut = MakePt(u, stubN, targetZ);

                TryAddPipe(doc, hwPipes, hwSysTypeId, pipeTypeId, levelId, pBaseNode, pEndWall, 22.0, $"AQ | Sub-ramal {f.Name} | Peso={f.RelativeWeight:F1}");
                if (f.IntermediateValveOffsetMeters.HasValue)
                    TryPlaceValveOnWall(doc, levelId, pEndWall, "pressao");
                TryAddPipe(doc, hwPipes, hwSysTypeId, pipeTypeId, levelId, pEndWall, pOut, 22.0, $"AQ | Ponto {f.Name} | Peso={f.RelativeWeight:F1}");
            }
        }

        private static List<Pipe> GenerateStormwaterRainLeaders(
            Document doc,
            ElementId sysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            BoundingBoxXYZ roofBox,
            double floorZ,
            out string summary)
        {
            var rainPipes = new List<Pipe>();
            double widthM = Math.Max(1.0, (roofBox.Max.X - roofBox.Min.X) / FeetPerMeter);
            double depthM = Math.Max(1.0, (roofBox.Max.Y - roofBox.Min.Y) / FeetPerMeter);
            double areaM2 = widthM * depthM;

            // NBR 10844: I = 150 mm/h (período de retorno 5 anos, t = 5 min) -> Q (L/min) = (I * A) / 60
            double flowLmin = (150.0 * areaM2) / 60.0;
            int leaderCount = areaM2 > 60.0 ? 4 : 2;
            double flowPerLeaderLmin = flowLmin / leaderCount;
            double leaderDnMm = flowPerLeaderLmin > 140.0 ? 100.0 : 75.0;

            double gutterZ = roofBox.Min.Z;
            double groundZ = floorZ + (0.05 * FeetPerMeter);

            XYZ c1 = new XYZ(roofBox.Min.X, roofBox.Min.Y, gutterZ);
            XYZ c2 = new XYZ(roofBox.Max.X, roofBox.Min.Y, gutterZ);
            XYZ c3 = new XYZ(roofBox.Max.X, roofBox.Max.Y, gutterZ);
            XYZ c4 = new XYZ(roofBox.Min.X, roofBox.Max.Y, gutterZ);

            // Calhas perimetrais ao longo dos beirais
            TryAddPipe(doc, rainPipes, sysTypeId, pipeTypeId, levelId, c1, c2, 100.0, $"AP | Calha Beiral Frontal | Area={areaM2:F1}m2");
            TryAddPipe(doc, rainPipes, sysTypeId, pipeTypeId, levelId, c4, c3, 100.0, $"AP | Calha Beiral Fundos | Area={areaM2:F1}m2");

            var corners = leaderCount >= 4 ? new[] { c1, c2, c3, c4 } : new[] { c1, c3 };
            for (int i = 0; i < corners.Length; i++)
            {
                XYZ topPt = corners[i];
                XYZ bottomPt = new XYZ(topPt.X, topPt.Y, groundZ);
                TryAddPipe(doc, rainPipes, sysTypeId, pipeTypeId, levelId, topPt, bottomPt, leaderDnMm, $"AP | Condutor Vertical #{i + 1} | Q={flowPerLeaderLmin:F1}L/min");
            }

            doc.Regenerate();
            PipeFittingAutoConnector.ConnectPipesWithElbows(doc, rainPipes);

            summary = $"Área Cobertura: {areaM2:F1} m² | Vazão Projeto (I=150mm/h): {flowLmin:F1} L/min | {leaderCount} Condutores DN{(int)leaderDnMm}";
            return rainPipes;
        }

        private static void RoutePexManifoldInWallZone(
            Document doc,
            List<Pipe> cwPipes,
            ElementId cwSysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            HydraulicWallZone zone,
            double sourceZ)
        {
            XYZ MakePt(double u, double n, double z) => zone.WallAlongX ? new XYZ(u, n, z) : new XYZ(n, u, z);

            double zoneWeight = zone.ColdWaterFixtures.Sum(f => f.RelativeWeight);
            double feedDn = zoneWeight > 3.5 ? 32.0 : 25.0;
            double manifoldZ = zone.FloorZ + (1.20 * FeetPerMeter);

            XYZ pTopDrop = MakePt(zone.DropU, zone.WallPlane, sourceZ);
            XYZ pManifold = MakePt(zone.DropU, zone.WallPlane, manifoldZ);
            TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pTopDrop, pManifold, feedDn, $"AF | Alimentação Manifold P{zone.Index} | Peso={zoneWeight:F1}");
            TryPlaceValveOnWall(doc, levelId, pManifold, "gaveta");

            int lineIdx = 0;
            foreach (var f in zone.ColdWaterFixtures)
            {
                double rawU = zone.WallAlongX ? f.Center.X : f.Center.Y;
                if (f.Role == HydraulicFixtureRole.Toilet)
                    rawU -= 0.12 * FeetPerMeter;
                else if (f.NeedsHotWater)
                    rawU += 0.075 * FeetPerMeter;

                double bayU = ChooseFreeStudBayU(zone.StudUs, rawU - 0.35 * FeetPerMeter, rawU + 0.35 * FeetPerMeter, rawU);
                double fixN = zone.WallAlongX ? f.Center.Y : f.Center.X;
                double dirSign = fixN >= zone.WallPlane ? 1.0 : -1.0;
                double stubN = zone.WallPlane + (dirSign * 0.08 * FeetPerMeter);

                double targetZ = zone.FloorZ + (f.FeedElevationOffsetMeters * FeetPerMeter);
                double plenumZ = sourceZ - ((0.08 + lineIdx * 0.03) * FeetPerMeter);
                double pexDn = 16.0;

                XYZ pManifoldStart = MakePt(zone.DropU, zone.WallPlane, manifoldZ + ((lineIdx + 1) * 0.04 * FeetPerMeter));
                XYZ pRiseCeiling = MakePt(zone.DropU, zone.WallPlane, plenumZ);
                XYZ pAcrossCeiling = MakePt(bayU, zone.WallPlane, plenumZ);
                XYZ pDropPoint = MakePt(bayU, zone.WallPlane, targetZ);
                XYZ pOut = MakePt(bayU, stubN, targetZ);

                TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pManifoldStart, pRiseCeiling, pexDn, $"AF-PEX | Subida Forro {f.Name} | Peso={f.RelativeWeight:F1}");
                TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pRiseCeiling, pAcrossCeiling, pexDn, $"AF-PEX | Forro Livre {f.Name} | Peso={f.RelativeWeight:F1}");
                TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pAcrossCeiling, pDropPoint, pexDn, $"AF-PEX | Descida Vão Livre {f.Name} | Peso={f.RelativeWeight:F1}");
                TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pDropPoint, pOut, pexDn, $"AF-PEX | Ponto {f.Name} | Peso={f.RelativeWeight:F1}");

                lineIdx++;
            }
        }

        private static void RouteColdWaterInWallZone(
            Document doc,
            List<Pipe> cwPipes,
            ElementId cwSysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            HydraulicWallZone zone,
            double sourceZ)
        {
            XYZ MakePt(double u, double n, double z) => zone.WallAlongX ? new XYZ(u, n, z) : new XYZ(n, u, z);

            double zoneWeight = zone.ColdWaterFixtures.Sum(f => f.RelativeWeight);
            double trunkDn = zoneWeight > 3.5 ? 32.0 : 25.0;
            double trunkZ = zone.FloorZ + (0.45 * FeetPerMeter);

            XYZ pTopDrop = MakePt(zone.DropU, zone.WallPlane, sourceZ);
            XYZ pBottomDrop = MakePt(zone.DropU, zone.WallPlane, trunkZ);
            TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pTopDrop, pBottomDrop, trunkDn, $"AF | Prumada Parede {zone.Index} | Peso={zoneWeight:F1}");

            TryPlaceValveOnWall(doc, levelId, MakePt(zone.DropU, zone.WallPlane, zone.FloorZ + 1.80 * FeetPerMeter), "gaveta");

            var fixtureNodes = zone.ColdWaterFixtures.Select(c =>
            {
                double u = zone.WallAlongX ? c.Center.X : c.Center.Y;
                if (c.Role == HydraulicFixtureRole.Toilet)
                    u -= 0.12 * FeetPerMeter;
                else if (c.NeedsHotWater)
                    u += 0.075 * FeetPerMeter; // Água Fria à DIREITA do misturador
                return new { Fixture = c, U = u };
            }).OrderBy(x => x.U).ToList();

            var allTrunkUs = new List<double> { zone.DropU };
            allTrunkUs.AddRange(fixtureNodes.Select(x => x.U));
            allTrunkUs = DeduplicateSorted(allTrunkUs, 0.03);

            for (int i = 0; i < allTrunkUs.Count - 1; i++)
            {
                RouteHorizontalSegmentAvoidingOpenings(
                    doc, cwPipes, cwSysTypeId, pipeTypeId, levelId,
                    zone, allTrunkUs[i], allTrunkUs[i + 1], trunkZ, trunkDn,
                    $"AF | Ramal Tronco P{zone.Index} #{i + 1} | Peso={zoneWeight:F1}");
            }

            foreach (var item in fixtureNodes)
            {
                var f = item.Fixture;
                double u = item.U;
                double fixN = zone.WallAlongX ? f.Center.Y : f.Center.X;
                double dirSign = fixN >= zone.WallPlane ? 1.0 : -1.0;
                double stubN = zone.WallPlane + (dirSign * 0.08 * FeetPerMeter);

                double targetZ = zone.FloorZ + (f.FeedElevationOffsetMeters * FeetPerMeter);
                XYZ pBaseNode = MakePt(u, zone.WallPlane, trunkZ);

                if (f.IntermediateValveOffsetMeters.HasValue)
                {
                    double valveZ = zone.FloorZ + (f.IntermediateValveOffsetMeters.Value * FeetPerMeter);
                    XYZ pValve = MakePt(u, zone.WallPlane, valveZ);
                    XYZ pTop = MakePt(u, zone.WallPlane, targetZ);
                    XYZ pOut = MakePt(u, stubN, targetZ);

                    TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pBaseNode, pValve, f.ColdWaterDiameterMm, $"AF | Sub-ramal {f.Name} | Peso={f.RelativeWeight:F1}");
                    TryPlaceValveOnWall(doc, levelId, pValve, "pressao");
                    TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pValve, pTop, f.ColdWaterDiameterMm, $"AF | Sub-ramal Ducha {f.Name} | Peso={f.RelativeWeight:F1}");
                    TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pTop, pOut, f.ColdWaterDiameterMm, $"AF | Ponto {f.Name} | Peso={f.RelativeWeight:F1}");
                }
                else
                {
                    XYZ pEndWall = MakePt(u, zone.WallPlane, targetZ);
                    XYZ pOut = MakePt(u, stubN, targetZ);

                    TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pBaseNode, pEndWall, f.ColdWaterDiameterMm, $"AF | Sub-ramal {f.Name} | Peso={f.RelativeWeight:F1}");
                    TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pEndWall, pOut, f.ColdWaterDiameterMm, $"AF | Ponto {f.Name} | Peso={f.RelativeWeight:F1}");
                }
            }
        }

        private static void RouteHorizontalSegmentAvoidingOpenings(
            Document doc,
            List<Pipe> cwPipes,
            ElementId sysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            HydraulicWallZone zone,
            double uStart,
            double uEnd,
            double trunkZ,
            double dnMm,
            string comment)
        {
            XYZ MakePt(double u, double z) => zone.WallAlongX
                ? new XYZ(u, zone.WallPlane, z)
                : new XYZ(zone.WallPlane, u, z);

            double segMin = Math.Min(uStart, uEnd);
            double segMax = Math.Max(uStart, uEnd);
            bool forward = uEnd >= uStart;

            var blocking = zone.Openings
                .Where(o => o.MaxU > segMin && o.MinU < segMax &&
                            trunkZ >= o.BottomZ - (0.05 * FeetPerMeter) &&
                            trunkZ <= o.TopZ + (0.05 * FeetPerMeter))
                .OrderBy(o => forward ? o.MinU : -o.MaxU)
                .ToList();

            if (blocking.Count == 0)
            {
                TryAddPipe(doc, cwPipes, sysTypeId, pipeTypeId, levelId, MakePt(uStart, trunkZ), MakePt(uEnd, trunkZ), dnMm, comment);
                return;
            }

            double curU = uStart;
            foreach (var obs in blocking)
            {
                double margin = 0.12 * FeetPerMeter;
                double detourZ = obs.TopZ + (0.15 * FeetPerMeter);

                double uEnter = forward ? Math.Max(curU, obs.MinU - margin) : Math.Min(curU, obs.MaxU + margin);
                double uExit = forward ? Math.Min(uEnd, obs.MaxU + margin) : Math.Max(uEnd, obs.MinU - margin);

                TryAddPipe(doc, cwPipes, sysTypeId, pipeTypeId, levelId, MakePt(curU, trunkZ), MakePt(uEnter, trunkZ), dnMm, comment);
                TryAddPipe(doc, cwPipes, sysTypeId, pipeTypeId, levelId, MakePt(uEnter, trunkZ), MakePt(uEnter, detourZ), dnMm, $"{comment} [Desvio Verga Subida]");
                TryAddPipe(doc, cwPipes, sysTypeId, pipeTypeId, levelId, MakePt(uEnter, detourZ), MakePt(uExit, detourZ), dnMm, $"{comment} [Desvio Verga Topo]");
                TryAddPipe(doc, cwPipes, sysTypeId, pipeTypeId, levelId, MakePt(uExit, detourZ), MakePt(uExit, trunkZ), dnMm, $"{comment} [Desvio Verga Descida]");

                curU = uExit;
            }

            TryAddPipe(doc, cwPipes, sysTypeId, pipeTypeId, levelId, MakePt(curU, trunkZ), MakePt(uEnd, trunkZ), dnMm, comment);
        }

        private static List<Pipe> GenerateSanitaryAndVentForZone(
            Document doc,
            ElementId sanSysTypeId,
            ElementId ventSysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            HydraulicWallZone zone,
            double roofZ)
        {
            var pipes = new List<Pipe>();
            if (sanSysTypeId == ElementId.InvalidElementId || zone.SewerFixtures.Count == 0)
                return pipes;

            XYZ MakePt(double u, double n, double z) => zone.WallAlongX ? new XYZ(u, n, z) : new XYZ(n, u, z);

            double subFloorZ = zone.FloorZ - (0.14 * FeetPerMeter);
            var toilet = zone.SewerFixtures.FirstOrDefault(f => f.Role == HydraulicFixtureRole.Toilet);
            var secondary = zone.SewerFixtures.Where(f => f.Role != HydraulicFixtureRole.Toilet).ToList();

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

                XYZ pFixOut = MakePt(u, stubN, fixtureOutZ);
                XYZ pWallEntry = MakePt(u, zone.WallPlane, fixtureOutZ);
                XYZ pWallDrop = MakePt(u, zone.WallPlane, subFloorZ + 0.03 * FeetPerMeter);

                TryAddPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pFixOut, pWallEntry, f.SewerDiameterMm, $"ESG | Saída {f.Name} | UHC={f.UhC}");
                TryAddPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pWallEntry, pWallDrop, f.SewerDiameterMm, $"ESG | Descida {f.Name} | UHC={f.UhC}");

                double horizDist = Math.Sqrt(Math.Pow(csU - u, 2) + Math.Pow(csN - zone.WallPlane, 2));
                double dropDelta = Math.Max(0.02 * horizDist, 0.015);
                XYZ pStartSlope = MakePt(u, zone.WallPlane, subFloorZ + dropDelta);
                TryAddPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pStartSlope, pCaixaSifonada, f.SewerDiameterMm, $"ESG | Ramal i=2% {f.Name} | UHC={f.UhC}");
            }

            double collectorStartU = toilet != null ? (zone.WallAlongX ? toilet.Center.X : toilet.Center.Y) : csU;
            double collectorN = toilet != null ? (zone.WallAlongX ? toilet.Center.Y : toilet.Center.X) : csN;
            double endStackU = Math.Abs(zone.DropU - collectorStartU) > 0.30 ? zone.DropU : collectorStartU + (0.80 * FeetPerMeter);

            XYZ pCollectorStart = MakePt(collectorStartU, collectorN, subFloorZ - 0.02 * FeetPerMeter);
            double mainDist = Math.Abs(endStackU - collectorStartU);
            XYZ pCollectorEnd = MakePt(endStackU, collectorN, subFloorZ - 0.02 * FeetPerMeter - (0.01 * mainDist));

            int totalUhc = zone.SewerFixtures.Sum(s => s.UhC);
            if (toilet != null)
            {
                XYZ pToiletFloor = MakePt(collectorStartU, collectorN, zone.FloorZ);
                TryAddPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pToiletFloor, pCollectorStart, 100.0, "ESG | Saída Vaso Sanitário | UHC=6");
            }

            TryAddPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pCollectorStart, pCollectorEnd, 100.0, $"ESG | Coletor Principal i=1% | UHC={totalUhc}");

            if (secondary.Count > 0)
            {
                double midU = (collectorStartU + endStackU) * 0.5;
                XYZ pJoinMain = MakePt(midU, collectorN, subFloorZ - 0.02 * FeetPerMeter - (0.005 * mainDist));
                TryAddPipe(doc, pipes, sanSysTypeId, pipeTypeId, levelId, pCaixaSifonada, pJoinMain, 50.0, "ESG | Saída Caixa Sifonada i=2%");

                if (ventSysTypeId != ElementId.InvalidElementId)
                {
                    double ventU = ChooseFreeStudBayU(zone.StudUs, csU - 0.40 * FeetPerMeter, csU + 0.40 * FeetPerMeter, csU);
                    XYZ pVentBase = MakePt(ventU, zone.WallPlane, subFloorZ);
                    XYZ pVentRoof = MakePt(ventU, zone.WallPlane, roofZ + (0.40 * FeetPerMeter));

                    TryAddPipe(doc, pipes, ventSysTypeId, pipeTypeId, levelId, pCaixaSifonada, pVentBase, 50.0, "VENT | Ramal Ventilação NBR 8160");
                    TryAddPipe(doc, pipes, ventSysTypeId, pipeTypeId, levelId, pVentBase, pVentRoof, 50.0, "VENT | Coluna Ventilação Cobertura");
                }
            }

            doc.Regenerate();
            PipeFittingAutoConnector.ConnectPipesWithElbows(doc, pipes);
            return pipes;
        }

        private static List<HydraulicWallZone> BuildHydraulicWallZones(Document doc, List<SemanticFixtureNode> fixtures)
        {
            var candidatePlanes = DiscoverAllWallPlanes(doc, out var diagonalBraces);
            var allOpenings = DiscoverAllWallOpenings(doc);
            var zones = new List<HydraulicWallZone>();

            foreach (var fix in fixtures)
            {
                var bestPlane = candidatePlanes
                    .Select(p => new
                    {
                        Plane = p,
                        Dist = p.WallAlongX ? Math.Abs(fix.Center.Y - p.WallPlane) : Math.Abs(fix.Center.X - p.WallPlane),
                        WithinSpan = (p.WallAlongX ? fix.Center.X : fix.Center.Y) >= p.MinU - (0.60 * FeetPerMeter) &&
                                     (p.WallAlongX ? fix.Center.X : fix.Center.Y) <= p.MaxU + (0.60 * FeetPerMeter)
                    })
                    .Where(x => x.Dist <= 1.20 * FeetPerMeter && x.WithinSpan)
                    .OrderBy(x => x.Dist)
                    .FirstOrDefault()?.Plane;

                bool wallAlongX = bestPlane?.WallAlongX ?? true;
                double wallPlane = bestPlane?.WallPlane ?? (fix.Center.Y - 0.12 * FeetPerMeter);

                var existingZone = zones.FirstOrDefault(z =>
                    z.WallAlongX == wallAlongX &&
                    Math.Abs(z.WallPlane - wallPlane) <= 0.35 * FeetPerMeter &&
                    Math.Abs(z.FloorZ - fix.BaseZ) <= 1.50 * FeetPerMeter);

                if (existingZone == null)
                {
                    existingZone = new HydraulicWallZone
                    {
                        Index = zones.Count + 1,
                        WallAlongX = wallAlongX,
                        WallPlane = wallPlane,
                        FloorZ = fix.BaseZ,
                        StudUs = bestPlane?.StudUs ?? new List<double>()
                    };
                    zones.Add(existingZone);
                }
                else
                {
                    existingZone.FloorZ = Math.Min(existingZone.FloorZ, fix.BaseZ);
                    if (existingZone.StudUs.Count == 0 && bestPlane != null)
                        existingZone.StudUs = bestPlane.StudUs;
                }

                if (fix.NeedsColdWater) existingZone.ColdWaterFixtures.Add(fix);
                if (fix.NeedsHotWater) existingZone.HotWaterFixtures.Add(fix);
                if (fix.NeedsSewer) existingZone.SewerFixtures.Add(fix);
            }

            foreach (var z in zones)
            {
                var zFixtures = z.ColdWaterFixtures.Concat(z.HotWaterFixtures).Concat(z.SewerFixtures).ToList();
                double minU = zFixtures.Min(f => z.WallAlongX ? f.Center.X : f.Center.Y);
                double maxU = zFixtures.Max(f => z.WallAlongX ? f.Center.X : f.Center.Y);
                z.DropU = ChooseFreeStudBayU(z.StudUs, minU, maxU, minU);

                foreach (var op in allOpenings)
                {
                    double opPlane = z.WallAlongX ? (op.Min.Y + op.Max.Y) * 0.5 : (op.Min.X + op.Max.X) * 0.5;
                    if (Math.Abs(opPlane - z.WallPlane) <= 0.35 * FeetPerMeter)
                    {
                        z.Openings.Add(new WallOpeningObstacle
                        {
                            MinU = z.WallAlongX ? op.Min.X : op.Min.Y,
                            MaxU = z.WallAlongX ? op.Max.X : op.Max.Y,
                            BottomZ = op.Min.Z,
                            TopZ = op.Max.Z
                        });
                    }
                }

                z.HasDiagonalBracing = diagonalBraces.Any(b =>
                    b.AlongX == z.WallAlongX &&
                    Math.Abs(b.Plane - z.WallPlane) <= 0.20 * FeetPerMeter &&
                    b.MaxU >= minU && b.MinU <= maxU);
            }

            return zones;
        }

        private static List<WallPanelDescriptor> DiscoverAllWallPlanes(
            Document doc,
            out List<(bool AlongX, double Plane, double MinU, double MaxU)> diagonalBraces)
        {
            var result = new List<WallPanelDescriptor>();
            var studs = new List<(double U, double N, bool AlongX)>();
            var braces = new List<(bool AlongX, double Plane, double MinU, double MaxU)>();

            var cats = new[]
            {
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_GenericModel
            };

            void ScanDoc(Document targetDoc, Transform tr)
            {
                foreach (var cat in cats)
                {
                    foreach (var el in new FilteredElementCollector(targetDoc).WhereElementIsNotElementType().OfCategory(cat))
                    {
                        if (el.Location is LocationCurve elLc && elLc.Curve is Line elLine)
                        {
                            XYZ a = tr.OfPoint(elLine.GetEndPoint(0));
                            XYZ b = tr.OfPoint(elLine.GetEndPoint(1));
                            double horiz = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
                            double vert = Math.Abs(b.Z - a.Z);
                            if (horiz > 0.35 * FeetPerMeter && vert > 0.35 * FeetPerMeter)
                            {
                                bool bAlongX = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
                                braces.Add((
                                    bAlongX,
                                    bAlongX ? (a.Y + b.Y) * 0.5 : (a.X + b.X) * 0.5,
                                    bAlongX ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y),
                                    bAlongX ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y)
                                ));
                                continue;
                            }
                        }

                        var bb = el.get_BoundingBox(null);
                        if (bb == null) continue;
                        XYZ p1 = tr.OfPoint(bb.Min);
                        XYZ p2 = tr.OfPoint(bb.Max);
                        double dx = Math.Abs(p2.X - p1.X);
                        double dy = Math.Abs(p2.Y - p1.Y);
                        double dz = Math.Abs(p2.Z - p1.Z);

                        if (dz < 1.5 * FeetPerMeter || dx > 0.30 * FeetPerMeter || dy > 0.30 * FeetPerMeter)
                            continue;

                        XYZ c = (p1 + p2) * 0.5;
                        studs.Add((c.X, c.Y, true));
                        studs.Add((c.Y, c.X, false));
                    }
                }

                foreach (var w in new FilteredElementCollector(targetDoc).WhereElementIsNotElementType().OfCategory(BuiltInCategory.OST_Walls).Cast<Wall>())
                {
                    if (w.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                    XYZ p0 = tr.OfPoint(line.GetEndPoint(0));
                    XYZ p1 = tr.OfPoint(line.GetEndPoint(1));
                    bool alongX = Math.Abs(p1.X - p0.X) >= Math.Abs(p1.Y - p0.Y);
                    result.Add(new WallPanelDescriptor
                    {
                        WallAlongX = alongX,
                        WallPlane = alongX ? (p0.Y + p1.Y) * 0.5 : (p0.X + p1.X) * 0.5,
                        MinU = alongX ? Math.Min(p0.X, p1.X) : Math.Min(p0.Y, p1.Y),
                        MaxU = alongX ? Math.Max(p0.X, p1.X) : Math.Max(p0.Y, p1.Y)
                    });
                }
            }

            ScanDoc(doc, Transform.Identity);
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ldoc = link.GetLinkDocument();
                if (ldoc != null)
                    ScanDoc(ldoc, link.GetTotalTransform());
            }

            foreach (bool alongX in new[] { true, false })
            {
                var dirStuds = studs.Where(s => s.AlongX == alongX).OrderBy(s => s.N).ToList();
                var groups = new List<List<(double U, double N)>>();

                foreach (var st in dirStuds)
                {
                    var g = groups.FirstOrDefault(gr => Math.Abs(gr[0].N - st.N) <= 0.12 * FeetPerMeter);
                    if (g == null)
                        groups.Add(new List<(double U, double N)> { (st.U, st.N) });
                    else
                        g.Add((st.U, st.N));
                }

                foreach (var gr in groups.Where(g => g.Count >= 2))
                {
                    var us = DeduplicateSorted(gr.Select(x => x.U).OrderBy(u => u).ToList(), 0.15);
                    if (us.Count >= 2)
                    {
                        result.Add(new WallPanelDescriptor
                        {
                            WallAlongX = alongX,
                            WallPlane = gr.Average(x => x.N),
                            MinU = us.First(),
                            MaxU = us.Last(),
                            StudUs = us
                        });
                    }
                }
            }

            diagonalBraces = braces;
            return result;
        }

        private static List<BoundingBoxXYZ> DiscoverAllWallOpenings(Document doc)
        {
            var boxes = new List<BoundingBoxXYZ>();
            var cats = new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows };

            void ScanOpenings(Document targetDoc, Transform tr)
            {
                foreach (var cat in cats)
                {
                    foreach (var el in new FilteredElementCollector(targetDoc).WhereElementIsNotElementType().OfCategory(cat))
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb == null) continue;
                        XYZ p1 = tr.OfPoint(bb.Min);
                        XYZ p2 = tr.OfPoint(bb.Max);
                        boxes.Add(new BoundingBoxXYZ
                        {
                            Min = new XYZ(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Min(p1.Z, p2.Z)),
                            Max = new XYZ(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), Math.Max(p1.Z, p2.Z))
                        });
                    }
                }
            }

            ScanOpenings(doc, Transform.Identity);
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ldoc = link.GetLinkDocument();
                if (ldoc != null)
                    ScanOpenings(ldoc, link.GetTotalTransform());
            }

            return boxes;
        }

        private static double DiscoverRoofElevation(Document doc, double floorZ, out BoundingBoxXYZ? combinedRoofBox)
        {
            combinedRoofBox = null;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool found = false;

            void ScanRoofs(Document targetDoc, Transform tr)
            {
                foreach (var r in new FilteredElementCollector(targetDoc).WhereElementIsNotElementType().OfCategory(BuiltInCategory.OST_Roofs))
                {
                    var bb = r.get_BoundingBox(null);
                    if (bb == null) continue;
                    XYZ p1 = tr.OfPoint(bb.Min);
                    XYZ p2 = tr.OfPoint(bb.Max);
                    if (Math.Max(p1.Z, p2.Z) <= floorZ + 2.0 * FeetPerMeter) continue;

                    minX = Math.Min(minX, Math.Min(p1.X, p2.X));
                    minY = Math.Min(minY, Math.Min(p1.Y, p2.Y));
                    minZ = Math.Min(minZ, Math.Min(p1.Z, p2.Z));
                    maxX = Math.Max(maxX, Math.Max(p1.X, p2.X));
                    maxY = Math.Max(maxY, Math.Max(p1.Y, p2.Y));
                    maxZ = Math.Max(maxZ, Math.Max(p1.Z, p2.Z));
                    found = true;
                }
            }

            ScanRoofs(doc, Transform.Identity);
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ldoc = link.GetLinkDocument();
                if (ldoc != null)
                    ScanRoofs(ldoc, link.GetTotalTransform());
            }

            if (found)
            {
                combinedRoofBox = new BoundingBoxXYZ
                {
                    Min = new XYZ(minX, minY, minZ),
                    Max = new XYZ(maxX, maxY, maxZ)
                };
                return maxZ;
            }

            return floorZ + (3.00 * FeetPerMeter);
        }

        private static void WriteExecutionAuditLog(
            Document doc,
            int zonesCount,
            int pipesCount,
            int lsfCrossings,
            HydraulicVerificationSummary? cwPhysics,
            HydraulicVerificationSummary? hwPhysics,
            string rainSummary,
            List<string> strategies)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BahrHidro",
                    "Logs");
                Directory.CreateDirectory(logDir);

                var sb = new StringBuilder();
                sb.AppendLine("======================================================================");
                sb.AppendLine($"BAHR HIDRO BIM — MEMORIAL AUTOMÁTICO DE TRAÇADO 3D ({DateTime.Now:dd/MM/yyyy HH:mm:ss})");
                sb.AppendLine($"Projeto: {doc.Title}");
                sb.AppendLine("======================================================================");
                sb.AppendLine($"Paredes Hidráulicas Detectadas: {zonesCount}");
                sb.AppendLine($"Segmentos de Tubulação Gerados: {pipesCount}");
                sb.AppendLine($"Passagens em Montantes LSF Auditadas: {lsfCrossings}");
                sb.AppendLine();
                sb.AppendLine("--- ESTRATÉGIA POR PAINEL ---");
                foreach (string s in strategies)
                    sb.AppendLine($" * {s}");

                foreach (var phys in new[] { cwPhysics, hwPhysics }.Where(p => p != null))
                {
                    sb.AppendLine();
                    sb.AppendLine($"--- VERIFICAÇÃO HIDRÁULICA: {phys!.SystemLabel.ToUpperInvariant()} ---");
                    sb.AppendLine($" * Soma de Pesos Relativos: {phys.TotalWeight:F2}");
                    sb.AppendLine($" * Vazão de Projeto: {phys.DesignFlowLs:F2} L/s");
                    sb.AppendLine($" * Velocidade Máxima: {phys.MaxVelocityMs:F2} m/s (Limite <= 3,00 m/s)");
                    sb.AppendLine($" * Pressão Dinâmica Crítica ({phys.CriticalFixtureName}): {phys.CriticalDynamicPressureMca:F2} m.c.a. ({phys.CriticalDynamicPressureMca * 10.0:F1} kPa)");
                    sb.AppendLine($" * Trechos Re-dimensionados: {phys.UpsizedPipesCount}");
                    sb.AppendLine($" * Status Normativo: {(phys.IsNormativeCompliant ? "CONFORME" : "VERIFICAR ALTURA DO RESERVATÓRIO")}");
                }

                sb.AppendLine();
                sb.AppendLine("--- ÁGUAS PLUVIAIS (NBR 10844) ---");
                sb.AppendLine($" * {rainSummary}");

                File.WriteAllText(Path.Combine(logDir, "Ultimo_Memorial_3D.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static void TryPlaceValveOnWall(Document doc, ElementId levelId, XYZ location, string kindHint)
        {
            try
            {
                var valveSymbol = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeAccessory)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s =>
                    {
                        string n = NormalizeText($"{s.FamilyName} {s.Name}");
                        return n.Contains(kindHint) || n.Contains("registro") || n.Contains("valve");
                    });

                if (valveSymbol == null) return;
                if (!valveSymbol.IsActive)
                {
                    valveSymbol.Activate();
                    doc.Regenerate();
                }

                if (doc.GetElement(levelId) is Level lvl)
                    doc.Create.NewFamilyInstance(location, valveSymbol, lvl, StructuralType.NonStructural);
            }
            catch { }
        }

        private static void TryAddPipe(
            Document doc,
            List<Pipe> list,
            ElementId sysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            XYZ p1,
            XYZ p2,
            double diameterMm,
            string tagComment)
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

        public static List<SemanticFixtureNode> DiscoverSemanticFixtures(Document doc)
        {
            var nodes = new List<SemanticFixtureNode>();

            void ScanDocument(Document targetDoc, Transform tr)
            {
                var elems = new FilteredElementCollector(targetDoc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                    .ToList();

                foreach (var el in elems)
                {
                    BoundingBoxXYZ? bb = el.get_BoundingBox(null);
                    if (bb == null) continue;

                    XYZ minPt = tr.OfPoint(bb.Min);
                    XYZ maxPt = tr.OfPoint(bb.Max);
                    XYZ center = (minPt + maxPt) * 0.5;
                    double baseZ = Math.Min(minPt.Z, maxPt.Z);

                    string fingerprint = BuildElementFingerprint(targetDoc, el);
                    var role = ClassifyFixture(fingerprint);
                    if (role == HydraulicFixtureRole.Unknown || role == HydraulicFixtureRole.Accessory)
                        continue;

                    var node = new SemanticFixtureNode
                    {
                        SourceId = el.Id,
                        Name = el.Name,
                        Role = role,
                        Center = center,
                        BaseZ = baseZ
                    };

                    ApplyRoleDefaults(node);
                    nodes.Add(node);
                }
            }

            ScanDocument(doc, Transform.Identity);

            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc != null)
                    ScanDocument(linkDoc, link.GetTotalTransform());
            }

            return nodes;
        }

        private static string BuildElementFingerprint(Document doc, Element el)
        {
            var sb = new StringBuilder();
            sb.Append(el.Name).Append(' ');

            Element? typeEl = doc.GetElement(el.GetTypeId());
            if (typeEl != null)
            {
                sb.Append(typeEl.Name).Append(' ');
                if (typeEl is FamilySymbol fs)
                    sb.Append(fs.FamilyName).Append(' ');
            }

            foreach (BuiltInParameter bip in new[]
            {
                BuiltInParameter.ALL_MODEL_DESCRIPTION,
                BuiltInParameter.ALL_MODEL_MARK,
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS
            })
            {
                var p = el.get_Parameter(bip) ?? typeEl?.get_Parameter(bip);
                if (p != null && p.HasValue)
                    sb.Append(p.AsString()).Append(' ');
            }

            return NormalizeText(sb.ToString());
        }

        private static HydraulicFixtureRole ClassifyFixture(string text)
        {
            if (text.Contains("registro de pressao") || text.Contains("registro de gaveta") || text.Contains("torneira"))
                return HydraulicFixtureRole.Accessory;

            if (text.Contains("ralo") || text.Contains("grelha") || text.Contains("caixa sifonada") || text.Contains("sifao"))
                return HydraulicFixtureRole.FloorDrain;

            if (text.Contains("aquecedor") || text.Contains("boiler") || text.Contains("heater") || text.Contains("solar"))
                return HydraulicFixtureRole.HotWaterHeater;

            if (text.Contains("reservatorio") || text.Contains("caixa d'agua") || text.Contains("caixa dagua") ||
                text.Contains("cisterna") || text.Contains("tank"))
                return HydraulicFixtureRole.WaterTank;

            if (text.Contains("chuveiro") || text.Contains("ducha") || text.Contains("shower"))
                return HydraulicFixtureRole.Shower;

            if (text.Contains("vaso") || text.Contains("bacia") || text.Contains("sanitari") || text.Contains("toilet") || text.Contains("wc"))
                return HydraulicFixtureRole.Toilet;

            if (text.Contains("maquina de lavar") || text.Contains("lavadora") || text.Contains("washing"))
                return HydraulicFixtureRole.WashingMachine;

            if (text.Contains("tanque") || text.Contains("laundry"))
                return HydraulicFixtureRole.LaundryTub;

            if (text.Contains("cuba") || text.Contains("lavatorio") || text.Contains("pia") || text.Contains("bancada") || text.Contains("sink") || text.Contains("basin"))
                return HydraulicFixtureRole.Sink;

            return HydraulicFixtureRole.Unknown;
        }

        private static void ApplyRoleDefaults(SemanticFixtureNode node)
        {
            switch (node.Role)
            {
                case HydraulicFixtureRole.WaterTank:
                case HydraulicFixtureRole.HotWaterHeater:
                    node.ColdWaterDiameterMm = 32.0;
                    node.RelativeWeight = 0.0;
                    node.NeedsColdWater = false;
                    node.NeedsHotWater = false;
                    node.NeedsSewer = false;
                    break;
                case HydraulicFixtureRole.FloorDrain:
                    node.NeedsColdWater = false;
                    node.NeedsHotWater = false;
                    node.NeedsSewer = true;
                    node.SewerDiameterMm = 40.0;
                    node.UhC = 2;
                    break;
                case HydraulicFixtureRole.Shower:
                    node.FeedElevationOffsetMeters = 2.10;
                    node.IntermediateValveOffsetMeters = 1.10;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 0.4;
                    node.NeedsHotWater = true;
                    node.SewerDiameterMm = 40.0;
                    node.UhC = 2;
                    break;
                case HydraulicFixtureRole.Toilet:
                    node.FeedElevationOffsetMeters = 0.20;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 0.3;
                    node.NeedsHotWater = false;
                    node.SewerDiameterMm = 100.0;
                    node.UhC = 6;
                    break;
                case HydraulicFixtureRole.Sink:
                    node.FeedElevationOffsetMeters = 0.60;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 0.3;
                    node.NeedsHotWater = true;
                    node.SewerDiameterMm = 40.0;
                    node.UhC = 1;
                    break;
                case HydraulicFixtureRole.LaundryTub:
                    node.FeedElevationOffsetMeters = 1.00;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 0.7;
                    node.NeedsHotWater = false;
                    node.SewerDiameterMm = 40.0;
                    node.UhC = 3;
                    break;
                case HydraulicFixtureRole.WashingMachine:
                    node.FeedElevationOffsetMeters = 0.90;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 1.0;
                    node.NeedsHotWater = false;
                    node.SewerDiameterMm = 50.0;
                    node.UhC = 3;
                    break;
            }
        }

        private static double ChooseFreeStudBayU(List<double> studUs, double minU, double maxU, double? preferredU)
        {
            if (studUs.Count < 2)
                return preferredU ?? minU;

            var bays = new List<double>();
            for (int i = 0; i < studUs.Count - 1; i++)
            {
                double gap = studUs[i + 1] - studUs[i];
                if (gap >= 0.25 * FeetPerMeter && gap <= 1.20 * FeetPerMeter)
                    bays.Add((studUs[i] + studUs[i + 1]) * 0.5);
            }

            if (bays.Count == 0)
                return preferredU ?? minU;

            double target = preferredU ?? minU;
            return bays.OrderBy(b => Math.Abs(b - target)).First();
        }

        private static List<double> DeduplicateSorted(List<double> sorted, double tol)
        {
            var res = new List<double>();
            foreach (double v in sorted)
            {
                if (res.Count == 0 || Math.Abs(v - res[res.Count - 1]) > tol)
                    res.Add(v);
            }
            return res;
        }

        private static ElementId ResolvePipeTypeId(Document doc, ElementId? preferred, bool isHotWater)
        {
            if (preferred != null && preferred != ElementId.InvalidElementId && doc.GetElement(preferred) is PipeType)
                return preferred;

            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .ToList();

            if (isHotWater)
            {
                var hw = types.FirstOrDefault(t =>
                {
                    string n = NormalizeText(t.Name);
                    return n.Contains("cpvc") || n.Contains("ppr") || n.Contains("quente") || n.Contains("aquatherm");
                });
                if (hw != null) return hw.Id;
            }

            var pvc = types.FirstOrDefault(t =>
            {
                string n = NormalizeText(t.Name);
                return n.Contains("pvc") || n.Contains("soldavel") || n.Contains("agua fria");
            });

            return (pvc ?? types.FirstOrDefault())?.Id ?? ElementId.InvalidElementId;
        }

        private static ElementId ResolveNearestLevelId(Document doc, double targetZ)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - targetZ))
                .ToList();

            return levels.FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
        }

        private static string NormalizeText(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            string formD = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (char ch in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
