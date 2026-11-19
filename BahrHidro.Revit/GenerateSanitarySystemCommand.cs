using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    [Transaction(TransactionMode.Manual)]
    public class GenerateSanitarySystemCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null || uidoc.Document == null)
                return Result.Cancelled;

            Document doc = uidoc.Document;
            try
            {
                var pipes = SanitarySewerRoutingEngine.GenerateSanitaryNetworkOnly(doc, out var report);

                var combined3D = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(LsfPanelElevationGenerator.View3DCombinedName, StringComparison.OrdinalIgnoreCase));

                if (combined3D != null)
                    uidoc.ActiveView = combined3D;

                TaskDialog.Show(
                    "Bahr Hidro — 3º Esgoto Sanitário e Ventilação (NBR 8160)",
                    pipes.Count > 0
                        ? $"Rede de Esgoto e Ventilação gerada com sucesso!\n\n" +
                          $"• Zonas molhadas: {report.ZonesCount} | Segmentos: {report.PipesCreated}\n" +
                          $"• Contribuição total: {report.TotalUhc} UHC | Subcoletor: DN {(int)report.MaxCollectorDnMm}\n" +
                          $"• Vistas 3D ao vivo atualizadas:\n" +
                          $"  1. {LsfPanelElevationGenerator.View3DSanitaryName}\n" +
                          $"  2. {LsfPanelElevationGenerator.View3DCombinedName}"
                        : "Nenhum aparelho sanitário de esgoto encontrado no modelo ou vínculos IFC.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Bahr Hidro — Erro", $"Falha ao gerar rede de esgoto: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
