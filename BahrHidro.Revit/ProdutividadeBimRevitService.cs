using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BahrHidro.Core.Productivity;

namespace BahrHidro.Revit
{
    public sealed class ProdutividadeBimExternalEventHandler : IExternalEventHandler
    {
        public ProdutividadeBimOpcoes OpcoesPendentes { get; set; } = new();

        public void Execute(UIApplication uiApp)
        {
            try
            {
                string resumo = ProdutividadeBimRevitService.ExecutarNoDocumento(uiApp, OpcoesPendentes);
                TaskDialog.Show("Smart Hidro — Produtividade Executiva BIM", resumo);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Smart Hidro — Aviso", $"Não foi possível concluir a operação:\n{ex.Message}");
            }
        }

        public string GetName() => "SmartHidro.ProdutividadeExecutivaBIM";
    }

    public static class ProdutividadeBimRevitService
    {
        private static ProdutividadeBimExternalEventHandler? _handler;
        private static ExternalEvent? _externalEvent;

        public static void Inicializar(UIControlledApplication application)
        {
            _handler = new ProdutividadeBimExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            ProdutividadeBimPlanner.ExecutarProdutividadeCallback = SolicitarExecucao;
        }

        public static string SolicitarExecucao(ProdutividadeBimOpcoes opcoes)
        {
            if (_handler == null || _externalEvent == null)
            {
                _handler = new ProdutividadeBimExternalEventHandler();
                _externalEvent = ExternalEvent.Create(_handler);
            }

            _handler.OpcoesPendentes = opcoes;
            var req = _externalEvent.Raise();
            return req == ExternalEventRequest.Accepted
                ? "Comandos de Produtividade BIM enviados ao Revit 2026! Aguarde o resumo na janela do Revit."
                : $"Solicitação enviada ao Revit (Status: {req}). Clique na janela do Revit para processar.";
        }

        public static string ExecutarNoDocumento(UIApplication uiApp, ProdutividadeBimOpcoes opcoes)
        {
            UIDocument? uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null)
                return "Nenhum projeto aberto no Revit.";

            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            int tubosCortados = 0;
            int luvasInseridas = 0;
            int luvasInvertidas = 0;
            int buchasLataoAplicadas = 0;
            int pecasNumeradas = 0;
            int colunasNumeradas = 0;
            int caixasNumeradas = 0;
            int tubosTracejados = 0;
            int paramsUnificados = 0;
            int ramaisGerados = 0;

            using var tx = new Transaction(doc, "Smart Hidro - Produtividade Executiva BIM");
            tx.Start();

            // ------------------------------------------------------------------
            // 1. CORTAR TUBOS PARA TAMANHO COMERCIAL + ADIÇÃO DE LUVAS
            //    (PVC 6m, Cobre 5m, CPVC/PPR 3m)
            // ------------------------------------------------------------------
            if (opcoes.CortarTubosComerciais)
            {
                var pipes = new FilteredElementCollector(doc)
                    .OfClass(typeof(Pipe))
                    .Cast<Pipe>()
                    .ToList();

                foreach (var pipe in pipes)
                {
                    if (pipe.Location is not LocationCurve lc) continue;
                    Curve curve = lc.Curve;
                    double lenFeet = curve.Length;
                    double lenMeters = lenFeet * 0.3048;

                    string typeName = doc.GetElement(pipe.GetTypeId())?.Name ?? pipe.Name ?? "";
                    var (barraM, _) = ProdutividadeBimPlanner.ObterBarraComercialMetros(typeName);

                    if (lenMeters <= barraM + 0.05) continue;

                    double barraFeet = barraM / 0.3048;
                    int numCortes = (int)Math.Floor((lenFeet - 0.05) / barraFeet);
                    if (numCortes <= 0) continue;

                    ElementId currentPipeId = pipe.Id;
                    XYZ pStart = curve.GetEndPoint(0);
                    XYZ dir = (curve.GetEndPoint(1) - pStart).Normalize();

                    for (int c = 1; c <= numCortes; c++)
                    {
                        XYZ ptBreak = pStart + dir.Multiply(barraFeet * c);
                        try
                        {
                            ElementId newPipeId = PlumbingUtils.BreakCurve(doc, currentPipeId, ptBreak);
                            if (newPipeId != ElementId.InvalidElementId)
                            {
                                tubosCortados++;
                                var p1 = doc.GetElement(currentPipeId) as Pipe;
                                var p2 = doc.GetElement(newPipeId) as Pipe;
                                if (p1 != null && p2 != null)
                                {
                                    var c1 = ObterConectorMaisProximo(p1.ConnectorManager, ptBreak);
                                    var c2 = ObterConectorMaisProximo(p2.ConnectorManager, ptBreak);
                                    if (c1 != null && c2 != null && !c1.IsConnected && !c2.IsConnected)
                                    {
                                        try
                                        {
                                            doc.Create.NewUnionFitting(c1, c2);
                                            luvasInseridas++;
                                        }
                                        catch { }
                                    }
                                }
                                currentPipeId = newPipeId;
                            }
                        }
                        catch { break; }
                    }
                }
                doc.Regenerate();
            }

