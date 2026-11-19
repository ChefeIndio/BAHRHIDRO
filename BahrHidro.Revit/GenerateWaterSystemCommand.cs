using System;
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
                TaskDialog.Show(
                    "Bahr Hidro — Água Fria e Água Quente",
                    pipes.Count > 0
                        ? $"Traçado pressurizado (NBR 5626 / NBR 7198) concluído!\n\n" +
                          $"• Trechos gerados (AF + AQ): {pipes.Count}\n" +
                          $"• Isolamento térmico aplicado automaticamente na Água Quente\n" +
                          $"• Vistas atualizadas: Planta Hidráulica Executiva e Isométrico 3D Vivo"
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
