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

namespace BahrHidro.UI
{
    public partial class MainWindow : Window
    {
        private static readonly CultureInfo PtBr =
            CultureInfo.GetCultureInfo("pt-BR");

        private readonly PipeCatalogDiameterSelector _diameterSelector;
        private readonly HydraulicNetwork _network =
            new HydraulicNetwork("Rede Hidráulica");

        private Guid? _editingNodeId;
        private Guid? _editingSegmentId;

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
                if (!double.TryParse(VazaoTextBox.Text, NumberStyles.Float, PtBr, out double vazao) || vazao <= 0)
                {
                    MessageBox.Show("Informe uma vazao valida e maior que zero.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!double.TryParse(VelocidadeTextBox.Text, NumberStyles.Float, PtBr, out double velocidadeLimite) || velocidadeLimite <= 0)
                {
                    MessageBox.Show("Informe uma velocidade limite valida.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double diametroMinimo = PipeSizing.CalculateMinimumDiameter(vazao, velocidadeLimite);
                DiametroMinimoText.Text = $"{diametroMinimo:F2} mm";

                var selecao = await _diameterSelector.SelectAsync(vazao, velocidadeLimite);

                if (selecao == null)
                {
                    DiametroSelecionadoText.Text = "Nao disponivel";
                    DescricaoTuboText.Text = "Nenhum produto cadastrado atende ao criterio.";
                    VelocidadeRealText.Text = "--";
                    StatusText.Text = "CATALOGO INSUFICIENTE";
                    StatusText.Foreground = Brushes.DarkOrange;
                    return;
                }

                var produto = selecao.Product;
                DiametroSelecionadoText.Text = $"{produto.InternalDiameterMm:F2} mm";
                DescricaoTuboText.Text = $"{produto.Manufacturer} | {produto.ProductLine} | {produto.NominalDesignation}";
                VelocidadeRealText.Text = $"{selecao.ActualVelocityMetersPerSecond:F3} m/s";

                bool atende = selecao.ActualVelocityMetersPerSecond <= velocidadeLimite;
                StatusText.Text = atende ? "ATENDE AO LIMITE DE VELOCIDADE" : "NAO ATENDE AO LIMITE DE VELOCIDADE";
                StatusText.Foreground = atende ? Brushes.ForestGreen : Brushes.DarkRed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro no dimensionamento:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
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
                if (selecao == null)
                {
                    MessageBox.Show("Nenhum tubo adequado foi encontrado no catalogo.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double diametroInterno = (double)selecao.Product.InternalDiameterMm;
                HydraulicSegmentResult resultado = HydraulicSegmentCalculator.Calculate(vazao, comprimento, diametroInterno, rugosidade, viscosidade, coeficienteK);

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
                MessageBox.Show($"Erro na analise hidraulica:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =====================================================================
        // ABA 3: REDE HIDRAULICA - ETAPA 1 (NÓS E RESERVATÓRIOS)
        // =====================================================================
        private void NoTipoComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NoDemandaTextBox == null || NoAjudaTipoText == null) return;
            AtualizarEstadoTipoNo();
        }

        private void AtualizarEstadoTipoNo()
        {
            HydraulicNodeType tipo = ObterTipoNoSelecionado();
            switch (tipo)
            {
                case HydraulicNodeType.Reservoir:
                    NoDemandaTextBox.Text = "0,00";
                    NoDemandaTextBox.IsEnabled = false;
                    NoAjudaTipoText.Text = "Reservatório: define a cota piezométrica de origem da rede (demanda fixa em 0,00 L/s).";
                    break;
                case HydraulicNodeType.Junction:
                    NoDemandaTextBox.IsEnabled = true;
                    if (string.IsNullOrWhiteSpace(NoDemandaTextBox.Text)) NoDemandaTextBox.Text = "0,00";
                    NoAjudaTipoText.Text = "Nó de Distribuição: ponto de derivação, mudança de diâmetro ou cota (normalmente demanda 0,00 L/s).";
                    break;
                case HydraulicNodeType.ConsumptionPoint:
                    NoDemandaTextBox.IsEnabled = true;
                    NoAjudaTipoText.Text = "Ponto de Consumo: peça de utilização ou saída da rede; informe a cota e a vazão de demanda (L/s).";
                    break;
            }
        }

        private HydraulicNodeType ObterTipoNoSelecionado()
        {
            if (NoTipoComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse(tag, out HydraulicNodeType parsed))
                return parsed;
            return HydraulicNodeType.Junction;
        }

        private void SelecionarTipoNoNoComboBox(HydraulicNodeType type)
        {
            string targetTag = type.ToString();
            foreach (var obj in NoTipoComboBox.Items)
            {
                if (obj is ComboBoxItem item && string.Equals(item.Tag as string, targetTag, StringComparison.Ordinal))
                {
                    NoTipoComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void SalvarNo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string nome = (NoNomeTextBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(nome)) { MessageBox.Show("Informe o nome.", "Bahr Hidro"); return; }

                bool nomeDuplicado = _network.Nodes.Any(n => n.Id != _editingNodeId && string.Equals(n.Name, nome, StringComparison.OrdinalIgnoreCase));
                if (nomeDuplicado) { MessageBox.Show("Já existe um elemento cadastrado com este nome.", "Bahr Hidro"); return; }

                if (!TryParsePtBrDouble(NoCotaTextBox.Text, out double cota)) { MessageBox.Show("Informe uma cota válida.", "Bahr Hidro"); return; }

                HydraulicNodeType tipo = ObterTipoNoSelecionado();
                double demanda = 0.0;
                if (tipo != HydraulicNodeType.Reservoir)
                {
                    if (!TryParsePtBrDouble(NoDemandaTextBox.Text, out demanda) || demanda < 0) { MessageBox.Show("Informe uma demanda válida.", "Bahr Hidro"); return; }
                }

                if (_editingNodeId.HasValue)
                {
                    _network.UpdateNode(new HydraulicNode { Id = _editingNodeId.Value, Name = nome, Type = tipo, ElevationMeters = cota, DemandLitersPerSecond = demanda });
                }
                else
                {
                    _network.AddNode(new HydraulicNode { Name = nome, Type = tipo, ElevationMeters = cota, DemandLitersPerSecond = demanda });
                }

                LimparFormularioNo();
                AtualizarListaNos();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Não foi possível salvar o elemento:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void EditarNo_Click(object sender, RoutedEventArgs e) => CarregarNoSelecionadoParaEdicao();
        private void NosDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (NosDataGrid.SelectedItem is NodeGridItem) CarregarNoSelecionadoParaEdicao();
        }

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
            SalvarNoButton.Content = "SALVAR ALTERAÇÕES";
        }

        private void ExcluirNo_Click(object sender, RoutedEventArgs e)
        {
            if (NosDataGrid.SelectedItem is not NodeGridItem item) return;
            var confirmacao = MessageBox.Show($"Deseja realmente excluir '{item.Name}'?", "Bahr Hidro", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmacao != MessageBoxResult.Yes) return;

            try
            {
                _network.RemoveNode(item.Id);
                if (_editingNodeId == item.Id) LimparFormularioNo();
                AtualizarListaNos();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelarEdicaoNo_Click(object sender, RoutedEventArgs e) => LimparFormularioNo();

        private void LimparFormularioNo()
        {
            _editingNodeId = null;
            NoNomeTextBox.Text = string.Empty;
            NoCotaTextBox.Text = "0,00";
            NoDemandaTextBox.Text = "0,00";
            AtualizarEstadoTipoNo();

            NoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
            NoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
            NoModoEdicaoText.Text = "Modo: Novo elemento";
            SalvarNoButton.Content = "ADICIONAR ELEMENTO";
        }

        private void AtualizarListaNos()
        {
            var itens = _network.Nodes.Select(node => new NodeGridItem
            {
                Id = node.Id,
                Name = node.Name,
                Type = node.Type,
                TypeLabel = FormatarTipoNo(node.Type),
                ElevationMeters = node.ElevationMeters,
                ElevationDisplay = node.ElevationMeters.ToString("F2", PtBr),
                DemandLitersPerSecond = node.DemandLitersPerSecond,
                DemandDisplay = node.DemandLitersPerSecond.ToString("F3", PtBr)
            }).ToList();

            NosDataGrid.ItemsSource = itens;

            int total = _network.Nodes.Count;
            double demandaTotal = _network.Nodes.Sum(n => n.DemandLitersPerSecond);
            ResumoRedeText.Text = $"Total: {total} elementos cadastrados | Demanda total: {demandaTotal.ToString("F3", PtBr)} L/s";

            AtualizarCombosNosTrecho(itens);
            AtualizarListaTrechos();
        }

        private void AtualizarCombosNosTrecho(List<NodeGridItem> itens)
        {
            if (TrechoNoInicialComboBox == null || TrechoNoFinalComboBox == null) return;
            Guid? selectedStartId = TrechoNoInicialComboBox.SelectedValue as Guid?;
            Guid? selectedEndId = TrechoNoFinalComboBox.SelectedValue as Guid?;

            TrechoNoInicialComboBox.ItemsSource = itens;
            TrechoNoFinalComboBox.ItemsSource = itens;

            if (selectedStartId.HasValue && itens.Any(i => i.Id == selectedStartId.Value)) TrechoNoInicialComboBox.SelectedValue = selectedStartId.Value;
            if (selectedEndId.HasValue && itens.Any(i => i.Id == selectedEndId.Value)) TrechoNoFinalComboBox.SelectedValue = selectedEndId.Value;
        }

        // =====================================================================
        // ABA 3: REDE HIDRAULICA - ETAPA 2 (TRECHOS DE TUBULACAO)
        // =====================================================================
        private void SalvarTrecho_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_network.Nodes.Count < 2) { MessageBox.Show("Cadastre pelo menos dois elementos.", "Bahr Hidro"); return; }

                string nome = (TrechoNomeTextBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(nome)) { MessageBox.Show("Informe o nome do trecho.", "Bahr Hidro"); return; }
                if (_network.Segments.Any(s => s.Id != _editingSegmentId && string.Equals(s.Name, nome, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("Nome de trecho duplicado.", "Bahr Hidro"); return; }
                if (TrechoNoInicialComboBox.SelectedValue is not Guid startNodeId) { MessageBox.Show("Selecione o nó inicial.", "Bahr Hidro"); return; }
                if (TrechoNoFinalComboBox.SelectedValue is not Guid endNodeId) { MessageBox.Show("Selecione o nó final.", "Bahr Hidro"); return; }
                if (startNodeId == endNodeId) { MessageBox.Show("Nó inicial e final devem ser diferentes.", "Bahr Hidro"); return; }

                if (!TryParsePtBrDouble(TrechoComprimentoTextBox.Text, out double comprimento) || comprimento <= 0) return;
                if (!TryParsePtBrDouble(TrechoDiametroTextBox.Text, out double diametroInterno) || diametroInterno <= 0) return;
                if (!TryParsePtBrDouble(TrechoRugosidadeTextBox.Text, out double rugosidade) || rugosidade < 0) return;
                if (!TryParsePtBrDouble(TrechoCoeficienteKTextBox.Text, out double coeficienteK) || coeficienteK < 0) return;

                double? vazaoTrecho = null;
                if (!string.IsNullOrWhiteSpace(TrechoVazaoTextBox.Text))
                {
                    if (TryParsePtBrDouble(TrechoVazaoTextBox.Text, out double vazaoInformada) && vazaoInformada >= 0)
                        vazaoTrecho = vazaoInformada;
                }

                var seg = new HydraulicPipeSegment
                {
                    Id = _editingSegmentId ?? Guid.NewGuid(),
                    Name = nome,
                    StartNodeId = startNodeId,
                    EndNodeId = endNodeId,
                    LengthMeters = comprimento,
                    InternalDiameterMm = diametroInterno,
                    AbsoluteRoughnessMm = rugosidade,
                    TotalLossCoefficientK = coeficienteK,
                    FlowLitersPerSecond = vazaoTrecho
                };

                if (_editingSegmentId.HasValue) _network.UpdateSegment(seg);
                else _network.AddSegment(seg);

                LimparFormularioTrecho();
                AtualizarListaTrechos();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Não foi possível salvar o trecho:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void EditarTrecho_Click(object sender, RoutedEventArgs e) => CarregarTrechoSelecionadoParaEdicao();
        private void TrechosDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TrechosDataGrid.SelectedItem is SegmentGridItem) CarregarTrechoSelecionadoParaEdicao();
        }

        private void CarregarTrechoSelecionadoParaEdicao()
        {
            if (TrechosDataGrid.SelectedItem is not SegmentGridItem item) return;
            var segment = _network.FindSegment(item.Id);
            if (segment == null) return;

            _editingSegmentId = segment.Id;
            TrechoNomeTextBox.Text = segment.Name;
            TrechoNoInicialComboBox.SelectedValue = segment.StartNodeId;
            TrechoNoFinalComboBox.SelectedValue = segment.EndNodeId;
            TrechoComprimentoTextBox.Text = segment.LengthMeters.ToString("F2", PtBr);
            TrechoDiametroTextBox.Text = segment.InternalDiameterMm.ToString("F2", PtBr);
            TrechoRugosidadeTextBox.Text = segment.AbsoluteRoughnessMm.ToString("G", PtBr);
            TrechoCoeficienteKTextBox.Text = segment.TotalLossCoefficientK.ToString("F2", PtBr);
            TrechoVazaoTextBox.Text = segment.FlowLitersPerSecond?.ToString("F3", PtBr) ?? string.Empty;

            TrechoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7"));
            TrechoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B45309"));
            TrechoModoEdicaoText.Text = $"Modo: Editando '{segment.Name}'";
            SalvarTrechoButton.Content = "SALVAR ALTERAÇÕES";
        }

        private void ExcluirTrecho_Click(object sender, RoutedEventArgs e)
        {
            if (TrechosDataGrid.SelectedItem is not SegmentGridItem item) return;
            if (MessageBox.Show($"Deseja realmente excluir '{item.Name}'?", "Bahr Hidro", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _network.RemoveSegment(item.Id);
            if (_editingSegmentId == item.Id) LimparFormularioTrecho();
            AtualizarListaTrechos();
        }

        private void CancelarEdicaoTrecho_Click(object sender, RoutedEventArgs e) => LimparFormularioTrecho();

        private void LimparFormularioTrecho()
        {
            _editingSegmentId = null;
            TrechoNomeTextBox.Text = string.Empty;
            TrechoNoInicialComboBox.SelectedIndex = -1;
            TrechoNoFinalComboBox.SelectedIndex = -1;
            TrechoComprimentoTextBox.Text = "10,00";
            TrechoDiametroTextBox.Text = "21,60";
            TrechoRugosidadeTextBox.Text = "0,001";
            TrechoCoeficienteKTextBox.Text = "0";
            TrechoVazaoTextBox.Text = string.Empty;

            TrechoModoEdicaoBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
            TrechoModoEdicaoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
            TrechoModoEdicaoText.Text = "Modo: Novo trecho";
            SalvarTrechoButton.Content = "ADICIONAR TRECHO";
        }

        private void AtualizarListaTrechos()
        {
            if (TrechosDataGrid == null || ResumoTrechosText == null) return;
            var itens = _network.Segments.Select(segment => new SegmentGridItem
            {
                Id = segment.Id,
                Name = segment.Name,
                ConnectionDisplay = $"{_network.FindNode(segment.StartNodeId)?.Name ?? "?"} → {_network.FindNode(segment.EndNodeId)?.Name ?? "?"}",
                LengthDisplay = segment.LengthMeters.ToString("F2", PtBr),
                DiameterDisplay = segment.InternalDiameterMm.ToString("F2", PtBr),
                KDisplay = segment.TotalLossCoefficientK.ToString("F2", PtBr),
                FlowDisplay = segment.FlowLitersPerSecond?.ToString("F3", PtBr) ?? "Auto"
            }).ToList();

            TrechosDataGrid.ItemsSource = itens;
            ResumoTrechosText.Text = $"Total: {_network.Segments.Count} trecho(s) | Extensão total da rede: {_network.Segments.Sum(s => s.LengthMeters).ToString("F2", PtBr)} m";
        }

        // =====================================================================
        // ABA 3: REDE HIDRÁULICA - ETAPA 3 (CÁLCULO E RESULTADOS)
        // =====================================================================
        private void CalcularRede_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!TryParsePtBrDouble(RedeViscosidadeTextBox.Text, out double viscosidade) || viscosidade <= 0)
                {
                    MessageBox.Show("Informe uma viscosidade cinemática válida.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 1. O Core orquestra tudo com uma única chamada, sem misturar matemática na interface
                var resultados = HydraulicNetworkSolver.Solve(_network, viscosidade);

                // 2. Mapear os resultados dos trechos para a tabela
                var trechosVisual = resultados.Segments.Select(s => new ResultadoTrechoGridItem
                {
                    Name = s.SegmentName,
                    ConnectionDisplay = s.Connection,
                    FlowDisplay = s.FlowLitersPerSecond.ToString("F3", PtBr),
                    VelocityDisplay = s.VelocityMetersPerSecond.ToString("F3", PtBr),
                    ReynoldsDisplay = s.ReynoldsNumber.ToString("F0", PtBr),
                    HeadLossDisplay = s.TotalHeadLossMeters.ToString("F4", PtBr)
                }).ToList();

                ResultadosTrechosDataGrid.ItemsSource = trechosVisual;

                // 3. Mapear os resultados de pressões nos nós para a tabela
                var nosVisual = resultados.Nodes.Select(n => new ResultadoNoGridItem
                {
                    Name = n.NodeName,
                    TypeLabel = FormatarTipoNo(n.NodeType),
                    ElevationDisplay = n.ElevationMeters.ToString("F2", PtBr),
                    HeadDisplay = n.PiezometricHeadMeters.ToString("F3", PtBr),
                    PressureDisplay = n.AvailablePressureMca.ToString("F3", PtBr),
                    // Se a pressão for menor que 1.0 m.c.a. (10 kPa da norma NBR 5626), fica vermelho na tela
                    IsLowPressure = n.AvailablePressureMca < 1.0 && n.NodeType != HydraulicNodeType.Reservoir
                }).ToList();

                ResultadosNosDataGrid.ItemsSource = nosVisual;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"O cálculo da rede falhou. Verifique se há um reservatório único, se não há desconexões ou loops na rede.\n\nDetalhe do erro: {ex.Message}", 
                    "Erro de Cálculo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =====================================================================
        // UTILITÁRIOS E CLASSES DE BINDING
        // =====================================================================
        private static string FormatarTipoNo(HydraulicNodeType type) =>
            type switch { HydraulicNodeType.Reservoir => "Reservatório", HydraulicNodeType.Junction => "Nó de Distribuição", HydraulicNodeType.ConsumptionPoint => "Ponto de Consumo", _ => type.ToString() };

        private static bool TryParsePtBrDouble(string? text, out double value)
        {
            if (string.IsNullOrWhiteSpace(text)) { value = 0; return false; }
            return double.TryParse(text.Trim().Replace('.', ','), NumberStyles.Float, PtBr, out value);
        }

        // Classes exclusivas para visualização nas tabelas
        private sealed class NodeGridItem
        {
            public Guid Id { get; init; }
            public string Name { get; init; } = "";
            public HydraulicNodeType Type { get; init; }
            public string TypeLabel { get; init; } = "";
            public double ElevationMeters { get; init; }
            public string ElevationDisplay { get; init; } = "";
            public double DemandLitersPerSecond { get; init; }
            public string DemandDisplay { get; init; } = "";
        }

        private sealed class SegmentGridItem
        {
            public Guid Id { get; init; }
            public string Name { get; init; } = "";
            public string ConnectionDisplay { get; init; } = "";
            public string LengthDisplay { get; init; } = "";
            public string DiameterDisplay { get; init; } = "";
            public string KDisplay { get; init; } = "";
            public string FlowDisplay { get; init; } = "";
        }

        private sealed class ResultadoTrechoGridItem
        {
            public string Name { get; init; } = "";
            public string ConnectionDisplay { get; init; } = "";
            public string FlowDisplay { get; init; } = "";
            public string VelocityDisplay { get; init; } = "";
            public string ReynoldsDisplay { get; init; } = "";
            public string HeadLossDisplay { get; init; } = "";
        }

        private sealed class ResultadoNoGridItem
        {
            public string Name { get; init; } = "";
            public string TypeLabel { get; init; } = "";
            public string ElevationDisplay { get; init; } = "";
            public string HeadDisplay { get; init; } = "";
            public string PressureDisplay { get; init; } = "";
            public bool IsLowPressure { get; init; }
        }
    }
}