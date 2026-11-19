using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    public class BahrHidroApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tabName = "Bahr Hidro";
            try { application.CreateRibbonTab(tabName); } catch { }

            RibbonPanel panel = application.CreateRibbonPanel(tabName, "Hidráulica & LSF");
            string asmPath = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData("BahrHidro_Main", "Abrir\nBahr Hidro", asmPath, "BahrHidro.Revit.MainCommand")
            {
                ToolTip = "Abre o painel flutuante do Bahr Hidro (Dimensionamento, Traçado LSF, Auditoria 3D e Pranchas)."
            });

            panel.AddSeparator();

            panel.AddItem(new PushButtonData("BahrHidro_QuickAudit", "Auditoria 3D\nPressão/Vel.", asmPath, "BahrHidro.Revit.QuickVisualAuditCommand")
            {
                ToolTip = "Pinta os tubos da vista ativa conforme velocidade e pressão dinâmica da NBR 5626."
            });

            panel.AddItem(new PushButtonData("BahrHidro_QuickLsf", "Validar\nFuração LSF", asmPath, "BahrHidro.Revit.QuickLsfPunchingCommand")
            {
                ToolTip = "Audita os cruzamentos em montantes Steel Frame usando o cache espacial 2D."
            });

            panel.AddItem(new PushButtonData("BahrHidro_QuickSheets", "Gerar Pranchas\n(1:20 / 1:25)", asmPath, "BahrHidro.Revit.QuickExecutiveSheetsCommand")
            {
                ToolTip = "Gera automaticamente as vistas de elevação frontal (1:20), isométricos (1:25) e pranchas executivas."
            });

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
