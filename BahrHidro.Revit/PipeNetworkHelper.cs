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

        public static ElementId GetOrCreateHotWaterSystemTypeId(Document doc)
        {
            return GetOrCreateSystemTypeId(doc, MEPSystemClassification.DomesticHotWater, "Água Quente - Bahr Hidro");
        }

        public static ElementId GetOrCreateSystemTypeId(Document doc, MEPSystemClassification classification, string defaultName)
        {
            if (doc == null) return ElementId.InvalidElementId;

            var sysTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .ToList();

            var named = sysTypes.FirstOrDefault(s =>
                s.SystemClassification == classification &&
                s.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named.Id;

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
            ElementId hwSysId = GetOrCreateHotWaterSystemTypeId(doc);
            ElementId sanSysId = GetOrCreateSystemTypeId(doc, MEPSystemClassification.Sanitary, "Esgoto Sanitário - Bahr Hidro");
            ElementId ventSysId = GetOrCreateSystemTypeId(doc, MEPSystemClassification.Vent, "Ventilação Sanitária - Bahr Hidro");

            foreach (var pipe in pipes)
            {
                try
                {
                    var sysParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                    if (sysParam == null || sysParam.IsReadOnly) continue;

                    string cmt = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;

                    ElementId currentId = sysParam.AsElementId();
                    var currentSys = doc.GetElement(currentId) as PipingSystemType;
                    if (currentSys == null ||
                        currentSys.SystemClassification == MEPSystemClassification.OtherPipe ||
                        currentSys.SystemClassification == MEPSystemClassification.UndefinedSystemClassification)
                    {
                        ElementId targetId = cwSysId;
                        if (cmt.Contains("AQ |")) targetId = hwSysId;
                        else if (cmt.Contains("VENT |")) targetId = ventSysId;
                        else if (cmt.Contains("ESG |") || cmt.Contains("AP |") || cmt.Contains("UHC=")) targetId = sanSysId;

                        if (targetId != ElementId.InvalidElementId)
                            sysParam.Set(targetId);
                    }
                }
                catch { }
            }
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
