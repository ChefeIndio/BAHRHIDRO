using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BahrHidro.Core;
using BahrHidro.Core.Catalog;
using BahrHidro.Core.Hydraulic;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.Core.Hydraulic.HotWater; // <-- Novo módulo

namespace BahrHidro.UI
{
    public partial class MainWindow : Window
    {
        private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

        private readonly PipeCatalogDiameterSelector _diameterSelector;
        private readonly HydraulicNetwork _network = new HydraulicNetwork("Rede Hidráulica");

        private Guid? _editingNodeId;
        private Guid? _editingSegmentId;

        public MainWindow()
        {
            InitializeComponent();

            string catalogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BahrHidro", "pipe-catalog.json");
            var repository = new PipeCatalogRepository(catalogPath);
            var catalogService = new PipeCatalogService(repository);
            _diameterSelector = new PipeCatalogDiameterSelector(catalogService);

            AtualizarEstadoTipoNo();
            AtualizarListaNos();
            AtualizarListaTrechos();
        }

        // =====================================================================
        // ABA 1: DIMENSIONAMENTO
        // =====================================================================
        private void AbrirCatalogo_Click(object sender, RoutedEventArgs e)
        {
            var janelaCatalogo = new PipeCatalogWindow { Owner = this };
            janelaCatalogo.ShowDialog();
        }

        private async void Calcular_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!double.TryParse(VazaoTextBox.Text, NumberStyles.Float, PtBr, out double vazao) || vazao <= 0) return;
                if (!double.TryParse(VelocidadeTextBox.Text, NumberStyles.Float, PtBr, out double velocidadeLimite) || velocidadeLimite <= 0) return;

                double diametroMinimo = PipeSizing.CalculateMinimumDiameter(vazao, velocidadeLimite);
                DiametroMinimoText.Text = $"{diametroMinimo:F2} mm";

                var selecao = await _diameterSelector.SelectAsync(vazao, velocidadeLimite);
                if (selecao == null)
                {
                    DiametroSelecionadoText.Text = "Nao disponivel";
                    StatusText.Text = "CATALOGO INSUFICIENTE";
                    StatusText.Foreground = Brushes.DarkOrange;
                    return;
                }

                DiametroSelecionadoText.Text = $"{selecao.Product.InternalDiameterMm:F2} mm";
                DescricaoTuboText.Text = $"{selecao.Product.Manufacturer} | {selecao.Product.ProductLine} | {selecao.Product.NominalDesignation}";
                VelocidadeRealText.Text = $"{selecao.ActualVelocityMetersPerSecond:F3} m/s";

