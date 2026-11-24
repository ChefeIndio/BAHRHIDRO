
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BahrHidro.Core.Catalog;

namespace BahrHidro.UI
{
    public partial class PipeProductForm : Window
    {
        private readonly PipeCatalogService _catalogService;
        private readonly PipeProduct? _productBeingEdited;

        private bool IsEditMode => _productBeingEdited != null;

        public PipeProductForm()
        {
            InitializeComponent();
            UiNavigationController.Initialize();

            _catalogService = CreateCatalogService();

            SaveButton.Click += SaveButton_Click;

            OuterDiameterBox.TextChanged += Dimension_Changed;
            WallThicknessBox.TextChanged += Dimension_Changed;
        }

        // CONSTRUTOR PARA EDIÇÃO
        public PipeProductForm(PipeProduct product)
            : this()
        {
            _productBeingEdited = product;

            Title = "Smart Hidro | Editar Produto";
            SaveButton.Content = "ATUALIZAR PRODUTO";

            LoadProductData(product);
        }

        private static PipeCatalogService CreateCatalogService()
        {
            string catalogPath = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "BahrHidro",
                "pipe-catalog.json");

            Directory.CreateDirectory(
                Path.GetDirectoryName(catalogPath)!);

            var repository = new PipeCatalogRepository(catalogPath);

            return new PipeCatalogService(repository);
        }

        // PREENCHER FORMULÁRIO COM DADOS EXISTENTES
        private void LoadProductData(PipeProduct product)
        {
            ManufacturerBox.Text = product.Manufacturer;
            ProductLineBox.Text = product.ProductLine;
            NominalDesignationBox.Text = product.NominalDesignation;
            PressureClassBox.Text = product.PressureClass;

            OuterDiameterBox.Text =
                FormatDecimal(product.OuterDiameterMm);

            WallThicknessBox.Text =
                FormatDecimal(product.WallThicknessMm);

            MaximumPressureBox.Text =
                product.MaximumOperatingPressureBar.HasValue
                    ? FormatDecimal(product.MaximumOperatingPressureBar.Value)
                    : "";

            StandardReferenceBox.Text = product.StandardReference;
            SourceDescriptionBox.Text = product.SourceDescription;
            SourceUrlBox.Text = product.SourceUrl;

            MaterialBox.SelectedIndex = product.Material switch
            {
                PipeMaterial.PvcSoldavel => 0,
                PipeMaterial.PvcRoscavel => 1,
                PipeMaterial.Ppr => 2,
                _ => -1
            };

            UpdateInternalDiameter();
        }

        private static string FormatDecimal(decimal value)
        {
            return value.ToString(
                "0.##",
                CultureInfo.GetCultureInfo("pt-BR"));
        }

        // CÁLCULO AUTOMÁTICO
        private void Dimension_Changed(
            object sender, TextChangedEventArgs e)
        {
            UpdateInternalDiameter();
        }

        private void UpdateInternalDiameter()
        {
            if (TryReadDecimal(OuterDiameterBox.Text, out decimal outer) &&
                TryReadDecimal(WallThicknessBox.Text, out decimal thickness) &&
                outer > 0 &&
                thickness > 0 &&
                outer > 2 * thickness)
            {
                decimal internalDiameter = outer - (2 * thickness);

                InternalDiameterBox.Text =
                    FormatDecimal(internalDiameter);
            }
            else
            {
                InternalDiameterBox.Text = "";
            }
        }

        private static bool TryReadDecimal(
            string value, out decimal result)
        {
            return decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.GetCultureInfo("pt-BR"),
                out result);
        }

        // SALVAR OU ATUALIZAR
        private async void SaveButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (!TryReadDecimal(
                    OuterDiameterBox.Text, out decimal outerDiameter) ||
                !TryReadDecimal(
                    WallThicknessBox.Text, out decimal wallThickness))
            {
                MessageBox.Show(
                    "Informe valores numéricos válidos para os diâmetros e a espessura.",
                    "Smart Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (outerDiameter <= 0 ||
                wallThickness <= 0 ||
                wallThickness * 2 >= outerDiameter)
            {
                MessageBox.Show(
                    "Verifique o diâmetro externo e a espessura. " +
                    "O diâmetro interno precisa ser maior que zero.",
                    "Smart Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            decimal? maximumPressure = null;

            if (!string.IsNullOrWhiteSpace(MaximumPressureBox.Text))
            {
                if (!TryReadDecimal(
                    MaximumPressureBox.Text, out decimal pressure) ||
                    pressure <= 0)
                {
                    MessageBox.Show(
                        "Informe uma pressão válida ou deixe o campo vazio.",
                        "Smart Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                maximumPressure = pressure;
            }

            if (string.IsNullOrWhiteSpace(ManufacturerBox.Text) ||
                string.IsNullOrWhiteSpace(ProductLineBox.Text) ||
                string.IsNullOrWhiteSpace(NominalDesignationBox.Text) ||
                string.IsNullOrWhiteSpace(PressureClassBox.Text) ||
                string.IsNullOrWhiteSpace(StandardReferenceBox.Text) ||
                string.IsNullOrWhiteSpace(SourceDescriptionBox.Text))
            {
                MessageBox.Show(
                    "Preencha todos os campos obrigatórios.",
                    "Smart Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            PipeMaterial material;

            string selectedMaterial =
                (MaterialBox.SelectedItem as ComboBoxItem)
                ?.Content?.ToString() ?? "";

            switch (selectedMaterial)
            {
                case "PVC Soldável":
                    material = PipeMaterial.PvcSoldavel;
                    break;

                case "PVC Roscável":
                    material = PipeMaterial.PvcRoscavel;
                    break;

                case "PPR":
                    material = PipeMaterial.Ppr;
                    break;

                default:
                    MessageBox.Show(
                        "Selecione um material válido.",
                        "Smart Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
            }

            var product = new PipeProduct
            {
                Id = IsEditMode
                    ? _productBeingEdited!.Id
                    : Guid.NewGuid(),

                Manufacturer = ManufacturerBox.Text.Trim(),
                ProductLine = ProductLineBox.Text.Trim(),
                Material = material,
                NominalDesignation = NominalDesignationBox.Text.Trim(),
                OuterDiameterMm = outerDiameter,
                WallThicknessMm = wallThickness,
                PressureClass = PressureClassBox.Text.Trim(),
                MaximumOperatingPressureBar = maximumPressure,
                StandardReference = StandardReferenceBox.Text.Trim(),
                SourceDescription = SourceDescriptionBox.Text.Trim(),
                SourceUrl = SourceUrlBox.Text.Trim()
            };

            try
            {
                SaveButton.IsEnabled = false;

                if (IsEditMode)
                {
                    await _catalogService.UpdateAsync(
                        _productBeingEdited!.Id,
                        product);
                }
                else
                {
                    await _catalogService.AddAsync(product);
                }

                MessageBox.Show(
                    IsEditMode
                        ? "Produto atualizado com sucesso!"
                        : "Produto cadastrado com sucesso!",
                    "Smart Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Não foi possível salvar o produto:\n{ex.Message}",
                    "Smart Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }
    }
}
