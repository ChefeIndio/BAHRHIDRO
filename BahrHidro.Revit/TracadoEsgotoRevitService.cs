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
    public sealed class TracadoEsgotoExternalEventHandler : IExternalEventHandler
    {
        public void Execute(UIApplication uiApp)
        {
            try
            {
                string msg = TracadoEsgotoRevitService.ExecutarTracadoEsgoto3D(uiApp);
                TaskDialog.Show("Smart Hidro — Traçado 3D Esgoto Sanitário (NBR 8160)", msg);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Smart Hidro — Aviso Esgoto 3D", $"Falha ao gerar traçado de esgoto:\n{ex.Message}");
            }
        }

        public string GetName() => "SmartHidro.TracadoEsgoto3D";
    }

    public static class TracadoEsgotoRevitService
    {
        private static TracadoEsgotoExternalEventHandler? _handler;
        private static ExternalEvent? _externalEvent;

        public static void Inicializar(UIControlledApplication application)
        {
            _handler = new TracadoEsgotoExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            TracadoEsgotoPlanner.GerarTracadoEsgoto3DCallback = SolicitarTracadoEsgoto;
        }

        public static string SolicitarTracadoEsgoto()
        {
            if (_handler == null || _externalEvent == null)
            {
                _handler = new TracadoEsgotoExternalEventHandler();
                _externalEvent = ExternalEvent.Create(_handler);
            }
            var req = _externalEvent.Raise();
            return req == ExternalEventRequest.Accepted
                ? "Selecione os aparelhos sanitários (Bacia, Lavatório, Chuveiro, Pia) no Revit 2026..."
                : $"Solicitação enviada ao Revit (Status: {req}).";
        }

        public static string ExecutarTracadoEsgoto3D(UIApplication uiApp)
        {
            UIDocument? uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null) return "Nenhum projeto aberto no Revit.";
            Document doc = uidoc.Document;

            var elementos = new List<Element>();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var el = doc.GetElement(id);
                if (el != null) elementos.Add(el);
            }

            if (elementos.Count == 0)
            {
                try
                {
                    var refs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        "Selecione os aparelhos de esgoto (Bacia, Lavatório, Chuveiro, Pia/Tanque) e clique em Concluir.");
                    foreach (var r in refs)
                    {
                        var el = doc.GetElement(r.ElementId);
                        if (el != null) elementos.Add(el);
                    }
                }
                catch
                {
                    return "Seleção cancelada pelo usuário.";
                }
            }

            if (elementos.Count == 0)
                return "Nenhum aparelho selecionado para gerar a rede de esgoto.";

            var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault();
            if (pipeType == null)
                return "Nenhum Tipo de Tubo (PipeType) carregado no template.";

            var sanitarySys = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .FirstOrDefault(st =>
                    st.SystemClassification == MEPSystemClassification.Sanitary ||
                    st.Name.Contains("Esgoto", StringComparison.OrdinalIgnoreCase) ||
                    st.Name.Contains("Sanit", StringComparison.OrdinalIgnoreCase))
                ?? new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault();

            var ventSys = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .FirstOrDefault(st =>
                    st.SystemClassification == MEPSystemClassification.Vent ||
                    st.Name.Contains("Ventil", StringComparison.OrdinalIgnoreCase))
                ?? sanitarySys;

            if (sanitarySys == null)
                return "Nenhum Sistema Sanitário (PipingSystemType) encontrado no template.";

            var level = doc.ActiveView?.GenLevel ?? new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            if (level == null) return "Nenhum Nível encontrado.";

            // Classificar aparelhos entre Bacias (DN 100 primário) e Secundários (DN 40/50 -> Caixa Sifonada)
            var bacias = new List<XYZ>();
            var secundarios = new List<(XYZ Pt, double DnMm)>();

            foreach (var el in elementos)
            {
                string nome = (((el as FamilyInstance)?.Symbol?.FamilyName ?? "") + " " + (el.Name ?? "")).ToLowerInvariant();
                XYZ? pt = (el.Location is LocationPoint lp) ? lp.Point
                          : (el.get_BoundingBox(null) is BoundingBoxXYZ bb ? (bb.Min + bb.Max) * 0.5 : null);
                if (pt == null) continue;

                XYZ ptPiso = new XYZ(pt.X, pt.Y, level.Elevation);
                if (nome.Contains("vaso") || nome.Contains("bacia") || nome.Contains("toilet") || nome.Contains("wc") || nome.Contains("water closet"))
                    bacias.Add(ptPiso);
                else if (nome.Contains("pia") || nome.Contains("cozinha") || nome.Contains("tanque") || nome.Contains("lavar") || nome.Contains("kitchen") || nome.Contains("laundry"))
                    secundarios.Add((ptPiso, 50.0));
                else
                    secundarios.Add((ptPiso, 40.0)); // Lavatório / Chuveiro
            }

            if (bacias.Count == 0 && secundarios.Count == 0)
                return "Não foi possível obter coordenadas válidas dos aparelhos selecionados.";

            int tubosCriados = 0;
            double zSubLajeSec = level.Elevation - (0.18 / 0.3048);  // -18 cm sob o piso acabado
            double zSubLajePri = level.Elevation - (0.28 / 0.3048);  // -28 cm sob o piso acabado

            using var tx = new Transaction(doc, "Smart Hidro - Traçado 3D Esgoto NBR 8160");
            tx.Start();

            // 1. Rede Secundária (DN 40/50 mm com declividade 2%) convergindo para Caixa Sifonada
            XYZ? pontoCaixaSifonada = null;
            if (secundarios.Count > 0)
            {
                double cxX = secundarios.Average(s => s.Pt.X);
                double cxY = secundarios.Average(s => s.Pt.Y);
                pontoCaixaSifonada = new XYZ(cxX, cxY, zSubLajeSec);

                foreach (var (ptAp, dnMm) in secundarios)
                {
                    XYZ pSaidaAp = new XYZ(ptAp.X, ptAp.Y, level.Elevation + (0.10 / 0.3048));
                    XYZ pJoelhoPiso = new XYZ(ptAp.X, ptAp.Y, zSubLajeSec);

                    // Descida até o nível sob a laje
                    if (pSaidaAp.DistanceTo(pJoelhoPiso) > 0.15)
                    {
                        var pDesc = Pipe.Create(doc, sanitarySys.Id, pipeType.Id, level.Id, pSaidaAp, pJoelhoPiso);
                        pDesc.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(dnMm / 304.8);
                        tubosCriados++;
                    }

                    // Ramal de descarga com declividade de 2,0% até a Caixa Sifonada
                    double distHoriz = new XYZ(pJoelhoPiso.X, pJoelhoPiso.Y, 0).DistanceTo(new XYZ(cxX, cxY, 0));
                    if (distHoriz > 0.25)
                    {
                        double queda = distHoriz * 0.02; // 2% NBR 8160
                        XYZ pChegadaCx = new XYZ(cxX, cxY, zSubLajeSec - queda);
                        var pRamal = Pipe.Create(doc, sanitarySys.Id, pipeType.Id, level.Id, pJoelhoPiso, pChegadaCx);
                        pRamal.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(dnMm / 304.8);
                        tubosCriados++;
                    }
                }
            }

            // 2. Rede Primária (DN 100 mm com declividade 1%) partindo da Bacia Sanitária
            XYZ pontoBasePrimario = bacias.Count > 0
                ? bacias[0]
                : (pontoCaixaSifonada != null ? new XYZ(pontoCaixaSifonada.X, pontoCaixaSifonada.Y, level.Elevation) : XYZ.Zero);

            XYZ pBaciaPiso = new XYZ(pontoBasePrimario.X, pontoBasePrimario.Y, level.Elevation);
            XYZ pBaciaFundo = new XYZ(pontoBasePrimario.X, pontoBasePrimario.Y, zSubLajePri);

            if (bacias.Count > 0)
            {
                var pDescBacia = Pipe.Create(doc, sanitarySys.Id, pipeType.Id, level.Id, pBaciaPiso, pBaciaFundo);
                pDescBacia.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(100.0 / 304.8);
                tubosCriados++;

                // Interliga outras bacias se houver mais de uma
                for (int i = 1; i < bacias.Count; i++)
                {
                    XYZ pOutraTop = new XYZ(bacias[i].X, bacias[i].Y, level.Elevation);
                    XYZ pOutraBot = new XYZ(bacias[i].X, bacias[i].Y, zSubLajePri + 0.05);
                    var pD2 = Pipe.Create(doc, sanitarySys.Id, pipeType.Id, level.Id, pOutraTop, pOutraBot);
                    pD2.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(100.0 / 304.8);

                    var pLiga = Pipe.Create(doc, sanitarySys.Id, pipeType.Id, level.Id, pOutraBot, pBaciaFundo);
                    pLiga.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(100.0 / 304.8);
                    tubosCriados += 2;
                }
            }

            // Liga saída da Caixa Sifonada (DN 50/75 mm) ao ramal primário DN 100 com declividade 2%
            if (pontoCaixaSifonada != null)
            {
                double distCxPri = new XYZ(pontoCaixaSifonada.X, pontoCaixaSifonada.Y, 0).DistanceTo(new XYZ(pBaciaFundo.X, pBaciaFundo.Y, 0));
                if (distCxPri > 0.25)
                {
                    XYZ pSaidaCx = new XYZ(pontoCaixaSifonada.X, pontoCaixaSifonada.Y, zSubLajeSec - 0.15);
                    XYZ pEncontro = new XYZ(pBaciaFundo.X, pBaciaFundo.Y, zSubLajePri);
                    var pRamalCx = Pipe.Create(doc, sanitarySys.Id, pipeType.Id, level.Id, pSaidaCx, pEncontro);
                    pRamalCx.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(75.0 / 304.8);
                    tubosCriados++;
                }
            }

            // 3. Trecho de saída do Subcoletor DN 100 (declividade 1%) + Coluna de Ventilação DN 50 mm
            XYZ pSaidaPrumada = pBaciaFundo + new XYZ(1.2 / 0.3048, 0, -(1.2 / 0.3048) * 0.01);
            var pSubcoletor = Pipe.Create(doc, sanitarySys.Id, pipeType.Id, level.Id, pBaciaFundo, pSaidaPrumada);
            pSubcoletor.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(100.0 / 304.8);
            tubosCriados++;

            if (ventSys != null)
            {
                XYZ pVentBase = pSaidaPrumada;
                XYZ pVentTopo = new XYZ(pVentBase.X, pVentBase.Y, level.Elevation + (3.20 / 0.3048));
                var pVent = Pipe.Create(doc, ventSys.Id, pipeType.Id, level.Id, pVentBase, pVentTopo);
                pVent.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(50.0 / 304.8);
                tubosCriados++;
            }

            tx.Commit();

            return
                $"Traçado 3D de Esgoto Sanitário e Ventilação gerado!\n\n" +
                $"• Bacias sanitárias (DN 100 mm — i = 1,0%): {bacias.Count}\n" +
                $"• Ramais secundários (DN 40/50 mm — i = 2,0%): {secundarios.Count}\n" +
                $"• Total de trechos modelados (incluindo Caixa Sifonada e Ventilação DN 50): {tubosCriados}";
        }
    }
}
