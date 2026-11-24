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
        private const double Cos30 = 0.8660254037844386;
        private const double Sin30 = 0.5;

        public static (Executive3DDocResult Result, ElementId TargetViewId) GenerateIsometricsAndAbntSheet(
            Document doc,
            bool createIsometrics,
            bool createAbntSheet,
            bool createNativeSchedules = true,
            bool createIsometrics2D = true)
        {
            if (!createIsometrics && !createIsometrics2D && !createAbntSheet && !createNativeSchedules)
                throw new InvalidOperationException("Selecione ao menos uma opção (Isométrica 2D, Isométrica 3D, Tabelas ou Prancha ABNT).");

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

            var zones = DetectWetAreaZones(doc, fixtures, pipes);
            var createdIso2DViews = new List<ViewDrafting>();
            var createdIso3DViews = new List<View3D>();
            var createdSchedules = new List<ViewSchedule>();
            int tagsPlaced = 0;
            ElementId activeTargetId = ElementId.InvalidElementId;

            using (Transaction tx = new Transaction(doc, "Smart Hidro - Gerar Isométricos 2D/3D e Prancha ABNT"))
            {
                tx.Start();

                // 1. Vistas Isométricas 2D Esquemáticas (Projeção 30° ABNT - Escala 1:20)
                if (createIsometrics2D)
                {
                    ViewFamilyType? vftDrafting = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);

                    ElementId textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                    if (textTypeId == ElementId.InvalidElementId)
                    {
                        textTypeId = new FilteredElementCollector(doc)
                            .OfClass(typeof(TextNoteType))
                            .FirstElementId();
                    }

                    if (vftDrafting != null)
                    {
                        foreach (var (zoneName, box, zonePipes, zoneFixtures) in zones.Take(4))
                        {
                            if (zonePipes.Count == 0 && zoneFixtures.Count == 0) continue;

                            ViewDrafting iso2d = Build2DIsometricDraftingView(
                                doc,
                                vftDrafting.Id,
                                textTypeId,
                                zoneName,
                                box,
                                zonePipes,
                                zoneFixtures);

                            createdIso2DViews.Add(iso2d);
                            if (activeTargetId == ElementId.InvalidElementId)
                                activeTargetId = iso2d.Id;
                        }
                    }
                }

                // 2. Vistas Isométricas 3D (Linha Oculta ABNT - evita loop de shader 63% na prancha)
                if (createIsometrics)
                {
                    ViewFamilyType? vft3d = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

                    if (vft3d != null)
                    {
                        foreach (var (zoneName, box, zonePipes, _) in zones.Take(4))
                        {
                            string viewName = MakeUniqueViewName(doc, $"Smart Hidro - Isométrico 3D - {zoneName}");
                            View3D iso = View3D.CreateIsometric(doc, vft3d.Id);
                            iso.Name = viewName;
                            iso.Scale = 25;
                            iso.DetailLevel = ViewDetailLevel.Fine;
                            iso.DisplayStyle = DisplayStyle.HLR;
                            iso.IsSectionBoxActive = true;
                            iso.SetSectionBox(box);

                            if (iso.CanSaveOrientation())
                                iso.SaveOrientationAndLock();

                            // Etiqueta apenas quando não estiver montando prancha em lote para evitar recálculo gráfico
                            if (!createAbntSheet)
                            {
                                foreach (var p in zonePipes.Take(10))
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
                            }

                            createdIso3DViews.Add(iso);
                            if (activeTargetId == ElementId.InvalidElementId)
                                activeTargetId = iso.Id;
                        }
                    }
                }

                // 3. Tabelas Nativas do Revit (Tubulações, Conexões e Aparelhos Sanitários)
                if (createNativeSchedules)
                {
                    var pipeSched = CreatePipeSchedule(doc);
                    if (pipeSched != null) createdSchedules.Add(pipeSched);

                    var fittingSched = CreateFittingSchedule(doc);
                    if (fittingSched != null) createdSchedules.Add(fittingSched);

                    var fixtureSched = CreateFixtureSchedule(doc);
                    if (fixtureSched != null) createdSchedules.Add(fixtureSched);

                    if (activeTargetId == ElementId.InvalidElementId && createdSchedules.Count > 0)
                        activeTargetId = createdSchedules[0].Id;
                }

                // Regenera uma única vez antes de inserir Viewports na folha (elimina loop de 63%)
                if (createAbntSheet)
                {
                    doc.Regenerate();
                }

                // 4. Montagem Limpa da Prancha Executiva ABNT (HID-01)
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
                    sheet.Name = "INSTALAÇÕES HIDROSSANITÁRIAS — ISOMÉTRICOS 2D/3D E QUANTITATIVOS (ABNT)";

                    double cursorX = UnitUtils.ConvertToInternalUnits(0.16, UnitTypeId.Meters);
                    double stepX = UnitUtils.ConvertToInternalUnits(0.20, UnitTypeId.Meters);

                    double row2DY = UnitUtils.ConvertToInternalUnits(0.46, UnitTypeId.Meters);
                    double row3DY = createdIso2DViews.Count > 0
                        ? UnitUtils.ConvertToInternalUnits(0.25, UnitTypeId.Meters)
                        : UnitUtils.ConvertToInternalUnits(0.42, UnitTypeId.Meters);

                    // Insere até 3 Isométricos 2D na faixa superior
                    for (int i = 0; i < Math.Min(3, createdIso2DViews.Count); i++)
                    {
                        var v2d = createdIso2DViews[i];
                        try
                        {
                            if (Viewport.CanAddViewToSheet(doc, sheet.Id, v2d.Id))
                                Viewport.Create(doc, sheet.Id, v2d.Id, new XYZ(cursorX + i * stepX, row2DY, 0));
                        }
                        catch { }
                    }

                    // Insere até 3 Isométricos 3D na faixa inferior
                    for (int i = 0; i < Math.Min(3, createdIso3DViews.Count); i++)
                    {
                        var v3d = createdIso3DViews[i];
                        try
                        {
                            if (Viewport.CanAddViewToSheet(doc, sheet.Id, v3d.Id))
                                Viewport.Create(doc, sheet.Id, v3d.Id, new XYZ(cursorX + i * stepX, row3DY, 0));
                        }
                        catch { }
                    }

                    // Insere Esquema Vertical de Prumadas caso já exista
                    ViewDrafting? riserView = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewDrafting))
                        .Cast<ViewDrafting>()
                        .FirstOrDefault(v => v.Name.Contains("Esquema Vertical de Prumadas", StringComparison.OrdinalIgnoreCase));

                    if (riserView != null)
                    {
                        try
                        {
                            if (Viewport.CanAddViewToSheet(doc, sheet.Id, riserView.Id))
                                Viewport.Create(doc, sheet.Id, riserView.Id, new XYZ(cursorX, UnitUtils.ConvertToInternalUnits(0.11, UnitTypeId.Meters), 0));
                        }
                        catch { }
                    }

                    // Insere Tabelas Nativas na coluna direita da prancha
                    if (createdSchedules.Count > 0)
                    {
                        double schedX = UnitUtils.ConvertToInternalUnits(0.68, UnitTypeId.Meters);
                        double schedTopY = UnitUtils.ConvertToInternalUnits(0.54, UnitTypeId.Meters);
                        double schedStepY = UnitUtils.ConvertToInternalUnits(0.16, UnitTypeId.Meters);

                        BoundingBoxUV outline = sheet.Outline;
                        if (outline != null && (outline.Max.U - outline.Min.U) > UnitUtils.ConvertToInternalUnits(0.50, UnitTypeId.Meters))
                        {
                            schedX = outline.Max.U - UnitUtils.ConvertToInternalUnits(0.24, UnitTypeId.Meters);
                            schedTopY = outline.Max.V - UnitUtils.ConvertToInternalUnits(0.05, UnitTypeId.Meters);
                        }

                        for (int i = 0; i < createdSchedules.Count; i++)
                        {
                            try
                            {
                                ScheduleSheetInstance.Create(
                                    doc,
                                    sheet.Id,
                                    createdSchedules[i].Id,
                                    new XYZ(schedX, schedTopY - i * schedStepY, 0));
                            }
                            catch { }
                        }
                    }

                    activeTargetId = sheet.Id;
                }

                tx.Commit();

                string summary = $"Concluído sem travamentos: {createdIso2DViews.Count} isométrico(s) 2D (30°), " +
                                 $"{createdIso3DViews.Count} isométrico(s) 3D, {createdSchedules.Count} tabela(s) nativa(s)" +
                                 (createAbntSheet ? $" e Prancha '{sheetCode}' gerada." : ".");

                return (new Executive3DDocResult(createdIso3DViews.Count, tagsPlaced, sheetCode, summary, createdSchedules.Count, createdIso2DViews.Count), activeTargetId);
            }
        }

        private static ViewDrafting Build2DIsometricDraftingView(
            Document doc,
            ElementId draftingVftId,
            ElementId textTypeId,
            string zoneName,
            BoundingBoxXYZ zoneBox,
            List<Pipe> zonePipes,
            List<Element> zoneFixtures)
        {
            ViewDrafting view = ViewDrafting.Create(doc, draftingVftId);
            view.Name = MakeUniqueViewName(doc, $"Smart Hidro - Isométrico 2D - {zoneName}");
            view.Scale = 20;

            XYZ origin3D = zoneBox.Min;
            XYZ ProjectIso2D(XYZ p3d)
            {
                double dx = p3d.X - origin3D.X;
                double dy = p3d.Y - origin3D.Y;
                double dz = p3d.Z - origin3D.Z;
                double u = (dx - dy) * Cos30;
                double v = dz + (dx + dy) * Sin30;
                return new XYZ(u, v, 0.0);
            }

            var ogsCold = MakeLineOgs(new Color(0, 102, 204), 5);
            var ogsHot = MakeLineOgs(new Color(220, 38, 38), 5);
            var ogsSanitary = MakeLineOgs(new Color(146, 64, 14), 6);
            var ogsVent = MakeLineOgs(new Color(22, 163, 74), 4);
            var ogsNode = MakeLineOgs(new Color(15, 23, 42), 3);
            var ogsFixture = MakeLineOgs(new Color(71, 85, 105), 3);
            var ogsFrame = MakeLineOgs(new Color(148, 163, 184), 2);

            double minU = double.MaxValue, minV = double.MaxValue;
            double maxU = double.MinValue, maxV = double.MinValue;

            void ExpandBounds(XYZ pt)
            {
                if (pt.X < minU) minU = pt.X;
                if (pt.Y < minV) minV = pt.Y;
                if (pt.X > maxU) maxU = pt.X;
                if (pt.Y > maxV) maxV = pt.Y;
            }

            var nodePoints = new List<XYZ>();
            double nodeMergeTol = UnitUtils.ConvertToInternalUnits(0.03, UnitTypeId.Meters);
            double nodeRadius = UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);

            void RegisterNode(XYZ pt2d)
            {
                foreach (var existing in nodePoints)
                {
                    if (existing.DistanceTo(pt2d) <= nodeMergeTol)
                        return;
                }
                nodePoints.Add(pt2d);
            }

            foreach (var p in zonePipes)
            {
                if (p.Location is not LocationCurve lc || lc.Curve == null) continue;
                XYZ a3 = lc.Curve.GetEndPoint(0);
                XYZ b3 = lc.Curve.GetEndPoint(1);
                XYZ a2 = ProjectIso2D(a3);
                XYZ b2 = ProjectIso2D(b3);

                ExpandBounds(a2);
                ExpandBounds(b2);

                if (a2.DistanceTo(b2) < 0.008) continue;

                string sysRaw = p.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "";
                string sysNorm = Normalize(sysRaw);

                OverrideGraphicSettings ogs = ogsCold;
                string sysCode = "AF";
                if (sysNorm.Contains("quente") || sysNorm.Contains("hot"))
                {
                    ogs = ogsHot;
                    sysCode = "AQ";
                }
                else if (sysNorm.Contains("esgoto") || sysNorm.Contains("sanit") || sysNorm.Contains("gordura") || sysNorm.Contains("pluvial"))
                {
                    ogs = ogsSanitary;
                    sysCode = sysNorm.Contains("pluvial") ? "AP" : "ES";
                }
                else if (sysNorm.Contains("ventil"))
                {
                    ogs = ogsVent;
                    sysCode = "VENT";
                }

                DetailCurve dc = doc.Create.NewDetailCurve(view, Line.CreateBound(a2, b2));
                view.SetElementOverrides(dc.Id, ogs);

                RegisterNode(a2);
                RegisterNode(b2);

                double lenM = UnitUtils.ConvertFromInternalUnits(a3.DistanceTo(b3), UnitTypeId.Meters);
                if (lenM >= 0.45)
                {
                    XYZ mid2 = (a2 + b2) * 0.5;
                    XYZ dir = (b2 - a2).Normalize();
                    XYZ perp = new XYZ(-dir.Y, dir.X, 0.0);
                    double arrLen = UnitUtils.ConvertToInternalUnits(0.08, UnitTypeId.Meters);
                    double arrWid = UnitUtils.ConvertToInternalUnits(0.035, UnitTypeId.Meters);

                    XYZ tip = mid2 + dir * (arrLen * 0.5);
                    XYZ w1 = tip - dir * arrLen + perp * arrWid;
                    XYZ w2 = tip - dir * arrLen - perp * arrWid;

                    if (tip.DistanceTo(w1) > 0.005 && tip.DistanceTo(w2) > 0.005)
                    {
                        var l1 = doc.Create.NewDetailCurve(view, Line.CreateBound(tip, w1));
                        var l2 = doc.Create.NewDetailCurve(view, Line.CreateBound(tip, w2));
                        view.SetElementOverrides(l1.Id, ogs);
                        view.SetElementOverrides(l2.Id, ogs);
                    }
                }

                if (textTypeId != ElementId.InvalidElementId && lenM >= 0.25)
                {
                    double diaFt = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0.08;
                    int dnMm = (int)Math.Round(UnitUtils.ConvertFromInternalUnits(diaFt, UnitTypeId.Millimeters));
                    string tagText = $"{sysCode} Ø{dnMm}";
                    if (sysCode == "ES" || sysCode == "AP")
                    {
                        double slopeVal = p.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE)?.AsDouble() ?? 0.0;
                        if (slopeVal > 0.001)
                            tagText += $" (i={slopeVal * 100.0:0.#}%)";
                    }

                    XYZ labelPos = (a2 + b2) * 0.5 + new XYZ(
                        UnitUtils.ConvertToInternalUnits(0.04, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(0.07, UnitTypeId.Meters),
                        0.0);
                    try
                    {
                        TextNote.Create(doc, view.Id, labelPos, tagText, textTypeId);
                    }
                    catch { }
                }
            }

            foreach (var np in nodePoints)
            {
                try
                {
                    Arc c1 = Arc.Create(np, nodeRadius, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY);
                    Arc c2 = Arc.Create(np, nodeRadius, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY);
                    var d1 = doc.Create.NewDetailCurve(view, c1);
                    var d2 = doc.Create.NewDetailCurve(view, c2);
                    view.SetElementOverrides(d1.Id, ogsNode);
                    view.SetElementOverrides(d2.Id, ogsNode);
                }
                catch { }
            }

            double fxHalf = UnitUtils.ConvertToInternalUnits(0.11, UnitTypeId.Meters);
            foreach (var fx in zoneFixtures.Take(16))
            {
                BoundingBoxXYZ? bb = fx.get_BoundingBox(null);
                if (bb == null) continue;
                XYZ c3 = (bb.Min + bb.Max) * 0.5;
                XYZ c2 = ProjectIso2D(c3);
                ExpandBounds(c2);

                XYZ pTop = c2 + new XYZ(0, fxHalf * 0.65, 0);
                XYZ pRight = c2 + new XYZ(fxHalf, 0, 0);
                XYZ pBot = c2 - new XYZ(0, fxHalf * 0.65, 0);
                XYZ pLeft = c2 - new XYZ(fxHalf, 0, 0);

                foreach (var edge in new[]
                {
                    Line.CreateBound(pTop, pRight),
                    Line.CreateBound(pRight, pBot),
                    Line.CreateBound(pBot, pLeft),
                    Line.CreateBound(pLeft, pTop)
                })
                {
                    var dc = doc.Create.NewDetailCurve(view, edge);
                    view.SetElementOverrides(dc.Id, ogsFixture);
                }

                if (textTypeId != ElementId.InvalidElementId)
                {
                    string shortLabel = GetShortFixtureLabel(fx);
                    XYZ txtPos = pTop + new XYZ(
                        UnitUtils.ConvertToInternalUnits(0.05, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(0.10, UnitTypeId.Meters),
                        0.0);
                    try
                    {
                        var leader = doc.Create.NewDetailCurve(view, Line.CreateBound(pTop, txtPos));
                        view.SetElementOverrides(leader.Id, ogsFrame);
                        TextNote.Create(doc, view.Id, txtPos, shortLabel, textTypeId);
                    }
                    catch { }
                }
            }

            if (minU > maxU)
            {
                minU = 0; minV = 0;
                maxU = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Meters);
                maxV = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Meters);
            }

            double pad = UnitUtils.ConvertToInternalUnits(0.45, UnitTypeId.Meters);
            double headerH = UnitUtils.ConvertToInternalUnits(0.32, UnitTypeId.Meters);

            XYZ fBL = new XYZ(minU - pad, minV - pad, 0);
            XYZ fBR = new XYZ(maxU + pad, minV - pad, 0);
            XYZ fTR = new XYZ(maxU + pad, maxV + pad + headerH, 0);
            XYZ fTL = new XYZ(minU - pad, maxV + pad + headerH, 0);
            XYZ fDivL = new XYZ(minU - pad, maxV + pad, 0);
            XYZ fDivR = new XYZ(maxU + pad, maxV + pad, 0);

            foreach (var borderLine in new[]
            {
                Line.CreateBound(fBL, fBR),
                Line.CreateBound(fBR, fTR),
                Line.CreateBound(fTR, fTL),
                Line.CreateBound(fTL, fBL),
                Line.CreateBound(fDivL, fDivR)
            })
            {
                var dc = doc.Create.NewDetailCurve(view, borderLine);
                view.SetElementOverrides(dc.Id, ogsFrame);
            }

            XYZ triadOrigin = fBL + new XYZ(
                UnitUtils.ConvertToInternalUnits(0.24, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(0.18, UnitTypeId.Meters),
                0);
            double axisLen = UnitUtils.ConvertToInternalUnits(0.18, UnitTypeId.Meters);
            XYZ axisZ = triadOrigin + new XYZ(0, axisLen, 0);
            XYZ axisX = triadOrigin + new XYZ(axisLen * Cos30, axisLen * Sin30, 0);
            XYZ axisY = triadOrigin + new XYZ(-axisLen * Cos30, axisLen * Sin30, 0);

            foreach (var ax in new[]
            {
                Line.CreateBound(triadOrigin, axisZ),
                Line.CreateBound(triadOrigin, axisX),
                Line.CreateBound(triadOrigin, axisY)
            })
            {
                var dc = doc.Create.NewDetailCurve(view, ax);
                view.SetElementOverrides(dc.Id, ogsNode);
            }

            if (textTypeId != ElementId.InvalidElementId)
            {
                try
                {
                    XYZ titlePos = fDivL + new XYZ(
                        UnitUtils.ConvertToInternalUnits(0.08, UnitTypeId.Meters),
                        UnitUtils.ConvertToInternalUnits(0.24, UnitTypeId.Meters),
                        0);
                    TextNote.Create(doc, view.Id, titlePos, $"ISOMÉTRICO 2D — {zoneName.ToUpperInvariant()}  |  PROJEÇÃO 30° ABNT (ESC. 1:20)", textTypeId);
                    TextNote.Create(doc, view.Id, axisZ + new XYZ(0.02, 0.04, 0), "Z", textTypeId);
                    TextNote.Create(doc, view.Id, axisX + new XYZ(0.02, 0.02, 0), "X", textTypeId);
                    TextNote.Create(doc, view.Id, axisY + new XYZ(-0.06, 0.02, 0), "Y", textTypeId);
                }
                catch { }
            }

            return view;
        }

        private static string GetShortFixtureLabel(Element fx)
        {
            string raw = Normalize($"{(fx as FamilyInstance)?.Symbol?.FamilyName} {fx.Name}");
            if (raw.Contains("bacia") || raw.Contains("vaso") || raw.Contains("sanit")) return "Bacia Sanitária";
            if (raw.Contains("lavat") || raw.Contains("cuba")) return "Lavatório";
            if (raw.Contains("chuveiro") || raw.Contains("ducha")) return "Chuveiro";
            if (raw.Contains("pia") || raw.Contains("cozinha")) return "Pia Cozinha";
            if (raw.Contains("tanque")) return "Tanque";
            if (raw.Contains("maquina")) return "Máq. Lavar";
            if (raw.Contains("ralo") || raw.Contains("sifonada")) return "Cx. Sifonada";
            return string.IsNullOrWhiteSpace(fx.Name) ? "Aparelho" : fx.Name;
        }

        private static OverrideGraphicSettings MakeLineOgs(Color color, int weight)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetProjectionLineWeight(weight);
            return ogs;
        }

        private static ViewSchedule? CreatePipeSchedule(Document doc)
        {
            try
            {
                ViewSchedule sched = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_PipeCurves));
                sched.Name = MakeUniqueViewName(doc, "Smart Hidro - Tabela de Tubulações");
                ScheduleDefinition def = sched.Definition;
                def.IsItemized = false;
                def.ShowGrandTotal = true;
                def.ShowGrandTotalTitle = true;

                IList<SchedulableField> avail = def.GetSchedulableFields();
                var fSys = TryAddParamField(def, avail, BuiltInParameter.RBS_SYSTEM_NAME_PARAM, "Sistema");
                var fType = TryAddParamField(def, avail, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM, "Tubulação / Material");
                var fDia = TryAddParamField(def, avail, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, "Diâmetro");
                TryAddParamField(def, avail, BuiltInParameter.CURVE_ELEM_LENGTH, "Comprimento Total", showTotals: true);

                TryAddSortGroup(def, fSys);
                TryAddSortGroup(def, fType);
                TryAddSortGroup(def, fDia);
                return sched;
            }
            catch { return null; }
        }

        private static ViewSchedule? CreateFittingSchedule(Document doc)
        {
            try
            {
                ViewSchedule sched = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_PipeFitting));
                sched.Name = MakeUniqueViewName(doc, "Smart Hidro - Tabela de Conexões");
                ScheduleDefinition def = sched.Definition;
                def.IsItemized = false;
                def.ShowGrandTotal = true;
                def.ShowGrandTotalTitle = true;

                IList<SchedulableField> avail = def.GetSchedulableFields();
                var fSys = TryAddParamField(def, avail, BuiltInParameter.RBS_SYSTEM_NAME_PARAM, "Sistema");
                var fType = TryAddParamField(def, avail, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM, "Conexão / Peça");
                var fSize = TryAddParamField(def, avail, BuiltInParameter.RBS_CALCULATED_SIZE, "Bitola");
                TryAddCountField(def, avail, "Qtd.");

                TryAddSortGroup(def, fSys);
                TryAddSortGroup(def, fType);
                TryAddSortGroup(def, fSize);
                return sched;
            }
            catch { return null; }
        }

        private static ViewSchedule? CreateFixtureSchedule(Document doc)
        {
            try
            {
                ViewSchedule sched = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_PlumbingFixtures));
                sched.Name = MakeUniqueViewName(doc, "Smart Hidro - Tabela de Aparelhos Sanitários");
                ScheduleDefinition def = sched.Definition;
                def.IsItemized = false;
                def.ShowGrandTotal = true;
                def.ShowGrandTotalTitle = true;

                IList<SchedulableField> avail = def.GetSchedulableFields();
                var fLvl = TryAddParamField(def, avail, BuiltInParameter.SCHEDULE_LEVEL_PARAM, "Nível");
                var fType = TryAddParamField(def, avail, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM, "Aparelho Sanitário");
                TryAddCountField(def, avail, "Qtd.");

                TryAddSortGroup(def, fLvl);
                TryAddSortGroup(def, fType);
                return sched;
            }
            catch { return null; }
        }

        private static ScheduleField? TryAddParamField(ScheduleDefinition def, IList<SchedulableField> available, BuiltInParameter bip, string? heading = null, bool showTotals = false)
        {
            ElementId targetId = new ElementId(bip);
            SchedulableField? sf = available.FirstOrDefault(f => f.ParameterId == targetId);
            if (sf == null) return null;

            ScheduleField field = def.AddField(sf);
            if (!string.IsNullOrWhiteSpace(heading)) field.ColumnHeading = heading;
            if (showTotals) field.DisplayType = ScheduleFieldDisplayType.Totals;
            return field;
        }

        private static ScheduleField? TryAddCountField(ScheduleDefinition def, IList<SchedulableField> available, string heading = "Qtd.")
        {
            SchedulableField? sf = available.FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count);
            if (sf == null) return null;

            ScheduleField field = def.AddField(sf);
            field.ColumnHeading = heading;
            field.DisplayType = ScheduleFieldDisplayType.Totals;
            return field;
        }

        private static void TryAddSortGroup(ScheduleDefinition def, ScheduleField? field)
        {
            if (field == null) return;
            try { def.AddSortGroupField(new ScheduleSortGroupField(field.FieldId)); } catch { }
        }

        private static List<(string Name, BoundingBoxXYZ Box, List<Pipe> Pipes, List<Element> Fixtures)> DetectWetAreaZones(Document doc, List<Element> fixtures, List<Pipe> pipes)
        {
            var result = new List<(string Name, BoundingBoxXYZ Box, List<Pipe> Pipes, List<Element> Fixtures)>();
            double marginFt = UnitUtils.ConvertToInternalUnits(0.65, UnitTypeId.Meters);

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
                var roomFixtures = fixtures.Where(f => IsElementInsideBox(f, expanded)).ToList();
                if (roomFixtures.Count > 0 || roomPipes.Count >= 2)
                {
                    string rName = SanitizeName($"{r.Name} ({r.Level?.Name ?? "Nível"})");
                    result.Add((rName, expanded, roomPipes, roomFixtures));
                }
            }

            if (result.Count > 0) return result;

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
                var zFixtures = fixtures.Where(f => IsElementInsideBox(f, box)).ToList();
                result.Add(($"Setor Molhado {idx++:00}", box, zPipes, zFixtures));
            }

            if (result.Count == 0 && pipes.Count > 0)
            {
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                foreach (var p in pipes)
                {
                    BoundingBoxXYZ? pb = p.get_BoundingBox(null);
                    if (pb == null) continue;
                    minX = Math.Min(minX, pb.Min.X); minY = Math.Min(minY, pb.Min.Y); minZ = Math.Min(minZ, pb.Min.Z);
                    maxX = Math.Max(maxX, pb.Max.X); maxY = Math.Max(maxY, pb.Max.Y); maxZ = Math.Max(maxZ, pb.Max.Z);
                }
                if (minX < maxX)
                {
                    var globalBox = new BoundingBoxXYZ
                    {
                        Min = new XYZ(minX - marginFt, minY - marginFt, minZ - marginFt),
                        Max = new XYZ(maxX + marginFt, maxY + marginFt, maxZ + marginFt)
                    };
                    result.Add(("Rede Geral 01", globalBox, pipes, fixtures));
                }
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
