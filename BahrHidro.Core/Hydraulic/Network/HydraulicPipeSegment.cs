using System;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed class HydraulicPipeSegment
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; init; } = "";
        public Guid StartNodeId { get; init; }
        public Guid EndNodeId { get; init; }
        public double LengthMeters { get; init; }
        
        // NOVO: Pode ser 'null' indicando que o sistema deve Auto-Dimensionar
        public double? InternalDiameterMm { get; init; } 
        
        public double AbsoluteRoughnessMm { get; init; }
        public double TotalLossCoefficientK { get; init; }
        public double? FlowLitersPerSecond { get; init; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("O nome do trecho não foi informado.");
            if (StartNodeId == Guid.Empty || EndNodeId == Guid.Empty) throw new ArgumentException("Nós de montante ou jusante inválidos.");
            if (StartNodeId == EndNodeId) throw new ArgumentException("O nó não pode ligar a si mesmo.");
            if (!double.IsFinite(LengthMeters) || LengthMeters <= 0) throw new ArgumentException("O comprimento deve ser maior que zero.");
            if (InternalDiameterMm.HasValue && (!double.IsFinite(InternalDiameterMm.Value) || InternalDiameterMm.Value <= 0)) throw new ArgumentException("O diâmetro interno deve ser maior que zero.");
            if (!double.IsFinite(AbsoluteRoughnessMm) || AbsoluteRoughnessMm < 0) throw new ArgumentException("A rugosidade deve ser positiva ou zero.");
            if (!double.IsFinite(TotalLossCoefficientK) || TotalLossCoefficientK < 0) throw new ArgumentException("O coeficiente K deve ser positivo ou zero.");
            if (FlowLitersPerSecond.HasValue && (!double.IsFinite(FlowLitersPerSecond.Value) || FlowLitersPerSecond.Value < 0)) throw new ArgumentException("A vazão fixa deve ser positiva ou zero.");
        }
    }
}