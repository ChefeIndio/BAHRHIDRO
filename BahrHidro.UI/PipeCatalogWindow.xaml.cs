
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BahrHidro.Core.Catalog;

namespace BahrHidro.UI
{
    public partial class PipeCatalogWindow : Window
    {
        private readonly PipeCatalogService _catalogService;
        private List<PipeProduct> _products = new();

        public PipeCatalogWindow()
        {
            InitializeComponent();
            UiNavigationController.Initialize();

            string catalogPath = System.IO.Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "BahrHidro",
                "pipe-catalog.json");

            var repository =
                new PipeCatalogRepository(catalogPath);

            _catalogService =
                new PipeCatalogService(repository);

            Loaded += PipeCatalogWindow_Loaded;

            SearchBox.TextChanged += Filter_Changed;
            MaterialFilter.SelectionChanged += Filter_Changed;
        }

        private async void PipeCatalogWindow_Loaded(
            object sender, RoutedEventArgs e)
        {
            await LoadCatalogAsync();
        }

        private async Task LoadCatalogAsync()
        {
            try
            {
                _products = await _catalogService.GetAllAsync();
                ApplyFilters();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro ao carregar catálogo:\n{ex.Message}",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Filter_Changed(
            object sender, RoutedEventArgs e)
        {
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            string search = SearchBox.Text?.Trim() ?? "";

            string material =
                (MaterialFilter.SelectedItem as ComboBoxItem)
                ?.Content?.ToString() ?? "Todos";

            IEnumerable<PipeProduct> filtered = _products;

            if (!string.IsNullOrWhiteSpace(search))
            {
                filtered = filtered.Where(p =>
                    p.Manufacturer.Contains(
                        search, StringComparison.OrdinalIgnoreCase) ||
                    p.ProductLine.Contains(
                        search, StringComparison.OrdinalIgnoreCase) ||
                    p.NominalDesignation.Contains(
                        search, StringComparison.OrdinalIgnoreCase));
            }

            if (material == "PVC Soldável")
                filtered = filtered.Where(
                    p => p.Material == PipeMaterial.PvcSoldavel);
            else if (material == "PVC Roscável")
                filtered = filtered.Where(
                    p => p.Material == PipeMaterial.PvcRoscavel);
            else if (material == "PPR")
                filtered = filtered.Where(
                    p => p.Material == PipeMaterial.Ppr);

            PipeDataGrid.ItemsSource = filtered.ToList();
        }

        private async void NewProductButton_Click(
            object sender, RoutedEventArgs e)
        {
            var form = new PipeProductForm
            {
                Owner = this
            };

            bool? result = form.ShowDialog();

            if (result == true)
                await LoadCatalogAsync();
        }

        private async void EditProduct_Click(
            object sender, RoutedEventArgs e)
        {
            if (sender is not Button button ||
                !Guid.TryParse(button.Tag?.ToString(), out Guid productId))
                return;

            var product = _products.FirstOrDefault(
                p => p.Id == productId);

            if (product == null)
                return;

            var form = new PipeProductForm(product)
            {
                Owner = this
            };

            bool? result = form.ShowDialog();

            if (result == true)
                await LoadCatalogAsync();
        }

        private async void DeleteProduct_Click(
            object sender, RoutedEventArgs e)
        {
            if (sender is not Button button ||
                !Guid.TryParse(button.Tag?.ToString(), out Guid productId))
                return;

            var product = _products.FirstOrDefault(
                p => p.Id == productId);

            if (product == null)
                return;

            MessageBoxResult confirmation = MessageBox.Show(
                $"Deseja realmente excluir este produto?\n\n" +
                $"{product.Manufacturer} - {product.ProductLine}\n" +
                $"{product.NominalDesignation}",
                "Confirmar exclusão",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.Yes)
                return;

            try
            {
                await _catalogService.DeleteAsync(productId);

                await LoadCatalogAsync();

                MessageBox.Show(
                    "Produto excluído com sucesso.",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro ao excluir produto:\n{ex.Message}",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