            // ------------------------------------------------------------------
            // 2. INVERTER LUVAS DE ACORDO COM O SENTIDO DE FLUXO
            // ------------------------------------------------------------------
            if (opcoes.InverterLuvasFluxo)
            {
                var fittings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .ToList();

                foreach (var fi in fittings)
                {
                    string fam = (fi.Symbol?.FamilyName ?? fi.Name ?? "").ToLowerInvariant();
                    if (!fam.Contains("luva") && !fam.Contains("coupling") && !fam.Contains("uniao") && !fam.Contains("união"))
                        continue;

                    var cm = fi.MEPModel?.ConnectorManager;
                    if (cm == null) continue;

                    var conns = cm.Connectors.Cast<Connector>().ToList();
                    if (conns.Count != 2) continue;

                    // Verifica se o conector apontando para cima/montante está invertido em relação ao eixo Z/fluxo
                    XYZ v0 = conns[0].CoordinateSystem.BasisZ;
                    XYZ p0 = conns[0].Origin;
                    XYZ p1 = conns[1].Origin;

                    // Em trechos inclinados de esgoto, o fluxo vai da cota Z maior para a menor
                    if (Math.Abs(p0.Z - p1.Z) > 0.005)
                    {
                        XYZ fluxoDir = (p1 - p0).Normalize(); // p0 -> p1
                        if (p1.Z > p0.Z) fluxoDir = (p0 - p1).Normalize(); // maior Z -> menor Z

                        if (v0.DotProduct(fluxoDir) < -0.5)
                        {
                            XYZ mid = (p0 + p1) * 0.5;
                            XYZ perp = Math.Abs(fluxoDir.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
                            Line axis = Line.CreateBound(mid, mid + perp);
                            try
                            {
                                ElementTransformUtils.RotateElement(doc, fi.Id, axis, Math.PI);
                                luvasInvertidas++;
                            }
                            catch { }
                        }
                    }
                }
            }

            // ------------------------------------------------------------------
            // 3. ADIÇÃO / ESPECIFICAÇÃO DE BUCHA DE LATÃO NOS PONTOS DE UTILIZAÇÃO
            // ------------------------------------------------------------------
            if (opcoes.AdicionarBuchaLatao)
            {
                var buchaSymbol = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s =>
                        (s.FamilyName + " " + s.Name).ToLowerInvariant().Contains("bucha") &&
                        ((s.FamilyName + " " + s.Name).ToLowerInvariant().Contains("lat") ||
                         (s.FamilyName + " " + s.Name).ToLowerInvariant().Contains("lr") ||
                         (s.FamilyName + " " + s.Name).ToLowerInvariant().Contains("azul")));

                var fixtures = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .ToList();

                foreach (var fix in fixtures)
                {
                    var cm = fix.MEPModel?.ConnectorManager;
                    if (cm == null) continue;

                    foreach (Connector cFix in cm.Connectors)
                    {
                        if (cFix.Domain != Domain.DomainPiping) continue;
                        if (cFix.PipeSystemType != PipeSystemType.DomesticColdWater &&
                            cFix.PipeSystemType != PipeSystemType.DomesticHotWater)
                            continue;

                        if (!cFix.IsConnected) continue;

                        foreach (Connector refConn in cFix.AllRefs)
                        {
                            if (refConn.Owner.Id == fix.Id) continue;
                            Element owner = refConn.Owner;

                            // Se já existe uma conexão a montante (joelho/tê), aplica tipo com bucha de latão ou marca no parâmetro
                            if (owner is FamilyInstance fiConn &&
                                fiConn.Category?.Id.Value == (long)BuiltInCategory.OST_PipeFitting)
                            {
                                if (buchaSymbol != null && fiConn.Symbol.FamilyName == buchaSymbol.FamilyName)
                                {
                                    if (!buchaSymbol.IsActive) buchaSymbol.Activate();
                                    try { fiConn.Symbol = buchaSymbol; } catch { }
                                }
                                GravarComentarioOuMarca(fiConn, null, "CONEXÃO C/ BUCHA DE LATÃO (LR)");
                                buchasLataoAplicadas++;
                            }
                            else if (owner is Pipe pipeTerminal)
                            {
                                GravarComentarioOuMarca(pipeTerminal, null, "PONTO DE UTILIZAÇÃO C/ BUCHA DE LATÃO");
                                buchasLataoAplicadas++;
                            }
                        }
                    }
                }
            }

