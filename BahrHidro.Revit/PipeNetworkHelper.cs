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
            return GetOrCreateSystemTypeId(doc, MEPSystemClassification.DomesticColdWater, "Água Fria - Bahr Hidro");
        }

        public static ElementId GetOrCreateSystemTypeId(Document doc, MEPSystemClassification classification, string defaultName)
        {
            if (doc == null) return ElementId.InvalidElementId;

            var sysTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .ToList();

            var existing = sysTypes.FirstOrDefault(s => s.SystemClassification == classification);
            if (existing != null) return existing.Id;

            try
            {
                var created = PipingSystemType.Create(doc, classification, defaultName);
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
            ElementId sanSysId = GetOrCreateSystemTypeId(doc, MEPSystemClassification.Sanitary, "Esgoto Sanitário - Bahr Hidro");

            foreach (var pipe in pipes)
            {
                try
                {
                    var sysParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                    if (sysParam == null || sysParam.IsReadOnly) continue;

                    string cmt = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    bool isSewer = cmt.Contains("ESG |") || cmt.Contains("UHC=");

                    ElementId currentId = sysParam.AsElementId();
                    var currentSys = doc.GetElement(currentId) as PipingSystemType;
                    if (currentSys == null ||
                        currentSys.SystemClassification == MEPSystemClassification.OtherPipe ||
                        currentSys.SystemClassification == MEPSystemClassification.UndefinedSystemClassification)
                    {
                        ElementId targetId = (isSewer && sanSysId != ElementId.InvalidElementId) ? sanSysId : cwSysId;
                        if (targetId != ElementId.InvalidElementId)
                            sysParam.Set(targetId);
                    }
                }
                catch { }
            }

            // Auto-cura topológica antes do cálculo hidráulico
            PipeFittingAutoConnector.ConnectPipesWithElbows(doc, pipes);
        }

        public static List<Element> GetAllPlumbingFixtures(Document doc)
        {
            var results = new List<Element>();
            if (doc == null) return results;

            results.AddRange(new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .ToList());

            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linkDoc = link.GetLinkDocument();
                if (linkDoc != null)
                {
                    results.AddRange(new FilteredElementCollector(linkDoc)
                        .WhereElementIsNotElementType()
                        .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                        .ToList());
                }
            }

            return results;
        }
    }
}
