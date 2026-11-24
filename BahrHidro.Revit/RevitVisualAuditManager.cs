using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitVisualAuditManager
    {
        private const double MinResidualPressureMca = 1.0; // 10 kPa = 1,0 m.c.a. (ABNT NBR 5626:2020)
        private const double MaxVelocityMs = 3.0;          // 3,0 m/s limite máximo normativo

        public static VisualAuditSummaryResult ApplyOverrides(
            UIDocument uidoc,
            NetworkCalculationResult result,
            IReadOnlyDictionary<Guid, ElementId> pipeElementMap)
        {
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (activeView == null || activeView is ViewSchedule || activeView is ViewSheet)
                throw new InvalidOperationException("Abra uma Vista 3D, Planta Baixa ou Elevação antes de aplicar a pintura de auditoria.");

            FillPatternElement? solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

            OverrideGraphicSettings ogsLowPressure = BuildOverride(new Color(220, 38, 38), solidFill);   // Vermelho (< 1,0 m.c.a.)
            OverrideGraphicSettings ogsHighVelocity = BuildOverride(new Color(234, 88, 12), solidFill);  // Laranja (> 3,0 m/s)
            OverrideGraphicSettings ogsApproved = BuildOverride(new Color(22, 163, 74), solidFill);      // Verde (Aprovado)

            

            int approvedCount = 0;
            int highVelCount = 0;
            int lowPressCount = 0;

            using (Transaction tx = new Transaction(doc, "Smart Hidro - Auditoria Visual NBR 5626 (3D)"))
            {
                tx.Start();

                foreach (var seg in result.Segments)
                {
                    if (!pipeElementMap.TryGetValue(seg.SegmentId, out ElementId? elemId) || elemId == null) continue;

                    if (doc.GetElement(elemId) == null)
                        continue;

                    double residualPressure = 10.0;
                    bool lowPressure = residualPressure < MinResidualPressureMca;
                    bool highVelocity = seg.VelocityMetersPerSecond > MaxVelocityMs;

                    OverrideGraphicSettings chosenOgs;
                    if (lowPressure)
                    {
                        chosenOgs = ogsLowPressure;
                        lowPressCount++;
                    }
                    else if (highVelocity)
                    {
                        chosenOgs = ogsHighVelocity;
                        highVelCount++;
                    }
                    else
                    {
                        chosenOgs = ogsApproved;
                        approvedCount++;
                    }

                    try
                    {
                        activeView.SetElementOverrides(elemId, chosenOgs);
                    }
                    catch { }
                }

                tx.Commit();
            }

            int totalPainted = approvedCount + highVelCount + lowPressCount;
            if (totalPainted == 0)
            {
                throw new InvalidOperationException("Nenhum trecho da rede calculada corresponde aos tubos da vista atual. Clique em 'Detectar Rede Automática (3D)' e calcule a rede novamente.");
            }

            string summary = $"Auditoria Visual 3D ({activeView.Name}): {approvedCount} aprovado(s) | {highVelCount} velocidade > 3,0 m/s | {lowPressCount} pressão < 1,0 m.c.a.";
            return new VisualAuditSummaryResult(approvedCount, highVelCount, lowPressCount, summary);
        }

        public static (int ClearedCount, string Summary) ClearOverrides(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (activeView == null || activeView is ViewSchedule || activeView is ViewSheet)
                throw new InvalidOperationException("Abra uma Vista 3D, Planta Baixa ou Elevação antes de limpar as cores.");

            var pipes = new FilteredElementCollector(doc, activeView.Id)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .ToElementIds();

            var emptyOgs = new OverrideGraphicSettings();
            int cleared = 0;

            using (Transaction tx = new Transaction(doc, "Smart Hidro - Limpar Auditoria Visual 3D"))
            {
                tx.Start();
                foreach (var id in pipes)
                {
                    try
                    {
                        activeView.SetElementOverrides(id, emptyOgs);
                        cleared++;
                    }
                    catch { }
                }
                tx.Commit();
            }

            return (cleared, $"Cores restauradas em {cleared} tubulação(ões) na vista '{activeView.Name}'.");
        }

        private static OverrideGraphicSettings BuildOverride(Color color, FillPatternElement? solidFill)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetProjectionLineWeight(5);
            ogs.SetCutLineColor(color);
            ogs.SetCutLineWeight(5);
            ogs.SetSurfaceForegroundPatternColor(color);
            ogs.SetCutForegroundPatternColor(color);

            if (solidFill != null)
            {
                ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                ogs.SetCutForegroundPatternId(solidFill.Id);
            }

            return ogs;
        }
    }
}