            // ------------------------------------------------------------------
            // 4. NUMERAR PEÇAS, COLUNAS E CAIXAS/POÇOS ('CX_')
            // ------------------------------------------------------------------
            if (opcoes.NumerarPecasEConexoes)
            {
                var pecas = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e =>
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeAccessory)
                    .OrderBy(e => (e.Location as LocationPoint)?.Point.Z ?? 0)
                    .ThenBy(e => (e.Location as LocationPoint)?.Point.X ?? 0)
                    .ToList();

                int seq = 1;
                foreach (var p in pecas)
                {
                    string fam = ((p as FamilyInstance)?.Symbol?.FamilyName ?? p.Name ?? "").ToLowerInvariant();
                    if (fam.StartsWith("cx_")) continue; // tratado na rotina de Caixas e Poços

                    GravarComentarioOuMarca(p, $"P-{seq:000}", null);
                    pecasNumeradas++;
                    seq++;
                }
            }

            if (opcoes.NumerarColunasVerticais)
            {
                var pipesVerticais = new FilteredElementCollector(doc)
                    .OfClass(typeof(Pipe))
                    .Cast<Pipe>()
                    .Where(p =>
                    {
                        if (p.Location is not LocationCurve lc) return false;
                        XYZ p0 = lc.Curve.GetEndPoint(0);
                        XYZ p1 = lc.Curve.GetEndPoint(1);
                        double dz = Math.Abs(p1.Z - p0.Z);
                        double dxy = Math.Sqrt(Math.Pow(p1.X - p0.X, 2) + Math.Pow(p1.Y - p0.Y, 2));
                        return dz > 2.0 && dxy < 0.15; // trecho vertical > ~0,60 m
                    })
                    .ToList();

                // Agrupa trechos verticais alinhados no mesmo eixo (X, Y) como uma mesma coluna
                var eixosColunas = new List<(double X, double Y, string Codigo)>();
                var contadoresPorPrefixo = new Dictionary<string, int>();

                foreach (var pv in pipesVerticais)
                {
                    var lc = (LocationCurve)pv.Location;
                    XYZ mid = lc.Curve.Evaluate(0.5, true);
                    string sysName = pv.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "";
                    string prefixo = ProdutividadeBimPlanner.ObterPrefixoColuna(sysName);

                    string? codigoExistente = null;
                    foreach (var eixo in eixosColunas)
                    {
                        if (Math.Sqrt(Math.Pow(mid.X - eixo.X, 2) + Math.Pow(mid.Y - eixo.Y, 2)) < 0.35 &&
                            eixo.Codigo.StartsWith(prefixo + "-"))
                        {
                            codigoExistente = eixo.Codigo;
                            break;
                        }
                    }

                    if (codigoExistente == null)
                    {
                        int prox = contadoresPorPrefixo.GetValueOrDefault(prefixo) + 1;
                        contadoresPorPrefixo[prefixo] = prox;
                        codigoExistente = $"{prefixo}-{prox:00}";
                        eixosColunas.Add((mid.X, mid.Y, codigoExistente));
                        colunasNumeradas++;
                    }

                    GravarComentarioOuMarca(pv, codigoExistente, $"Coluna Vertical {codigoExistente}");
                }
            }

