using System;
using System.Collections.Generic;
using System.Windows;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.Core.Reporting;

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

    public sealed class DetectedRoutingPoint
    {
        public bool IsSelected { get; set; } = true;
        public string Name { get; set; } = "";
        public string FixtureCategory { get; set; } = "";
        public string SourceType { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double BaseZ { get; set; }
        public double PointHeightMeters { get; set; } = 0.60;
        public double OutwardDirX { get; set; } = 0.0;
        public double OutwardDirY { get; set; } = 1.0;
        public double Weight { get; set; } = 0.4;
        public long? HostElementId { get; set; }
        public string CoordinatesDisplay => $"X:{X:F2} Y:{Y:F2}";
    }

    public sealed record RevitAutoRoutingRequest(
        string DisciplineMode, // "ColdWater" ou "SanitarySewer"
        long PipeTypeId,
        long PipingSystemTypeId,
        long? LevelId,
        bool RouteFromAbove,
        double TrunkOffsetMeters,
        double InitialDiameterMm,
        bool AvoidSteelFrameClashes,
        List<DetectedRoutingPoint> Points);

    public sealed record RevitAutoRoutingResult(
        int FixturesConnected,
        int PipesCreated,
        int FittingsCreated,
        int SteelClashesAvoided,
        double TotalLengthMeters,
        string Summary,
        List<string> Logs);

    public sealed record ClashCheckSummaryResult(
        int TotalElementsChecked,
        int ClashesFound,
        string Summary,
        List<string> ClashDetails);

    public sealed record SheetGenerationResult(
        int SheetsCreated,
        int ViewportsPlaced,
        string Summary);

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
        List<DetectedRoutingPoint> DetectRoutingPointsFromModel(long? levelId);
        DetectedRoutingPoint? PickManualRoutingPoint(Window ownerWindow, string fixtureCategory, long? levelId);
        RevitAutoRoutingResult ExecuteAutomaticPipeRouting(RevitAutoRoutingRequest request);
        BimMaterialTakeoff ExtractBomFromRevit(string projectName, double wasteMarginPercent);
        (int TaggedCount, string Message) TagPipesInActiveView();
        (int ViewsCreated, int TotalTagsPlaced, string Summary) GenerateWetZoneIsometricViews();
        ClashCheckSummaryResult RunHydraulicClashCheck();
        SheetGenerationResult GenerateHydraulicSheets();
    }
}
