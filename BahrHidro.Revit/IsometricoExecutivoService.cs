using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BahrHidro.Core.Detailing;

namespace BahrHidro.Revit
{
    public sealed class IsometricoAmbienteExternalEventHandler : IExternalEventHandler
    {
        public void Execute(UIApplication uiApp)
        {
            try
            {
                string resumo = IsometricoExecutivoService.GerarIsometricosPorAmbiente(uiApp);
                TaskDialog.Show("Smart Hidro — Isométricos 3D por Ambiente", resumo);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Smart Hidro — Aviso", $"Não foi possível gerar os isométricos por ambiente:\n{ex.Message}");
            }
        }

        public string GetName() => "SmartHidro.IsometricosPorAmbiente";
    }

    public static class IsometricoExecutivoService
    {
        private static IsometricoAmbienteExternalEventHandler? _handler;
        private static ExternalEvent? _externalEvent;

        private sealed class AmbienteDetectado
        {
            public string Nome { get; set; } = string.Empty;
            public string Nivel { get; set; } = string.Empty;
            public Func<XYZ, bool> ContemPonto { get; set; } = _ => false;
            public BoundingBoxXYZ? Bounds { get; set; }
            public List<Element> Elementos { get; } = new();
        }

        public static void Inicializar(UIControlledApplication application)
        {
            _handler = new IsometricoAmbienteExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            IsometricoAmbientePlanner.GerarIsometricosPorAmbienteCallback = SolicitarGeracaoPorAmbiente;
        }

        public static string SolicitarGeracaoPorAmbiente()
        {
            if (_handler == null || _externalEvent == null)
            {
                _handler = new IsometricoAmbienteExternalEventHandler();
                _externalEvent = ExternalEvent.Create(_handler);
            }

            var req = _externalEvent.Raise();
            return req == ExternalEventRequest.Accepted
                ? "Gerando vistas isométricas 3D separadas por ambiente no Revit 2026..."
                : $"Solicitação enviada ao Revit (Status: {req}). Clique na janela do Revit para processar.";
        }

        public static string GerarIsometricosPorAmbiente(UIApplication uiApp)
        {
            UIDocument? uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null)
                return "Nenhum projeto aberto no Revit.";

            Document doc = uidoc.Document;

            var viewFamilyType3D = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

            if (viewFamilyType3D == null)
                return "Nenhum tipo de Vista 3D encontrado no template atual.";

            // 1. Coletar todos os aparelhos sanitários e tubulações do modelo
            var fixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .WhereElementIsNotElementType()
                .ToElements();

            var pipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .Cast<Pipe>()
                .Cast<Element>()
                .ToList();

            var fittings = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e =>
                    e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                    e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeAccessory)
                .ToList();

            if (fixtures.Count == 0 && pipes.Count == 0)
                return "Nenhum aparelho sanitário ou tubulação encontrado no modelo.";

            // 2. Mapear ambientes (Rooms no hospedeiro, Rooms em vínculos RVT e Spaces MEP)
            var ambientes = ColetarAmbientesDisponiveis(doc);

            // 3. Associar cada aparelho sanitário ao seu ambiente
            var semAmbiente = new List<Element>();
            foreach (var fix in fixtures)
            {
                var pontosTeste = ObterPontosTesteElemento(fix);
                var amb = ambientes.FirstOrDefault(a => pontosTeste.Any(pt => a.ContemPonto(pt)));
                if (amb != null)
                    amb.Elementos.Add(fix);
                else
                    semAmbiente.Add(fix);
            }

            // 4. Associar tubulações e conexões aos ambientes onde estão inseridos
            foreach (var el in pipes.Concat(fittings))
            {
                var pontosTeste = ObterPontosTesteElemento(el);
                var amb = ambientes.FirstOrDefault(a => pontosTeste.Any(pt => a.ContemPonto(pt)));
                if (amb != null)
                {
                    amb.Elementos.Add(el);
                }
                else
                {
                    // Se o tubo estiver logo acima/abaixo ou na parede de um ambiente com aparelhos, associa por proximidade 3D
                    XYZ? centro = pontosTeste.FirstOrDefault();
                    if (centro != null)
                    {
                        var ambProximo = ambientes
                            .Where(a => a.Elementos.Count > 0 && a.Bounds != null)
                            .FirstOrDefault(a => PontoPertoDaCaixa(centro, a.Bounds!, margemXY: 2.5, margemZ: 2.5));
                        if (ambProximo != null)
                            ambProximo.Elementos.Add(el);
                    }
                }
            }

