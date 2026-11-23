using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace BahrHidro.Core.Reporting
{
    public sealed record SinapiImportResult(
        string StateUf,
        string SourceFileName,
        int TotalCodesLoaded,
        Dictionary<string, decimal> PricesByNormalizedCode);

    public static class SinapiOfficialPriceImporter
    {
        private static readonly HashSet<string> ValidBrazilianUfs = new(StringComparer.OrdinalIgnoreCase)
        {
            "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS",
            "MG", "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC",
            "SP", "SE", "TO"
        };

        public static SinapiImportResult ImportFromFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Arquivo SINAPI não encontrado.", filePath);

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string fileName = Path.GetFileName(filePath);

            return ext switch
            {
                ".xlsx" or ".xlsm" => ImportFromXlsx(filePath, fileName),
                ".csv" or ".txt" => ImportFromCsv(filePath, fileName),
                _ => throw new NotSupportedException($"Formato '{ext}' não suportado. Utilize planilhas .xlsx ou .csv.")
            };
        }

        public static string NormalizeSinapiCode(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string cleaned = Regex.Replace(raw, @"sinapi", "", RegexOptions.IgnoreCase).Trim();
            cleaned = cleaned.Replace(".", "").Replace("-", "").Replace("/", "").Replace(" ", "");
            if (cleaned.All(char.IsDigit))
            {
                cleaned = cleaned.TrimStart('0');
            }
            return cleaned.ToUpperInvariant();
        }

        private static SinapiImportResult ImportFromXlsx(string filePath, string fileName)
        {
            var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            string detectedUf = DetectUfFromText(fileName);

            using var workbook = new XLWorkbook(filePath);
            foreach (var ws in workbook.Worksheets)
            {
                var used = ws.RangeUsed();
                if (used == null) continue;

                int firstRow = used.FirstRow().RowNumber();
                int lastRow = used.LastRow().RowNumber();
                int firstCol = used.FirstColumn().ColumnNumber();
                int lastCol = Math.Min(used.LastColumn().ColumnNumber(), firstCol + 25);

                // Procura UF nas primeiras linhas da aba
                if (string.IsNullOrEmpty(detectedUf))
                {
                    for (int r = firstRow; r <= Math.Min(lastRow, firstRow + 12); r++)
                    {
                        for (int c = firstCol; c <= lastCol; c++)
                        {
                            string cellTxt = ws.Cell(r, c).GetFormattedString();
                            string uf = DetectUfFromText(cellTxt);
                            if (!string.IsNullOrEmpty(uf))
                            {
                                detectedUf = uf;
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(detectedUf)) break;
                    }
                }

                int headerRow = -1;
                int codeCol = -1;
                int priceCol = -1;

                for (int r = firstRow; r <= Math.Min(lastRow, firstRow + 25); r++)
                {
                    int foundCode = -1;
                    int foundPrice = -1;

                    for (int c = firstCol; c <= lastCol; c++)
                    {
                        string h = NormalizeHeader(ws.Cell(r, c).GetFormattedString());
                        if (string.IsNullOrEmpty(h)) continue;

                        if (foundCode == -1 && IsCodeHeader(h))
                            foundCode = c;
                        else if (IsPriceHeader(h))
                        {
                            if (foundPrice == -1 || h.Contains("mediano") || h.Contains("unitario"))
                                foundPrice = c;
                        }
                    }

                    if (foundCode != -1 && foundPrice != -1)
                    {
                        headerRow = r;
                        codeCol = foundCode;
                        priceCol = foundPrice;
                        break;
                    }
                }

                if (headerRow != -1)
                {
                    for (int r = headerRow + 1; r <= lastRow; r++)
                    {
                        string rawCode = ws.Cell(r, codeCol).GetFormattedString();
                        string normCode = NormalizeSinapiCode(rawCode);
                        if (normCode.Length < 3) continue;

                        if (TryReadExcelCellPrice(ws.Cell(r, priceCol), out decimal price) && price > 0m)
                        {
                            prices[normCode] = Math.Round(price, 2);
                        }
                    }
                }
                else
                {
                    // Fallback heurístico para planilhas sem cabeçalho explícito
                    for (int r = firstRow; r <= lastRow; r++)
                    {
                        string normCode = "";
                        for (int c = firstCol; c <= Math.Min(lastCol, firstCol + 3); c++)
                        {
                            string cand = NormalizeSinapiCode(ws.Cell(r, c).GetFormattedString());
                            if (cand.Length >= 4 && cand.Length <= 10 && cand.All(char.IsDigit))
                            {
                                normCode = cand;
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(normCode)) continue;

                        for (int c = firstCol + 1; c <= lastCol; c++)
                        {
                            if (TryReadExcelCellPrice(ws.Cell(r, c), out decimal price) && price > 0m)
                            {
                                prices[normCode] = Math.Round(price, 2);
                                break;
                            }
                        }
                    }
                }
            }

            if (prices.Count == 0)
                throw new InvalidOperationException("Não foi possível identificar colunas de Código SINAPI e Preço Unitário/Mediano na planilha.");

            return new SinapiImportResult(
                string.IsNullOrEmpty(detectedUf) ? "BR" : detectedUf.ToUpperInvariant(),
                fileName,
                prices.Count,
                prices);
        }

        private static SinapiImportResult ImportFromCsv(string filePath, string fileName)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            string content;
            try
            {
                content = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch
            {
                content = Encoding.Latin1.GetString(bytes);
            }

            var rawLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            if (rawLines.Length == 0)
                throw new InvalidOperationException("O arquivo CSV selecionado está vazio.");

            char sep = DetectCsvSeparator(rawLines.Take(15));
            string detectedUf = DetectUfFromText(fileName);

            int headerIdx = -1;
            int codeCol = -1;
            int priceCol = -1;

            for (int i = 0; i < Math.Min(rawLines.Length, 25); i++)
            {
                if (string.IsNullOrEmpty(detectedUf))
                    detectedUf = DetectUfFromText(rawLines[i]);

                var cols = SplitCsvLine(rawLines[i], sep);
                int foundCode = -1;
                int foundPrice = -1;

                for (int c = 0; c < cols.Count; c++)
                {
                    string h = NormalizeHeader(cols[c]);
                    if (string.IsNullOrEmpty(h)) continue;

                    if (foundCode == -1 && IsCodeHeader(h))
                        foundCode = c;
                    else if (IsPriceHeader(h))
                    {
                        if (foundPrice == -1 || h.Contains("mediano") || h.Contains("unitario"))
                            foundPrice = c;
                    }
                }

                if (foundCode != -1 && foundPrice != -1)
                {
                    headerIdx = i;
                    codeCol = foundCode;
                    priceCol = foundPrice;
                    break;
                }
            }

            var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            if (headerIdx != -1)
            {
                for (int i = headerIdx + 1; i < rawLines.Length; i++)
                {
                    var cols = SplitCsvLine(rawLines[i], sep);
                    if (codeCol >= cols.Count || priceCol >= cols.Count) continue;

                    string normCode = NormalizeSinapiCode(cols[codeCol]);
                    if (normCode.Length < 3) continue;

                    if (TryParseBrazilianDecimal(cols[priceCol], out decimal price) && price > 0m)
                    {
                        prices[normCode] = Math.Round(price, 2);
                    }
                }
            }
            else
            {
                foreach (var line in rawLines)
                {
                    var cols = SplitCsvLine(line, sep);
                    if (cols.Count < 2) continue;

                    string normCode = "";
                    int codeIdx = -1;
                    for (int c = 0; c < Math.Min(cols.Count, 3); c++)
                    {
                        string cand = NormalizeSinapiCode(cols[c]);
                        if (cand.Length >= 4 && cand.Length <= 10 && cand.All(char.IsDigit))
                        {
                            normCode = cand;
                            codeIdx = c;
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(normCode)) continue;

                    for (int c = codeIdx + 1; c < cols.Count; c++)
                    {
                        if (TryParseBrazilianDecimal(cols[c], out decimal price) && price > 0m)
                        {
                            prices[normCode] = Math.Round(price, 2);
                            break;
                        }
                    }
                }
            }

            if (prices.Count == 0)
                throw new InvalidOperationException("Não foi possível identificar colunas de Código SINAPI e Preço no arquivo CSV.");

            return new SinapiImportResult(
                string.IsNullOrEmpty(detectedUf) ? "BR" : detectedUf.ToUpperInvariant(),
                fileName,
                prices.Count,
                prices);
        }

        private static bool TryReadExcelCellPrice(IXLCell cell, out decimal price)
        {
            price = 0m;
            if (cell.IsEmpty()) return false;
            if (cell.DataType == XLDataType.Number)
            {
                double d = cell.GetDouble();
                if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0) return false;
                price = (decimal)d;
                return true;
            }
            return TryParseBrazilianDecimal(cell.GetFormattedString(), out price);
        }

        private static bool TryParseBrazilianDecimal(string? raw, out decimal result)
        {
            result = 0m;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string cleaned = raw.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (cleaned.Contains(',') && cleaned.Contains('.'))
                cleaned = cleaned.Replace(".", "").Replace(',', '.');
            else if (cleaned.Contains(','))
                cleaned = cleaned.Replace(',', '.');

            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
        }

        private static bool IsCodeHeader(string h) =>
            h.Contains("codigo") || h.Contains("sinapi") || h == "cod" || h.StartsWith("cod ");

        private static bool IsPriceHeader(string h) =>
            h.Contains("mediano") || h.Contains("preco") || h.Contains("custo") || h.Contains("unitario") || h == "valor";

        private static string DetectUfFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var explicitMatch = Regex.Match(text, @"\b(?:UF|ESTADO|LOCALIDADE)\s*[:\-]?\s*([A-Z]{2})\b", RegexOptions.IgnoreCase);
            if (explicitMatch.Success && ValidBrazilianUfs.Contains(explicitMatch.Groups[1].Value))
                return explicitMatch.Groups[1].Value.ToUpperInvariant();

            var tokenMatches = Regex.Matches(text, @"(?:^|[_\-\s\(\[])([A-Z]{2})(?:$|[_\-\s\)\]\.])");
            foreach (Match m in tokenMatches)
            {
                string uf = m.Groups[1].Value;
                if (ValidBrazilianUfs.Contains(uf))
                    return uf.ToUpperInvariant();
            }
            return "";
        }

        private static char DetectCsvSeparator(IEnumerable<string> sampleLines)
        {
            int semi = 0, tab = 0, comma = 0;
            foreach (var line in sampleLines)
            {
                semi += line.Count(c => c == ';');
                tab += line.Count(c => c == '\t');
                comma += line.Count(c => c == ',');
            }
            if (semi >= tab && semi >= comma && semi > 0) return ';';
            if (tab >= semi && tab >= comma && tab > 0) return '\t';
            return ',';
        }

        private static List<string> SplitCsvLine(string line, char sep)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == sep && !inQuotes)
                {
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }
            result.Add(sb.ToString().Trim());
            return result;
        }

        private static string NormalizeHeader(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string formD = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char ch in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
        }
    }
}
