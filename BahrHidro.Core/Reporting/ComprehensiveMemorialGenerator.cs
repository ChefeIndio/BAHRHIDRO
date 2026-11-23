using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.Core.Hydraulic.Rainwater;

namespace BahrHidro.Core.Reporting
{
    public sealed class ReservoirSnapshotData
    {
        public ReservoirSnapshotData() { }
        public string BuildingCategory { get; set; } = "";
        public int Population { get; set; }
        public int ReserveDays { get; set; }
        public double FireReserveLiters { get; set; }
        public double DailyConsumptionLiters { get; set; }
        public double TotalRequiredLiters { get; set; }
        public double UpperVolumeLiters { get; set; }
        public int CommercialUpperLiters { get; set; }
        public double LowerVolumeLiters { get; set; }
        public int CommercialLowerLiters { get; set; }
    }

    public sealed class BoilerSnapshotData
    {
        public BoilerSnapshotData() { }
        public string BuildingProfile { get; set; } = "";
        public int Occupants { get; set; }
        public double ColdWaterPressureMca { get; set; }
        public double DailyConsumptionLiters { get; set; }
        public double RequiredVolumeLiters { get; set; }
        public int CommercialBoilerLiters { get; set; }
        public double HeatingPowerKw { get; set; }
        public string PressureClassification { get; set; } = "";
    }

    public sealed class SewerSnapshotData
    {
        public SewerSnapshotData() { }
        public int TotalUhc { get; set; }
        public int RequiredDiameterMm { get; set; }
        public double MinimumSlopePercent { get; set; }
        public string ComponentType { get; set; } = "";
        public List<string> FixturesSummary { get; set; } = new();
        public string MandatoryRecommendations { get; set; } = "";
    }

    public sealed class GreaseTrapSnapshotData
    {
        public GreaseTrapSnapshotData() { }
        public int KitchenCount { get; set; }
        public int Population { get; set; }
        public string ModelName { get; set; } = "";
        public double MinimumVolumeLiters { get; set; }
        public string DimensionsOrDiameter { get; set; } = "";
        public int SeptumSubmersionCm { get; set; }
        public int OutletDiameterMm { get; set; }
    }

    public sealed class PumpSnapshotData
    {
        public PumpSnapshotData() { }
        public bool RequiresPump { get; set; }
        public double RequiredAmtMca { get; set; }
        public double TotalFlowLps { get; set; }
        public double EstimatedPowerCv { get; set; }
        public string CriticalNodeName { get; set; } = "";
        public double CriticalNodePressureMca { get; set; }
    }

    public sealed class ProjectEngineeringSnapshot
    {
        public ProjectEngineeringSnapshot() { }
        public string ProjectName { get; set; } = "Projeto Hidrossanitário BIM";
        public string ClientName { get; set; } = "";
        public string Address { get; set; } = "";
        public string EngineerName { get; set; } = "";
        public string CreaNumber { get; set; } = "";
        public string ArtNumber { get; set; } = "";
        public string Revision { get; set; } = "R00";
        public string AppliedNorms { get; set; } = "ABNT NBR 5626 / NBR 7198 / NBR 8160 / NBR 10844";

        public ReservoirSnapshotData? Reservoir { get; set; }
        public NetworkCalculationResult? ColdWaterNetwork { get; set; }
        public PumpSnapshotData? Pump { get; set; }
        public List<string> ColdWaterDiagnosticAlerts { get; set; } = new();
        public BoilerSnapshotData? Boiler { get; set; }
        public SewerSnapshotData? Sewer { get; set; }
        public RainwaterResult? Rainwater { get; set; }
        public GreaseTrapSnapshotData? GreaseTrap { get; set; }
    }

    public static class ComprehensiveMemorialGenerator
    {
        public static List<string> GenerateAllFromJson(string snapshotJson, string? takeoffJson, string basePathWithoutExt, string mode)
        {
            var snapshot = JsonSerializer.Deserialize<ProjectEngineeringSnapshot>(snapshotJson) ?? new ProjectEngineeringSnapshot();
            BimMaterialTakeoff? takeoff = !string.IsNullOrWhiteSpace(takeoffJson)
                ? JsonSerializer.Deserialize<BimMaterialTakeoff>(takeoffJson)
                : null;

            return Generate(snapshot, takeoff, basePathWithoutExt, mode);
        }

        public static List<string> Generate(ProjectEngineeringSnapshot s, BimMaterialTakeoff? takeoff, string basePathWithoutExt, string mode)
        {
            string dir = Path.GetDirectoryName(basePathWithoutExt) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            Directory.CreateDirectory(dir);
            string baseName = Path.GetFileNameWithoutExtension(basePathWithoutExt);
            var generatedFiles = new List<string>();

            if (mode == "ALL" || mode == "EXCEL")
            {
                string xlsxPath = Path.Combine(dir, $"{baseName}_Memorial_Calculo_Completo.xlsx");
                GenerateComprehensiveExcel(s, takeoff, xlsxPath);
                generatedFiles.Add(xlsxPath);
            }

            if (mode == "ALL" || mode == "DESCRITIVO")
            {
                string descDocPath = Path.Combine(dir, $"{baseName}_Memorial_Descritivo.doc");
                File.WriteAllText(descDocPath, BuildDescriptiveHtml(s), Encoding.UTF8);
                generatedFiles.Add(descDocPath);
            }

            if (mode == "ALL" || mode == "CALCULO")
            {
                string calcDocPath = Path.Combine(dir, $"{baseName}_Memorial_Calculo_Analitico.doc");
                File.WriteAllText(calcDocPath, BuildAnalyticalCalculationHtml(s), Encoding.UTF8);
                generatedFiles.Add(calcDocPath);
            }

            if (mode == "ALL" || mode == "UNIFICADO")
            {
                string unifiedHtmlPath = Path.Combine(dir, $"{baseName}_Memorial_Unificado_Completo.html");
                string unifiedDocPath = Path.Combine(dir, $"{baseName}_Memorial_Unificado_Completo.doc");
                string html = BuildUnifiedHtml(s, takeoff);
                File.WriteAllText(unifiedHtmlPath, html, Encoding.UTF8);
                File.WriteAllText(unifiedDocPath, html, Encoding.UTF8);
                generatedFiles.Add(unifiedHtmlPath);
                generatedFiles.Add(unifiedDocPath);
            }

            return generatedFiles;
        }

