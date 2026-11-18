using System;
using System.Windows;
using BahrHidro.Core.Hydraulic.Network;

namespace BahrHidro.UI
{
    public interface IRevitBimBridge
    {
        void ImportNetworkFromRevit(Window ownerWindow, Action<HydraulicNetwork> onNetworkImported);
        int ApplyDiametersToRevit(NetworkCalculationResult result);
    }
}
