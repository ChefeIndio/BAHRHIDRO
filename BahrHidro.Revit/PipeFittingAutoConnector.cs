using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace BahrHidro.Revit
{
    public static class PipeFittingAutoConnector
    {
        public static bool EnsureElbowRoutingPreference(Document doc, ElementId pipeTypeId)
        {
            if (doc == null || pipeTypeId == ElementId.InvalidElementId) return false;
            return EnsureElbowRoutingPreference(doc, doc.GetElement(pipeTypeId) as PipeType);
        }

        public static bool EnsureElbowRoutingPreference(Document doc, PipeType? pipeType)
        {
            if (doc == null || pipeType == null) return false;

            var rpm = pipeType.RoutingPreferenceManager;
            if (rpm == null) return false;

            if (rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows) > 0)
            {
                var firstRule = rpm.GetRule(RoutingPreferenceRuleGroupType.Elbows, 0);
                if (firstRule != null && firstRule.MEPPartId != ElementId.InvalidElementId)
                    return true;
            }

            var fittingSymbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            FamilySymbol? elbowSymbol = fittingSymbols.FirstOrDefault(s =>
            {
                var p = s.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                return p != null && p.AsInteger() == (int)PartType.Elbow;
            });

            elbowSymbol ??= fittingSymbols.FirstOrDefault(s =>
            {
                string full = $"{s.FamilyName} {s.Name}".ToLowerInvariant();
                return full.Contains("joelho") || full.Contains("elbow") || full.Contains("curva");
            });

            if (elbowSymbol == null)
                return false;

            if (!elbowSymbol.IsActive)
            {
                elbowSymbol.Activate();
                doc.Regenerate();
            }

            var rule = new RoutingPreferenceRule(elbowSymbol.Id, "Joelho Automático Bahr Hidro");
            rpm.AddRule(RoutingPreferenceRuleGroupType.Elbows, rule, 0);
            return true;
        }

        public static int ConnectPipesWithElbows(Document doc, IEnumerable<Pipe> createdPipes)
        {
            var pipes = createdPipes?.Where(p => p != null && p.IsValidObject).Distinct().ToList();
            if (doc == null || pipes == null || pipes.Count < 2) return 0;

            EnsureElbowRoutingPreference(doc, pipes[0].GetTypeId());
            doc.Regenerate();

            int connectedCount = 0;
            const double tolFeet = 0.025; // ~7.6 mm

            for (int i = 0; i < pipes.Count; i++)
            {
                var cListA = GetOpenEndConnectors(pipes[i]);
                foreach (var cA in cListA)
                {
                    if (cA.IsConnected) continue;

                    for (int j = i + 1; j < pipes.Count; j++)
                    {
                        var cListB = GetOpenEndConnectors(pipes[j]);
                        foreach (var cB in cListB)
                        {
                            if (cB.IsConnected) continue;

                            if (cA.Origin.DistanceTo(cB.Origin) <= tolFeet)
                            {
                                double angle = cA.CoordinateSystem.BasisZ.AngleTo(cB.CoordinateSystem.BasisZ);
                                if (angle > 0.03 && angle < Math.PI - 0.03)
                                {
                                    try
                                    {
                                        doc.Create.NewElbowFitting(cA, cB);
                                        connectedCount++;
                                    }
                                    catch
                                    {
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return connectedCount;
        }

        private static List<Connector> GetOpenEndConnectors(Pipe pipe)
        {
            var result = new List<Connector>();
            var cm = pipe.ConnectorManager;
            if (cm == null) return result;

            foreach (Connector c in cm.Connectors)
            {
                if (c != null && c.ConnectorType == ConnectorType.End && !c.IsConnected)
                    result.Add(c);
            }
            return result;
        }
    }
}
