using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BahrHidro.Core.Detailing;

namespace BahrHidro.Revit
{
    public sealed class DetalhamentoFinoExternalEventHandler : IExternalEventHandler
    {
        public DetalhamentoFinoOpcoes OpcoesPendentes { get; set; } = new();

        public void Execute(UIApplication uiApp)
        {
            try
            {
                string resumo = DetalhamentoFinoRevitService.ExecutarNoDocumento(uiApp, OpcoesPendentes);
                TaskDialog.Show("Smart Hidro — Detalhamento Fino Executivo", resumo);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Smart Hidro — Aviso no Detalhamento Fino", $"Não foi possível concluir o detalhamento:\n{ex.Message}");
            }
        }

        public string GetName() => "SmartHidro.DetalhamentoFinoExecutivo";
    }

    public static class DetalhamentoFinoRevitService
    {
        private static DetalhamentoFinoExternalEventHandler? _handler;
        private static ExternalEvent? _externalEvent;

        public static void Inicializar(UIControlledApplication application)
        {
            _handler = new DetalhamentoFinoExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            DetalhamentoFinoPlanner.ExecutarDetalhamentoFinoCallback = SolicitarExecucao;
        }

        public static string SolicitarExecucao(DetalhamentoFinoOpcoes opcoes)
        {
            if (_handler == null || _externalEvent == null)
            {
                _handler = new DetalhamentoFinoExternalEventHandler();
                _externalEvent = ExternalEvent.Create(_handler);
            }

            _handler.OpcoesPendentes = opcoes;
            var req = _externalEvent.Raise();
            return req == ExternalEventRequest.Accepted
                ? "Detalhamento Fino enviado ao Revit 2026! Aguarde a conclusão na janela ativa."
                : $"Solicitação enviada ao Revit (Status: {req}). Clique na janela do Revit para processar.";
        }

        public static string ExecutarNoDocumento(UIApplication uiApp, DetalhamentoFinoOpcoes opcoes)
        {
            UIDocument? uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null)
                return "Nenhum projeto aberto no Revit.";

            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            int tagsCriadas = 0;
            int tagsReposicionadas = 0;
            int elementosColoridos = 0;
            int elevacoesCriadas = 0;
            bool notasInseridas = false;

            using var tx = new Transaction(doc, "Smart Hidro - Detalhamento Fino Executivo ABNT");
            tx.Start();

            // ------------------------------------------------------------------
            // 1. PADRÃO GRÁFICO EXECUTIVO ABNT (CORES POR SISTEMA + MEIO-TOM)
            // ------------------------------------------------------------------
            if (opcoes.AplicarPadraoGraficoABNT)
            {
                var catsHidraulicas = new[]
                {
                    BuiltInCategory.OST_PipeCurves,
                    BuiltInCategory.OST_PipeFitting,
                    BuiltInCategory.OST_PipeAccessory,
                    BuiltInCategory.OST_PlumbingFixtures
                };

                foreach (var cat in catsHidraulicas)
                {
                    var elems = new FilteredElementCollector(doc, activeView.Id)
                        .OfCategory(cat)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    foreach (var el in elems)
                    {
                        string sysName = ObterNomeSistemaElemento(el);
                        var corAbnt = DetalhamentoFinoPlanner.ObterCorSistemaABNT(sysName);

                        var ogs = new OverrideGraphicSettings();
                        var rvtColor = new Color(corAbnt.R, corAbnt.G, corAbnt.B);
                        ogs.SetProjectionLineColor(rvtColor);
                        ogs.SetCutLineColor(rvtColor);
                        ogs.SetProjectionLineWeight(corAbnt.LineWeight);

                        try
                        {
                            activeView.SetElementOverrides(el.Id, ogs);
                            elementosColoridos++;
                        }
                        catch { }
                    }
                }

                // Arquitetura em meio-tom (Halftone) para destacar a tubulação
                var catsArq = new[]
                {
                    BuiltInCategory.OST_Walls,
                    BuiltInCategory.OST_Floors,
                    BuiltInCategory.OST_Doors,
                    BuiltInCategory.OST_Windows
                };
                foreach (var catArq in catsArq)
                {
                    try
                    {
                        var catObj = Category.GetCategory(doc, catArq);
                        if (catObj != null && activeView.CanCategoryBeHidden(catObj.Id))
                        {
                            var ogsHalftone = new OverrideGraphicSettings();
                            ogsHalftone.SetHalftone(true);
                            activeView.SetCategoryOverrides(catObj.Id, ogsHalftone);
                        }
                    }
                    catch { }
                }
            }

