
using System;
using System.Linq;
using System.Threading.Tasks;
using BahrHidro.Core;

namespace BahrHidro.Core.Catalog
{
    public sealed record PipeCatalogSelectionResult(
        PipeProduct Product,
        double MinimumRequiredDiameterMm,
        double ActualVelocityMetersPerSecond
    );

    public sealed class PipeCatalogDiameterSelector
    {
        private readonly PipeCatalogService _catalogService;

        public PipeCatalogDiameterSelector(
            PipeCatalogService catalogService)
        {
            _catalogService = catalogService ??
                throw new ArgumentNullException(nameof(catalogService));
        }

        public async Task<PipeCatalogSelectionResult?> SelectAsync(
            double flowLitersPerSecond,
            double maximumVelocityMetersPerSecond = 2.0)
        {
            if (!double.IsFinite(flowLitersPerSecond) ||
                flowLitersPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(flowLitersPerSecond),
                    "A vazao deve ser maior que zero.");
            }

            if (!double.IsFinite(maximumVelocityMetersPerSecond) ||
                maximumVelocityMetersPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumVelocityMetersPerSecond),
                    "A velocidade maxima deve ser maior que zero.");
            }

            // Calcula o diametro interno minimo necessario.
            double minimumDiameterMm =
                PipeSizing.CalculateMinimumDiameter(
                    flowLitersPerSecond,
                    maximumVelocityMetersPerSecond);

            // Carrega os produtos cadastrados.
            var products = await _catalogService.GetAllAsync();

            // Procura o menor diametro interno que atende ao calculo.
            var selectedProduct = products
                .Where(product => product.InternalDiameterMm > 0)
                .Where(product =>
                    (double)product.InternalDiameterMm >= minimumDiameterMm)
                .OrderBy(product => product.InternalDiameterMm)
                .FirstOrDefault();

            // Nenhum produto do catalogo atende ao criterio.
            if (selectedProduct == null)
                return null;

            // Calcula a velocidade real no tubo selecionado.
            double actualVelocity =
                PipeDiameterSelector.CalculateActualVelocity(
                    flowLitersPerSecond,
                    (double)selectedProduct.InternalDiameterMm);

            return new PipeCatalogSelectionResult(
                selectedProduct,
                minimumDiameterMm,
                actualVelocity);
        }
    }
}
