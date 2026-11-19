using System;
using System.Windows;
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
        private static MainWindow? _activeWindow;
        private static RevitExternalEventRunner? _eventRunner;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (_activeWindow != null && _activeWindow.IsLoaded)
                {
                    if (_activeWindow.WindowState == WindowState.Minimized)
                        _activeWindow.WindowState = WindowState.Normal;

                    _activeWindow.Activate();
                    return Result.Succeeded;
                }

                _eventRunner ??= new RevitExternalEventRunner();
                var bridge = new RevitBimBridge(commandData.Application, _eventRunner);

                var window = new MainWindow(bridge);
                new WindowInteropHelper(window)
                {
                    Owner = commandData.Application.MainWindowHandle
                };

                window.Closed += (_, _) => _activeWindow = null;
                _activeWindow = window;
                window.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
