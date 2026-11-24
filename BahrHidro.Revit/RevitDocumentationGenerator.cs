using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit
{
    public static class RevitDocumentationGenerator
    {
        public static (int TaggedCount, string Message) TagPipesInActiveView(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (activeView == null)
                throw new InvalidOperationException("Nenhuma vista ativa encontrada no Revit.");

            if (activeView is ViewSchedule || activeView is ViewSheet)
                throw new InvalidOperationException("Abra uma Planta Baixa, Corte, Elevação ou Vista 3D antes de identificar (taggear) os tubos.");

            var pipesInView = new FilteredElementCollector(doc, activeView.Id)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            if (pipesInView.Count == 0)
                return (0, "Nenhuma tubulação visível foi encontrada na vista ativa.");

            var alreadyTaggedPipeIds = new HashSet<long>();
            var existingTags = new FilteredElementCollector(doc, activeView.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .ToList();

            foreach (var tag in existingTags)
            {
                try
                {
                    foreach (var elemId in tag.GetTaggedLocalElementIds())
                        alreadyTaggedPipeIds.Add(elemId.Value);
                }
                catch { }
            }

            int taggedCount = 0;

            using (Transaction tx = new Transaction(doc, "Smart Hidro - Identificar Tubulações na Vista"))
            {
                tx.Start();

                if (activeView is View3D v3d && !v3d.IsLocked)
                {
                    if (v3d.CanSaveOrientation())
                        v3d.SaveOrientationAndLock();
                    else
                        throw new InvalidOperationException("Salve ou renomeie a vista 3D '{3D}' padrão antes de inserir tags.");
                }

                foreach (Pipe pipe in pipesInView)
                {
                    if (alreadyTaggedPipeIds.Contains(pipe.Id.Value))
                        continue;

                    if (pipe.Location is not LocationCurve lc)
                        continue;

                    if (lc.Curve.Length < UnitUtils.ConvertToInternalUnits(0.12, UnitTypeId.Meters))
                        continue;

                    XYZ midPt = lc.Curve.Evaluate(0.5, normalized: true);
                    XYZ tagPos = midPt + new XYZ(
                        UnitUtils.ConvertToInternalUnits(0.12, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(0.12, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(0.08, UnitTypeId.Meters));

                    try
                    {
                        IndependentTag.Create(
                            doc,
                            activeView.Id,
                            new Reference(pipe),
                            addLeader: true,
                            TagMode.TM_ADDBY_CATEGORY,
                            TagOrientation.Horizontal,
                            tagPos);

                        taggedCount++;
                    }
                    catch { }
                }

                tx.Commit();
            }

            string msg = taggedCount > 0
                ? $"{taggedCount} tubulação(ões) identificada(s) com sucesso na vista '{activeView.Name}'."
                : "Todas as tubulações elegíveis desta vista já estão identificadas (ou não há família de Tag de Tubo carregada no template).";

            return (taggedCount, msg);
        }

        public static (int ViewsCreated, int TotalTagsPlaced, string Summary) GenerateWetZoneIsometricViews(UIDocument uidoc)
        {
            Document doc = uidoc.Document;

            var allPipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            if (allPipes.Count == 0)
                throw new InvalidOperationException("Nenhuma tubulação encontrada no modelo 3D. Gere o traçado antes de criar as vistas isométricas.");

            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

            if (viewFamilyType == null)
                throw new InvalidOperationException("Tipo de Vista 3D não encontrado no template do Revit.");

            double clusterRadiusFeet = UnitUtils.ConvertToInternalUnits(2.40, UnitTypeId.Meters);
            var clusters = ClusterPipesByProximity(allPipes, clusterRadiusFeet);

            var existingViewNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            int viewsCreated = 0;
            int totalTagsPlaced = 0;
            View3D? firstCreatedView = null;

            using (Transaction tx = new Transaction(doc, "Smart Hidro - Gerar Vistas Isométricas 3D"))
            {
                tx.Start();

                for (int i = 0; i < clusters.Count; i++)
                {
                    var clusterPipes = clusters[i];
                    if (clusterPipes.Count == 0) continue;

                    double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

                    foreach (var pipe in clusterPipes)
                    {
                        BoundingBoxXYZ? bb = pipe.get_BoundingBox(null);
                        if (bb == null) continue;
                        if (bb.Min.X < minX) minX = bb.Min.X;
                        if (bb.Min.Y < minY) minY = bb.Min.Y;
                        if (bb.Min.Z < minZ) minZ = bb.Min.Z;
                        if (bb.Max.X > maxX) maxX = bb.Max.X;
                        if (bb.Max.Y > maxY) maxY = bb.Max.Y;
                        if (bb.Max.Z > maxZ) maxZ = bb.Max.Z;
                    }

                    if (minX > maxX) continue;

                    double marginFeet = UnitUtils.ConvertToInternalUnits(0.55, UnitTypeId.Meters);
                    var sectionBox = new BoundingBoxXYZ
                    {
                        Min = new XYZ(minX - marginFeet, minY - marginFeet, minZ - marginFeet),
                        Max = new XYZ(maxX + marginFeet, maxY + marginFeet, maxZ + marginFeet)
                    };

                    View3D isoView = View3D.CreateIsometric(doc, viewFamilyType.Id);

                    string baseName = $"SmartHidro - Isométrico {i + 1:D2}";
                    string finalName = baseName;
                    int suffix = 1;
                    while (existingViewNames.Contains(finalName))
                    {
                        finalName = $"{baseName} ({suffix++})";
                    }
                    existingViewNames.Add(finalName);

                    try
                    {
                        isoView.Name = finalName;
                        isoView.Scale = 25;
                        isoView.DetailLevel = ViewDetailLevel.Fine;
                        isoView.SetSectionBox(sectionBox);
                        isoView.IsSectionBoxActive = true;

                        if (isoView.CanSaveOrientation())
                            isoView.SaveOrientationAndLock();
                    }
                    catch { }

                    viewsCreated++;
                    firstCreatedView ??= isoView;

                    if (isoView.IsLocked)
                    {
                        foreach (var pipe in clusterPipes)
                        {
                            if (pipe.Location is not LocationCurve lc) continue;
                            if (lc.Curve.Length < UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters)) continue;

                            XYZ midPt = lc.Curve.Evaluate(0.5, normalized: true);
                            XYZ tagPos = midPt + new XYZ(
                                UnitUtils.ConvertToInternalUnits(0.14, UnitTypeId.Meters),
                                UnitUtils.ConvertToInternalUnits(0.14, UnitTypeId.Meters),
                                UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters));

                            try
                            {
                                IndependentTag.Create(
                                    doc,
                                    isoView.Id,
                                    new Reference(pipe),
                                    addLeader: true,
                                    TagMode.TM_ADDBY_CATEGORY,
                                    TagOrientation.Horizontal,
                                    tagPos);

                                totalTagsPlaced++;

                                string comment = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                                if (comment.Contains("|H=", StringComparison.OrdinalIgnoreCase))
                                {
                                    XYZ bendPt = midPt + new XYZ(0.25, 0.25, 0.20);
                                    XYZ endPt = bendPt + new XYZ(0.35, 0, 0);
                                    doc.Create.NewSpotElevation(isoView, new Reference(pipe), midPt, bendPt, endPt, midPt, hasLeader: true);
                                }
                            }
                            catch { }
                        }
                    }
                }

                tx.Commit();
            }

            if (firstCreatedView != null)
            {
                try { uidoc.ActiveView = firstCreatedView; } catch { }
            }

            string summary = $"Documentação 3D Concluída: {viewsCreated} vista(s) isométrica(s) recortada(s) (1:25) criada(s) com {totalTagsPlaced} identificador(es) aplicado(s).";
            return (viewsCreated, totalTagsPlaced, summary);
        }

        public static (int ViewsCreated, int TotalTagsPlaced, string Summary) GenerateHydraulicWallElevations(UIDocument uidoc)
        {
            Document doc = uidoc.Document;

            var allPipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            if (allPipes.Count == 0)
                throw new InvalidOperationException("Nenhuma tubulação encontrada no modelo. Gere o traçado antes de criar as elevações das paredes hidráulicas.");

            var sectionVft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Section);

            if (sectionVft == null)
                throw new InvalidOperationException("Tipo de Vista de Corte/Elevação (Section) não encontrado no template do Revit.");

            // Coleta eixos das paredes do projeto e vínculos para alinhar perfeitamente o plano frontal da elevação
            var wallDirections = CollectWallDirections(doc);

            double clusterRadiusFeet = UnitUtils.ConvertToInternalUnits(2.60, UnitTypeId.Meters);
            var clusters = ClusterPipesByProximity(allPipes, clusterRadiusFeet);

            var existingViewNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            int viewsCreated = 0;
            int totalTagsPlaced = 0;
            ViewSection? firstElevation = null;

            using (Transaction tx = new Transaction(doc, "Smart Hidro - Elevações Frontais Paredes Hidráulicas (1:20)"))
            {
                tx.Start();

                for (int i = 0; i < clusters.Count; i++)
                {
                    var clusterPipes = clusters[i];
                    if (clusterPipes.Count == 0) continue;

                    double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

                    foreach (var pipe in clusterPipes)
                    {
                        BoundingBoxXYZ? bb = pipe.get_BoundingBox(null);
                        if (bb == null) continue;
                        if (bb.Min.X < minX) minX = bb.Min.X;
                        if (bb.Min.Y < minY) minY = bb.Min.Y;
                        if (bb.Min.Z < minZ) minZ = bb.Min.Z;
                        if (bb.Max.X > maxX) maxX = bb.Max.X;
                        if (bb.Max.Y > maxY) maxY = bb.Max.Y;
                        if (bb.Max.Z > maxZ) maxZ = bb.Max.Z;
                    }

                    if (minX > maxX) continue;

                    XYZ center = new XYZ((minX + maxX) / 2.0, (minY + maxY) / 2.0, (minZ + maxZ) / 2.0);
                    XYZ basisX = ResolveDominantWallDirection(center, maxX - minX, maxY - minY, wallDirections);
                    XYZ basisY = XYZ.BasisZ;
                    XYZ basisZ = basisX.CrossProduct(basisY).Normalize();

                    // Projeta todos os extremos dos tubos no sistema local da parede para recortar a elevação 1:20
                    double localMinX = double.MaxValue, localMaxX = double.MinValue;
                    double localMinY = double.MaxValue, localMaxY = double.MinValue;

                    foreach (var pipe in clusterPipes)
                    {
                        BoundingBoxXYZ? bb = pipe.get_BoundingBox(null);
                        if (bb == null) continue;
                        XYZ[] corners =
                        {
                            new(bb.Min.X, bb.Min.Y, bb.Min.Z),
                            new(bb.Max.X, bb.Max.Y, bb.Max.Z),
                            new(bb.Min.X, bb.Max.Y, bb.Min.Z),
                            new(bb.Max.X, bb.Min.Y, bb.Max.Z)
                        };
                        foreach (var c in corners)
                        {
                            XYZ rel = c - center;
                            double lx = rel.DotProduct(basisX);
                            double ly = rel.DotProduct(basisY);
                            if (lx < localMinX) localMinX = lx;
                            if (lx > localMaxX) localMaxX = lx;
                            if (ly < localMinY) localMinY = ly;
                            if (ly > localMaxY) localMaxY = ly;
                        }
                    }

                    double marginFeet = UnitUtils.ConvertToInternalUnits(0.45, UnitTypeId.Meters);
                    double depthHalfFeet = UnitUtils.ConvertToInternalUnits(0.55, UnitTypeId.Meters);

                    Transform sectionTransform = Transform.Identity;
                    sectionTransform.Origin = center;
                    sectionTransform.BasisX = basisX;
                    sectionTransform.BasisY = basisY;
                    sectionTransform.BasisZ = basisZ;

                    var sectionBox = new BoundingBoxXYZ
                    {
                        Transform = sectionTransform,
                        Min = new XYZ(localMinX - marginFeet, localMinY - marginFeet, -depthHalfFeet),
                        Max = new XYZ(localMaxX + marginFeet, localMaxY + marginFeet, depthHalfFeet)
                    };

                    ViewSection elevView = ViewSection.CreateSection(doc, sectionVft.Id, sectionBox);

                    string baseName = $"SmartHidro - Parede Hidráulica {i + 1:D2}";
                    string finalName = baseName;
                    int suffix = 1;
                    while (existingViewNames.Contains(finalName))
                    {
                        finalName = $"{baseName} ({suffix++})";
                    }
                    existingViewNames.Add(finalName);

                    try
                    {
                        elevView.Name = finalName;
                        elevView.Scale = 20; // Escala 1:20 padrão detalhamento de painel/parede hidráulica
                        elevView.DetailLevel = ViewDetailLevel.Fine;
                    }
                    catch { }

                    viewsCreated++;
                    firstElevation ??= elevView;

                    // Insere Tags automáticas nos tubos visíveis na elevação frontal
                    foreach (var pipe in clusterPipes)
                    {
                        if (pipe.Location is not LocationCurve lc) continue;
                        if (lc.Curve.Length < UnitUtils.ConvertToInternalUnits(0.15, UnitTypeId.Meters)) continue;

                        XYZ midPt = lc.Curve.Evaluate(0.5, normalized: true);
                        XYZ tagPos = midPt + (basisX * UnitUtils.ConvertToInternalUnits(0.14, UnitTypeId.Meters)) + (basisY * UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters));

                        try
                        {
                            IndependentTag.Create(
                                doc,
                                elevView.Id,
                                new Reference(pipe),
                                addLeader: true,
                                TagMode.TM_ADDBY_CATEGORY,
                                TagOrientation.Horizontal,
                                tagPos);

                            totalTagsPlaced++;
                        }
                        catch { }
                    }
                }

                tx.Commit();
            }

            if (firstElevation != null)
            {
                try { uidoc.ActiveView = firstElevation; } catch { }
            }

            string summary = $"Elevações de Parede (1:20): {viewsCreated} vista(s) frontal(is) criada(s) com {totalTagsPlaced} identificador(es) aplicado(s).";
            return (viewsCreated, totalTagsPlaced, summary);
        }

        public static (int DeletedCount, string Summary) ClearGeneratedRouting(Document doc, string disciplineMode)
        {
            bool clearAll = string.IsNullOrWhiteSpace(disciplineMode) ||
                            string.Equals(disciplineMode, "All", StringComparison.OrdinalIgnoreCase);

            string discToken = $"Disc={disciplineMode}";

            var targetCategories = new[]
            {
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_PipeAccessory,
                BuiltInCategory.OST_PlumbingFixtures
            };

            var elements = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(targetCategories))
                .WhereElementIsNotElementType()
                .ToElements();

            var idsToDelete = new List<ElementId>();

            foreach (Element elem in elements)
            {
                string comment = elem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                if (!comment.Contains("BahrHidro", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!clearAll && !comment.Contains(discToken, StringComparison.OrdinalIgnoreCase))
                    continue;

                idsToDelete.Add(elem.Id);
            }

            if (idsToDelete.Count == 0)
            {
                return (0, $"Nenhum elemento gerado automaticamente pelo Smart Hidro foi encontrado para a disciplina selecionada ({disciplineMode}).");
            }

            int deletedCount = 0;
            using (Transaction tx = new Transaction(doc, "Smart Hidro - Limpar Traçado Gerado"))
            {
                tx.Start();
                foreach (var id in idsToDelete)
                {
                    if (doc.GetElement(id) == null) continue;
                    try
                    {
                        var deleted = doc.Delete(id);
                        deletedCount += deleted.Count;
                    }
                    catch { }
                }
                tx.Commit();
            }

            string summary = $"Limpeza Concluída: {deletedCount} elemento(s) da rede ({disciplineMode}) removido(s) com segurança.";
            return (deletedCount, summary);
        }

        private static List<(XYZ Mid, XYZ Dir)> CollectWallDirections(Document doc)
        {
            var result = new List<(XYZ Mid, XYZ Dir)>();

            void CollectFrom(Document targetDoc, Transform tr)
            {
                var walls = new FilteredElementCollector(targetDoc)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();

                foreach (var w in walls)
                {
                    if (w.Location is not LocationCurve lc || lc.Curve is not Line line) continue;
                    XYZ p0 = tr.OfPoint(line.GetEndPoint(0));
                    XYZ p1 = tr.OfPoint(line.GetEndPoint(1));
                    XYZ dir = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0);
                    if (dir.GetLength() < 0.05) continue;
                    result.Add(((p0 + p1) * 0.5, dir.Normalize()));
                }
            }

            CollectFrom(doc, Transform.Identity);
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc != null)
                    CollectFrom(linkDoc, link.GetTotalTransform());
            }

            return result;
        }

        private static XYZ ResolveDominantWallDirection(XYZ clusterCenter, double spanX, double spanY, List<(XYZ Mid, XYZ Dir)> wallDirections)
        {
            double maxSearchFeet = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Meters);
            XYZ? bestDir = null;
            double bestDist = double.MaxValue;

            foreach (var w in wallDirections)
            {
                double dist = Math.Sqrt(Math.Pow(w.Mid.X - clusterCenter.X, 2) + Math.Pow(w.Mid.Y - clusterCenter.Y, 2));
                if (dist < bestDist && dist <= maxSearchFeet)
                {
                    bestDist = dist;
                    bestDir = w.Dir;
                }
            }

            if (bestDir != null)
            {
                // Garante orientação consistente (esquerda -> direita ou baixo -> cima)
                if (Math.Abs(bestDir.X) >= Math.Abs(bestDir.Y) && bestDir.X < 0)
                    bestDir = -bestDir;
                else if (Math.Abs(bestDir.Y) > Math.Abs(bestDir.X) && bestDir.Y < 0)
                    bestDir = -bestDir;
                return bestDir;
            }

            return spanX >= spanY ? XYZ.BasisX : XYZ.BasisY;
        }

        private static List<List<Pipe>> ClusterPipesByProximity(List<Pipe> pipes, double maxDistanceFeet)
        {
            var centers = new Dictionary<long, XYZ>();
            foreach (var p in pipes)
            {
                if (p.Location is LocationCurve lc)
                    centers[p.Id.Value] = lc.Curve.Evaluate(0.5, true);
            }

            var visited = new HashSet<long>();
            var clusters = new List<List<Pipe>>();

            foreach (var seed in pipes)
            {
                if (visited.Contains(seed.Id.Value) || !centers.ContainsKey(seed.Id.Value))
                    continue;

                var cluster = new List<Pipe>();
                var queue = new Queue<Pipe>();
                queue.Enqueue(seed);
                visited.Add(seed.Id.Value);

                while (queue.Count > 0)
                {
                    var curr = queue.Dequeue();
                    cluster.Add(curr);
                    XYZ c1 = centers[curr.Id.Value];

                    foreach (var other in pipes)
                    {
                        if (visited.Contains(other.Id.Value) || !centers.ContainsKey(other.Id.Value))
                            continue;

                        XYZ c2 = centers[other.Id.Value];
                        double distXY = Math.Sqrt(Math.Pow(c1.X - c2.X, 2) + Math.Pow(c1.Y - c2.Y, 2));
                        double distZ = Math.Abs(c1.Z - c2.Z);

                        if (distXY <= maxDistanceFeet && distZ <= UnitUtils.ConvertToInternalUnits(3.5, UnitTypeId.Meters))
                        {
                            visited.Add(other.Id.Value);
                            queue.Enqueue(other);
                        }
                    }
                }

                clusters.Add(cluster);
            }

            return clusters;
        }
    }
}