                bool atende = selecao.ActualVelocityMetersPerSecond <= velocidadeLimite;
                StatusText.Text = atende ? "ATENDE AO LIMITE DE VELOCIDADE" : "NAO ATENDE AO LIMITE DE VELOCIDADE";
                StatusText.Foreground = atende ? Brushes.ForestGreen : Brushes.DarkRed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =====================================================================
        // ABA 2: ANÁLISE HIDRÁULICA
        // =====================================================================
        private async void AnalisarTrecho_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!double.TryParse(VazaoTextBox.Text, NumberStyles.Float, PtBr, out double vazao) || vazao <= 0) return;
                if (!double.TryParse(VelocidadeTextBox.Text, NumberStyles.Float, PtBr, out double velocidadeLimite) || velocidadeLimite <= 0) return;
                if (!double.TryParse(ComprimentoTextBox.Text, NumberStyles.Float, PtBr, out double comprimento) || comprimento <= 0) return;
                if (!double.TryParse(RugosidadeTextBox.Text, NumberStyles.Float, PtBr, out double rugosidade) || rugosidade < 0) return;
                if (!double.TryParse(CoeficienteKTextBox.Text, NumberStyles.Float, PtBr, out double coeficienteK) || coeficienteK < 0) return;
                if (!double.TryParse(ViscosidadeTextBox.Text, NumberStyles.Float, PtBr, out double viscosidade) || viscosidade <= 0) return;

                var selecao = await _diameterSelector.SelectAsync(vazao, velocidadeLimite);
                if (selecao == null) return;

                HydraulicSegmentResult resultado = HydraulicSegmentCalculator.Calculate(vazao, comprimento, (double)selecao.Product.InternalDiameterMm, rugosidade, viscosidade, coeficienteK);
                AnaliseVelocidadeText.Text = $"{resultado.VelocityMetersPerSecond:F3} m/s";
                ReynoldsText.Text = $"{resultado.ReynoldsNumber:F0}";
                RegimeText.Text = resultado.Regime.ToString();
                FatorAtritoText.Text = $"{resultado.FrictionFactor:F5}";
                PerdaDistribuidaText.Text = $"{resultado.HeadLossMeters:F4} m.c.a.";
                PerdaLocalizadaText.Text = $"{resultado.LocalLossMeters:F4} m.c.a.";
                PerdaTotalText.Text = $"{resultado.TotalHeadLossMeters:F4} m.c.a.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =====================================================================
        // ABA 3: REDE HIDRAULICA
        // =====================================================================
        private void NoTipoComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (NoDemandaTextBox != null) AtualizarEstadoTipoNo(); }
        private void AtualizarEstadoTipoNo()
        {
            HydraulicNodeType tipo = ObterTipoNoSelecionado();
            if (tipo == HydraulicNodeType.Reservoir) { NoDemandaTextBox.Text = "0,00"; NoDemandaTextBox.IsEnabled = false; }
            else { NoDemandaTextBox.IsEnabled = true; if (string.IsNullOrWhiteSpace(NoDemandaTextBox.Text)) NoDemandaTextBox.Text = "0,00"; }
        }
        private HydraulicNodeType ObterTipoNoSelecionado() => (NoTipoComboBox.SelectedItem is ComboBoxItem item && Enum.TryParse(item.Tag as string, out HydraulicNodeType parsed)) ? parsed : HydraulicNodeType.Junction;

        private void SelecionarTipoNoNoComboBox(HydraulicNodeType type)
        {
            foreach (var obj in NoTipoComboBox.Items) if (obj is ComboBoxItem item && string.Equals(item.Tag as string, type.ToString(), StringComparison.Ordinal)) { NoTipoComboBox.SelectedItem = item; break; }
        }

        private void SalvarNo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string nome = (NoNomeTextBox.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(nome)) return;
                if (!TryParsePtBrDouble(NoCotaTextBox.Text, out double cota)) return;
                HydraulicNodeType tipo = ObterTipoNoSelecionado();
                double demanda = 0;
                if (tipo != HydraulicNodeType.Reservoir) if (!TryParsePtBrDouble(NoDemandaTextBox.Text, out demanda) || demanda < 0) return;

                if (_editingNodeId.HasValue) _network.UpdateNode(new HydraulicNode { Id = _editingNodeId.Value, Name = nome, Type = tipo, ElevationMeters = cota, DemandLitersPerSecond = demanda });
                else _network.AddNode(new HydraulicNode { Name = nome, Type = tipo, ElevationMeters = cota, DemandLitersPerSecond = demanda });
                LimparFormularioNo(); AtualizarListaNos();
            }
            catch { }
        }

        private void EditarNo_Click(object sender, RoutedEventArgs e) => CarregarNoSelecionadoParaEdicao();
        private void NosDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (NosDataGrid.SelectedItem is NodeGridItem) CarregarNoSelecionadoParaEdicao(); }
        private void CarregarNoSelecionadoParaEdicao()
        {
            if (NosDataGrid.SelectedItem is not NodeGridItem item) return;
            var node = _network.FindNode(item.Id);
            if (node == null) return;
            _editingNodeId = node.Id;
            SelecionarTipoNoNoComboBox(node.Type);
            AtualizarEstadoTipoNo();
            NoNomeTextBox.Text = node.Name;
            NoCotaTextBox.Text = node.ElevationMeters.ToString("F2", PtBr);
            NoDemandaTextBox.Text = node.DemandLitersPerSecond.ToString("F3", PtBr);
            NoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7"));
            NoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B45309"));
            NoModoEdicaoText.Text = $"Modo: Editando '{node.Name}'";
        }
        private void ExcluirNo_Click(object sender, RoutedEventArgs e)
        {
            if (NosDataGrid.SelectedItem is not NodeGridItem item) return;
            _network.RemoveNode(item.Id);
            if (_editingNodeId == item.Id) LimparFormularioNo();
            AtualizarListaNos();
        }
        private void CancelarEdicaoNo_Click(object sender, RoutedEventArgs e) => LimparFormularioNo();
        private void LimparFormularioNo()
        {
            _editingNodeId = null; NoNomeTextBox.Text = ""; NoCotaTextBox.Text = "0,00"; NoDemandaTextBox.Text = "0,00";
            AtualizarEstadoTipoNo();
            NoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
            NoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
            NoModoEdicaoText.Text = "Modo: Novo elemento";
        }
        private void AtualizarListaNos()
        {
            var itens = _network.Nodes.Select(n => new NodeGridItem { Id = n.Id, Name = n.Name, Type = n.Type, TypeLabel = FormatarTipoNo(n.Type), ElevationMeters = n.ElevationMeters, ElevationDisplay = n.ElevationMeters.ToString("F2", PtBr), DemandLitersPerSecond = n.DemandLitersPerSecond, DemandDisplay = n.DemandLitersPerSecond.ToString("F3", PtBr) }).ToList();
            NosDataGrid.ItemsSource = itens;
            ResumoRedeText.Text = $"Total: {_network.Nodes.Count} elementos";
            AtualizarCombosNosTrecho(itens); AtualizarListaTrechos();
        }
        private void AtualizarCombosNosTrecho(List<NodeGridItem> itens)
        {
            Guid? s = TrechoNoInicialComboBox?.SelectedValue as Guid?; Guid? e = TrechoNoFinalComboBox?.SelectedValue as Guid?;
            if (TrechoNoInicialComboBox != null) TrechoNoInicialComboBox.ItemsSource = itens;
            if (TrechoNoFinalComboBox != null) TrechoNoFinalComboBox.ItemsSource = itens;
            if (s.HasValue && itens.Any(i => i.Id == s.Value)) TrechoNoInicialComboBox.SelectedValue = s.Value;
            if (e.HasValue && itens.Any(i => i.Id == e.Value)) TrechoNoFinalComboBox.SelectedValue = e.Value;
        }

        private void SalvarTrecho_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string nome = (TrechoNomeTextBox.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(nome) || TrechoNoInicialComboBox.SelectedValue is not Guid start || TrechoNoFinalComboBox.SelectedValue is not Guid end || start == end) return;
                if (!TryParsePtBrDouble(TrechoComprimentoTextBox.Text, out double comp) || !TryParsePtBrDouble(TrechoDiametroTextBox.Text, out double dia) || !TryParsePtBrDouble(TrechoRugosidadeTextBox.Text, out double rug) || !TryParsePtBrDouble(TrechoCoeficienteKTextBox.Text, out double k)) return;
                double? vazao = null; if (TryParsePtBrDouble(TrechoVazaoTextBox.Text, out double v) && v >= 0) vazao = v;
                var seg = new HydraulicPipeSegment { Id = _editingSegmentId ?? Guid.NewGuid(), Name = nome, StartNodeId = start, EndNodeId = end, LengthMeters = comp, InternalDiameterMm = dia, AbsoluteRoughnessMm = rug, TotalLossCoefficientK = k, FlowLitersPerSecond = vazao };
                if (_editingSegmentId.HasValue) _network.UpdateSegment(seg); else _network.AddSegment(seg);
                LimparFormularioTrecho(); AtualizarListaTrechos();
            }
            catch { }
        }
        private void EditarTrecho_Click(object sender, RoutedEventArgs e) => CarregarTrechoSelecionadoParaEdicao();
        private void TrechosDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (TrechosDataGrid.SelectedItem is SegmentGridItem) CarregarTrechoSelecionadoParaEdicao(); }
        private void CarregarTrechoSelecionadoParaEdicao()
        {
            if (TrechosDataGrid.SelectedItem is not SegmentGridItem item) return;
            var seg = _network.FindSegment(item.Id); if (seg == null) return;
            _editingSegmentId = seg.Id; TrechoNomeTextBox.Text = seg.Name; TrechoNoInicialComboBox.SelectedValue = seg.StartNodeId; TrechoNoFinalComboBox.SelectedValue = seg.EndNodeId;
            TrechoComprimentoTextBox.Text = seg.LengthMeters.ToString("F2", PtBr); TrechoDiametroTextBox.Text = seg.InternalDiameterMm.ToString("F2", PtBr);
            TrechoRugosidadeTextBox.Text = seg.AbsoluteRoughnessMm.ToString("G", PtBr); TrechoCoeficienteKTextBox.Text = seg.TotalLossCoefficientK.ToString("F2", PtBr);
            TrechoVazaoTextBox.Text = seg.FlowLitersPerSecond?.ToString("F3", PtBr) ?? "";
            TrechoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7"));
            TrechoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B45309"));
            TrechoModoEdicaoText.Text = $"Modo: Editando '{seg.Name}'";
        }
        private void ExcluirTrecho_Click(object sender, RoutedEventArgs e)
        {
            if (TrechosDataGrid.SelectedItem is not SegmentGridItem item) return;
            _network.RemoveSegment(item.Id); if (_editingSegmentId == item.Id) LimparFormularioTrecho(); AtualizarListaTrechos();
        }
        private void CancelarEdicaoTrecho_Click(object sender, RoutedEventArgs e) => LimparFormularioTrecho();
        private void LimparFormularioTrecho()
        {
            _editingSegmentId = null; TrechoNomeTextBox.Text = ""; TrechoNoInicialComboBox.SelectedIndex = -1; TrechoNoFinalComboBox.SelectedIndex = -1;
            TrechoComprimentoTextBox.Text = "10,00"; TrechoDiametroTextBox.Text = "21,60"; TrechoRugosidadeTextBox.Text = "0,001"; TrechoCoeficienteKTextBox.Text = "0"; TrechoVazaoTextBox.Text = "";
            TrechoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
            TrechoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
            TrechoModoEdicaoText.Text = "Modo: Novo trecho";
        }
        private void AtualizarListaTrechos()
        {
            if (TrechosDataGrid == null) return;
            TrechosDataGrid.ItemsSource = _network.Segments.Select(s => new SegmentGridItem { Id = s.Id, Name = s.Name, ConnectionDisplay = $"{_network.FindNode(s.StartNodeId)?.Name ?? "?"} → {_network.FindNode(s.EndNodeId)?.Name ?? "?"}", LengthDisplay = s.LengthMeters.ToString("F2", PtBr), DiameterDisplay = s.InternalDiameterMm.ToString("F2", PtBr), KDisplay = s.TotalLossCoefficientK.ToString("F2", PtBr), FlowDisplay = s.FlowLitersPerSecond?.ToString("F3", PtBr) ?? "Auto" }).ToList();
            ResumoTrechosText.Text = $"Total: {_network.Segments.Count} trecho(s)";
        }

        private void CalcularRede_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!TryParsePtBrDouble(RedeViscosidadeTextBox.Text, out double viscosidade)) return;
                var res = HydraulicNetworkSolver.Solve(_network, viscosidade);
                ResultadosTrechosDataGrid.ItemsSource = res.Segments.Select(s => new ResultadoTrechoGridItem { Name = s.SegmentName, ConnectionDisplay = s.Connection, FlowDisplay = s.FlowLitersPerSecond.ToString("F3", PtBr), VelocityDisplay = s.VelocityMetersPerSecond.ToString("F3", PtBr), ReynoldsDisplay = s.ReynoldsNumber.ToString("F0", PtBr), HeadLossDisplay = s.TotalHeadLossMeters.ToString("F4", PtBr) }).ToList();
                ResultadosNosDataGrid.ItemsSource = res.Nodes.Select(n => new ResultadoNoGridItem { Name = n.NodeName, TypeLabel = FormatarTipoNo(n.NodeType), ElevationDisplay = n.ElevationMeters.ToString("F2", PtBr), HeadDisplay = n.PiezometricHeadMeters.ToString("F3", PtBr), PressureDisplay = n.AvailablePressureMca.ToString("F3", PtBr), IsLowPressure = n.AvailablePressureMca < 1.0 && n.NodeType != HydraulicNodeType.Reservoir }).ToList();
            }
            catch (Exception ex) { MessageBox.Show($"Erro:\n{ex.Message}"); }
        }

        // =====================================================================
        // ABA 4: ÁGUA QUENTE / BOILER (NOVO CÓDIGO)
        // =====================================================================
        private void CalcularBoiler_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Validar e capturar o número de ocupantes
                if (!int.TryParse(BoilerOcupantesTextBox.Text, out int ocupantes) || ocupantes <= 0)
                {
                    MessageBox.Show("Informe um número válido de ocupantes (maior que zero).", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 2. Validar e capturar a pressão disponível (em m.c.a)
                if (!TryParsePtBrDouble(BoilerPressaoTextBox.Text, out double pressao))
                {
                    MessageBox.Show("Informe uma pressão disponível válida em m.c.a.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 3. Descobrir qual perfil de edificação foi selecionado no ComboBox
                if (BoilerPerfilComboBox.SelectedItem is not ComboBoxItem itemSelecionado || 
                    itemSelecionado.Tag is not string tagString ||
                    !Enum.TryParse(tagString, out BuildingProfile perfil))
                {
                    MessageBox.Show("Selecione um perfil de edificação válido.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 4. Executar o cálculo matemático do Core
                var resultado = BoilerCalculator.Calculate(perfil, ocupantes, pressao);

                // 5. Exibir os resultados na tela
                BoilerConsumoText.Text = $"{resultado.DailyConsumptionLiters:F0} L";
                BoilerVolumeText.Text = $"{resultado.RequiredAccumulationVolumeLiters:F0} L";
                BoilerTipoText.Text = resultado.RecommendedPressureType;

                // 6. Colorir o texto do equipamento de acordo com a pressão
                if (resultado.RecommendedPressureType.Contains("INSUFICIENTE") || resultado.RecommendedPressureType.Contains("EXCESSIVA"))
                    BoilerTipoText.Foreground = Brushes.DarkRed;
                else if (resultado.RecommendedPressureType.Contains("ALTA PRESSÃO"))
                    BoilerTipoText.Foreground = Brushes.DarkOrange;
                else
                    BoilerTipoText.Foreground = Brushes.ForestGreen;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ocorreu um erro ao dimensionar o Boiler:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =====================================================================
        // UTILITÁRIOS
        // =====================================================================
        private static string FormatarTipoNo(HydraulicNodeType type) => type switch { HydraulicNodeType.Reservoir => "Reservatório", HydraulicNodeType.Junction => "Nó", HydraulicNodeType.ConsumptionPoint => "Consumo", _ => type.ToString() };
        private static bool TryParsePtBrDouble(string? text, out double value) { if (string.IsNullOrWhiteSpace(text)) { value = 0; return false; } return double.TryParse(text.Trim().Replace('.', ','), NumberStyles.Float, PtBr, out value); }

        private sealed class NodeGridItem { public Guid Id { get; init; } public string Name { get; init; } = ""; public HydraulicNodeType Type { get; init; } public string TypeLabel { get; init; } = ""; public double ElevationMeters { get; init; } public string ElevationDisplay { get; init; } = ""; public double DemandLitersPerSecond { get; init; } public string DemandDisplay { get; init; } = ""; }
        private sealed class SegmentGridItem { public Guid Id { get; init; } public string Name { get; init; } = ""; public string ConnectionDisplay { get; init; } = ""; public string LengthDisplay { get; init; } = ""; public string DiameterDisplay { get; init; } = ""; public string KDisplay { get; init; } = ""; public string FlowDisplay { get; init; } = ""; }
        private sealed class ResultadoTrechoGridItem { public string Name { get; init; } = ""; public string ConnectionDisplay { get; init; } = ""; public string FlowDisplay { get; init; } = ""; public string VelocityDisplay { get; init; } = ""; public string ReynoldsDisplay { get; init; } = ""; public string HeadLossDisplay { get; init; } = ""; }
        private sealed class ResultadoNoGridItem { public string Name { get; init; } = ""; public string TypeLabel { get; init; } = ""; public string ElevationDisplay { get; init; } = ""; public string HeadDisplay { get; init; } = ""; public string PressureDisplay { get; init; } = ""; public bool IsLowPressure { get; init; } }
    }
}