        public static void GenerateComprehensiveExcel(ProjectEngineeringSnapshot s, BimMaterialTakeoff? takeoff, string filePath)
        {
            if (s.ColdWaterNetwork != null || takeoff != null)
            {
                ExcelReportGenerator.GenerateProjectReportWithBom(s.ColdWaterNetwork, takeoff, filePath);
            }
            else
            {
                using var tempWb = new XLWorkbook();
                tempWb.Worksheets.Add("Resumo Executivo");
                tempWb.SaveAs(filePath);
            }

            using var wb = new XLWorkbook(filePath);

            // Aba de Resumo Executivo & Parecer Técnico
            var wsResumo = wb.Worksheets.Contains("Resumo Executivo")
                ? wb.Worksheet("Resumo Executivo")
                : wb.Worksheets.Add("Resumo Executivo", 1);

            wsResumo.Cell("A1").Value = "BAHR HIDRO — MEMORIAL DE CÁLCULO UNIFICADO E PARECER NORMATIVO";
            wsResumo.Range("A1:F1").Merge().Style
                .Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(XLColor.White)
                .Fill.SetBackgroundColor(XLColor.FromHtml("#0F172A"));

            wsResumo.Cell("A3").Value = "Empreendimento / Obra:"; wsResumo.Cell("B3").Value = s.ProjectName;
            wsResumo.Cell("A4").Value = "Proprietário / Cliente:"; wsResumo.Cell("B4").Value = s.ClientName;
            wsResumo.Cell("A5").Value = "Endereço da Obra:"; wsResumo.Cell("B5").Value = s.Address;
            wsResumo.Cell("D3").Value = "Responsável Técnico:"; wsResumo.Cell("E3").Value = $"{s.EngineerName} ({s.CreaNumber})";
            wsResumo.Cell("D4").Value = "ART / RRT:"; wsResumo.Cell("E4").Value = s.ArtNumber;
            wsResumo.Cell("D5").Value = "Revisão / Normas:"; wsResumo.Cell("E5").Value = $"{s.Revision} — {s.AppliedNorms}";
            wsResumo.Range("A3:A5").Style.Font.SetBold();
            wsResumo.Range("D3:D5").Style.Font.SetBold();

            int r = 7;
            wsResumo.Cell(r, 1).Value = "ETAPA";
            wsResumo.Cell(r, 2).Value = "DISCIPLINA / NORMA";
            wsResumo.Cell(r, 3).Value = "PARÂMETROS PRINCIPAIS";
            wsResumo.Cell(r, 4).Value = "DIMENSIONAMENTO RESULTANTE";
            wsResumo.Cell(r, 5).Value = "STATUS NORMATIVO";
            wsResumo.Range(r, 1, r, 5).Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml("#1E3A8A"));
            r++;

            if (s.Reservoir != null)
            {
                wsResumo.Cell(r, 1).Value = "1";
                wsResumo.Cell(r, 2).Value = "Reservatórios (NBR 5626)";
                wsResumo.Cell(r, 3).Value = $"{s.Reservoir.BuildingCategory} | {s.Reservoir.Population} hab. | {s.Reservoir.ReserveDays} dia(s)";
                wsResumo.Cell(r, 4).Value = $"Total: {s.Reservoir.TotalRequiredLiters:F0} L | Sup.: {s.Reservoir.CommercialUpperLiters} L | Inf.: {s.Reservoir.CommercialLowerLiters} L";
                wsResumo.Cell(r, 5).Value = "DIMENSIONADO";
                r++;
            }

            if (s.ColdWaterNetwork != null)
            {
                wsResumo.Cell(r, 1).Value = "2";
                wsResumo.Cell(r, 2).Value = "Água Fria & Bomba (NBR 5626)";
                wsResumo.Cell(r, 3).Value = $"{s.ColdWaterNetwork.Segments.Count} trechos | {s.ColdWaterNetwork.Nodes.Count} nós";
                string bombaTxt = s.Pump != null && s.Pump.RequiresPump
                    ? $" | Requer Bomba AMT {s.Pump.RequiredAmtMca:F2} m.c.a ({s.Pump.EstimatedPowerCv:F2} cv)"
                    : " | Abastecimento por gravidade OK";
                wsResumo.Cell(r, 4).Value = $"Bitolas verificadas{bombaTxt}";
                wsResumo.Cell(r, 5).Value = s.ColdWaterDiagnosticAlerts.Any(a => a.Contains("CRÍTICO") || a.Contains("ALERTA")) ? "REQUER AJUSTES" : "CONFORME NBR 5626";
                r++;
            }

