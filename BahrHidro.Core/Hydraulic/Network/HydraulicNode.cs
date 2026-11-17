
using System;

namespace BahrHidro.Core.Hydraulic.Network
{
    public enum HydraulicNodeType
    {
        Junction,
        Reservoir,
        ConsumptionPoint
    }

    public sealed class HydraulicNode
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public string Name { get; init; } = "";

        public double ElevationMeters { get; init; }

        public HydraulicNodeType Type { get; init; }

        // Demanda de agua do no, em litros por segundo
        public double DemandLitersPerSecond { get; init; } = 0.0;

        // Alimentacao externa fornecida ao no, em litros por segundo
        public double ExternalSupplyLitersPerSecond { get; init; } = 0.0;

        /// <summary>
        /// Valida os dados básicos do nó hidráulico.
        /// Elevação, demanda e alimentação em suas respectivas unidades.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new ArgumentException(
                    "O nome do nó não foi informado.");

            if (!double.IsFinite(ElevationMeters))
                throw new ArgumentException(
                    "A elevação do nó deve ser um valor finito.");

            if (!Enum.IsDefined(Type))
                throw new ArgumentException(
                    "O tipo de nó é inválido.");

            if (!double.IsFinite(DemandLitersPerSecond) ||
                DemandLitersPerSecond < 0)
                throw new ArgumentException(
                    "A demanda deve ser positiva ou zero.");

            if (!double.IsFinite(ExternalSupplyLitersPerSecond) ||
                ExternalSupplyLitersPerSecond < 0)
                throw new ArgumentException(
                    "A alimentação externa deve ser positiva ou zero.");
        }
    }
}
