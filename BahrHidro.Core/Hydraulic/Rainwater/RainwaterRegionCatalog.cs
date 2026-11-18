using System;
using System.Collections.Generic;

namespace BahrHidro.Core.Hydraulic.Rainwater
{
    public enum RainwaterReturnPeriod
    {
        OneYear = 1,       // Áreas pavimentadas onde empoçamentos são tolerados
        FiveYears = 5,     // Coberturas e telhados (padrão NBR 10844)
        TwentyFiveYears = 25 // Coberturas onde extravasamento não pode ser tolerado
    }

    public class RainwaterRegion
    {
        public string City { get; init; } = "";
        public string State { get; init; } = "";
        public double Intensity5YearsMmPerHour { get; init; }
        public bool IsCustom { get; init; }

        public string DisplayName => IsCustom
            ? $"{City} (Digitar índice manual)"
            : $"{City} - {State} ({Intensity5YearsMmPerHour:F0} mm/h)";
    }

    public static class RainwaterRegionCatalog
    {
        public static List<RainwaterRegion> GetRegions()
        {
            return new List<RainwaterRegion>
            {
                new RainwaterRegion { City = "Personalizado / Outra Cidade", State = "BR", Intensity5YearsMmPerHour = 150, IsCustom = true },
                new RainwaterRegion { City = "Aracaju", State = "SE", Intensity5YearsMmPerHour = 138 },
                new RainwaterRegion { City = "Belém", State = "PA", Intensity5YearsMmPerHour = 180 },
                new RainwaterRegion { City = "Belo Horizonte", State = "MG", Intensity5YearsMmPerHour = 162 },
                new RainwaterRegion { City = "Boa Vista", State = "RR", Intensity5YearsMmPerHour = 168 },
                new RainwaterRegion { City = "Brasília", State = "DF", Intensity5YearsMmPerHour = 156 },
                new RainwaterRegion { City = "Campinas", State = "SP", Intensity5YearsMmPerHour = 150 },
                new RainwaterRegion { City = "Campo Grande", State = "MS", Intensity5YearsMmPerHour = 168 },
                new RainwaterRegion { City = "Cuiabá", State = "MT", Intensity5YearsMmPerHour = 174 },
                new RainwaterRegion { City = "Curitiba", State = "PR", Intensity5YearsMmPerHour = 146 },
                new RainwaterRegion { City = "Florianópolis", State = "SC", Intensity5YearsMmPerHour = 144 },
                new RainwaterRegion { City = "Fortaleza", State = "CE", Intensity5YearsMmPerHour = 156 },
                new RainwaterRegion { City = "Goiânia", State = "GO", Intensity5YearsMmPerHour = 168 },
                new RainwaterRegion { City = "João Pessoa", State = "PB", Intensity5YearsMmPerHour = 132 },
                new RainwaterRegion { City = "Londrina", State = "PR", Intensity5YearsMmPerHour = 156 },
                new RainwaterRegion { City = "Macapá", State = "AP", Intensity5YearsMmPerHour = 174 },
                new RainwaterRegion { City = "Maceió", State = "AL", Intensity5YearsMmPerHour = 138 },
                new RainwaterRegion { City = "Manaus", State = "AM", Intensity5YearsMmPerHour = 186 },
                new RainwaterRegion { City = "Maringá", State = "PR", Intensity5YearsMmPerHour = 156 },
                new RainwaterRegion { City = "Natal", State = "RN", Intensity5YearsMmPerHour = 132 },
                new RainwaterRegion { City = "Palmas", State = "TO", Intensity5YearsMmPerHour = 168 },
                new RainwaterRegion { City = "Ponta Grossa", State = "PR", Intensity5YearsMmPerHour = 148 },
                new RainwaterRegion { City = "Porto Alegre", State = "RS", Intensity5YearsMmPerHour = 132 },
                new RainwaterRegion { City = "Porto Velho", State = "RO", Intensity5YearsMmPerHour = 174 },
                new RainwaterRegion { City = "Recife", State = "PE", Intensity5YearsMmPerHour = 138 },
                new RainwaterRegion { City = "Ribeirão Preto", State = "SP", Intensity5YearsMmPerHour = 156 },
                new RainwaterRegion { City = "Rio Branco", State = "AC", Intensity5YearsMmPerHour = 174 },
                new RainwaterRegion { City = "Rio de Janeiro", State = "RJ", Intensity5YearsMmPerHour = 155 },
                new RainwaterRegion { City = "Salvador", State = "BA", Intensity5YearsMmPerHour = 144 },
                new RainwaterRegion { City = "São Luís", State = "MA", Intensity5YearsMmPerHour = 162 },
                new RainwaterRegion { City = "São Paulo", State = "SP", Intensity5YearsMmPerHour = 147 },
                new RainwaterRegion { City = "Teresina", State = "PI", Intensity5YearsMmPerHour = 162 },
                new RainwaterRegion { City = "Vitória", State = "ES", Intensity5YearsMmPerHour = 144 }
            };
        }

        public static double AdjustIntensityForReturnPeriod(double intensity5Years, RainwaterReturnPeriod period)
        {
            return period switch
            {
                RainwaterReturnPeriod.OneYear => Math.Round(intensity5Years * 0.72, 1),
                RainwaterReturnPeriod.TwentyFiveYears => Math.Round(intensity5Years * 1.32, 1),
                _ => intensity5Years
            };
        }
    }
}