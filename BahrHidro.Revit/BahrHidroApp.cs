using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    public class BahrHidroApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try { BahrHidro.UI.UiNavigationController.Initialize(); } catch { }

            try
            {
                const string tabName = "Bahr Hidro";
                try { application.CreateRibbonTab(tabName); } catch { }

                RibbonPanel panel = application.CreateRibbonPanel(tabName, "Hidráulica & LSF • Licenciado");
                string asmPath = Assembly.GetExecutingAssembly().Location;

                panel.AddItem(new PushButtonData(
                    "BahrHidro_Main",
                    "Abrir\nBahr Hidro",
                    asmPath,
                    "BahrHidro.Revit.MainCommand")
                {
                    ToolTip = "Abre a plataforma Bahr Hidro BIM (Água Fria, Água Quente, Esgoto NBR 8160, Pluvial, Vistas 3D ao Vivo e Steel Frame).",
                    LargeImage = RibbonIconFactory.CreateMainIcon()
                });
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Bahr Hidro - Inicialização", ex.Message);
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
