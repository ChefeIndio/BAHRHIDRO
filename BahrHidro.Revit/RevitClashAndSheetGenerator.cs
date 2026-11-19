using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitClashAndSheetGenerator
    {
        public static ClashCheckSummaryResult RunHydraulicClashCheck(Document doc)
        {
            var pipes = new FilteredElementCollector(doc)
                .OfClass(typeof(Pipe))
                .WhereElementIsNotElementType()
                .Cast<Pipe>()
                .ToList();

            var fittings = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new[]
                {
                    BuiltInCategory.OST_PipeFitting,
                    BuiltInCategory.OST_PipeAccessory
                }))
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            var hydraulicElements = new List<(Element Elem, string Label, WorldAabb Box)>();
            // Tolerância negativa de 5 mm para evitar falso-positivo em encostos tangenciais
            double shrinkFeet = UnitUtils.ConvertToInternalUnits(0.005, UnitTypeId.Meters);

            foreach (var p in pipes)
            {
                BoundingBoxXYZ? bb = p.get_BoundingBox(null);
                if (bb == null) continue;
                string sys = p.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "Tubulação";
                double diaMm = 0;
                var dParam = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (dParam != null && dParam.HasValue)
                    diaMm = Math.Round(UnitUtils.ConvertFromInternalUnits(dParam.AsDouble(), UnitTypeId.Millimeters), 0);

                hydraulicElements.Add((
                    p,
                    $"Tubo {sys} Ø{diaMm:F0}mm (ID {p.Id.Value})",
                    ShrinkBox(ToWorldAabb(bb, Transform.Identity), shrinkFeet)));
            }

            foreach (var f in fittings)
            {
                BoundingBoxXYZ? bb = f.get_BoundingBox(null);
                if (bb == null) continue;
                string sys = f.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? "Conexão";
                hydraulicElements.Add((
                    f,
                    $"Conexão {sys}: {f.Symbol?.FamilyName} (ID {f.Id.Value})",
                    ShrinkBox(ToWorldAabb(bb, Transform.Identity), shrinkFeet)));
            }

            if (hydraulicElements.Count == 0)
            {
                return new ClashCheckSummaryResult(
                    0,
                    0,
                    "Nenhuma tubulação encontrada no modelo para verificar interferências.",
                    new List<string>());
            }

            // Coleta elementos estruturais (Steel Frame, pilares, vigas, lajes) do Host e dos Vínculos RVT/IFC
            var structuralBoxes = new List<(string Label, WorldAabb Box)>();
            CollectStructuralTargets(doc, Transform.Identity, "Modelo Atual", structuralBoxes);

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var link in links)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                CollectStructuralTargets(linkDoc, link.GetTotalTransform(), $"Vínculo ({link.Name})", structuralBoxes);
            }

            var clashLogs = new List<string>();
            int clashesCount = 0;

            foreach (var h in hydraulicElements)
            {
                foreach (var s in structuralBoxes)
                {
                    if (BoxesOverlap(h.Box, s.Box))
                    {
                        clashesCount++;
                        double cx = UnitUtils.ConvertFromInternalUnits((Math.Max(h.Box.MinX, s.Box.MinX) + Math.Min(h.Box.MaxX, s.Box.MaxX)) / 2.0, UnitTypeId.Meters);
                        double cy = UnitUtils.ConvertFromInternalUnits((Math.Max(h.Box.MinY, s.Box.MinY) + Math.Min(h.Box.MaxY, s.Box.MaxY)) / 2.0, UnitTypeId.Meters);
                        double cz = UnitUtils.ConvertFromInternalUnits((Math.Max(h.Box.MinZ, s.Box.MinZ) + Math.Min(h.Box.MaxZ, s.Box.MaxZ)) / 2.0, UnitTypeId.Meters);

                        clashLogs.Add($"[COLISÃO #{clashesCount:D2}] {h.Label}  ✕  {s.Label}  |  Coord: (X={cx:F2}m, Y={cy:F2}m, Z={cz:F2}m)");
                        if (clashLogs.Count >= 150) break;
                    }
                }
                if (clashLogs.Count >= 150) break;
            }

            string summary = clashesCount == 0
                ? $"APROVADO (0 Colisões): {hydraulicElements.Count} elemento(s) hidráulico(s) verificado(s) contra {structuralBoxes.Count} elemento(s) estrutural(is) Steel Frame."
                : $"ATENÇÃO: {clashesCount} interferência(s) detectada(s) entre {hydraulicElements.Count} elemento(s) hidráulico(s) e {structuralBoxes.Count} perfil(is) estrutural(is).";

            if (clashesCount == 0)
            {
                clashLogs.Add("Nenhuma interferência geométrica detectada entre as tubulações hidráulicas e os perfis Steel Frame / estrutura.");
            }

            return new ClashCheckSummaryResult(hydraulicElements.Count, clashesCount, summary, clashLogs);
        }

        public static SheetGenerationResult GenerateHydraulicSheets(UIDocument uidoc, string projectName)
        {
            Document doc = uidoc.Document;

            // 1. Busca vistas isométricas geradas pelo Bahr Hidro; se não houver, gera automaticamente agora
            var isoViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate && v.Name.StartsWith("BahrHidro - Isométrico", StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.Name)
                .ToList();

            if (isoViews.Count == 0)
            {
                RevitDocumentationGenerator.GenerateWetZoneIsometricViews(uidoc);
                isoViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .Where(v => !v.IsTemplate && v.Name.StartsWith("BahrHidro - Isométrico", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(v => v.Name)
                    .ToList();
            }

            if (isoViews.Count == 0)
                throw new InvalidOperationException("Não foi possível localizar ou gerar vistas isométricas 3D para inserir na prancha.");

            // 2. Seleciona o carimbo (TitleBlock) do template (prioriza A1 / A0)
            var titleBlocks = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .ToList();

            FamilySymbol? chosenTitleBlock =
                titleBlocks.FirstOrDefault(tb => tb.Name.Contains("A1", StringComparison.OrdinalIgnoreCase) || (tb.FamilyName?.Contains("A1", StringComparison.OrdinalIgnoreCase) ?? false)) ??
                titleBlocks.FirstOrDefault(tb => tb.Name.Contains("A0", StringComparison.OrdinalIgnoreCase) || (tb.FamilyName?.Contains("A0", StringComparison.OrdinalIgnoreCase) ?? false)) ??
                titleBlocks.FirstOrDefault();

            ElementId titleBlockId = chosenTitleBlock?.Id ?? ElementId.InvalidElementId;

            var existingSheetNumbers = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber),
                StringComparer.OrdinalIgnoreCase);

            int sheetsCreated = 0;
            int viewportsPlaced = 0;
            ViewSheet? firstSheet = null;

            // Distribui até 4 vistas isométricas por prancha (grade 2x2)
            const int viewsPerSheet = 4;
            int totalSheetsNeeded = (int)Math.Ceiling(isoViews.Count / (double)viewsPerSheet);

            using (Transaction tx = new Transaction(doc, "Bahr Hidro - Gerar Pranchas de Plotagem"))
            {
                tx.Start();

                for (int sIdx = 0; sIdx < totalSheetsNeeded; sIdx++)
                {
                    ViewSheet sheet = ViewSheet.Create(doc, titleBlockId);

                    string baseNumber = $"HID-{sIdx + 1:D2}";
                    string finalNumber = baseNumber;
                    int suffix = 1;
                    while (existingSheetNumbers.Contains(finalNumber))
                    {
                        finalNumber = $"{baseNumber}-{suffix++}";
                    }
                    existingSheetNumbers.Add(finalNumber);

                    try
                    {
                        sheet.SheetNumber = finalNumber;
                        sheet.Name = totalSheetsNeeded == 1
                            ? "INSTALAÇÕES HIDROSSANITÁRIAS — DETALHAMENTO ISOMÉTRICO 3D"
                            : $"INSTALAÇÕES HIDROSSANITÁRIAS — ISOMÉTRICOS (FOLHA {sIdx + 1}/{totalSheetsNeeded})";
                    }
                    catch { }

                    sheetsCreated++;
                    firstSheet ??= sheet;

                    // Calcula área útil da folha (descontando margens e coluna direita do carimbo)
                    BoundingBoxUV outline = sheet.Outline;
                    double minU = outline != null && outline.Max.U > outline.Min.U ? outline.Min.U : 0.0;
                    double maxU = outline != null && outline.Max.U > outline.Min.U ? outline.Max.U : 2.76; // ~841 mm (A1)
                    double minV = outline != null && outline.Max.V > outline.Min.V ? outline.Min.V : 0.0;
                    double maxV = outline != null && outline.Max.V > outline.Min.V ? outline.Max.V : 1.95; // ~594 mm (A1)

                    double usableMinU = minU + 0.12;
                    double usableMaxU = maxU - 0.55; // Reserva espaço do selo/carimbo à direita
                    double usableMinV = minV + 0.12;
                    double usableMaxV = maxV - 0.12;

                    if (usableMaxU <= usableMinU)
                    {
                        usableMinU = minU + 0.05;
                        usableMaxU = maxU - 0.05;
                    }

                    double midU = (usableMinU + usableMaxU) / 2.0;
                    double midV = (usableMinV + usableMaxV) / 2.0;

                    XYZ[] gridCenters =
                    {
                        new((usableMinU + midU) / 2.0, (midV + usableMaxV) / 2.0, 0), // Superior Esquerdo
                        new((midU + usableMaxU) / 2.0, (midV + usableMaxV) / 2.0, 0), // Superior Direito
                        new((usableMinU + midU) / 2.0, (usableMinV + midV) / 2.0, 0), // Inferior Esquerdo
                        new((midU + usableMaxU) / 2.0, (usableMinV + midV) / 2.0, 0)  // Inferior Direito
                    };

                    var batch = isoViews.Skip(sIdx * viewsPerSheet).Take(viewsPerSheet).ToList();
                    for (int vIdx = 0; vIdx < batch.Count; vIdx++)
                    {
                        View3D view = batch[vIdx];
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                            continue;

                        try
                        {
                            Viewport.Create(doc, sheet.Id, view.Id, gridCenters[vIdx]);
                            viewportsPlaced++;
                        }
                        catch { }
                    }
                }

                tx.Commit();
            }

            if (firstSheet != null)
            {
                try { uidoc.ActiveView = firstSheet; } catch { }
            }

            string tbName = chosenTitleBlock != null ? $"{chosenTitleBlock.FamilyName} ({chosenTitleBlock.Name})" : "Folha Padrão";
            string summary = $"Pranchas Geradas: {sheetsCreated} prancha(s) criada(s) [{tbName}] com {viewportsPlaced} vista(s) isométrica(s) posicionada(s).";
            return new SheetGenerationResult(sheetsCreated, viewportsPlaced, summary);
        }

        private static void CollectStructuralTargets(
            Document targetDoc,
            Transform tr,
            string sourceLabel,
            List<(string Label, WorldAabb Box)> output)
        {
            var categories = new[]
            {
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_GenericModel
            };

            var elements = new FilteredElementCollector(targetDoc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element elem in elements)
            {
                string elemName = BuildElementName(elem);
                if (elem.Category?.Id.Value == (long)BuiltInCategory.OST_GenericModel)
                {
                    string lower = RemoverAcentos(elemName).ToLowerInvariant();
                    if (!lower.Contains("stud") && !lower.Contains("montante") && !lower.Contains("perfil") &&
                        !lower.Contains("steel") && !lower.Contains("lsf") && !lower.Contains("column") &&
                        !lower.Contains("beam") && !lower.Contains("member"))
                        continue;
                }

                BoundingBoxXYZ? bb = elem.get_BoundingBox(null);
                if (bb == null) continue;

                WorldAabb wb = ToWorldAabb(bb, tr);
                double dx = UnitUtils.ConvertFromInternalUnits(wb.MaxX - wb.MinX, UnitTypeId.Meters);
                double dy = UnitUtils.ConvertFromInternalUnits(wb.MaxY - wb.MinY, UnitTypeId.Meters);
                double dz = UnitUtils.ConvertFromInternalUnits(wb.MaxZ - wb.MinZ, UnitTypeId.Meters);

                if ((dx > 3.5 && dy > 3.5) || dz < 0.03) continue;

                output.Add(($"{elemName} [{sourceLabel}]", wb));
            }
        }

        private static string BuildElementName(Element elem)
        {
            if (elem is FamilyInstance fi && !string.IsNullOrWhiteSpace(fi.Symbol?.FamilyName))
                return $"{fi.Symbol.FamilyName} ({fi.Name})";
            return elem.Name;
        }

        private static WorldAabb ToWorldAabb(BoundingBoxXYZ bb, Transform tr)
        {
            XYZ[] corners =
            {
                new(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new(bb.Max.X, bb.Max.Y, bb.Min.Z),
                new(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new(bb.Max.X, bb.Max.Y, bb.Max.Z)
            };

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var c in corners)
            {
                XYZ p = tr.OfPoint(c);
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            return new WorldAabb(minX, minY, minZ, maxX, maxY, maxZ);
        }

        private static WorldAabb ShrinkBox(WorldAabb b, double marginFeet)
        {
            if ((b.MaxX - b.MinX) <= marginFeet * 2.5 ||
                (b.MaxY - b.MinY) <= marginFeet * 2.5 ||
                (b.MaxZ - b.MinZ) <= marginFeet * 2.5)
                return b;

            return new WorldAabb(
                b.MinX + marginFeet,
                b.MinY + marginFeet,
                b.MinZ + marginFeet,
                b.MaxX - marginFeet,
                b.MaxY - marginFeet,
                b.MaxZ - marginFeet);
        }

        private static bool BoxesOverlap(WorldAabb a, WorldAabb b)
        {
            return a.MinX <= b.MaxX && a.MaxX >= b.MinX &&
                   a.MinY <= b.MaxY && a.MaxY >= b.MinY &&
                   a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
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

        private readonly record struct WorldAabb(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);
    }
}
