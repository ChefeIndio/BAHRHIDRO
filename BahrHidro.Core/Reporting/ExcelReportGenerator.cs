using System;
using ClosedXML.Excel;
using BahrHidro.Core.Hydraulic.Network;

namespace BahrHidro.Core.Reporting
{
    public static class ExcelReportGenerator
    {
        public static void GenerateNetworkReport(NetworkCalculationResult result, string filePath)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("Caminho do ficheiro inválido.");

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Memorial Hidráulico");

            ws.Cell("A1").Value = "BAHR HIDRO - MEMORIAL DE CÁLCULO DE REDE";
            var titleRange = ws.Range("A1:H1");
            titleRange.Merge();
            titleRange.Style.Font.SetBold().Font.FontSize = 14;
            titleRange.Style.Font.SetFontColor(XLColor.White);
            titleRange.Style.Fill.SetBackgroundColor(XLColor.MidnightBlue);
            titleRange.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A2").Value = $"Gerado automaticamente em: {DateTime.Now:dd/MM/yyyy HH:mm}";
            ws.Cell("A2").Style.Font.SetItalic();

            int row = 4;
            ws.Cell(row, 1).Value = "1. DIMENSIONAMENTO DOS TRECHOS DE TUBULAÇÃO";
            ws.Cell(row, 1).Style.Font.SetBold().Font.FontSize = 12;
            row++;

            // NOVA COLUNA ADICIONADA AQUI: "D. Escolhido (mm)"
            string[] headersTrechos = { "Trecho", "Ligação", "Vazão (L/s)", "D. Escolhido (mm)", "Velocidade (m/s)", "Reynolds", "Fator Atrito", "Perda Total (m.c.a)" };
            for (int i = 0; i < headersTrechos.Length; i++)
            {
                var cell = ws.Cell(row, i + 1);
                cell.Value = headersTrechos[i];
                cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White);
                cell.Style.Fill.SetBackgroundColor(XLColor.SteelBlue);
            }
            row++;

            foreach (var seg in result.Segments)
            {
                ws.Cell(row, 1).Value = seg.SegmentName;
                ws.Cell(row, 2).Value = seg.Connection;
                ws.Cell(row, 3).Value = Math.Round(seg.FlowLitersPerSecond, 3);
                
                // Exibe o diâmetro escolhido
                if (seg.SelectedDiameterMm > 0)
                {
                    ws.Cell(row, 4).Value = Math.Round(seg.SelectedDiameterMm, 2);
                    ws.Cell(row, 4).Style.Font.SetBold().Font.SetFontColor(XLColor.Blue);
                }
                else ws.Cell(row, 4).Value = "--";

                ws.Cell(row, 5).Value = Math.Round(seg.VelocityMetersPerSecond, 3);
                ws.Cell(row, 6).Value = Math.Round(seg.ReynoldsNumber, 0);
                ws.Cell(row, 7).Value = Math.Round(seg.FrictionFactor, 5);
                ws.Cell(row, 8).Value = Math.Round(seg.TotalHeadLossMeters, 4);
                row++;
            }

            row += 2;
            ws.Cell(row, 1).Value = "2. VERIFICAÇÃO DE PRESSÕES NOS NÓS";
            ws.Cell(row, 1).Style.Font.SetBold().Font.FontSize = 12;
            row++;