            // Filtrar apenas ambientes que realmente possuem instalações hidráulicas
            var setoresValidos = ambientes
                .Where(a => a.Elementos.Count > 0)
                .ToList();

            // 5. Fallback espacial: se não houver Rooms/Spaces no modelo, agrupa aparelhos e tubos por proximidade (banheiro/cozinha)
            if (setoresValidos.Count == 0)
            {
                setoresValidos = AgruparPorProximidadeEspacial(doc, fixtures, pipes, fittings);
            }

            if (setoresValidos.Count == 0)
                return "Não foi possível isolar setores molhados com tubulações no modelo.";

            View3D? primeiraVistaCriada = null;
            var nomesGerados = new List<string>();

            using (var tx = new Transaction(doc, "Smart Hidro - Gerar Isométricos 3D por Ambiente"))
            {
                tx.Start();

                // Se a vista ativa for uma vista antiga do Smart Hidro, muda temporariamente para outra vista antes de limpar antigas
                if (doc.ActiveView is View3D activeV3d &&
                    activeV3d.Name.StartsWith("Smart Hidro - Isométrico", StringComparison.OrdinalIgnoreCase))
                {
                    var fallbackView = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .FirstOrDefault(vp => !vp.IsTemplate);
                    if (fallbackView != null)
                    {
                        tx.Commit();
                        uidoc.ActiveView = fallbackView;
                        tx.Start();
                    }
                }

                // Remover vistas antigas 'Smart Hidro - Isométrico...' para evitar cópias (2), (3)
                var vistasAntigas = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .Where(v => !v.IsTemplate &&
                                v.Id != doc.ActiveView.Id &&
                                v.Name.StartsWith("Smart Hidro - Isométrico", StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.Id)
                    .ToList();

                if (vistasAntigas.Count > 0)
                {
                    try { doc.Delete(vistasAntigas); } catch { }
                }

                var nomesEmUso = new HashSet<string>(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Select(v => v.Name),
                    StringComparer.OrdinalIgnoreCase);

                int idx = 1;
                foreach (var setor in setoresValidos.Take(15))
                {
                    string rotuloBase = SanitizarNomeVista(
                        string.IsNullOrWhiteSpace(setor.Nivel)
                            ? setor.Nome
                            : $"{setor.Nome} ({setor.Nivel})");

                    string nomeVista = $"Smart Hidro - Isométrico 3D - {rotuloBase}";
                    if (nomesEmUso.Contains(nomeVista))
                        nomeVista = $"Smart Hidro - Isométrico 3D - {idx:00} - {rotuloBase}";

                    View3D v3d = View3D.CreateIsometric(doc, viewFamilyType3D.Id);
                    v3d.Name = nomeVista;
                    nomesEmUso.Add(nomeVista);

                    RefinarVistaIsometrica(doc, v3d, setor.Elementos);

                    primeiraVistaCriada ??= v3d;
                    nomesGerados.Add($"• {nomeVista} ({setor.Elementos.Count} elementos)");
                    idx++;
                }

                tx.Commit();
            }

            // Abre automaticamente o primeiro isométrico gerado para visualização imediata
            if (primeiraVistaCriada != null)
            {
                try { uidoc.ActiveView = primeiraVistaCriada; } catch { }
            }

            return
                $"Foram geradas {nomesGerados.Count} vista(s) isométrica(s) 3D recortada(s) por ambiente:\n\n" +
                string.Join("\n", nomesGerados) +
                "\n\nTodas as vistas foram configuradas com SectionBox individual, Disciplina = Plumbing, câmera travada e tags 3D.";
        }