            if (s.Boiler != null)
            {
                wsResumo.Cell(r, 1).Value = "3";
                wsResumo.Cell(r, 2).Value = "Água Quente & Boiler (NBR 7198)";
                wsResumo.Cell(r, 3).Value = $"{s.Boiler.Occupants} ocupantes | Pressão AF: {s.Boiler.ColdWaterPressureMca:F2} m.c.a";
                wsResumo.Cell(r, 4).Value = $"Boiler Comercial {s.Boiler.CommercialBoilerLiters} L ({s.Boiler.HeatingPowerKw:F2} kW) — {s.Boiler.PressureClassification}";
                wsResumo.Cell(r, 5).Value = "DIMENSIONADO";
                r++;
            }

            if (s.Sewer != null)
            {
                wsResumo.Cell(r, 1).Value = "4";
                wsResumo.Cell(r, 2).Value = "Esgoto Sanitário (NBR 8160)";
                wsResumo.Cell(r, 3).Value = $"Contribuição Total: {s.Sewer.TotalUhc} UHC";
                wsResumo.Cell(r, 4).Value = $"Subcoletor/Ramal DN {s.Sewer.RequiredDiameterMm} mm | Declividade mín. {s.Sewer.MinimumSlopePercent:F1}%";
                wsResumo.Cell(r, 5).Value = "CONFORME NBR 8160";
                r++;
            }

            if (s.Rainwater != null)
            {
                wsResumo.Cell(r, 1).Value = "5";
                wsResumo.Cell(r, 2).Value = "Calhas & Pluvial (NBR 10844)";
                wsResumo.Cell(r, 3).Value = $"Área: {s.Rainwater.ContributionAreaM2:F2} m² | I = {s.Rainwater.IntensityMmPerHour:F0} mm/h ({s.Rainwater.RegionName})";
                wsResumo.Cell(r, 4).Value = $"Calha {s.Rainwater.SelectedGutterDescription} | {s.Rainwater.DownspoutCount}× Condutor DN {s.Rainwater.RequiredDownspoutDiameterMm} mm";
                wsResumo.Cell(r, 5).Value = s.Rainwater.GutterMeetsRequirement ? "CONFORME NBR 10844" : "CALHA SUBDIMENSIONADA";
                r++;
            }

            if (s.GreaseTrap != null)
            {
                wsResumo.Cell(r, 1).Value = "6";
                wsResumo.Cell(r, 2).Value = "Caixa de Gordura (NBR 8160)";
                wsResumo.Cell(r, 3).Value = $"{s.GreaseTrap.KitchenCount} cozinha(s) | {s.GreaseTrap.Population} pessoa(s)";
                wsResumo.Cell(r, 4).Value = $"{s.GreaseTrap.ModelName} — Vol. mín. {s.GreaseTrap.MinimumVolumeLiters:F0} L | Saída DN {s.GreaseTrap.OutletDiameterMm}";
                wsResumo.Cell(r, 5).Value = "CONFORME NBR 8160";
                r++;
            }

            if (s.ColdWaterDiagnosticAlerts.Count > 0)
            {
                r += 2;
                wsResumo.Cell(r, 1).Value = "PARECER TÉCNICO E RECOMENDAÇÕES DE AJUSTE NO PROJETO";
                wsResumo.Cell(r, 1).Style.Font.SetBold().Font.SetFontSize(12);
                r++;
                foreach (var alert in s.ColdWaterDiagnosticAlerts)
                {
                    wsResumo.Cell(r, 1).Value = alert;
                    wsResumo.Range(r, 1, r, 5).Merge();
                    r++;
                }
            }

            wsResumo.Columns().AdjustToContents();

            // Abas dedicadas para Reservatórios, Boiler, Esgoto/Cx Gordura e Pluvial
            var wsCompl = wb.Worksheets.Add("Dimensionamentos Complementares");
            int rowC = 1;
            wsCompl.Cell(rowC++, 1).Value = "MEMÓRIA DE CÁLCULO — RESERVATÓRIOS, BOILER, ESGOTO, PLUVIAL E CAIXA DE GORDURA";
            wsCompl.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(13);
            rowC++;

            if (s.Reservoir != null)
            {
                wsCompl.Cell(rowC++, 1).Value = "1. RESERVAÇÃO DE ÁGUA POTÁVEL (ABNT NBR 5626)";
                wsCompl.Cell(rowC - 1, 1).Style.Font.SetBold();
                wsCompl.Cell(rowC, 1).Value = "População (hab.)"; wsCompl.Cell(rowC++, 2).Value = s.Reservoir.Population;
                wsCompl.Cell(rowC, 1).Value = "Consumo Diário (L/dia)"; wsCompl.Cell(rowC++, 2).Value = s.Reservoir.DailyConsumptionLiters;
                wsCompl.Cell(rowC, 1).Value = "Reserva Incêndio (L)"; wsCompl.Cell(rowC++, 2).Value = s.Reservoir.FireReserveLiters;
                wsCompl.Cell(rowC, 1).Value = "Volume Total Necessário (L)"; wsCompl.Cell(rowC++, 2).Value = s.Reservoir.TotalRequiredLiters;
                wsCompl.Cell(rowC, 1).Value = "Reservatório Superior Comercial (L)"; wsCompl.Cell(rowC++, 2).Value = s.Reservoir.CommercialUpperLiters;
                wsCompl.Cell(rowC, 1).Value = "Cisterna / Inferior Comercial (L)"; wsCompl.Cell(rowC++, 2).Value = s.Reservoir.CommercialLowerLiters;
                rowC++;
            }

