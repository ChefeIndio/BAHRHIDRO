using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BahrHidro.Core.Reporting
{
    public sealed record SinapiBudgetLine(
        int ItemNumber,
        string SystemName,
        string Description,
        string DiameterDisplay,
        string CommercialRef,
        string SinapiCode,
        string Unit,
        double Quantity,
        decimal UnitPriceBrl,
        decimal TotalPriceBrl);

    public static class SinapiCommercialMapper
    {
        public static List<SinapiBudgetLine> BuildBudgetLines(BimMaterialTakeoff? takeoff)
        {
            var lines = new List<SinapiBudgetLine>();
            if (takeoff == null) return lines;

            int idx = 1;

            foreach (var p in takeoff.Pipes)
            {
                int dn = (int)Math.Round(p.DiameterMm);
                var (brandRef, sinapi, unitPrice) = MapPipeCommercialAndSinapi(p.SystemName, p.PipeTypeName, dn);
                double qty = Math.Round(p.TotalLengthWithWasteMeters, 2);
                decimal total = Math.Round((decimal)qty * unitPrice, 2);

                lines.Add(new SinapiBudgetLine(
                    idx++,
                    p.SystemName,
                    $"Tubo {p.PipeTypeName} (Varas 6m: {p.CommercialBars6m})",
                    $"DN {dn} mm",
                    brandRef,
                    sinapi,
                    "m",
                    qty,
                    unitPrice,
                    total));
            }

            foreach (var f in takeoff.Fittings)
            {
                string desc = $"{f.CategoryName} - {f.FamilyAndType}";
                string size = f.SizeDescription;
                var (brandRef, sinapi, unitPrice) = MapFittingCommercialAndSinapi(f.SystemName, desc, size);
                decimal total = Math.Round((decimal)f.Quantity * unitPrice, 2);

                lines.Add(new SinapiBudgetLine(
                    idx++,
                    f.SystemName,
                    desc,
                    size,
                    brandRef,
                    sinapi,
                    "un",
                    f.Quantity,
                    unitPrice,
                    total));
            }

            foreach (var k in takeoff.Kits)
            {
                var (brandRef, sinapi, unitPrice) = MapKitCommercialAndSinapi(k.KitName);
                decimal total = Math.Round((decimal)k.Quantity * unitPrice, 2);

                lines.Add(new SinapiBudgetLine(
                    idx++,
                    "Acessórios / Registros",
                    k.KitName,
                    "-",
                    brandRef,
                    sinapi,
                    "cj",
                    k.Quantity,
                    unitPrice,
                    total));
            }

            return lines;
        }

        private static (string BrandRef, string SinapiCode, decimal UnitPrice) MapPipeCommercialAndSinapi(string system, string type, int dn)
        {
            string norm = Normalize($"{system} {type}");
            if (norm.Contains("quente") || norm.Contains("hot") || norm.Contains("cpvc") || norm.Contains("ppr") || norm.Contains("aquatherm"))
            {
                return dn switch
                {
                    <= 15 => ("Tigre Aquatherm / Amanco CPVC DN 15", "SINAPI 89620", 14.90m),
                    <= 22 => ("Tigre Aquatherm / Amanco CPVC DN 22", "SINAPI 89621", 21.50m),
                    <= 28 => ("Tigre Aquatherm / Amanco CPVC DN 28", "SINAPI 89622", 34.80m),
                    _ => ($"Tigre Aquatherm / Amanco CPVC DN {dn}", "SINAPI 89623", 49.00m)
                };
            }

            if (norm.Contains("esgoto") || norm.Contains("sanit") || norm.Contains("pluvial") || norm.Contains("ventil"))
            {
                return dn switch
                {
                    <= 40 => ("Tigre / Amanco PVC Esgoto SN DN 40", "SINAPI 89711", 9.80m),
                    <= 50 => ("Tigre / Amanco PVC Esgoto SN DN 50", "SINAPI 89712", 13.40m),
                    <= 75 => ("Tigre / Amanco PVC Esgoto SN DN 75", "SINAPI 89713", 19.90m),
                    <= 100 => ("Tigre / Amanco PVC Esgoto SN/SR DN 100", "SINAPI 89714", 26.50m),
                    _ => ($"Tigre / Amanco PVC Esgoto SR DN {dn}", "SINAPI 89715", 48.00m)
                };
            }

            return dn switch
            {
                <= 20 => ("Tigre / Amanco PVC Soldável DN 20", "SINAPI 89401", 6.20m),
                <= 25 => ("Tigre / Amanco PVC Soldável DN 25", "SINAPI 89402", 8.40m),
                <= 32 => ("Tigre / Amanco PVC Soldável DN 32", "SINAPI 89403", 14.20m),
                <= 40 => ("Tigre / Amanco PVC Soldável DN 40", "SINAPI 89404", 19.50m),
                <= 50 => ("Tigre / Amanco PVC Soldável DN 50", "SINAPI 89405", 25.80m),
                _ => ($"Tigre / Amanco PVC Soldável DN {dn}", "SINAPI 89406", 38.00m)
            };
        }

        private static (string BrandRef, string SinapiCode, decimal UnitPrice) MapFittingCommercialAndSinapi(string system, string desc, string size)
        {
            string norm = Normalize($"{system} {desc}");
            bool isHot = norm.Contains("quente") || norm.Contains("cpvc") || norm.Contains("ppr");
            bool isSewer = norm.Contains("esgoto") || norm.Contains("sanit") || norm.Contains("pluvial");

            if (isHot)
                return ($"Tigre Aquatherm / Amanco CPVC ({size})", "SINAPI 89635", 16.50m);
            if (isSewer)
                return ($"Tigre / Amanco PVC Esgoto ({size})", "SINAPI 89732", 12.80m);
            return ($"Tigre / Amanco PVC Soldável ({size})", "SINAPI 89442", 5.90m);
        }

        private static (string BrandRef, string SinapiCode, decimal UnitPrice) MapKitCommercialAndSinapi(string kitName)
        {
            string norm = Normalize(kitName);
            if (norm.Contains("gaveta") || norm.Contains("pressao") || norm.Contains("registro"))
                return ("Deca / Docol / Tigre Registro Base", "SINAPI 89985", 68.00m);
            if (norm.Contains("sifonada") || norm.Contains("caixa"))
                return ("Tigre / Amanco Caixa Sifonada", "SINAPI 89707", 42.50m);
            return ("Tigre / Amanco / Deca Padrão", "SINAPI 89450", 28.00m);
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
