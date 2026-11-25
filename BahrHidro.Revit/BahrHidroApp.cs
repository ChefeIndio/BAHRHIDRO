using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    public class BahrHidroApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            TracadoAutomaticoRevitService.Inicializar(application);
            IsometricoExecutivoService.Inicializar(application);
            ProdutividadeBimRevitService.Inicializar(application);
            DetalhamentoFinoRevitService.Inicializar(application);
            try { BahrHidro.UI.UiNavigationController.Initialize(); } catch { }

            try
            {
                const string tabName = "Smart Hidro";
                try { application.CreateRibbonTab(tabName); } catch { }

                try { application.CreateRibbonTab("Smart Hidro"); } catch { }
                RibbonPanel panel = application.CreateRibbonPanel("Smart Hidro", "Hidráulica & LSF • Licenciado");
                string asmPath = Assembly.GetExecutingAssembly().Location;

                panel.AddItem(new PushButtonData(
                    "BahrHidro_Main",
                    "Abrir\nSmart Hidro",
                    asmPath,
                    "BahrHidro.Revit.MainCommand")
                {
                    ToolTip = "Abre a plataforma Smart Hidro BIM (Água Fria, Água Quente, Esgoto NBR 8160, Pluvial, Vistas 3D ao Vivo e Steel Frame).",
                    LargeImage = RibbonIconFactory.CreateMainIcon()
                });
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Smart Hidro - Inicialização", ex.Message);
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
