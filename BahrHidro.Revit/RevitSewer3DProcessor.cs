using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitSewer3DProcessor
    {
        private enum SewerSegmentRole
        {
            Branch,
            VerticalStack,
            SubCollector,
            Vent
        }

        private sealed class PipeNodeInfo
        {
            public Pipe Pipe { get; init; } = null!;
            public long Id => Pipe.Id.Value;
            public SewerSegmentRole Role { get; set; }
            public double MinZ { get; set; }
            public double MaxZ { get; set; }
            public double MidX { get; set; }
            public double MidY { get; set; }
            public double LengthMeters { get; set; }
            public int DirectUhc { get; set; }
            public int DirectMinDnMm { get; set; }
            public bool HasWaterCloset { get; set; }
            public int AccumulatedUhc { get; set; }
            public int RequiredDnMm { get; set; }
            public double RequiredSlopePercent { get; set; }
            public string TagLabel { get; set; } = "";
        }

        public static (Sewer3DCalculationResult Result, Dictionary<string, int> FixtureCounts) CalculateAndApplySewer3D(Document doc)
        {
            var allPipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .Where(IsSanitaryOrVentPipe)
                .ToList();

            if (allPipes.Count == 0)
                throw new InvalidOperationException("Nenhuma tubulação de Esgoto Sanitário ou Ventilação foi encontrada no modelo 3D.");

            var nodesById = new Dictionary<long, PipeNodeInfo>();
            var fixtureCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in allPipes)
            {
                var (p1, p2) = GetPipeEndpointsMeters(p);
                double dz = Math.Abs(p2.Z - p1.Z);
                double dxy = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
                double lenM = Math.Max(0.05, Math.Sqrt(dx2(p2.X - p1.X, p2.Y - p1.Y, p2.Z - p1.Z)));

                bool isVent = IsVentPipe(p);
                bool isVertical = dz >= 0.80 && dxy <= 0.45;

                var info = new PipeNodeInfo
                {
                    Pipe = p,
                    MinZ = Math.Min(p1.Z, p2.Z),
                    MaxZ = Math.Max(p1.Z, p2.Z),
                    MidX = (p1.X + p2.X) * 0.5,
                    MidY = (p1.Y + p2.Y) * 0.5,
                    LengthMeters = lenM,
                    Role = isVent
                        ? SewerSegmentRole.Vent
                        : (isVertical ? SewerSegmentRole.VerticalStack : SewerSegmentRole.Branch),
                    DirectMinDnMm = isVent ? 50 : 40
                };
                nodesById[info.Id] = info;
            }

            // Mapeia conectividade entre tubos e aparelhos sanitários
            var adj = nodesById.Keys.ToDictionary(id => id, _ => new HashSet<long>());
            var visitedFixtures = new HashSet<long>();

            foreach (var info in nodesById.Values)
            {
                TraverseConnectedElements(info.Pipe, info, nodesById, adj, visitedFixtures, fixtureCounts);
            }

            // Identifica subcoletores / coletores prediais (trechos horizontais na base das prumadas ou cota baixa)
            double globalMinZ = nodesById.Values.Min(n => n.MinZ);
            foreach (var info in nodesById.Values)
            {
                if (info.Role == SewerSegmentRole.Branch)
                {
                    bool connectsToStack = adj[info.Id].Any(nid => nodesById[nid].Role == SewerSegmentRole.VerticalStack);
                    bool isAtBase = info.MaxZ <= globalMinZ + 0.45;
                    if (connectsToStack && isAtBase)
                        info.Role = SewerSegmentRole.SubCollector;
                }
            }

            // Agrupa componentes conectados e propaga UHC de cima para baixo (montante -> jusante)
            var visitedComp = new HashSet<long>();
            var risersSummary = new List<Sewer3DRiserSummary>();
            int stackCounter = 1;
            int subCounter = 1;
            int ventCounter = 1;

            foreach (var seedId in nodesById.Keys)
            {
                if (!visitedComp.Add(seedId)) continue;

                var compIds = new List<long> { seedId };
                var q = new Queue<long>();
                q.Enqueue(seedId);
                while (q.Count > 0)
                {
                    long curr = q.Dequeue();
                    foreach (long nb in adj[curr])
                    {
                        if (visitedComp.Add(nb))
                        {
                            compIds.Add(nb);
                            q.Enqueue(nb);
                        }
                    }
                }

                // Ordena da maior cota para a menor cota (fluxo gravitacional)
                var orderedDownstream = compIds
                    .Select(id => nodesById[id])
                    .OrderByDescending(n => n.MaxZ)
                    .ThenByDescending(n => n.MinZ)
                    .ToList();

                // Inicializa acumulados
                foreach (var n in orderedDownstream)
                {
                    n.AccumulatedUhc = n.DirectUhc;
                    n.RequiredDnMm = n.DirectMinDnMm;
                }

                // Propaga carga para os vizinhos a jusante (menor ou igual cota)
                foreach (var n in orderedDownstream)
                {
                    var downstreamNeighbors = adj[n.Id]
                        .Select(id => nodesById[id])
                        .Where(nb => nb.MinZ <= n.MinZ + 0.05 && nb.Id != n.Id)
                        .OrderBy(nb => nb.MinZ)
                        .ToList();

                    if (downstreamNeighbors.Count > 0)
                    {
                        var target = downstreamNeighbors.First();
                        target.AccumulatedUhc += n.AccumulatedUhc;
                        if (n.HasWaterCloset) target.HasWaterCloset = true;
                        if (n.Role == SewerSegmentRole.VerticalStack && target.Role == SewerSegmentRole.Branch)
                            target.Role = SewerSegmentRole.SubCollector;
                    }
                }

                // Dimensiona cada trecho segundo NBR 8160 (Tabelas 6, 7, 8 e 9)
                foreach (var n in orderedDownstream)
                {
                    int uhc = Math.Max(1, n.AccumulatedUhc);
                    if (n.Role == SewerSegmentRole.Vent)
                    {
                        n.RequiredDnMm = uhc <= 42 ? 50 : (uhc <= 300 ? 75 : 100);
                        n.RequiredSlopePercent = 1.0;
                        n.TagLabel = $"CV-{ventCounter++}";
                    }
                    else if (n.Role == SewerSegmentRole.VerticalStack)
                    {
                        int dn = uhc <= 4 ? 40 : (uhc <= 10 ? 50 : (uhc <= 70 ? 75 : (uhc <= 500 ? 100 : 150)));
                        if (n.HasWaterCloset) dn = Math.Max(dn, 100);
                        n.RequiredDnMm = Math.Max(dn, n.DirectMinDnMm);
                        n.RequiredSlopePercent = 0.0;
                        n.TagLabel = $"TQ-{stackCounter++}";
                    }
                    else if (n.Role == SewerSegmentRole.SubCollector)
                    {
                        int dn = uhc <= 180 ? 100 : (uhc <= 700 ? 150 : 200);
                        if (n.HasWaterCloset) dn = Math.Max(dn, 100);
                        n.RequiredDnMm = Math.Max(dn, n.DirectMinDnMm);
                        n.RequiredSlopePercent = n.RequiredDnMm <= 75 ? 2.0 : 1.0;
                        n.TagLabel = $"SUB-{subCounter++}";
                    }
                    else
                    {
                        int dn = uhc <= 3 ? 40 : (uhc <= 6 ? 50 : (uhc <= 20 ? 75 : 100));
                        if (n.HasWaterCloset) dn = Math.Max(dn, 100);
                        n.RequiredDnMm = Math.Max(dn, n.DirectMinDnMm);
                        n.RequiredSlopePercent = n.RequiredDnMm <= 75 ? 2.0 : 1.0;
                        n.TagLabel = "RAMAL";
                    }
                }

                // Garante monotonicidade (não reduz diâmetro a jusante)
                foreach (var n in orderedDownstream)
                {
                    foreach (long nbId in adj[n.Id])
                    {
                        var nb = nodesById[nbId];
                        if (nb.MinZ <= n.MinZ + 0.05 && nb.Role != SewerSegmentRole.Vent && n.Role != SewerSegmentRole.Vent)
                        {
                            nb.RequiredDnMm = Math.Max(nb.RequiredDnMm, n.RequiredDnMm);
                            nb.RequiredSlopePercent = nb.RequiredDnMm <= 75 ? 2.0 : 1.0;
                        }
                    }
                }

                foreach (var n in orderedDownstream.Where(x => x.Role != SewerSegmentRole.Branch))
                {
                    string tipoTxt = n.Role switch
                    {
                        SewerSegmentRole.VerticalStack => "Tubo de Queda",
                        SewerSegmentRole.SubCollector => "Subcoletor / Coletor",
                        SewerSegmentRole.Vent => "Coluna Ventilação",
                        _ => "Ramal"
                    };
                    risersSummary.Add(new Sewer3DRiserSummary(
                        n.TagLabel,
                        tipoTxt,
                        n.AccumulatedUhc,
                        n.RequiredDnMm,
                        n.RequiredSlopePercent,
                        Math.Round(n.LengthMeters, 2)));
                }
            }

            // Aplica os diâmetros calculados diretamente nos tubos 3D do Revit
            int pipesUpdated = 0;
            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Dimensionar Esgoto 3D (NBR 8160)"))
            {
                tx.Start();
                foreach (var info in nodesById.Values)
                {
                    double dnFeet = UnitUtils.ConvertToInternalUnits(info.RequiredDnMm, UnitTypeId.Millimeters);
                    using (SubTransaction st = new SubTransaction(doc))
                    {
                        st.Start();
                        try
                        {
                            Parameter? diaParam = info.Pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                            if (diaParam != null && !diaParam.IsReadOnly)
                            {
                                diaParam.Set(dnFeet);
                                Parameter? cmt = info.Pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                                if (cmt != null && !cmt.IsReadOnly)
                                {
                                    string slopeTxt = info.RequiredSlopePercent > 0 ? $" | i={info.RequiredSlopePercent:F1}%" : " | Vertical";
                                    cmt.Set($"BahrHidro NBR8160 | {info.TagLabel} | {info.AccumulatedUhc} UHC | DN {info.RequiredDnMm}{slopeTxt}");
                                }
                                st.Commit();
                                if (st.GetStatus() == TransactionStatus.Committed)
                                    pipesUpdated++;
                            }
                            else
                            {
                                st.RollBack();
                            }
                        }
                        catch
                        {
                            if (st.HasStarted()) st.RollBack();
                        }
                    }
                }
                tx.Commit();
            }

            int totalUhc = Math.Max(1, nodesById.Values.Max(n => n.AccumulatedUhc));
            int mainDn = nodesById.Values.Where(n => n.Role != SewerSegmentRole.Vent).Select(n => n.RequiredDnMm).DefaultIfEmpty(100).Max();
            double mainSlope = mainDn <= 75 ? 2.0 : 1.0;

            string summary = $"Rede Sanitária 3D dimensionada (ABNT NBR 8160): {allPipes.Count} trechos analisados, {pipesUpdated} tubos atualizados no 3D | " +
                             $"{visitedFixtures.Count} aparelhos conectados ({totalUhc} UHC) | Coletor Principal: DN {mainDn} mm (i = {mainSlope:F1}%).";

            var result = new Sewer3DCalculationResult(
                allPipes.Count,
                pipesUpdated,
                visitedFixtures.Count,
                totalUhc,
                mainDn,
                mainSlope,
                summary,
                risersSummary.Take(25).ToList());

            return (result, fixtureCounts);
        }

        public static (VerticalRiserSchematicResult Result, ElementId ViewId) GenerateVerticalRiserSchematic(Document doc)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .WhereElementIsNotElementType()
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (levels.Count == 0)
                throw new InvalidOperationException("Nenhum nível (pavimento) encontrado no projeto do Revit.");

            var allPipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            // Detecta prumadas verticais (ΔZ >= 0,90 m) em todas as disciplinas
            var rawVerticals = new List<(string Disc, double X, double Y, double MinZ, double MaxZ, int DnMm)>();
            foreach (var p in allPipes)
            {
                var (p1, p2) = GetPipeEndpointsMeters(p);
                double dz = Math.Abs(p2.Z - p1.Z);
                double dxy = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
                if (dz < 0.90 || dxy > 0.45) continue;

                string disc = ClassifyDisciplineCode(p);
                double dnMm = 25;
                Parameter? dp = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (dp != null && dp.HasValue)
                    dnMm = Math.Round(UnitUtils.ConvertFromInternalUnits(dp.AsDouble(), UnitTypeId.Millimeters));

                rawVerticals.Add((disc, (p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5, Math.Min(p1.Z, p2.Z), Math.Max(p1.Z, p2.Z), (int)dnMm));
            }

            // Agrupa tubos verticais colineares (tolerância de 0,25 m em planta) em prumadas únicas
            var groupedRisers = new List<(string Name, string Disc, double MinZ, double MaxZ, List<(double ZMid, int DnMm)> Segments)>();
            var counters = new Dictionary<string, int> { ["AF"] = 1, ["AQ"] = 1, ["TQ"] = 1, ["CV"] = 1 };

            foreach (var seg in rawVerticals.OrderBy(s => s.Disc).ThenBy(s => s.X).ThenBy(s => s.Y))
            {
                _riserCoords.Clear();
// Procura prumada existente mesma disciplina e mesma coordenada XY
                int foundIdx = -1;
                for (int i = 0; i < groupedRisers.Count; i++)
                {
                    // Usa tag temporária armazenada no dicionário de coordenadas
                    if (groupedRisers[i].Disc == seg.Disc)
                    {
                        var coords = _riserCoords[groupedRisers[i].Name];
                        if (Math.Sqrt((coords.X - seg.X) * (coords.X - seg.X) + (coords.Y - seg.Y) * (coords.Y - seg.Y)) <= 0.30)
                        {
                            foundIdx = i;
                            break;
                        }
                    }
                }

                if (foundIdx >= 0)
                {
                    var cur = groupedRisers[foundIdx];
                    cur.Segments.Add(((seg.MinZ + seg.MaxZ) * 0.5, seg.DnMm));
                    groupedRisers[foundIdx] = (cur.Name, cur.Disc, Math.Min(cur.MinZ, seg.MinZ), Math.Max(cur.MaxZ, seg.MaxZ), cur.Segments);
                }
                else
                {
                    int idx = counters.TryGetValue(seg.Disc, out int c) ? c : 1;
                    counters[seg.Disc] = idx + 1;
                    string rName = $"{seg.Disc}-{idx}";
                    _riserCoords[rName] = (seg.X, seg.Y);
                    groupedRisers.Add((rName, seg.Disc, seg.MinZ, seg.MaxZ, new List<(double, int)> { ((seg.MinZ + seg.MaxZ) * 0.5, seg.DnMm) }));
                }
            }

            // Se o modelo ainda não tiver prumadas lançadas, gera esquema-base com os pavimentos do projeto
            if (groupedRisers.Count == 0)
            {
                double minLvl = UnitUtils.ConvertFromInternalUnits(levels.First().Elevation, UnitTypeId.Meters);
                double maxLvl = UnitUtils.ConvertFromInternalUnits(levels.Last().Elevation, UnitTypeId.Meters) + 2.5;
                groupedRisers.Add(("AF-1 (Ref.)", "AF", minLvl, maxLvl, new List<(double, int)> { ((minLvl + maxLvl) * 0.5, 32) }));
                groupedRisers.Add(("AQ-1 (Ref.)", "AQ", minLvl, maxLvl, new List<(double, int)> { ((minLvl + maxLvl) * 0.5, 22) }));
                groupedRisers.Add(("TQ-1 (Ref.)", "TQ", minLvl, maxLvl - 1.5, new List<(double, int)> { ((minLvl + maxLvl) * 0.5, 100) }));
                groupedRisers.Add(("CV-1 (Ref.)", "CV", minLvl, maxLvl + 0.3, new List<(double, int)> { ((minLvl + maxLvl) * 0.5, 50) }));
            }

            const string viewName = "Bahr Hidro - Esquema Vertical de Prumadas (ABNT)";
            ViewDrafting? schematicView = null;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Gerar Esquema Vertical de Prumadas"))
            {
                tx.Start();

                // Remove vista anterior com o mesmo nome para recriar atualizada
                var oldView = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .FirstOrDefault(v => v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));
                if (oldView != null)
                {
                    try { doc.Delete(oldView.Id); } catch { }
                }

                ViewFamilyType? vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);

                if (vft == null)
                    throw new InvalidOperationException("Tipo de vista de detalhe (Drafting View) não encontrado no template Revit.");

                schematicView = ViewDrafting.Create(doc, vft.Id);
                schematicView.Name = viewName;
                schematicView.Scale = 50;

                TextNoteType? textType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault();

                double colSpacingFt = UnitUtils.ConvertToInternalUnits(2.4, UnitTypeId.Meters);
                double leftMarginFt = UnitUtils.ConvertToInternalUnits(4.5, UnitTypeId.Meters);
                double totalWidthFt = leftMarginFt + (groupedRisers.Count + 1) * colSpacingFt;

                double minZFt = levels.First().Elevation;
                double maxZFt = Math.Max(levels.Last().Elevation + UnitUtils.ConvertToInternalUnits(2.5, UnitTypeId.Meters),
                    groupedRisers.Max(r => UnitUtils.ConvertToInternalUnits(r.MaxZ, UnitTypeId.Meters)));

                // Título do Esquema Vertical
                if (textType != null)
                {
                    TextNote.Create(doc, schematicView.Id,
                        new XYZ(0, maxZFt + UnitUtils.ConvertToInternalUnits(2.2, UnitTypeId.Meters), 0),
                        "ESQUEMA VERTICAL DE PRUMADAS HIDROSSANITÁRIAS — ABNT NBR 5626 / 7198 / 8160",
                        textType.Id);
                }

                // Desenha linhas de nível dos pavimentos
                foreach (var lvl in levels)
                {
                    double z = lvl.Elevation;
                    double zMeters = UnitUtils.ConvertFromInternalUnits(z, UnitTypeId.Meters);
                    Line lvlLine = Line.CreateBound(new XYZ(0, z, 0), new XYZ(totalWidthFt, z, 0));
                    doc.Create.NewDetailCurve(schematicView, lvlLine);

                    if (textType != null)
                    {
                        string sign = zMeters >= 0 ? "+" : "";
                        TextNote.Create(doc, schematicView.Id,
                            new XYZ(UnitUtils.ConvertToInternalUnits(0.2, UnitTypeId.Meters), z + UnitUtils.ConvertToInternalUnits(0.35, UnitTypeId.Meters), 0),
                            $"{lvl.Name} ({sign}{zMeters:F2} m)",
                            textType.Id);
                    }
                }

                // Desenha cada prumada vertical
                for (int i = 0; i < groupedRisers.Count; i++)
                {
                    var r = groupedRisers[i];
                    double x = leftMarginFt + i * colSpacingFt;
                    double yBot = Math.Max(minZFt, UnitUtils.ConvertToInternalUnits(r.MinZ, UnitTypeId.Meters));
                    double yTop = UnitUtils.ConvertToInternalUnits(r.MaxZ, UnitTypeId.Meters);
                    if (yTop <= yBot) yTop = yBot + UnitUtils.ConvertToInternalUnits(2.8, UnitTypeId.Meters);

                    Line riserLine = Line.CreateBound(new XYZ(x, yBot, 0), new XYZ(x, yTop, 0));
                    doc.Create.NewDetailCurve(schematicView, riserLine);

                    if (textType != null)
                    {
                        // Cabeçalho da prumada no topo
                        TextNote.Create(doc, schematicView.Id,
                            new XYZ(x - UnitUtils.ConvertToInternalUnits(0.45, UnitTypeId.Meters), yTop + UnitUtils.ConvertToInternalUnits(0.95, UnitTypeId.Meters), 0),
                            r.Name,
                            textType.Id);

                        // Bitola nominal por trecho vertical
                        foreach (var seg in r.Segments.OrderByDescending(s => s.ZMid))
                        {
                            double yLabel = UnitUtils.ConvertToInternalUnits(seg.ZMid, UnitTypeId.Meters);
                            TextNote.Create(doc, schematicView.Id,
                                new XYZ(x + UnitUtils.ConvertToInternalUnits(0.18, UnitTypeId.Meters), yLabel, 0),
                                $"DN {seg.DnMm} mm",
                                textType.Id);
                        }
                    }
                }

                tx.Commit();
            }

            var res = new VerticalRiserSchematicResult(
                viewName,
                groupedRisers.Count,
                levels.Count,
                $"Vista '{viewName}' gerada com {groupedRisers.Count} prumada(s) em {levels.Count} pavimento(s) (Escala 1:50).");

            return (res, schematicView!.Id);
        }

        private static readonly Dictionary<string, (double X, double Y)> _riserCoords = new();

        private static void TraverseConnectedElements(
            Element current,
            PipeNodeInfo originInfo,
            Dictionary<long, PipeNodeInfo> nodesById,
            Dictionary<long, HashSet<long>> adj,
            HashSet<long> visitedFixtures,
            Dictionary<string, int> fixtureCounts)
        {
            var conns = GetConnectors(current);
            if (conns == null) return;

            var localVisited = new HashSet<long> { current.Id.Value };
            var queue = new Queue<Element>();
            queue.Enqueue(current);

            while (queue.Count > 0)
            {
                var elem = queue.Dequeue();
                var elemConns = GetConnectors(elem);
                if (elemConns == null) continue;

                foreach (Connector c in elemConns)
                {
                    if (!c.IsConnected || c.Domain != Domain.DomainPiping) continue;
                    foreach (Connector refC in c.AllRefs)
                    {
                        Element owner = refC.Owner;
                        long ownerId = owner.Id.Value;
                        if (ownerId == originInfo.Id || !localVisited.Add(ownerId)) continue;

                        if (nodesById.ContainsKey(ownerId))
                        {
                            adj[originInfo.Id].Add(ownerId);
                            adj[ownerId].Add(originInfo.Id);
                        }
                        else if (owner is FamilyInstance fi)
                        {
                            if (fi.Category != null && fi.Category.Id.Value == (long)BuiltInCategory.OST_PlumbingFixtures)
                            {
                                if (visitedFixtures.Add(ownerId))
                                {
                                    var (uhc, minDn, isWc, keyName) = ClassifySanitaryFixture(fi);
                                    originInfo.DirectUhc += uhc;
                                    originInfo.DirectMinDnMm = Math.Max(originInfo.DirectMinDnMm, minDn);
                                    if (isWc) originInfo.HasWaterCloset = true;
                                    fixtureCounts[keyName] = fixtureCounts.TryGetValue(keyName, out int cnt) ? cnt + 1 : 1;
                                }
                            }
                            else
                            {
                                // Conexão (joelho, tê, redução, luva): continua atravessando até o próximo tubo ou aparelho
                                queue.Enqueue(fi);
                            }
                        }
                    }
                }
            }
        }

        private static (int Uhc, int MinDnMm, bool IsWaterCloset, string KeyName) ClassifySanitaryFixture(FamilyInstance fi)
        {
            string raw = Normalize($"{fi.Symbol?.FamilyName} {fi.Name}");
            if (raw.Contains("bacia") || raw.Contains("vaso") || raw.Contains("sanit") || raw.Contains("toilet") || raw.Contains("wc"))
                return (6, 100, true, "Bacia Sanitária");
            if (raw.Contains("cozinha") || raw.Contains("kitchen") || raw.Contains("gordura"))
                return (3, 50, false, "Pia de Cozinha");
            if (raw.Contains("maquina") && raw.Contains("roupa") || raw.Contains("washer") || raw.Contains("lavadora"))
                return (3, 50, false, "Máquina de Lavar Roupa");
            if (raw.Contains("tanque") || raw.Contains("laundry"))
                return (3, 40, false, "Tanque de Lavar");
            if (raw.Contains("chuveiro") || raw.Contains("ducha") || raw.Contains("shower"))
                return (2, 40, false, "Chuveiro");
            if (raw.Contains("banheira") || raw.Contains("bath") || raw.Contains("tub"))
                return (2, 40, false, "Banheira");
            if (raw.Contains("mictorio") || raw.Contains("urinal"))
                return (2, 40, false, "Mictório");
            return (1, 40, false, "Lavatório");
        }

        private static bool IsSanitaryOrVentPipe(Pipe p)
        {
            if (p.MEPSystem is PipingSystem ps)
            {
                if (ps.SystemType == PipeSystemType.Sanitary || ps.SystemType == PipeSystemType.Vent)
                    return true;
            }
            string sys = Normalize($"{p.MEPSystem?.Name} {p.PipeType?.Name}");
            return sys.Contains("esgoto") || sys.Contains("sanit") || sys.Contains("sewer") || sys.Contains("ventil") || sys.Contains("vent");
        }

        private static bool IsVentPipe(Pipe p)
        {
            if (p.MEPSystem is PipingSystem ps && ps.SystemType == PipeSystemType.Vent)
                return true;
            string sys = Normalize($"{p.MEPSystem?.Name} {p.PipeType?.Name}");
            return sys.Contains("ventil") || sys.Contains("vent");
        }

        private static string ClassifyDisciplineCode(Pipe p)
        {
            if (p.MEPSystem is PipingSystem ps)
            {
                if (ps.SystemType == PipeSystemType.Vent) return "CV";
                if (ps.SystemType == PipeSystemType.Sanitary) return "TQ";
                if (ps.SystemType == PipeSystemType.DomesticHotWater) return "AQ";
                if (ps.SystemType == PipeSystemType.DomesticColdWater) return "AF";
            }
            string sys = Normalize($"{p.MEPSystem?.Name} {p.PipeType?.Name}");
            if (sys.Contains("ventil") || sys.Contains("vent")) return "CV";
            if (sys.Contains("esgoto") || sys.Contains("sanit") || sys.Contains("sewer")) return "TQ";
            if (sys.Contains("quente") || sys.Contains("hot") || sys.Contains("ppr") || sys.Contains("cpvc")) return "AQ";
            return "AF";
        }

        private static (XYZ P1, XYZ P2) GetPipeEndpointsMeters(Pipe p)
        {
            if (p.Location is LocationCurve lc && lc.Curve != null)
            {
                XYZ a = lc.Curve.GetEndPoint(0);
                XYZ b = lc.Curve.GetEndPoint(1);
                return (
                    new XYZ(UnitUtils.ConvertFromInternalUnits(a.X, UnitTypeId.Meters), UnitUtils.ConvertFromInternalUnits(a.Y, UnitTypeId.Meters), UnitUtils.ConvertFromInternalUnits(a.Z, UnitTypeId.Meters)),
                    new XYZ(UnitUtils.ConvertFromInternalUnits(b.X, UnitTypeId.Meters), UnitUtils.ConvertFromInternalUnits(b.Y, UnitTypeId.Meters), UnitUtils.ConvertFromInternalUnits(b.Z, UnitTypeId.Meters))
                );
            }
            return (XYZ.Zero, XYZ.Zero);
        }

        private static ConnectorSet? GetConnectors(Element e)
        {
            if (e is MEPCurve mc) return mc.ConnectorManager?.Connectors;
            if (e is FamilyInstance fi) return fi.MEPModel?.ConnectorManager?.Connectors;
            return null;
        }

        private static double dx2(double dx, double dy, double dz) => dx * dx + dy * dy + dz * dz;

        private static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string formD = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char ch in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
    }
}
