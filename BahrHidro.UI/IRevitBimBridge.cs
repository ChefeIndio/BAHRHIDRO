using System;
using System.Collections.Generic;
using System.Windows;
using BahrHidro.Core.Hydraulic.Network;

namespace BahrHidro.UI
{
    public sealed record RevitNetworkImportData(
        HydraulicNetwork Network,
        Dictionary<Guid, double> OriginalDiametersMm,
        string Summary = "");

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

    public sealed record RevitLookupItem(long Id, string Name);

    public sealed record RevitLevelLookupItem(long Id, string Name, double ElevationMeters);

    public sealed record RevitTemplateRoutingOptions(
        List<RevitLookupItem> PipeTypes,
        List<RevitLookupItem> PipingSystemTypes,
        List<RevitLevelLookupItem> Levels);

    public sealed record RevitAutoRoutingRequest(
        long PipeTypeId,
        long PipingSystemTypeId,
        long? LevelId,
        bool RouteFromAbove,
        double TrunkOffsetMeters,
        double InitialDiameterMm,
        bool SelectedFixturesOnly);

    public sealed record RevitAutoRoutingResult(
        int FixturesConnected,
        int PipesCreated,
        int FittingsCreated,
        double TotalLengthMeters,
        string Summary,
        List<string> Logs);

    public interface IRevitBimBridge
    {
        RevitNetworkImportData ImportNetworkAutomaticallyFromRevit();
        void ImportNetworkFromRevit(Window ownerWindow, Action<RevitNetworkImportData> onNetworkImported);
        int ApplyDiametersToRevit(NetworkCalculationResult result);
        (string ProjectName, string ClientName, string Address, string Author) GetProjectInfo();
        void SaveProjectInfo(string projectName, string clientName, string address, string author);
        string GetModelOverviewSummary();
        Dictionary<string, int> ImportAllSewerFixturesFromRevit();
        Dictionary<string, int> ImportSewerFixturesFromRevit(Window ownerWindow);
        RevitRoofData? CaptureAllRoofsFromRevit();
        RevitRoofData? CaptureRoofFromRevit(Window ownerWindow);
        RevitPopulationEstimate EstimateBuildingPopulation();
        RevitTemplateRoutingOptions GetRoutingTemplateOptions();
        RevitAutoRoutingResult ExecuteAutomaticPipeRouting(Window ownerWindow, RevitAutoRoutingRequest request);
    }
}
