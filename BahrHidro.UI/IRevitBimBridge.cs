using System.Threading.Tasks;
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
        string DisciplineMode,
        long PipeTypeId,
        long PipingSystemTypeId,
        long? LevelId,
        bool RouteFromAbove,
        double TrunkOffsetMeters,
        double InitialDiameterMm,
        bool AvoidSteelFrameClashes,
        bool InsertValvesAutomatically,
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

    public sealed record VisualAuditSummaryResult(
        int ApprovedCount,
        int HighVelocityCount,
        int LowPressureCount,
        string Summary);

    public sealed record LsfPunchingAuditResult(
        int TotalHoles,
        int ApprovedHoles,
        int WarningHoles,
        string Summary,
        List<string> HoleDetails);

    public sealed record MultilevelRiserResult(
        int RisersCreated,
        int LevelsConnected,
        int PipesCreated,
        int FittingsCreated,
        int ClashesAvoided,
        string Summary,
        List<string> Details);

    public sealed record SpatialCacheStatsResult(
        int StudsIndexed,
        int WallsIndexed,
        int ObstaclesIndexed,
        double ElapsedMs,
        bool WasCacheHit,
        string Summary);

    public sealed record NetworkCleanupResult(
        int PipesDeleted,
        int FittingsDeleted,
        int ViewsDeleted,
        string Summary);

    public interface IRevitBimBridge
    {Task<RevitNetworkImportData> ImportNetworkAutomaticallyFromRevit();Task ImportNetworkFromRevit(Window ownerWindow, Action<RevitNetworkImportData> onNetworkImported);Task<int> ApplyDiametersToRevit(NetworkCalculationResult result);Task<(string ProjectName, string ClientName, string Address, string Author)> GetProjectInfo();Task SaveProjectInfo(string projectName, string clientName, string address, string author);Task<string> GetModelOverviewSummary();Task<Dictionary<string, int>> ImportAllSewerFixturesFromRevit();Task<Dictionary<string, int>> ImportSewerFixturesFromRevit(Window ownerWindow);Task<RevitRoofData?> CaptureAllRoofsFromRevit();Task<RevitRoofData?> CaptureRoofFromRevit(Window ownerWindow);Task<RevitPopulationEstimate> EstimateBuildingPopulation();Task<RevitTemplateRoutingOptions> GetRoutingTemplateOptions();Task<List<DetectedRoutingPoint>> DetectRoutingPointsFromModel(long? levelId);Task<DetectedRoutingPoint?> PickManualRoutingPoint(Window ownerWindow, string fixtureCategory, long? levelId);Task<RevitAutoRoutingResult> ExecuteAutomaticPipeRouting(RevitAutoRoutingRequest request);Task<BimMaterialTakeoff> ExtractBomFromRevit(string projectName, double wasteMarginPercent);Task<(int TaggedCount, string Message)> TagPipesInActiveView();Task<(int ViewsCreated, int TotalTagsPlaced, string Summary)> GenerateWetZoneIsometricViews();Task<ClashCheckSummaryResult> RunHydraulicClashCheck();Task<SheetGenerationResult> GenerateHydraulicSheets();Task<(int ViewsCreated, int TotalTagsPlaced, string Summary)> GenerateHydraulicWallElevations();Task<(int DeletedCount, string Summary)> ClearGeneratedRouting(string disciplineMode);Task<VisualAuditSummaryResult> ApplyHydraulicVisualAudit(NetworkCalculationResult result);Task<(int ClearedCount, string Summary)> ClearHydraulicVisualAudit();Task<LsfPunchingAuditResult> AuditLsfStudPunching();Task<(int ViewsAnnotated, int CalloutsPlaced, string Summary)> ApplyFabricationCoordinatesToElevations();Task<MultilevelRiserResult> GenerateMultilevelRisers();
Task<SpatialCacheStatsResult> RebuildSpatialCache();
Task<NetworkCleanupResult> CleanGeneratedNetwork();
    
        Task<string> RunOrderedFullAutomationAsync();
        Task<string> RunColdWaterOnlyAsync();
        Task<string> RunHotWaterOnlyAsync();
        Task<string> RunSanitaryOnlyAsync();
        Task<string> OpenLiveViewByNameAsync(string viewName);
}
}
