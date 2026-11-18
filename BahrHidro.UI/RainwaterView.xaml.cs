using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BahrHidro.Core.Hydraulic.Rainwater;

namespace BahrHidro.UI
{
    public partial class RainwaterView : UserControl
    {
        public static RainwaterResult? UltimoResultado { get; private set; }

        public RainwaterView()
        {
            InitializeComponent();
            var regioes = RainwaterRegionCatalog.GetRegions();
            CmbRegiao.ItemsSource = regioes;

            // Seleciona Ponta Grossa - PR por defeito (ou o primeiro item)
            var padrao = regioes.FirstOrDefault(r => r.City == "Ponta Grossa") ?? regioes.First();
            CmbRegiao.SelectedItem = padrao;
            AtualizarIntensidadePelaSelecao();
        }

        private void CmbRegiao_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            AtualizarIntensidadePelaSelecao();
        }

        private void CmbPeriodoRetorno_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            AtualizarIntensidadePelaSelecao();
        }

        private void AtualizarIntensidadePelaSelecao()
        {
            if (CmbRegiao.SelectedItem is not RainwaterRegion regiao) return;

            TxtIntensidade.IsReadOnly = !regiao.IsCustom;
            TxtIntensidade.Background = regiao.IsCustom ? Brushes.White : new SolidColorBrush(Color.FromRgb(241, 245, 249));

            if (!regiao.IsCustom)
            {
                var periodo = ObterPeriodoRetornoSelecionado();
                double intensidadeAjustada = RainwaterRegionCatalog.AdjustIntensityForReturnPeriod(
                    regiao.Intensity5YearsMmPerHour, periodo);
                TxtIntensidade.Text = intensidadeAjustada.ToString("F1", CultureInfo.GetCultureInfo("pt-BR"));
            }
        }

        private void ChkCalhaManual_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            PainelCalhaManual.Visibility = ChkCalhaManual.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnCalcularPluvial_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var regiao = CmbRegiao.SelectedItem as RainwaterRegion;
                string nomeRegiao = regiao != null && !regiao.IsCustom
                    ? $"{regiao.City} - {regiao.State}"
                    : "Região Personalizada";

                var input = new RainwaterInput
                {
                    RegionName = nomeRegiao,
                    IntensityMmPerHour = ParseDouble(TxtIntensidade.Text, "Intensidade pluviométrica"),
                    ReturnPeriod = ObterPeriodoRetornoSelecionado(),
                    RoofWidthMeters = ParseDouble(TxtLarguraTelhado.Text, "Comprimento da calha"),
                    RoofProjectionMeters = ParseDouble(TxtProjecaoTelhado.Text, "Projeção horizontal"),
                    RoofHeightMeters = ParseDouble(TxtAlturaTelhado.Text, "Altura do telhado"),
                    InterceptingWallAreaM2 = ParseDouble(TxtParedeInterceptante.Text, "Parede interceptante"),
                    Shape = (CmbFormatoCalha.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Semicircular"
                        ? GutterShape.Semicircular
                        : GutterShape.Rectangular,
                    Material = Enum.Parse<GutterMaterial>(
                        (CmbMaterialCalha.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "PvcOrMetal"),
                    GutterSlopePercent = ParseDouble(TxtDeclividade.Text, "Declividade da calha"),
                    ManualWidthMm = ChkCalhaManual.IsChecked == true ? ParseDouble(TxtLarguraManual.Text, "Largura/Diâmetro manual") : 0,
                    ManualHeightMm = ChkCalhaManual.IsChecked == true ? ParseDouble(TxtAlturaManual.Text, "Altura manual") : 0,
                    DownspoutCount = (int)Math.Max(1, ParseDouble(TxtQtdCondutores.Text, "Número de descidas")),
                    DownspoutHeightMeters = ParseDouble(TxtAlturaCondutor.Text, "Altura do condutor"),
                    WaterDepthAtExitMm = ParseDouble(TxtLaminaSaida.Text, "Lâmina de saída"),
                    ExitType = (CmbTipoSaida.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Funnel"
                        ? DownspoutExitType.Funnel
                        : DownspoutExitType.SharpEdge
                };

                RainwaterResult res = RainwaterCalculator.Calculate(input);
                UltimoResultado = res;

                var pt = CultureInfo.GetCultureInfo("pt-BR");
                LblResRegiao.Text = $"Região: {res.RegionName} (I = {res.IntensityMmPerHour.ToString("F1", pt)} mm/h)";
                LblResArea.Text = $"Área de Contribuição: {res.ContributionAreaM2.ToString("F2", pt)} m²";
                LblResVazao.Text = $"Vazão Total (Q): {res.DesignFlowLitersPerMinute.ToString("F2", pt)} L/min ({res.DesignFlowLitersPerSecond.ToString("F2", pt)} L/s)";

                LblResCalha.Text = $"Seção Comercial: {res.SelectedGutterDescription}";
                LblResCapacidadeCalha.Text = $"Capacidade Máxima: {res.GutterMaxCapacityLitersPerMinute.ToString("F2", pt)} L/min (n = {res.ManningRoughnessN.ToString("F3", pt)})";
                LblResOcupacaoCalha.Text = $"Ocupação: {res.GutterOccupancyPercentage.ToString("F1", pt)}% | Velocidade: {res.GutterVelocityMetersPerSecond.ToString("F2", pt)} m/s";

                if (res.GutterMeetsRequirement)
                {
                    LblStatusCalha.Text = "Status: ATENDE À VAZÃO DE PROJETO (NBR 10844)";
                    LblStatusCalha.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                }
                else
                {
                    LblStatusCalha.Text = "Status: INSUFICIENTE — Aumente a seção ou declividade";
                    LblStatusCalha.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                }

                LblResCondutor.Text = $"Diâmetro Nominal: {res.DownspoutCount}× Tubo PVC DN {res.RequiredDownspoutDiameterMm} mm";
                LblResVazaoCondutor.Text = $"Vazão por descida: {res.FlowPerDownspoutLitersPerMinute.ToString("F2", pt)} L/min";
                LblResCapCondutor.Text = $"Capacidade por tubo: {res.DownspoutMaxCapacityLitersPerMinute.ToString("F2", pt)} L/min";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Verifique os dados informados:\n{ex.Message}", "Bahr Hidro - Águas Pluviais", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private RainwaterReturnPeriod ObterPeriodoRetornoSelecionado()
        {
            string tag = (CmbPeriodoRetorno.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "5";
            return tag switch
            {
                "1" => RainwaterReturnPeriod.OneYear,
                "25" => RainwaterReturnPeriod.TwentyFiveYears,
                _ => RainwaterReturnPeriod.FiveYears
            };
        }

        private static double ParseDouble(string text, string fieldName)
        {
            string normalized = (text ?? "").Trim().Replace(",", ".");
            if (!double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                throw new ArgumentException($"Valor inválido no campo '{fieldName}'.");
            return value;
        }
    }
}