            if (opcoes.NumerarCaixasEPocosCX)
            {
                var caixas = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi =>
                        (fi.Symbol?.FamilyName ?? "").StartsWith("CX_", StringComparison.OrdinalIgnoreCase) ||
                        (fi.Name ?? "").StartsWith("CX_", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(fi => (fi.Location as LocationPoint)?.Point.X ?? 0)
                    .ToList();

                int seqCx = 1;
                foreach (var cx in caixas)
                {
                    BoundingBoxXYZ? bb = cx.get_BoundingBox(null);
                    double cotaTampaM = bb != null ? bb.Max.Z * 0.3048 : 0.0;
                    double cotaFundoM = bb != null ? bb.Min.Z * 0.3048 : 0.0;
                    double profM = Math.Max(0.0, cotaTampaM - cotaFundoM);

                    string tagCx = $"CX-{seqCx:00}";
                    string infoCotas = $"{tagCx} | CT={cotaTampaM:0.00}m | CF={cotaFundoM:0.00}m | H={profM:0.00}m";
                    GravarComentarioOuMarca(cx, tagCx, infoCotas);
                    caixasNumeradas++;
                    seqCx++;
                }
            }

            // ------------------------------------------------------------------
            // 5. TRACEJAR TUBOS NA VISTA ATUAL
            // ------------------------------------------------------------------
            if (opcoes.TracejarTubosVistaAtual)
            {
                var dashedPattern = new FilteredElementCollector(doc)
                    .OfClass(typeof(LinePatternElement))
                    .Cast<LinePatternElement>()
                    .FirstOrDefault(lp =>
                        lp.Name.Contains("Tracej", StringComparison.OrdinalIgnoreCase) ||
                        lp.Name.Contains("Dash", StringComparison.OrdinalIgnoreCase) ||
                        lp.Name.Contains("Hidden", StringComparison.OrdinalIgnoreCase));

                if (dashedPattern != null)
                {
                    var viewPipes = new FilteredElementCollector(doc, activeView.Id)
                        .OfClass(typeof(Pipe))
                        .ToElements();

                    foreach (var vp in viewPipes)
                    {
                        var ogs = activeView.GetElementOverrides(vp.Id);
                        ogs.SetProjectionLinePatternId(dashedPattern.Id);
                        try
                        {
                            activeView.SetElementOverrides(vp.Id, ogs);
                            tubosTracejados++;
                        }
                        catch { }
                    }
                }
            }

            // ------------------------------------------------------------------
            // 6. UNIFICAR PARÂMETROS PARA TABELAS & QUANTITATIVOS
            // ------------------------------------------------------------------
            if (opcoes.UnificarParametrosETabelas)
            {
                var mepElems = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e =>
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeCurves ||
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeAccessory ||
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PlumbingFixtures)
                    .ToList();

                foreach (var el in mepElems)
                {
                    string sys = el.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "HID";
                    string size = el.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString()
                                  ?? el.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsValueString()
                                  ?? "";
                    string fam = (el as FamilyInstance)?.Symbol?.FamilyName ?? el.Name ?? "Elemento";
                    string resumoUnificado = $"{sys} | {fam} {size}".Trim();

                    Parameter? pDesc = el.LookupParameter("SH_Descricao_Unificada")
                                       ?? el.LookupParameter("Descrição Unificada")
                                       ?? el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);

                    if (pDesc != null && !pDesc.IsReadOnly && string.IsNullOrWhiteSpace(pDesc.AsString()))
                    {
                        try
                        {
                            pDesc.Set(resumoUnificado);
                            paramsUnificados++;
                        }
                        catch { }
                    }
                }
            }

            // ------------------------------------------------------------------
            // 7. GERAR RAMAIS POR AMBIENTE (TRAÇADO AUTOMÁTICO BÁSICO)
            // ------------------------------------------------------------------
            if (opcoes.GerarRamaisPorAmbiente)
            {
                var pipeType = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipeType))
                    .Cast<PipeType>()
                    .FirstOrDefault();

                var coldSysType = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystemType))
                    .Cast<PipingSystemType>()
                    .FirstOrDefault(st => st.SystemClassification == MEPSystemClassification.DomesticColdWater);

                var level = activeView.GenLevel ?? new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .FirstOrDefault();

                if (pipeType != null && coldSysType != null && level != null)
                {
                    var rooms = new FilteredElementCollector(doc, activeView.Id)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => r.Area > 0)
                        .ToList();

                    var fixtures = new FilteredElementCollector(doc, activeView.Id)
                        .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                        .WhereElementIsNotElementType()
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .ToList();

                    foreach (var room in rooms.Take(3))
                    {
                        var pontosAmbiente = new List<XYZ>();
                        foreach (var fix in fixtures)
                        {
                            if (fix.Location is not LocationPoint lp) continue;
                            if (!room.IsPointInRoom(lp.Point)) continue;

                            var cm = fix.MEPModel?.ConnectorManager;
                            if (cm == null) continue;
                            foreach (Connector c in cm.Connectors)
                            {
                                if (c.Domain == Domain.DomainPiping &&
                                    c.PipeSystemType == PipeSystemType.DomesticColdWater &&
                                    !c.IsConnected)
                                {
                                    pontosAmbiente.Add(c.Origin);
                                }
                            }
                        }

                        if (pontosAmbiente.Count >= 2)
                        {
                            // Cria barrilete/ramal distribuidor horizontal ligando os pontos do ambiente
                            double zBarrilete = level.Elevation + (2.40 / 0.3048); // H = 2,40 m
                            var ordenados = pontosAmbiente.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();

                            for (int idx = 0; idx < ordenados.Count; idx++)
                            {
                                XYZ ptPonto = ordenados[idx];
                                XYZ ptTopo = new XYZ(ptPonto.X, ptPonto.Y, zBarrilete);
                                try
                                {
                                    Pipe.Create(doc, coldSysType.Id, pipeType.Id, level.Id, ptTopo, ptPonto);
                                    ramaisGerados++;
                                }
                                catch { }

                                if (idx > 0)
                                {
                                    XYZ ptAntTopo = new XYZ(ordenados[idx - 1].X, ordenados[idx - 1].Y, zBarrilete);
                                    if (ptAntTopo.DistanceTo(ptTopo) > 0.15)
                                    {
                                        try
                                        {
                                            Pipe.Create(doc, coldSysType.Id, pipeType.Id, level.Id, ptAntTopo, ptTopo);
                                            ramaisGerados++;
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            tx.Commit();

            return
                $"Execução concluída no modelo Revit 2026:\n" +
                $"• Tubos cortados em barras comerciais: {tubosCortados} cortes ({luvasInseridas} luvas inseridas)\n" +
                $"• Luvas invertidas pelo sentido do fluxo: {luvasInvertidas}\n" +
                $"• Pontos com Bucha de Latão aplicada/especificada: {buchasLataoAplicadas}\n" +
                $"• Numeração automática: {pecasNumeradas} peças | {colunasNumeradas} colunas | {caixasNumeradas} caixas (CX_)\n" +
                $"• Tubos tracejados na vista: {tubosTracejados} | Parâmetros unificados: {paramsUnificados}\n" +
                $"• Trechos de ramais gerados por ambiente: {ramaisGerados}";
        }

        private static Connector? ObterConectorMaisProximo(ConnectorManager? cm, XYZ alvo)
        {
            if (cm == null) return null;
            return cm.Connectors
                .Cast<Connector>()
                .OrderBy(c => c.Origin.DistanceTo(alvo))
                .FirstOrDefault();
        }

        private static void GravarComentarioOuMarca(Element el, string? marca, string? comentario)
        {
            if (!string.IsNullOrWhiteSpace(marca))
            {
                Parameter? pMark = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (pMark != null && !pMark.IsReadOnly)
                {
                    try { pMark.Set(marca); } catch { }
                }
            }

            if (!string.IsNullOrWhiteSpace(comentario))
            {
                Parameter? pComm = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (pComm != null && !pComm.IsReadOnly)
                {
                    try { pComm.Set(comentario); } catch { }
                }
            }
        }
    }
}