        private static List<AmbienteDetectado> ColetarAmbientesDisponiveis(Document doc)
        {
            var lista = new List<AmbienteDetectado>();

            // A) Rooms no modelo hospedeiro
            var hostRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            foreach (var r in hostRooms)
            {
                string nome = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name ?? "Ambiente";
                string num = r.Number ?? "";
                string rotulo = string.IsNullOrWhiteSpace(num) ? nome : $"{nome} {num}".Trim();
                string nivel = r.Level?.Name ?? "";
                BoundingBoxXYZ? bb = r.get_BoundingBox(null);

                lista.Add(new AmbienteDetectado
                {
                    Nome = rotulo,
                    Nivel = nivel,
                    Bounds = bb,
                    ContemPonto = pt =>
                    {
                        try { return r.IsPointInRoom(pt); }
                        catch { return false; }
                    }
                });
            }

            // B) Spaces MEP no modelo hospedeiro
            var spaces = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType()
                .Cast<Space>()
                .Where(s => s.Area > 0)
                .ToList();

            foreach (var sp in spaces)
            {
                string nome = sp.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? sp.Name ?? "Espaço";
                string num = sp.Number ?? "";
                string rotulo = string.IsNullOrWhiteSpace(num) ? nome : $"{nome} {num}".Trim();
                string nivel = sp.Level?.Name ?? "";
                BoundingBoxXYZ? bb = sp.get_BoundingBox(null);

                lista.Add(new AmbienteDetectado
                {
                    Nome = rotulo,
                    Nivel = nivel,
                    Bounds = bb,
                    ContemPonto = pt =>
                    {
                        try { return sp.IsPointInSpace(pt); }
                        catch { return false; }
                    }
                });
            }

            // C) Rooms dentro de modelos arquitetônicos vinculados (RevitLinkInstance)
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;

                Transform totalTf = link.GetTotalTransform();
                Transform invTf = totalTf.Inverse;

                var linkRooms = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Area > 0)
                    .ToList();

