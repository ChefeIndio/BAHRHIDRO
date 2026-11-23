using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BahrHidro.Core.Hydraulic.Pumping
{
    public enum ReservoirBuildingTypology
    {
        ResidencialUnifamiliar,
        ResidencialMultifamiliar,
        ComercialEscritorio,
        HotelPousada,
        EscolaHospital
    }

    public enum PumpingPipeMaterial
    {
        PvcSoldavel,
        PprTermofusao,
        FerroGalvanizado
    }

    public sealed record ReservoirPumpingInput(
        ReservoirBuildingTypology Typology,
        int OccupantsCount,
        double AutonomyDays,
        double FireReserveLiters,
        double UpperReservoirSharePercent,
        double PumpOperatingHoursPerDay,
        double DischargeGeometricHeightMeters,
        double SuctionHeightMeters,
        double DischargePipeLengthMeters,
        double SuctionPipeLengthMeters,
        PumpingPipeMaterial PipeMaterial,
        double PumpEfficiencyPercent,
        double AltitudeMeters = 600.0);

    public sealed record ReservoirPumpingResult(
        double DailyConsumptionLiters,
        double TotalPotableReserveLiters,
        double RequiredLowerLiters,
        double RequiredUpperWithFireLiters,
        int CommercialLowerReservoirLiters,
        int CommercialUpperReservoirLiters,
        double PumpingFlowLps,
        double PumpingFlowM3h,
        int DischargeNominalDiameterMm,
        double DischargeInternalDiameterMm,
        double DischargeVelocityMs,
        int SuctionNominalDiameterMm,
        double SuctionInternalDiameterMm,
        double SuctionVelocityMs,
        double SuctionHeadLossMeters,
        double DischargeHeadLossMeters,
        double GeometricHeadMeters,
        double TotalDynamicHeadAmtMeters,
        double NpshAvailableMeters,
        double CalculatedShaftPowerCv,
        double SelectedMotorPowerCv,
        string SelectedPumpSpec,
        List<string> NormativeAlerts,
        string TechnicalMemorialText);

    public static class ReservoirAndPumpingCalculator
    {
        private static readonly (int Dn, double DiMm)[] CommercialPipes =
        {
            (20, 17.0),
            (25, 21.6),
            (32, 27.8),
            (40, 35.2),
            (50, 44.0),
            (60, 53.4),
            (75, 66.6),
            (85, 75.6),
            (110, 97.8),
            (125, 111.2),
            (150, 133.6)
        };

        private static readonly int[] CommercialTankSizes =
        {
            500, 1000, 1500, 2000, 2500, 3000, 5000, 7500, 10000, 15000, 20000, 25000, 30000, 40000, 50000
        };

        private static readonly double[] CommercialMotorCv =
        {
            0.33, 0.50, 0.75, 1.0, 1.5, 2.0, 3.0, 4.0, 5.0, 7.5, 10.0, 12.5, 15.0, 20.0, 25.0, 30.0
        };

        public static ReservoirPumpingResult Calculate(ReservoirPumpingInput input)
        {
            int occupants = Math.Max(1, input.OccupantsCount);
            double days = Math.Clamp(input.AutonomyDays, 1.0, 5.0);
            double rtiLiters = Math.Max(0.0, input.FireReserveLiters);
            double upperPct = Math.Clamp(input.UpperReservoirSharePercent, 0.0, 100.0) / 100.0;
            double lowerPct = 1.0 - upperPct;

            // 1. Consumo diário per capita conforme tipologia (ABNT NBR 5626)
            double perCapitaL = input.Typology switch
            {
                ReservoirBuildingTypology.ResidencialUnifamiliar => 150.0,
                ReservoirBuildingTypology.ResidencialMultifamiliar => 200.0,
                ReservoirBuildingTypology.ComercialEscritorio => 50.0,
                ReservoirBuildingTypology.HotelPousada => 250.0,
                ReservoirBuildingTypology.EscolaHospital => 120.0,
                _ => 150.0
            };

            double dailyLiters = Math.Round(occupants * perCapitaL, 1);
            double totalPotableLiters = Math.Round(dailyLiters * days, 1);

            double reqLower = Math.Round(totalPotableLiters * lowerPct, 1);
            double reqUpper = Math.Round(totalPotableLiters * upperPct + rtiLiters, 1);

            int commLower = reqLower > 0 ? SelectCommercialTank(reqLower) : 0;
            int commUpper = SelectCommercialTank(reqUpper);

            // 2. Vazão de recalque (L/s e m³/h)
            double opHours = Math.Clamp(input.PumpOperatingHoursPerDay, 0.5, 16.0);
            double qLps = Math.Round(dailyLiters / (opHours * 3600.0), 3);
            double qM3h = Math.Round(qLps * 3.6, 2);
            double qM3s = qLps / 1000.0;

            // 3. Diâmetros de recalque (Bresse) e sucção (1 bitola comercial acima)
            double bresseMeters = 1.3 * Math.Pow(opHours / 24.0, 0.25) * Math.Sqrt(qM3s);
            double bresseMm = bresseMeters * 1000.0;

            int disIdx = 1; // mínimo DN 25 mm
            for (int i = 1; i < CommercialPipes.Length; i++)
            {
                double diM = CommercialPipes[i].DiMm / 1000.0;
                double area = Math.PI * diM * diM * 0.25;
                double vel = qM3s / area;
                if (CommercialPipes[i].DiMm >= bresseMm && vel <= 2.5)
                {
                    disIdx = i;
                    break;
                }
                disIdx = i;
            }

            int sucIdx = Math.Min(CommercialPipes.Length - 1, disIdx + 1);

            var (dnDis, diDisMm) = CommercialPipes[disIdx];
            var (dnSuc, diSucMm) = CommercialPipes[sucIdx];

            double diDisM = diDisMm / 1000.0;
            double diSucM = diSucMm / 1000.0;

            double velDis = Math.Round(qM3s / (Math.PI * diDisM * diDisM * 0.25), 2);
            double velSuc = Math.Round(qM3s / (Math.PI * diSucM * diSucM * 0.25), 2);

            // 4. Perdas de carga (Hazen-Williams + comprimento equivalente de conexões)
            double cHw = input.PipeMaterial switch
            {
                PumpingPipeMaterial.PvcSoldavel => 140.0,
                PumpingPipeMaterial.PprTermofusao => 150.0,
                PumpingPipeMaterial.FerroGalvanizado => 120.0,
                _ => 140.0
            };

            double lenDis = Math.Max(1.0, input.DischargePipeLengthMeters);
            double lenSuc = Math.Max(0.5, input.SuctionPipeLengthMeters);

            // Acréscimo de comprimento equivalente: +45% na sucção (válvula de pé, crivo, curvas, registro) e +25% no recalque
            double eqLenSuc = lenSuc * 1.45;
            double eqLenDis = lenDis * 1.25;

            double jSuc = 10.643 * Math.Pow(qM3s, 1.852) * Math.Pow(cHw, -1.852) * Math.Pow(diSucM, -4.87);
            double jDis = 10.643 * Math.Pow(qM3s, 1.852) * Math.Pow(cHw, -1.852) * Math.Pow(diDisM, -4.87);

            double dhSuc = Math.Round(jSuc * eqLenSuc, 2);
            double dhDis = Math.Round(jDis * eqLenDis, 2);

            // 5. Altura Geométrica e Altura Manométrica Total (AMT)
            // SuctionHeightMeters: positivo = sucção afogada; negativo = sucção por aspiração
            double hGeo = Math.Round(input.DischargeGeometricHeightMeters - input.SuctionHeightMeters, 2);
            double amt = Math.Round(Math.Max(1.0, hGeo + dhSuc + dhDis), 2);

            // 6. NPSH Disponível (anti-cavitação)
            double pAtmMca = 10.33 * Math.Exp(-Math.Max(0.0, input.AltitudeMeters) / 8400.0);
            double pVaporMca = 0.24; // água a 20 °C
            double npshDisp = Math.Round((pAtmMca - pVaporMca) + input.SuctionHeightMeters - dhSuc, 2);

            // 7. Potência do Conjunto Motobomba (CV)
            double eff = Math.Clamp(input.PumpEfficiencyPercent, 35.0, 85.0) / 100.0;
            double calcCv = Math.Round((1000.0 * qM3s * amt) / (75.0 * eff), 2);
            double selectedCv = SelectCommercialMotorCv(calcCv);
            string cvLabel = selectedCv < 1.0
                ? (selectedCv <= 0.35 ? "1/3" : selectedCv <= 0.55 ? "1/2" : "3/4")
                : selectedCv.ToString("0.##", new CultureInfo("pt-BR"));

            string pumpSpec = $"2x Conjuntos Motobomba Centrífugos de {cvLabel} CV (1 Operante + 1 Reserva) — Q = {qM3h:F2} m³/h @ AMT = {amt:F2} m.c.a.";

            // 8. Alertas Normativos (ABNT NBR 5626)
            var alerts = new List<string>();
            if (velDis > 2.5)
                alerts.Add($"NBR 5626: Velocidade no recalque ({velDis:F2} m/s) acima do limite econômico de 2,5 m/s — avaliar bitola superior.");
            if (velSuc > 1.5)
                alerts.Add($"NBR 5626: Velocidade na sucção ({velSuc:F2} m/s) acima de 1,5 m/s — risco de perda de carga excessiva na entrada da bomba.");
            if (npshDisp < 4.0)
                alerts.Add($"NBR 5626: NPSH disponível reduzido ({npshDisp:F2} m.c.a.) — priorizar instalação com sucção afogada e conferir curva NPSH requerido do fabricante.");
            if (rtiLiters > 0)
                alerts.Add($"NBR 5626 / Bombeiros: Manter tomada de consumo predial acima da cota da Reserva Técnica de Incêndio ({rtiLiters:N0} L).");
            alerts.Add("NBR 5626: Instalar válvula de retenção e registro de gaveta no recalque, além de boia automática de nível nos reservatórios.");
            alerts.Add("NBR 5626: Dividir reservatórios acima de 2.000 L em duas células interligadas para limpeza sem interrupção do abastecimento.");

            string memorial = BuildMemorialText(
                input, occupants, perCapitaL, dailyLiters, totalPotableLiters,
                reqLower, reqUpper, commLower, commUpper,
                qLps, qM3h, dnDis, diDisMm, velDis, dnSuc, diSucMm, velSuc,
                dhSuc, dhDis, hGeo, amt, npshDisp, calcCv, pumpSpec, alerts);

            return new ReservoirPumpingResult(
                dailyLiters,
                totalPotableLiters,
                reqLower,
                reqUpper,
                commLower,
                commUpper,
                qLps,
                qM3h,
                dnDis,
                diDisMm,
                velDis,
                dnSuc,
                diSucMm,
                velSuc,
                dhSuc,
                dhDis,
                hGeo,
                amt,
                npshDisp,
                calcCv,
                selectedCv,
                pumpSpec,
                alerts,
                memorial);
        }

        private static int SelectCommercialTank(double requiredLiters)
        {
            foreach (int size in CommercialTankSizes)
            {
                if (size >= requiredLiters) return size;
            }
            return (int)(Math.Ceiling(requiredLiters / 5000.0) * 5000);
        }

        private static double SelectCommercialMotorCv(double requiredCv)
        {
            foreach (double cv in CommercialMotorCv)
            {
                if (cv >= requiredCv) return cv;
            }
            return Math.Ceiling(requiredCv / 5.0) * 5.0;
        }

        private static string BuildMemorialText(
            ReservoirPumpingInput input,
            int occupants,
            double perCapita,
            double dailyL,
            double totalPotableL,
            double reqLower,
            double reqUpper,
            int commLower,
            int commUpper,
            double qLps,
            double qM3h,
            int dnDis,
            double diDisMm,
            double velDis,
            int dnSuc,
            double diSucMm,
            double velSuc,
            double dhSuc,
            double dhDis,
            double hGeo,
            double amt,
            double npshDisp,
            double calcCv,
            string pumpSpec,
            List<string> alerts)
        {
            var pt = new CultureInfo("pt-BR");
            var sb = new StringBuilder();
            sb.AppendLine("MEMORIAL DE RESERVATÓRIOS, RECALQUE E BOMBEAMENTO (ABNT NBR 5626)");
            sb.AppendLine(new string('=', 68));
            sb.AppendLine($"• Tipologia / População   : {input.Typology} ({occupants} pessoas × {perCapita:F0} L/dia)");
            sb.AppendLine($"• Consumo Diário Predial  : {dailyL.ToString("N0", pt)} L/dia");
            sb.AppendLine($"• Reserva Potável ({input.AutonomyDays:F1}d) : {totalPotableL.ToString("N0", pt)} L");
            sb.AppendLine();
            sb.AppendLine("1. RESERVAÇÃO INFERIOR E SUPERIOR (ABNT NBR 5626)");
            sb.AppendLine($"   - Reservatório Inferior ({100.0 - input.UpperReservoirSharePercent:F0}%) : Calculado = {reqLower.ToString("N0", pt)} L  →  Comercial = {commLower.ToString("N0", pt)} L");
            sb.AppendLine($"   - Reservatório Superior ({input.UpperReservoirSharePercent:F0}% + RTI {input.FireReserveLiters:N0} L): Calculado = {reqUpper.ToString("N0", pt)} L  →  Comercial = {commUpper.ToString("N0", pt)} L");
            sb.AppendLine();
            sb.AppendLine("2. TUBULAÇÕES DE SUCÇÃO E RECALQUE (BRESSE / HAZEN-WILLIAMS)");
            sb.AppendLine($"   - Vazão de Recalque ({input.PumpOperatingHoursPerDay:F1} h/dia) : {qLps.ToString("N3", pt)} L/s  ({qM3h.ToString("N2", pt)} m³/h)");
            sb.AppendLine($"   - Tubulação de Sucção      : DN {dnSuc} mm (DI {diSucMm:F1} mm) | Vel = {velSuc:F2} m/s | ΔH_suc = {dhSuc:F2} m.c.a.");
            sb.AppendLine($"   - Tubulação de Recalque    : DN {dnDis} mm (DI {diDisMm:F1} mm) | Vel = {velDis:F2} m/s | ΔH_rec = {dhDis:F2} m.c.a.");
            sb.AppendLine();
            sb.AppendLine("3. ALTURA MANOMÉTRICA TOTAL (AMT) E SELEÇÃO DO CONJUNTO MOTOBOMBA");
            sb.AppendLine($"   - Desnível Geométrico (H_g): {hGeo:F2} m (Recalque {input.DischargeGeometricHeightMeters:F2} m | Sucção {input.SuctionHeightMeters:+0.00;-0.00;0.00} m)");
            sb.AppendLine($"   - Altura Manométrica (AMT) : {amt:F2} m.c.a.");
            sb.AppendLine($"   - NPSH Disponível          : {npshDisp:F2} m.c.a.");
            sb.AppendLine($"   - Potência Calculada (η={input.PumpEfficiencyPercent:F0}%): {calcCv:F2} CV");
            sb.AppendLine($"   - Especificação Comercial  : {pumpSpec}");
            sb.AppendLine();
            sb.AppendLine("4. RECOMENDAÇÕES EXECUTIVAS E NORMATIVAS");
            foreach (var a in alerts)
                sb.AppendLine($"   * {a}");

            return sb.ToString();
        }
    }
}
