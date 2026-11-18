using System;

namespace BahrHidro.Core.Hydraulic.HotWater
{
    /// <summary>
    /// Tipos de edificação para estimativa de consumo de água quente (baseado na ABNT).
    /// </summary>
    public enum BuildingProfile
    {
        ResidenciaUnifamiliar,
        Apartamento,
        Hotel,
        Hospital,
        Alojamento
    }

    public sealed record BoilerSizingResult(
        double DailyConsumptionLiters,
        double RequiredAccumulationVolumeLiters,
        string RecommendedPressureType
    );

    public static class BoilerCalculator
    {
        /// <summary>
        /// Calcula o volume necessário do boiler e avalia o tipo de equipamento
        /// com base na pressão da água fria que chega até ele.
        /// </summary>
        public static BoilerSizingResult Calculate(
            BuildingProfile profile, 
            int occupants, 
            double coldWaterAvailablePressureMca)
        {
            if (occupants <= 0)
                throw new ArgumentException("O número de ocupantes deve ser maior que zero.");

            // 1. Define o consumo diário per capita de água quente (L/dia) segundo normas
            double litersPerPerson = profile switch
            {
                BuildingProfile.ResidenciaUnifamiliar => 45.0,
                BuildingProfile.Apartamento => 60.0,
                BuildingProfile.Alojamento => 45.0,
                BuildingProfile.Hotel => 60.0,
                BuildingProfile.Hospital => 125.0,
                _ => 45.0
            };

            // 2. Calcula a demanda diária total
            double dailyConsumption = litersPerPerson * occupants;

            // Para sistemas de acumulação padrão, o volume do boiler 
            // costuma atender 100% da demanda diária de água quente.
            double requiredVolume = dailyConsumption;

            // 3. Avaliação da Pressão (Alta vs Baixa)
            string pressureType = EvaluatePressureType(coldWaterAvailablePressureMca);

            return new BoilerSizingResult(
                DailyConsumptionLiters: dailyConsumption,
                RequiredAccumulationVolumeLiters: requiredVolume,
                RecommendedPressureType: pressureType
            );
        }

        private static string EvaluatePressureType(double pressureMca)
        {
            if (pressureMca < 1.0)
                return "PRESSÃO INSUFICIENTE (Requer pressurizador na rede de água fria)";
            
            // Boilers de baixa pressão geralmente suportam até 5 m.c.a.
            if (pressureMca <= 5.0)
                return "BAIXA PRESSÃO (Boiler padrão, alimentado por gravidade)";
            
            // Boilers de alta pressão suportam até 40 m.c.a.
            if (pressureMca <= 40.0)
                return "ALTA PRESSÃO (Exige boiler reforçado de alta pressão)";
            
            return "PRESSÃO EXCESSIVA (Requer Válvula Redutora de Pressão antes do boiler)";
        }
    }
}