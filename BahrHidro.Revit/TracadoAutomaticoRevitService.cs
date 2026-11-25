using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BahrHidro.Core.Routing;

namespace BahrHidro.Revit
{
    public sealed class Tracado3DExternalEventHandler : IExternalEventHandler
    {
        public IList<PontoTracadoItem> Pontos { get; set; } = new List<PontoTracadoItem>();
        public bool ModoDetectarAguaFria { get; set; } = false;

        public void Execute(UIApplication uiApp)
        {
            try
            {
                if (ModoDetectarAguaFria)
                {
                    string resDet = TracadoAutomaticoRevitService.ExecutarDeteccaoAguaFria(uiApp);
                    TaskDialog.Show("Smart Hidro — Detecção de Rede Água Fria", resDet);
                }
                else
                {
                    string resTrac = TracadoAutomaticoRevitService.ExecutarGeracaoTracado3D(uiApp, Pontos);
                    TaskDialog.Show("Smart Hidro — Traçado Automático 3D", resTrac);
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Smart Hidro — Aviso", $"Falha na operação:\n{ex.Message}");
            }
        }

        public string GetName() => "SmartHidro.Tracado3DeDeteccaoAF";
    }

    public static class TracadoAutomaticoRevitService
    {
        private static Tracado3DExternalEventHandler? _handler;
        private static ExternalEvent? _externalEvent;

        private sealed class PontoAlinhadoParede
        {
            public XYZ PontoParedeXY { get; set; } = XYZ.Zero;
            public double ZConsumo { get; set; }
            public string ParedeIdChave { get; set; } = "LIVRE";
            public XYZ DirecaoParede { get; set; } = XYZ.BasisX;
            public string Rotulo { get; set; } = string.Empty;
        }

        public static void Inicializar(UIControlledApplication application)
        {
            _handler = new Tracado3DExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            TracadoAutomaticoPlanner.GerarTracado3DCallback = SolicitarTracado3D;
            TracadoAutomaticoPlanner.DetectarAguaFriaCallback = SolicitarDeteccaoAguaFria;
        }

        public static string SolicitarTracado3D(IList<PontoTracadoItem> pontos)
        {
            if (_handler == null || _externalEvent == null)
            {
                _handler = new Tracado3DExternalEventHandler();
                _externalEvent = ExternalEvent.Create(_handler);
            }
            _handler.ModoDetectarAguaFria = false;
            _handler.Pontos = pontos ?? new List<PontoTracadoItem>();
            var req = _externalEvent.Raise();
            return req == ExternalEventRequest.Accepted
                ? "Comando de Traçado 3D enviado ao Revit 2026! Se nenhum aparelho estiver selecionado, selecione-os na tela do Revit."
                : $"Solicitação enviada ao Revit (Status: {req}).";
        }

        public static string SolicitarDeteccaoAguaFria()
        {
            if (_handler == null || _externalEvent == null)
            {
                _handler = new Tracado3DExternalEventHandler();
                _externalEvent = ExternalEvent.Create(_handler);
            }
            _handler.ModoDetectarAguaFria = true;
            var req = _externalEvent.Raise();
            return req == ExternalEventRequest.Accepted
                ? "Varrendo tubulações de Água Fria no modelo Revit 2026..."
                : $"Solicitação enviada ao Revit (Status: {req}).";
        }

        public static string ExecutarGeracaoTracado3D(UIApplication uiApp, IList<PontoTracadoItem> pontosInformados)
        {
            UIDocument? uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null)
                return "Nenhum documento aberto no Revit.";

            Document doc = uidoc.Document;

            // 1. Coletar aparelhos selecionados ou informados
            var elementosAlvo = new List<(Element Elem, double AlturaM, string Rotulo)>();

            if (pontosInformados != null && pontosInformados.Count > 0)
            {
                foreach (var p in pontosInformados)
                {
                    if (p.ElementIdValue <= 0) continue;
                    var el = doc.GetElement(new ElementId(p.ElementIdValue));
                    if (el != null)
                    {
                        double alt = p.AlturaPontoMetros > 0
                            ? p.AlturaPontoMetros
                            : TracadoAutomaticoPlanner.ObterAlturaNormativaMetros(p.TipoAparelho);
                        elementosAlvo.Add((el, alt, p.TipoAparelho));
                    }
                }
            }

