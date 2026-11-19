using System;
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
                TaskDialog.Show(
                    "Bahr Hidro — Esgoto Sanitário e Ventilação (NBR 8160)",
                    pipes.Count > 0
                        ? $"Rede de Esgoto e Ventilação gerada com sucesso!\n\n" +
                          $"• Zonas molhadas / prumadas: {report.ZonesCount}\n" +
                          $"• Segmentos gerados (ESG + VENT): {report.PipesCreated}\n" +
                          $"• Contribuição total: {report.TotalUhc} UHC\n" +
                          $"• Subcoletor predial (i = 1%): DN {(int)report.MaxCollectorDnMm}\n" +
                          $"• Vistas criadas: Planta de Esgoto e Ventilação + Isométrico Esgoto 3D"
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
