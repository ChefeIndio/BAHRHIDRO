using System;
using System.Collections.Generic;

namespace BahrHidro.Core.Reporting
{
    public sealed record PipeTakeoffItem(
        string SystemName,
        string PipeTypeName,
        double DiameterMm,
        double NetLengthMeters,
        double WastePercentage,
        double TotalLengthWithWasteMeters,
        int CommercialBars6m);

    public sealed record FittingTakeoffItem(
        string SystemName,
        string CategoryName,
        string FamilyAndType,
        string SizeDescription,
        int Quantity);

    public sealed record KitTakeoffItem(
        string KitName,
        string SystemName,
        string ItemCategory,
        string Description,
        string SizeDescription,
        double Quantity,
        string Unit);

    public sealed record LsfPunchingHoleItem(
        string HoleId,
        string WallOrKitName,
        string StudName,
        string SystemName,
        double PipeDiameterMm,
        double RequiredHoleMm,
        double StudWebWidthMm,
        double OccupancyPercent,
        double PositionXFromWallStartMeters,
        double HeightHFromFloorMeters,
        string Status);

    public sealed record BimMaterialTakeoff(
        string ProjectName,
        double WasteMarginPercent,
        IReadOnlyList<PipeTakeoffItem> Pipes,
        IReadOnlyList<FittingTakeoffItem> Fittings,
        IReadOnlyList<KitTakeoffItem> Kits,
        IReadOnlyList<LsfPunchingHoleItem> LsfPunchingHoles)
    {
        public BimMaterialTakeoff(
            string ProjectName,
            double WasteMarginPercent,
            IReadOnlyList<PipeTakeoffItem> Pipes,
            IReadOnlyList<FittingTakeoffItem> Fittings,
            IReadOnlyList<KitTakeoffItem> Kits)
            : this(ProjectName, WasteMarginPercent, Pipes, Fittings, Kits, Array.Empty<LsfPunchingHoleItem>())
        {
        }
    }
}
