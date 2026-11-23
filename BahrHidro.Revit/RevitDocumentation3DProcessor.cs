using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitDocumentation3DProcessor
    {
        public static (Executive3DDocResult Result, ElementId TargetViewId) GenerateIsometricsAndAbntSheet(
            Document doc,
            bool createIsometrics,
            bool createAbntSheet)
        {
            var fixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .WhereElementIsNotElementType()
                .ToList();

            var pipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            if (fixtures.Count == 0 && pipes.Count == 0)
                throw new InvalidOperationException("Nenhum aparelho sanitário ou tubulação encontrado no modelo 3D.");

            // Identifica ambientes molhados (Rooms nativos ou agrupamento espacial de aparelhos no 3D)
            var zones = DetectWetAreaZones(doc, fixtures, pipes);
            var createdIsoViews = new List<View3D>();
            int tagsPlaced = 0;
            ElementId activeTargetId = ElementId.InvalidElementId;

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Gerar Isométricos 3D e Prancha ABNT"))
            {
                tx.Start();

                if (createIsometrics)
                {
                    ViewFamilyType? vft3d = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

                    if (vft3d != null)
                    {
                        foreach (var (zoneName, box, zonePipes) in zones.Take(6))
                        {
                            string viewName = MakeUniqueViewName(doc, $"Bahr Hidro - Isométrico 3D - {zoneName}");
                            View3D iso = View3D.CreateIsometric(doc, vft3d.Id);
                            iso.Name = viewName;
                            iso.Scale = 25;
                            iso.DetailLevel = ViewDetailLevel.Fine;
                            iso.DisplayStyle = DisplayStyle.ShadingWithEdges;
                            iso.IsSectionBoxActive = true;
                            iso.SetSectionBox(box);

                            if (iso.CanSaveOrientation())
                                iso.SaveOrientationAndLock();

                            // Insere tags nos principais trechos visíveis no isométrico
                            foreach (var p in zonePipes.Take(14))
                            {
                                if (p.Location is LocationCurve lc && lc.Curve != null)
                                {
                                    XYZ mid = lc.Curve.Evaluate(0.5, true);
                                    try
                                    {
                                        IndependentTag.Create(
                                            doc,
                                            iso.Id,
                                            new Reference(p),
                                            false,
                                            TagMode.TM_ADDBY_CATEGORY,
                                            TagOrientation.Horizontal,
                                            mid);
                                        tagsPlaced++;
                                    }
                                    catch { }
                                }
                            }

                            createdIsoViews.Add(iso);
                            if (activeTargetId == ElementId.InvalidElementId)
                                activeTargetId = iso.Id;
                        }
                    }
                }

                string sheetCode = "";
                if (createAbntSheet)
                {
                    FamilySymbol? titleBlock = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .OrderByDescending(tb => Normalize(tb.Name).Contains("a1") || Normalize(tb.Name).Contains("a0"))
                        .FirstOrDefault();

                    ElementId tbId = titleBlock?.Id ?? ElementId.InvalidElementId;
                    ViewSheet sheet = ViewSheet.Create(doc, tbId);
                    sheetCode = MakeUniqueSheetNumber(doc, "HID-01");
                    sheet.SheetNumber = sheetCode;
                    sheet.Name = "INSTALAÇÕES HIDROSSANITÁRIAS — ISOMÉTRICOS 3D E PRUMADAS (ABNT)";

                    double cursorX = UnitUtils.ConvertToInternalUnits(0.18, UnitTypeId.Meters);
                    double topY = UnitUtils.ConvertToInternalUnits(0.45, UnitTypeId.Meters);
                    double botY = UnitUtils.ConvertToInternalUnits(0.20, UnitTypeId.Meters);
                    double stepX = UnitUtils.ConvertToInternalUnits(0.24, UnitTypeId.Meters);

                    // 1. Insere até 3 vistas isométricas 3D na prancha
                    for (int i = 0; i < Math.Min(3, createdIsoViews.Count); i++)
                    {
                        var v = createdIsoViews[i];
                        if (Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id))
                        {
                            Viewport.Create(doc, sheet.Id, v.Id, new XYZ(cursorX + i * stepX, topY, 0));
                        }
                    }

                    // 2. Insere o Esquema Vertical de Prumadas caso já tenha sido gerado
                    ViewDrafting? riserView = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewDrafting))
                        .Cast<ViewDrafting>()
                        .FirstOrDefault(v => v.Name.Contains("Esquema Vertical de Prumadas", StringComparison.OrdinalIgnoreCase));

                    if (riserView != null && Viewport.CanAddViewToSheet(doc, sheet.Id, riserView.Id))
                    {
                        Viewport.Create(doc, sheet.Id, riserView.Id, new XYZ(cursorX + 2.2 * stepX, botY, 0));
                    }

                    // 3. Insere a primeira planta baixa hidráulica disponível (não alocada em outra prancha)
                    ViewPlan? floorPlan = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .FirstOrDefault(vp => !vp.IsTemplate && vp.ViewType == ViewType.FloorPlan && Viewport.CanAddViewToSheet(doc, sheet.Id, vp.Id));

                    if (floorPlan != null)
                    {
                        Viewport.Create(doc, sheet.Id, floorPlan.Id, new XYZ(cursorX, botY, 0));
                    }

                    activeTargetId = sheet.Id;
                }

                tx.Commit();

                string summary = $"Documentação 3D ABNT concluída: {createdIsoViews.Count} vista(s) isométrica(s) 3D bloqueada(s), " +
                                 $"{tagsPlaced} etiqueta(s) inserida(s)" +
                                 (createAbntSheet ? $" e Prancha '{sheetCode}' montada automaticamente." : ".");

                return (new Executive3DDocResult(createdIsoViews.Count, tagsPlaced, sheetCode, summary), activeTargetId);
            }
        }

        private static List<(string Name, BoundingBoxXYZ Box, List<Pipe> Pipes)> DetectWetAreaZones(
            Document doc,
            List<Element> fixtures,
            List<Pipe> pipes)
        {
            var result = new List<(string Name, BoundingBoxXYZ Box, List<Pipe> Pipes)>();
            double marginFt = UnitUtils.ConvertToInternalUnits(0.65, UnitTypeId.Meters);

            // 1. Tenta ler Rooms nativos do projeto
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            foreach (var r in rooms)
            {
                BoundingBoxXYZ? bb = r.get_BoundingBox(null);
                if (bb == null) continue;

                var expanded = new BoundingBoxXYZ
                {
                    Min = new XYZ(bb.Min.X - marginFt, bb.Min.Y - marginFt, bb.Min.Z - marginFt),
                    Max = new XYZ(bb.Max.X + marginFt, bb.Max.Y + marginFt, bb.Max.Z + marginFt)
                };

                var roomPipes = pipes.Where(p => IsElementInsideBox(p, expanded)).ToList();
                bool hasFixture = fixtures.Any(f => IsElementInsideBox(f, expanded));
                if (hasFixture || roomPipes.Count >= 2)
                {
                    string rName = SanitizeName($"{r.Name} ({r.Level?.Name ?? "Nível"})");
                    result.Add((rName, expanded, roomPipes));
                }
            }

            if (result.Count > 0) return result;

            // 2. Fallback inteligente: agrupa aparelhos sanitários por proximidade (quando Rooms vêm de vínculo IFC/RVT)
            double clusterRadiusFt = UnitUtils.ConvertToInternalUnits(3.5, UnitTypeId.Meters);
            var used = new HashSet<long>();
            int idx = 1;

            foreach (var fx in fixtures)
            {
                if (!used.Add(fx.Id.Value)) continue;
                BoundingBoxXYZ? b0 = fx.get_BoundingBox(null);
                if (b0 == null) continue;
                XYZ c0 = (b0.Min + b0.Max) * 0.5;

                double minX = b0.Min.X, minY = b0.Min.Y, minZ = b0.Min.Z;
                double maxX = b0.Max.X, maxY = b0.Max.Y, maxZ = b0.Max.Z;

                foreach (var other in fixtures)
                {
                    if (used.Contains(other.Id.Value)) continue;
                    BoundingBoxXYZ? bo = other.get_BoundingBox(null);
                    if (bo == null) continue;
                    XYZ co = (bo.Min + bo.Max) * 0.5;
                    if (Math.Abs(co.Z - c0.Z) <= UnitUtils.ConvertToInternalUnits(2.5, UnitTypeId.Meters) &&
                        Math.Sqrt(Math.Pow(co.X - c0.X, 2) + Math.Pow(co.Y - c0.Y, 2)) <= clusterRadiusFt)
                    {
                        used.Add(other.Id.Value);
                        minX = Math.Min(minX, bo.Min.X); minY = Math.Min(minY, bo.Min.Y); minZ = Math.Min(minZ, bo.Min.Z);
                        maxX = Math.Max(maxX, bo.Max.X); maxY = Math.Max(maxY, bo.Max.Y); maxZ = Math.Max(maxZ, bo.Max.Z);
                    }
                }

                var box = new BoundingBoxXYZ
                {
                    Min = new XYZ(minX - marginFt, minY - marginFt, minZ - marginFt),
                    Max = new XYZ(maxX + marginFt, maxY + marginFt, maxZ + marginFt)
                };
                var zPipes = pipes.Where(p => IsElementInsideBox(p, box)).ToList();
                result.Add(($"Setor Molhado {idx++:00}", box, zPipes));
            }

            return result;
        }

        private static bool IsElementInsideBox(Element e, BoundingBoxXYZ box)
        {
            BoundingBoxXYZ? bb = e.get_BoundingBox(null);
            if (bb == null) return false;
            XYZ mid = (bb.Min + bb.Max) * 0.5;
            return mid.X >= box.Min.X && mid.X <= box.Max.X &&
                   mid.Y >= box.Min.Y && mid.Y <= box.Max.Y &&
                   mid.Z >= box.Min.Z && mid.Z <= box.Max.Z;
        }

        private static string MakeUniqueViewName(Document doc, string desired)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(desired)) return desired;
            int n = 2;
            while (existing.Contains($"{desired} ({n})")) n++;
            return $"{desired} ({n})";
        }

        private static string MakeUniqueSheetNumber(Document doc, string prefix)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(prefix)) return prefix;
            int n = 2;
            while (existing.Contains($"HID-{n:00}")) n++;
            return $"HID-{n:00}";
        }

        private static string SanitizeName(string raw)
        {
            foreach (char c in new[] { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' })
                raw = raw.Replace(c, '-');
            return raw.Trim();
        }

        private static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string formD = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char ch in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
    }
}
