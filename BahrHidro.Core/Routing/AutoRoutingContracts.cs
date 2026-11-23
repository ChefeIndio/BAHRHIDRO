using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BahrHidro.Core.Routing
{
    public sealed record SpatialPoint3D(double XMeters, double YMeters, double ZMeters);

    public sealed record SpatialConnectorInfo(
        string SystemDomain,
        string FlowDirection,
        double DiameterMm,
        SpatialPoint3D Origin,
        SpatialPoint3D Direction);

    public sealed record SpatialFixtureInfo(
        long ElementId,
        string FamilyName,
        string TypeName,
        string NormalizedRole,
        string LevelName,
        SpatialPoint3D Center,
        List<SpatialConnectorInfo> Connectors);

    public sealed record SpatialRiserAnchorInfo(
        long ElementId,
        string SystemClassification,
        double DiameterMm,
        SpatialPoint3D BottomPoint,
        SpatialPoint3D TopPoint);

    public sealed record SpatialWetAreaContext(
        string ProjectName,
        string GeneratedAtUtc,
        List<SpatialFixtureInfo> Fixtures,
        List<SpatialRiserAnchorInfo> RiserAnchors);

    public sealed record AutoRouteSegmentSpec(
        string SystemClassification,
        double DiameterMm,
        double SlopePercent,
        List<SpatialPoint3D> WaypointsMeters);

    public sealed record AutoRoutingPlanJson(
        string PlanName,
        List<AutoRouteSegmentSpec> Segments);

    public static class AutoRoutingSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string SerializeContext(SpatialWetAreaContext context) =>
            JsonSerializer.Serialize(context, Options);

        public static AutoRoutingPlanJson? DeserializePlan(string json) =>
            string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AutoRoutingPlanJson>(json, Options);

        public static string BuildSamplePlanFromContext(SpatialWetAreaContext context)
        {
            var segments = new List<AutoRouteSegmentSpec>();
            var coldRiser = context.RiserAnchors.Find(r => r.SystemClassification.Contains("Fria", StringComparison.OrdinalIgnoreCase))
                            ?? (context.RiserAnchors.Count > 0 ? context.RiserAnchors[0] : null);

            if (coldRiser != null)
            {
                foreach (var fx in context.Fixtures)
                {
                    var cwConn = fx.Connectors.Find(c => c.SystemDomain.Contains("Fria", StringComparison.OrdinalIgnoreCase) || c.SystemDomain.Contains("Cold", StringComparison.OrdinalIgnoreCase));
                    if (cwConn == null) continue;

                    double zRoute = cwConn.Origin.ZMeters;
                    var pStart = new SpatialPoint3D(coldRiser.BottomPoint.XMeters, coldRiser.BottomPoint.YMeters, zRoute);
                    var pElbow = new SpatialPoint3D(cwConn.Origin.XMeters, coldRiser.BottomPoint.YMeters, zRoute);
                    var pEnd = cwConn.Origin;

                    segments.Add(new AutoRouteSegmentSpec(
                        "Água Fria",
                        25.0,
                        0.0,
                        new List<SpatialPoint3D> { pStart, pElbow, pEnd }));
                }
            }

            var plan = new AutoRoutingPlanJson("Plano_Derivado_Contexto_3D", segments);
            return JsonSerializer.Serialize(plan, Options);
        }
    }
}
