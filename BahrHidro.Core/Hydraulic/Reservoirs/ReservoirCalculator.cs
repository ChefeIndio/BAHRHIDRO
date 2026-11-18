using System;

namespace BahrHidro.Core.Hydraulic.Reservoirs
{
    public enum BuildingCategory
    {
        Residencia,
        Apartamento,
        Escritorio,
        Hotel,
        Hospital,
        Escola
    }

    public sealed record ReservoirSizingResult(
        double TotalDailyConsumptionLiters,
        double TotalRequiredVolumeLiters,
        double LowerReservoirVolumeLiters,   // Cisterna (60%)
        double UpperReservoirVolumeLiters,   // Reservatório Superior (40%)
        double FireReserveVolumeLiters
    );

    public static class ReservoirCalculator
    {
        public static ReservoirSizingResult Calculate(
            BuildingCategory category,
            int population,
            int reserveDays = 1,
            double fireReserveLiters = 0,
            double lowerReservoirPercentage = 0.60) // Padrão: 60% em baixo, 40% em cima
        {
            if (population <= 0) throw new ArgumentException("A população deve ser maior que zero.");
            if (reserveDays <= 0) throw new ArgumentException("Os dias de reserva devem ser de pelo menos 1.");
            if (fireReserveLiters < 0) throw new ArgumentException("A reserva de incêndio não pode ser negativa.");
            if (lowerReservoirPercentage < 0 || lowerReservoirPercentage > 1) 
                throw new ArgumentException("A percentagem da cisterna deve estar entre 0 e 1.");

            // 1. Determina o consumo per capita (Litros/Pessoa/Dia) baseado em dados normativos típicos
            double perCapitaConsumption = category switch
            {
                BuildingCategory.Residencia => 150,
                BuildingCategory.Apartamento => 200,
                BuildingCategory.Escritorio => 50,
                BuildingCategory.Hotel => 250,
                BuildingCategory.Hospital => 300,
                BuildingCategory.Escola => 50,
                _ => 150
            };

            // 2. Cálculo do Consumo Diário e Volume Total Necessário
            double dailyConsumption = population * perCapitaConsumption;
            
            // O volume total é o consumo diário multiplicado pelos dias de segurança + reserva de incêndio (se houver)
            double totalVolume = (dailyConsumption * reserveDays) + fireReserveLiters;

            // 3. Distribuição do volume (Inferior vs Superior)
            // Se for uma casa simples (sem reserva de incêndio ou cisterna), tudo pode ir para cima (percentagem = 0)
            double lowerVolume = totalVolume * lowerReservoirPercentage;
            double upperVolume = totalVolume - lowerVolume;

            return new ReservoirSizingResult(
                dailyConsumption,
                totalVolume,
                lowerVolume,
                upperVolume,
                fireReserveLiters
            );
        }
    }
}