            // ------------------------------------------------------------------
            // 2. ETIQUETAS E REORGANIZAÇÃO ANTI-SOBREPOSIÇÃO (COLLISION-FREE)
            // ------------------------------------------------------------------
            if (opcoes.ReorganizarTagsSemSobreposicao && activeView.ViewType != ViewType.ThreeD)
            {
                var existingTags = new FilteredElementCollector(doc, activeView.Id)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>()
                    .ToList();

                // Se a vista ainda não tiver tags de tubo, cria tags nos tubos visíveis
                if (existingTags.Count == 0)
                {
                    var pipes = new FilteredElementCollector(doc, activeView.Id)
                        .OfClass(typeof(Pipe))
                        .Cast<Pipe>()
                        .ToList();

                    foreach (var pipe in pipes)
                    {
                        if (pipe.Location is LocationCurve lc)
                        {
                            XYZ mid = lc.Curve.Evaluate(0.5, true);
                            try
                            {
                                var newTag = IndependentTag.Create(
                                    doc,
                                    activeView.Id,
                                    new Reference(pipe),
                                    true,
                                    TagMode.TM_ADDBY_CATEGORY,
                                    TagOrientation.Horizontal,
                                    mid);
                                if (newTag != null)
                                {
                                    existingTags.Add(newTag);
                                    tagsCriadas++;
                                }
                            }
                            catch { }
                        }
                    }
                    doc.Regenerate();
                }

                XYZ rightDir = activeView.RightDirection;
                XYZ upDir = activeView.UpDirection;

                var caixas = new List<TagBox2D>();
                var tagPorId = new Dictionary<long, IndependentTag>();

                foreach (var tag in existingTags)
                {
                    BoundingBoxXYZ? bb = tag.get_BoundingBox(activeView);
                    if (bb == null) continue;

                    XYZ[] corners =
                    {
                        new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                        new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                        new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                        new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                        new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                        new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)
                    };

                    double minX = corners.Min(p => p.DotProduct(rightDir));
                    double maxX = corners.Max(p => p.DotProduct(rightDir));
                    double minY = corners.Min(p => p.DotProduct(upDir));
                    double maxY = corners.Max(p => p.DotProduct(upDir));

                    long idVal = tag.Id.Value;
                    tagPorId[idVal] = tag;
                    caixas.Add(new TagBox2D
                    {
                        ElementIdValue = idVal,
                        MinX = minX,
                        MaxX = maxX,
                        MinY = minY,
                        MaxY = maxY
                    });
                }

                tagsReposicionadas = DetalhamentoFinoPlanner.ResolverColisoes(caixas);

                foreach (var cx in caixas)
                {
                    if ((Math.Abs(cx.OffsetX) > 1e-4 || Math.Abs(cx.OffsetY) > 1e-4) &&
                        tagPorId.TryGetValue(cx.ElementIdValue, out var tag))
                    {
                        try
                        {
                            tag.HasLeader = true;
                            XYZ delta = rightDir.Multiply(cx.OffsetX) + upDir.Multiply(cx.OffsetY);
                            tag.TagHeadPosition = tag.TagHeadPosition + delta;
                        }
                        catch { }
                    }
                }
            }

            // ------------------------------------------------------------------
            // 3. ELEVAÇÕES EXECUTIVAS DE PAREDES HIDRÁULICAS (ESCALA 1:20)
            // ------------------------------------------------------------------
            if (opcoes.GerarElevacoesParedes120)
            {
                var sectionType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Section);

