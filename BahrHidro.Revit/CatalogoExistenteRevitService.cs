using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BahrHidro.Core.Catalog;

namespace BahrHidro.Revit
{
    public sealed class CatalogoExistenteExternalEventHandler : IExternalEventHandler
    {
        public CatalogoItemSyncDto? ItemParaInserir { get; set; }
        public List<CatalogoItemSyncDto>? ListaParaSincronizar { get; set; }

        public void Execute(UIApplication uiApp)
        {
            try
            {
                if (ListaParaSincronizar != null)
                {
                    string msg = CatalogoExistenteRevitService.ExecutarSincronizacaoTemplate(uiApp, ListaParaSincronizar);
                    TaskDialog.Show("Smart Hidro — Catálogo Sincronizado no Revit", msg);
                }
                else if (ItemParaInserir != null)
                {
                    string msg = CatalogoExistenteRevitService.ExecutarInsercaoItem(uiApp, ItemParaInserir);
                    if (!string.IsNullOrWhiteSpace(msg))
                        TaskDialog.Show("Smart Hidro — Catálogo", msg);
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Smart Hidro — Aviso", $"Falha ao executar item do catálogo:\n{ex.Message}");
            }
        }

        public string GetName() => "SmartHidro.CatalogoExistenteRevitBridge";
    }

    public static class CatalogoExistenteRevitService
    {
        private static CatalogoExistenteExternalEventHandler? _handler;
        private static ExternalEvent? _externalEvent;

        public static void Inicializar(UIControlledApplication application)
        {
            _handler = new CatalogoExistenteExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);

            CatalogoRevitBridge.InserirNoRevitCallback = item =>
            {
                if (_handler == null || _externalEvent == null) return "Conector Revit não inicializado.";
                _handler.ListaParaSincronizar = null;
                _handler.ItemParaInserir = item;
                _externalEvent.Raise();
                return $"Enviado ao Revit: '{item.Nome}'. Clique na vista do Revit para posicionar.";
            };

            CatalogoRevitBridge.SincronizarTubosTemplateCallback = lista =>
            {
                if (_handler == null || _externalEvent == null) return "Conector Revit não inicializado.";
                _handler.ItemParaInserir = null;
                _handler.ListaParaSincronizar = lista.ToList();
                _externalEvent.Raise();
                return "Sincronizando catálogos de tubos no projeto ativo do Revit...";
            };
        }

        public static string ExecutarSincronizacaoTemplate(UIApplication uiApp, IList<CatalogoItemSyncDto> itens)
        {
            UIDocument? uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null) return "Nenhum projeto aberto no Revit.";

            Document doc = uidoc.Document;
            var pipeBase = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault();
            if (pipeBase == null)
                return "Nenhum tipo de tubo base encontrado no template atual.";

            var existentes = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            int criados = 0;
            using (var tx = new Transaction(doc, "Smart Hidro - Sincronizar Catálogo de Tubos"))
            {
                tx.Start();
                foreach (var it in itens.Where(i => i.EhTubo || i.Nome.Contains("Tubo", StringComparison.OrdinalIgnoreCase) || i.Nome.Contains("PVC", StringComparison.OrdinalIgnoreCase) || i.Nome.Contains("PPR", StringComparison.OrdinalIgnoreCase) || i.Nome.Contains("CPVC", StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.IsNullOrWhiteSpace(it.Nome) || existentes.Contains(it.Nome)) continue;
                    try
                    {
                        var dup = pipeBase.Duplicate(it.Nome);
                        dup.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Set($"{it.Material} | {it.Bitolas}".Trim(' ', '|'));
                        existentes.Add(it.Nome);
                        criados++;
                    }
                    catch { }
                }
                tx.Commit();
            }

            return $"Sincronização concluída! {criados} novo(s) tipo(s) de tubo criados no projeto atual do Revit 2026.";
        }

        public static string ExecutarInsercaoItem(UIApplication uiApp, CatalogoItemSyncDto item)
        {
            UIDocument? uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null) return "Nenhum projeto aberto no Revit.";
            Document doc = uidoc.Document;

            // 1. Se for tubo comercial, seleciona/cria o PipeType e ativa o comando Pipe
            bool ehTubo = item.EhTubo ||
                          item.Nome.Contains("Tubo", StringComparison.OrdinalIgnoreCase) ||
                          item.Nome.Contains("PVC", StringComparison.OrdinalIgnoreCase) ||
                          item.Nome.Contains("CPVC", StringComparison.OrdinalIgnoreCase) ||
                          item.Nome.Contains("PPR", StringComparison.OrdinalIgnoreCase);

