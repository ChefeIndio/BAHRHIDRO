using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes; // NOVO: Para desenhar Linhas e Bolinhas
using BahrHidro.Core;
using BahrHidro.Core.Catalog;
using BahrHidro.Core.Hydraulic;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.Core.Hydraulic.HotWater;
using BahrHidro.Core.Hydraulic.Reservoirs; 
using BahrHidro.Core.Hydraulic.Sewer; 
using BahrHidro.Core.Reporting; 

namespace BahrHidro.UI
{
    public partial class MainWindow : Window
    {
        private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

        private readonly PipeCatalogDiameterSelector _diameterSelector;
        private readonly PipeCatalogService _catalogService; 
        private readonly HydraulicNetwork _network = new HydraulicNetwork("Rede Hidráulica");

        private Guid? _editingNodeId;
        private Guid? _editingSegmentId;
        private NetworkCalculationResult? _ultimoResultadoDaRede;
        
        private readonly List<SewerFixtureViewModel> _sewerFixtures;

        // VARIÁVEIS PARA O NOVO CAD 2D
        private enum ModoDesenho { Selecionar, DesenharNo, ConectarTrecho }
        private ModoDesenho _modoAtual = ModoDesenho.Selecionar;
        private Ellipse? _noInicialParaTrecho = null; // Guarda o primeiro nó clicado ao fazer um tubo
        
        // Mapeamentos para o software saber que bolinha corresponde a que Nó na memória
        private readonly Dictionary<Ellipse, Guid> _mapaNosVisuais = new();

        public MainWindow()
        {
            InitializeComponent();

            // CORREÇÃO: System.IO.Path explícito para evitar conflito com System.Windows.Shapes.Path
            string catalogPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BahrHidro", "pipe-catalog.json");
            var repository = new PipeCatalogRepository(catalogPath);
            _catalogService = new PipeCatalogService(repository);
            _diameterSelector = new PipeCatalogDiameterSelector(_catalogService);

            AtualizarEstadoTipoNo();
            AtualizarListaNos();
            AtualizarListaTrechos();

            _sewerFixtures = SewerFixtureCatalog.GetStandardFixtures().Select(f => new SewerFixtureViewModel { Name = f.Name, Uhc = f.UhcValue, MinDiameter = f.MinimumDiameterMm }).ToList();
            SewerItemsControl.ItemsSource = _sewerFixtures;
        }

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

        private void NoTipoComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (NoDemandaTextBox != null) AtualizarEstadoTipoNo(); }
        private void AtualizarEstadoTipoNo()
        {
            HydraulicNodeType tipo = ObterTipoNoSelecionado();
            if (tipo == HydraulicNodeType.Reservoir) 
            { 
                NoDemandaTextBox.Text = "0,00"; NoDemandaTextBox.IsEnabled = false; 
                NoPesoTextBox.Text = "0,00"; NoPesoTextBox.IsEnabled = false;
            }
            else 
            { 
                NoDemandaTextBox.IsEnabled = true; if (string.IsNullOrWhiteSpace(NoDemandaTextBox.Text)) NoDemandaTextBox.Text = "0,00"; 
                NoPesoTextBox.IsEnabled = true; if (string.IsNullOrWhiteSpace(NoPesoTextBox.Text)) NoPesoTextBox.Text = "0,00";
            }
        }
        private HydraulicNodeType ObterTipoNoSelecionado() => (NoTipoComboBox.SelectedItem is ComboBoxItem item && Enum.TryParse(item.Tag as string, out HydraulicNodeType parsed)) ? parsed : HydraulicNodeType.Junction;
        private void SelecionarTipoNoNoComboBox(HydraulicNodeType type) { foreach (var obj in NoTipoComboBox.Items) if (obj is ComboBoxItem item && string.Equals(item.Tag as string, type.ToString(), StringComparison.Ordinal)) { NoTipoComboBox.SelectedItem = item; break; } }

        private void SalvarNo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string nome = (NoNomeTextBox.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(nome)) return;
                if (!TryParsePtBrDouble(NoCotaTextBox.Text, out double cota)) return;
                HydraulicNodeType tipo = ObterTipoNoSelecionado();
                double demanda = 0; double peso = 0;
                
                if (tipo != HydraulicNodeType.Reservoir) 
                {
                    if (!TryParsePtBrDouble(NoDemandaTextBox.Text, out demanda) || demanda < 0) return;
                    if (!TryParsePtBrDouble(NoPesoTextBox.Text, out peso) || peso < 0) return;
                }

