using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    [Transaction(TransactionMode.Manual)]
    public class QuickVisualAuditCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!BahrHidro.UI.LicenseService.EnsureLicensed()) return Result.Cancelled;
            try
            {
                if (commandData.Application.ActiveUIDocument == null)
                    throw new InvalidOperationException("Abra um projeto no Revit antes de executar a auditoria.");

                var bridge = new RevitBimBridge(commandData.Application);
                var res = bridge.ExecuteVisualAuditSync();
                TaskDialog.Show("Bahr Hidro - Auditoria Visual 3D", res.Summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Bahr Hidro - Auditoria Visual 3D", ex.Message);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class QuickLsfPunchingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!BahrHidro.UI.LicenseService.EnsureLicensed()) return Result.Cancelled;
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument
                    ?? throw new InvalidOperationException("Abra um projeto no Revit antes de validar a furação LSF.");

                var res = RevitLsfFabricationManager.RunLsfPunchingAudit(uidoc.Document);
                TaskDialog.Show("Bahr Hidro - Validação de Furação LSF", res.Summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Bahr Hidro - Validação de Furação LSF", ex.Message);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class QuickExecutiveSheetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!BahrHidro.UI.LicenseService.EnsureLicensed()) return Result.Cancelled;
            try
            {
                if (commandData.Application.ActiveUIDocument == null)
                    throw new InvalidOperationException("Abra um projeto no Revit antes de gerar as pranchas.");

                var bridge = new RevitBimBridge(commandData.Application);
                var res = bridge.ExecuteSheetGenerationSync();
                TaskDialog.Show("Bahr Hidro - Pranchas Executivas LSF", res.Summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Bahr Hidro - Pranchas Executivas LSF", ex.Message);
                return Result.Failed;
            }
        }
    }
}