            if (ehTubo)
            {
                var pt = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>()
                    .FirstOrDefault(p => p.Name.Equals(item.Nome, StringComparison.OrdinalIgnoreCase));

                if (pt == null)
                {
                    ExecutarSincronizacaoTemplate(uiApp, new[] { item });
                    pt = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>()
                        .FirstOrDefault(p => p.Name.Equals(item.Nome, StringComparison.OrdinalIgnoreCase));
                }

                if (pt != null)
                {
                    using (var tx = new Transaction(doc, "Ativar Tubo do Catálogo"))
                    {
                        tx.Start();
                        doc.SetDefaultElementTypeId(ElementTypeGroup.PipeType, pt.Id);
                        tx.Commit();
                    }
                    var cmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Pipe);
                    if (cmd != null && uiApp.CanPostCommand(cmd))
                    {
                        uiApp.PostCommand(cmd);
                        return string.Empty;
                    }
                }
            }

            // 2. Se o usuário informou um caminho .rfa manual, carrega diretamente
            FamilySymbol? symbol = null;
            if (!string.IsNullOrWhiteSpace(item.CaminhoRfa) && File.Exists(item.CaminhoRfa))
            {
                using (var tx = new Transaction(doc, "Carregar Família .RFA do Catálogo"))
                {
                    tx.Start();
                    if (doc.LoadFamily(item.CaminhoRfa, out Family fam) && fam != null)
                    {
                        var sid = fam.GetFamilySymbolIds().FirstOrDefault();
                        if (sid != null && sid != ElementId.InvalidElementId)
                            symbol = doc.GetElement(sid) as FamilySymbol;
                    }
                    tx.Commit();
                }
            }

            // 3. Caso contrário, procura símbolo já carregado no projeto pelo nome do item
            if (symbol == null)
            {
                var palavras = item.Nome.Split(new[] { ' ', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= 3)
                    .ToList();

                symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(s => s.Category != null &&
                                (s.Category.Id.Value == (long)BuiltInCategory.OST_PlumbingFixtures ||
                                 s.Category.Id.Value == (long)BuiltInCategory.OST_PipeAccessory ||
                                 s.Category.Id.Value == (long)BuiltInCategory.OST_PipeFitting ||
                                 s.Category.Id.Value == (long)BuiltInCategory.OST_MechanicalEquipment))
                    .FirstOrDefault(s => palavras.Any(p => $"{s.FamilyName} {s.Name}".Contains(p, StringComparison.OrdinalIgnoreCase)));
            }

            // 4. Se ainda não achou, busca nas bibliotecas padrão do Revit 2026
            if (symbol == null)
            {
                string[] dirs = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartHidro_Familias"),
                    @"C:\ProgramData\Autodesk\RVT 2026\Libraries"
                };
                var termos = item.Nome.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();

                foreach (var d in dirs.Where(Directory.Exists))
                {
                    var rfa = Directory.EnumerateFiles(d, "*.rfa", SearchOption.AllDirectories)
                        .FirstOrDefault(f => termos.Any(t => Path.GetFileNameWithoutExtension(f).Contains(t, StringComparison.OrdinalIgnoreCase)));
                    if (rfa != null)
                    {
                        using (var tx = new Transaction(doc, "Carregar Família Biblioteca Revit"))
                        {
                            tx.Start();
                            if (doc.LoadFamily(rfa, out Family fam) && fam != null)
                            {
                                var sid = fam.GetFamilySymbolIds().FirstOrDefault();
                                if (sid != null && sid != ElementId.InvalidElementId)
                                    symbol = doc.GetElement(sid) as FamilySymbol;
                            }
                            tx.Commit();
                        }
                        break;
                    }
                }
            }

            if (symbol != null)
            {
                if (!symbol.IsActive)
                {
                    using (var tx = new Transaction(doc, "Ativar Família"))
                    {
                        tx.Start();
                        symbol.Activate();
                        tx.Commit();
                    }
                }
                uidoc.PromptForFamilyInstancePlacement(symbol);
                return string.Empty;
            }

            return $"Família '{item.Nome}' não encontrada no projeto nem em 'Documentos\\SmartHidro_Familias'. Associe o arquivo .rfa no cadastro manual ou salve a família nessa pasta.";
        }
    }
}
