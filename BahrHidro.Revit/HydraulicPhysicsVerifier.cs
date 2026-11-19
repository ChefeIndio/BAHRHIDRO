using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace BahrHidro.Revit
{
    public sealed class HydraulicVerificationSummary
    {
        public double TotalWeight { get; set; }
        public double DesignFlowLs { get; set; }
        public double MaxVelocityMs { get; set; }
        public double CriticalDynamicPressureMca { get; set; }
        public string CriticalFixtureName { get; set; } = "Chuveiro / Ponto Crítico";
        public bool IsNormativeCompliant { get; set; }
        public int UpsizedPipesCount { get; set; }
    }

    public static class HydraulicPhysicsVerifier
    {
        private const double FeetPerMeter = 3.280839895013123;
        private const double MmToFeet = 1.0 / 304.8;
        private static readonly double[] CommercialDnsMm = { 20.0, 25.0, 32.0, 40.0, 50.0, 60.0, 75.0, 85.0, 110.0 };

        public static HydraulicVerificationSummary VerifyAndOptimizeColdWater(
            Document doc,
            IEnumerable<Pipe> pipes,
            double sourceElevationFeet,
            double criticalOutletElevationFeet,
            string criticalOutletName)
        {
            var summary = new HydraulicVerificationSummary
            {
                CriticalFixtureName = criticalOutletName
            };

            var cwPipes = (pipes ?? Enumerable.Empty<Pipe>())
                .Where(p => p != null && p.IsValidObject)
                .Where(p =>
                {
                    string cmt = p.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    return !cmt.Contains("ESG |") && !cmt.Contains("VENT |");
                })
                .ToList();

            if (doc == null || cwPipes.Count == 0)
            {
                summary.IsNormativeCompliant = true;
                return summary;
            }

            double staticHeadM = Math.Max(0.20, (sourceElevationFeet - criticalOutletElevationFeet) / FeetPerMeter);

            // Executa até 3 passagens de otimização caso Pdin < 1.0 m.c.a. (10 kPa) ou v > 3.0 m/s
            for (int pass = 0; pass < 3; pass++)
            {
                double totalHeadLossM = 0.0;
                double maxVel = 0.0;
                double maxWeight = 0.0;

                foreach (var pipe in cwPipes)
                {
                    string cmt = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    double weight = ExtractWeightFromComment(cmt);
                    if (weight <= 0.0) weight = 0.3;
                    if (weight > maxWeight) maxWeight = weight;

                    double flowLs = 0.30 * Math.Sqrt(weight);
                    double dnMm = GetPipeDiameterMm(pipe);
                    double internalMm = GetPvcInternalDiameterMm(dnMm);

                    double velMs = ComputeVelocityMs(flowLs, internalMm);
                    if (velMs > 3.0)
                    {
                        double nextDn = GetNextCommercialDn(dnMm);
                        if (nextDn > dnMm)
                        {
                            SetPipeDiameterMm(pipe, nextDn);
                            dnMm = nextDn;
                            internalMm = GetPvcInternalDiameterMm(dnMm);
                            velMs = ComputeVelocityMs(flowLs, internalMm);
                            summary.UpsizedPipesCount++;
                        }
                    }

                    if (velMs > maxVel) maxVel = velMs;

                    double lengthM = GetPipeLengthMeters(pipe);
                    // Fair-Whipple-Hsiao para tubos plásticos (PVC/PEX): J = 8.59e5 * Q(L/s)^1.75 * D(mm)^-4.75
                    // Fator 1.35 contempla perdas localizadas em joelhos, tês e registros
                    double unitLoss = 8.59e5 * Math.Pow(flowLs, 1.75) * Math.Pow(internalMm, -4.75);
                    double segLoss = unitLoss * lengthM * 1.35;

                    // Soma ao caminho crítico se for prumada, barrilete ou sub-ramal do ponto crítico
                    if (cmt.Contains("Prumada") || cmt.Contains("Barrilete") || cmt.Contains("Tronco") ||
                        cmt.IndexOf(criticalOutletName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        totalHeadLossM += segLoss;
                    }
                }

                double dynamicPressureMca = staticHeadM - totalHeadLossM;
                summary.TotalWeight = maxWeight;
                summary.DesignFlowLs = 0.30 * Math.Sqrt(maxWeight);
                summary.MaxVelocityMs = maxVel;
                summary.CriticalDynamicPressureMca = dynamicPressureMca;
                summary.IsNormativeCompliant = dynamicPressureMca >= 1.0 && maxVel <= 3.0;

                if (summary.IsNormativeCompliant)
                    break;

                // Se Pdin < 1.0 m.c.a. (10 kPa), aumenta um degrau comercial nos trechos tronco/prumada
                bool anyUpsized = false;
                foreach (var pipe in cwPipes)
                {
                    string cmt = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    if (cmt.Contains("Prumada") || cmt.Contains("Barrilete") || cmt.Contains("Tronco"))
                    {
                        double curDn = GetPipeDiameterMm(pipe);
                        double nextDn = GetNextCommercialDn(curDn);
                        if (nextDn > curDn && nextDn <= 60.0)
                        {
                            SetPipeDiameterMm(pipe, nextDn);
                            summary.UpsizedPipesCount++;
                            anyUpsized = true;
                        }
                    }
                }

                if (!anyUpsized) break;
            }

            // Grava o memorial hidráulico calculado em cada segmento de tubo
            foreach (var pipe in cwPipes)
            {
                string cmt = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "BahrHidro | AF";
                double weight = Math.Max(0.3, ExtractWeightFromComment(cmt));
                double flowLs = 0.30 * Math.Sqrt(weight);
                double dnMm = GetPipeDiameterMm(pipe);
                double velMs = ComputeVelocityMs(flowLs, GetPvcInternalDiameterMm(dnMm));

                string cleanBase = Regex.Replace(cmt, @"\s*\|\s*NBR5626\[[^\]]*\]", string.Empty);
                string badge = summary.IsNormativeCompliant ? "OK" : "ALERTA_PRESSAO";
                string stamp = $"NBR5626[Q={flowLs:F2}L/s; v={velMs:F2}m/s; Pdin={summary.CriticalDynamicPressureMca:F2}mca; {badge}]";

                var cmtParam = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (cmtParam != null && !cmtParam.IsReadOnly)
                    cmtParam.Set($"{cleanBase} | {stamp}");

                var markParam = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (markParam != null && !markParam.IsReadOnly)
                    markParam.Set($"DN{(int)dnMm} ({velMs:F1}m/s)");
            }

            return summary;
        }

        private static double ComputeVelocityMs(double flowLs, double internalDiameterMm)
        {
            double qM3s = flowLs / 1000.0;
            double dM = Math.Max(0.010, internalDiameterMm / 1000.0);
            double area = Math.PI * Math.Pow(dM * 0.5, 2.0);
            return qM3s / area;
        }

        private static double GetPvcInternalDiameterMm(double nominalDnMm)
        {
            return nominalDnMm switch
            {
                <= 20.0 => 17.0,
                <= 25.0 => 21.6,
                <= 32.0 => 27.8,
                <= 40.0 => 35.2,
                <= 50.0 => 44.0,
                <= 60.0 => 53.4,
                _ => nominalDnMm * 0.88
            };
        }

        private static double GetNextCommercialDn(double currentDn)
        {
            foreach (double dn in CommercialDnsMm)
            {
                if (dn > currentDn + 0.5)
                    return dn;
            }
            return currentDn;
        }

        private static double GetPipeDiameterMm(Pipe pipe)
        {
            double feet = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? (25.0 * MmToFeet);
            return feet / MmToFeet;
        }

        private static void SetPipeDiameterMm(Pipe pipe, double dnMm)
        {
            var p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (p != null && !p.IsReadOnly)
                p.Set(dnMm * MmToFeet);
        }

        private static double GetPipeLengthMeters(Pipe pipe)
        {
            double feet = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 1.0;
            return Math.Max(0.05, feet / FeetPerMeter);
        }

        private static double ExtractWeightFromComment(string comment)
        {
            var m = Regex.Match(comment, @"Peso\s*=\s*([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                return val;
            return 0.0;
        }
    }
}
