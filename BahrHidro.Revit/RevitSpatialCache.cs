using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public sealed record CachedVerticalStud(
        string UniqueKey,
        string DisplayName,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double MinZ,
        double MaxZ,
        double WebWidthMm);

    public sealed record CachedWallPanel(
        string Name,
        XYZ P0,
        XYZ P1,
        double LengthMeters,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY);

    public sealed record CachedObstacleBox(
        string CategoryLabel,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double MinZ,
        double MaxZ);

    public sealed class SpatialSceneIndex
    {
        private readonly double _cellSizeFeet;
        private readonly Dictionary<(int GX, int GY), List<int>> _studGrid = new();
        private readonly Dictionary<(int GX, int GY), List<int>> _obstacleGrid = new();
        private readonly Dictionary<(int GX, int GY), List<int>> _wallGrid = new();

        public IReadOnlyList<CachedVerticalStud> Studs { get; }
        public IReadOnlyList<CachedWallPanel> Walls { get; }
        public IReadOnlyList<CachedObstacleBox> Obstacles { get; }

        public SpatialSceneIndex(
            List<CachedVerticalStud> studs,
            List<CachedWallPanel> walls,
            List<CachedObstacleBox> obstacles)
        {
            Studs = studs;
            Walls = walls;
            Obstacles = obstacles;
            _cellSizeFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Meters);

            for (int i = 0; i < studs.Count; i++)
            {
                var s = studs[i];
                InsertBox(_studGrid, i, s.MinX, s.MaxX, s.MinY, s.MaxY);
            }

            for (int i = 0; i < obstacles.Count; i++)
            {
                var o = obstacles[i];
                InsertBox(_obstacleGrid, i, o.MinX, o.MaxX, o.MinY, o.MaxY);
            }

            for (int i = 0; i < walls.Count; i++)
            {
                var w = walls[i];
                InsertBox(_wallGrid, i, w.MinX, w.MaxX, w.MinY, w.MaxY);
            }
        }

        public List<CachedVerticalStud> QueryStudsAlongSegment(XYZ p0, XYZ p1, double radiusFeet)
        {
            double minX = Math.Min(p0.X, p1.X) - radiusFeet;
            double maxX = Math.Max(p0.X, p1.X) + radiusFeet;
            double minY = Math.Min(p0.Y, p1.Y) - radiusFeet;
            double maxY = Math.Max(p0.Y, p1.Y) + radiusFeet;

            var candidateIndices = QueryGrid(_studGrid, minX, maxX, minY, maxY);
            var list = new List<CachedVerticalStud>(candidateIndices.Count);
            foreach (int idx in candidateIndices)
                list.Add(Studs[idx]);
            return list;
        }

        public bool HasObstacleCollision(double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
        {
            var candidateIndices = QueryGrid(_obstacleGrid, minX, maxX, minY, maxY);
            foreach (int idx in candidateIndices)
            {
                var o = Obstacles[idx];
                if (maxX >= o.MinX && minX <= o.MaxX &&
                    maxY >= o.MinY && minY <= o.MaxY &&
                    maxZ >= o.MinZ && minZ <= o.MaxZ)
                {
                    return true;
                }
            }
            return false;
        }

        public (CachedWallPanel? Wall, double XFromStartMeters, double DistanceFeet) FindNearestWall(
            double x,
            double y,
            double maxDistFeet)
        {
            var candidateIndices = QueryGrid(_wallGrid, x - maxDistFeet, x + maxDistFeet, y - maxDistFeet, y + maxDistFeet);
            double bestDist = double.MaxValue;
            CachedWallPanel? bestWall = null;
            double bestX = 0.0;

            foreach (int idx in candidateIndices)
            {
                var w = Walls[idx];
                double dx = w.P1.X - w.P0.X;
                double dy = w.P1.Y - w.P0.Y;
                double lenSq = dx * dx + dy * dy;
                if (lenSq < 1e-6) continue;

                double t = Math.Clamp(((x - w.P0.X) * dx + (y - w.P0.Y) * dy) / lenSq, 0.0, 1.0);
                double projX = w.P0.X + t * dx;
                double projY = w.P0.Y + t * dy;
                double dist = Math.Sqrt(Math.Pow(x - projX, 2) + Math.Pow(y - projY, 2));

                if (dist < bestDist && dist <= maxDistFeet)
                {
                    bestDist = dist;
                    bestWall = w;
                    bestX = Math.Round(t * w.LengthMeters, 2);
                }
            }

            return (bestWall, bestX, bestDist);
        }

        private void InsertBox(
            Dictionary<(int GX, int GY), List<int>> grid,
            int index,
            double minX,
            double maxX,
            double minY,
            double maxY)
        {
            int gx0 = (int)Math.Floor(minX / _cellSizeFeet);
            int gx1 = (int)Math.Floor(maxX / _cellSizeFeet);
            int gy0 = (int)Math.Floor(minY / _cellSizeFeet);
            int gy1 = (int)Math.Floor(maxY / _cellSizeFeet);

            for (int gx = gx0; gx <= gx1; gx++)
            {
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    var key = (gx, gy);
                    if (!grid.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<int>();
                        grid[key] = bucket;
                    }
                    bucket.Add(index);
                }
            }
        }

        private HashSet<int> QueryGrid(
            Dictionary<(int GX, int GY), List<int>> grid,
            double minX,
            double maxX,
            double minY,
            double maxY)
        {
            var result = new HashSet<int>();
            int gx0 = (int)Math.Floor(minX / _cellSizeFeet);
            int gx1 = (int)Math.Floor(maxX / _cellSizeFeet);
            int gy0 = (int)Math.Floor(minY / _cellSizeFeet);
            int gy1 = (int)Math.Floor(maxY / _cellSizeFeet);

            for (int gx = gx0; gx <= gx1; gx++)
            {
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    if (grid.TryGetValue((gx, gy), out var bucket))
                    {
                        for (int i = 0; i < bucket.Count; i++)
                            result.Add(bucket[i]);
                    }
                }
            }

            return result;
        }
    }

    public static class RevitSpatialCache
    {
        private static readonly object _sync = new();
        private static string _cachedSignature = string.Empty;
        private static SpatialSceneIndex? _cachedIndex;

        public static (SpatialSceneIndex Index, bool WasCacheHit, double ElapsedMs) GetOrBuild(
            Document doc,
            bool forceRebuild = false)
        {
            var sw = Stopwatch.StartNew();

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            string signature = BuildSignature(doc, links);

            lock (_sync)
            {
                if (!forceRebuild && _cachedIndex != null && _cachedSignature == signature)
                {
                    sw.Stop();
                    return (_cachedIndex, true, Math.Round(sw.Elapsed.TotalMilliseconds, 2));
                }
            }

            var studs = new List<CachedVerticalStud>();
            var walls = new List<CachedWallPanel>();
            var obstacles = new List<CachedObstacleBox>();

            CollectFromDocument(doc, Transform.Identity, "Modelo", studs, walls, obstacles);

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                CollectFromDocument(linkDoc, link.GetTotalTransform(), link.Name, studs, walls, obstacles);
            }

            var newIndex = new SpatialSceneIndex(studs, walls, obstacles);

            lock (_sync)
            {
                _cachedSignature = signature;
                _cachedIndex = newIndex;
            }

            sw.Stop();
            return (newIndex, false, Math.Round(sw.Elapsed.TotalMilliseconds, 2));
        }

        public static SpatialCacheStatsResult RebuildAndGetStats(Document doc)
        {
            var (index, _, elapsedMs) = GetOrBuild(doc, forceRebuild: true);
            string summary = $"Cache Espacial LSF atualizado em {elapsedMs:F1} ms: {index.Studs.Count} montante(s), {index.Walls.Count} parede(s) e {index.Obstacles.Count} obstáculo(s) indexados.";
            return new SpatialCacheStatsResult(
                index.Studs.Count,
                index.Walls.Count,
                index.Obstacles.Count,
                elapsedMs,
                false,
                summary);
        }

        private static string BuildSignature(Document doc, List<RevitLinkInstance> links)
        {
            int localWalls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();
            int localOpenings = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows }))
                .GetElementCount();

            var sb = new StringBuilder();
            sb.Append(doc.Title).Append('|').Append(doc.PathName)
              .Append("|W:").Append(localWalls)
              .Append("|O:").Append(localOpenings)
              .Append("|L:").Append(links.Count);

            foreach (var l in links)
            {
                Transform tr = l.GetTotalTransform();
                Document? ld = l.GetLinkDocument();
                sb.Append(';').Append(l.Id.Value)
                  .Append(':').Append(ld != null ? "1" : "0")
                  .Append(':').Append(Math.Round(tr.Origin.X, 4))
                  .Append(',').Append(Math.Round(tr.Origin.Y, 4))
                  .Append(',').Append(Math.Round(tr.Origin.Z, 4));
            }

            return sb.ToString();
        }

        private static void CollectFromDocument(
            Document targetDoc,
            Transform tr,
            string sourceLabel,
            List<CachedVerticalStud> studsOut,
            List<CachedWallPanel> wallsOut,
            List<CachedObstacleBox> obstaclesOut)
        {
            var wallElems = new FilteredElementCollector(targetDoc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .ToList();

            int wallIdx = wallsOut.Count + 1;
            foreach (var w in wallElems)
            {
                if (w.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                XYZ p0 = tr.OfPoint(line.GetEndPoint(0));
                XYZ p1 = tr.OfPoint(line.GetEndPoint(1));
                double lenM = UnitUtils.ConvertFromInternalUnits(p0.DistanceTo(p1), UnitTypeId.Meters);
                if (lenM < 0.25) continue;

                string mark = w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                string wName = !string.IsNullOrWhiteSpace(mark) ? $"Painel {mark}" : $"Parede P-{wallIdx:D2}";
                wallsOut.Add(new CachedWallPanel(
                    wName,
                    p0,
                    p1,
                    lenM,
                    Math.Min(p0.X, p1.X),
                    Math.Max(p0.X, p1.X),
                    Math.Min(p0.Y, p1.Y),
                    Math.Max(p0.Y, p1.Y)));
                wallIdx++;
            }

            var categories = new[]
            {
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Windows
            };

            var elements = new FilteredElementCollector(targetDoc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element elem in elements)
            {
                long catVal = elem.Category?.Id.Value ?? 0;
                string rawName = BuildElementName(elem);

                if (catVal == (long)BuiltInCategory.OST_GenericModel)
                {
                    string lower = RemoverAcentos(rawName).ToLowerInvariant();
                    if (!lower.Contains("stud") && !lower.Contains("montante") && !lower.Contains("perfil") &&
                        !lower.Contains("steel") && !lower.Contains("lsf") && !lower.Contains("column"))
                        continue;
                }

                BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
                if (bb == null) continue;

                XYZ[] corners =
                {
                    tr.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)),
                    tr.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)),
                    tr.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)),
                    tr.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z))
                };

                double minX = corners.Min(c => c.X);
                double maxX = corners.Max(c => c.X);
                double minY = corners.Min(c => c.Y);
                double maxY = corners.Max(c => c.Y);
                double minZ = corners.Min(c => c.Z);
                double maxZ = corners.Max(c => c.Z);

                if (catVal != (long)BuiltInCategory.OST_GenericModel)
                {
                    obstaclesOut.Add(new CachedObstacleBox(
                        elem.Category?.Name ?? "Obstáculo",
                        minX, maxX, minY, maxY, minZ, maxZ));
                }

                if (catVal == (long)BuiltInCategory.OST_Doors || catVal == (long)BuiltInCategory.OST_Windows)
                    continue;

                double dxM = UnitUtils.ConvertFromInternalUnits(maxX - minX, UnitTypeId.Meters);
                double dyM = UnitUtils.ConvertFromInternalUnits(maxY - minY, UnitTypeId.Meters);
                double dzM = UnitUtils.ConvertFromInternalUnits(maxZ - minZ, UnitTypeId.Meters);

                if (dzM < 1.20 || dxM > 0.35 || dyM > 0.35) continue;

                double webMm = Math.Round(Math.Clamp(Math.Max(dxM, dyM) * 1000.0, 60.0, 200.0), 0);
                if (webMm < 70) webMm = 90.0;

                studsOut.Add(new CachedVerticalStud(
                    $"{sourceLabel}_{elem.Id.Value}",
                    rawName,
                    minX, maxX, minY, maxY, minZ, maxZ,
                    webMm));
            }
        }

        private static string BuildElementName(Element elem)
        {
            if (elem is FamilyInstance fi && !string.IsNullOrWhiteSpace(fi.Symbol?.FamilyName))
                return $"{fi.Symbol.FamilyName} ({fi.Name})";
            return elem.Name;
        }

        private static string RemoverAcentos(string texto)
        {
            var normalized = texto.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
