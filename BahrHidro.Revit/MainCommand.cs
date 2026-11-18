using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    [Transaction(TransactionMode.Manual)]
    public class MainCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                if (uidoc == null || uidoc.Document == null)
                {
                    TaskDialog.Show("Bahr Hidro BIM", "Abra um projeto no Revit antes de iniciar o Bahr Hidro.");
                    return Result.Cancelled;
                }

                var bridge = new RevitBimBridge(uidoc);
                var janela = new MainWindow(bridge);
                new WindowInteropHelper(janela)
                {
                    Owner = commandData.Application.MainWindowHandle
                };

                janela.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Bahr Hidro BIM - Erro", $"Falha ao abrir a interface:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
