using System;
using System.Collections.Generic;
using System.Windows;
using BahrHidro.Core.Hydraulic.Network;

namespace BahrHidro.UI
{
    public sealed record RevitRoofData(
        string RoofName,
        double AreaM2,
        double WidthMeters,
        double ProjectionMeters,
        double HeightMeters);

    public sealed record RevitPopulationEstimate(
        int EstimatedPeople,
        int BedroomCount,
        int TotalRoomsCount,
        string Summary);

    public interface IRevitBimBridge
    {
        void ImportNetworkFromRevit(Window ownerWindow, Action<HydraulicNetwork> onNetworkImported);
        int ApplyDiametersToRevit(NetworkCalculationResult result);
        (string ProjectName, string ClientName, string Address, string Author) GetProjectInfo();
        void SaveProjectInfo(string projectName, string clientName, string address, string author);
        Dictionary<string, int> ImportAllSewerFixturesFromRevit();
        Dictionary<string, int> ImportSewerFixturesFromRevit(Window ownerWindow);
        RevitRoofData? CaptureAllRoofsFromRevit();
        RevitRoofData? CaptureRoofFromRevit(Window ownerWindow);
        RevitPopulationEstimate EstimateBuildingPopulation();
    }
}
