using System;
using System.Windows;
using BahrHidro.Core.Hydraulic.Network;

namespace BahrHidro.UI
{
    public interface IRevitBimBridge
    {
        void ImportNetworkFromRevit(Window ownerWindow, Action<HydraulicNetwork> onNetworkImported);
        int ApplyDiametersToRevit(NetworkCalculationResult result);
        (string ProjectName, string ClientName, string Address, string Author) GetProjectInfo();
        void SaveProjectInfo(string projectName, string clientName, string address, string author);
    }
}
