using System;
using ClosedXML.Excel;
using BahrHidro.Core.Hydraulic.Network;

namespace BahrHidro.Core.Reporting
{
    public static class ExcelReportGenerator
    {
        public static void GenerateNetworkReport(NetworkCalculationResult result, string filePath)
        {
            if (result == null) 
                throw new ArgumentNullException(nameof(result));
                
            if (string.IsNullOrWhiteSpace(filePath)) 
                throw new ArgumentException("Caminho do ficheiro inválido.");

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Memorial Hidráulico");

            // ==========================================
            // 1. TÍTULO E CABEÇALHO DO RELATÓRIO
            // ==========================================
            ws.Cell("A1").Value = "BAHR HIDRO - MEMORIAL DE CÁLCULO DE REDE";
            var titleRange = ws.Range("A1:G1");
            titleRange.Merge();
            titleRange.Style.Font.SetBold().Font.FontSize = 14;
            titleRange.Style.Font.SetFontColor(XLColor.White);
            titleRange.Style.Fill.SetBackgroundColor(XLColor.MidnightBlue);
            titleRange.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

            ws.Cell("A2").Value = $"Gerado automaticamente em: {DateTime.Now:dd/MM/yyyy HH:mm}";
            ws.Cell("A2").Style.Font.SetItalic();

            // ==========================================
            // 2. TABELA DE DIMENSIONAMENTO DOS TRECHOS
            // ==========================================
            int row = 4;
            ws.Cell(row, 1).Value = "1. DIMENSIONAMENTO DOS TRECHOS DE TUBULAÇÃO";
            ws.Cell(row, 1).Style.Font.SetBold().Font.FontSize = 12;
            row++;

            string[] headersTrechos = { "Trecho", "Ligação", "Vazão (L/s)", "Velocidade (m/s)", "Reynolds", "Fator Atrito", "Perda Total (m.c.a)" };
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
                ws.Cell(row, 4).Value = Math.Round(seg.VelocityMetersPerSecond, 3);
                ws.Cell(row, 5).Value = Math.Round(seg.ReynoldsNumber, 0);
                ws.Cell(row, 6).Value = Math.Round(seg.FrictionFactor, 5);
                ws.Cell(row, 7).Value = Math.Round(seg.TotalHeadLossMeters, 4);
                row++;
            }

            // ==========================================
            // 3. TABELA DE VERIFICAÇÃO DE PRESSÕES (NÓS)
            // ==========================================
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
                
                string tipo = node.NodeType == HydraulicNodeType.Reservoir ? "Reservatório" :
                              node.NodeType == HydraulicNodeType.Junction ? "Nó" : "Consumo";
                ws.Cell(row, 2).Value = tipo;
                
                ws.Cell(row, 3).Value = Math.Round(node.ElevationMeters, 2);
                ws.Cell(row, 4).Value = Math.Round(node.PiezometricHeadMeters, 3);
                
                var pressaoCell = ws.Cell(row, 5);
                pressaoCell.Value = Math.Round(node.AvailablePressureMca, 3);
                
                var statusCell = ws.Cell(row, 6);
                
                // Validação de Pressão Baixa (< 1.0 m.c.a da norma)
                if (node.NodeType != HydraulicNodeType.Reservoir && node.AvailablePressureMca < 1.0)
                {
                    statusCell.Value = "PRESSÃO BAIXA";
                    statusCell.Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                    pressaoCell.Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                }
                else if (node.NodeType == HydraulicNodeType.Reservoir)
                {
                    statusCell.Value = "ORIGEM";
                    statusCell.Style.Font.SetFontColor(XLColor.Gray);
                }
                else
                {
                    statusCell.Value = "OK";
                    statusCell.Style.Font.SetBold().Font.SetFontColor(XLColor.ForestGreen);
                }
                row++;
            }

            // Ajustar largura das colunas automaticamente para o texto caber
            ws.Columns(1, 7).AdjustToContents();

            // Guardar o ficheiro no disco
            workbook.SaveAs(filePath);
        }
    }
}