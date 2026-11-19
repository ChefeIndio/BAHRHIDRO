using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitCleanupManager
    {
        public static NetworkCleanupResult CleanGeneratedNetwork(UIDocument uidoc)
        {
            Document doc = uidoc.Document;

            var pipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .Where(IsBahrHidroElement)
                .ToList();

            var fittingsAndAccessories = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Where(IsBahrHidroElement)
                .ToList();

            var notesAndTags = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new[]
                {
                    BuiltInCategory.OST_TextNotes,
                    BuiltInCategory.OST_PipeTags
                }))
                .WhereElementIsNotElementType()
                .Where(e => IsBahrHidroElement(e) ||
                            (e.OwnerViewId != ElementId.InvalidElementId &&
                             (doc.GetElement(e.OwnerViewId)?.Name ?? "").StartsWith("BahrHidro", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var generatedViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.Name.StartsWith("BahrHidro", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var generatedSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => s.Name.StartsWith("BahrHidro", StringComparison.OrdinalIgnoreCase) ||
                            s.SheetNumber.StartsWith("BH-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Se a vista ativa for uma das vistas geradas pelo plugin, muda para uma planta/3D segura antes de apagar
            ElementId activeId = doc.ActiveView?.Id ?? ElementId.InvalidElementId;
            if (generatedViews.Any(v => v.Id == activeId) || generatedSheets.Any(s => s.Id == activeId))
            {
                View? fallbackView = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate &&
                                         (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.ThreeD) &&
                                         !v.Name.StartsWith("BahrHidro", StringComparison.OrdinalIgnoreCase));
                if (fallbackView != null)
                {
                    try { uidoc.ActiveView = fallbackView; } catch { }
                }
            }

            int pipesCount = pipes.Count;
            int fittingsCount = fittingsAndAccessories.Count;
            int viewsCount = generatedViews.Count + generatedSheets.Count;

            var allIds = new HashSet<ElementId>();
            foreach (var e in notesAndTags) allIds.Add(e.Id);
            foreach (var e in fittingsAndAccessories) allIds.Add(e.Id);
            foreach (var e in pipes) allIds.Add(e.Id);
            foreach (var v in generatedViews) allIds.Add(v.Id);
            foreach (var s in generatedSheets) allIds.Add(s.Id);

            if (allIds.Count == 0)
            {
                return new NetworkCleanupResult(0, 0, 0, "Nenhum elemento gerado pelo Bahr Hidro foi encontrado para limpeza.");
            }

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Limpar Rede e Vistas Geradas"))
            {
                tx.Start();
                doc.Delete(allIds.ToList());
                tx.Commit();
            }

            string summary = $"Limpeza concluída: {pipesCount} tubo(s), {fittingsCount} conexão(ões) e {viewsCount} vista(s)/prancha(s) removidos.";
            return new NetworkCleanupResult(pipesCount, fittingsCount, viewsCount, summary);
        }

        private static bool IsBahrHidroElement(Element e)
        {
            string comment = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
            return comment.StartsWith("BahrHidro", StringComparison.OrdinalIgnoreCase);
        }
    }
}