            if (s.Boiler != null)
            {
                wsCompl.Cell(rowC++, 1).Value = "2. AQUECIMENTO E BOILER (ABNT NBR 7198)";
                wsCompl.Cell(rowC - 1, 1).Style.Font.SetBold();
                wsCompl.Cell(rowC, 1).Value = "Ocupantes Atendidos"; wsCompl.Cell(rowC++, 2).Value = s.Boiler.Occupants;
                wsCompl.Cell(rowC, 1).Value = "Consumo Água Quente (L/dia)"; wsCompl.Cell(rowC++, 2).Value = s.Boiler.DailyConsumptionLiters;
                wsCompl.Cell(rowC, 1).Value = "Boiler Comercial Adotado (L)"; wsCompl.Cell(rowC++, 2).Value = s.Boiler.CommercialBoilerLiters;
                wsCompl.Cell(rowC, 1).Value = "Potência Térmica Estimada (kW)"; wsCompl.Cell(rowC++, 2).Value = Math.Round(s.Boiler.HeatingPowerKw, 2);
                wsCompl.Cell(rowC, 1).Value = "Classificação de Pressão"; wsCompl.Cell(rowC++, 2).Value = s.Boiler.PressureClassification;
                rowC++;
            }

            if (s.Sewer != null)
            {
                wsCompl.Cell(rowC++, 1).Value = "3. ESGOTO SANITÁRIO (ABNT NBR 8160)";
                wsCompl.Cell(rowC - 1, 1).Style.Font.SetBold();
                wsCompl.Cell(rowC, 1).Value = "Contribuição Total (UHC)"; wsCompl.Cell(rowC++, 2).Value = s.Sewer.TotalUhc;
                wsCompl.Cell(rowC, 1).Value = "Diâmetro Mínimo Dimensionado (mm)"; wsCompl.Cell(rowC++, 2).Value = s.Sewer.RequiredDiameterMm;
                wsCompl.Cell(rowC, 1).Value = "Declividade Mínima (%)"; wsCompl.Cell(rowC++, 2).Value = s.Sewer.MinimumSlopePercent;
                rowC++;
            }

            if (s.Rainwater != null)
            {
                wsCompl.Cell(rowC++, 1).Value = "4. ÁGUAS PLUVIAIS E CALHAS (ABNT NBR 10844)";
                wsCompl.Cell(rowC - 1, 1).Style.Font.SetBold();
                wsCompl.Cell(rowC, 1).Value = "Região / Intensidade (mm/h)"; wsCompl.Cell(rowC++, 2).Value = $"{s.Rainwater.RegionName} ({s.Rainwater.IntensityMmPerHour:F0} mm/h)";
                wsCompl.Cell(rowC, 1).Value = "Área de Contribuição (m²)"; wsCompl.Cell(rowC++, 2).Value = Math.Round(s.Rainwater.ContributionAreaM2, 2);
                wsCompl.Cell(rowC, 1).Value = "Vazão de Projeto (L/min)"; wsCompl.Cell(rowC++, 2).Value = Math.Round(s.Rainwater.DesignFlowLitersPerMinute, 2);
                wsCompl.Cell(rowC, 1).Value = "Calha Selecionada"; wsCompl.Cell(rowC++, 2).Value = s.Rainwater.SelectedGutterDescription;
                wsCompl.Cell(rowC, 1).Value = "Taxa de Ocupação Calha (%)"; wsCompl.Cell(rowC++, 2).Value = Math.Round(s.Rainwater.GutterOccupancyPercentage, 1);
                wsCompl.Cell(rowC, 1).Value = "Condutores Verticais"; wsCompl.Cell(rowC++, 2).Value = $"{s.Rainwater.DownspoutCount}x DN {s.Rainwater.RequiredDownspoutDiameterMm} mm";
                rowC++;
            }

            if (s.GreaseTrap != null)
            {
                wsCompl.Cell(rowC++, 1).Value = "5. CAIXA DE GORDURA (ABNT NBR 8160 - ITEM 5.1.5)";
                wsCompl.Cell(rowC - 1, 1).Style.Font.SetBold();
                wsCompl.Cell(rowC, 1).Value = "Modelo Normativo"; wsCompl.Cell(rowC++, 2).Value = s.GreaseTrap.ModelName;
                wsCompl.Cell(rowC, 1).Value = "Volume Útil Mínimo (L)"; wsCompl.Cell(rowC++, 2).Value = s.GreaseTrap.MinimumVolumeLiters;
                wsCompl.Cell(rowC, 1).Value = "Dimensões / Diâmetro"; wsCompl.Cell(rowC++, 2).Value = s.GreaseTrap.DimensionsOrDiameter;
                wsCompl.Cell(rowC, 1).Value = "Parte Submersa Septo (cm)"; wsCompl.Cell(rowC++, 2).Value = s.GreaseTrap.SeptumSubmersionCm;
                wsCompl.Cell(rowC, 1).Value = "Diâmetro Saída (mm)"; wsCompl.Cell(rowC++, 2).Value = s.GreaseTrap.OutletDiameterMm;
            }

            wsCompl.Columns().AdjustToContents();
            wb.SaveAs(filePath);
        }