            string[] headersNos = { "Nó / Ponto", "Tipo", "Cota (m)", "Cota Piezométrica", "Pressão Disponível (m.c.a)", "Status" };
            for (int i = 0; i < headersNos.Length; i++)
            {
                var cell = ws.Cell(row, i + 1);
                cell.Value = headersNos[i];
                cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White);
                cell.Style.Fill.SetBackgroundColor(XLColor.SteelBlue);
            }
            row++;

            foreach (var node in result.Nodes)
            {
                ws.Cell(row, 1).Value = node.NodeName;
                string tipo = node.NodeType == HydraulicNodeType.Reservoir ? "Reservatório" : node.NodeType == HydraulicNodeType.Junction ? "Nó" : "Consumo";
                ws.Cell(row, 2).Value = tipo;
                ws.Cell(row, 3).Value = Math.Round(node.ElevationMeters, 2);
                ws.Cell(row, 4).Value = Math.Round(node.PiezometricHeadMeters, 3);
                
                var pressaoCell = ws.Cell(row, 5);
                pressaoCell.Value = Math.Round(node.AvailablePressureMca, 3);
                
                var statusCell = ws.Cell(row, 6);
                if (node.NodeType != HydraulicNodeType.Reservoir && node.AvailablePressureMca < 1.0)
                {
                    statusCell.Value = "PRESSÃO BAIXA";
                    statusCell.Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                    pressaoCell.Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                }
                else if (node.NodeType == HydraulicNodeType.Reservoir) { statusCell.Value = "ORIGEM"; statusCell.Style.Font.SetFontColor(XLColor.Gray); }
                else { statusCell.Value = "OK"; statusCell.Style.Font.SetBold().Font.SetFontColor(XLColor.ForestGreen); }
                row++;
            }

            ws.Columns(1, 8).AdjustToContents();
            workbook.SaveAs(filePath);
        }
    
        public static void GenerateProjectReportWithBom(
            NetworkCalculationResult? networkResult,
            BimMaterialTakeoff? takeoff,
            string filePath)
        {
            XLWorkbook workbook;
            if (networkResult != null)
            {
                GenerateNetworkReport(networkResult, filePath);
                workbook = new XLWorkbook(filePath);
            }
            else
            {
                workbook = new XLWorkbook();
            }

            using (workbook)
            {
                if (takeoff != null)
                {
                    // 1. Aba de Tubulações (BOM)
                    var wsTubos = workbook.Worksheets.Add("Lista de Tubos (BOM 3D)");
                    wsTubos.Cell(1, 1).Value = "LISTA EXECUTIVA DE TUBULAÇÕES — QUANTITATIVO BIM 3D";
                    wsTubos.Cell(1, 1).Style.Font.Bold = true;
                    wsTubos.Cell(1, 1).Style.Font.FontSize = 14;

                    wsTubos.Cell(2, 1).Value = $"Obra: {takeoff.ProjectName} | Margem de Perda Aplicada: {takeoff.WasteMarginPercent:F1}%";
                    wsTubos.Cell(2, 1).Style.Font.Italic = true;

                    string[] headersTubos =
                    {
                        "Sistema",
                        "Tipo de Tubo / Material",
                        "Diâmetro (mm)",
                        "Comprimento Líquido (m)",
                        "Perda (%)",
                        "Comprimento c/ Perda (m)",
                        "Varas Comerciais (6,0 m)"
                    };

                    for (int c = 0; c < headersTubos.Length; c++)
                    {
                        var cell = wsTubos.Cell(4, c + 1);
                        cell.Value = headersTubos[c];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                        cell.Style.Font.FontColor = XLColor.White;
                    }

                    int row = 5;
                    foreach (var p in takeoff.Pipes)
                    {
                        wsTubos.Cell(row, 1).Value = p.SystemName;
                        wsTubos.Cell(row, 2).Value = p.PipeTypeName;
                        wsTubos.Cell(row, 3).Value = p.DiameterMm;
                        wsTubos.Cell(row, 4).Value = Math.Round(p.NetLengthMeters, 2);
                        wsTubos.Cell(row, 5).Value = Math.Round(p.WastePercentage, 1);
                        wsTubos.Cell(row, 6).Value = Math.Round(p.TotalLengthWithWasteMeters, 2);
                        wsTubos.Cell(row, 7).Value = p.CommercialBars6m;
                        row++;
                    }

                    if (takeoff.Pipes.Count > 0)
                    {
                        wsTubos.Cell(row, 1).Value = "TOTAL GERAL";
                        wsTubos.Cell(row, 1).Style.Font.Bold = true;
                        wsTubos.Cell(row, 4).FormulaA1 = $"SUM(D5:D{row - 1})";
                        wsTubos.Cell(row, 4).Style.Font.Bold = true;
                        wsTubos.Cell(row, 6).FormulaA1 = $"SUM(F5:F{row - 1})";
                        wsTubos.Cell(row, 6).Style.Font.Bold = true;
                        wsTubos.Cell(row, 7).FormulaA1 = $"SUM(G5:G{row - 1})";
                        wsTubos.Cell(row, 7).Style.Font.Bold = true;
                    }

                    wsTubos.Columns().AdjustToContents();

                    // 2. Aba de Conexões e Registros (BOM)
                    var wsConexoes = workbook.Worksheets.Add("Conexoes e Registros (3D)");
                    wsConexoes.Cell(1, 1).Value = "LISTA DE CONEXÕES, REGISTROS E ACESSÓRIOS — QUANTITATIVO BIM 3D";
                    wsConexoes.Cell(1, 1).Style.Font.Bold = true;
                    wsConexoes.Cell(1, 1).Style.Font.FontSize = 14;

                    string[] headersConex =
                    {
                        "Sistema",
                        "Categoria",
                        "Família / Especificação Comercial",
                        "Bitola / Dimensão",
                        "Quantidade (un)"
                    };

                    for (int c = 0; c < headersConex.Length; c++)
                    {
                        var cell = wsConexoes.Cell(3, c + 1);
                        cell.Value = headersConex[c];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F766E");
                        cell.Style.Font.FontColor = XLColor.White;
                    }

                    int rowC = 4;
                    foreach (var f in takeoff.Fittings)
                    {
                        wsConexoes.Cell(rowC, 1).Value = f.SystemName;
                        wsConexoes.Cell(rowC, 2).Value = f.CategoryName;
                        wsConexoes.Cell(rowC, 3).Value = f.FamilyAndType;
                        wsConexoes.Cell(rowC, 4).Value = f.SizeDescription;
                        wsConexoes.Cell(rowC, 5).Value = f.Quantity;
                        rowC++;
                    }

                    if (takeoff.Fittings.Count > 0)
                    {
                        wsConexoes.Cell(rowC, 1).Value = "TOTAL DE PEÇAS";
                        wsConexoes.Cell(rowC, 1).Style.Font.Bold = true;
                        wsConexoes.Cell(rowC, 5).FormulaA1 = $"SUM(E4:E{rowC - 1})";
                        wsConexoes.Cell(rowC, 5).Style.Font.Bold = true;
                    }

                    wsConexoes.Columns().AdjustToContents();

                    // 3. Aba de Kits Hidráulicos por Ambiente (Pré-Montagem Steel Frame)
                    var wsKits = workbook.Worksheets.Add("Kits por Ambiente (LSF)");
                    wsKits.Cell(1, 1).Value = "SEPARAÇÃO DE MATERIAIS POR KIT / AMBIENTE (PRÉ-MONTAGEM STEEL FRAME)";
                    wsKits.Cell(1, 1).Style.Font.Bold = true;
                    wsKits.Cell(1, 1).Style.Font.FontSize = 14;

                    string[] headersKits =
                    {
                        "Kit / Ambiente",
                        "Sistema",
                        "Tipo",
                        "Material / Peça",
                        "Bitola",
                        "Quantidade",
                        "Unidade"
                    };

                    for (int c = 0; c < headersKits.Length; c++)
                    {
                        var cell = wsKits.Cell(3, c + 1);
                        cell.Value = headersKits[c];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#166534");
                        cell.Style.Font.FontColor = XLColor.White;
                    }

                    int rowK = 4;
                    foreach (var k in takeoff.Kits)
                    {
                        wsKits.Cell(rowK, 1).Value = k.KitName;
                        wsKits.Cell(rowK, 2).Value = k.SystemName;
                        wsKits.Cell(rowK, 3).Value = k.ItemCategory;
                        wsKits.Cell(rowK, 4).Value = k.Description;
                        wsKits.Cell(rowK, 5).Value = k.SizeDescription;
                        wsKits.Cell(rowK, 6).Value = Math.Round(k.Quantity, 2);
                        wsKits.Cell(rowK, 7).Value = k.Unit;
                        rowK++;
                    }

                    wsKits.Columns().AdjustToContents();
                }

                    // 4. Aba de Furação Controlada em Montantes LSF (Fábrica)
                    var wsFuros = workbook.Worksheets.Add("Furacao Montantes (LSF)");
                    wsFuros.Cell(1, 1).Value = "CONTROLE DE FURAÇÃO EM MONTANTES STEEL FRAME — PRÉ-MONTAGEM INDUSTRIALIZADA";
                    wsFuros.Cell(1, 1).Style.Font.Bold = true;
                    wsFuros.Cell(1, 1).Style.Font.FontSize = 14;

                    string[] headersFuros =
                    {
                        "Furo ID",
                        "Parede / Kit",
                        "Perfil Montante LSF",
                        "Sistema",
                        "Tubo DN (mm)",
                        "Furo c/ Bucha (mm)",
                        "Alma Perfil (mm)",
                        "Ocupação (%)",
                        "Cota X Canto (m)",
                        "Altura H Piso (m)",
                        "Status Estrutural"
                    };

                    for (int c = 0; c < headersFuros.Length; c++)
                    {
                        var cell = wsFuros.Cell(3, c + 1);
                        cell.Value = headersFuros[c];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#312E81");
                        cell.Style.Font.FontColor = XLColor.White;
                    }

                    int rowF = 4;
                    if (takeoff.LsfPunchingHoles.Count == 0)
                    {
                        wsFuros.Cell(rowF, 1).Value = "Nenhuma perfuração horizontal detectada nos montantes metálicos (traçado livre).";
                        wsFuros.Cell(rowF, 1).Style.Font.Italic = true;
                    }
                    else
                    {
                        foreach (var h in takeoff.LsfPunchingHoles)
                        {
                            wsFuros.Cell(rowF, 1).Value = h.HoleId;
                            wsFuros.Cell(rowF, 2).Value = h.WallOrKitName;
                            wsFuros.Cell(rowF, 3).Value = h.StudName;
                            wsFuros.Cell(rowF, 4).Value = h.SystemName;
                            wsFuros.Cell(rowF, 5).Value = h.PipeDiameterMm;
                            wsFuros.Cell(rowF, 6).Value = h.RequiredHoleMm;
                            wsFuros.Cell(rowF, 7).Value = h.StudWebWidthMm;
                            wsFuros.Cell(rowF, 8).Value = h.OccupancyPercent;
                            wsFuros.Cell(rowF, 9).Value = h.PositionXFromWallStartMeters;
                            wsFuros.Cell(rowF, 10).Value = h.HeightHFromFloorMeters;
                            wsFuros.Cell(rowF, 11).Value = h.Status;

                            bool ok = h.Status.StartsWith("APROVADO", StringComparison.OrdinalIgnoreCase);
                            wsFuros.Cell(rowF, 11).Style.Font.Bold = true;
                            wsFuros.Cell(rowF, 11).Style.Fill.BackgroundColor = ok
                                ? XLColor.FromHtml("#DCFCE7")
                                : XLColor.FromHtml("#FEE2E2");
                            wsFuros.Cell(rowF, 11).Style.Font.FontColor = ok
                                ? XLColor.FromHtml("#166534")
                                : XLColor.FromHtml("#991B1B");

                            rowF++;
                        }
                    }

                    wsFuros.Columns().AdjustToContents();

                if (takeoff != null)
                {
                    var budgetLines = SinapiCommercialMapper.BuildBudgetLines(takeoff);
                    if (budgetLines.Count > 0)
                    {
                        var wsSinapi = workbook.Worksheets.Add("Orçamento SINAPI & Fabricante");
                        wsSinapi.Cell(1, 1).Value = "ORÇAMENTO ESTIMATIVO DE MATERIAIS — CATÁLOGO TIGRE/AMANCO & SINAPI";
                        wsSinapi.Range("A1:J1").Merge().Style.Font.SetBold().Font.SetFontSize(13)
                            .Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml("#0F172A"));

                        string[] hdrs = { "Item", "Sistema", "Descrição Técnica", "Bitola", "Ref. Comercial (Tigre/Amanco)", "Cód. SINAPI", "Unid.", "Qtd. c/ Perda", "Unit. Est. (R$)", "Subtotal (R$)" };
                        for (int c = 0; c < hdrs.Length; c++)
                        {
                            wsSinapi.Cell(3, c + 1).Value = hdrs[c];
                            wsSinapi.Cell(3, c + 1).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E2E8F0"));
                        }

                        int row = 4;
                        decimal grandTotal = 0m;
                        foreach (var b in budgetLines)
                        {
                            wsSinapi.Cell(row, 1).Value = b.ItemNumber;
                            wsSinapi.Cell(row, 2).Value = b.SystemName;
                            wsSinapi.Cell(row, 3).Value = b.Description;
                            wsSinapi.Cell(row, 4).Value = b.DiameterDisplay;
                            wsSinapi.Cell(row, 5).Value = b.CommercialRef;
                            wsSinapi.Cell(row, 6).Value = b.SinapiCode;
                            wsSinapi.Cell(row, 7).Value = b.Unit;
                            wsSinapi.Cell(row, 8).Value = b.Quantity;
                            wsSinapi.Cell(row, 9).Value = (double)b.UnitPriceBrl;
                            wsSinapi.Cell(row, 10).Value = (double)b.TotalPriceBrl;
                            wsSinapi.Cell(row, 9).Style.NumberFormat.Format = "R$ #,##0.00";
                            wsSinapi.Cell(row, 10).Style.NumberFormat.Format = "R$ #,##0.00";
                            grandTotal += b.TotalPriceBrl;
                            row++;
                        }

                        wsSinapi.Cell(row, 1).Value = "TOTAL ESTIMADO DE MATERIAIS HIDROSSANITÁRIOS (REFERÊNCIA SINAPI)";
                        wsSinapi.Range(row, 1, row, 9).Merge().Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#F1F5F9"));
                        wsSinapi.Cell(row, 10).Value = (double)grandTotal;
                        wsSinapi.Cell(row, 10).Style.Font.SetBold().NumberFormat.Format = "R$ #,##0.00";
                        wsSinapi.Cell(row, 10).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#DCFCE7"));
                        wsSinapi.Columns().AdjustToContents();
                    }
                }
                workbook.SaveAs(filePath);
            }
        }

}
}