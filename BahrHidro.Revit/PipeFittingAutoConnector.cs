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
            if (doc == null) return false;
            FamilyLoader.EnsureEssentialFittings(doc);

            bool anyOk = false;
            if (pipeTypeId != ElementId.InvalidElementId && doc.GetElement(pipeTypeId) is PipeType targetPt)
                anyOk = EnsureRoutingPreferences(doc, targetPt);

            // Garante regras de conexão em todos os PipeTypes do projeto
            foreach (var pt in new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>())
            {
                if (EnsureRoutingPreferences(doc, pt))
                    anyOk = true;
            }
            return anyOk;
        }

        public static bool EnsureRoutingPreferences(Document doc, PipeType? pipeType)
        {
            if (doc == null || pipeType == null) return false;

            var rpm = pipeType.RoutingPreferenceManager;
            if (rpm == null) return false;

            var fittingSymbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            if (fittingSymbols.Count == 0) return false;

            bool okElbow = EnsureRuleGroup(doc, rpm, fittingSymbols, RoutingPreferenceRuleGroupType.Elbows, PartType.Elbow, new[] { "joelho", "elbow", "curva" });
            EnsureRuleGroup(doc, rpm, fittingSymbols, RoutingPreferenceRuleGroupType.Junctions, PartType.Tee, new[] { "te ", "tê", "tee", "junção", "juncao" });
            EnsureRuleGroup(doc, rpm, fittingSymbols, RoutingPreferenceRuleGroupType.Transitions, PartType.Transition, new[] { "redu", "transi", "bucha", "luva" });
            return okElbow;
        }

        private static bool EnsureRuleGroup(
            Document doc,
            RoutingPreferenceManager rpm,
            List<FamilySymbol> symbols,
            RoutingPreferenceRuleGroupType groupType,
            PartType targetPartType,
            string[] nameHints)
        {
            if (rpm.GetNumberOfRules(groupType) > 0)
            {
                var existing = rpm.GetRule(groupType, 0);
                if (existing != null && existing.MEPPartId != ElementId.InvalidElementId)
                {
                    if (doc.GetElement(existing.MEPPartId) is FamilySymbol existingSym)
                    {
                        if (!existingSym.IsActive)
                        {
                            existingSym.Activate();
                            doc.Regenerate();
                        }
                        return true;
                    }
                }
            }

            FamilySymbol? symbol = symbols.FirstOrDefault(s =>
            {
                var p = s.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                return p != null && p.AsInteger() == (int)targetPartType;
            });

            symbol ??= symbols.FirstOrDefault(s =>
            {
                string full = $" {s.FamilyName} {s.Name} ".ToLowerInvariant();
                return nameHints.Any(h => full.Contains(h));
            });

            if (symbol == null) return false;

            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            rpm.AddRule(groupType, new RoutingPreferenceRule(symbol.Id, $"Regra Automática Bahr Hidro ({groupType})"), 0);
            return true;
        }

        public static int ConnectPipesWithElbows(Document doc, IEnumerable<Pipe> createdPipes)
        {
            var pipes = createdPipes?.Where(p => p != null && p.IsValidObject).Distinct().ToList();
            if (doc == null || pipes == null || pipes.Count == 0) return 0;

            PipeNetworkHelper.NormalizePipesForSizing(doc, pipes);
            EnsureElbowRoutingPreference(doc, pipes[0].GetTypeId());
            doc.Regenerate();

            int connectedCount = 0;
            const double tolFeet = 0.05; // ~15 mm

            // 1. Conecta pontas coincidentes (Joelho para ângulos, Luva/Redução ou ConnectTo para colineares)
            for (int i = 0; i < pipes.Count; i++)
            {
                foreach (var cA in GetOpenEndConnectors(pipes[i]))
                {
                    if (cA.IsConnected) continue;
                    for (int j = i + 1; j < pipes.Count; j++)
                    {
                        foreach (var cB in GetOpenEndConnectors(pipes[j]))
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
                                        try { cA.ConnectTo(cB); connectedCount++; } catch { }
                                    }
                                }
                                else if (Math.Abs(cA.Radius - cB.Radius) > 0.001)
                                {
                                    try
                                    {
                                        doc.Create.NewTransitionFitting(cA, cB);
                                        connectedCount++;
                                    }
                                    catch
                                    {
                                        try { cA.ConnectTo(cB); connectedCount++; } catch { }
                                    }
                                }
                                else
                                {
                                    try { cA.ConnectTo(cB); connectedCount++; } catch { }
                                }
                            }
                        }
                    }
                }
            }

            // 2. Conecta ramais que encostam no meio de outro tubo com Tê (quebrando o tronco)
            var workingPipes = new List<Pipe>(pipes);
            foreach (var branch in pipes)
            {
                if (!branch.IsValidObject) continue;
                foreach (var cBranch in GetOpenEndConnectors(branch))
                {
                    if (cBranch.IsConnected) continue;
                    XYZ pt = cBranch.Origin;

                    for (int k = 0; k < workingPipes.Count; k++)
                    {
                        var host = workingPipes[k];
                        if (!host.IsValidObject || host.Id == branch.Id) continue;
                        if (host.Location is not LocationCurve lc || lc.Curve is not Line line) continue;

                        XYZ p0 = line.GetEndPoint(0);
                        XYZ p1 = line.GetEndPoint(1);
                        if (pt.DistanceTo(p0) <= tolFeet || pt.DistanceTo(p1) <= tolFeet) continue;

                        IntersectionResult proj = line.Project(pt);
                        if (proj != null && proj.Distance <= tolFeet)
                        {
                            try
                            {
                                ElementId splitId = PlumbingUtils.BreakCurve(doc, host.Id, proj.XYZPoint);
                                if (splitId != ElementId.InvalidElementId && doc.GetElement(splitId) is Pipe splitPipe)
                                {
                                    workingPipes.Add(splitPipe);
                                    var cHost1 = GetClosestOpenEnd(host, proj.XYZPoint, tolFeet);
                                    var cHost2 = GetClosestOpenEnd(splitPipe, proj.XYZPoint, tolFeet);
                                    if (cHost1 != null && cHost2 != null)
                                    {
                                        try
                                        {
                                            doc.Create.NewTeeFitting(cHost1, cHost2, cBranch);
                                            connectedCount++;
                                        }
                                        catch
                                        {
                                            try { cHost1.ConnectTo(cHost2); cHost1.ConnectTo(cBranch); connectedCount++; } catch { }
                                        }
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }

            return connectedCount;
        }

        private static Connector? GetClosestOpenEnd(Pipe pipe, XYZ target, double tol)
        {
            return GetOpenEndConnectors(pipe)
                .OrderBy(c => c.Origin.DistanceTo(target))
                .FirstOrDefault(c => c.Origin.DistanceTo(target) <= tol);
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
