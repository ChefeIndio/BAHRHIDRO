
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using BahrHidro.Core;
using BahrHidro.Core.Catalog;
using BahrHidro.Core.Hydraulic;

namespace BahrHidro.UI
{
    public partial class MainWindow : Window
    {
        private static readonly CultureInfo PtBr =
            CultureInfo.GetCultureInfo("pt-BR");

        private readonly PipeCatalogDiameterSelector _diameterSelector;

        public MainWindow()
        {
            InitializeComponent();

            string catalogPath = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "BahrHidro",
                "pipe-catalog.json");

            var repository =
                new PipeCatalogRepository(catalogPath);

            var catalogService =
                new PipeCatalogService(repository);

            _diameterSelector =
                new PipeCatalogDiameterSelector(catalogService);
        }

        // ABRIR CATALOGO DE TUBULACOES
        private void AbrirCatalogo_Click(
            object sender,
            RoutedEventArgs e)
        {
            var janelaCatalogo = new PipeCatalogWindow
            {
                Owner = this
            };

            janelaCatalogo.ShowDialog();
        }

        // DIMENSIONAMENTO AUTOMATICO
        private async void Calcular_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                if (!double.TryParse(
                    VazaoTextBox.Text,
                    NumberStyles.Float,
                    PtBr,
                    out double vazao) ||
                    !double.IsFinite(vazao) ||
                    vazao <= 0)
                {
                    MessageBox.Show(
                        "Informe uma vazao valida e maior que zero.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!double.TryParse(
                    VelocidadeTextBox.Text,
                    NumberStyles.Float,
                    PtBr,
                    out double velocidadeLimite) ||
                    !double.IsFinite(velocidadeLimite) ||
                    velocidadeLimite <= 0)
                {
                    MessageBox.Show(
                        "Informe uma velocidade limite valida.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                double diametroMinimo =
                    PipeSizing.CalculateMinimumDiameter(
                        vazao,
                        velocidadeLimite);

                DiametroMinimoText.Text =
                    $"{diametroMinimo:F2} mm";

                var selecao = await _diameterSelector.SelectAsync(
                    vazao,
                    velocidadeLimite);

                if (selecao == null)
                {
                    DiametroSelecionadoText.Text =
                        "Nao disponivel";

                    DescricaoTuboText.Text =
                        "Nenhum produto cadastrado atende ao criterio.";

                    VelocidadeRealText.Text = "--";

                    StatusText.Text = "CATALOGO INSUFICIENTE";
                    StatusText.Foreground = Brushes.DarkOrange;

                    return;
                }

                var produto = selecao.Product;

                DiametroSelecionadoText.Text =
                    $"{produto.InternalDiameterMm:F2} mm";

                DescricaoTuboText.Text =
                    $"{produto.Manufacturer} | " +
                    $"{produto.ProductLine} | " +
                    $"{produto.NominalDesignation}";

                VelocidadeRealText.Text =
                    $"{selecao.ActualVelocityMetersPerSecond:F3} m/s";

                bool atende =
                    selecao.ActualVelocityMetersPerSecond
                    <= velocidadeLimite;

                StatusText.Text = atende
                    ? "ATENDE AO LIMITE DE VELOCIDADE"
                    : "NAO ATENDE AO LIMITE DE VELOCIDADE";

                StatusText.Foreground = atende
                    ? Brushes.ForestGreen
                    : Brushes.DarkRed;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro no dimensionamento:\n{ex.Message}",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ANALISE HIDRAULICA DO TRECHO
        private async void AnalisarTrecho_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                // Vazao utilizada no dimensionamento.
                if (!double.TryParse(
                    VazaoTextBox.Text,
                    NumberStyles.Float,
                    PtBr,
                    out double vazao) ||
                    !double.IsFinite(vazao) ||
                    vazao <= 0)
                {
                    MessageBox.Show(
                        "Informe uma vazao valida na aba Dimensionamento.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!double.TryParse(
                    VelocidadeTextBox.Text,
                    NumberStyles.Float,
                    PtBr,
                    out double velocidadeLimite) ||
                    !double.IsFinite(velocidadeLimite) ||
                    velocidadeLimite <= 0)
                {
                    MessageBox.Show(
                        "Informe uma velocidade limite valida.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Comprimento da tubulacao.
                if (!double.TryParse(
                    ComprimentoTextBox.Text,
                    NumberStyles.Float,
                    PtBr,
                    out double comprimento) ||
                    !double.IsFinite(comprimento) ||
                    comprimento <= 0)
                {
                    MessageBox.Show(
                        "Informe um comprimento maior que zero.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Rugosidade absoluta.
                if (!double.TryParse(
                    RugosidadeTextBox.Text,
                    NumberStyles.Float,
                    PtBr,
                    out double rugosidade) ||
                    !double.IsFinite(rugosidade) ||
                    rugosidade < 0)
                {
                    MessageBox.Show(
                        "Informe uma rugosidade valida.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Coeficiente total de perdas localizadas.
                if (!double.TryParse(
                    CoeficienteKTextBox.Text,
                    NumberStyles.Float,
                    PtBr,
                    out double coeficienteK) ||
                    !double.IsFinite(coeficienteK) ||
                    coeficienteK < 0)
                {
                    MessageBox.Show(
                        "Informe um coeficiente K valido.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Viscosidade cinematica.
                if (!double.TryParse(
                    ViscosidadeTextBox.Text,
                    NumberStyles.Float,
                    PtBr,
                    out double viscosidade) ||
                    !double.IsFinite(viscosidade) ||
                    viscosidade <= 0)
                {
                    MessageBox.Show(
                        "Informe uma viscosidade valida.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Busca novamente o produto adequado no catalogo.
                var selecao = await _diameterSelector.SelectAsync(
                    vazao,
                    velocidadeLimite);

                if (selecao == null)
                {
                    MessageBox.Show(
                        "Nenhum tubo adequado foi encontrado no catalogo. " +
                        "Faca o dimensionamento primeiro.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Diametro interno real do produto selecionado.
                double diametroInterno =
                    (double)selecao.Product.InternalDiameterMm;

                // Executa os calculos hidraulicos do trecho.
                HydraulicSegmentResult resultado =
                    HydraulicSegmentCalculator.Calculate(
                        flowLitersPerSecond: vazao,
                        lengthMeters: comprimento,
                        internalDiameterMm: diametroInterno,
                        absoluteRoughnessMm: rugosidade,
                        kinematicViscosityM2s: viscosidade,
                        totalLossCoefficientK: coeficienteK);

                // Apresenta os resultados.
                AnaliseVelocidadeText.Text =
                    $"{resultado.VelocityMetersPerSecond:F3} m/s";

                ReynoldsText.Text =
                    $"{resultado.ReynoldsNumber:F0}";

                RegimeText.Text =
                    resultado.Regime.ToString();

                FatorAtritoText.Text =
                    $"{resultado.FrictionFactor:F5}";

                PerdaDistribuidaText.Text =
                    $"{resultado.HeadLossMeters:F4} m.c.a.";

                PerdaLocalizadaText.Text =
                    $"{resultado.LocalLossMeters:F4} m.c.a.";

                PerdaTotalText.Text =
                    $"{resultado.TotalHeadLossMeters:F4} m.c.a.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro na analise hidraulica:\n{ex.Message}",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