                foreach (var lr in linkRooms)
                {
                    string nome = lr.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? lr.Name ?? "Ambiente";
                    string num = lr.Number ?? "";
                    string rotulo = string.IsNullOrWhiteSpace(num) ? nome : $"{nome} {num}".Trim();
                    string nivel = lr.Level?.Name ?? "";
                    BoundingBoxXYZ? bbLocal = lr.get_BoundingBox(null);
                    BoundingBoxXYZ? bbGlobal = bbLocal == null ? null : new BoundingBoxXYZ
                    {
                        Min = totalTf.OfPoint(bbLocal.Min),
                        Max = totalTf.OfPoint(bbLocal.Max)
                    };

                    lista.Add(new AmbienteDetectado
                    {
                        Nome = rotulo,
                        Nivel = nivel,
                        Bounds = bbGlobal,
                        ContemPonto = pt =>
                        {
                            try
                            {
                                XYZ ptLocal = invTf.OfPoint(pt);
                                return lr.IsPointInRoom(ptLocal);
                            }
                            catch { return false; }
                        }
                    });
                }
            }

            return lista;
        }

        private static List<AmbienteDetectado> AgruparPorProximidadeEspacial(
            Document doc,
            IList<Element> fixtures,
            IList<Element> pipes,
            IList<Element> fittings)
        {
            var grupos = new List<AmbienteDetectado>();
            var bases = fixtures.Count > 0 ? fixtures : pipes;
            var usados = new HashSet<long>();

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            int seq = 1;
            foreach (var item in bases)
            {
                if (usados.Contains(item.Id.Value)) continue;
                XYZ? p0 = ObterPontosTesteElemento(item).FirstOrDefault();
                if (p0 == null) continue;

                string nivelNome = levels
                    .OrderBy(l => Math.Abs(l.Elevation - p0.Z))
                    .FirstOrDefault()?.Name ?? "";

                var grupo = new AmbienteDetectado
                {
                    Nome = $"Setor Molhado {seq:00}",
                    Nivel = nivelNome
                };

                // Agrupa elementos dentro de um raio de 3,3 m (11 pés) em XY e mesmo pavimento (Z <= 10 pés)
                foreach (var cand in bases)
                {
                    if (usados.Contains(cand.Id.Value)) continue;
                    XYZ? pc = ObterPontosTesteElemento(cand).FirstOrDefault();
                    if (pc == null) continue;

                    double dxy = Math.Sqrt(Math.Pow(pc.X - p0.X, 2) + Math.Pow(pc.Y - p0.Y, 2));
                    double dz = Math.Abs(pc.Z - p0.Z);
                    if (dxy <= 11.0 && dz <= 10.0)
                    {
                        grupo.Elementos.Add(cand);
                        usados.Add(cand.Id.Value);
                    }
                }

                BoundingBoxXYZ? bbGrupo = CalcularCaixaSetor(grupo.Elementos, margemPes: 2.5);
                if (bbGrupo != null)
                {
                    foreach (var extra in pipes.Concat(fittings))
                    {
                        if (grupo.Elementos.Any(e => e.Id == extra.Id)) continue;
                        XYZ? pe = ObterPontosTesteElemento(extra).FirstOrDefault();
                        if (pe != null && PontoPertoDaCaixa(pe, bbGrupo, margemXY: 1.5, margemZ: 1.5))
                            grupo.Elementos.Add(extra);
                    }
                }

                grupos.Add(grupo);
                seq++;
            }

            return grupos;
        }

        private static List<XYZ> ObterPontosTesteElemento(Element el)
        {
            var pts = new List<XYZ>();
            if (el.Location is LocationPoint lp)
            {
                pts.Add(lp.Point);
                pts.Add(lp.Point + new XYZ(0, 0, 1.5)); // eleva 45 cm acima do piso
                if (el is FamilyInstance fi)
                {
                    // Testa também 45 cm à frente e atrás da face da parede para aparelhos de parede
                    pts.Add(lp.Point + fi.FacingOrientation.Multiply(1.5) + new XYZ(0, 0, 1.5));
                    pts.Add(lp.Point - fi.FacingOrientation.Multiply(1.5) + new XYZ(0, 0, 1.5));
                }
            }
            else if (el.Location is LocationCurve lc)
            {
                pts.Add(lc.Curve.Evaluate(0.5, true));
                pts.Add(lc.Curve.GetEndPoint(0));
                pts.Add(lc.Curve.GetEndPoint(1));
            }
            return pts;
        }

        private static bool PontoPertoDaCaixa(XYZ pt, BoundingBoxXYZ bb, double margemXY, double margemZ)
        {
            return pt.X >= bb.Min.X - margemXY && pt.X <= bb.Max.X + margemXY &&
                   pt.Y >= bb.Min.Y - margemXY && pt.Y <= bb.Max.Y + margemXY &&
                   pt.Z >= bb.Min.Z - margemZ && pt.Z <= bb.Max.Z + margemZ;
        }

        private static string SanitizarNomeVista(string raw)
        {
            string limpo = Regex.Replace(raw, @"[\\:\{\}\[\]\|;<>\?`~]", "-");
            limpo = Regex.Replace(limpo, @"\s+", " ").Trim();
            return limpo.Length > 45 ? limpo[..45].Trim() : limpo;
        }

        public static string RefinarVistaIsometrica(Document doc, View3D view3d, IList<Element>? elementosSetor = null)
        {
            if (view3d == null || view3d.IsTemplate)
                return "Vista 3D inválida ou é um template.";

            var alvos = (elementosSetor != null && elementosSetor.Count > 0)
                ? elementosSetor.ToList()
                : new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e =>
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeCurves ||
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PipeAccessory ||
                        e.Category?.Id.Value == (long)BuiltInCategory.OST_PlumbingFixtures)
                    .ToList();

            BoundingBoxXYZ? sectorBox = CalcularCaixaSetor(alvos, margemPes: 1.8);

            try { view3d.Discipline = ViewDiscipline.Plumbing; } catch { }
            try { view3d.DetailLevel = ViewDetailLevel.Fine; } catch { }
            try { view3d.DisplayStyle = DisplayStyle.HLR; } catch { }
            try { view3d.Scale = 25; } catch { }

            if (sectorBox != null)
            {
                try
                {
                    view3d.IsSectionBoxActive = true;
                    view3d.SetSectionBox(sectorBox);
                    view3d.CropBoxActive = false;
                    view3d.CropBoxVisible = false;
                }
                catch { }
            }

            // Mantém vínculos arquitetônicos visíveis em meio-tom (via Discipline = Plumbing) para dar contexto às paredes do banheiro
            var categoriasOcultar = new[]
            {
                BuiltInCategory.OST_Grids,
                BuiltInCategory.OST_Levels,
                BuiltInCategory.OST_VolumeOfInterest,
                BuiltInCategory.OST_Mass,
                BuiltInCategory.OST_Topography,
                BuiltInCategory.OST_Site,
                BuiltInCategory.OST_Rooms,
                BuiltInCategory.OST_MEPSpaces,
                BuiltInCategory.OST_Cameras,
                BuiltInCategory.OST_SectionBox
            };

            foreach (var bic in categoriasOcultar)
            {
                try
                {
                    var cat = Category.GetCategory(doc, bic);
                    if (cat != null && view3d.CanCategoryBeHidden(cat.Id))
                        view3d.SetCategoryHidden(cat.Id, true);
                }
                catch { }
            }

            foreach (var el in alvos)
            {
                string sysName = el.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString()
                                 ?? el.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsValueString()
                                 ?? el.Name ?? "";
                var cor = DetalhamentoFinoPlanner.ObterCorSistemaABNT(sysName);
                var ogs = new OverrideGraphicSettings();
                var c = new Color(cor.R, cor.G, cor.B);
                ogs.SetProjectionLineColor(c);
                ogs.SetCutLineColor(c);
                ogs.SetProjectionLineWeight(cor.LineWeight);
                try { view3d.SetElementOverrides(el.Id, ogs); } catch { }
            }

            if (sectorBox != null && !view3d.IsLocked)
            {
                try
                {
                    XYZ center = (sectorBox.Min + sectorBox.Max) * 0.5;
                    XYZ forward = new XYZ(-1.0, 1.0, -0.85).Normalize();
                    XYZ up = new XYZ(-1.0, 1.0, 1.35).Normalize();
                    XYZ eye = center - forward.Multiply(22.0);

                    view3d.SetOrientation(new ViewOrientation3D(eye, up, forward));
                    if (view3d.CanBeLocked())
                        view3d.SaveOrientationAndLock();
                }
                catch { }
            }

            doc.Regenerate();

            int tagsInseridas = 0;
            if (view3d.IsLocked)
            {
                var existingTaggedIds = new HashSet<long>(
                    new FilteredElementCollector(doc, view3d.Id)
                        .OfClass(typeof(IndependentTag))
                        .Cast<IndependentTag>()
                        .SelectMany(t => t.GetTaggedLocalElementIds())
                        .Select(id => id.Value));

                foreach (var pipe in alvos.OfType<Pipe>())
                {
                    if (existingTaggedIds.Contains(pipe.Id.Value)) continue;
                    if (pipe.Location is not LocationCurve lc) continue;
                    if (lc.Curve.Length < 0.65) continue;

                    XYZ mid = lc.Curve.Evaluate(0.5, true);
                    try
                    {
                        var tag = IndependentTag.Create(
                            doc,
                            view3d.Id,
                            new Reference(pipe),
                            true,
                            TagMode.TM_ADDBY_CATEGORY,
                            TagOrientation.Horizontal,
                            mid);
                        if (tag != null)
                        {
                            tag.TagHeadPosition = mid + new XYZ(0.35, 0.35, 0.45);
                            tagsInseridas++;
                        }
                    }
                    catch { }
                }
            }

            return $"Vista '{view3d.Name}' otimizada com {tagsInseridas} tags 3D.";
        }

        private static BoundingBoxXYZ? CalcularCaixaSetor(IList<Element> elementos, double margemPes)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool achou = false;

            foreach (var el in elementos)
            {
                BoundingBoxXYZ? bb = el.get_BoundingBox(null);
                if (bb == null) continue;

                minX = Math.Min(minX, bb.Min.X);
                minY = Math.Min(minY, bb.Min.Y);
                minZ = Math.Min(minZ, bb.Min.Z);
                maxX = Math.Max(maxX, bb.Max.X);
                maxY = Math.Max(maxY, bb.Max.Y);
                maxZ = Math.Max(maxZ, bb.Max.Z);
                achou = true;
            }

            if (!achou) return null;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX - margemPes, minY - margemPes, minZ - margemPes),
                Max = new XYZ(maxX + margemPes, maxY + margemPes, maxZ + margemPes)
            };
        }
    }
}
