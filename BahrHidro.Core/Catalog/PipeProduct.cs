
using System;
using System.Collections.Generic;

namespace BahrHidro.Core.Catalog
{
    public enum PipeMaterial
    {
        PvcSoldavel,
        PvcRoscavel,
        Ppr
    }

    public sealed class PipeProduct
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Manufacturer { get; init; } = "";

        public string ProductLine { get; init; } = "";

        public PipeMaterial Material { get; init; }

        // Exemplo: DN 20, 1/2", DN 25
        public string NominalDesignation { get; init; } = "";

        // Dimensoes em milimetros
        public decimal OuterDiameterMm { get; init; }

        public decimal WallThicknessMm { get; init; }

        public decimal InternalDiameterMm =>
            OuterDiameterMm - (2m * WallThicknessMm);

        // Dados de pressao e temperatura
        public string PressureClass { get; init; } = "";

        public decimal? MaximumOperatingPressureBar { get; init; }

        public string StandardReference { get; init; } = "";

        // Rastreabilidade da informacao
        public string SourceDescription { get; init; } = "";

        public string SourceUrl { get; init; } = "";

        public List<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Manufacturer))
                errors.Add("Fabricante nao informado.");

            if (string.IsNullOrWhiteSpace(ProductLine))
                errors.Add("Linha comercial nao informada.");

            if (string.IsNullOrWhiteSpace(NominalDesignation))
                errors.Add("Designacao nominal nao informada.");

            if (OuterDiameterMm <= 0)
                errors.Add("Diametro externo invalido.");

            if (WallThicknessMm <= 0)
                errors.Add("Espessura da parede invalida.");

            if (WallThicknessMm * 2m >= OuterDiameterMm)
                errors.Add("Diametro interno invalido.");

            if (MaximumOperatingPressureBar.HasValue &&
                MaximumOperatingPressureBar.Value <= 0)
                errors.Add("Pressao de operacao invalida.");

            if (string.IsNullOrWhiteSpace(StandardReference))
                errors.Add("Referencia normativa nao informada.");

            if (string.IsNullOrWhiteSpace(SourceDescription))
                errors.Add("Fonte tecnica nao informada.");

            return errors;
        }
    }
}
