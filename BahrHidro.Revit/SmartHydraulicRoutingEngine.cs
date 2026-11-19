using System;
using System.Collections.Generic;
using System.Globalization;
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
        public bool NeedsSewer { get; set; } = true;
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
            var cwConsumers = fixtures.Where(f => f.NeedsColdWater).ToList();
            var sewerConsumers = fixtures.Where(f => f.NeedsSewer).ToList();

            if (cwConsumers.Count == 0 && sewerConsumers.Count == 0)
                return allCreated;

            var referenceGroup = cwConsumers.Count > 0 ? cwConsumers : sewerConsumers;
            var tank = fixtures.FirstOrDefault(f => f.Role == HydraulicFixtureRole.WaterTank);

            double minX = referenceGroup.Min(c => c.Center.X);
            double maxX = referenceGroup.Max(c => c.Center.X);
            double minY = referenceGroup.Min(c => c.Center.Y);
            double maxY = referenceGroup.Max(c => c.Center.Y);
            bool wallAlongX = (maxX - minX) >= (maxY - minY);

            double floorZ = referenceGroup.Min(c => c.BaseZ);
            var studPositions = DiscoverLsfStudPositions(doc, referenceGroup, wallAlongX, out double? detectedWallPlane);

            double wallPlane;
            if (detectedWallPlane.HasValue)
            {
                wallPlane = detectedWallPlane.Value;
            }
            else
            {
                double avgNormal = wallAlongX
                    ? referenceGroup.Average(c => c.Center.Y)
                    : referenceGroup.Average(c => c.Center.X);
                wallPlane = avgNormal - (0.12 * FeetPerMeter);
            }

            // Regra estrutural LSF: barrilete horizontal a 0.45m do piso (>= 150mm acima da guia inferior)
            double trunkZ = floorZ + (0.45 * FeetPerMeter);
            double minU = referenceGroup.Min(c => wallAlongX ? c.Center.X : c.Center.Y);
            double maxU = referenceGroup.Max(c => wallAlongX ? c.Center.X : c.Center.Y);
            double dropU = ChooseFreeStudBayU(
                studPositions,
                minU,
                maxU,
                tank != null ? (wallAlongX ? tank.Center.X : tank.Center.Y) : (double?)null);

            ElementId cwSysTypeId = PipeNetworkHelper.GetOrCreateSystemTypeId(doc, MEPSystemClassification.DomesticColdWater, "Água Fria - Bahr Hidro");
            ElementId sanSysTypeId = PipeNetworkHelper.GetOrCreateSystemTypeId(doc, MEPSystemClassification.Sanitary, "Esgoto Sanitário - Bahr Hidro");
            ElementId pipeTypeId = ResolvePipeTypeId(doc, preferredPipeTypeId);
            ElementId levelId = ResolveNearestLevelId(doc, floorZ);

            if (pipeTypeId == ElementId.InvalidElementId || levelId == ElementId.InvalidElementId)
                return allCreated;

            bool ownTransaction = !doc.IsModifiable;
            Transaction? tx = null;

            try
            {
                if (ownTransaction)
                {
                    tx = new Transaction(doc, "Bahr Hidro - Traçado Inteligente LSF 3D (AF + Esgoto)");
                    tx.Start();
                }

                FamilyLoader.EnsureEssentialFittings(doc);
                PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, pipeTypeId);

                XYZ MakePt(double u, double n, double z) => wallAlongX ? new XYZ(u, n, z) : new XYZ(n, u, z);

                // =========================================================
                // 1. REDE DE ÁGUA FRIA PRESSURIZADA (NBR 5626)
                // =========================================================
                var cwPipes = new List<Pipe>();
                if (cwConsumers.Count > 0 && cwSysTypeId != ElementId.InvalidElementId)
                {
                    double totalWeight = cwConsumers.Sum(c => c.RelativeWeight);
                    double mainTrunkDn = totalWeight > 3.5 ? 32.0 : 25.0;

                    if (tank != null)
                    {
                        double tankU = wallAlongX ? tank.Center.X : tank.Center.Y;
                        double tankN = wallAlongX ? tank.Center.Y : tank.Center.X;
                        double tankZ = Math.Max(tank.BaseZ, floorZ + 2.60 * FeetPerMeter);

                        XYZ pTankOut = MakePt(tankU, tankN, tankZ);
                        XYZ pAlignU = MakePt(dropU, tankN, tankZ);
                        XYZ pTopWall = MakePt(dropU, wallPlane, tankZ);
                        XYZ pTrunkDrop = MakePt(dropU, wallPlane, trunkZ);

                        TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pTankOut, pAlignU, 32.0, $"AF | Barrilete 1 | Peso={totalWeight:F1}");
                        TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pAlignU, pTopWall, 32.0, $"AF | Barrilete 2 | Peso={totalWeight:F1}");
                        TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pTopWall, pTrunkDrop, 32.0, $"AF | Prumada Principal | Peso={totalWeight:F1}");
                    }
                    else
                    {
                        XYZ pCeiling = MakePt(dropU, wallPlane, floorZ + 2.80 * FeetPerMeter);
                        XYZ pTrunkDrop = MakePt(dropU, wallPlane, trunkZ);
                        TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pCeiling, pTrunkDrop, 32.0, $"AF | Prumada Principal | Peso={totalWeight:F1}");
                    }

                    // Registro de Gaveta geral na descida (Z = 1.80m) se família existir
                    TryPlaceValveOnWall(doc, levelId, MakePt(dropU, wallPlane, floorZ + 1.80 * FeetPerMeter), "gaveta");

                    var fixtureNodes = cwConsumers.Select(c =>
                    {
                        double u = wallAlongX ? c.Center.X : c.Center.Y;
                        if (c.Role == HydraulicFixtureRole.Toilet)
                            u -= 0.12 * FeetPerMeter;
                        return new { Fixture = c, U = u };
                    }).OrderBy(x => x.U).ToList();

                    var allTrunkUs = new List<double> { dropU };
                    allTrunkUs.AddRange(fixtureNodes.Select(x => x.U));
                    allTrunkUs = DeduplicateSorted(allTrunkUs, 0.03);

                    for (int i = 0; i < allTrunkUs.Count - 1; i++)
                    {
                        XYZ pA = MakePt(allTrunkUs[i], wallPlane, trunkZ);
                        XYZ pB = MakePt(allTrunkUs[i + 1], wallPlane, trunkZ);
                        TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pA, pB, mainTrunkDn, $"AF | Ramal Tronco LSF #{i + 1} | Peso={totalWeight:F1}");
                    }

                    foreach (var item in fixtureNodes)
                    {
                        var f = item.Fixture;
                        double u = item.U;
                        double fixN = wallAlongX ? f.Center.Y : f.Center.X;
                        double dirSign = fixN >= wallPlane ? 1.0 : -1.0;
                        double stubN = wallPlane + (dirSign * 0.08 * FeetPerMeter);

                        double targetZ = floorZ + (f.FeedElevationOffsetMeters * FeetPerMeter);
                        XYZ pBaseNode = MakePt(u, wallPlane, trunkZ);

                        if (f.IntermediateValveOffsetMeters.HasValue)
                        {
                            double valveZ = floorZ + (f.IntermediateValveOffsetMeters.Value * FeetPerMeter);
                            XYZ pValve = MakePt(u, wallPlane, valveZ);
                            XYZ pTop = MakePt(u, wallPlane, targetZ);
                            XYZ pOut = MakePt(u, stubN, targetZ);

                            TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pBaseNode, pValve, f.ColdWaterDiameterMm, $"AF | Sub-ramal {f.Name} | Peso={f.RelativeWeight:F1}");
                            TryPlaceValveOnWall(doc, levelId, pValve, "pressao");
                            TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pValve, pTop, f.ColdWaterDiameterMm, $"AF | Sub-ramal Ducha {f.Name} | Peso={f.RelativeWeight:F1}");
                            TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pTop, pOut, f.ColdWaterDiameterMm, $"AF | Ponto {f.Name} | Peso={f.RelativeWeight:F1}");
                        }
                        else
                        {
                            XYZ pEndWall = MakePt(u, wallPlane, targetZ);
                            XYZ pOut = MakePt(u, stubN, targetZ);

                            TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pBaseNode, pEndWall, f.ColdWaterDiameterMm, $"AF | Sub-ramal {f.Name} | Peso={f.RelativeWeight:F1}");
                            TryAddPipe(doc, cwPipes, cwSysTypeId, pipeTypeId, levelId, pEndWall, pOut, f.ColdWaterDiameterMm, $"AF | Ponto {f.Name} | Peso={f.RelativeWeight:F1}");
                        }
                    }

                    doc.Regenerate();
                    PipeFittingAutoConnector.ConnectPipesWithElbows(doc, cwPipes);
                    allCreated.AddRange(cwPipes);
                }

                // =========================================================
                // 2. REDE DE ESGOTO SANITÁRIO POR GRAVIDADE (NBR 8160)
                // =========================================================
                var sewerPipes = GenerateSanitarySewerBranch(doc, sanSysTypeId, pipeTypeId, levelId, sewerConsumers, wallAlongX, wallPlane, floorZ, dropU);
                allCreated.AddRange(sewerPipes);

                // =========================================================
                // 3. AUDITORIA LSF E QUANTITATIVOS EXECUTIVOS
                // =========================================================
                ExecutiveDetailingGenerator.AuditAndMarkLsfPenetrations(doc, allCreated, floorZ);
                ExecutiveDetailingGenerator.CreateHydraulicSchedules(doc);

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

        private static List<Pipe> GenerateSanitarySewerBranch(
            Document doc,
            ElementId sanSysTypeId,
            ElementId pipeTypeId,
            ElementId levelId,
            List<SemanticFixtureNode> sewerConsumers,
            bool wallAlongX,
            double wallPlane,
            double floorZ,
            double stackU)
        {
            var sewerPipes = new List<Pipe>();
            if (sanSysTypeId == ElementId.InvalidElementId || sewerConsumers.Count == 0)
                return sewerPipes;

            XYZ MakePt(double u, double n, double z) => wallAlongX ? new XYZ(u, n, z) : new XYZ(n, u, z);

            double subFloorZ = floorZ - (0.14 * FeetPerMeter);
            var toilet = sewerConsumers.FirstOrDefault(f => f.Role == HydraulicFixtureRole.Toilet);
            var secondary = sewerConsumers.Where(f => f.Role != HydraulicFixtureRole.Toilet).ToList();

            // Localiza caixa sifonada (ralo do box ou ponto médio dos aparelhos secundários)
            var drainNode = secondary.FirstOrDefault(f => f.Role == HydraulicFixtureRole.FloorDrain);
            double csU = drainNode != null
                ? (wallAlongX ? drainNode.Center.X : drainNode.Center.Y)
                : (secondary.Count > 0 ? secondary.Average(f => wallAlongX ? f.Center.X : f.Center.Y) : stackU);
            double csN = drainNode != null
                ? (wallAlongX ? drainNode.Center.Y : drainNode.Center.X)
                : wallPlane + (0.35 * FeetPerMeter);

            XYZ pCaixaSifonada = MakePt(csU, csN, subFloorZ);

            // Ramais secundários (DN40 / DN50) com declividade de 2% até a Caixa Sifonada
            foreach (var f in secondary)
            {
                if (f.Role == HydraulicFixtureRole.FloorDrain) continue;

                double u = wallAlongX ? f.Center.X : f.Center.Y;
                double fixN = wallAlongX ? f.Center.Y : f.Center.X;
                double dirSign = fixN >= wallPlane ? 1.0 : -1.0;
                double stubN = wallPlane + (dirSign * 0.08 * FeetPerMeter);

                double fixtureOutZ = f.Role switch
                {
                    HydraulicFixtureRole.Sink => floorZ + (0.50 * FeetPerMeter),
                    HydraulicFixtureRole.LaundryTub => floorZ + (0.75 * FeetPerMeter),
                    HydraulicFixtureRole.WashingMachine => floorZ + (0.75 * FeetPerMeter),
                    _ => floorZ + (0.45 * FeetPerMeter)
                };

                XYZ pFixOut = MakePt(u, stubN, fixtureOutZ);
                XYZ pWallEntry = MakePt(u, wallPlane, fixtureOutZ);
                XYZ pWallDrop = MakePt(u, wallPlane, subFloorZ + 0.03 * FeetPerMeter);

                TryAddPipe(doc, sewerPipes, sanSysTypeId, pipeTypeId, levelId, pFixOut, pWallEntry, f.SewerDiameterMm, $"ESG | Saída {f.Name} | UHC={f.UhC}");
                TryAddPipe(doc, sewerPipes, sanSysTypeId, pipeTypeId, levelId, pWallEntry, pWallDrop, f.SewerDiameterMm, $"ESG | Descida {f.Name} | UHC={f.UhC}");

                // Trecho horizontal inclinado a 2% até a Caixa Sifonada
                double horizDist = Math.Sqrt(Math.Pow(csU - u, 2) + Math.Pow(csN - wallPlane, 2));
                double dropDelta = Math.Max(0.02 * horizDist, 0.015);
                XYZ pStartSlope = MakePt(u, wallPlane, subFloorZ + dropDelta);
                TryAddPipe(doc, sewerPipes, sanSysTypeId, pipeTypeId, levelId, pStartSlope, pCaixaSifonada, f.SewerDiameterMm, $"ESG | Ramal i=2% {f.Name} | UHC={f.UhC}");
            }

            // Coletor principal DN100 (declividade 1%) do Vaso Sanitário até o Tubo de Queda
            double collectorStartU = toilet != null ? (wallAlongX ? toilet.Center.X : toilet.Center.Y) : csU;
            double collectorN = toilet != null ? (wallAlongX ? toilet.Center.Y : toilet.Center.X) : csN;
            double endStackU = Math.Abs(stackU - collectorStartU) > 0.30 ? stackU : collectorStartU + (0.80 * FeetPerMeter);

            XYZ pCollectorStart = MakePt(collectorStartU, collectorN, subFloorZ - 0.02 * FeetPerMeter);
            double mainDist = Math.Abs(endStackU - collectorStartU);
            XYZ pCollectorEnd = MakePt(endStackU, collectorN, subFloorZ - 0.02 * FeetPerMeter - (0.01 * mainDist));

            int totalUhc = sewerConsumers.Sum(s => s.UhC);
            if (toilet != null)
            {
                XYZ pToiletFloor = MakePt(collectorStartU, collectorN, floorZ);
                TryAddPipe(doc, sewerPipes, sanSysTypeId, pipeTypeId, levelId, pToiletFloor, pCollectorStart, 100.0, "ESG | Saída Vaso Sanitário | UHC=6");
            }

            TryAddPipe(doc, sewerPipes, sanSysTypeId, pipeTypeId, levelId, pCollectorStart, pCollectorEnd, 100.0, $"ESG | Coletor Principal i=1% | UHC={totalUhc}");

            // Saída DN50 da Caixa Sifonada (declividade 2%) ligando no coletor principal DN100
            if (secondary.Count > 0)
            {
                double midU = (collectorStartU + endStackU) * 0.5;
                XYZ pJoinMain = MakePt(midU, collectorN, subFloorZ - 0.02 * FeetPerMeter - (0.005 * mainDist));
                TryAddPipe(doc, sewerPipes, sanSysTypeId, pipeTypeId, levelId, pCaixaSifonada, pJoinMain, 50.0, "ESG | Saída Caixa Sifonada i=2%");
            }

            doc.Regenerate();
            PipeFittingAutoConnector.ConnectPipesWithElbows(doc, sewerPipes);
            return sewerPipes;
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
            catch
            {
            }
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
                    node.ColdWaterDiameterMm = 32.0;
                    node.RelativeWeight = 0.0;
                    node.NeedsColdWater = false;
                    node.NeedsSewer = false;
                    break;
                case HydraulicFixtureRole.FloorDrain:
                    node.NeedsColdWater = false;
                    node.NeedsSewer = true;
                    node.SewerDiameterMm = 40.0;
                    node.UhC = 2;
                    break;
                case HydraulicFixtureRole.Shower:
                    node.FeedElevationOffsetMeters = 2.10;
                    node.IntermediateValveOffsetMeters = 1.10;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 0.4;
                    node.SewerDiameterMm = 40.0;
                    node.UhC = 2;
                    break;
                case HydraulicFixtureRole.Toilet:
                    node.FeedElevationOffsetMeters = 0.20;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 0.3;
                    node.SewerDiameterMm = 100.0;
                    node.UhC = 6;
                    break;
                case HydraulicFixtureRole.Sink:
                    node.FeedElevationOffsetMeters = 0.60;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 0.3;
                    node.SewerDiameterMm = 40.0;
                    node.UhC = 1;
                    break;
                case HydraulicFixtureRole.LaundryTub:
                    node.FeedElevationOffsetMeters = 1.00;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 0.7;
                    node.SewerDiameterMm = 40.0;
                    node.UhC = 3;
                    break;
                case HydraulicFixtureRole.WashingMachine:
                    node.FeedElevationOffsetMeters = 0.90;
                    node.ColdWaterDiameterMm = 20.0;
                    node.RelativeWeight = 1.0;
                    node.SewerDiameterMm = 50.0;
                    node.UhC = 3;
                    break;
            }
        }

        private static List<double> DiscoverLsfStudPositions(
            Document doc,
            List<SemanticFixtureNode> consumers,
            bool wallAlongX,
            out double? detectedWallPlane)
        {
            detectedWallPlane = null;
            var studs = new List<(double U, double N)>();
            double avgFixtureN = wallAlongX
                ? consumers.Average(c => c.Center.Y)
                : consumers.Average(c => c.Center.X);

            var cats = new[]
            {
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_GenericModel
            };

            void ScanStuds(Document targetDoc, Transform tr)
            {
                foreach (var cat in cats)
                {
                    var elems = new FilteredElementCollector(targetDoc)
                        .WhereElementIsNotElementType()
                        .OfCategory(cat)
                        .ToList();

                    foreach (var el in elems)
                    {
                        BoundingBoxXYZ? bb = el.get_BoundingBox(null);
                        if (bb == null) continue;

                        XYZ pMin = tr.OfPoint(bb.Min);
                        XYZ pMax = tr.OfPoint(bb.Max);
                        double dx = Math.Abs(pMax.X - pMin.X);
                        double dy = Math.Abs(pMax.Y - pMin.Y);
                        double dz = Math.Abs(pMax.Z - pMin.Z);

                        if (dz < 1.5 * FeetPerMeter || dx > 0.30 * FeetPerMeter || dy > 0.30 * FeetPerMeter)
                            continue;

                        XYZ c = (pMin + pMax) * 0.5;
                        double u = wallAlongX ? c.X : c.Y;
                        double n = wallAlongX ? c.Y : c.X;

                        if (Math.Abs(n - avgFixtureN) <= 1.20 * FeetPerMeter)
                            studs.Add((u, n));
                    }
                }
            }

            ScanStuds(doc, Transform.Identity);
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ldoc = link.GetLinkDocument();
                if (ldoc != null)
                    ScanStuds(ldoc, link.GetTotalTransform());
            }

            if (studs.Count >= 2)
            {
                var orderedN = studs.Select(s => s.N).OrderBy(x => x).ToList();
                detectedWallPlane = orderedN[orderedN.Count / 2];
                double plane = detectedWallPlane.Value;

                var wallStudUs = studs
                    .Where(s => Math.Abs(s.N - plane) <= 0.15 * FeetPerMeter)
                    .Select(s => s.U)
                    .OrderBy(x => x)
                    .ToList();

                return DeduplicateSorted(wallStudUs, 0.15);
            }

            // Fallback: detecta parede arquitetônica nativa próxima aos aparelhos
            var walls = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategory(BuiltInCategory.OST_Walls)
                .Cast<Wall>()
                .ToList();

            double bestDist = 1.0 * FeetPerMeter;
            foreach (var w in walls)
            {
                if (w.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                XYZ p0 = line.GetEndPoint(0);
                XYZ p1 = line.GetEndPoint(1);
                bool wAlongX = Math.Abs(p1.X - p0.X) >= Math.Abs(p1.Y - p0.Y);
                if (wAlongX != wallAlongX) continue;

                double wPlane = wallAlongX ? (p0.Y + p1.Y) * 0.5 : (p0.X + p1.X) * 0.5;
                double d = Math.Abs(wPlane - avgFixtureN);
                if (d < bestDist)
                {
                    bestDist = d;
                    detectedWallPlane = wPlane;
                }
            }

            return new List<double>();
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

        private static ElementId ResolvePipeTypeId(Document doc, ElementId? preferred)
        {
            if (preferred != null && preferred != ElementId.InvalidElementId && doc.GetElement(preferred) is PipeType)
                return preferred;

            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .ToList();

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
