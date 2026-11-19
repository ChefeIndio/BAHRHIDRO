using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace BahrHidro.Revit
{
    public enum HydraulicFixtureRole
    {
        Unknown,
        WaterTank,
        Shower,
        Toilet,
        Sink,
        LaundryTub,
        WashingMachine,
        DrainOrAccessory
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
        public double DiameterMm { get; set; } = 20.0;
        public double RelativeWeight { get; set; } = 0.3;
    }

    public static class SmartHydraulicRoutingEngine
    {
        private const double FeetPerMeter = 3.280839895013123;
        private const double MmToFeet = 1.0 / 304.8;

        public static List<Pipe> GenerateOrthogonalLsfNetwork(Document doc, ElementId? preferredPipeTypeId = null)
        {
            var createdPipes = new List<Pipe>();
            if (doc == null) return createdPipes;

            var fixtures = DiscoverSemanticFixtures(doc);
            var consumers = fixtures.Where(f =>
                f.Role != HydraulicFixtureRole.WaterTank &&
                f.Role != HydraulicFixtureRole.DrainOrAccessory &&
                f.Role != HydraulicFixtureRole.Unknown).ToList();

            if (consumers.Count == 0) return createdPipes;

            var tank = fixtures.FirstOrDefault(f => f.Role == HydraulicFixtureRole.WaterTank);

            // Detecta orientação predominante da parede hidráulica (along X vs along Y)
            double minX = consumers.Min(c => c.Center.X);
            double maxX = consumers.Max(c => c.Center.X);
            double minY = consumers.Min(c => c.Center.Y);
            double maxY = consumers.Max(c => c.Center.Y);
            bool wallAlongX = (maxX - minX) >= (maxY - minY);

            double floorZ = consumers.Min(c => c.BaseZ);
            var studPositions = DiscoverLsfStudPositions(doc, consumers, wallAlongX, out double? detectedWallPlane);

            double wallPlane;
            if (detectedWallPlane.HasValue)
            {
                wallPlane = detectedWallPlane.Value;
            }
            else
            {
                double avgNormal = wallAlongX
                    ? consumers.Average(c => c.Center.Y)
                    : consumers.Average(c => c.Center.X);
                wallPlane = avgNormal - (0.12 * FeetPerMeter);
            }

            // Cota do barrilete horizontal dentro da parede LSF (Z_piso + 0.45m, acima da zona crítica de 150mm da guia)
            double trunkZ = floorZ + (0.45 * FeetPerMeter);

            // Escolhe posição longitudinal (U) da descida da prumada no centro de um vão livre entre montantes LSF
            double minU = consumers.Min(c => wallAlongX ? c.Center.X : c.Center.Y);
            double maxU = consumers.Max(c => wallAlongX ? c.Center.X : c.Center.Y);
            double dropU = ChooseFreeStudBayU(studPositions, minU, maxU, tank != null ? (wallAlongX ? tank.Center.X : tank.Center.Y) : (double?)null);

            ElementId sysTypeId = PipeNetworkHelper.GetOrCreateColdWaterSystemTypeId(doc);
            ElementId pipeTypeId = ResolvePipeTypeId(doc, preferredPipeTypeId);
            ElementId levelId = ResolveNearestLevelId(doc, floorZ);

            if (sysTypeId == ElementId.InvalidElementId || pipeTypeId == ElementId.InvalidElementId || levelId == ElementId.InvalidElementId)
                return createdPipes;

            bool ownTransaction = !doc.IsModifiable;
            Transaction? tx = null;

            try
            {
                if (ownTransaction)
                {
                    tx = new Transaction(doc, "Bahr Hidro - Traçado Inteligente LSF 3D");
                    tx.Start();
                }

                FamilyLoader.EnsureEssentialFittings(doc);
                PipeFittingAutoConnector.EnsureElbowRoutingPreference(doc, pipeTypeId);

                XYZ MakePt(double u, double n, double z) => wallAlongX ? new XYZ(u, n, z) : new XYZ(n, u, z);

                // 1. Descida da Prumada (Reservatório -> Parede LSF -> Cota do Barrilete)
                if (tank != null)
                {
                    double tankU = wallAlongX ? tank.Center.X : tank.Center.Y;
                    double tankN = wallAlongX ? tank.Center.Y : tank.Center.X;
                    double tankZ = Math.Max(tank.BaseZ, floorZ + 2.60 * FeetPerMeter);

                    XYZ pTankOut = MakePt(tankU, tankN, tankZ);
                    XYZ pAlignU = MakePt(dropU, tankN, tankZ);
                    XYZ pTopWall = MakePt(dropU, wallPlane, tankZ);
                    XYZ pTrunkDrop = MakePt(dropU, wallPlane, trunkZ);

                    TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pTankOut, pAlignU, 32.0, "Barrilete Superior - Trecho 1");
                    TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pAlignU, pTopWall, 32.0, "Barrilete Superior - Trecho 2");
                    TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pTopWall, pTrunkDrop, 32.0, "Prumada Principal AF");
                }
                else
                {
                    XYZ pCeiling = MakePt(dropU, wallPlane, floorZ + 2.80 * FeetPerMeter);
                    XYZ pTrunkDrop = MakePt(dropU, wallPlane, trunkZ);
                    TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pCeiling, pTrunkDrop, 32.0, "Prumada Principal AF");
                }

                // 2. Mapeia nós de derivação no barrilete horizontal para garantir Tês exatos
                var fixtureNodes = consumers.Select(c =>
                {
                    double u = wallAlongX ? c.Center.X : c.Center.Y;
                    if (c.Role == HydraulicFixtureRole.Toilet)
                        u -= 0.12 * FeetPerMeter; // Ponto AF do vaso deslocado 12cm do eixo da bacia
                    return new { Fixture = c, U = u };
                }).OrderBy(x => x.U).ToList();

                var allTrunkUs = new List<double> { dropU };
                allTrunkUs.AddRange(fixtureNodes.Select(x => x.U));
                allTrunkUs = DeduplicateSorted(allTrunkUs, 0.03); // ~9mm tolerância de nó

                for (int i = 0; i < allTrunkUs.Count - 1; i++)
                {
                    XYZ pA = MakePt(allTrunkUs[i], wallPlane, trunkZ);
                    XYZ pB = MakePt(allTrunkUs[i + 1], wallPlane, trunkZ);
                    TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pA, pB, 25.0, $"Ramal Principal Parede #{i + 1}");
                }

                // 3. Sub-ramais verticais e pontos de saída para cada aparelho
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

                        TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pBaseNode, pValve, f.DiameterMm, $"Sub-ramal {f.Name} (Ate Registro)");
                        TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pValve, pTop, f.DiameterMm, $"Sub-ramal {f.Name} (Registro -> Ponto)");
                        TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pTop, pOut, f.DiameterMm, $"Ponto Consumo - {f.Name}");
                    }
                    else
                    {
                        XYZ pEndWall = MakePt(u, wallPlane, targetZ);
                        XYZ pOut = MakePt(u, stubN, targetZ);

                        TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pBaseNode, pEndWall, f.DiameterMm, $"Sub-ramal {f.Name}");
                        TryAddPipe(doc, createdPipes, sysTypeId, pipeTypeId, levelId, pEndWall, pOut, f.DiameterMm, $"Ponto Consumo - {f.Name}");
                    }
                }

                doc.Regenerate();
                PipeFittingAutoConnector.ConnectPipesWithElbows(doc, createdPipes);

                if (ownTransaction && tx != null)
                    tx.Commit();
            }
            catch
            {
                if (ownTransaction && tx != null && tx.HasStarted())
                    tx.RollBack();
                throw;
            }

            return createdPipes;
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
            if (p1.DistanceTo(p2) < 0.02) return; // Evita trechos menores que ~6mm
            var pipe = Pipe.Create(doc, sysTypeId, pipeTypeId, levelId, p1, p2);
            if (pipe == null) return;

            var diamParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (diamParam != null && !diamParam.IsReadOnly)
                diamParam.Set(diameterMm * MmToFeet);

            var cmtParam = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (cmtParam != null && !cmtParam.IsReadOnly)
                cmtParam.Set($"BahrHidro | {tagComment}");

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
                    var role = ClassifyFixture(fingerprint, maxPt.Z - minPt.Z);
                    if (role == HydraulicFixtureRole.Unknown || role == HydraulicFixtureRole.DrainOrAccessory)
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

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
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

        private static HydraulicFixtureRole ClassifyFixture(string text, double heightFeet)
        {
            if (text.Contains("ralo") || text.Contains("grelha") || text.Contains("sifao") ||
                text.Contains("caixa sifonada") || text.Contains("registro de pressao") || text.Contains("registro de gaveta"))
                return HydraulicFixtureRole.DrainOrAccessory;

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
                    node.DiameterMm = 32.0;
                    node.RelativeWeight = 0.0;
                    break;
                case HydraulicFixtureRole.Shower:
                    node.FeedElevationOffsetMeters = 2.10;
                    node.IntermediateValveOffsetMeters = 1.10;
                    node.DiameterMm = 20.0;
                    node.RelativeWeight = 0.4;
                    break;
                case HydraulicFixtureRole.Toilet:
                    node.FeedElevationOffsetMeters = 0.20;
                    node.DiameterMm = 20.0;
                    node.RelativeWeight = 0.3;
                    break;
                case HydraulicFixtureRole.Sink:
                    node.FeedElevationOffsetMeters = 0.60;
                    node.DiameterMm = 20.0;
                    node.RelativeWeight = 0.3;
                    break;
                case HydraulicFixtureRole.LaundryTub:
                    node.FeedElevationOffsetMeters = 1.00;
                    node.DiameterMm = 20.0;
                    node.RelativeWeight = 0.7;
                    break;
                case HydraulicFixtureRole.WashingMachine:
                    node.FeedElevationOffsetMeters = 0.90;
                    node.DiameterMm = 20.0;
                    node.RelativeWeight = 1.0;
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

                        // Montante vertical LSF: altura > 1.5m e seção < 0.30m
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
