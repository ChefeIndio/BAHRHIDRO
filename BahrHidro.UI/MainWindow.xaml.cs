using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        private readonly PipeCatalogService _catalogService;
        private HydraulicNetwork _network = new HydraulicNetwork("Rede Hidráulica");
        private IRevitBimBridge? _revitBridge;
        private NetworkCalculationResult? _ultimoResultadoDaRede;
        private Dictionary<Guid, double> _diametrosOriginaisRevit = new();
        private readonly HashSet<Guid> _trechosManuais = new();
        private List<ResultadoTrechoGridItem> _todosResultadosTrechos = new();
        private Guid? _trechoSelecionadoParaEdicaoId;

        private readonly List<SewerFixtureViewModel> _sewerFixtures;

        public MainWindow()
        {
            InitializeComponent();

            string catalogDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BahrHidro");
            if (!System.IO.Directory.Exists(catalogDir))
                System.IO.Directory.CreateDirectory(catalogDir);

            string catalogPath = System.IO.Path.Combine(catalogDir, "pipe-catalog.json");
            if (!System.IO.File.Exists(catalogPath))
                System.IO.File.WriteAllText(catalogPath, "[]");

            var repository = new PipeCatalogRepository(catalogPath);
            _catalogService = new PipeCatalogService(repository);

            CarregarPerfisEdificacao();
            AtualizarListaNos();
            AtualizarListaTrechos();

            _sewerFixtures = SewerFixtureCatalog.GetStandardFixtures()
                .Select(f => new SewerFixtureViewModel { Name = f.Name, Uhc = f.UhcValue, MinDiameter = f.MinimumDiameterMm })
                .ToList();
            SewerItemsControl.ItemsSource = _sewerFixtures;

            RainwaterViewControl.CalculoAtualizado += AtualizarResumoPluvialNoMemorial;
            Loaded += async (_, _) => await AtualizarStatusCatalogoInicioAsync();
        }

        public MainWindow(IRevitBimBridge revitBridge) : this()
        {
            _revitBridge = revitBridge;

            BtnImportarInicioRevit.Visibility = Visibility.Visible;
            HeaderActionsAguaFria.Visibility = Visibility.Visible;
            HeaderActionsAguaQuente.Visibility = Visibility.Visible;
            HeaderActionsReservatorios.Visibility = Visibility.Visible;
            HeaderActionsEsgoto.Visibility = Visibility.Visible;
            HeaderActionsPluvial.Visibility = Visibility.Visible;
            SalvarDadosRevitButton.Visibility = Visibility.Visible;

            var info = _revitBridge.GetProjectInfo();
            MemObraTextBox.Text = info.ProjectName;
            MemClienteTextBox.Text = info.ClientName;
            MemEnderecoTextBox.Text = info.Address;
            MemEngenheiroTextBox.Text = info.Author;

            if (!string.IsNullOrWhiteSpace(info.ProjectName))
                InicioProjetoStatusText.Text = $"Projeto Revit Conectado: {info.ProjectName} | Cliente: {info.ClientName}";
        }

        private async Task AtualizarStatusCatalogoInicioAsync()
        {
            try
            {
                var produtos = await _catalogService.GetAllAsync();
                InicioCatalogoStatusText.Text = produtos.Count > 0
                    ? $"{produtos.Count} bitola(s) comercial(is) pronta(s) no catálogo para dimensionamento automático."
                    : "Nenhuma bitola cadastrada. Clique abaixo para carregar o catálogo padrão ABNT NBR 5648.";
            }
            catch { }
        }

        private void NavegadorTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem item && item.Tag is string painelTag)
            {
                SelecionarPainel(painelTag);
            }
        }

        private void SelecionarPainel(string tag)
        {
            if (PanelInicio == null) return;

            PanelInicio.Visibility = tag == "Inicio" ? Visibility.Visible : Visibility.Collapsed;
            PanelAguaFria.Visibility = tag == "AguaFria" ? Visibility.Visible : Visibility.Collapsed;
            PanelBomba.Visibility = tag == "Bomba" ? Visibility.Visible : Visibility.Collapsed;
            PanelAguaQuente.Visibility = tag == "AguaQuente" ? Visibility.Visible : Visibility.Collapsed;
            PanelReservatorios.Visibility = tag == "Reservatorios" ? Visibility.Visible : Visibility.Collapsed;
            PanelEsgoto.Visibility = tag == "Esgoto" ? Visibility.Visible : Visibility.Collapsed;
            PanelPluvial.Visibility = tag == "Pluvial" ? Visibility.Visible : Visibility.Collapsed;
            PanelMemorial.Visibility = tag == "Memorial" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnIrInicio_Click(object sender, RoutedEventArgs e)
        {
            NavInicio.IsSelected = true;
            SelecionarPainel("Inicio");
        }

        private void BtnIrAguaFria_Click(object sender, RoutedEventArgs e)
        {
            NavAguaFria.IsSelected = true;
            SelecionarPainel("AguaFria");
        }

        private void BtnIrEsgoto_Click(object sender, RoutedEventArgs e)
        {
            NavEsgoto.IsSelected = true;
            SelecionarPainel("Esgoto");
        }

        private void BtnIrMemorial_Click(object sender, RoutedEventArgs e)
        {
            NavMemorial.IsSelected = true;
            SelecionarPainel("Memorial");
        }

        private async void BtnCarregarPadraoInicio_Click(object sender, RoutedEventArgs e)
        {
            await GarantirCatalogoPadraoNbrAsync();
            await AtualizarStatusCatalogoInicioAsync();
            MessageBox.Show("Catálogo padrão ABNT NBR 5648 (PVC Soldável DN 20 a DN 110) carregado com sucesso!", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CarregarPerfisEdificacao()
        {
            BoilerPerfilComboBox.Items.Clear();
            foreach (BuildingProfile perfil in Enum.GetValues(typeof(BuildingProfile)))
            {
                BoilerPerfilComboBox.Items.Add(new ComboBoxItem
                {
                    Content = TraduzirPerfil(perfil.ToString()),
                    Tag = perfil.ToString()
                });
            }
            if (BoilerPerfilComboBox.Items.Count > 0) BoilerPerfilComboBox.SelectedIndex = 0;

            CaixaPerfilComboBox.Items.Clear();
            foreach (BuildingCategory categoria in Enum.GetValues(typeof(BuildingCategory)))
            {
                CaixaPerfilComboBox.Items.Add(new ComboBoxItem
                {
                    Content = TraduzirPerfil(categoria.ToString()),
                    Tag = categoria.ToString()
                });
            }
            if (CaixaPerfilComboBox.Items.Count > 0) CaixaPerfilComboBox.SelectedIndex = 0;
        }

        private static string TraduzirPerfil(string nomeEnum) => nomeEnum switch
        {
            "ResidentialHigh" or "HighStandardResidential" or "ResidencialAlto" => "Residencial Padrão Alto",
            "ResidentialMedium" or "MediumStandardResidential" or "ResidencialMedio" => "Residencial Padrão Médio",
            "ResidentialLow" or "LowStandardResidential" or "ResidencialPopular" => "Residencial Padrão Popular",
            "SingleFamilyResidential" or "SingleFamily" or "House" or "Casa" or "ResidenciaUnifamiliar" => "Residência Unifamiliar",
            "MultiFamilyResidential" or "MultiFamily" or "Apartment" or "Apartamento" or "EdificioMultifamiliar" => "Edifício Multifamiliar (Apartamentos)",
            "Residential" or "Residencial" or "Residencia" => "Residência",
            "Commercial" or "Office" or "Comercial" => "Edifício Comercial / Escritórios",
            "Hotel" => "Hotel / Pousada",
            "Hospital" => "Hospital / Clínica",
            "School" or "Escola" => "Escola",
            "Gym" or "Academia" => "Academia / Vestiário",
            _ => nomeEnum
        };

        private async void AbrirCatalogo_Click(object sender, RoutedEventArgs e)
        {
            var janelaCatalogo = new PipeCatalogWindow { Owner = this };
            janelaCatalogo.ShowDialog();
            await AtualizarStatusCatalogoInicioAsync();
        }

        private void AtualizarListaNos()
        {
            var itens = _network.Nodes.Select(n => new NodeGridItem
            {
                Id = n.Id,
                Name = n.Name,
                Type = n.Type,
                TypeLabel = FormatarTipoNo(n.Type),
                ElevationMeters = n.ElevationMeters,
                ElevationDisplay = n.ElevationMeters.ToString("F2", PtBr),
                DemandLitersPerSecond = n.DemandLitersPerSecond,
                DemandDisplay = n.DemandLitersPerSecond.ToString("F3", PtBr),
                WeightDisplay = n.RelativeWeight.ToString("F2", PtBr)
            }).ToList();

            NosDataGrid.ItemsSource = itens;
            ResumoRedeText.Text = $"{_network.Nodes.Count} elemento(s)";
        }

        private void AtualizarListaTrechos()
        {
            if (TrechosDataGrid == null) return;
            TrechosDataGrid.ItemsSource = _network.Segments.Select(s => new SegmentGridItem
            {
                Id = s.Id,
                Name = s.Name,
                ConnectionDisplay = $"{_network.FindNode(s.StartNodeId)?.Name ?? "?"} → {_network.FindNode(s.EndNodeId)?.Name ?? "?"}",
                LengthDisplay = s.LengthMeters.ToString("F2", PtBr),
                DiameterDisplay = s.InternalDiameterMm.HasValue ? s.InternalDiameterMm.Value.ToString("F2", PtBr) : "Auto",
                KDisplay = s.TotalLossCoefficientK.ToString("F2", PtBr)
            }).ToList();
            ResumoTrechosText.Text = $"{_network.Segments.Count} trecho(s)";
        }

        private async void CalcularRede_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_network.Segments.Count == 0)
                {
                    MessageBox.Show("Importe uma rede do modelo 3D do Revit antes de calcular.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!TryParsePtBrDouble(RedeViscosidadeTextBox.Text, out double viscosidade)) return;

                var produtosNoCatalogo = await _catalogService.GetAllAsync();
                if (produtosNoCatalogo.Count <= 1)
                {
                    await GarantirCatalogoPadraoNbrAsync();
                    produtosNoCatalogo = await _catalogService.GetAllAsync();
                    await AtualizarStatusCatalogoInicioAsync();
                }

                var opcoesDiametro = produtosNoCatalogo
                    .Select(p => new PipeDiameterOption($"{p.Manufacturer} {p.NominalDesignation}", (double)p.InternalDiameterMm))
                    .ToList();

                AtualizarComboDiametrosManuais(produtosNoCatalogo);

                var res = HydraulicNetworkSolver.Solve(_network, viscosidade, opcoesDiametro, 3.0);
                _ultimoResultadoDaRede = res;

                int qtdTrocados = 0, qtdAumentados = 0, qtdReduzidos = 0, qtdMantidos = 0;
                var listaAuditoria = new List<ResultadoTrechoGridItem>();

                foreach (var s in res.Segments)
                {
                    var segObj = _network.FindSegment(s.SegmentId);
                    double origMm = _diametrosOriginaisRevit.TryGetValue(s.SegmentId, out double o) ? o : 0;
                    double novoMm = s.SelectedDiameterMm;
                    bool ehManual = _trechosManuais.Contains(s.SegmentId);

                    double origVel = 0, origDh = 0;
                    if (origMm > 0 && s.FlowLitersPerSecond > 0 && segObj != null)
                    {
                        var calcOrig = HydraulicSegmentCalculator.Calculate(
                            s.FlowLitersPerSecond,
                            segObj.LengthMeters,
                            origMm,
                            segObj.AbsoluteRoughnessMm,
                            viscosidade,
                            segObj.TotalLossCoefficientK);
                        origVel = calcOrig.VelocityMetersPerSecond;
                        origDh = calcOrig.TotalHeadLossMeters;
                    }

                    string statusTroca;
                    string motivo;
                    string impacto;

                    bool foiAlterado = origMm > 0 && novoMm > 0 && Math.Abs(novoMm - origMm) > 0.5;
                    if (foiAlterado)
                    {
                        qtdTrocados++;
                        if (novoMm > origMm) qtdAumentados++;
                        else qtdReduzidos++;
                    }
                    else
                    {
                        qtdMantidos++;
                    }

                    if (ehManual)
                        statusTroca = foiAlterado ? $"Manual ({origMm:F0}→{novoMm:F0})" : $"Manual ({novoMm:F0} mm)";
                    else if (origMm <= 0)
                        statusTroca = $"Auto ({novoMm:F1} mm)";
                    else if (novoMm > origMm + 0.5)
                        statusTroca = $"Aumentado (↑)";
                    else if (novoMm < origMm - 0.5)
                        statusTroca = $"Reduzido (↓)";
                    else
                        statusTroca = "Mantido (=)";

                    if (ehManual)
                    {
                        motivo = $"Definido manualmente pelo projetista (Ø {novoMm:F2} mm).";
                    }
                    else if (s.FlowLitersPerSecond <= 0)
                    {
                        motivo = "Sem vazão a jusante; mantida bitola mínima do catálogo.";
                    }
                    else if (origMm <= 0)
                    {
                        motivo = $"Menor bitola comercial para Q = {s.FlowLitersPerSecond:F3} L/s com V ≤ 3,0 m/s (NBR 5626).";
                    }
                    else if (novoMm > origMm + 0.5)
                    {
                        motivo = $"Velocidade no Ø antigo ({origMm:F1} mm) seria {origVel:F2} m/s (> 3,0 m/s NBR 5626 para Q = {s.FlowLitersPerSecond:F3} L/s).";
                    }
                    else if (novoMm < origMm - 0.5)
                    {
                        motivo = $"Otimização: Ø antigo ({origMm:F1} mm) superdimensionado (V = {origVel:F2} m/s); nova bitola atende com V = {s.VelocityMetersPerSecond:F2} m/s.";
                    }
                    else
                    {
                        motivo = $"Ø original ({origMm:F1} mm) já atende Q = {s.FlowLitersPerSecond:F3} L/s com V = {s.VelocityMetersPerSecond:F2} m/s ≤ 3,0 m/s.";
                    }

                    if (origMm > 0 && s.FlowLitersPerSecond > 0 && foiAlterado)
                    {
                        impacto = $"V: {origVel:F2}→{s.VelocityMetersPerSecond:F2} m/s | ΔH: {origDh:F3}→{s.TotalHeadLossMeters:F3} mca";
                    }
                    else
                    {
                        impacto = $"V = {s.VelocityMetersPerSecond:F2} m/s | ΔH = {s.TotalHeadLossMeters:F3} mca";
                    }

                    listaAuditoria.Add(new ResultadoTrechoGridItem
                    {
                        SegmentId = s.SegmentId,
                        Name = s.SegmentName,
                        ConnectionDisplay = s.Connection,
                        FlowDisplay = s.FlowLitersPerSecond.ToString("F3", PtBr),
                        OriginalDiameterDisplay = origMm > 0 ? $"{origMm.ToString("F1", PtBr)} mm" : "--",
                        DiameterDisplay = novoMm > 0 ? $"{novoMm.ToString("F1", PtBr)} mm" : "--",
                        SelectedDiameterMm = novoMm,
                        OriginalDiameterMm = origMm,
                        WasChanged = foiAlterado,
                        IsManual = ehManual,
                        ChangeStatusDisplay = statusTroca,
                        ImpactDisplay = impacto,
                        ReasonDisplay = motivo,
                        VelocityDisplay = s.VelocityMetersPerSecond.ToString("F3", PtBr),
                        ReynoldsDisplay = s.ReynoldsNumber.ToString("F0", PtBr),
                        HeadLossDisplay = s.TotalHeadLossMeters.ToString("F4", PtBr)
                    });
                }

                _todosResultadosTrechos = listaAuditoria;
                AplicarFiltroTrechosTrocados();
                AtualizarListaTrechos();

                ResumoTrocasTubosText.Text = $"Total analisado: {res.Segments.Count} trecho(s) — {qtdTrocados} trocado(s) ({qtdAumentados} aumentado(s), {qtdReduzidos} reduzido(s)) | {qtdMantidos} mantido(s).";

                StatusCalculoAguaFriaText.Text = $"Calculado: {qtdTrocados} tubo(s) trocado(s) de {res.Segments.Count} trechos.";
                StatusCalculoAguaFriaText.Foreground = Brushes.ForestGreen;

                if (MemStatusRedeText != null)
                {
                    MemStatusRedeText.Text = $"Calculado: {res.Segments.Count} trecho(s) ({qtdTrocados} bitola(s) ajustada(s)) prontos para exportação.";
                    MemStatusRedeText.Foreground = Brushes.ForestGreen;
                }

                if (_trechoSelecionadoParaEdicaoId.HasValue)
                {
                    var itemReSelecionado = _todosResultadosTrechos.FirstOrDefault(x => x.SegmentId == _trechoSelecionadoParaEdicaoId.Value);
                    if (itemReSelecionado != null)
                        ExibirDetalheTrechoSelecionado(itemReSelecionado);
                }

                ResultadosNosDataGrid.ItemsSource = res.Nodes.Select(n => new ResultadoNoGridItem
                {
                    Name = n.NodeName,
                    TypeLabel = FormatarTipoNo(n.NodeType),
                    ElevationDisplay = n.ElevationMeters.ToString("F2", PtBr),
                    HeadDisplay = n.PiezometricHeadMeters.ToString("F3", PtBr),
                    PressureDisplay = n.AvailablePressureMca.ToString("F3", PtBr)
                }).ToList();

                AtualizarPainelBomba();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro no dimensionamento:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RecalcularBomba_Click(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultadoDaRede == null)
            {
                MessageBox.Show("Calcule primeiro a rede na aba 'Água Fria (Rede BIM 3D)'.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AtualizarPainelBomba();
        }

        private void AtualizarPainelBomba()
        {
            if (_ultimoResultadoDaRede == null) return;

            try
            {
                double pressaoMin = TryParsePtBrDouble(BombaPressaoMinimaTextBox.Text, out double pMin) && pMin > 0 ? pMin : 1.0;
                double rendimento = TryParsePtBrDouble(BombaRendimentoTextBox.Text, out double rend) && rend > 0 && rend <= 100 ? rend / 100.0 : 0.60;

                var resultadoBomba = PumpCalculator.Calculate(_ultimoResultadoDaRede, pressaoMin, rendimento);

                BombaNoCriticoText.Text = resultadoBomba.CriticalNodeName;
                BombaPressaoAtualText.Text = $"{resultadoBomba.CriticalNodeCurrentPressure.ToString("F2", PtBr)} m.c.a.";
                BombaVazaoText.Text = $"{resultadoBomba.TotalFlowLitersPerSecond.ToString("F2", PtBr)} L/s ({(resultadoBomba.TotalFlowLitersPerSecond * 3.6).ToString("F2", PtBr)} m³/h)";

                if (!resultadoBomba.RequiresPump)
                {
                    BombaDiagnosticoText.Text = $"ATENDE POR GRAVIDADE — Todos os pontos possuem pressão acima de {pressaoMin.ToString("F2", PtBr)} m.c.a. Não é necessária bomba pressurizadora.";
                    BombaDiagnosticoText.Foreground = Brushes.ForestGreen;
                    BombaAmtText.Text = "0,00 m.c.a. (Gravidade)";
                    BombaPotenciaText.Text = "Dispensada";

                    if (MemStatusBombaText != null)
                        MemStatusBombaText.Text = $"Atende por gravidade (ponto crítico: {resultadoBomba.CriticalNodeName} com {resultadoBomba.CriticalNodeCurrentPressure:F2} m.c.a.).";
                }
                else
                {
                    BombaDiagnosticoText.Text = $"PRESSURIZAÇÃO NECESSÁRIA — Pressão insuficiente no ponto '{resultadoBomba.CriticalNodeName}'. Instale conjunto pressurizador.";
                    BombaDiagnosticoText.Foreground = Brushes.DarkRed;
                    BombaAmtText.Text = $"{resultadoBomba.RequiredAmtMca.ToString("F2", PtBr)} m.c.a.";
                    BombaPotenciaText.Text = $"{resultadoBomba.EstimatedPowerCv.ToString("F2", PtBr)} cv";

                    if (MemStatusBombaText != null)
                        MemStatusBombaText.Text = $"Necessária bomba: Q = {resultadoBomba.TotalFlowLitersPerSecond:F2} L/s | AMT = {resultadoBomba.RequiredAmtMca:F2} m.c.a. | {resultadoBomba.EstimatedPowerCv:F2} cv.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao verificar pressurização:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CalcularBoiler_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(BoilerOcupantesTextBox.Text, out int ocupantes) || ocupantes <= 0)
                {
                    MessageBox.Show("Informe um número válido de ocupantes.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!TryParsePtBrDouble(BoilerPressaoTextBox.Text, out double pressao) || pressao <= 0)
                {
                    MessageBox.Show("Informe uma pressão de trabalho válida.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (BoilerPerfilComboBox.SelectedItem is not ComboBoxItem itemSelecionado || itemSelecionado.Tag is not string tagString || !Enum.TryParse(tagString, out BuildingProfile perfil))
                    return;

                var resultado = BoilerCalculator.Calculate(perfil, ocupantes, pressao);
                int comercial = SugerirCapacidadeComercialBoiler(resultado.RequiredAccumulationVolumeLiters);

                BoilerConsumoText.Text = $"{resultado.DailyConsumptionLiters:F0} L";
                BoilerVolumeText.Text = $"{resultado.RequiredAccumulationVolumeLiters:F0} L";
                BoilerComercialText.Text = $"Boiler {comercial} Litros ({(resultado.RecommendedPressureType.Contains("ALTA") ? "Alta Pressão" : "Baixa Pressão")})";
                BoilerTipoText.Text = resultado.RecommendedPressureType;

                if (resultado.RecommendedPressureType.Contains("INSUFICIENTE") || resultado.RecommendedPressureType.Contains("EXCESSIVA"))
                    BoilerTipoText.Foreground = Brushes.DarkRed;
                else if (resultado.RecommendedPressureType.Contains("ALTA PRESSÃO"))
                    BoilerTipoText.Foreground = Brushes.DarkOrange;
                else
                    BoilerTipoText.Foreground = Brushes.ForestGreen;

                if (MemStatusBoilerText != null)
                    MemStatusBoilerText.Text = $"Boiler Comercial {comercial} L ({ocupantes} ocupantes) — {resultado.RecommendedPressureType}.";
            }
            catch (Exception ex) { MessageBox.Show($"Ocorreu um erro:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private static int SugerirCapacidadeComercialBoiler(double litros)
        {
            int[] comerciais = { 100, 150, 200, 250, 300, 400, 500, 600, 800, 1000, 1500, 2000 };
            return comerciais.FirstOrDefault(c => c >= litros);
        }

        private static int SugerirCapacidadeComercialCaixa(double litros)
        {
            int[] comerciais = { 310, 500, 750, 1000, 1500, 2000, 2500, 3000, 5000, 10000 };
            int achado = comerciais.FirstOrDefault(c => c >= litros);
            return achado > 0 ? achado : (int)(Math.Ceiling(litros / 1000.0) * 1000);
        }

        private void CalcularCaixa_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(CaixaPopulacaoTextBox.Text, out int populacao) || populacao <= 0)
                {
                    MessageBox.Show("Informe uma população válida.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!int.TryParse(CaixaDiasTextBox.Text, out int dias) || dias <= 0)
                {
                    MessageBox.Show("Informe uma autonomia (dias) válida.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!TryParsePtBrDouble(CaixaIncendioTextBox.Text, out double incendio) || incendio < 0)
                {
                    MessageBox.Show("Informe um valor válido para reserva de incêndio.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!TryParsePtBrDouble(CaixaCisternaTextBox.Text, out double percentagem) || percentagem < 0 || percentagem > 100)
                {
                    MessageBox.Show("Informe um percentual entre 0 e 100 para a cisterna.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (CaixaPerfilComboBox.SelectedItem is not ComboBoxItem itemSelecionado || itemSelecionado.Tag is not string tagString || !Enum.TryParse(tagString, out BuildingCategory perfil))
                    return;

                var resultado = ReservoirCalculator.Calculate(perfil, populacao, dias, incendio, percentagem / 100.0);
                int comSup = SugerirCapacidadeComercialCaixa(resultado.UpperReservoirVolumeLiters);
                int comInf = resultado.LowerReservoirVolumeLiters > 0 ? SugerirCapacidadeComercialCaixa(resultado.LowerReservoirVolumeLiters) : 0;

                CaixaConsumoText.Text = $"{resultado.TotalDailyConsumptionLiters:F0} Litros";
                CaixaVolumeTotalText.Text = $"{resultado.TotalRequiredVolumeLiters:F0} Litros";
                CaixaSuperiorText.Text = $"{resultado.UpperReservoirVolumeLiters:F0} L (Comercial: {comSup} L)";
                CaixaCisternaText.Text = resultado.LowerReservoirVolumeLiters > 0
                    ? $"{resultado.LowerReservoirVolumeLiters:F0} L (Comercial: {comInf} L)"
                    : "Dispensada (0 L)";

                if (MemStatusCaixaText != null)
                    MemStatusCaixaText.Text = $"Total: {resultado.TotalRequiredVolumeLiters:F0} L | Superior: {comSup} L | Cisterna: {comInf} L.";
            }
            catch (Exception ex) { MessageBox.Show($"Ocorreu um erro:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void CalcularEsgoto_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int totalUhc = 0;
                int maiorDiametroPeca = 0;
                bool temCozinha = false;
                bool temBanheiroOuServico = false;

                foreach (var item in _sewerFixtures)
                {
                    if (item.Quantity > 0)
                    {
                        totalUhc += (item.Uhc * item.Quantity);
                        if (item.MinDiameter > maiorDiametroPeca) maiorDiametroPeca = item.MinDiameter;
                        if (item.Name.Contains("Cozinha", StringComparison.OrdinalIgnoreCase)) temCozinha = true;
                        else temBanheiroOuServico = true;
                    }
                }

                if (totalUhc == 0)
                {
                    MessageBox.Show("Adicione ou importe pelo menos uma peça de esgoto (UHC) para calcular.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var resultado = SewerCalculator.CalculateBranch(totalUhc, maiorDiametroPeca);
                EsgotoUhcText.Text = resultado.TotalUhc.ToString();
                EsgotoTipoText.Text = resultado.ComponentType;
                EsgotoDiametroText.Text = $"DN {resultado.RequiredDiameterMm}";
                EsgotoInclinacaoText.Text = $"{resultado.MinimumSlopePercentage:F1} %";

                string itensObrigatorios = "• Linha Recomendada: PVC Esgoto Série Normal (ramais internos) e Série Reforçada (prumadas/subcoletores — Tigre / Amanco).\n• Peças Obrigatórias (NBR 8160):\n";
                if (temBanheiroOuServico)
                    itensObrigatorios += "  - Caixa Sifonada (DN 100×100×50 ou DN 150×150×50) com grelha para proteção do fecho hídrico;\n";
                if (temCozinha)
                    itensObrigatorios += "  - Caixa de Gordura obrigatória na saída da pia de cozinha antes da rede coletora;\n";
                itensObrigatorios += "  - Ramal e Coluna de Ventilação (DN 50 / DN 75) com terminal 30 cm acima da cobertura;\n  - Caixas de Inspeção (CI 60×60 cm) nas mudanças de direção.";
                EsgotoSugestoesText.Text = itensObrigatorios;

                if (MemStatusEsgotoText != null)
                    MemStatusEsgotoText.Text = $"Total: {resultado.TotalUhc} UHC | Tubulação: DN {resultado.RequiredDiameterMm} mm | Declividade: {resultado.MinimumSlopePercentage:F1}%.";
            }
            catch (Exception ex) { MessageBox.Show($"Ocorreu um erro ao dimensionar o esgoto:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void AtualizarResumoPluvialNoMemorial()
        {
            if (RainwaterView.UltimoResultado != null && MemStatusPluvialText != null)
            {
                var r = RainwaterView.UltimoResultado;
                MemStatusPluvialText.Text = $"Área: {r.ContributionAreaM2:F2} m² | Q = {r.DesignFlowLitersPerMinute:F1} L/min | Calha: {r.SelectedGutterDescription} | Descidas: {r.DownspoutCount}× DN {r.RequiredDownspoutDiameterMm} mm.";
            }
        }

        // =====================================================================
        // INTEGRAÇÃO REVIT 3D (PADRONIZADA)
        // =====================================================================
        private void ImportarRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                _revitBridge.ImportNetworkFromRevit(this, importData =>
                {
                    _network = importData.Network;
                    _diametrosOriginaisRevit = importData.OriginalDiametersMm ?? new Dictionary<Guid, double>();
                    _trechosManuais.Clear();
                    _trechoSelecionadoParaEdicaoId = null;
                    _ultimoResultadoDaRede = null;
                    _todosResultadosTrechos.Clear();
                    ResultadosTrechosDataGrid.ItemsSource = null;
                    ResultadosNosDataGrid.ItemsSource = null;
                    ResumoTrocasTubosText.Text = "Rede importada do Revit! Clique em 'Calcular Rede Hidráulica' para comparar e dimensionar os tubos.";
                    DetalheTrechoTituloText.Text = "Clique em qualquer linha da tabela acima para inspecionar a troca e alterar o diâmetro manualmente.";
                    DetalheTrechoComparativoText.Text = "Comparativo Antes → Depois: --";
                    DetalheTrechoMotivoText.Text = "Motivo Técnico: --";
                    BtnAplicarManualTrecho.IsEnabled = false;
                    BtnVoltarAutoTrecho.IsEnabled = false;
                    AtualizarListaNos();
                    AtualizarListaTrechos();
                    InicioRedeStatusText.Text = $"Rede carregada: {_network.Nodes.Count} nós e {_network.Segments.Count} trechos.";
                    StatusCalculoAguaFriaText.Text = "Rede importada! Clique em 'Calcular Rede Hidráulica'.";
                    StatusCalculoAguaFriaText.Foreground = Brushes.DarkOrange;

                    NavAguaFria.IsSelected = true;
                    SelecionarPainel("AguaFria");

                    if (MemStatusRedeText != null)
                    {
                        MemStatusRedeText.Text = $"Rede importada ({_network.Segments.Count} trechos). Clique em 'Calcular Rede Hidráulica' antes de exportar.";
                        MemStatusRedeText.Foreground = Brushes.DarkOrange;
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar rede do Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AplicarRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            if (_ultimoResultadoDaRede == null)
            {
                MessageBox.Show("Calcule a rede hidráulica antes de aplicar os diâmetros no Revit.", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                int atualizados = _revitBridge.ApplyDiametersToRevit(_ultimoResultadoDaRede);
                MessageBox.Show($"{atualizados} tubo(s) atualizado(s) com sucesso no modelo 3D do Revit!", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao aplicar diâmetros no Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void EstimarPopulacaoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var est = _revitBridge.EstimateBuildingPopulation();
                CaixaPopulacaoTextBox.Text = est.EstimatedPeople.ToString();
                BoilerOcupantesTextBox.Text = est.EstimatedPeople.ToString();
                MessageBox.Show(est.Summary, "Bahr Hidro BIM - Ambientes Revit", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao ler ambientes do Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ImportarEsgotoAutoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var mapeados = _revitBridge.ImportAllSewerFixturesFromRevit();
                AplicarAparelhosEsgoto(mapeados, sender, e);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao ler aparelhos do projeto:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ImportarEsgotoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var mapeados = _revitBridge.ImportSewerFixturesFromRevit(this);
                AplicarAparelhosEsgoto(mapeados, sender, e);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao selecionar aparelhos no 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AplicarAparelhosEsgoto(Dictionary<string, int> mapeados, object sender, RoutedEventArgs e)
        {
            if (mapeados.Count == 0)
            {
                MessageBox.Show("Nenhum aparelho sanitário compatível encontrado no modelo.", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var item in _sewerFixtures)
            {
                item.Quantity = mapeados.TryGetValue(item.Name, out int qtd) ? qtd : 0;
            }

            SewerItemsControl.ItemsSource = null;
            SewerItemsControl.ItemsSource = _sewerFixtures;
            CalcularEsgoto_Click(sender, e);

            int totalPecas = mapeados.Values.Sum();
            MessageBox.Show($"{totalPecas} aparelho(s) sanitário(s) lido(s) do Revit e dimensionados!", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ImportarPluvialAutoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var roof = _revitBridge.CaptureAllRoofsFromRevit();
                if (roof == null)
                {
                    MessageBox.Show("Nenhum telhado (Roof) encontrado no modelo Revit.", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                RainwaterViewControl.AplicarDadosTelhadoRevit(roof);
                MessageBox.Show($"Cobertura '{roof.RoofName}' lida automaticamente!\nÁrea total: {roof.AreaM2:F2} m²", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao ler telhados do projeto:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ImportarPluvialRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var roof = _revitBridge.CaptureRoofFromRevit(this);
                if (roof == null) return;
                RainwaterViewControl.AplicarDadosTelhadoRevit(roof);
                MessageBox.Show($"Telhado '{roof.RoofName}' capturado!\nÁrea medida: {roof.AreaM2:F2} m²", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao capturar telhado no 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SalvarDadosRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                string autorCompleto = string.IsNullOrWhiteSpace(MemCreaTextBox.Text)
                    ? MemEngenheiroTextBox.Text.Trim()
                    : $"{MemEngenheiroTextBox.Text.Trim()} ({MemCreaTextBox.Text.Trim()})";

                _revitBridge.SaveProjectInfo(
                    MemObraTextBox.Text.Trim(),
                    MemClienteTextBox.Text.Trim(),
                    MemEnderecoTextBox.Text.Trim(),
                    autorCompleto);

                MessageBox.Show("Dados salvos com sucesso nas propriedades do projeto Revit!", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao salvar dados no Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExportarMemorialAba_Click(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultadoDaRede == null)
            {
                MessageBox.Show("Calcule a rede hidráulica em 'Água Fria (Rede BIM 3D)' antes de gerar o memorial.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                NavAguaFria.IsSelected = true;
                SelecionarPainel("AguaFria");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"Memorial_Hidraulico_{(string.IsNullOrWhiteSpace(MemObraTextBox.Text) ? "BahrHidro" : MemObraTextBox.Text.Trim().Replace(" ", "_"))}",
                DefaultExt = ".xlsx",
                Filter = "Ficheiro Excel (.xlsx)|*.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    if (_revitBridge != null)
                        GerarExcelIsoladoNoRevit(_ultimoResultadoDaRede, dialog.FileName);
                    else
                        ExcelReportGenerator.GenerateNetworkReport(_ultimoResultadoDaRede, dialog.FileName);

                    MessageBox.Show("Memorial de cálculo exportado com sucesso!", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    var detalhe = ex.InnerException?.Message ?? ex.Message;
                    MessageBox.Show($"Ocorreu um erro ao gerar o Excel:\n{detalhe}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static void GerarExcelIsoladoNoRevit(NetworkCalculationResult resultado, string caminhoArquivo)
        {
            string coreDllPath = typeof(ExcelReportGenerator).Assembly.Location;
            string pluginDir = System.IO.Path.GetDirectoryName(coreDllPath)!;
            string json = System.Text.Json.JsonSerializer.Serialize(resultado);

            var alc = new IsolatedExcelLoadContext(pluginDir);
            try
            {
                var coreAsm = alc.LoadFromAssemblyPath(coreDllPath);
                var resultType = coreAsm.GetType(typeof(NetworkCalculationResult).FullName!)!;
                var generatorType = coreAsm.GetType(typeof(ExcelReportGenerator).FullName!)!;

                object? isolatedResult = System.Text.Json.JsonSerializer.Deserialize(json, resultType);
                var method = generatorType.GetMethod(nameof(ExcelReportGenerator.GenerateNetworkReport))!;
                method.Invoke(null, new[] { isolatedResult, caminhoArquivo });
            }
            finally
            {
                alc.Unload();
            }
        }

        private sealed class IsolatedExcelLoadContext : System.Runtime.Loader.AssemblyLoadContext
        {
            private readonly string _baseDir;

            public IsolatedExcelLoadContext(string baseDir) : base("BahrHidroExcelContext", isCollectible: true)
            {
                _baseDir = baseDir;
            }

            protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName)
            {
                if (!string.IsNullOrEmpty(assemblyName.Name))
                {
                    string candidatePath = System.IO.Path.Combine(_baseDir, assemblyName.Name + ".dll");
                    if (System.IO.File.Exists(candidatePath))
                        return LoadFromAssemblyPath(candidatePath);
                }
                return null;
            }
        }

        private async Task GarantirCatalogoPadraoNbrAsync()
        {
            var padroes = new (string dn, decimal ext, decimal esp)[]
            {
                ("DN 20 (1/2\")", 20.0m, 1.5m),
                ("DN 25 (3/4\")", 25.0m, 1.7m),
                ("DN 32 (1\")", 32.0m, 2.1m),
                ("DN 40 (1 1/4\")", 40.0m, 2.4m),
                ("DN 50 (1 1/2\")", 50.0m, 3.0m),
                ("DN 60 (2\")", 60.0m, 3.3m),
                ("DN 75 (2 1/2\")", 75.0m, 4.2m),
                ("DN 85 (3\")", 85.0m, 4.7m),
                ("DN 110 (4\")", 110.0m, 6.1m)
            };

            foreach (var (dn, ext, esp) in padroes)
            {
                try
                {
                    await _catalogService.AddAsync(new PipeProduct
                    {
                        Manufacturer = "Tigre / Amanco (NBR 5648)",
                        ProductLine = "PVC Soldável",
                        Material = PipeMaterial.PvcSoldavel,
                        NominalDesignation = dn,
                        OuterDiameterMm = ext,
                        WallThicknessMm = esp,
                        PressureClass = "PN 7,5",
                        MaximumOperatingPressureBar = 7.5m,
                        StandardReference = "ABNT NBR 5648",
                        SourceDescription = "Catálogo Padrão Automático"
                    });
                }
                catch { }
            }
        }

        private static string FormatarTipoNo(HydraulicNodeType type) => type switch
        {
            HydraulicNodeType.Reservoir => "Reservatório",
            HydraulicNodeType.Junction => "Nó",
            HydraulicNodeType.ConsumptionPoint => "Consumo",
            _ => type.ToString()
        };

        private static bool TryParsePtBrDouble(string? text, out double value)
        {
            if (string.IsNullOrWhiteSpace(text)) { value = 0; return false; }
            return double.TryParse(text.Trim().Replace('.', ','), NumberStyles.Float, PtBr, out value);
        }

        private sealed class NodeGridItem { public Guid Id { get; init; } public string Name { get; init; } = ""; public HydraulicNodeType Type { get; init; } public string TypeLabel { get; init; } = ""; public double ElevationMeters { get; init; } public string ElevationDisplay { get; init; } = ""; public double DemandLitersPerSecond { get; init; } public string DemandDisplay { get; init; } = ""; public string WeightDisplay { get; init; } = ""; }
        private sealed class SegmentGridItem { public Guid Id { get; init; } public string Name { get; init; } = ""; public string ConnectionDisplay { get; init; } = ""; public string LengthDisplay { get; init; } = ""; public string DiameterDisplay { get; init; } = ""; public string KDisplay { get; init; } = ""; }
        private sealed class ManualDiameterOption { public string Label { get; init; } = ""; public double? DiameterMm { get; init; } }
        private sealed class ResultadoTrechoGridItem
        {
            public Guid SegmentId { get; init; }
            public string Name { get; init; } = "";
            public string ConnectionDisplay { get; init; } = "";
            public string FlowDisplay { get; init; } = "";
            public string OriginalDiameterDisplay { get; init; } = "";
            public string DiameterDisplay { get; init; } = "";
            public double SelectedDiameterMm { get; init; }
            public double OriginalDiameterMm { get; init; }
            public bool WasChanged { get; init; }
            public bool IsManual { get; init; }
            public string ChangeStatusDisplay { get; init; } = "";
            public string ImpactDisplay { get; init; } = "";
            public string ReasonDisplay { get; init; } = "";
            public string VelocityDisplay { get; init; } = "";
            public string ReynoldsDisplay { get; init; } = "";
            public string HeadLossDisplay { get; init; } = "";
        }
        private sealed class ResultadoNoGridItem { public string Name { get; init; } = ""; public string TypeLabel { get; init; } = ""; public string ElevationDisplay { get; init; } = ""; public string HeadDisplay { get; init; } = ""; public string PressureDisplay { get; init; } = ""; }
        private class SewerFixtureViewModel { public string Name { get; set; } = ""; public int Uhc { get; set; } public int MinDiameter { get; set; } public string UhcDisplay => $"{Uhc} UHC"; public int Quantity { get; set; } = 0; }
    
        private void AtualizarComboDiametrosManuais(List<PipeProduct> produtos)
        {
            var valorAtual = TrechoManualDiametroComboBox.SelectedValue;
            var opcoes = new List<ManualDiameterOption>
            {
                new() { Label = "Automático (Dimensionar pela ABNT NBR 5626)", DiameterMm = null }
            };

            foreach (var p in produtos.OrderBy(x => x.InternalDiameterMm))
            {
                opcoes.Add(new ManualDiameterOption
                {
                    Label = $"{p.NominalDesignation} — Ø Interno: {p.InternalDiameterMm:F2} mm ({p.Manufacturer})",
                    DiameterMm = (double)p.InternalDiameterMm
                });
            }

            TrechoManualDiametroComboBox.ItemsSource = opcoes;
            TrechoManualDiametroComboBox.SelectedValue = valorAtual;
            if (TrechoManualDiametroComboBox.SelectedIndex < 0)
                TrechoManualDiametroComboBox.SelectedIndex = 0;
        }

        private void ChkSomenteTrocados_Changed(object sender, RoutedEventArgs e) => AplicarFiltroTrechosTrocados();

        private void AplicarFiltroTrechosTrocados()
        {
            if (ResultadosTrechosDataGrid == null) return;
            if (ChkSomenteTrocados?.IsChecked == true)
                ResultadosTrechosDataGrid.ItemsSource = _todosResultadosTrechos.Where(x => x.WasChanged || x.IsManual).ToList();
            else
                ResultadosTrechosDataGrid.ItemsSource = _todosResultadosTrechos.ToList();
        }

        private void ResultadosTrechosDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultadosTrechosDataGrid.SelectedItem is ResultadoTrechoGridItem item)
            {
                _trechoSelecionadoParaEdicaoId = item.SegmentId;
                ExibirDetalheTrechoSelecionado(item);
            }
        }

        private void ExibirDetalheTrechoSelecionado(ResultadoTrechoGridItem item)
        {
            DetalheTrechoBadgeText.Text = item.IsManual ? "MODO MANUAL ATIVO" : item.ChangeStatusDisplay.ToUpperInvariant();
            DetalheTrechoTituloText.Text = $"Trecho {item.Name} ({item.ConnectionDisplay}) — Vazão Q = {item.FlowDisplay} L/s";
            DetalheTrechoComparativoText.Text = $"Ø Antigo (Revit): {item.OriginalDiameterDisplay}  →  Ø Atual: {item.DiameterDisplay}  |  {item.ImpactDisplay}";
            DetalheTrechoMotivoText.Text = $"Motivo: {item.ReasonDisplay}";

            BtnAplicarManualTrecho.IsEnabled = true;
            BtnVoltarAutoTrecho.IsEnabled = item.IsManual;

            if (item.IsManual && item.SelectedDiameterMm > 0)
            {
                var itensCombo = TrechoManualDiametroComboBox.ItemsSource as IEnumerable<ManualDiameterOption>;
                var match = itensCombo?.FirstOrDefault(o => o.DiameterMm.HasValue && Math.Abs(o.DiameterMm.Value - item.SelectedDiameterMm) < 0.05);
                if (match != null)
                    TrechoManualDiametroComboBox.SelectedItem = match;
            }
            else
            {
                TrechoManualDiametroComboBox.SelectedIndex = 0;
            }
        }

        private void AplicarDiametroManualTrecho_Click(object sender, RoutedEventArgs e)
        {
            if (!_trechoSelecionadoParaEdicaoId.HasValue) return;
            var seg = _network.FindSegment(_trechoSelecionadoParaEdicaoId.Value);
            if (seg == null) return;

            double? novoDiametro = (TrechoManualDiametroComboBox.SelectedItem as ManualDiameterOption)?.DiameterMm;

            var segAtualizado = new HydraulicPipeSegment
            {
                Id = seg.Id,
                Name = seg.Name,
                StartNodeId = seg.StartNodeId,
                EndNodeId = seg.EndNodeId,
                LengthMeters = seg.LengthMeters,
                InternalDiameterMm = novoDiametro,
                AbsoluteRoughnessMm = seg.AbsoluteRoughnessMm,
                TotalLossCoefficientK = seg.TotalLossCoefficientK,
                FlowLitersPerSecond = seg.FlowLitersPerSecond
            };

            _network.UpdateSegment(segAtualizado);
            if (novoDiametro.HasValue)
                _trechosManuais.Add(seg.Id);
            else
                _trechosManuais.Remove(seg.Id);

            CalcularRede_Click(sender, e);
        }

        private void RestaurarDiametroAutoTrecho_Click(object sender, RoutedEventArgs e)
        {
            TrechoManualDiametroComboBox.SelectedIndex = 0;
            AplicarDiametroManualTrecho_Click(sender, e);
        }

}
}
