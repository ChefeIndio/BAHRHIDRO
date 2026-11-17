
using System;

namespace BahrHidro.Core.Hydraulic
{
    public sealed record HydraulicSegmentResult(
        double FlowLitersPerSecond,
        double VelocityMetersPerSecond,
        double ReynoldsNumber,
        FlowRegime Regime,
        double FrictionFactor,
        double HeadLossMeters,
        double LocalLossMeters,
        double TotalHeadLossMeters);

    public static class HydraulicSegmentCalculator
    {
        /// <summary>
        /// Calcula os parâmetros hidráulicos de um trecho.
        ///
        /// Vazão: L/s
        /// Comprimento: m
        /// Diâmetro interno: mm
        /// Rugosidade absoluta: mm
        /// Viscosidade cinemática: m²/s
        /// Coeficiente K total: adimensional
        ///
        /// Perdas: m.c.a.
        /// </summary>
        public static HydraulicSegmentResult Calculate(
            double flowLitersPerSecond,
            double lengthMeters,
            double internalDiameterMm,
            double absoluteRoughnessMm,
            double kinematicViscosityM2s,
            double totalLossCoefficientK = 0)
        {
            if (!double.IsFinite(flowLitersPerSecond) ||
                flowLitersPerSecond <= 0)
                throw new ArgumentException(
                    "A vazão deve ser positiva.");

            if (!double.IsFinite(lengthMeters) ||
                lengthMeters < 0)
                throw new ArgumentException(
                    "O comprimento deve ser positivo ou zero.");

            if (!double.IsFinite(internalDiameterMm) ||
                internalDiameterMm <= 0)
                throw new ArgumentException(
                    "O diâmetro interno deve ser positivo.");

            if (!double.IsFinite(absoluteRoughnessMm) ||
                absoluteRoughnessMm < 0)
                throw new ArgumentException(
                    "A rugosidade não pode ser negativa.");

            if (!double.IsFinite(kinematicViscosityM2s) ||
                kinematicViscosityM2s <= 0)
                throw new ArgumentException(
                    "A viscosidade deve ser positiva.");

            if (!double.IsFinite(totalLossCoefficientK) ||
                totalLossCoefficientK < 0)
                throw new ArgumentException(
                    "O coeficiente K total deve ser positivo ou zero.");

            // 1. Calcular velocidade real
            double velocity =
                PipeDiameterSelector.CalculateActualVelocity(
                    flowLitersPerSecond,
                    internalDiameterMm);

            // 2. Calcular Reynolds
            ReynoldsResult reynolds =
                ReynoldsCalculator.Calculate(
                    velocity,
                    internalDiameterMm,
                    kinematicViscosityM2s);

            // 3. Calcular fator de atrito
            double frictionFactor =
                FrictionFactorCalculator.Calculate(
                    reynolds.ReynoldsNumber,
                    internalDiameterMm,
                    absoluteRoughnessMm);

            // 4. Calcular perda distribuída
            double distributedHeadLoss =
                HeadLossCalculator.CalculateDarcyWeisbach(
                    lengthMeters,
                    internalDiameterMm,
                    velocity,
                    frictionFactor);

            // 5. Calcular perda localizada
            double localHeadLoss =
                LocalLossCalculator.CalculateTotal(
                    totalLossCoefficientK,
                    velocity);

            // 6. Somar as perdas
            double totalHeadLoss =
                distributedHeadLoss + localHeadLoss;

            return new HydraulicSegmentResult(
                flowLitersPerSecond,
                velocity,
                reynolds.ReynoldsNumber,
                reynolds.Regime,
                frictionFactor,
                distributedHeadLoss,
                localHeadLoss,
                totalHeadLoss);
        }
    }
}