                var novoNo = new HydraulicNode { Id = _editingNodeId ?? Guid.NewGuid(), Name = nome, Type = tipo, ElevationMeters = cota, DemandLitersPerSecond = demanda, RelativeWeight = peso };
                if (_editingNodeId.HasValue) _network.UpdateNode(novoNo); else _network.AddNode(novoNo);
                
                LimparFormularioNo(); AtualizarListaNos();
            }
            catch (Exception ex) { MessageBox.Show($"Não foi possível salvar o elemento:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void EditarNo_Click(object sender, RoutedEventArgs e) => CarregarNoSelecionadoParaEdicao();
        private void NosDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (NosDataGrid.SelectedItem is NodeGridItem) CarregarNoSelecionadoParaEdicao(); }
        private void CarregarNoSelecionadoParaEdicao()
        {
            if (NosDataGrid.SelectedItem is not NodeGridItem item) return;
            var node = _network.FindNode(item.Id);
            if (node == null) return;
            _editingNodeId = node.Id;
            SelecionarTipoNoNoComboBox(node.Type); AtualizarEstadoTipoNo();
            NoNomeTextBox.Text = node.Name; NoCotaTextBox.Text = node.ElevationMeters.ToString("F2", PtBr);
            NoDemandaTextBox.Text = node.DemandLitersPerSecond.ToString("F3", PtBr); NoPesoTextBox.Text = node.RelativeWeight.ToString("F2", PtBr);
            NoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7"));
            NoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B45309"));
            NoModoEdicaoText.Text = $"Modo: Editando '{node.Name}'";
        }
        private void ExcluirNo_Click(object sender, RoutedEventArgs e)
        {
            if (NosDataGrid.SelectedItem is not NodeGridItem item) return;
            _network.RemoveNode(item.Id); if (_editingNodeId == item.Id) LimparFormularioNo(); AtualizarListaNos();
        }
        private void CancelarEdicaoNo_Click(object sender, RoutedEventArgs e) => LimparFormularioNo();
        private void LimparFormularioNo()
        {
            _editingNodeId = null; NoNomeTextBox.Text = ""; NoCotaTextBox.Text = "0,00"; NoDemandaTextBox.Text = "0,00"; NoPesoTextBox.Text = "0,00";
            AtualizarEstadoTipoNo();
            NoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
            NoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
            NoModoEdicaoText.Text = "Modo: Novo elemento";
        }
        private void AtualizarListaNos()
        {
            var itens = _network.Nodes.Select(n => new NodeGridItem { Id = n.Id, Name = n.Name, Type = n.Type, TypeLabel = FormatarTipoNo(n.Type), ElevationMeters = n.ElevationMeters, ElevationDisplay = n.ElevationMeters.ToString("F2", PtBr), DemandLitersPerSecond = n.DemandLitersPerSecond, DemandDisplay = n.DemandLitersPerSecond.ToString("F3", PtBr), WeightDisplay = n.RelativeWeight.ToString("F2", PtBr) }).ToList();
            NosDataGrid.ItemsSource = itens; ResumoRedeText.Text = $"Total: {_network.Nodes.Count} elementos";
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

        private void CalcularConexoes_Click(object sender, RoutedEventArgs e)
        {
            var janelaConexoes = new FittingsWindow { Owner = this };
            if (janelaConexoes.ShowDialog() == true)
            {
                TrechoCoeficienteKTextBox.Text = janelaConexoes.TotalK.ToString("F2", PtBr);
            }
        }

        private void SalvarTrecho_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string nome = (TrechoNomeTextBox.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(nome) || TrechoNoInicialComboBox.SelectedValue is not Guid start || TrechoNoFinalComboBox.SelectedValue is not Guid end || start == end) return;
                
                if (!TryParsePtBrDouble(TrechoComprimentoTextBox.Text, out double comp) || !TryParsePtBrDouble(TrechoRugosidadeTextBox.Text, out double rug) || !TryParsePtBrDouble(TrechoCoeficienteKTextBox.Text, out double k)) return;
                
                double? dia = null;
                if (TrechoDiametroTextBox.Text.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase)) { dia = null; }
                else if (TryParsePtBrDouble(TrechoDiametroTextBox.Text, out double parsedDia) && parsedDia > 0) { dia = parsedDia; }
                else { MessageBox.Show("Informe um diâmetro válido ou escreva 'Auto'.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

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
            TrechoComprimentoTextBox.Text = seg.LengthMeters.ToString("F2", PtBr); 
            TrechoDiametroTextBox.Text = seg.InternalDiameterMm.HasValue ? seg.InternalDiameterMm.Value.ToString("F2", PtBr) : "Auto";
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
            TrechoComprimentoTextBox.Text = "10,00"; TrechoDiametroTextBox.Text = "Auto"; TrechoRugosidadeTextBox.Text = "0,001"; TrechoCoeficienteKTextBox.Text = "0"; TrechoVazaoTextBox.Text = "";
            TrechoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
            TrechoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
            TrechoModoEdicaoText.Text = "Modo: Novo trecho";
        }
        private void AtualizarListaTrechos()
        {
            if (TrechosDataGrid == null) return;
            TrechosDataGrid.ItemsSource = _network.Segments.Select(s => new SegmentGridItem { Id = s.Id, Name = s.Name, ConnectionDisplay = $"{_network.FindNode(s.StartNodeId)?.Name ?? "?"} → {_network.FindNode(s.EndNodeId)?.Name ?? "?"}", LengthDisplay = s.LengthMeters.ToString("F2", PtBr), DiameterDisplay = s.InternalDiameterMm.HasValue ? s.InternalDiameterMm.Value.ToString("F2", PtBr) : "Auto", KDisplay = s.TotalLossCoefficientK.ToString("F2", PtBr), FlowDisplay = s.FlowLitersPerSecond?.ToString("F3", PtBr) ?? "Auto" }).ToList();
            ResumoTrechosText.Text = $"Total: {_network.Segments.Count} trecho(s)";
        }

        private async void CalcularRede_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!TryParsePtBrDouble(RedeViscosidadeTextBox.Text, out double viscosidade)) return;
                var produtosNoCatalogo = await _catalogService.GetAllAsync();
                var opcoesDiametro = produtosNoCatalogo.Select(p => new PipeDiameterOption($"{p.Manufacturer} {p.NominalDesignation}", (double)p.InternalDiameterMm)).ToList();

                var res = HydraulicNetworkSolver.Solve(_network, viscosidade, opcoesDiametro, 3.0);
                
                _ultimoResultadoDaRede = res;
                ExportarMemorialButton.IsEnabled = true;
                DimensionarBombaButton.IsEnabled = true;
                
                ResultadosTrechosDataGrid.ItemsSource = res.Segments.Select(s => new ResultadoTrechoGridItem { Name = s.SegmentName, ConnectionDisplay = s.Connection, FlowDisplay = s.FlowLitersPerSecond.ToString("F3", PtBr), DiameterDisplay = s.SelectedDiameterMm > 0 ? s.SelectedDiameterMm.ToString("F2", PtBr) : "--", VelocityDisplay = s.VelocityMetersPerSecond.ToString("F3", PtBr), ReynoldsDisplay = s.ReynoldsNumber.ToString("F0", PtBr), HeadLossDisplay = s.TotalHeadLossMeters.ToString("F4", PtBr) }).ToList();
                ResultadosNosDataGrid.ItemsSource = res.Nodes.Select(n => new ResultadoNoGridItem { Name = n.NodeName, TypeLabel = FormatarTipoNo(n.NodeType), ElevationDisplay = n.ElevationMeters.ToString("F2", PtBr), HeadDisplay = n.PiezometricHeadMeters.ToString("F3", PtBr), PressureDisplay = n.AvailablePressureMca.ToString("F3", PtBr), IsLowPressure = n.AvailablePressureMca < 1.0 && n.NodeType != HydraulicNodeType.Reservoir }).ToList();
            }
            catch (Exception ex) { MessageBox.Show($"Erro no dimensionamento:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void DimensionarBomba_Click(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultadoDaRede == null) return;
            try
            {
                var resultadoBomba = PumpCalculator.Calculate(_ultimoResultadoDaRede, 1.0, 0.60);
                if (!resultadoBomba.RequiresPump) { MessageBox.Show("O sistema atende perfeitamente por gravidade!\n\nNão é necessária a instalação de bomba/pressurizador na rede.", "Dimensionamento de Bomba", MessageBoxButton.OK, MessageBoxImage.Information); }
                else
                {
                    string msg = $"Atenção! A rede necessita de PRESSURIZAÇÃO.\n\nNó mais crítico: {resultadoBomba.CriticalNodeName}\n(Pressão atual: {resultadoBomba.CriticalNodeCurrentPressure:F2} m.c.a)\n\nESPECIFICAÇÕES RECOMENDADAS PARA A BOMBA:\n- Vazão de Projeto: {resultadoBomba.TotalFlowLitersPerSecond:F2} L/s\n- Altura Manométrica (AMT): {resultadoBomba.RequiredAmtMca:F2} m.c.a.\n- Potência Estimada: {resultadoBomba.EstimatedPowerCv:F2} cv";
                    MessageBox.Show(msg, "Dimensionamento de Bomba", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex) { MessageBox.Show($"Ocorreu um erro ao dimensionar o sistema de bombagem:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ExportarMemorial_Click(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultadoDaRede == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "Memorial_Hidraulico_BahrHidro", DefaultExt = ".xlsx", Filter = "Ficheiro Excel (.xlsx)|*.xlsx" };
            if (dialog.ShowDialog() == true)
            {
                try { ExcelReportGenerator.GenerateNetworkReport(_ultimoResultadoDaRede, dialog.FileName); MessageBox.Show("Memorial exportado com sucesso!", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Information); }
                catch (Exception ex) { MessageBox.Show($"Ocorreu um erro ao gerar o Excel:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }

        private void CalcularBoiler_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(BoilerOcupantesTextBox.Text, out int ocupantes) || ocupantes <= 0) return;
                if (!TryParsePtBrDouble(BoilerPressaoTextBox.Text, out double pressao)) return;
                if (BoilerPerfilComboBox.SelectedItem is not ComboBoxItem itemSelecionado || itemSelecionado.Tag is not string tagString || !Enum.TryParse(tagString, out BuildingProfile perfil)) return;
                var resultado = BoilerCalculator.Calculate(perfil, ocupantes, pressao);
                BoilerConsumoText.Text = $"{resultado.DailyConsumptionLiters:F0} L"; BoilerVolumeText.Text = $"{resultado.RequiredAccumulationVolumeLiters:F0} L"; BoilerTipoText.Text = resultado.RecommendedPressureType;
                if (resultado.RecommendedPressureType.Contains("INSUFICIENTE") || resultado.RecommendedPressureType.Contains("EXCESSIVA")) BoilerTipoText.Foreground = Brushes.DarkRed;
                else if (resultado.RecommendedPressureType.Contains("ALTA PRESSÃO")) BoilerTipoText.Foreground = Brushes.DarkOrange;
                else BoilerTipoText.Foreground = Brushes.ForestGreen;
            }
            catch (Exception ex) { MessageBox.Show($"Ocorreu um erro:\n{ex.Message}"); }
        }

        private void CalcularCaixa_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(CaixaPopulacaoTextBox.Text, out int populacao) || populacao <= 0) return;
                if (!int.TryParse(CaixaDiasTextBox.Text, out int dias) || dias <= 0) return;
                if (!TryParsePtBrDouble(CaixaIncendioTextBox.Text, out double incendio)) return;
                if (!TryParsePtBrDouble(CaixaCisternaTextBox.Text, out double percentagem) || percentagem < 0 || percentagem > 100) return;
                if (CaixaPerfilComboBox.SelectedItem is not ComboBoxItem itemSelecionado || itemSelecionado.Tag is not string tagString || !Enum.TryParse(tagString, out BuildingCategory perfil)) return;

                var resultado = ReservoirCalculator.Calculate(perfil, populacao, dias, incendio, percentagem / 100.0);

                CaixaConsumoText.Text = $"{resultado.TotalDailyConsumptionLiters:F0} Litros";
                CaixaVolumeTotalText.Text = $"{resultado.TotalRequiredVolumeLiters:F0} Litros";
                CaixaSuperiorText.Text = $"{resultado.UpperReservoirVolumeLiters:F0} Litros";
                CaixaCisternaText.Text = $"{resultado.LowerReservoirVolumeLiters:F0} Litros";
            }
            catch (Exception ex) { MessageBox.Show($"Ocorreu um erro:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void CalcularEsgoto_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int totalUhc = 0; int maiorDiametroPeca = 0;
                foreach (var item in _sewerFixtures)
                {
                    if (item.Quantity > 0)
                    {
                        totalUhc += (item.Uhc * item.Quantity);
                        if (item.MinDiameter > maiorDiametroPeca) maiorDiametroPeca = item.MinDiameter;
                    }
                }
                if (totalUhc == 0) { MessageBox.Show("Adicione pelo menos uma peça de esgoto (UHC) para calcular.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                var resultado = SewerCalculator.CalculateBranch(totalUhc, maiorDiametroPeca);
                EsgotoUhcText.Text = resultado.TotalUhc.ToString(); EsgotoTipoText.Text = resultado.ComponentType;
                EsgotoDiametroText.Text = $"DN {resultado.RequiredDiameterMm}"; EsgotoInclinacaoText.Text = $"{resultado.MinimumSlopePercentage:F1} %";
            }
            catch (Exception ex) { MessageBox.Show($"Ocorreu um erro ao dimensionar o esgoto:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // =====================================================================
        // NOVO: INTELIGÊNCIA DO CANVAS 2D (DESENHO DE REDES)
        // =====================================================================
        private void ModoDesenho_Changed(object sender, RoutedEventArgs e)
        {
            if (RbSelecionar?.IsChecked == true) _modoAtual = ModoDesenho.Selecionar;
            else if (RbNo?.IsChecked == true) _modoAtual = ModoDesenho.DesenharNo;
            else if (RbTrecho?.IsChecked == true) _modoAtual = ModoDesenho.ConectarTrecho;
            
            // Reseta qualquer clique inacabado
            if (_noInicialParaTrecho != null)
            {
                _noInicialParaTrecho.Fill = Brushes.SteelBlue; // Volta à cor normal
                _noInicialParaTrecho = null;
            }
            
            if (_modoAtual == ModoDesenho.DesenharNo) InstrucaoCanvasText.Text = "Clique em qualquer lugar da tela branca para adicionar um novo Nó.";
            else if (_modoAtual == ModoDesenho.ConectarTrecho) InstrucaoCanvasText.Text = "Clique num Nó existente (A) e depois clique noutro Nó (B) para traçar um Trecho de tubo entre eles.";
            else InstrucaoCanvasText.Text = "Clique nos elementos desenhados para ver as suas propriedades na Aba 3.";
        }

        private void RedeCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point clique = e.GetPosition(RedeCanvas);

            // 1. MODO: DESENHAR NÓ
            if (_modoAtual == ModoDesenho.DesenharNo)
            {
                // Cria a bolinha gráfica (Ellipse)
                var visualNo = new Ellipse { Width = 20, Height = 20, Fill = Brushes.SteelBlue, Stroke = Brushes.DarkBlue, StrokeThickness = 2, Cursor = Cursors.Hand };
                
                // Posiciona onde o rato clicou (centrando a bolinha de 20x20)
                Canvas.SetLeft(visualNo, clique.X - 10);
                Canvas.SetTop(visualNo, clique.Y - 10);
                RedeCanvas.Children.Add(visualNo);

                // Cria o Nó matemático na memória da rede
                var novoId = Guid.NewGuid();
                var node = new HydraulicNode { Id = novoId, Name = $"Nó {_network.Nodes.Count + 1}", Type = HydraulicNodeType.Junction, ElevationMeters = 0, DemandLitersPerSecond = 0, RelativeWeight = 0 };
                
                _network.AddNode(node);
                _mapaNosVisuais[visualNo] = novoId; // Mapeia o visual para a matemática
                
                AtualizarListaNos(); // Atualiza a tabela na Aba 3 instantaneamente
            }
            
            // 2. MODO: CONECTAR TRECHO
            else if (_modoAtual == ModoDesenho.ConectarTrecho)
            {
                if (e.OriginalSource is Ellipse noClicado)
                {
                    if (_noInicialParaTrecho == null)
                    {
                        // Seleciona o primeiro nó (Montante) e pinta de Laranja
                        _noInicialParaTrecho = noClicado;
                        _noInicialParaTrecho.Fill = Brushes.DarkOrange; 
                    }
                    else
                    {
                        // Seleciona o segundo nó (Jusante)
                        if (_noInicialParaTrecho != noClicado)
                        {
                            Guid idMontante = _mapaNosVisuais[_noInicialParaTrecho];
                            Guid idJusante = _mapaNosVisuais[noClicado];

                            // Desenha o tubo (Line)
                            var tubo = new Line
                            {
                                X1 = Canvas.GetLeft(_noInicialParaTrecho) + 10,
                                Y1 = Canvas.GetTop(_noInicialParaTrecho) + 10,
                                X2 = Canvas.GetLeft(noClicado) + 10,
                                Y2 = Canvas.GetTop(noClicado) + 10,
                                Stroke = Brushes.Gray,
                                StrokeThickness = 4,
                                Cursor = Cursors.Hand
                            };
                            
                            // Insere a linha antes das bolinhas, para ficar por trás
                            RedeCanvas.Children.Insert(0, tubo);

                            // Cria o Trecho matemático na memória da rede
                            var pipe = new HydraulicPipeSegment { Id = Guid.NewGuid(), Name = $"T-{_network.Segments.Count + 1}", StartNodeId = idMontante, EndNodeId = idJusante, LengthMeters = 5.0, InternalDiameterMm = null, AbsoluteRoughnessMm = 0.001 };
                            _network.AddSegment(pipe);
                            
                            AtualizarListaTrechos(); // Atualiza a tabela na Aba 3
                        }
                        
                        // Reseta a cor do primeiro nó e limpa a memória temporária
                        _noInicialParaTrecho.Fill = Brushes.SteelBlue;
                        _noInicialParaTrecho = null;
                    }
                }
            }
            
            // 3. MODO: SELECIONAR (Ver Propriedades)
            else if (_modoAtual == ModoDesenho.Selecionar)
            {
                if (e.OriginalSource is Ellipse noParaEditar)
                {
                    // Encontra o ID do nó clicado e joga para a janela de edição da Aba 3
                    Guid id = _mapaNosVisuais[noParaEditar];
                    var item = NosDataGrid.Items.OfType<NodeGridItem>().FirstOrDefault(n => n.Id == id);
                    if (item != null)
                    {
                        NosDataGrid.SelectedItem = item;
                        CarregarNoSelecionadoParaEdicao();
                        MessageBox.Show($"Selecionou o nó: {item.Name}.\n\nVá até a Aba 'REDE DE ÁGUA' para editar as suas propriedades (cota, pressão, etc).", "Bahr Hidro - CAD", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
        }

        // =====================================================================
        // UTILITÁRIOS GERAIS
        // =====================================================================
        private static string FormatarTipoNo(HydraulicNodeType type) => type switch { HydraulicNodeType.Reservoir => "Reservatório", HydraulicNodeType.Junction => "Nó", HydraulicNodeType.ConsumptionPoint => "Consumo", _ => type.ToString() };
        private static bool TryParsePtBrDouble(string? text, out double value) { if (string.IsNullOrWhiteSpace(text)) { value = 0; return false; } return double.TryParse(text.Trim().Replace('.', ','), NumberStyles.Float, PtBr, out value); }

        private sealed class NodeGridItem { public Guid Id { get; init; } public string Name { get; init; } = ""; public HydraulicNodeType Type { get; init; } public string TypeLabel { get; init; } = ""; public double ElevationMeters { get; init; } public string ElevationDisplay { get; init; } = ""; public double DemandLitersPerSecond { get; init; } public string DemandDisplay { get; init; } = ""; public string WeightDisplay { get; init; } = ""; }
        private sealed class SegmentGridItem { public Guid Id { get; init; } public string Name { get; init; } = ""; public string ConnectionDisplay { get; init; } = ""; public string LengthDisplay { get; init; } = ""; public string DiameterDisplay { get; init; } = ""; public string KDisplay { get; init; } = ""; public string FlowDisplay { get; init; } = ""; }
        private sealed class ResultadoTrechoGridItem { public string Name { get; init; } = ""; public string ConnectionDisplay { get; init; } = ""; public string FlowDisplay { get; init; } = ""; public string DiameterDisplay { get; init; } = ""; public string VelocityDisplay { get; init; } = ""; public string ReynoldsDisplay { get; init; } = ""; public string HeadLossDisplay { get; init; } = ""; }
        private sealed class ResultadoNoGridItem { public string Name { get; init; } = ""; public string TypeLabel { get; init; } = ""; public string ElevationDisplay { get; init; } = ""; public string HeadDisplay { get; init; } = ""; public string PressureDisplay { get; init; } = ""; public bool IsLowPressure { get; init; } }
        private class SewerFixtureViewModel { public string Name { get; set; } = ""; public int Uhc { get; set; } public int MinDiameter { get; set; } public string UhcDisplay => $"{Uhc} UHC"; public int Quantity { get; set; } = 0; }
    }
}