            if (elementosAlvo.Count == 0)
            {
                foreach (var id in uidoc.Selection.GetElementIds())
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    string nome = ((el as FamilyInstance)?.Symbol?.FamilyName ?? "") + " " + (el.Name ?? "");
                    double alt = TracadoAutomaticoPlanner.ObterAlturaNormativaMetros(nome);
                    elementosAlvo.Add((el, alt, nome.Trim()));
                }
            }

            if (elementosAlvo.Count == 0)
            {
                try
                {
                    var refs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        "Selecione os aparelhos hidráulicos (Vaso, Chuveiro, Pia/Lavatório) e clique em Concluir.");

                    foreach (var r in refs)
                    {
                        var el = doc.GetElement(r.ElementId);
                        if (el == null) continue;
                        string nome = ((el as FamilyInstance)?.Symbol?.FamilyName ?? "") + " " + (el.Name ?? "");
                        double alt = TracadoAutomaticoPlanner.ObterAlturaNormativaMetros(nome);
                        elementosAlvo.Add((el, alt, nome.Trim()));
                    }
                }
                catch
                {
                    return "Seleção cancelada. Selecione os aparelhos no Revit antes de clicar em Gerar Traçado.";
                }
            }

            if (elementosAlvo.Count == 0)
                return "Nenhum aparelho válido encontrado para gerar o traçado 3D.";

            // 2. Localizar PipeType com Preferências de Roteamento (joelho/tê) válidas
            var pipeTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .ToList();

