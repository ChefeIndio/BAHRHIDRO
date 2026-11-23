using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BahrHidro.Core.Hydraulic.HotWater
{
    public enum BuildingHotWaterTypology
    {
        ResidencialUnifamiliar,
        ResidencialMultifamiliar,
        HotelPousada,
        AcademiaVestiario,
        HospitalClinica
    }

    public enum BrazilianSolarRegion
    {
        Sul,
        Sudeste,
        CentroOeste,
        Nordeste,
        Norte
    }

    public sealed record HotWaterThermalInput(
        BuildingHotWaterTypology Typology,
        int OccupantsCount,
        double ColdWaterTempC,
        double StorageTempC,
        double UsageTempC,
        int SimultaneousShowers,
        int SimultaneousLavatories,
        int SimultaneousKitchenSinks,
        int SimultaneousBathtubs,
        BrazilianSolarRegion SolarRegion,
        double SolarFractionPercent,
        bool VacuumTubeCollector,
        double LongestPipeRunMeters);

    public sealed record HotWaterThermalResult(
        double DailyConsumption60CLiters,
        double DailyThermalEnergyKwh,
        int RecommendedBoilerLiters,
        double ElectricBackupPowerKw,
        double SimultaneousFlow40CLpm,
        double EquivalentFlowDelta20Lpm,
        double RequiredGasPowerKcalH,
        string RecommendedGasHeaterModel,
        int FlueDuctDiameterMm,
        double SolarIrradiationKwhM2Day,
        double RequiredCollectorAreaM2,
        int CollectorPlates2M2Count,
        int RecommendedSolarBoilerLiters,
        double LinearExpansionPprMm,
        List<string> NormativeAlerts,
        string TechnicalMemorialText);

    public static class HotWaterThermalSizingCalculator
    {
        private static readonly int[] CommercialBoilerSizes =
        {
            100, 150, 200, 250, 300, 400, 500, 600, 800, 1000, 1500, 2000, 2500, 3000, 4000, 5000
        };

        private static readonly int[] CommercialGasHeaterLpm =
        {
            15, 20, 25, 32, 35, 42
        };

        public static HotWaterThermalResult Calculate(HotWaterThermalInput input)
        {
            int occupants = Math.Max(1, input.OccupantsCount);
            double tAf = Math.Clamp(input.ColdWaterTempC, 5.0, 32.0);
            double tArm = Math.Clamp(input.StorageTempC, 50.0, 75.0);
            double tUso = Math.Clamp(input.UsageTempC, 35.0, 50.0);

            // 1. Consumo per capita a 60 °C conforme tipologia (ABNT NBR 7198)
            double perCapita60L = input.Typology switch
            {
                BuildingHotWaterTypology.ResidencialUnifamiliar => 45.0,
                BuildingHotWaterTypology.ResidencialMultifamiliar => 50.0,
                BuildingHotWaterTypology.HotelPousada => 80.0,
                BuildingHotWaterTypology.AcademiaVestiario => 35.0,
                BuildingHotWaterTypology.HospitalClinica => 110.0,
                _ => 45.0
            };

            // Ajuste térmico caso T_arm != 60 °C mantendo a mesma energia útil
            double deltaRef = Math.Max(10.0, 60.0 - tAf);
            double deltaArm = Math.Max(15.0, tArm - tAf);
            double dailyVolumeArmLiters = Math.Round(occupants * perCapita60L * (deltaRef / deltaArm), 1);

            // Energia térmica diária necessária (1,162 Wh / L.°C)
            double dailyEnergyKwh = Math.Round((dailyVolumeArmLiters * 1.162 * deltaArm) / 1000.0, 2);

            // 2. Dimensionamento do Boiler (NBR 7198 — fator de estratificação/segurança 1,15)
            double minBoilerVol = dailyVolumeArmLiters * 1.15;
            int boilerCommercial = SelectCommercialBoiler(minBoilerVol);
            // Resistência elétrica de apoio dimensionada para recuperação total em 3,5 horas
            double electricBackupKw = Math.Round(dailyEnergyKwh / 3.5, 2);

            // 3. Aquecedor de Passagem a Gás (NBR 7198 — vazão simultânea a 40 °C -> ΔT = 20 °C)
            double qShowers = Math.Max(0, input.SimultaneousShowers) * 9.0;      // 9,0 L/min por ducha
            double qLavs = Math.Max(0, input.SimultaneousLavatories) * 4.0;      // 4,0 L/min por lavatório
            double qKitchen = Math.Max(0, input.SimultaneousKitchenSinks) * 6.0; // 6,0 L/min por pia
            double qTubs = Math.Max(0, input.SimultaneousBathtubs) * 15.0;       // 15,0 L/min por banheira

            double rawFlow40 = qShowers + qLavs + qKitchen + qTubs;
            if (rawFlow40 <= 0) rawFlow40 = 9.0; // Mínimo 1 ducha

            int totalActivePoints = Math.Max(1,
                input.SimultaneousShowers + input.SimultaneousLavatories +
                input.SimultaneousKitchenSinks + input.SimultaneousBathtubs);

            // Fator de simultaneidade para múltiplos pontos simultâneos
            double simFactor = totalActivePoints <= 2 ? 1.0 : Math.Max(0.75, 1.0 - (totalActivePoints - 2) * 0.05);
            double q40 = Math.Round(rawFlow40 * simFactor, 2);

            double deltaUso = Math.Max(10.0, tUso - tAf);
            // Vazão equivalente no padrão de ensaio brasileiro (ΔT = 20 °C)
            double q20 = Math.Round(q40 * (deltaUso / 20.0), 2);

            // Potência térmica do aquecedor a gás (rendimento médio 84%)
            double gasEfficiency = 0.84;
            double gasPowerKcalH = Math.Round((q40 * 60.0 * deltaUso) / gasEfficiency, 0);

            string gasModel;
            int flueDiaMm;
            int? singleUnit = CommercialGasHeaterLpm.Cast<int?>().FirstOrDefault(cap => cap >= q20);
            if (singleUnit.HasValue)
            {
                gasModel = $"1x Aquecedor Digital Exaustão Forçada {singleUnit.Value} L/min (ΔT 20°C)";
                flueDiaMm = singleUnit.Value <= 25 ? 60 : 80;
            }
            else
            {
                int units = (int)Math.Ceiling(q20 / 35.0);
                gasModel = $"Sistema em Cascata: {units}x Aquecedores Digitais 35 L/min (Total {units * 35} L/min)";
                flueDiaMm = 100;
            }

            // 4. Aquecimento Solar (ABNT NBR 15569)
            double irradiation = input.SolarRegion switch
            {
                BrazilianSolarRegion.Sul => 4.25,
                BrazilianSolarRegion.Sudeste => 4.85,
                BrazilianSolarRegion.CentroOeste => 5.25,
                BrazilianSolarRegion.Nordeste => 5.65,
                BrazilianSolarRegion.Norte => 5.05,
                _ => 4.85
            };

            double solarFraction = Math.Clamp(input.SolarFractionPercent, 30.0, 95.0) / 100.0;
            double collectorEff = input.VacuumTubeCollector ? 0.68 : 0.54;
            double pipingLossFactor = 1.15; // Perdas térmicas circuito primário (NBR 15569)

            double usefulSolarKwh = dailyEnergyKwh * solarFraction;
            double collectorAreaM2 = Math.Round((usefulSolarKwh * pipingLossFactor) / (irradiation * collectorEff), 2);
            int platesCount = Math.Max(1, (int)Math.Ceiling(collectorAreaM2 / 2.0));
            // Reservatório solar exige margem de autonomia (fator 1,25 sobre demanda diária — NBR 15569)
            int solarBoilerLiters = SelectCommercialBoiler(dailyVolumeArmLiters * 1.25);

            // 5. Dilatação Térmica Linear em PPR PN20/PN25 (α = 0,15 mm/m.°C — NBR 7198)
            double runM = Math.Max(0.0, input.LongestPipeRunMeters);
            double expansionMm = Math.Round(runM * 0.15 * deltaArm, 1);

            // 6. Alertas Normativos (NBR 7198 / NBR 15569)
            var alerts = new List<string>();
            if (tArm < 60.0)
                alerts.Add("NBR 7198: Temperatura de armazenamento abaixo de 60 °C aumenta risco de proliferação de Legionella.");
            if (q20 > 15.0)
                alerts.Add("NBR 7198: Aquecedor de passagem exige pressão dinâmica mínima de 10,0 m.c.a. na entrada de água fria.");
            if (expansionMm >= 12.0)
                alerts.Add($"NBR 7198: Dilatação térmica estimada de {expansionMm:F1} mm no trecho de {runM:F1} m — prever lira de dilatação ou ponto fixo/deslizante.");
            alerts.Add("NBR 15569: Instalar válvula eliminadora de ar no ponto mais alto dos coletores e válvula de alívio T&P no boiler.");
            alerts.Add("NBR 7198: Não interligar o respiro do reservatório térmico ao barrilete de água fria.");

            string memorial = BuildMemorialText(
                input, occupants, tAf, tArm, tUso,
                dailyVolumeArmLiters, dailyEnergyKwh, boilerCommercial, electricBackupKw,
                q40, q20, gasPowerKcalH, gasModel, flueDiaMm,
                irradiation, collectorAreaM2, platesCount, solarBoilerLiters, expansionMm, alerts);

            return new HotWaterThermalResult(
                dailyVolumeArmLiters,
                dailyEnergyKwh,
                boilerCommercial,
                electricBackupKw,
                q40,
                q20,
                gasPowerKcalH,
                gasModel,
                flueDiaMm,
                irradiation,
                collectorAreaM2,
                platesCount,
                solarBoilerLiters,
                expansionMm,
                alerts,
                memorial);
        }

        private static int SelectCommercialBoiler(double requiredLiters)
        {
            foreach (int size in CommercialBoilerSizes)
            {
                if (size >= requiredLiters) return size;
            }
            return (int)(Math.Ceiling(requiredLiters / 500.0) * 500);
        }

        private static string BuildMemorialText(
            HotWaterThermalInput input,
            int occupants,
            double tAf,
            double tArm,
            double tUso,
            double volLiters,
            double energyKwh,
            int boilerL,
            double elecKw,
            double q40,
            double q20,
            double gasKcal,
            string gasModel,
            int flueMm,
            double irrad,
            double areaM2,
            int plates,
            int solarBoilerL,
            double expansionMm,
            List<string> alerts)
        {
            var pt = new CultureInfo("pt-BR");
            var sb = new StringBuilder();
            sb.AppendLine("MEMORIAL DE DIMENSIONAMENTO TÉRMICO DE ÁGUA QUENTE");
            sb.AppendLine("Normas Técnicas: ABNT NBR 7198 e ABNT NBR 15569");
            sb.AppendLine(new string('=', 66));
            sb.AppendLine($"• Tipologia da Edificação : {input.Typology}");
            sb.AppendLine($"• População Atendida      : {occupants} pessoa(s)");
            sb.AppendLine($"• Temperaturas de Projeto : T_fria = {tAf:F1} °C | T_armaz = {tArm:F1} °C | T_uso = {tUso:F1} °C");
            sb.AppendLine($"• Demanda Diária (T_armaz): {volLiters.ToString("N1", pt)} L/dia");
            sb.AppendLine($"• Energia Térmica Diária  : {energyKwh.ToString("N2", pt)} kWh/dia");
            sb.AppendLine();
            sb.AppendLine("1. RESERVATÓRIO TÉRMICO (BOILER — NBR 7198)");
            sb.AppendLine($"   - Volume Comercial Recomendado : {boilerL} Litros (Alta ou Baixa Pressão conforme altura da caixa)");
            sb.AppendLine($"   - Apoio Elétrico (Recup. 3,5h) : {elecKw.ToString("N2", pt)} kW (220V / 380V)");
            sb.AppendLine();
            sb.AppendLine("2. AQUECEDOR DE PASSAGEM A GÁS (NBR 7198)");
            sb.AppendLine($"   - Vazão Simultânea a {tUso:F0} °C     : {q40.ToString("N2", pt)} L/min");
            sb.AppendLine($"   - Vazão Nominal (ΔT = 20 °C)   : {q20.ToString("N2", pt)} L/min");
            sb.AppendLine($"   - Potência Térmica Requerida   : {gasKcal.ToString("N0", pt)} kcal/h");
            sb.AppendLine($"   - Equipamento Especificado     : {gasModel}");
            sb.AppendLine($"   - Duto de Exaustão (Chaminé)   : Ø {flueMm} mm");
            sb.AppendLine();
            sb.AppendLine("3. AQUECIMENTO SOLAR (ABNT NBR 15569)");
            sb.AppendLine($"   - Região / Irradiação Média    : {input.SolarRegion} ({irrad.ToString("N2", pt)} kWh/m².dia)");
            sb.AppendLine($"   - Fração Solar de Projeto      : {input.SolarFractionPercent:F0}% ({(input.VacuumTubeCollector ? "Tubo a Vácuo" : "Placa Plana Fechada")})");
            sb.AppendLine($"   - Área Coletora Necessária     : {areaM2.ToString("N2", pt)} m² ({plates} placa(s) padrão de 2,00 m²)");
            sb.AppendLine($"   - Boiler Solar c/ Autonomia    : {solarBoilerL} Litros");
            sb.AppendLine($"   - Orientação & Inclinação      : Face Norte Verdadeiro | Inclinação = Latitude + 10°");
            sb.AppendLine();
            sb.AppendLine("4. DILATAÇÃO TÉRMICA E RECOMENDAÇÕES EXECUTIVAS");
            sb.AppendLine($"   - Dilatação Linear PPR ({input.LongestPipeRunMeters:F1} m) : {expansionMm.ToString("N1", pt)} mm");
            foreach (var a in alerts)
                sb.AppendLine($"   * {a}");

            return sb.ToString();
        }
    }
}
