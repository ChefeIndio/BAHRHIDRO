
using System;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed class HydraulicPipeSegment
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public string Name { get; init; } = "";

        // Identificação dos nós conectados
        public Guid StartNodeId { get; init; }

        public Guid EndNodeId { get; init; }

        // Características da tubulação
        public double LengthMeters { get; init; }

        public double InternalDiameterMm { get; init; }

        public double AbsoluteRoughnessMm { get; init; }

        // Vazão que percorre o trecho, em litros por segundo
        public double? FlowLitersPerSecond { get; init; }

        // Soma dos coeficientes K dos acessórios
        public double TotalLossCoefficientK { get; init; }

        /// <summary>
        /// Valida os dados básicos do trecho.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new ArgumentException(
                    "O nome do trecho não foi informado.");

            if (StartNodeId == Guid.Empty ||
                EndNodeId == Guid.Empty)
                throw new ArgumentException(
                    "Os nós inicial e final devem ser informados.");

            if (StartNodeId == EndNodeId)
                throw new ArgumentException(
                    "O trecho não pode conectar um nó a ele mesmo.");

            if (!double.IsFinite(LengthMeters) ||
                LengthMeters <= 0)
                throw new ArgumentException(
                    "O comprimento deve ser positivo.");

            if (!double.IsFinite(InternalDiameterMm) ||
                InternalDiameterMm <= 0)
                throw new ArgumentException(
                    "O diâmetro interno deve ser positivo.");

            if (!double.IsFinite(AbsoluteRoughnessMm) ||
                AbsoluteRoughnessMm < 0)
                throw new ArgumentException(
                    "A rugosidade não pode ser negativa.");

            if (FlowLitersPerSecond.HasValue &&
                (!double.IsFinite(FlowLitersPerSecond.Value) ||
                 FlowLitersPerSecond.Value < 0))
                throw new ArgumentException(
                    "A vazão deve ser positiva ou zero.");

            if (!double.IsFinite(TotalLossCoefficientK) ||
                TotalLossCoefficientK < 0)
                throw new ArgumentException(
                    "O coeficiente K deve ser positivo ou zero.");
        }
    }
}
