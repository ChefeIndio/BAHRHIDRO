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

        // =====================================================================
        // ABA 3: REDE HIDRAULICA - ETAPA 1 (CADASTRO DE NOS E RESERVATORIOS)
        // =====================================================================

        private void NoTipoComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (NoDemandaTextBox == null || NoAjudaTipoText == null)
                return;

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
                    NoAjudaTipoText.Text =
                        "Reservatório: define a cota piezométrica de origem da rede (demanda fixa em 0,00 L/s).";
                    break;

                case HydraulicNodeType.Junction:
                    NoDemandaTextBox.IsEnabled = true;
                    if (string.IsNullOrWhiteSpace(NoDemandaTextBox.Text))
                        NoDemandaTextBox.Text = "0,00";
                    NoAjudaTipoText.Text =
                        "Nó de Distribuição: ponto de derivação, mudança de diâmetro ou cota (normalmente demanda 0,00 L/s).";
                    break;

                case HydraulicNodeType.ConsumptionPoint:
                    NoDemandaTextBox.IsEnabled = true;
                    NoAjudaTipoText.Text =
                        "Ponto de Consumo: peça de utilização ou saída da rede; informe a cota e a vazão de demanda (L/s).";
                    break;
            }
        }

        private HydraulicNodeType ObterTipoNoSelecionado()
        {
            if (NoTipoComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag &&
                Enum.TryParse(tag, out HydraulicNodeType parsed))
            {
                return parsed;
            }

            return HydraulicNodeType.Junction;
        }

        private void SelecionarTipoNoNoComboBox(HydraulicNodeType type)
        {
            string targetTag = type.ToString();

            foreach (var obj in NoTipoComboBox.Items)
            {
                if (obj is ComboBoxItem item &&
                    string.Equals(item.Tag as string, targetTag, StringComparison.Ordinal))
                {
                    NoTipoComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void SalvarNo_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                string nome = (NoNomeTextBox.Text ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(nome))
                {
                    MessageBox.Show(
                        "Informe o nome ou identificação do elemento.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    NoNomeTextBox.Focus();
                    return;
                }

                bool nomeDuplicado = _network.Nodes.Any(n =>
                    n.Id != _editingNodeId &&
                    string.Equals(n.Name, nome, StringComparison.OrdinalIgnoreCase));

                if (nomeDuplicado)
                {
                    MessageBox.Show(
                        $"Já existe um elemento cadastrado com o nome '{nome}'. Utilize uma identificação única.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    NoNomeTextBox.Focus();
                    return;
                }

                if (!TryParsePtBrDouble(NoCotaTextBox.Text, out double cota) ||
                    !double.IsFinite(cota))
                {
                    MessageBox.Show(
                        "Informe uma cota / elevação numérica válida (em metros).",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    NoCotaTextBox.Focus();
                    return;
                }

                HydraulicNodeType tipo = ObterTipoNoSelecionado();
                double demanda = 0.0;

                if (tipo != HydraulicNodeType.Reservoir)
                {
                    if (!TryParsePtBrDouble(NoDemandaTextBox.Text, out demanda) ||
                        !double.IsFinite(demanda) ||
                        demanda < 0)
                    {
                        MessageBox.Show(
                            "Informe uma demanda válida (maior ou igual a zero, em L/s).",
                            "Bahr Hidro",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        NoDemandaTextBox.Focus();
                        return;
                    }

                    if (tipo == HydraulicNodeType.ConsumptionPoint && demanda <= 0)
                    {
                        MessageBox.Show(
                            "Um ponto de consumo deve possuir demanda maior que zero (L/s).",
                            "Bahr Hidro",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        NoDemandaTextBox.Focus();
                        return;
                    }
                }

                if (_editingNodeId.HasValue)
                {
                    var noAtualizado = new HydraulicNode
                    {
                        Id = _editingNodeId.Value,
                        Name = nome,
                        Type = tipo,
                        ElevationMeters = cota,
                        DemandLitersPerSecond = demanda
                    };

                    _network.UpdateNode(noAtualizado);
                }
                else
                {
                    var novoNo = new HydraulicNode
                    {
                        Name = nome,
                        Type = tipo,
                        ElevationMeters = cota,
                        DemandLitersPerSecond = demanda
                    };

                    _network.AddNode(novoNo);
                }

                LimparFormularioNo();
                AtualizarListaNos();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Não foi possível salvar o elemento:\n{ex.Message}",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void EditarNo_Click(
            object sender,
            RoutedEventArgs e)
        {
            CarregarNoSelecionadoParaEdicao();
        }

        private void NosDataGrid_MouseDoubleClick(
            object sender,
            MouseButtonEventArgs e)
        {
            if (NosDataGrid.SelectedItem is NodeGridItem)
            {
                CarregarNoSelecionadoParaEdicao();
            }
        }

        private void CarregarNoSelecionadoParaEdicao()
        {
            if (NosDataGrid.SelectedItem is not NodeGridItem item)
            {
                MessageBox.Show(
                    "Selecione um elemento na tabela para editar.",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var node = _network.FindNode(item.Id);

            if (node == null)
                return;

            _editingNodeId = node.Id;

            SelecionarTipoNoNoComboBox(node.Type);
            AtualizarEstadoTipoNo();

            NoNomeTextBox.Text = node.Name;
            NoCotaTextBox.Text = node.ElevationMeters.ToString("F2", PtBr);
            NoDemandaTextBox.Text = node.DemandLitersPerSecond.ToString("F3", PtBr);

            NoModoEdicaoBadge.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#FEF3C7"));
            NoModoEdicaoText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#B45309"));
            NoModoEdicaoText.Text = $"Modo: Editando '{node.Name}'";

            SalvarNoButton.Content = "SALVAR ALTERAÇÕES";
            NoNomeTextBox.Focus();
            NoNomeTextBox.SelectAll();
        }

        private void ExcluirNo_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (NosDataGrid.SelectedItem is not NodeGridItem item)
            {
                MessageBox.Show(
                    "Selecione um elemento na tabela para excluir.",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirmacao = MessageBox.Show(
                $"Deseja realmente excluir o elemento '{item.Name}'?",
                "Bahr Hidro",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmacao != MessageBoxResult.Yes)
                return;

            try
            {
                _network.RemoveNode(item.Id);

                if (_editingNodeId == item.Id)
                {
                    LimparFormularioNo();
                }

                AtualizarListaNos();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Não foi possível excluir o elemento:\n{ex.Message}",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void CancelarEdicaoNo_Click(
            object sender,
            RoutedEventArgs e)
        {
            LimparFormularioNo();
        }

        private void LimparFormularioNo()
        {
            _editingNodeId = null;

            NoNomeTextBox.Text = string.Empty;
            NoCotaTextBox.Text = "0,00";
            NoDemandaTextBox.Text = "0,00";

            AtualizarEstadoTipoNo();

            NoModoEdicaoBadge.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#EFF6FF"));
            NoModoEdicaoText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#1D4ED8"));
            NoModoEdicaoText.Text = "Modo: Novo elemento";

            SalvarNoButton.Content = "ADICIONAR ELEMENTO";
            NoNomeTextBox.Focus();
        }

        private void AtualizarListaNos()
        {
            var itens = new List<NodeGridItem>();

            foreach (var node in _network.Nodes)
            {
                itens.Add(new NodeGridItem
                {
                    Id = node.Id,
                    Name = node.Name,
                    Type = node.Type,
                    TypeLabel = FormatarTipoNo(node.Type),
                    ElevationMeters = node.ElevationMeters,
                    ElevationDisplay = node.ElevationMeters.ToString("F2", PtBr),
                    DemandLitersPerSecond = node.DemandLitersPerSecond,
                    DemandDisplay = node.DemandLitersPerSecond.ToString("F3", PtBr)
                });
            }

            NosDataGrid.ItemsSource = itens;

            int total = _network.Nodes.Count;
            int reservatorios = _network.Nodes.Count(n => n.Type == HydraulicNodeType.Reservoir);
            int nos = _network.Nodes.Count(n => n.Type == HydraulicNodeType.Junction);
            int pontosConsumo = _network.Nodes.Count(n => n.Type == HydraulicNodeType.ConsumptionPoint);
            double demandaTotal = _network.Nodes.Sum(n => n.DemandLitersPerSecond);

            ResumoRedeText.Text =
                $"Total: {total} ({reservatorios} reservatório(s), {nos} nó(s), {pontosConsumo} ponto(s) de consumo) | " +
                $"Demanda total: {demandaTotal.ToString("F3", PtBr)} L/s";

            AtualizarCombosNosTrecho(itens);
            AtualizarListaTrechos();
        }

        private void AtualizarCombosNosTrecho(List<NodeGridItem> itens)
        {
            if (TrechoNoInicialComboBox == null || TrechoNoFinalComboBox == null)
                return;

            Guid? selectedStartId = TrechoNoInicialComboBox.SelectedValue as Guid?;
            Guid? selectedEndId = TrechoNoFinalComboBox.SelectedValue as Guid?;

            TrechoNoInicialComboBox.ItemsSource = itens;
            TrechoNoFinalComboBox.ItemsSource = itens;

            if (selectedStartId.HasValue && itens.Any(i => i.Id == selectedStartId.Value))
                TrechoNoInicialComboBox.SelectedValue = selectedStartId.Value;

            if (selectedEndId.HasValue && itens.Any(i => i.Id == selectedEndId.Value))
                TrechoNoFinalComboBox.SelectedValue = selectedEndId.Value;
        }

        // =====================================================================
        // ABA 3: REDE HIDRAULICA - ETAPA 2 (TRECHOS DE TUBULACAO ENTRE NOS)
        // =====================================================================

        private void SalvarTrecho_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                if (_network.Nodes.Count < 2)
                {
                    MessageBox.Show(
                        "Cadastre pelo menos dois elementos (nós ou reservatórios) antes de criar um trecho.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string nome = (TrechoNomeTextBox.Text ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(nome))
                {
                    MessageBox.Show(
                        "Informe o nome ou identificação do trecho.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoNomeTextBox.Focus();
                    return;
                }

                bool nomeDuplicado = _network.Segments.Any(s =>
                    s.Id != _editingSegmentId &&
                    string.Equals(s.Name, nome, StringComparison.OrdinalIgnoreCase));

                if (nomeDuplicado)
                {
                    MessageBox.Show(
                        $"Já existe um trecho cadastrado com o nome '{nome}'. Utilize uma identificação única.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoNomeTextBox.Focus();
                    return;
                }

                if (TrechoNoInicialComboBox.SelectedValue is not Guid startNodeId ||
                    startNodeId == Guid.Empty)
                {
                    MessageBox.Show(
                        "Selecione o nó inicial (montante) do trecho.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoNoInicialComboBox.Focus();
                    return;
                }

                if (TrechoNoFinalComboBox.SelectedValue is not Guid endNodeId ||
                    endNodeId == Guid.Empty)
                {
                    MessageBox.Show(
                        "Selecione o nó final (jusante) do trecho.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoNoFinalComboBox.Focus();
                    return;
                }

                if (startNodeId == endNodeId)
                {
                    MessageBox.Show(
                        "O nó inicial e o nó final devem ser diferentes.",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoNoFinalComboBox.Focus();
                    return;
                }

                if (!TryParsePtBrDouble(TrechoComprimentoTextBox.Text, out double comprimento) ||
                    !double.IsFinite(comprimento) ||
                    comprimento <= 0)
                {
                    MessageBox.Show(
                        "Informe um comprimento válido e maior que zero (em metros).",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoComprimentoTextBox.Focus();
                    return;
                }

                if (!TryParsePtBrDouble(TrechoDiametroTextBox.Text, out double diametroInterno) ||
                    !double.IsFinite(diametroInterno) ||
                    diametroInterno <= 0)
                {
                    MessageBox.Show(
                        "Informe um diâmetro interno válido e maior que zero (em mm).",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoDiametroTextBox.Focus();
                    return;
                }

                if (!TryParsePtBrDouble(TrechoRugosidadeTextBox.Text, out double rugosidade) ||
                    !double.IsFinite(rugosidade) ||
                    rugosidade < 0)
                {
                    MessageBox.Show(
                        "Informe uma rugosidade absoluta válida (maior ou igual a zero, em mm).",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoRugosidadeTextBox.Focus();
                    return;
                }

                if (!TryParsePtBrDouble(TrechoCoeficienteKTextBox.Text, out double coeficienteK) ||
                    !double.IsFinite(coeficienteK) ||
                    coeficienteK < 0)
                {
                    MessageBox.Show(
                        "Informe um coeficiente de perdas localizadas K válido (maior ou igual a zero).",
                        "Bahr Hidro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    TrechoCoeficienteKTextBox.Focus();
                    return;
                }

                double? vazaoTrecho = null;

                if (!string.IsNullOrWhiteSpace(TrechoVazaoTextBox.Text))
                {
                    if (!TryParsePtBrDouble(TrechoVazaoTextBox.Text, out double vazaoInformada) ||
                        !double.IsFinite(vazaoInformada) ||
                        vazaoInformada < 0)
                    {
                        MessageBox.Show(
                            "Informe uma vazão válida (maior ou igual a zero, em L/s) ou deixe em branco para cálculo automático.",
                            "Bahr Hidro",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        TrechoVazaoTextBox.Focus();
                        return;
                    }

                    vazaoTrecho = vazaoInformada;
                }

                if (_editingSegmentId.HasValue)
                {
                    var trechoAtualizado = new HydraulicPipeSegment
                    {
                        Id = _editingSegmentId.Value,
                        Name = nome,
                        StartNodeId = startNodeId,
                        EndNodeId = endNodeId,
                        LengthMeters = comprimento,
                        InternalDiameterMm = diametroInterno,
                        AbsoluteRoughnessMm = rugosidade,
                        TotalLossCoefficientK = coeficienteK,
                        FlowLitersPerSecond = vazaoTrecho
                    };

                    _network.UpdateSegment(trechoAtualizado);
                }
                else
                {
                    var novoTrecho = new HydraulicPipeSegment
                    {
                        Name = nome,
                        StartNodeId = startNodeId,
                        EndNodeId = endNodeId,
                        LengthMeters = comprimento,
                        InternalDiameterMm = diametroInterno,
                        AbsoluteRoughnessMm = rugosidade,
                        TotalLossCoefficientK = coeficienteK,
                        FlowLitersPerSecond = vazaoTrecho
                    };

                    _network.AddSegment(novoTrecho);
                }

                LimparFormularioTrecho();
                AtualizarListaTrechos();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Não foi possível salvar o trecho:\n{ex.Message}",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void EditarTrecho_Click(
            object sender,
            RoutedEventArgs e)
        {
            CarregarTrechoSelecionadoParaEdicao();
        }

        private void TrechosDataGrid_MouseDoubleClick(
            object sender,
            MouseButtonEventArgs e)
        {
            if (TrechosDataGrid.SelectedItem is SegmentGridItem)
            {
                CarregarTrechoSelecionadoParaEdicao();
            }
        }

        private void CarregarTrechoSelecionadoParaEdicao()
        {
            if (TrechosDataGrid.SelectedItem is not SegmentGridItem item)
            {
                MessageBox.Show(
                    "Selecione um trecho na tabela para editar.",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var segment = _network.FindSegment(item.Id);

            if (segment == null)
                return;

            _editingSegmentId = segment.Id;

            TrechoNomeTextBox.Text = segment.Name;
            TrechoNoInicialComboBox.SelectedValue = segment.StartNodeId;
            TrechoNoFinalComboBox.SelectedValue = segment.EndNodeId;
            TrechoComprimentoTextBox.Text = segment.LengthMeters.ToString("F2", PtBr);
            TrechoDiametroTextBox.Text = segment.InternalDiameterMm.ToString("F2", PtBr);
            TrechoRugosidadeTextBox.Text = segment.AbsoluteRoughnessMm.ToString("G", PtBr);
            TrechoCoeficienteKTextBox.Text = segment.TotalLossCoefficientK.ToString("F2", PtBr);
            TrechoVazaoTextBox.Text = segment.FlowLitersPerSecond.HasValue
                ? segment.FlowLitersPerSecond.Value.ToString("F3", PtBr)
                : string.Empty;

            TrechoModoEdicaoBadge.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#FEF3C7"));
            TrechoModoEdicaoText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#B45309"));
            TrechoModoEdicaoText.Text = $"Modo: Editando '{segment.Name}'";

            SalvarTrechoButton.Content = "SALVAR ALTERAÇÕES";
            TrechoNomeTextBox.Focus();
            TrechoNomeTextBox.SelectAll();
        }

        private void ExcluirTrecho_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (TrechosDataGrid.SelectedItem is not SegmentGridItem item)
            {
                MessageBox.Show(
                    "Selecione um trecho na tabela para excluir.",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirmacao = MessageBox.Show(
                $"Deseja realmente excluir o trecho '{item.Name}'?",
                "Bahr Hidro",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmacao != MessageBoxResult.Yes)
                return;

            try
            {
                _network.RemoveSegment(item.Id);

                if (_editingSegmentId == item.Id)
                {
                    LimparFormularioTrecho();
                }

                AtualizarListaTrechos();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Não foi possível excluir o trecho:\n{ex.Message}",
                    "Bahr Hidro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void CancelarEdicaoTrecho_Click(
            object sender,
            RoutedEventArgs e)
        {
            LimparFormularioTrecho();
        }

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

            TrechoModoEdicaoBadge.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#EFF6FF"));
            TrechoModoEdicaoText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#1D4ED8"));
            TrechoModoEdicaoText.Text = "Modo: Novo trecho";

            SalvarTrechoButton.Content = "ADICIONAR TRECHO";
            TrechoNomeTextBox.Focus();
        }

        private void AtualizarListaTrechos()
        {
            if (TrechosDataGrid == null || ResumoTrechosText == null)
                return;

            var itens = new List<SegmentGridItem>();

            foreach (var segment in _network.Segments)
            {
                string startName = _network.FindNode(segment.StartNodeId)?.Name ?? "?";
                string endName = _network.FindNode(segment.EndNodeId)?.Name ?? "?";

                itens.Add(new SegmentGridItem
                {
                    Id = segment.Id,
                    Name = segment.Name,
                    ConnectionDisplay = $"{startName} → {endName}",
                    LengthDisplay = segment.LengthMeters.ToString("F2", PtBr),
                    DiameterDisplay = segment.InternalDiameterMm.ToString("F2", PtBr),
                    KDisplay = segment.TotalLossCoefficientK.ToString("F2", PtBr),
                    FlowDisplay = segment.FlowLitersPerSecond.HasValue
                        ? segment.FlowLitersPerSecond.Value.ToString("F3", PtBr)
                        : "Auto"
                });
            }

            TrechosDataGrid.ItemsSource = itens;

            int total = _network.Segments.Count;
            double extensaoTotal = _network.Segments.Sum(s => s.LengthMeters);

            ResumoTrechosText.Text =
                $"Total: {total} trecho(s) | Extensão total da rede: {extensaoTotal.ToString("F2", PtBr)} m";
        }

        private static string FormatarTipoNo(HydraulicNodeType type)
        {
            return type switch
            {
                HydraulicNodeType.Reservoir => "Reservatório",
                HydraulicNodeType.Junction => "Nó de Distribuição",
                HydraulicNodeType.ConsumptionPoint => "Ponto de Consumo",
                _ => type.ToString()
            };
        }

        private static bool TryParsePtBrDouble(string? text, out double value)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                value = 0;
                return false;
            }

            string normalized = text.Trim().Replace('.', ',');
            return double.TryParse(normalized, NumberStyles.Float, PtBr, out value);
        }

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
    }
}