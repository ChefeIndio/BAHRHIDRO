using System;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenBahrHidroLogsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BahrHidro",
                "Logs");
            Directory.CreateDirectory(logDir);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Bahr Hidro — Memoriais", $"Pasta dos relatórios:\n{logDir}\n\nErro ao abrir: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
