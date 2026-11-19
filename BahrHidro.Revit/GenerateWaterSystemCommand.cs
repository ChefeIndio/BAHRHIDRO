using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    [Transaction(TransactionMode.Manual)]
    public class GenerateWaterSystemCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
                return Result.Cancelled;

            Document doc = uidoc.Document;
            try
            {
                var pipes = SmartHydraulicRoutingEngine.GeneratePressurizedWaterOnly(doc);

                var combined3D = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(LsfPanelElevationGenerator.View3DCombinedName, StringComparison.OrdinalIgnoreCase));

                if (combined3D != null)
                    uidoc.ActiveView = combined3D;

                TaskDialog.Show(
                    "Bahr Hidro — 1º Água Fria e 2º Água Quente",
                    pipes.Count > 0
                        ? $"Sequência 1º Água Fria -> 2º Água Quente concluída!\n\n" +
                          $"• Trechos gerados (AF + AQ): {pipes.Count}\n" +
                          $"• Vistas 3D ao vivo criadas/atualizadas:\n" +
                          $"  1. {LsfPanelElevationGenerator.View3DColdWaterName}\n" +
                          $"  2. {LsfPanelElevationGenerator.View3DHotWaterName}\n" +
                          $"  3. {LsfPanelElevationGenerator.View3DCombinedName}"
                        : "Nenhum aparelho sanitário de consumo encontrado no modelo ou vínculos IFC.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Bahr Hidro — Erro", $"Falha ao gerar rede de água: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