        private static string GetHtmlStyles() => @"
<style>
  body { font-family: 'Segoe UI', Arial, sans-serif; color: #1e293b; margin: 28px 36px; line-height: 1.5; font-size: 11pt; }
  .header-box { background: #0f172a; color: #ffffff; padding: 18px 22px; border-radius: 6px; margin-bottom: 18px; }
  .header-box h1 { margin: 0 0 4px 0; font-size: 17pt; }
  .header-box p { margin: 0; color: #cbd5e1; font-size: 10pt; }
  h2 { color: #1e3a8a; border-bottom: 2px solid #cbd5e1; padding-bottom: 4px; margin-top: 22px; font-size: 13pt; }
  h3 { color: #0f766e; margin-top: 14px; font-size: 11.5pt; }
  table { width: 100%; border-collapse: collapse; margin: 10px 0 16px 0; font-size: 9.5pt; }
  th { background: #1e3a8a; color: #ffffff; text-align: left; padding: 7px 8px; border: 1px solid #cbd5e1; }
  td { padding: 6px 8px; border: 1px solid #cbd5e1; }
  tr:nth-child(even) td { background: #f8fafc; }
  .formula-box { background: #f1f5f9; border-left: 4px solid #0284c7; padding: 10px 14px; margin: 8px 0; font-family: Consolas, monospace; font-size: 10pt; }
  .alert-box { background: #fffbeb; border-left: 4px solid #f59e0b; padding: 10px 14px; margin: 8px 0; }
  .ok-badge { color: #166534; font-weight: bold; }
  .warn-badge { color: #991b1b; font-weight: bold; }
  .sig-box { margin-top: 42px; text-align: center; }
  .sig-line { width: 320px; border-top: 1px solid #0f172a; margin: 0 auto 4px auto; }
  @media print { body { margin: 15mm; } }
</style>";

        private static string BuildProjectHeaderHtml(ProjectEngineeringSnapshot s, string docTitle)
        {
            return $@"
<div class='header-box'>
  <h1>{docTitle}</h1>
  <p>Bahr Hidro BIM — Engenharia Hidrossanitária &amp; Compatibilização Normativa ({s.AppliedNorms})</p>
</div>
<table>
  <tr><th>Empreendimento / Obra</th><td>{s.ProjectName}</td><th>Revisão / Data</th><td>{s.Revision} — {DateTime.Now:dd/MM/yyyy}</td></tr>
  <tr><th>Proprietário / Cliente</th><td>{s.ClientName}</td><th>Responsável Técnico</th><td>{s.EngineerName}</td></tr>
  <tr><th>Endereço da Obra</th><td>{s.Address}</td><th>CREA / ART</th><td>{s.CreaNumber} | ART: {s.ArtNumber}</td></tr>
</table>";
        }

        private static string BuildSignatureHtml(ProjectEngineeringSnapshot s) => $@"
<div class='sig-box'>
  <div class='sig-line'></div>
  <strong>{s.EngineerName}</strong><br/>
  Engenheiro(a) Responsável Técnico — {s.CreaNumber}<br/>
  ART / RRT nº {s.ArtNumber}
</div>";

        private static string BuildDescriptiveHtml(ProjectEngineeringSnapshot s)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang='pt-BR'><head><meta charset='utf-8'/>");
            sb.Append(GetHtmlStyles());
            sb.Append("</head><body>");
            sb.Append(BuildProjectHeaderHtml(s, "MEMORIAL DESCRITIVO EXECUTIVO — INSTALAÇÕES HIDROSSANITÁRIAS"));

            sb.Append("<h2>1. OBJETIVO E NORMAS TÉCNICAS APLICÁVEIS</h2>");
            sb.Append("<p>O presente Memorial Descritivo estabelece as diretrizes técnicas, especificações de materiais, critérios executivos e procedimentos de ensaio para as instalações prediais hidrossanitárias e de drenagem pluvial do empreendimento identificado acima, em estrita conformidade com as normas da ABNT:</p>");
            sb.Append("<ul>");
            sb.Append("<li><strong>ABNT NBR 5626:2020</strong> — Sistemas prediais de água fria e água quente — Projeto, execução, operação e manutenção;</li>");
            sb.Append("<li><strong>ABNT NBR 7198:1993</strong> — Projeto e execução de instalações prediais de água quente;</li>");
            sb.Append("<li><strong>ABNT NBR 8160:1999</strong> — Sistemas prediais de esgoto sanitário — Projeto e execução;</li>");
            sb.Append("<li><strong>ABNT NBR 10844:1989</strong> — Instalações prediais de águas pluviais;</li>");
            sb.Append("<li><strong>ABNT NBR 16970:2022</strong> — Light Steel Framing — Diretrizes passivas de compatibilização de passagens e furação em perfis metálicos.</li>");
            sb.Append("</ul>");

            sb.Append("<h2>2. SISTEMA DE RESERVAÇÃO E ÁGUA FRIA (NBR 5626)</h2>");
            if (s.Reservoir != null)
            {
                sb.Append($"<p>Para a tipologia <strong>{s.Reservoir.BuildingCategory}</strong> com população estimada de <strong>{s.Reservoir.Population} habitantes</strong> e autonomia de <strong>{s.Reservoir.ReserveDays} dia(s)</strong>, o consumo diário calculado é de <strong>{s.Reservoir.DailyConsumptionLiters:F0} L/dia</strong>, resultando no volume total de <strong>{s.Reservoir.TotalRequiredLiters:F0} L</strong> (Reservatório Superior Comercial: <strong>{s.Reservoir.CommercialUpperLiters} L</strong>; Reservatório Inferior / Cisterna: <strong>{s.Reservoir.CommercialLowerLiters} L</strong>).</p>");
            }
            sb.Append("<p><strong>Especificação dos Materiais:</strong> Tubulações e conexões de água fria em PVC rígido soldável (marrom), classe de pressão PN 750 kPa (75 m.c.a.), conforme NBR 5648, dotadas de registros de gaveta para manutenção setorial e registros de pressão nos pontos de chuveiro.</p>");

            sb.Append("<h2>3. SISTEMA DE ÁGUA QUENTE E AQUECIMENTO (NBR 7198)</h2>");
            if (s.Boiler != null)
            {
                sb.Append($"<p>O sistema de acumulação térmica foi dimensionado para <strong>{s.Boiler.Occupants} ocupantes</strong>, com demanda diária de <strong>{s.Boiler.DailyConsumptionLiters:F0} L/dia</strong> a 60 °C, adotando-se <strong>Boiler Comercial de {s.Boiler.CommercialBoilerLiters} Litros</strong> ({s.Boiler.PressureClassification}) e potência térmica de <strong>{s.Boiler.HeatingPowerKw:F2} kW</strong>.</p>");
            }
            sb.Append("<p><strong>Especificação dos Materiais:</strong> Tubulações e conexões de água quente em CPVC (Aquatherm) ou PPR PN 20/25 com isolamento térmico elastomérico e previsão de liras/juntas de dilatação térmica nos trechos retilíneos extensos.</p>");

            sb.Append("<h2>4. SISTEMA DE ESGOTO SANITÁRIO, VENTILAÇÃO E CAIXA DE GORDURA (NBR 8160)</h2>");
            sb.Append("<p>As redes internas de esgoto primário e secundário serão executadas em PVC rígido série normal (ramais internos) e série reforçada (tubos de queda e subcoletores enterrados), com declividade mínima de <strong>2,0%</strong> para diâmetros até DN 75 e <strong>1,0%</strong> para diâmetros iguais ou superiores a DN 100. Todos os aparelhos sanitários são protegidos por desconectores (fecho hídrico mínimo de 50 mm) e sistema de ventilação primária e secundária.</p>");
            if (s.GreaseTrap != null)
            {
                sb.Append($"<p><strong>Caixa de Gordura:</strong> Na saída da(s) {s.GreaseTrap.KitchenCount} cozinha(s), antes da ligação à rede coletora, deverá ser instalada <strong>{s.GreaseTrap.ModelName}</strong> com volume útil mínimo de <strong>{s.GreaseTrap.MinimumVolumeLiters:F0} L</strong> ({s.GreaseTrap.DimensionsOrDiameter}), septo submerso de <strong>{s.GreaseTrap.SeptumSubmersionCm} cm</strong> e tubulação de saída <strong>DN {s.GreaseTrap.OutletDiameterMm} mm</strong>.</p>");
            }

            sb.Append("<h2>5. SISTEMA DE DRENAGEM DE ÁGUAS PLUVIAIS (NBR 10844)</h2>");
            if (s.Rainwater != null)
            {
                sb.Append($"<p>Dimensionado para o índice pluviométrico de <strong>{s.Rainwater.IntensityMmPerHour:F0} mm/h</strong> ({s.Rainwater.RegionName}), área de contribuição de <strong>{s.Rainwater.ContributionAreaM2:F2} m²</strong> e vazão de projeto de <strong>{s.Rainwater.DesignFlowLitersPerMinute:F1} L/min</strong>, utilizando calha <strong>{s.Rainwater.SelectedGutterDescription}</strong> (declividade mínima 0,5%) e <strong>{s.Rainwater.DownspoutCount} condutor(es) vertical(is) DN {s.Rainwater.RequiredDownspoutDiameterMm} mm</strong>.</p>");
            }

            sb.Append("<h2>6. DIRETRIZES DE COMPATIBILIZAÇÃO COM LIGHT STEEL FRAMING (LSF)</h2>");
            sb.Append("<ul>");
            sb.Append("<li>Toda travessia horizontal de tubulações pela alma de montantes metálicos deve respeitar o limite máximo normativo de abertura e utilizar <strong>passa-tubos plásticos ou elastoméricos</strong> para isolamento acústico e prevenção de corrosão galvânica;</li>");
            sb.Append("<li>É vedada a abertura de furos não previstos em projeto a menos de 25 cm das guias superior e inferior ou sobre emendas estruturais;</li>");
            sb.Append("<li>Tubulações de água quente devem possuir folga radial no passa-tubo para livre movimentação térmica.</li>");
            sb.Append("</ul>");

            sb.Append("<h2>7. ENSAIOS DE RECEBIMENTO E COMISSIONAMENTO</h2>");
            sb.Append("<p>Antes do fechamento das paredes/shafts, todas as tubulações de água fria e quente deverão ser submetidas ao <strong>ensaio de estanqueidade hidrostática</strong> (1,5 vezes a pressão máxima de serviço, mínimo 100 kPa por 1 hora sem queda manométrica). As redes de esgoto e pluvial serão testadas por lâmina d'água e ensaio de fumaça conforme ABNT NBR 8160.</p>");

            sb.Append(BuildSignatureHtml(s));
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string BuildAnalyticalCalculationHtml(ProjectEngineeringSnapshot s)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang='pt-BR'><head><meta charset='utf-8'/>");
            sb.Append(GetHtmlStyles());
            sb.Append("</head><body>");
            sb.Append(BuildProjectHeaderHtml(s, "MEMORIAL DE CÁLCULO ANALÍTICO — PASSO A PASSO NORMATIVO"));

            sb.Append("<h2>1. DIMENSIONAMENTO DOS RESERVATÓRIOS (ABNT NBR 5626)</h2>");
            sb.Append("<div class='formula-box'>Cd = P × q &nbsp;|&nbsp; Vtotal = (Cd × d) + V_incêndio</div>");
            if (s.Reservoir != null)
            {
                sb.Append("<table><tr><th>Parâmetro</th><th>Valor Calculado</th><th>Observação Normativa</th></tr>");
                sb.Append($"<tr><td>Categoria / População (P)</td><td>{s.Reservoir.BuildingCategory} — {s.Reservoir.Population} hab.</td><td>Estimativa populacional de projeto</td></tr>");
                sb.Append($"<tr><td>Consumo Diário Total (Cd)</td><td>{s.Reservoir.DailyConsumptionLiters:F0} L/dia</td><td>Autonomia de {s.Reservoir.ReserveDays} dia(s)</td></tr>");
                sb.Append($"<tr><td>Reserva Técnica de Incêndio (RTI)</td><td>{s.Reservoir.FireReserveLiters:F0} L</td><td>Conforme exigência CBM / projeto</td></tr>");
                sb.Append($"<tr><td>Volume Total / Superior / Cisterna</td><td>{s.Reservoir.TotalRequiredLiters:F0} L / Sup: {s.Reservoir.CommercialUpperLiters} L / Inf: {s.Reservoir.CommercialLowerLiters} L</td><td>Capacidades comerciais adotadas</td></tr>");
                sb.Append("</table>");
            }

            sb.Append("<h2>2. REDE DE ÁGUA FRIA E PRESSURIZAÇÃO (ABNT NBR 5626)</h2>");
            sb.Append("<div class='formula-box'>Q = 0,30 × √(ΣP) [L/s] &nbsp;|&nbsp; V = (4 × Q) / (π × D²) ≤ 3,0 m/s &nbsp;|&nbsp; ΔH = f × (Leq / D) × (V² / 2g)</div>");
            if (s.ColdWaterNetwork != null)
            {
                sb.Append("<table><tr><th>Trecho</th><th>Ligação</th><th>Vazão (L/s)</th><th>DN (mm)</th><th>Veloc. (m/s)</th><th>Perda Total (m.c.a)</th></tr>");
                foreach (var seg in s.ColdWaterNetwork.Segments)
                {
                    string velClass = seg.VelocityMetersPerSecond > 3.0 ? "warn-badge" : "ok-badge";
                    sb.Append($"<tr><td>{seg.SegmentName}</td><td>{seg.Connection}</td><td>{seg.FlowLitersPerSecond:F3}</td><td><strong>{seg.SelectedDiameterMm:F0}</strong></td><td class='{velClass}'>{seg.VelocityMetersPerSecond:F2}</td><td>{seg.TotalHeadLossMeters:F3}</td></tr>");
                }
                sb.Append("</table>");

                sb.Append("<h3>Verificação de Pressões Disponíveis nos Nós</h3>");
                sb.Append("<table><tr><th>Nó / Ponto</th><th>Cota (m)</th><th>Cota Piezométrica (m)</th><th>Pressão Disponível (m.c.a)</th><th>Status</th></tr>");
                foreach (var n in s.ColdWaterNetwork.Nodes)
                {
                    bool low = n.NodeType != HydraulicNodeType.Reservoir && n.AvailablePressureMca < 1.0;
                    sb.Append($"<tr><td>{n.NodeName}</td><td>{n.ElevationMeters:F2}</td><td>{n.PiezometricHeadMeters:F3}</td><td><strong>{n.AvailablePressureMca:F2}</strong></td><td class='{(low ? "warn-badge" : "ok-badge")}'>{(low ? "PRESSÃO BAIXA (< 1,0 m.c.a)" : "OK")}</td></tr>");
                }
                sb.Append("</table>");
            }

            sb.Append("<h2>3. ÁGUA QUENTE E BOILER (ABNT NBR 7198)</h2>");
            sb.Append("<div class='formula-box'>V_boiler = N_ocupantes × q_aq &nbsp;|&nbsp; P(kW) = (V × ΔT) / (860 × t_recup)</div>");
            if (s.Boiler != null)
            {
                sb.Append($"<p>Ocupantes: <strong>{s.Boiler.Occupants}</strong> | Consumo diário: <strong>{s.Boiler.DailyConsumptionLiters:F0} L/dia</strong> | Boiler comercial: <strong>{s.Boiler.CommercialBoilerLiters} L</strong> | Potência: <strong>{s.Boiler.HeatingPowerKw:F2} kW</strong> | Regime: <strong>{s.Boiler.PressureClassification}</strong>.</p>");
            }

            sb.Append("<h2>4. ESGOTO SANITÁRIO E CAIXA DE GORDURA (ABNT NBR 8160)</h2>");
            if (s.Sewer != null)
            {
                sb.Append($"<p>Total acumulado: <strong>{s.Sewer.TotalUhc} UHC</strong> → Diâmetro mínimo: <strong>DN {s.Sewer.RequiredDiameterMm} mm</strong> | Declividade mínima: <strong>{s.Sewer.MinimumSlopePercent:F1}%</strong>.</p>");
            }
            if (s.GreaseTrap != null)
            {
                sb.Append($"<p>Caixa de Gordura: <strong>{s.GreaseTrap.ModelName}</strong> | Volume útil: <strong>{s.GreaseTrap.MinimumVolumeLiters:F0} L</strong> | Dimensões: <strong>{s.GreaseTrap.DimensionsOrDiameter}</strong> | Septo submerso: <strong>{s.GreaseTrap.SeptumSubmersionCm} cm</strong> | Saída: <strong>DN {s.GreaseTrap.OutletDiameterMm} mm</strong>.</p>");
            }

            sb.Append("<h2>5. CALHAS E CONDUTORES PLUVIAIS (ABNT NBR 10844)</h2>");
            sb.Append("<div class='formula-box'>Q = (I × A) / 60 [L/min] &nbsp;|&nbsp; V = (1/n) × Rh^(2/3) × i^(1/2) [Manning-Strickler]</div>");
            if (s.Rainwater != null)
            {
                sb.Append($"<p>Região: <strong>{s.Rainwater.RegionName}</strong> (I = {s.Rainwater.IntensityMmPerHour:F0} mm/h) | Área de contribuição: <strong>{s.Rainwater.ContributionAreaM2:F2} m²</strong> | Vazão: <strong>{s.Rainwater.DesignFlowLitersPerMinute:F1} L/min</strong> | Calha: <strong>{s.Rainwater.SelectedGutterDescription}</strong> (Ocupação: {s.Rainwater.GutterOccupancyPercentage:F1}%) | Condutores: <strong>{s.Rainwater.DownspoutCount}× DN {s.Rainwater.RequiredDownspoutDiameterMm} mm</strong>.</p>");
            }

            if (s.ColdWaterDiagnosticAlerts.Count > 0)
            {
                sb.Append("<h2>6. PARECER TÉCNICO E AUDITORIA NORMATIVA</h2><div class='alert-box'><ul>");
                foreach (var a in s.ColdWaterDiagnosticAlerts)
                    sb.Append($"<li>{a}</li>");
                sb.Append("</ul></div>");
            }

            sb.Append(BuildSignatureHtml(s));
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string BuildUnifiedHtml(ProjectEngineeringSnapshot s, BimMaterialTakeoff? takeoff)
        {
            string desc = BuildDescriptiveHtml(s);
            string calc = BuildAnalyticalCalculationHtml(s);
            int bodyStart = calc.IndexOf("<body>", StringComparison.OrdinalIgnoreCase) + 6;
            int sigStart = calc.IndexOf("<div class='sig-box'>", StringComparison.OrdinalIgnoreCase);
            string calcInner = (bodyStart > 5 && sigStart > bodyStart) ? calc.Substring(bodyStart, sigStart - bodyStart) : "";

            var bomHtml = new StringBuilder();
            if (takeoff != null && (takeoff.Pipes.Count > 0 || takeoff.Fittings.Count > 0))
            {
                bomHtml.Append("<h2>PARTE III — RESUMO QUANTITATIVO DE MATERIAIS (BOM BIM 3D)</h2>");
                bomHtml.Append("<table><tr><th>Sistema</th><th>Material / Tipo</th><th>DN (mm)</th><th>Compr. Líquido (m)</th><th>Compr. c/ Perda (m)</th><th>Varas 6m</th></tr>");
                foreach (var p in takeoff.Pipes)
                    bomHtml.Append($"<tr><td>{p.SystemName}</td><td>{p.PipeTypeName}</td><td>{p.DiameterMm}</td><td>{p.NetLengthMeters:F2}</td><td>{p.TotalLengthWithWasteMeters:F2}</td><td>{p.CommercialBars6m}</td></tr>");
                bomHtml.Append("</table>");
                var budget = SinapiCommercialMapper.BuildBudgetLines(takeoff);
                if (budget.Count > 0)
                {
                    decimal totalBrl = budget.Sum(b => b.TotalPriceBrl);
                    bomHtml.Append("<h2>PARTE IV — ORÇAMENTO ESTIMATIVO (CATÁLOGO TIGRE/AMANCO &amp; SINAPI)</h2>");
                    bomHtml.Append("<table><tr><th>Item</th><th>Descrição</th><th>Ref. Comercial</th><th>SINAPI</th><th>Qtd.</th><th>Unit. (R$)</th><th>Total (R$)</th></tr>");
                    foreach (var b in budget)
                        bomHtml.Append($"<tr><td>{b.ItemNumber}</td><td>{b.Description} ({b.DiameterDisplay})</td><td>{b.CommercialRef}</td><td>{b.SinapiCode}</td><td>{b.Quantity:F2} {b.Unit}</td><td>R$ {b.UnitPriceBrl:N2}</td><td>R$ {b.TotalPriceBrl:N2}</td></tr>");
                    bomHtml.Append($"<tr><th colspan='6' style='text-align:right'>TOTAL ESTIMADO DE MATERIAIS</th><th>R$ {totalBrl:N2}</th></tr></table>");
                }
            }

            int descSig = desc.IndexOf("<div class='sig-box'>", StringComparison.OrdinalIgnoreCase);
            if (descSig > 0)
            {
                return desc.Substring(0, descSig)
                    + "<hr style='margin: 28px 0; border: 1px solid #cbd5e1;'/>"
                    + "<h1>PARTE II — MEMÓRIA DE CÁLCULO ANALÍTICA E TABELAS DE VERIFICAÇÃO</h1>"
                    + calcInner
                    + bomHtml
                    + desc.Substring(descSig);
            }
            return desc;
        }
    }
}