            var pipeType = pipeTypes.FirstOrDefault(pt =>
                pt.RoutingPreferenceManager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows) > 0 &&
                pt.RoutingPreferenceManager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) > 0)
                ?? pipeTypes.FirstOrDefault();

            if (pipeType == null)
                return "Nenhum Tipo de Tubo (PipeType) encontrado no template. Carregue uma família de tubo PVC antes de gerar o traçado.";

            var coldSys = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .FirstOrDefault(st =>
                    st.SystemClassification == MEPSystemClassification.DomesticColdWater ||
                    st.Name.Contains("Fria", StringComparison.OrdinalIgnoreCase) ||
                    st.Name.Contains("Cold", StringComparison.OrdinalIgnoreCase))
                ?? new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault();

            if (coldSys == null)
                return "Nenhum Sistema de Tubulação (PipingSystemType) encontrado no template.";

            var level = doc.ActiveView?.GenLevel ?? new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

            if (level == null)
                return "Nenhum Nível (Level) encontrado no projeto.";

            // 3. Coletar todas as paredes (do modelo atual e de vínculos RVT) para alinhar os tubos no eixo da parede
            var paredesDisponiveis = ColetarCurvasParedes(doc);

            var pontosAlinhados = new List<PontoAlinhadoParede>();
            foreach (var (el, altM, rotulo) in elementosAlvo)
            {
                var pAlinhado = ProjetarNoEixoDaParede(el, paredesDisponiveis, level.Elevation, altM, rotulo);
                if (pAlinhado != null)
                    pontosAlinhados.Add(pAlinhado);
            }

            if (pontosAlinhados.Count == 0)
                return "Não foi possível obter as coordenadas dos aparelhos selecionados.";

            // 4. Agrupar por parede para evitar zigue-zague entre paredes diferentes
            var gruposPorParede = pontosAlinhados
                .GroupBy(p => p.ParedeIdChave)
                .ToList();

            double zBarrilete = Math.Max(
                level.Elevation + (2.35 / 0.3048),
                pontosAlinhados.Max(p => p.ZConsumo) + (0.25 / 0.3048));

            double diamBarriletePes = 32.0 / 304.8; // DN 32 mm
            double diamSubRamalPes = 25.0 / 304.8; // DN 25 mm
            double distMinimaTrecho = 0.25;        // ~7,6 cm (evita micro-tubos que falham conexão)

            int tubosCriados = 0;
            int conexoesCriadas = 0;
            var pontasPorGrupo = new List<(XYZ Inicio, XYZ Fim, Pipe TuboInicio, Pipe TuboFim)>();

            using var tx = new Transaction(doc, "Smart Hidro - Gerar Traçado 3D Alinhado à Parede");
            tx.Start();

            foreach (var grupo in gruposPorParede)
            {
                var itensGrupo = grupo.ToList();
                XYZ dir = itensGrupo[0].DirecaoParede;

                // Ordena os aparelhos sequencialmente ao longo da parede (sem ir e voltar)
                var ordenadosNaParede = itensGrupo
                    .OrderBy(p => p.PontoParedeXY.X * dir.X + p.PontoParedeXY.Y * dir.Y)
                    .ToList();

                // Remove duplicatas muito próximas (< 12 cm) no mesmo eixo
                var filtrados = new List<PontoAlinhadoParede>();
                foreach (var item in ordenadosNaParede)
                {
                    if (filtrados.Count == 0 ||
                        filtrados.Last().PontoParedeXY.DistanceTo(item.PontoParedeXY) >= 0.40)
                    {
                        filtrados.Add(item);
                    }
                }

                var descidas = new List<(XYZ Topo, Pipe SubRamal)>();
                foreach (var pt in filtrados)
                {
                    XYZ pBase = new XYZ(pt.PontoParedeXY.X, pt.PontoParedeXY.Y, pt.ZConsumo);
                    XYZ pTopo = new XYZ(pt.PontoParedeXY.X, pt.PontoParedeXY.Y, zBarrilete);
                    if (pBase.DistanceTo(pTopo) < distMinimaTrecho) continue;

                    try
                    {
                        Pipe sub = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, pTopo, pBase);
                        sub.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamSubRamalPes);
                        tubosCriados++;
                        descidas.Add((pTopo, sub));
                    }
                    catch { }
                }

                // Cria o trecho horizontal reto dentro da mesma parede ligando as descidas
                var horizontaisParede = new List<Pipe>();
                for (int i = 0; i < descidas.Count - 1; i++)
                {
                    XYZ a = descidas[i].Topo;
                    XYZ b = descidas[i + 1].Topo;
                    if (a.DistanceTo(b) < distMinimaTrecho) continue;

                    try
                    {
                        Pipe pHoriz = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, a, b);
                        pHoriz.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamBarriletePes);
                        tubosCriados++;
                        horizontaisParede.Add(pHoriz);
                    }
                    catch { }
                }

                // Insere joelhos nas pontas e tês nos pontos intermediários dentro da parede
                for (int i = 0; i < descidas.Count; i++)
                {
                    XYZ no = descidas[i].Topo;
                    var cVert = ObterConectorProximo(descidas[i].SubRamal, no);
                    if (cVert == null || cVert.IsConnected) continue;

                    var hConectados = horizontaisParede
                        .Where(h => h.Location is LocationCurve lc &&
                                    (lc.Curve.GetEndPoint(0).DistanceTo(no) < 0.03 ||
                                     lc.Curve.GetEndPoint(1).DistanceTo(no) < 0.03))
                        .ToList();

                    if (hConectados.Count == 1)
                    {
                        var cH = ObterConectorProximo(hConectados[0], no);
                        if (cH != null && !cH.IsConnected)
                        {
                            try { doc.Create.NewElbowFitting(cVert, cH); conexoesCriadas++; } catch { }
                        }
                    }
                    else if (hConectados.Count == 2)
                    {
                        var cH1 = ObterConectorProximo(hConectados[0], no);
                        var cH2 = ObterConectorProximo(hConectados[1], no);
                        if (cH1 != null && cH2 != null && !cH1.IsConnected && !cH2.IsConnected)
                        {
                            try { doc.Create.NewTeeFitting(cH1, cH2, cVert); conexoesCriadas++; } catch { }
                        }
                    }
                }

                if (horizontaisParede.Count > 0)
                {
                    var hIni = horizontaisParede.First();
                    var hFim = horizontaisParede.Last();
                    XYZ pIni = (hIni.Location as LocationCurve)!.Curve.GetEndPoint(0);
                    XYZ pFim = (hFim.Location as LocationCurve)!.Curve.GetEndPoint(1);
                    pontasPorGrupo.Add((pIni, pFim, hIni, hFim));
                }
                else if (descidas.Count == 1)
                {
                    pontasPorGrupo.Add((descidas[0].Topo, descidas[0].Topo, descidas[0].SubRamal, descidas[0].SubRamal));
                }
            }

            // 5. Se houver 2 paredes distintas (ex.: banheiro em L), interliga as pontas mais próximas com no máximo 1 canto de 90°
            if (pontasPorGrupo.Count >= 2)
            {
                for (int g = 0; g < pontasPorGrupo.Count - 1; g++)
                {
                    var gA = pontasPorGrupo[g];
                    var gB = pontasPorGrupo[g + 1];

                    // Escolhe o par de extremidades mais próximo entre as duas paredes
                    var pares = new[]
                    {
                        (PtA: gA.Inicio, TuboA: gA.TuboInicio, PtB: gB.Inicio, TuboB: gB.TuboInicio),
                        (PtA: gA.Inicio, TuboA: gA.TuboInicio, PtB: gB.Fim,    TuboB: gB.TuboFim),
                        (PtA: gA.Fim,    TuboA: gA.TuboFim,    PtB: gB.Inicio, TuboB: gB.TuboInicio),
                        (PtA: gA.Fim,    TuboA: gA.TuboFim,    PtB: gB.Fim,    TuboB: gB.TuboFim)
                    };

                    var melhor = pares.OrderBy(x => x.PtA.DistanceTo(x.PtB)).First();
                    if (melhor.PtA.DistanceTo(melhor.PtB) < distMinimaTrecho || melhor.PtA.DistanceTo(melhor.PtB) > 15.0)
                        continue;

                    XYZ canto = new XYZ(melhor.PtB.X, melhor.PtA.Y, zBarrilete);
                    if (melhor.PtA.DistanceTo(canto) > distMinimaTrecho && canto.DistanceTo(melhor.PtB) > distMinimaTrecho)
                    {
                        try
                        {
                            Pipe pL1 = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, melhor.PtA, canto);
                            Pipe pL2 = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, canto, melhor.PtB);
                            pL1.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamBarriletePes);
                            pL2.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamBarriletePes);
                            tubosCriados += 2;

                            var c1 = ObterConectorProximo(pL1, canto);
                            var c2 = ObterConectorProximo(pL2, canto);
                            if (c1 != null && c2 != null)
                            {
                                doc.Create.NewElbowFitting(c1, c2);
                                conexoesCriadas++;
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        try
                        {
                            Pipe pDir = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, melhor.PtA, melhor.PtB);
                            pDir.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamBarriletePes);
                            tubosCriados++;
                        }
                        catch { }
                    }
                }
            }

            tx.Commit();

            return
                $"Traçado 3D alinhado às paredes concluído!\n\n" +
                $"• Paredes atendidas: {gruposPorParede.Count}\n" +
                $"• Aparelhos atendidos: {pontosAlinhados.Count}\n" +
                $"• Trechos de tubo criados no eixo da parede: {tubosCriados}\n" +
                $"• Conexões (Joelhos/Tês) aplicadas: {conexoesCriadas}";
        }

        private static List<(string Chave, Line LinhaXY)> ColetarCurvasParedes(Document doc)
        {
            var resultado = new List<(string Chave, Line LinhaXY)>();

            // Paredes do modelo hospedeiro
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .ToList();

            foreach (var w in walls)
            {
                if (w.Location is LocationCurve lc && lc.Curve is Line line)
                {
                    XYZ p0 = new XYZ(line.GetEndPoint(0).X, line.GetEndPoint(0).Y, 0);
                    XYZ p1 = new XYZ(line.GetEndPoint(1).X, line.GetEndPoint(1).Y, 0);
                    if (p0.DistanceTo(p1) > 0.1)
                        resultado.Add(($"HOST_{w.Id.Value}", Line.CreateBound(p0, p1)));
                }
            }

            // Paredes de vínculos arquitetônicos (RevitLinkInstance)
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                Transform tf = link.GetTotalTransform();

                var linkWalls = new FilteredElementCollector(linkDoc)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .ToList();

                foreach (var lw in linkWalls)
                {
                    if (lw.Location is LocationCurve lc && lc.Curve is Line line)
                    {
                        XYZ g0 = tf.OfPoint(line.GetEndPoint(0));
                        XYZ g1 = tf.OfPoint(line.GetEndPoint(1));
                        XYZ p0 = new XYZ(g0.X, g0.Y, 0);
                        XYZ p1 = new XYZ(g1.X, g1.Y, 0);
                        if (p0.DistanceTo(p1) > 0.1)
                            resultado.Add(($"LINK_{link.Id.Value}_{lw.Id.Value}", Line.CreateBound(p0, p1)));
                    }
                }
            }

            return resultado;
        }

        private static PontoAlinhadoParede? ProjetarNoEixoDaParede(
            Element el,
            List<(string Chave, Line LinhaXY)> paredes,
            double levelElevationPes,
            double alturaNormativaM,
            string rotulo)
        {
            double zAlvo = levelElevationPes + (alturaNormativaM / 0.3048);
            XYZ? ptOrigem = null;

            if (el.Location is LocationPoint lp)
                ptOrigem = lp.Point;
            else if (el.get_BoundingBox(null) is BoundingBoxXYZ bb)
                ptOrigem = (bb.Min + bb.Max) * 0.5;

            if (ptOrigem == null) return null;

            XYZ ptXY = new XYZ(ptOrigem.X, ptOrigem.Y, 0);

            // 1. Se a família estiver hospedada diretamente em uma Wall, projeta no eixo dela
            if (el is FamilyInstance fi && fi.Host is Wall hostWall &&
                hostWall.Location is LocationCurve hostLc && hostLc.Curve is Line hostLine)
            {
                XYZ h0 = new XYZ(hostLine.GetEndPoint(0).X, hostLine.GetEndPoint(0).Y, 0);
                XYZ h1 = new XYZ(hostLine.GetEndPoint(1).X, hostLine.GetEndPoint(1).Y, 0);
                var boundHost = Line.CreateBound(h0, h1);
                var proj = boundHost.Project(ptXY);
                if (proj != null)
                {
                    return new PontoAlinhadoParede
                    {
                        PontoParedeXY = proj.XYZPoint,
                        ZConsumo = zAlvo,
                        ParedeIdChave = $"HOST_{hostWall.Id.Value}",
                        DirecaoParede = boundHost.Direction,
                        Rotulo = rotulo
                    };
                }
            }

            // 2. Caso contrário (ou se a parede estiver em um vínculo RVT), busca a parede mais próxima até 1,20 m (4 pés)
            string melhorChave = "LIVRE";
            XYZ melhorPonto = ptXY;
            XYZ melhorDir = XYZ.BasisX;
            double menorDist = 4.0; // 1,22 metros

            foreach (var (chave, linha) in paredes)
            {
                var proj = linha.Project(ptXY);
                if (proj != null && proj.Distance < menorDist)
                {
                    menorDist = proj.Distance;
                    melhorPonto = proj.XYZPoint;
                    melhorChave = chave;
                    melhorDir = linha.Direction;
                }
            }

            return new PontoAlinhadoParede
            {
                PontoParedeXY = melhorPonto,
                ZConsumo = zAlvo,
                ParedeIdChave = melhorChave,
                DirecaoParede = melhorDir,
                Rotulo = rotulo
            };
        }

        public static string ExecutarDeteccaoAguaFria(UIApplication uiApp)
        {
            UIDocument? uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null)
                return "Nenhum documento aberto no Revit.";

            Document doc = uidoc.Document;

            var todosTubos = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .Cast<Pipe>()
                .ToList();

            var tubosAF = new List<Pipe>();
            foreach (var p in todosTubos)
            {
                var sysType = doc.GetElement(p.GetTypeId()) as PipeType;
                string pipSysName = p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "";
                string sysClass = p.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString()
                                  ?? p.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsValueString()
                                  ?? "";
                string combined = $"{pipSysName} {sysClass} {sysType?.Name}".ToLowerInvariant();

                bool ehAguaFria =
                    combined.Contains("domestic cold") ||
                    combined.Contains("cold water") ||
                    combined.Contains("agua fria") ||
                    combined.Contains("água fria") ||
                    combined.Contains("af_") ||
                    combined.Contains("af ") ||
                    combined.EndsWith(" af");

                bool ehOutroSistema =
                    combined.Contains("sanit") || combined.Contains("esgoto") ||
                    combined.Contains("pluvial") || combined.Contains("storm") ||
                    combined.Contains("quente") || combined.Contains("hot") ||
                    combined.Contains("ventil");

                if (ehAguaFria || (!ehOutroSistema && todosTubos.Count > 0))
                    tubosAF.Add(p);
            }

            if (tubosAF.Count == 0)
            {
                return
                    $"Foram encontrados {todosTubos.Count} tubo(s) no arquivo, mas nenhum está no sistema de Água Fria.\n\n" +
                    "Verifique se os tubos não estão dentro de um vínculo RVT (Link) ou se o 'System Type' está definido como Água Fria / Domestic Cold Water.";
            }

            double metragemTotal = tubosAF.Sum(p => (p.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0.0) * 0.3048);

            try { uidoc.Selection.SetElementIds(tubosAF.Select(p => p.Id).ToList()); } catch { }

            return
                $"Rede de Água Fria detectada e selecionada no modelo!\n\n" +
                $"• Trechos encontrados: {tubosAF.Count}\n" +
                $"• Comprimento total de tubulação: {metragemTotal:0.00} m";
        }

        private static Connector? ObterConectorProximo(Pipe pipe, XYZ alvo)
        {
            return pipe.ConnectorManager?.Connectors
                .Cast<Connector>()
                .OrderBy(c => c.Origin.DistanceTo(alvo))
                .FirstOrDefault();
        }
    }
}
