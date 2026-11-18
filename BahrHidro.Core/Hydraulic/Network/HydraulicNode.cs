using System;

namespace BahrHidro.Core.Hydraulic.Network
{
    public enum HydraulicNodeType
    {
        Reservoir,
        Junction,
        ConsumptionPoint
    }

    public sealed class HydraulicNode
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; init; } = "";
        public HydraulicNodeType Type { get; init; }
        
        public double ElevationMeters { get; init; }
        
        // Vazão contínua fixa (L/s) - Usada para equipamentos específicos (ex: mangueiras, hidrantes)
        public double DemandLitersPerSecond { get; init; }
        
        // NOVO: Peso Relativo (ABNT NBR 5626) para cálculo de vazão provável
        public double RelativeWeight { get; init; }

        public double ExternalSupplyLitersPerSecond { get; init; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new ArgumentException("O nome do nó não foi informado.");

            if (!double.IsFinite(ElevationMeters))
                throw new ArgumentException("Cota inválida.");

            if (!double.IsFinite(DemandLitersPerSecond) || DemandLitersPerSecond < 0)
                throw new ArgumentException("A demanda contínua deve ser positiva ou zero.");
                
            if (!double.IsFinite(RelativeWeight) || RelativeWeight < 0)
                throw new ArgumentException("O peso relativo não pode ser negativo.");
        }
    }
}