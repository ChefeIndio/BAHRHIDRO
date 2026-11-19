using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    public class BahrHidroApp : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                const string tabName = "Bahr Hidro";
                try { application.CreateRibbonTab(tabName); } catch { }

                RibbonPanel panel = application.CreateRibbonPanel(tabName, "Hidráulica & LSF • Licenciado");
            try
            {
                var pbdWater = new PushButtonData("BH_WaterOnly", "Água Fria/Quente", System.Reflection.Assembly.GetExecutingAssembly().Location, "BahrHidro.Revit.GenerateWaterSystemCommand")
                {
                    ToolTip = "Traça e dimensiona apenas Água Fria (NBR 5626) e Água Quente isolada (NBR 7198) nas paredes LSF."
                };
                var pbdSanitary = new PushButtonData("BH_SanitaryOnly", "Esgoto (NBR 8160)", System.Reflection.Assembly.GetExecutingAssembly().Location, "BahrHidro.Revit.GenerateSanitarySystemCommand")
                {
                    ToolTip = "Traça e dimensiona separadamente Esgoto Sanitário (i=2% e 1%), Caixa Sifonada, Caixa de Gordura e Ventilação."
                };
                panel.AddItem(pbdWater);
                panel.AddItem(pbdSanitary);
                var pbdLogs = new PushButtonData("BH_OpenLogs", "Memoriais & CSV", System.Reflection.Assembly.GetExecutingAssembly().Location, "BahrHidro.Revit.OpenBahrHidroLogsCommand") { ToolTip = "Abre a pasta com os memoriais de cálculo (NBR 5626/8160) e a tabela CSV de furação LSF." };
                panel.AddItem(pbdLogs);
            }
            catch { }

                Assembly asm = Assembly.GetExecutingAssembly();
                string asmPath = asm.Location;

                var mainCmd = asm.GetTypes().FirstOrDefault(t =>
                    typeof(IExternalCommand).IsAssignableFrom(t) &&
                    !t.IsAbstract &&
                    t.IsPublic &&
                    !t.Name.StartsWith("Quick"));

                if (mainCmd != null)
                {
                    panel.AddItem(new PushButtonData("BahrHidro_Main", "Abrir\nBahr Hidro", asmPath, mainCmd.FullName!)
                    {
                        ToolTip = "Abre o painel flutuante do Bahr Hidro.",
                        LargeImage = RibbonIconFactory.CreateMainIcon()
                    });
                }

                var quickBtns = new[]
                {
                    ("BahrHidro_QuickAudit", "Auditoria 3D\nPressão/Vel.", "BahrHidro.Revit.QuickVisualAuditCommand", "Auditoria visual NBR 5626."),
                    ("BahrHidro_QuickLsf", "Validar\nFuração LSF", "BahrHidro.Revit.QuickLsfPunchingCommand", "Valida furação em montantes LSF."),
                    ("BahrHidro_QuickSheets", "Gerar Pranchas\n(1:20 / 1:25)", "BahrHidro.Revit.QuickExecutiveSheetsCommand", "Gera elevações 1:20 e pranchas.")
                };

                bool addedSep = false;
                foreach (var (id, label, cls, tip) in quickBtns)
                {
                    if (asm.GetType(cls) == null) continue;
                    if (!addedSep && mainCmd != null) { panel.AddSeparator(); addedSep = true; }
                    panel.AddItem(new PushButtonData(id, label, asmPath, cls) { ToolTip = tip });
                }
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
