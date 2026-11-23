using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BahrHidro.Core.Hydraulic.SewageTreatment
{
    public enum SewageBuildingTypology
    {
        ResidenciaPadraoAlto,
        ResidenciaPadraoMedio,
        ResidenciaPadraoBaixo,
        HotelPousada,
        ComercialEscritorio,
        EscolaExternato,
        FabricaIndustria
    }

    public enum ColdMonthTemperatureBand
    {
        MenorOuIgual10C,
        Entre10e20C,
        MaiorQue20C
    }

    public enum SoilPercolationProfile
    {
        AreiaGrossaPedregulho,
        AreiaFinaSilteArenoso,
        SilteArgiloso,
        ArgilaArenosa,
        ArgilaCompactaImpermeavel
    }

    public sealed record SewageTreatmentInput(
        SewageBuildingTypology Typology,
        int OccupantsCount,
        int KitchenSinksCount,
        ColdMonthTemperatureBand TemperatureBand,
        int CleaningIntervalYears,
        SoilPercolationProfile SoilProfile,
        double WaterTableDepthMeters,
        double PreferredSumidouroDiameterMeters);

    public sealed record SewageTreatmentResult(
        string GreaseTrapModelCode,
        string GreaseTrapDescription,
        double GreaseTrapVolumeLiters,
        int GreaseTrapOutletDnMm,
        double DailySewageFlowLiters,
        double DetentionTimeDays,
        int SludgeAccumulationFactorK,
        double SepticTankUsefulVolumeLiters,
        double SepticTankDiameterMeters,
        double SepticTankUsefulDepthMeters,
        double AnaerobicFilterUsefulVolumeLiters,
        double AnaerobicFilterDiameterMeters,
        double AnaerobicFilterTotalHeightMeters,
        double SoilInfiltrationCoeffLpm2Day,
        double RequiredInfiltrationAreaM2,
        int SumidouroUnitsCount,
        double SumidouroDiameterMeters,
        double SumidouroUsefulDepthMeters,
        List<string> NormativeAlerts,
        string TechnicalMemorialText);

    public static class SewageTreatmentAndGreaseTrapCalculator
    {
        public static SewageTreatmentResult Calculate(SewageTreatmentInput input)
        {
            int occupants = Math.Max(1, input.OccupantsCount);
            int sinks = Math.Max(0, input.KitchenSinksCount);
            int cleaningYears = Math.Clamp(input.CleaningIntervalYears, 1, 5);

            // =========================================================================
            // 1. CAIXA DE GORDURA (ABNT NBR 8160:1999 - Item 5.1.5 / Tabela 8)
            // =========================================================================
            string gtCode;
            string gtDesc;
            double gtVolumeL;
            int gtOutletDn;

            if (sinks <= 0)
            {
                gtCode = "N/A";
                gtDesc = "Sem pias de cozinha conectadas ao ramal de gordura";
                gtVolumeL = 0;
                gtOutletDn = 75;
            }
            else if (sinks == 1)
            {
                gtCode = "CGP (Pequena)";
                gtDesc = "Caixa de Gordura Pequena — Cilíndrica Ø int. 300 mm | Septo submerso 200 mm (até 1 pia)";
                gtVolumeL = 18.0;
                gtOutletDn = 75;
            }
            else if (sinks == 2)
            {
                gtCode = "CGS (Simples)";
                gtDesc = "Caixa de Gordura Simples — Cilíndrica Ø int. 400 mm | Septo submerso 200 mm (até 2 pias)";
                gtVolumeL = 31.0;
                gtOutletDn = 75;
            }
            else if (sinks <= 12)
            {
                gtCode = "CGD (Dupla)";
                gtDesc = $"Caixa de Gordura Dupla — Cilíndrica Ø int. 600 mm | Septo submerso 350 mm ({sinks} pias)";
                gtVolumeL = 120.0;
                gtOutletDn = 100;
            }
            else
            {
                gtCode = "CGE (Especial)";
                gtVolumeL = Math.Round(2.0 * occupants + 20.0, 0);
                double volM3 = Math.Max(0.25, gtVolumeL / 1000.0);
                double hMolhada = 0.65;
                double largura = Math.Max(0.60, Math.Round(Math.Sqrt(volM3 / (2.0 * hMolhada)), 2));
                double comprimento = Math.Round(2.0 * largura, 2);
                gtDesc = $"Caixa de Gordura Especial Prismática ({largura:F2} m × {comprimento:F2} m × H_útil {hMolhada:F2} m) | Septo 350 mm";
                gtOutletDn = 100;
            }

            // =========================================================================
            // 2. TANQUE SÉPTICO / FOSSA SÉPTICA (ABNT NBR 7229:1993)
            // =========================================================================
            var (contribC, freshSludgeLf) = input.Typology switch
            {
                SewageBuildingTypology.ResidenciaPadraoAlto => (160.0, 1.0),
                SewageBuildingTypology.ResidenciaPadraoMedio => (130.0, 1.0),
                SewageBuildingTypology.ResidenciaPadraoBaixo => (100.0, 1.0),
                SewageBuildingTypology.HotelPousada => (100.0, 1.0),
                SewageBuildingTypology.ComercialEscritorio => (50.0, 0.20),
                SewageBuildingTypology.EscolaExternato => (50.0, 0.20),
                SewageBuildingTypology.FabricaIndustria => (70.0, 0.30),
                _ => (130.0, 1.0)
            };

            double dailyFlowL = Math.Round(occupants * contribC, 1);

            // Período de detenção hidráulica T (dias) - Tabela 2 NBR 7229
            double detentionT = dailyFlowL switch
            {
                <= 1500.0 => 1.00,
                <= 3000.0 => 0.92,
                <= 4500.0 => 0.83,
                <= 6000.0 => 0.75,
                <= 7500.0 => 0.67,
                <= 9000.0 => 0.58,
                _ => 0.50
            };

            // Taxa de acumulação de lodo digerido K (dias) - Tabela 3 NBR 7229
            int kFactor = input.TemperatureBand switch
            {
                ColdMonthTemperatureBand.MenorOuIgual10C => cleaningYears switch { 1 => 94, 2 => 134, 3 => 174, 4 => 214, _ => 254 },
                ColdMonthTemperatureBand.Entre10e20C => cleaningYears switch { 1 => 65, 2 => 105, 3 => 145, 4 => 185, _ => 225 },
                _ => cleaningYears switch { 1 => 57, 2 => 97, 3 => 137, 4 => 177, _ => 217 }
            };

            // Volume útil do Tanque Séptico (Item 5.7 NBR 7229 — mínimo normativo 1.250 L)
            double septicVolL = Math.Max(1250.0, Math.Round(1000.0 + occupants * (contribC * detentionT + kFactor * freshSludgeLf), 0));
            double septicVolM3 = septicVolL / 1000.0;

            // Profundidade útil normativa (Tabela 4 NBR 7229)
            double minDepth = septicVolL <= 6000.0 ? 1.20 : (septicVolL <= 10000.0 ? 1.50 : 1.80);
            double maxDepth = septicVolL <= 6000.0 ? 2.20 : (septicVolL <= 10000.0 ? 2.50 : 2.80);

            double septicDiaM = Math.Max(1.10, Math.Round(Math.Sqrt((4.0 * septicVolM3) / (Math.PI * minDepth)), 2));
            double septicDepthM = Math.Round(septicVolM3 / (Math.PI * septicDiaM * septicDiaM * 0.25), 2);
            if (septicDepthM < minDepth) septicDepthM = minDepth;
            if (septicDepthM > maxDepth)
            {
                septicDepthM = maxDepth;
                septicDiaM = Math.Max(1.10, Math.Round(Math.Sqrt((4.0 * septicVolM3) / (Math.PI * septicDepthM)), 2));
            }

            // =========================================================================
            // 3. FILTRO ANAERÓBIO DE FLUXO ASCENDENTE (ABNT NBR 13969:1997 - Item 5.1)
            // =========================================================================
            // Vu = 1,6 * N * C * T (mínimo normativo 1.000 L)
            double filterVolL = Math.Max(1000.0, Math.Round(1.6 * occupants * contribC * detentionT, 0));
            double filterVolM3 = filterVolL / 1000.0;

            // Altura máxima do leito filtrante (brita nº 4/5) = 1,20 m + 0,20 m fundo falso + 0,30 m borda livre
            double filterBedHeightM = 1.20;
            double filterTotalHeightM = 1.70;
            double filterDiaM = Math.Max(0.95, Math.Round(Math.Sqrt((4.0 * filterVolM3) / (Math.PI * filterBedHeightM)), 2));

            // =========================================================================
            // 4. SUMIDOURO / POÇO ABSORVENTE (ABNT NBR 13969:1997 - Item 5.2 / Anexo A)
            // =========================================================================
            double ci = input.SoilProfile switch
            {
                SoilPercolationProfile.AreiaGrossaPedregulho => 100.0,
                SoilPercolationProfile.AreiaFinaSilteArenoso => 65.0,
                SoilPercolationProfile.SilteArgiloso => 40.0,
                SoilPercolationProfile.ArgilaArenosa => 25.0,
                SoilPercolationProfile.ArgilaCompactaImpermeavel => 10.0,
                _ => 40.0
            };

            double reqInfiltrationArea = Math.Round(dailyFlowL / ci, 2);
            double sumDiaM = Math.Clamp(input.PreferredSumidouroDiameterMeters, 1.10, 3.00);

            // Distância mínima obrigatória de 1,50 m entre o fundo do sumidouro e o lençol freático (NBR 13969)
            // Considera tubulação de entrada a ~0,60 m abaixo do nível do terreno + 0,30 m de brita no fundo
            double waterTableDepth = Math.Max(2.0, input.WaterTableDepthMeters);
            double maxPermittedUsefulDepth = Math.Clamp(waterTableDepth - 1.50 - 0.90, 1.00, 3.20);

            double bottomArea = Math.PI * sumDiaM * sumDiaM * 0.25;
            double perimeter = Math.PI * sumDiaM;

            // Calcula profundidade necessária para 1 unidade; se exceder o limite freático, subdivide em N unidades
            double singleUnitNeededDepth = Math.Max(1.00, (reqInfiltrationArea - bottomArea) / perimeter);
            int sumUnits = 1;
            double sumUsefulDepthM = Math.Round(singleUnitNeededDepth, 2);

            if (sumUsefulDepthM > maxPermittedUsefulDepth)
            {
                double maxAreaPerUnit = bottomArea + perimeter * maxPermittedUsefulDepth;
                sumUnits = Math.Max(2, (int)Math.Ceiling(reqInfiltrationArea / maxAreaPerUnit));
                double areaPerUnit = reqInfiltrationArea / sumUnits;
                sumUsefulDepthM = Math.Max(1.00, Math.Round((areaPerUnit - bottomArea) / perimeter, 2));
            }

            // =========================================================================
            // 5. ALERTAS NORMATIVOS (NBR 8160 / NBR 7229 / NBR 13969)
            // =========================================================================
            var alerts = new List<string>();
            if (input.SoilProfile == SoilPercolationProfile.ArgilaCompactaImpermeavel)
                alerts.Add("NBR 13969: Solo de argila compacta/impermeável (Ci ≤ 10 L/m².dia) — substituir sumidouro por Vala de Filtração + Cloração e lançamento em galeria pluvial.");
            if (waterTableDepth < 3.40)
                alerts.Add($"NBR 13969: Lençol freático raso ({waterTableDepth:F2} m) — profundidade útil do sumidouro limitada a {sumUsefulDepthM:F2} m para garantir folga mínima de 1,50 m acima do nível d'água.");
            if (sumUnits > 1)
                alerts.Add($"NBR 13969: Prever caixa de distribuição igualitária e manter afastamento mínimo entre eixos de {3.0 * sumDiaM:F2} m (3 × Ø) entre os {sumUnits} sumidouros.");
            if (septicDiaM > 2.0 * septicDepthM)
                alerts.Add("NBR 7229: Relação Diâmetro / Profundidade útil próxima do limite de 2,0 — verificar estabilidade da laje de cobertura.");
            alerts.Add("NBR 8160: Não lançar águas pluviais nem esgoto primário (bacias sanitárias) na Caixa de Gordura.");
            alerts.Add("NBR 7229 / 13969: Manter afastamento horizontal mínimo de 1,50 m de divisas/fundações e 15,0 m de poços freáticos de captação de água.");

            string memorial = BuildMemorialText(
                input, occupants, sinks, cleaningYears,
                gtCode, gtDesc, gtVolumeL, gtOutletDn,
                contribC, freshSludgeLf, dailyFlowL, detentionT, kFactor,
                septicVolL, septicDiaM, septicDepthM,
                filterVolL, filterDiaM, filterBedHeightM, filterTotalHeightM,
                ci, reqInfiltrationArea, sumUnits, sumDiaM, sumUsefulDepthM, alerts);

            return new SewageTreatmentResult(
                gtCode,
                gtDesc,
                gtVolumeL,
                gtOutletDn,
                dailyFlowL,
                detentionT,
                kFactor,
                septicVolL,
                septicDiaM,
                septicDepthM,
                filterVolL,
                filterDiaM,
                filterTotalHeightM,
                ci,
                reqInfiltrationArea,
                sumUnits,
                sumDiaM,
                sumUsefulDepthM,
                alerts,
                memorial);
        }

        private static string BuildMemorialText(
            SewageTreatmentInput input,
            int occupants,
            int sinks,
            int cleaningYears,
            string gtCode,
            string gtDesc,
            double gtVol,
            int gtDn,
            double contribC,
            double lf,
            double qDaily,
            double detT,
            int kFactor,
            double septicVol,
            double septicDia,
            double septicDepth,
            double filterVol,
            double filterDia,
            double filterBedH,
            double filterTotH,
            double ci,
            double areaInf,
            int sumUnits,
            double sumDia,
            double sumDepth,
            List<string> alerts)
        {
            var pt = new CultureInfo("pt-BR");
            var sb = new StringBuilder();
            sb.AppendLine("MEMORIAL DE TRATAMENTO INDIVIDUAL DE ESGOTO E CAIXA DE GORDURA");
            sb.AppendLine("Normas Técnicas: ABNT NBR 8160, ABNT NBR 7229 e ABNT NBR 13969");
            sb.AppendLine(new string('=', 68));
            sb.AppendLine($"• Tipologia / População   : {input.Typology} ({occupants} contribuintes)");
            sb.AppendLine($"• Contribuição Esgoto (C) : {contribC:F0} L/pessoa.dia  |  Lodo Fresco (Lf): {lf:F2} L/pessoa.dia");
            sb.AppendLine($"• Vazão Diária Total (Q)  : {qDaily.ToString("N0", pt)} L/dia  ({(qDaily / 1000.0).ToString("N2", pt)} m³/dia)");
            sb.AppendLine();
            sb.AppendLine("1. CAIXA DE GORDURA (ABNT NBR 8160 — TABELA 8)");
            sb.AppendLine($"   - Pias de Cozinha Atendidas : {sinks} unidade(s)");
            sb.AppendLine($"   - Modelo Normativo          : {gtCode}");
            sb.AppendLine($"   - Especificação Construtiva : {gtDesc}");
            sb.AppendLine($"   - Volume Retenção / Saída   : {gtVol:F0} Litros  |  Tubulação de Saída DN {gtDn} mm");
            sb.AppendLine();
            sb.AppendLine("2. TANQUE SÉPTICO / FOSSA SÉPTICA (ABNT NBR 7229)");
            sb.AppendLine($"   - Período Detenção (T)      : {detT:F2} dia ({detT * 24.0:F0} horas)");
            sb.AppendLine($"   - Taxa Acúmulo Lodo (K)     : {kFactor} dias (Limpeza a cada {cleaningYears} ano(s))");
            sb.AppendLine($"   - Volume Útil Calculado     : {septicVol.ToString("N0", pt)} Litros  ({(septicVol / 1000.0).ToString("N2", pt)} m³)");
            sb.AppendLine($"   - Dimensões (Cilíndrico)    : Ø interno = {septicDia:F2} m  |  Profundidade útil = {septicDepth:F2} m");
            sb.AppendLine();
            sb.AppendLine("3. FILTRO ANAERÓBIO DE FLUXO ASCENDENTE (ABNT NBR 13969)");
            sb.AppendLine($"   - Volume Útil (1,6·N·C·T)   : {filterVol.ToString("N0", pt)} Litros  ({(filterVol / 1000.0).ToString("N2", pt)} m³)");
            sb.AppendLine($"   - Dimensões Construtivas    : Ø interno = {filterDia:F2} m  |  H_leito (Brita 4/5) = {filterBedH:F2} m  |  H_total = {filterTotH:F2} m");
            sb.AppendLine();
            sb.AppendLine("4. SUMIDOURO / DISPOSIÇÃO FINAL NO SOLO (ABNT NBR 13969)");
            sb.AppendLine($"   - Perfil / Coef. Infiltr.   : {input.SoilProfile} (Ci = {ci:F0} L/m².dia)");
            sb.AppendLine($"   - Área de Infiltração Req.  : {areaInf.ToString("N2", pt)} m²");
            sb.AppendLine($"   - Dimensionamento Final     : {sumUnits} unidade(s) × Ø {sumDia:F2} m × Profundidade útil {sumDepth:F2} m (+ 0,30 m brita fundo)");
            sb.AppendLine();
            sb.AppendLine("5. RECOMENDAÇÕES EXECUTIVAS E AMBIENTAIS");
            foreach (var a in alerts)
                sb.AppendLine($"   * {a}");

            return sb.ToString();
        }
    }
}