                if (sectionType != null)
                {
                    var existingViewNames = new HashSet<string>(
                        new FilteredElementCollector(doc)
                            .OfClass(typeof(View))
                            .Cast<View>()
                            .Select(v => v.Name),
                        StringComparer.OrdinalIgnoreCase);

                    var fixtures = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    var walls = new FilteredElementCollector(doc)
                        .OfClass(typeof(Wall))
                        .Cast<Wall>()
                        .ToList();

                    var paredesHidraulicas = new List<Wall>();
                    foreach (var wall in walls)
                    {
                        if (wall.Location is not LocationCurve wlc) continue;
                        Curve wCurve = wlc.Curve;
                        bool temAparelho = fixtures.Any(f =>
                        {
                            if (f is FamilyInstance fi && fi.Host?.Id == wall.Id) return true;
                            if (f.Location is LocationPoint lp)
                                return wCurve.Distance(new XYZ(lp.Point.X, lp.Point.Y, wCurve.GetEndPoint(0).Z)) < 1.65; // ~0,50 m
                            return false;
                        });

                        if (temAparelho)
                            paredesHidraulicas.Add(wall);
                    }

                    int idxElev = 1;
                    foreach (var wall in paredesHidraulicas.Take(4))
                    {
                        if (wall.Location is not LocationCurve wlc) continue;
                        XYZ p0 = wlc.Curve.GetEndPoint(0);
                        XYZ p1 = wlc.Curve.GetEndPoint(1);
                        XYZ mid = (p0 + p1) * 0.5;
                        double length = p0.DistanceTo(p1);
                        if (length < 1.0) continue;

                        XYZ dirX = (p1 - p0).Normalize();
                        XYZ dirZ = XYZ.BasisZ;
                        XYZ viewDir = dirX.CrossProduct(dirZ).Normalize();

                        Transform t = Transform.Identity;
                        t.Origin = new XYZ(mid.X, mid.Y, mid.Z + 4.5); // ~1,35 m altura média
                        t.BasisX = dirX;
                        t.BasisY = dirZ;
                        t.BasisZ = viewDir;

                        BoundingBoxXYZ secBox = new BoundingBoxXYZ
                        {
                            Transform = t,
                            Min = new XYZ(-length * 0.5 - 0.5, -4.8, -1.5),
                            Max = new XYZ(length * 0.5 + 0.5, 5.2, 2.5)
                        };

                        string viewName = $"DET-HID - Elevação Parede {idxElev:00} (1:20)";
                        while (existingViewNames.Contains(viewName))
                        {
                            idxElev++;
                            viewName = $"DET-HID - Elevação Parede {idxElev:00} (1:20)";
                        }

                        try
                        {
                            ViewSection vs = ViewSection.CreateSection(doc, sectionType.Id, secBox);
                            vs.Name = viewName;
                            vs.Scale = 20;
                            existingViewNames.Add(viewName);
                            elevacoesCriadas++;
                            idxElev++;
                        }
                        catch { }
                    }
                }
            }

            // ------------------------------------------------------------------
            // 4. NOTAS TÉCNICAS ABNT & RESUMO QUANTITATIVO NA VISTA
            // ------------------------------------------------------------------
            if (opcoes.InserirNotasTecnicasEBOM && activeView.ViewType != ViewType.ThreeD)
            {
                var textType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault();

                if (textType != null)
                {
                    var metragemPorBitola = new Dictionary<string, double>();
                    var allPipes = new FilteredElementCollector(doc)
                        .OfClass(typeof(Pipe))
                        .Cast<Pipe>();

                    foreach (var p in allPipes)
                    {
                        double diamMm = Math.Round(p.Diameter * 304.8);
                        string key = $"DN {diamMm:0} mm";
                        double lenM = (p.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0.0) * 0.3048;
                        metragemPorBitola[key] = metragemPorBitola.GetValueOrDefault(key) + lenM;
                    }

                    var contagemConexoes = new Dictionary<string, int>();
                    var fittings = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PipeFitting)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    foreach (var fit in fittings)
                    {
                        string famName = (fit as FamilyInstance)?.Symbol?.FamilyName ?? fit.Name ?? "Conexão";
                        contagemConexoes[famName] = contagemConexoes.GetValueOrDefault(famName) + 1;
                    }

                    string textoNotas = DetalhamentoFinoPlanner.GerarTextoNotasExecutivasABNT(metragemPorBitola, contagemConexoes);

                    BoundingBoxXYZ? viewBb = activeView.CropBox;
                    XYZ posNota = viewBb != null
                        ? new XYZ(viewBb.Max.X + 1.0, viewBb.Max.Y, 0)
                        : activeView.Origin;

                    try
                    {
                        TextNote.Create(doc, activeView.Id, posNota, textoNotas, textType.Id);
                        notasInseridas = true;
                    }
                    catch { }
                }
            }

            tx.Commit();

            return
                $"Acabamento Executivo ABNT concluído na vista '{activeView.Name}':\n" +
                $"• Elementos coloridos no padrão ABNT: {elementosColoridos}\n" +
                $"• Etiquetas criadas: {tagsCriadas} | Etiquetas reorganizadas sem sobreposição: {tagsReposicionadas}\n" +
                $"• Elevações hidráulicas de parede (1:20) criadas: {elevacoesCriadas}\n" +
                $"• Bloco de Notas Técnicas ABNT & BOM inserido: {(notasInseridas ? "Sim" : "Não aplicado (vista 3D ou sem TextNoteType)")}";
        }

        private static string ObterNomeSistemaElemento(Element el)
        {
            Parameter? pSys = el.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            if (pSys != null && pSys.HasValue)
            {
                string val = pSys.AsValueString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            Parameter? pClass = el.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM);
            if (pClass != null && pClass.HasValue)
            {
                string val = pClass.AsString() ?? pClass.AsValueString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            return el.Name ?? string.Empty;
        }
    }
}
