using System;
using System.Linq;

namespace BahrHidro.Core.Hydraulic.Network
{
    public sealed record PumpSizingResult(
        bool RequiresPump,
        double RequiredAmtMca,
        double TotalFlowLitersPerSecond,
        double EstimatedPowerCv,
        string CriticalNodeName,
        double CriticalNodeCurrentPressure
    );

    public static class PumpCalculator
    {
        public static PumpSizingResult Calculate(
            NetworkCalculationResult networkResult, 
            double requiredMinimumPressureMca = 1.0, 
            double pumpEfficiency = 0.60) // 60% de rendimento padrão
        {
            if (networkResult == null || !networkResult.Nodes.Any())
                throw new ArgumentNullException(nameof(networkResult));

            // Exclui o reservatório da análise (queremos saber a pressão nas pontas/consumo)
            var consumptionNodes = networkResult.Nodes.Where(n => n.NodeType != HydraulicNodeType.Reservoir).ToList();
            
            if (!consumptionNodes.Any())
                return new PumpSizingResult(false, 0, 0, 0, "Nenhum", 0);

            // Encontra o nó na pior situação (menor pressão disponível)
            var criticalNode = consumptionNodes.OrderBy(n => n.AvailablePressureMca).First();

            bool requiresPump = criticalNode.AvailablePressureMca < requiredMinimumPressureMca;
            double amt = 0;
            double powerCv = 0;
            
            // A vazão total do sistema para dimensionar a bomba é a maior vazão orquestrada pela rede principal
            double totalFlow = networkResult.Segments.Any() ? networkResult.Segments.Max(s => s.FlowLitersPerSecond) : 0;

            if (requiresPump)
            {
                // AMT = Pressão que falta para atingir o mínimo normativo no pior nó
                amt = requiredMinimumPressureMca - criticalNode.AvailablePressureMca;
                
                // Fórmula da Potência em CV: P(cv) = (Q(L/s) * AMT(m.c.a)) / (75 * rendimento)
                powerCv = (totalFlow * amt) / (75.0 * pumpEfficiency);
            }

            return new PumpSizingResult(
                requiresPump,
                amt,
                totalFlow,
                powerCv,
                criticalNode.NodeName,
                criticalNode.AvailablePressureMca
            );
        }
    }
}
