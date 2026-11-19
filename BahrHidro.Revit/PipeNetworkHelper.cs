using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace BahrHidro.Revit
{
    public static class PipeNetworkHelper
    {
        public static ElementId GetOrCreateColdWaterSystemTypeId(Document doc)
        {
            if (doc == null) return ElementId.InvalidElementId;

            var sysTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .ToList();

            var cw = sysTypes.FirstOrDefault(s => s.SystemClassification == MEPSystemClassification.DomesticColdWater);
            if (cw != null) return cw.Id;

            try
            {
                var created = PipingSystemType.Create(doc, MEPSystemClassification.DomesticColdWater, "Água Fria - Bahr Hidro");
                return created?.Id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return sysTypes.FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
            }
        }

        public static void NormalizePipesForSizing(Document doc, IEnumerable<Pipe>? targetPipes = null)
        {
            if (doc == null) return;

            var pipes = (targetPipes ?? new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .OfClass(typeof(Pipe))
                .Cast<Pipe>())
                .Where(p => p != null && p.IsValidObject)
                .ToList();

            if (pipes.Count == 0) return;

            ElementId cwSysId = GetOrCreateColdWaterSystemTypeId(doc);
            if (cwSysId == ElementId.InvalidElementId) return;

            foreach (var pipe in pipes)
            {
                try
                {
                    var sysParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                    if (sysParam == null || sysParam.IsReadOnly) continue;

                    ElementId currentId = sysParam.AsElementId();
                    var currentSys = doc.GetElement(currentId) as PipingSystemType;
                    if (currentSys == null ||
                        currentSys.SystemClassification == MEPSystemClassification.OtherPipe ||
                        currentSys.SystemClassification == MEPSystemClassification.UndefinedSystemClassification)
                    {
                        sysParam.Set(cwSysId);
                    }
                }
                catch { }
            }
        }
    
        public static List<Element> GetAllPlumbingFixtures(Document doc)
        {
            var results = new List<Element>();
            
            // 1. Pega do modelo atual (IFC aberto ou RVT)
            results.AddRange(new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .ToList());

            // 2. Vareja arquivos vinculados (IFC linkado ou RVT linkado)
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc != null)
                {
                    var linkedFixtures = new FilteredElementCollector(linkDoc)
                        .WhereElementIsNotElementType()
                        .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                        .ToList();
                        
                    // Nota: No momento de ler a coordenada de linkedFixtures,
                    // o coletor deve aplicar o link.GetTotalTransform()
                    results.AddRange(linkedFixtures);
                }
            }
            return results;
        }
}
}
