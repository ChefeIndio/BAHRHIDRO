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
                ? "Comando de Traçado 3D enviado ao Revit 2026! Se nenhum aparelho estiver selecionado, o Revit pedirá para você selecionar."
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

            // 1. Obter os aparelhos a partir da lista informada OU da seleção atual do usuário no Revit
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
                var selIds = uidoc.Selection.GetElementIds();
                foreach (var id in selIds)
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    string nome = ((el as FamilyInstance)?.Symbol?.FamilyName ?? "") + " " + (el.Name ?? "");
                    double alt = TracadoAutomaticoPlanner.ObterAlturaNormativaMetros(nome);
                    elementosAlvo.Add((el, alt, nome.Trim()));
                }
            }

            // Se ainda não há nada selecionado, solicita seleção interativa na tela do Revit
            if (elementosAlvo.Count == 0)
            {
                try
                {
                    var refs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        "Selecione os aparelhos hidráulicos (Vaso, Chuveiro, Pia/Lavatório) e clique em Concluir na barra superior.");

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
                    return "Seleção cancelada pelo usuário. Selecione os aparelhos no Revit antes de clicar em Gerar Traçado.";
                }
            }

            if (elementosAlvo.Count == 0)
                return "Nenhum aparelho válido encontrado para gerar o traçado 3D.";

            // 2. Garantir PipeType e PipingSystemType de Água Fria no template
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault();

            if (pipeType == null)
                return "Nenhum Tipo de Tubo (PipeType) encontrado neste template do Revit. Carregue uma família de tubo PVC antes de gerar o traçado.";

            var coldSys = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .FirstOrDefault(st =>
                    st.SystemClassification == MEPSystemClassification.DomesticColdWater ||
                    st.Name.Contains("Fria", StringComparison.OrdinalIgnoreCase) ||
                    st.Name.Contains("Cold", StringComparison.OrdinalIgnoreCase));

            if (coldSys == null)
            {
                coldSys = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystemType))
                    .Cast<PipingSystemType>()
                    .FirstOrDefault();
            }

            if (coldSys == null)
                return "Nenhum Sistema de Tubulação (PipingSystemType) encontrado no template.";

            var level = doc.ActiveView?.GenLevel ?? new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

            if (level == null)
                return "Nenhum Nível (Level) encontrado no projeto.";

            // 3. Extrair coordenadas 3D reais de cada aparelho (suporta conector MEP ou família arquitetônica)
            var pontos3D = new List<(XYZ PontoConsumo, string Rotulo)>();
            foreach (var (el, altM, rotulo) in elementosAlvo)
            {
                XYZ? pt = ObterPontoHidraulicoElemento(el, level.Elevation, altM);
                if (pt != null)
                    pontos3D.Add((pt, rotulo));
            }

            if (pontos3D.Count == 0)
                return "Não foi possível determinar as coordenadas 3D dos elementos selecionados.";

            // Ordena ao longo do eixo principal do ambiente para formar um barrilete contínuo sem cruzamentos
            double spanX = pontos3D.Max(p => p.PontoConsumo.X) - pontos3D.Min(p => p.PontoConsumo.X);
            double spanY = pontos3D.Max(p => p.PontoConsumo.Y) - pontos3D.Min(p => p.PontoConsumo.Y);
            var ordenados = spanX >= spanY
                ? pontos3D.OrderBy(p => p.PontoConsumo.X).ToList()
                : pontos3D.OrderBy(p => p.PontoConsumo.Y).ToList();

            double zBarrilete = Math.Max(
                level.Elevation + (2.35 / 0.3048),
                ordenados.Max(p => p.PontoConsumo.Z) + (0.25 / 0.3048));

            int tubosCriados = 0;
            int conexoesCriadas = 0;
            double diamBarriletePes = 32.0 / 304.8; // DN 32 mm
            double diamSubRamalPes = 25.0 / 304.8; // DN 25 mm

            using var tx = new Transaction(doc, "Smart Hidro - Gerar Traçado 3D Água Fria");
            tx.Start();

            var toposSubRamais = new List<(XYZ Topo, Pipe SubRamal)>();

            // A) Cria descidas verticais até cada aparelho (Vaso 0,20m, Lavatório 0,60m, Chuveiro 2,10m)
            foreach (var (ptBase, _) in ordenados)
            {
                XYZ ptTopo = new XYZ(ptBase.X, ptBase.Y, zBarrilete);
                if (ptTopo.DistanceTo(ptBase) < 0.10) continue;

                try
                {
                    Pipe subRamal = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, ptTopo, ptBase);
                    subRamal.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamSubRamalPes);
                    tubosCriados++;
                    toposSubRamais.Add((ptTopo, subRamal));
                }
                catch { }
            }

            // B) Interliga os topos das descidas com o barrilete superior (ou ramal em L) + conexões
            var trechosBarrilete = new List<Pipe>();
            for (int i = 0; i < toposSubRamais.Count - 1; i++)
            {
                XYZ a = toposSubRamais[i].Topo;
                XYZ b = toposSubRamais[i + 1].Topo;

                // Se estiverem alinhados em X ou Y, liga direto; se estiverem em paredes perpendiculares, faz canto em L (90°)
                if (Math.Abs(a.X - b.X) < 0.15 || Math.Abs(a.Y - b.Y) < 0.15)
                {
                    if (a.DistanceTo(b) > 0.10)
                    {
                        try
                        {
                            Pipe pHoriz = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, a, b);
                            pHoriz.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamBarriletePes);
                            tubosCriados++;
                            trechosBarrilete.Add(pHoriz);
                        }
                        catch { }
                    }
                }
                else
                {
                    XYZ canto = new XYZ(b.X, a.Y, zBarrilete);
                    try
                    {
                        Pipe p1 = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, a, canto);
                        Pipe p2 = Pipe.Create(doc, coldSys.Id, pipeType.Id, level.Id, canto, b);
                        p1.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamBarriletePes);
                        p2.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diamBarriletePes);
                        tubosCriados += 2;
                        trechosBarrilete.Add(p1);
                        trechosBarrilete.Add(p2);

                        var c1 = ObterConectorProximo(p1, canto);
                        var c2 = ObterConectorProximo(p2, canto);
                        if (c1 != null && c2 != null)
                        {
                            doc.Create.NewElbowFitting(c1, c2);
                            conexoesCriadas++;
                        }
                    }
                    catch { }
                }
            }

            // C) Tenta conectar joelhos nas extremidades e tês nos pontos intermediários do barrilete
            for (int i = 0; i < toposSubRamais.Count; i++)
            {
                XYZ ptNo = toposSubRamais[i].Topo;
                Pipe pVert = toposSubRamais[i].SubRamal;
                var cVert = ObterConectorProximo(pVert, ptNo);
                if (cVert == null || cVert.IsConnected) continue;

                var barriletesNoNo = trechosBarrilete
                    .Where(pb =>
                    {
                        if (pb.Location is not LocationCurve lc) return false;
                        return lc.Curve.GetEndPoint(0).DistanceTo(ptNo) < 0.05 ||
                               lc.Curve.GetEndPoint(1).DistanceTo(ptNo) < 0.05;
                    })
                    .ToList();

                if (barriletesNoNo.Count == 1)
                {
                    var cHor = ObterConectorProximo(barriletesNoNo[0], ptNo);
                    if (cHor != null && !cHor.IsConnected)
                    {
                        try { doc.Create.NewElbowFitting(cVert, cHor); conexoesCriadas++; } catch { }
                    }
                }
                else if (barriletesNoNo.Count == 2)
                {
                    var cH1 = ObterConectorProximo(barriletesNoNo[0], ptNo);
                    var cH2 = ObterConectorProximo(barriletesNoNo[1], ptNo);
                    if (cH1 != null && cH2 != null && !cH1.IsConnected && !cH2.IsConnected)
                    {
                        try { doc.Create.NewTeeFitting(cH1, cH2, cVert); conexoesCriadas++; } catch { }
                    }
                }
            }

            tx.Commit();

            return
                $"Traçado 3D gerado com sucesso no Revit 2026!\n\n" +
                $"• Aparelhos atendidos: {ordenados.Count}\n" +
                $"• Trechos de tubo criados: {tubosCriados} (Barrilete a H = 2,35 m)\n" +
                $"• Conexões (Joelhos/Tês) inseridas: {conexoesCriadas}\n\n" +
                $"Dica: Caso os tubos não apareçam na planta baixa atual, ajuste a Faixa da Vista (View Range) ou abra uma Vista 3D.";
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

                // Se houver apenas um sistema no projeto e não for esgoto/pluvial/quente, inclui na detecção
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

            // Seleciona os tubos detectados no Revit para destaque visual imediato
            try
            {
                uidoc.Selection.SetElementIds(tubosAF.Select(p => p.Id).ToList());
            }
            catch { }

            return
                $"Rede de Água Fria detectada e selecionada no modelo!\n\n" +
                $"• Trechos encontrados: {tubosAF.Count}\n" +
                $"• Comprimento total de tubulação: {metragemTotal:0.00} m\n\n" +
                "Os trechos foram destacados em azul na vista ativa do Revit.";
        }

        private static XYZ? ObterPontoHidraulicoElemento(Element el, double levelElevationPes, double alturaNormativaM)
        {
            double zAlvo = levelElevationPes + (alturaNormativaM / 0.3048);

            // 1. Tenta conector MEP de Água Fria / Doméstica primeiro
            if (el is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                var connAF = fi.MEPModel.ConnectorManager.Connectors
                    .Cast<Connector>()
                    .FirstOrDefault(c => c.Domain == Domain.DomainPiping &&
                                         (c.PipeSystemType == PipeSystemType.DomesticColdWater ||
                                          c.PipeSystemType == PipeSystemType.UndefinedSystemType));
                if (connAF != null)
                    return connAF.Origin;
            }

            // 2. Fallback para famílias arquitetônicas sem conector MEP: usa ponto de inserção + deslocamento para a parede
            if (el.Location is LocationPoint lp)
            {
                XYZ pt = lp.Point;
                if (el is FamilyInstance fi2)
                {
                    // Recua 15 cm em direção à parede traseira do aparelho
                    pt = pt - fi2.FacingOrientation.Multiply(0.15 / 0.3048);
                }
                return new XYZ(pt.X, pt.Y, zAlvo);
            }

            BoundingBoxXYZ? bb = el.get_BoundingBox(null);
            if (bb != null)
            {
                XYZ mid = (bb.Min + bb.Max) * 0.5;
                return new XYZ(mid.X, mid.Y, zAlvo);
            }

            return null;
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
