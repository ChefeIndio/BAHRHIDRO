using System.Diagnostics;
using System.Windows.Input;
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
        private readonly List<DetectedRoutingPoint> _pontosTracadoDetectados = new();
        private BimMaterialTakeoff? _ultimoBomExtraido;

        public MainWindow()
        {
            try { try { InitializeComponent();
            UiNavigationController.Initialize(); } catch (Exception ex) { System.Windows.MessageBox.Show(ex.InnerException?.ToString() ?? ex.ToString(), "Bahr Hidro BIM - Erro de Interface"); throw; } } catch (System.Windows.Markup.XamlParseException ex) { System.Windows.MessageBox.Show($"Erro de XAML na Interface:\n{ex.Message}\nDetalhe: {ex.InnerException?.Message}\nLinha: {ex.LineNumber}", "Bahr Hidro BIM - Debug"); throw; }
            InitializeKeyboardShortcuts();
            LoadOfficeProfileToUi();

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

            this.Loaded += async (_, _) =>
            {
                try
                {
                _revitBridge = revitBridge;

                BtnImportarInicioRevit.Visibility = Visibility.Visible;
                HeaderActionsTracado3D.Visibility = Visibility.Visible;
                HeaderActionsAguaFria.Visibility = Visibility.Visible;
                await CarregarOpcoesTemplateTracado();
                HeaderActionsAguaQuente.Visibility = Visibility.Visible;
                HeaderActionsReservatorios.Visibility = Visibility.Visible;
                HeaderActionsEsgoto.Visibility = Visibility.Visible;
                HeaderActionsPluvial.Visibility = Visibility.Visible;
                SalvarDadosRevitButton.Visibility = Visibility.Visible;
                BtnExtrairBom3D.Visibility = Visibility.Visible;
                BtnPintarAuditoria3D.Visibility = Visibility.Visible;
                BtnLimparAuditoria3D.Visibility = Visibility.Visible;

                var info = await _revitBridge.GetProjectInfo();
                MemObraTextBox.Text = info.ProjectName;
                MemClienteTextBox.Text = info.ClientName;
                MemEnderecoTextBox.Text = info.Address;
                MemEngenheiroTextBox.Text = info.Author;

                string raioX = await _revitBridge.GetModelOverviewSummary();
                InicioProjetoStatusText.Text = !string.IsNullOrWhiteSpace(info.ProjectName)
                    ? $"Projeto: {info.ProjectName} — {raioX}"
                    : raioX;
                }
                catch { }
            };
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

        private readonly ProjectEngineeringSnapshot _snapshot = new();
        private RevitReservoirScanResult? _ultimoScanReservatorios;
        private bool _ignorandoTrocaSubRede = false;
        private readonly Stack<string> _tabHistory = new();
        private string _activePanelTag = "Inicio";
        private bool _isGoingBackTab = false;

        private void SelecionarPainel(string tag)
        {
            if (PanelInicio == null) return;

            if (!_isGoingBackTab && !string.IsNullOrWhiteSpace(_activePanelTag) && _activePanelTag != tag)
            {
                _tabHistory.Push(_activePanelTag);
            }
            _activePanelTag = tag;

            PanelInicio.Visibility = tag == "Inicio" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelVistas3DVivas != null)
                PanelVistas3DVivas.Visibility = tag == "Vistas3DVivas" ? Visibility.Visible : Visibility.Collapsed;
            PanelTracado3D.Visibility = tag == "Tracado3D" ? Visibility.Visible : Visibility.Collapsed;
            PanelAguaFria.Visibility = tag == "AguaFria" ? Visibility.Visible : Visibility.Collapsed;
            PanelBomba.Visibility = tag == "Bomba" ? Visibility.Visible : Visibility.Collapsed;
            PanelAguaQuente.Visibility = tag == "AguaQuente" ? Visibility.Visible : Visibility.Collapsed;
            PanelReservatorios.Visibility = tag == "Reservatorios" ? Visibility.Visible : Visibility.Collapsed;
            PanelEsgoto.Visibility = (tag == "Esgoto" || tag == "CaixaGordura") ? Visibility.Visible : Visibility.Collapsed;
            if (tag == "CaixaGordura") PanelEsgoto.Children.OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
            PanelPluvial.Visibility = tag == "Pluvial" ? Visibility.Visible : Visibility.Collapsed;
            PanelMemorial.Visibility = tag == "Memorial" ? Visibility.Visible : Visibility.Collapsed;
            if (ConfiguracoesPanel != null)
                ConfiguracoesPanel.Visibility = tag == "Configuracoes" ? Visibility.Visible : Visibility.Collapsed;

            if (BtnVoltarAba != null)
                BtnVoltarAba.IsEnabled = _tabHistory.Count > 0;

            if (ActiveStepBadgeText != null)
            {
                ActiveStepBadgeText.Text = tag switch
                {
                    "Inicio" => "Visão Geral do Fluxo",
                    "Reservatorios" => "Etapa 1 de 5 • Reservatórios (NBR 5626)",
                    "AguaFria" => "Etapa 2 de 5 • Água Fria (NBR 5626)",
                    "Bomba" => "Etapa 2B • Pressurização & Bomba",
                    "AguaQuente" => "Etapa 3 de 5 • Água Quente & Boiler (NBR 7198)",
                    "Esgoto" => "Etapa 4 de 5 • Esgoto Sanitário (NBR 8160)",
                    "Pluvial" => "Etapa 5 de 5 • Calhas & Pluvial (NBR 10844)",
                    "CaixaGordura" => "Etapa 4 de 5 • Esgoto Sanitário (NBR 8160)",
                    "Memorial" => "Entrega Final • Todos os Memoriais",
                    "Configuracoes" => "Parâmetros Técnicos do Escritório",
                    _ => "Bahr Hidro BIM"
                };
            }

        }

        private void BtnVoltarAba_Click(object sender, RoutedEventArgs e)
        {
            if (_tabHistory.Count == 0)
            {
                SelecionarPainel("Inicio");
                if (NavInicio != null) NavInicio.IsSelected = true;
                return;
            }

            string prevTag = _tabHistory.Pop();
            _isGoingBackTab = true;
            try
            {
                SelecionarPainel(prevTag);
                SyncTreeSelectionByTag(prevTag);
            }
            finally
            {
                _isGoingBackTab = false;
            }
        }

        private void SyncTreeSelectionByTag(string tag)
        {
            TreeViewItem? target = tag switch
            {
                "Inicio" => NavInicio,
                "Tracado3D" => NavTracado3D,
                "Vistas3DVivas" => NavVistas3DVivas,
                "AguaFria" => NavAguaFria,
                "Bomba" => NavBomba,
                "AguaQuente" => NavAguaQuente,
                "Reservatorios" => NavReservatorios,
                "Esgoto" => NavEsgoto,
                "Pluvial" => NavPluvial,
                "Memorial" => NavMemorial,
                _ => null
            };
            if (target != null)
                target.IsSelected = true;
        }

        private void BtnIrInicio_Click(object sender, RoutedEventArgs e)
        {
            NavInicio.IsSelected = true;
            SelecionarPainel("Inicio");
        }

        private void BtnIrTracado3D_Click(object sender, RoutedEventArgs e)
        {
            NavTracado3D.IsSelected = true;
            SelecionarPainel("Tracado3D");
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
                double pressaoMin = TryParsePtBrDouble("1,00", out double pMin) && pMin > 0 ? pMin : 1.0;
                double rendimento = TryParsePtBrDouble("60", out double rend) && rend > 0 && rend <= 100 ? rend / 100.0 : 0.60;

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

                double potenciaKw = (comercial * 45.0) / (860.0 * 4.0);
                _snapshot.Boiler = new BoilerSnapshotData
                {
                    BuildingProfile = perfil.ToString(),
                    Occupants = ocupantes,
                    ColdWaterPressureMca = pressao,
                    DailyConsumptionLiters = resultado.DailyConsumptionLiters,
                    RequiredVolumeLiters = resultado.RequiredAccumulationVolumeLiters,
                    CommercialBoilerLiters = comercial,
                    HeatingPowerKw = potenciaKw,
                    PressureClassification = resultado.RecommendedPressureType
                };
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

                _snapshot.Reservoir = new ReservoirSnapshotData
                {
                    BuildingCategory = perfil.ToString(),
                    Population = populacao,
                    ReserveDays = dias,
                    FireReserveLiters = incendio,
                    DailyConsumptionLiters = resultado.TotalDailyConsumptionLiters,
                    TotalRequiredLiters = resultado.TotalRequiredVolumeLiters,
                    UpperVolumeLiters = resultado.UpperReservoirVolumeLiters,
                    CommercialUpperLiters = comSup,
                    LowerVolumeLiters = resultado.LowerReservoirVolumeLiters,
                    CommercialLowerLiters = comInf
                };

                // Encadeamento automático para Boiler (Etapa 3) e Caixa de Gordura (Etapa 6)
                if (BoilerOcupantesTextBox != null) BoilerOcupantesTextBox.Text = populacao.ToString();

                AtualizarDiagnosticoReservatorioERua(resultado.TotalRequiredVolumeLiters, comSup);
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

                int cozinhasDetectadas = _sewerFixtures
                    .Where(f => f.Quantity > 0 && f.Name.Contains("Cozinha", StringComparison.OrdinalIgnoreCase))
                    .Sum(f => f.Quantity);

                _snapshot.Sewer = new SewerSnapshotData
                {
                    TotalUhc = resultado.TotalUhc,
                    RequiredDiameterMm = resultado.RequiredDiameterMm,
                    MinimumSlopePercent = resultado.MinimumSlopePercentage,
                    ComponentType = resultado.ComponentType,
                    FixturesSummary = _sewerFixtures.Where(f => f.Quantity > 0).Select(f => $"{f.Quantity}x {f.Name} ({f.Uhc} UHC)").ToList(),
                    MandatoryRecommendations = itensObrigatorios
                };
            }
            catch (Exception ex) { MessageBox.Show($"Ocorreu um erro ao dimensionar o esgoto:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void AtualizarResumoPluvialNoMemorial()
        {
            if (RainwaterView.UltimoResultado != null && MemStatusPluvialText != null)
            {
                var r = RainwaterView.UltimoResultado;
                _snapshot.Rainwater = r;
                MemStatusPluvialText.Text = $"Área: {r.ContributionAreaM2:F2} m² | Q = {r.DesignFlowLitersPerMinute:F1} L/min | Calha: {r.SelectedGutterDescription} | Descidas: {r.DownspoutCount}× DN {r.RequiredDownspoutDiameterMm} mm.";
            }
        }

        // =====================================================================
        // INTEGRAÇÃO REVIT 3D (PADRONIZADA)
        // =====================================================================
        private async void DetectarRedeAutoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                int subIdx = SubRedeComboBox != null ? SubRedeComboBox.SelectedIndex : -1;
                double pressaoRua = TryParsePtBrDouble(PressaoRuaAguaFriaTextBox?.Text ?? "15,0", out double pr) && pr >= 0 ? pr : 15.0;
                var importData = await _revitBridge.ImportNetworkAutomaticallyFromRevit(subIdx, pressaoRua);
                CarregarRedeImportadaNaTela(importData);
                if (!string.IsNullOrWhiteSpace(importData.Summary))
                    MessageBox.Show(importData.Summary, "Bahr Hidro - Detecção Automática 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso na detecção automática:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ImportarRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                await _revitBridge.ImportNetworkFromRevit(this, importData =>
                {
                    CarregarRedeImportadaNaTela(importData);
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar rede do Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CarregarRedeImportadaNaTela(RevitNetworkImportData importData)
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

                    if (!string.IsNullOrWhiteSpace(importData.Summary))
                        ResumoTrocasTubosText.Text = importData.Summary;

                    if (SubRedeComboBox != null && importData.SubNetworkOptions != null && importData.SubNetworkOptions.Count > 0)
                    {
                        _ignorandoTrocaSubRede = true;
                        try
                        {
                            SubRedeComboBox.ItemsSource = importData.SubNetworkOptions;
                            SubRedeComboBox.SelectedIndex = Math.Clamp(importData.SelectedSubNetworkIndex, 0, importData.SubNetworkOptions.Count - 1);
                        }
                        finally { _ignorandoTrocaSubRede = false; }
                    }

                    if (importData.IsStreetFeedNetwork && PressaoRuaAguaFriaTextBox != null && importData.SuggestedInletPressureMca > 0)
                    {
                        PressaoRuaAguaFriaTextBox.Text = importData.SuggestedInletPressureMca.ToString("F1");
                    }

                    if (MemStatusRedeText != null)
                    {
                        MemStatusRedeText.Text = $"Rede detectada ({_network.Segments.Count} trechos). Clique em 'Calcular Rede Hidráulica' antes de exportar.";
                        MemStatusRedeText.Foreground = Brushes.DarkOrange;
                    }
        }

        private async void AplicarRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            if (_ultimoResultadoDaRede == null)
            {
                MessageBox.Show("Calcule a rede hidráulica antes de aplicar os diâmetros no Revit.", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                int atualizados = await _revitBridge.ApplyDiametersToRevit(_ultimoResultadoDaRede);
                MessageBox.Show($"{atualizados} tubo(s) atualizado(s) com sucesso no modelo 3D do Revit!", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao aplicar diâmetros no Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void EstimarPopulacaoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var est = await _revitBridge.EstimateBuildingPopulation();
                CaixaPopulacaoTextBox.Text = est.EstimatedPeople.ToString();
                BoilerOcupantesTextBox.Text = est.EstimatedPeople.ToString();
                MessageBox.Show(est.Summary, "Bahr Hidro BIM - Ambientes Revit", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao ler ambientes do Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ImportarEsgotoAutoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var mapeados = await _revitBridge.ImportAllSewerFixturesFromRevit();
                AplicarAparelhosEsgoto(mapeados, sender, e);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao ler aparelhos do projeto:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ImportarEsgotoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var mapeados = await _revitBridge.ImportSewerFixturesFromRevit(this);
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

        private async void ImportarPluvialAutoRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var roof = await _revitBridge.CaptureAllRoofsFromRevit();
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

        private async void ImportarPluvialRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var roof = await _revitBridge.CaptureRoofFromRevit(this);
                if (roof == null) return;
                RainwaterViewControl.AplicarDadosTelhadoRevit(roof);
                MessageBox.Show($"Telhado '{roof.RoofName}' capturado!\nÁrea medida: {roof.AreaM2:F2} m²", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao capturar telhado no 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void SalvarDadosRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                string autorCompleto = string.IsNullOrWhiteSpace(MemCreaTextBox.Text)
                    ? MemEngenheiroTextBox.Text.Trim()
                    : $"{MemEngenheiroTextBox.Text.Trim()} ({MemCreaTextBox.Text.Trim()})";

                await _revitBridge.SaveProjectInfo(
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

        private async void ExtrairBomRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                double margem = TryParsePtBrDouble(MemMargemPerdaTextBox.Text, out double m) && m >= 0 ? m : 10.0;
                _ultimoBomExtraido = await _revitBridge.ExtractBomFromRevit(MemObraTextBox.Text.Trim(), margem);

                double totalMetros = _ultimoBomExtraido.Pipes.Sum(p => p.TotalLengthWithWasteMeters);
                int totalVaras = _ultimoBomExtraido.Pipes.Sum(p => p.CommercialBars6m);
                int totalPecas = _ultimoBomExtraido.Fittings.Sum(f => f.Quantity);

                MemBomPreviewText.Text = $"Extraído do 3D: {_ultimoBomExtraido.Pipes.Count} linha(s) de tubo ({totalMetros:F2} m c/ perda = {totalVaras} varas de 6m) e {totalPecas} conexão(ões)/acessório(s).";
                MessageBox.Show(MemBomPreviewText.Text, "Bahr Hidro - Quantitativo BIM 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao extrair lista de materiais:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ExportarMemorialAba_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge != null)
            {
                try
                {
                    double margem = TryParsePtBrDouble(MemMargemPerdaTextBox.Text, out double m) && m >= 0 ? m : 10.0;
                    _ultimoBomExtraido = await _revitBridge.ExtractBomFromRevit(MemObraTextBox.Text.Trim(), margem);
                }
                catch { }
            }

            if (_ultimoResultadoDaRede == null && (_ultimoBomExtraido == null || (_ultimoBomExtraido.Pipes.Count == 0 && _ultimoBomExtraido.Fittings.Count == 0)))
            {
                MessageBox.Show("Calcule a rede hidráulica em 'Água Fria' ou gere o traçado 3D antes de exportar o memorial.", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"Memorial_e_Materiais_{(string.IsNullOrWhiteSpace(MemObraTextBox.Text) ? "BahrHidro" : MemObraTextBox.Text.Trim().Replace(" ", "_"))}",
                DefaultExt = ".xlsx",
                Filter = "Ficheiro Excel (.xlsx)|*.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    if (_revitBridge != null)
                        GerarExcelIsoladoNoRevit(_ultimoResultadoDaRede, _ultimoBomExtraido, dialog.FileName);
                    else
                        ExcelReportGenerator.GenerateProjectReportWithBom(_ultimoResultadoDaRede, _ultimoBomExtraido, dialog.FileName);

                    MessageBox.Show("Memorial de cálculo e Lista de Materiais exportados com sucesso!", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    var detalhe = ex.InnerException?.Message ?? ex.Message;
                    MessageBox.Show($"Ocorreu um erro ao gerar o Excel:\n{detalhe}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static void GerarExcelIsoladoNoRevit(NetworkCalculationResult? resultado, BimMaterialTakeoff? takeoff, string caminhoArquivo)
        {
            string coreDllPath = typeof(ExcelReportGenerator).Assembly.Location;
            string pluginDir = System.IO.Path.GetDirectoryName(coreDllPath)!;

            var alc = new IsolatedExcelLoadContext(pluginDir);
            try
            {
                var coreAsm = alc.LoadFromAssemblyPath(coreDllPath);
                var resultType = coreAsm.GetType(typeof(NetworkCalculationResult).FullName!)!;
                var takeoffType = coreAsm.GetType(typeof(BimMaterialTakeoff).FullName!)!;
                var generatorType = coreAsm.GetType(typeof(ExcelReportGenerator).FullName!)!;

                object? isolatedResult = resultado != null
                    ? System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(resultado), resultType)
                    : null;

                object? isolatedTakeoff = takeoff != null
                    ? System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(takeoff), takeoffType)
                    : null;

                var method = generatorType.GetMethod(nameof(ExcelReportGenerator.GenerateProjectReportWithBom))!;
                method.Invoke(null, new[] { isolatedResult, isolatedTakeoff, caminhoArquivo });
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


        private async Task CarregarOpcoesTemplateTracado()
        {
            if (_revitBridge == null) return;
            try
            {
                var opts = await _revitBridge.GetRoutingTemplateOptions();
                TracadoPipeTypeComboBox.ItemsSource = opts.PipeTypes;
                if (opts.PipeTypes.Count > 0)
                {
                    var pvc = opts.PipeTypes.FirstOrDefault(p => p.Name.Contains("PVC", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Sold", StringComparison.OrdinalIgnoreCase));
                    TracadoPipeTypeComboBox.SelectedItem = pvc ?? opts.PipeTypes[0];
                }

                TracadoSystemTypeComboBox.ItemsSource = opts.PipingSystemTypes;
                if (opts.PipingSystemTypes.Count > 0)
                {
                    var af = opts.PipingSystemTypes.FirstOrDefault(s => s.Name.Contains("Fria", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Cold", StringComparison.OrdinalIgnoreCase));
                    TracadoSystemTypeComboBox.SelectedItem = af ?? opts.PipingSystemTypes[0];
                }

                TracadoNivelComboBox.ItemsSource = opts.Levels;
                if (opts.Levels.Count > 0)
                    TracadoNivelComboBox.SelectedIndex = 0;

                var officeProfile = OfficeProfileManager.Load();
                if (!string.IsNullOrWhiteSpace(officeProfile.PreferredPipeTypeName))
                {
                    var savedPipe = opts.PipeTypes.FirstOrDefault(p => p.Name.Equals(officeProfile.PreferredPipeTypeName, StringComparison.OrdinalIgnoreCase));
                    if (savedPipe != null) TracadoPipeTypeComboBox.SelectedItem = savedPipe;
                }
                if (!string.IsNullOrWhiteSpace(officeProfile.PreferredSystemTypeName))
                {
                    var savedSys = opts.PipingSystemTypes.FirstOrDefault(s => s.Name.Equals(officeProfile.PreferredSystemTypeName, StringComparison.OrdinalIgnoreCase));
                    if (savedSys != null) TracadoSystemTypeComboBox.SelectedItem = savedSys;
                }
                TracadoOffsetTextBox.Text = officeProfile.DefaultTopOffsetMeters.ToString("F2", PtBr);
                MemMargemPerdaTextBox.Text = officeProfile.DefaultWasteMarginPercent.ToString("F0", PtBr);
                ChkDesviarSteelFrame.IsChecked = officeProfile.AvoidSteelFrameClashes;
                ChkInserirRegistrosAuto.IsChecked = officeProfile.InsertValvesAutomatically;

                TracadoTemplateStatusText.Text = $"Template + Padrão do Escritório carregados ({opts.PipeTypes.Count} tubo(s)). Clique em 'Detectar (IFC / DWG)'.";
            }
            catch (Exception ex)
            {
                TracadoTemplateStatusText.Text = $"Aviso ao ler template: {ex.Message}";
            }
        }

        private void TracadoDisciplinaComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TracadoPlanoComboBox == null || TracadoOffsetTextBox == null) return;
            string disc = (TracadoDisciplinaComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ColdWater";

            if (disc == "HotWater")
            {
                TracadoPlanoComboBox.SelectedIndex = 0; // Por Cima
                TracadoOffsetTextBox.Text = "2,45";
                if (TracadoSystemTypeComboBox.ItemsSource is IEnumerable<RevitLookupItem> sysList)
                {
                    var aq = sysList.FirstOrDefault(s => s.Name.Contains("Quente", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Hot", StringComparison.OrdinalIgnoreCase));
                    if (aq != null) TracadoSystemTypeComboBox.SelectedItem = aq;
                }
                if (TracadoPipeTypeComboBox.ItemsSource is IEnumerable<RevitLookupItem> pipeList)
                {
                    var pAq = pipeList.FirstOrDefault(p => p.Name.Contains("CPVC", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("PPR", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Quente", StringComparison.OrdinalIgnoreCase));
                    if (pAq != null) TracadoPipeTypeComboBox.SelectedItem = pAq;
                }
            }
            else if (disc == "Ventilation")
            {
                TracadoPlanoComboBox.SelectedIndex = 0; // Por Cima
                TracadoOffsetTextBox.Text = "3,20";
                if (TracadoSystemTypeComboBox.ItemsSource is IEnumerable<RevitLookupItem> sysList)
                {
                    var vent = sysList.FirstOrDefault(s => s.Name.Contains("Vent", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Esgoto", StringComparison.OrdinalIgnoreCase));
                    if (vent != null) TracadoSystemTypeComboBox.SelectedItem = vent;
                }
            }
            else if (disc == "StormDrainage")
            {
                TracadoPlanoComboBox.SelectedIndex = 0; // Por Cima
                TracadoOffsetTextBox.Text = "2,80";
                TracadoDiametroComboBox.SelectedIndex = 6; // 100 mm
                if (TracadoSystemTypeComboBox.ItemsSource is IEnumerable<RevitLookupItem> sysList)
                {
                    var pluv = sysList.FirstOrDefault(s => s.Name.Contains("Pluv", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Storm", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Esgoto", StringComparison.OrdinalIgnoreCase));
                    if (pluv != null) TracadoSystemTypeComboBox.SelectedItem = pluv;
                }
            }
            else if (disc == "SanitarySewer")
            {
                TracadoPlanoComboBox.SelectedIndex = 1; // Por Baixo
                TracadoOffsetTextBox.Text = "-0,22";
                if (TracadoSystemTypeComboBox.ItemsSource is IEnumerable<RevitLookupItem> sysList)
                {
                    var esg = sysList.FirstOrDefault(s => s.Name.Contains("Esgoto", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Sanit", StringComparison.OrdinalIgnoreCase));
                    if (esg != null) TracadoSystemTypeComboBox.SelectedItem = esg;
                }
                if (TracadoPipeTypeComboBox.ItemsSource is IEnumerable<RevitLookupItem> pipeList)
                {
                    var pEsg = pipeList.FirstOrDefault(p => p.Name.Contains("Esgoto", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Sanit", StringComparison.OrdinalIgnoreCase));
                    if (pEsg != null) TracadoPipeTypeComboBox.SelectedItem = pEsg;
                }
            }
            else
            {
                TracadoPlanoComboBox.SelectedIndex = 0; // Por Cima
                TracadoOffsetTextBox.Text = "2,60";
                if (TracadoSystemTypeComboBox.ItemsSource is IEnumerable<RevitLookupItem> sysList)
                {
                    var af = sysList.FirstOrDefault(s => s.Name.Contains("Fria", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Cold", StringComparison.OrdinalIgnoreCase));
                    if (af != null) TracadoSystemTypeComboBox.SelectedItem = af;
                }
                if (TracadoPipeTypeComboBox.ItemsSource is IEnumerable<RevitLookupItem> pipeList)
                {
                    var pAf = pipeList.FirstOrDefault(p => p.Name.Contains("Sold", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("PVC", StringComparison.OrdinalIgnoreCase));
                    if (pAf != null) TracadoPipeTypeComboBox.SelectedItem = pAf;
                }
            }
        }

        private void TracadoPlanoComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TracadoOffsetTextBox == null) return;
            string tag = (TracadoPlanoComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Top";
            TracadoOffsetTextBox.Text = tag == "Bottom" ? "-0,15" : "2,60";
        }

        private async void DetectarPontosTracado_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null)
            {
                MessageBox.Show("Esta função requer o Bahr Hidro aberto dentro do Revit.", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                long? levelId = TracadoNivelComboBox.SelectedValue is long lid ? lid : null;
                var encontrados = await _revitBridge.DetectRoutingPointsFromModel(levelId);

                _pontosTracadoDetectados.Clear();
                _pontosTracadoDetectados.AddRange(encontrados);
                AtualizarTabelaPontosTracado();

                if (encontrados.Count == 0)
                {
                    MessageBox.Show(
                        "Nenhum bloco sanitário identificado automaticamente no IFC/DWG.\n\nUse o painel '3. Marcação Rápida no DWG / 3D' à esquerda para clicar nos locais das peças sobre a planta.",
                        "Bahr Hidro BIM - Leitura IFC/DWG",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    var fontes = string.Join(", ", encontrados.Select(x => x.SourceType).Distinct());
                    TracadoTemplateStatusText.Text = $"{encontrados.Count} ponto(s) detectado(s) via {fontes}. Ajuste as alturas se desejar e gere o traçado.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao detectar pontos:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void AdicionarPontoManualDwg_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                string categoria = (TracadoPontoManualTipoComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Lavatório";
                long? levelId = TracadoNivelComboBox.SelectedValue is long lid ? lid : null;

                var novoPonto = await _revitBridge.PickManualRoutingPoint(this, categoria, levelId);
                if (novoPonto != null)
                {
                    int num = _pontosTracadoDetectados.Count(p => p.FixtureCategory == novoPonto.FixtureCategory) + 1;
                    novoPonto.Name = $"{novoPonto.FixtureCategory} {num:D2}";
                    _pontosTracadoDetectados.Add(novoPonto);
                    AtualizarTabelaPontosTracado();
                    TracadoTemplateStatusText.Text = $"Ponto '{novoPonto.Name}' adicionado! Total: {_pontosTracadoDetectados.Count} ponto(s).";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao marcar ponto:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LimparPontosTracado_Click(object sender, RoutedEventArgs e)
        {
            _pontosTracadoDetectados.Clear();
            AtualizarTabelaPontosTracado();
            TracadoTemplateStatusText.Text = "Lista limpa. Detecte pontos do IFC/DWG ou adicione manualmente.";
        }

        private void AtualizarTabelaPontosTracado()
        {
            TracadoPontosDataGrid.ItemsSource = null;
            TracadoPontosDataGrid.ItemsSource = _pontosTracadoDetectados;
        }

        private async void ExecutarTracado3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null)
            {
                MessageBox.Show("Esta função requer o Bahr Hidro aberto dentro do Revit.", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (_pontosTracadoDetectados.Count == 0)
                {
                    DetectarPontosTracado_Click(sender, e);
                    if (_pontosTracadoDetectados.Count == 0) return;
                }

                if (TracadoPipeTypeComboBox.SelectedValue is not long pipeTypeId)
                {
                    MessageBox.Show("Selecione um Tipo de Tubo do template.", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (TracadoSystemTypeComboBox.SelectedValue is not long systemTypeId)
                {
                    MessageBox.Show("Selecione um Sistema de Tubulação do template.", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!TryParsePtBrDouble(TracadoOffsetTextBox.Text, out double offsetM))
                {
                    MessageBox.Show("Informe um valor numérico válido para a cota relativa (m).", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                long? levelId = TracadoNivelComboBox.SelectedValue is long lid ? lid : null;
                string discipline = (TracadoDisciplinaComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ColdWater";
                bool routeFromAbove = ((TracadoPlanoComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Top") == "Top";
                double initDia = double.TryParse((TracadoDiametroComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out double d) ? d : 25.0;
                bool avoidSteel = ChkDesviarSteelFrame.IsChecked == true;
                bool insertValves = ChkInserirRegistrosAuto.IsChecked == true;

                var profile = OfficeProfileManager.Load();
                profile.PreferredPipeTypeName = (TracadoPipeTypeComboBox.SelectedItem as RevitLookupItem)?.Name;
                profile.PreferredSystemTypeName = (TracadoSystemTypeComboBox.SelectedItem as RevitLookupItem)?.Name;
                if (routeFromAbove) profile.DefaultTopOffsetMeters = offsetM;
                else profile.DefaultBottomOffsetMeters = offsetM;
                profile.AvoidSteelFrameClashes = avoidSteel;
                profile.InsertValvesAutomatically = insertValves;
                OfficeProfileManager.Save(profile);

                var req = new RevitAutoRoutingRequest(
                    discipline,
                    pipeTypeId,
                    systemTypeId,
                    levelId,
                    routeFromAbove,
                    offsetM,
                    initDia,
                    avoidSteel,
                    insertValves,
                    _pontosTracadoDetectados.ToList());

                var res = await _revitBridge.ExecuteAutomaticPipeRouting(req);

                TracadoResumoGeracaoText.Text = res.Summary;
                TracadoLogTextBox.Text = string.Join(Environment.NewLine, res.Logs);

                InicioProjetoStatusText.Text = await _revitBridge.GetModelOverviewSummary();
                MessageBox.Show(res.Summary, "Bahr Hidro - Traçado Automático 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar traçado 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    
        private async void GerarIsometricos3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.GenerateWetZoneIsometricViews();
                TracadoResumoGeracaoText.Text = res.Summary;
                MessageBox.Show(res.Summary, "Bahr Hidro - Vistas Isométricas 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao gerar vistas isométricas:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void TaggearTubosVistaAtual_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.TagPipesInActiveView();
                MessageBox.Show(res.Message, "Bahr Hidro - Identificação de Tubulações", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao identificar tubos:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private async void VerificarColisoesSteelFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.RunHydraulicClashCheck();
                TracadoResumoGeracaoText.Text = res.Summary;
                TracadoLogTextBox.Text = string.Join(Environment.NewLine, res.ClashDetails);
                MessageBox.Show(
                    res.Summary,
                    "Bahr Hidro - Verificação de Interferências 3D",
                    MessageBoxButton.OK,
                    res.ClashesFound == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro na verificação de colisões:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void GerarPranchasPlotagem_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.GenerateHydraulicSheets();
                TracadoResumoGeracaoText.Text = res.Summary;
                MessageBox.Show(res.Summary, "Bahr Hidro - Pranchas de Plotagem", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao gerar pranchas:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private async void LimparTracadoDisciplina_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                string discipline = (TracadoDisciplinaComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ColdWater";
                string discLabel = (TracadoDisciplinaComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? discipline;

                var confirm = MessageBox.Show(
                    $"Deseja remover todos os tubos e conexões gerados automaticamente pelo Bahr Hidro para:\n{discLabel}?",
                    "Bahr Hidro - Limpar Traçado da Disciplina",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                var res = await _revitBridge.ClearGeneratedRouting(discipline);
                TracadoResumoGeracaoText.Text = res.Summary;
                InicioProjetoStatusText.Text = await _revitBridge.GetModelOverviewSummary();
                MessageBox.Show(res.Summary, "Bahr Hidro - Limpeza Concluída", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao limpar traçado:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void GerarElevacoesParede_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.GenerateHydraulicWallElevations();
                TracadoResumoGeracaoText.Text = res.Summary;
                MessageBox.Show(res.Summary, "Bahr Hidro - Elevações de Parede (1:20)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao gerar elevações de parede:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private async void PintarAuditoria3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            if (_ultimoResultadoDaRede == null)
            {
                MessageBox.Show("Clique em 'Calcular Rede Hidráulica' antes de aplicar a pintura de auditoria no 3D.", "Bahr Hidro - Auditoria NBR 5626", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var res = await _revitBridge.ApplyHydraulicVisualAudit(_ultimoResultadoDaRede);
                MessageBox.Show(
                    $"{res.Summary}\n\nLegenda aplicada na vista:\n• Verde: Pressão >= 1,0 m.c.a. e Velocidade <= 3,0 m/s\n• Laranja: Velocidade > 3,0 m/s\n• Vermelho: Pressão < 1,0 m.c.a. (10 kPa)",
                    "Bahr Hidro - Auditoria Visual 3D",
                    MessageBoxButton.OK,
                    res.LowPressureCount > 0 || res.HighVelocityCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao pintar rede 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void LimparAuditoria3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.ClearHydraulicVisualAudit();
                MessageBox.Show(res.Summary, "Bahr Hidro - Cores Restauradas", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao limpar cores:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private async void AuditarFuracaoLsf_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.AuditLsfStudPunching();
                TracadoResumoGeracaoText.Text = res.Summary;
                TracadoLogTextBox.Text = string.Join(Environment.NewLine, res.HoleDetails);
                MessageBox.Show(
                    res.Summary,
                    "Bahr Hidro - Validação de Furação em Montantes LSF",
                    MessageBoxButton.OK,
                    res.WarningHoles > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao auditar furação LSF:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CotasFabricacaoElevacoes_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.ApplyFabricationCoordinatesToElevations();
                TracadoResumoGeracaoText.Text = res.Summary;
                MessageBox.Show(res.Summary, "Bahr Hidro - Cotas de Fabricação X e H (1:20)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao aplicar cotas X e H:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


        private async void GerarPrumadasMultinivel_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.GenerateMultilevelRisers();
                TracadoResumoGeracaoText.Text = res.Summary;
                TracadoLogTextBox.Text = string.Join(Environment.NewLine, res.Details);
                InicioProjetoStatusText.Text = await _revitBridge.GetModelOverviewSummary();
                MessageBox.Show(res.Summary, "Bahr Hidro - Prumadas Multinível (AF / AQ / TQ / CV)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao gerar prumadas multinível:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }


    private async void AtualizarCacheLsf_Click(object sender, RoutedEventArgs e)
    {
        if (_revitBridge == null) return;
        try
        {
            var res = await _revitBridge.RebuildSpatialCache();
            TracadoResumoGeracaoText.Text = res.Summary;
            TracadoLogTextBox.Text = res.Summary;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Aviso ao atualizar cache LSF:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private void NavConfiguracoes_Click(object sender, RoutedEventArgs e)
    {
        SelecionarPainel("Configuracoes");
        LoadOfficeProfileToUi();
    }

    private void LoadOfficeProfileToUi(OfficeProfileConfig? cfg = null)
    {
        cfg ??= OfficeProfileService.LoadCurrent();
        if (CfgProfileNameTextBox == null) return;

        CfgProfileNameTextBox.Text = cfg.ProfileName;
        CfgAlturaLavatorioBox.Text = cfg.AlturaLavatorioM.ToString("F2", CultureInfo.InvariantCulture);
        CfgAlturaVasoBox.Text = cfg.AlturaVasoSanitarioM.ToString("F2", CultureInfo.InvariantCulture);
        CfgAlturaChuveiroBox.Text = cfg.AlturaChuveiroM.ToString("F2", CultureInfo.InvariantCulture);
        CfgAlturaPiaBox.Text = cfg.AlturaPiaCozinhaM.ToString("F2", CultureInfo.InvariantCulture);
        CfgAlturaTanqueBox.Text = cfg.AlturaTanqueMaquinaM.ToString("F2", CultureInfo.InvariantCulture);

        CfgMaxFuroAlmaBox.Text = cfg.MaxFuroAlmaPercent.ToString("F0", CultureInfo.InvariantCulture);
        CfgDistMinGuiaBox.Text = cfg.DistanciaMinimaGuiaCm.ToString("F0", CultureInfo.InvariantCulture);
        CfgFolgaFuroBox.Text = cfg.FolgaFuroMm.ToString("F0", CultureInfo.InvariantCulture);

        CfgVelMaxBox.Text = cfg.VelocidadeMaximaMs.ToString("F1", CultureInfo.InvariantCulture);
        CfgPressaoMinBox.Text = cfg.PressaoMinimaDinamicaMca.ToString("F1", CultureInfo.InvariantCulture);
        CfgVaraTuboBox.Text = cfg.ComprimentoVaraTuboM.ToString("F1", CultureInfo.InvariantCulture);

        CfgStatusText.Text = $"Perfil ativo: {cfg.ProfileName} | Arquivo: {OfficeProfileService.DefaultProfilePath}";
    }

    private OfficeProfileConfig ReadOfficeProfileFromUi()
    {
        static double ParseD(string? txt, double fallback)
        {
            if (string.IsNullOrWhiteSpace(txt)) return fallback;
            string norm = txt.Trim().Replace(',', '.');
            return double.TryParse(norm, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        return new OfficeProfileConfig
        {
            ProfileName = string.IsNullOrWhiteSpace(CfgProfileNameTextBox.Text) ? "Padrão Escritório LSF" : CfgProfileNameTextBox.Text.Trim(),
            AlturaLavatorioM = ParseD(CfgAlturaLavatorioBox.Text, 0.60),
            AlturaVasoSanitarioM = ParseD(CfgAlturaVasoBox.Text, 0.20),
            AlturaChuveiroM = ParseD(CfgAlturaChuveiroBox.Text, 2.10),
            AlturaPiaCozinhaM = ParseD(CfgAlturaPiaBox.Text, 0.60),
            AlturaTanqueMaquinaM = ParseD(CfgAlturaTanqueBox.Text, 0.90),
            MaxFuroAlmaPercent = ParseD(CfgMaxFuroAlmaBox.Text, 50.0),
            DistanciaMinimaGuiaCm = ParseD(CfgDistMinGuiaBox.Text, 25.0),
            FolgaFuroMm = ParseD(CfgFolgaFuroBox.Text, 6.0),
            VelocidadeMaximaMs = ParseD(CfgVelMaxBox.Text, 3.0),
            PressaoMinimaDinamicaMca = ParseD(CfgPressaoMinBox.Text, 1.0),
            ComprimentoVaraTuboM = ParseD(CfgVaraTuboBox.Text, 6.0)
        };
    }

    private void SalvarConfiguracoes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = ReadOfficeProfileFromUi();
            OfficeProfileService.SaveCurrent(cfg);
            CfgStatusText.Text = $"Configurações salvas com sucesso às {DateTime.Now:HH:mm:ss} em {OfficeProfileService.DefaultProfilePath}";
            MessageBox.Show("Padrão técnico do escritório salvo com sucesso!", "Bahr Hidro - Configurações", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao salvar configurações:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportarPerfilJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = ReadOfficeProfileFromUi();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Perfil Bahr Hidro (*.json)|*.json",
                FileName = "BahrHidro_PerfilEscritorio.json",
                Title = "Exportar Perfil Técnico do Escritório"
            };
            if (dlg.ShowDialog() == true)
            {
                OfficeProfileService.ExportToFile(cfg, dlg.FileName);
                CfgStatusText.Text = $"Perfil exportado para: {dlg.FileName}";
                MessageBox.Show($"Perfil exportado com sucesso!\n{dlg.FileName}", "Bahr Hidro - Exportar JSON", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao exportar perfil:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportarPerfilJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Perfil Bahr Hidro (*.json)|*.json",
                Title = "Importar Perfil Técnico do Escritório"
            };
            if (dlg.ShowDialog() == true)
            {
                var loaded = OfficeProfileService.ImportFromFile(dlg.FileName);
                LoadOfficeProfileToUi(loaded);
                MessageBox.Show($"Perfil '{loaded.ProfileName}' importado e definido como padrão!", "Bahr Hidro - Importar JSON", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao importar perfil:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestaurarPadraoNbr_Click(object sender, RoutedEventArgs e)
    {
        var def = new OfficeProfileConfig();
        OfficeProfileService.SaveCurrent(def);
        LoadOfficeProfileToUi(def);
        MessageBox.Show("Valores padrão ABNT NBR 5626 / 8160 e Steel Frame restaurados.", "Bahr Hidro - Configurações", MessageBoxButton.OK, MessageBoxImage.Information);
    }


    private async void VerificarAtualizacoes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateStatusText.Text = "Consultando repositório ChefeIndio/BAHRHIDRO no GitHub...";
            var res = await GitHubUpdateService.CheckForUpdatesAsync();
            UpdateStatusText.Text = res.StatusSummary;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Não foi possível consultar o GitHub agora: {ex.Message}";
        }
    }

    private void ExecutarAtualizadorWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string msg = GitHubUpdateService.TriggerWindowsUpdater();
            UpdateStatusText.Text = msg;
            MessageBox.Show(msg, "Bahr Hidro - Atualizador", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Aviso ao iniciar atualizador:\n{ex.Message}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private async void LimparRedeGerada_Click(object sender, RoutedEventArgs e)
    {
        if (_revitBridge == null) return;
        var confirm = MessageBox.Show(
            "Deseja remover todos os tubos, conexões e vistas gerados pelo Bahr Hidro neste modelo?",
            "Bahr Hidro - Limpar Rede Gerada",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var res = await _revitBridge.CleanGeneratedNetwork();
            TracadoResumoGeracaoText.Text = res.Summary;
            TracadoLogTextBox.Text = res.Summary;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao limpar rede:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    private System.Collections.Generic.List<System.Windows.Controls.Primitives.ButtonBase> _navShortcutButtons = new();

    private void InitializeKeyboardShortcuts()
    {
        this.PreviewKeyDown += MainWindow_PreviewKeyDown;
        this.Loaded += (_, _) => { if (!LicenseService.EnsureLicensed(this)) { this.Close(); return; } DiscoverNavigationShortcutButtons(); UpdateLicenseInfoCard(); };
    }

    private void DiscoverNavigationShortcutButtons()
    {
        _navShortcutButtons.Clear();
        if (NavConfiguracoesBtn?.Parent is Panel navPanel)
        {
            foreach (UIElement child in navPanel.Children)
            {
                if (child is System.Windows.Controls.Primitives.ButtonBase btn && btn.Visibility == Visibility.Visible)
                {
                    _navShortcutButtons.Add(btn);
                    int idx = _navShortcutButtons.Count;
                    if (idx <= 9 && btn.Content is string txt && !txt.Contains("Ctrl+"))
                    {
                        btn.ToolTip = $"{txt.Trim()} (Atalho: Ctrl+{idx})";
                    }
                }
            }
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;
        ModifierKeys mods = Keyboard.Modifiers;

        if (key == Key.F1)
        {
            e.Handled = true;
            MostrarGuiaAtalhos_Click(this, new RoutedEventArgs());
            return;
        }

        if (key == Key.F5)
        {
            e.Handled = true;
            QuickRibbonCache_Click(this, new RoutedEventArgs());
            return;
        }

        bool ctrl = (mods & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (mods & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl && shift)
        {
            if (key == Key.A)
            {
                e.Handled = true;
                QuickRibbonAudit_Click(this, new RoutedEventArgs());
                return;
            }
            if (key == Key.L)
            {
                e.Handled = true;
                QuickRibbonLsf_Click(this, new RoutedEventArgs());
                return;
            }
            if (key == Key.P)
            {
                e.Handled = true;
                QuickRibbonSheets_Click(this, new RoutedEventArgs());
                return;
            }
        }

        if (ctrl && !shift)
        {
            if (key == Key.S && ConfiguracoesPanel.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                SalvarConfiguracoes_Click(this, new RoutedEventArgs());
                return;
            }

            int numIndex = key switch
            {
                Key.D1 or Key.NumPad1 => 0,
                Key.D2 or Key.NumPad2 => 1,
                Key.D3 or Key.NumPad3 => 2,
                Key.D4 or Key.NumPad4 => 3,
                Key.D5 or Key.NumPad5 => 4,
                Key.D6 or Key.NumPad6 => 5,
                Key.D7 or Key.NumPad7 => 6,
                _ => -1
            };

            if (numIndex >= 0 && numIndex < _navShortcutButtons.Count)
            {
                e.Handled = true;
                var targetBtn = _navShortcutButtons[numIndex];
                if (targetBtn is System.Windows.Controls.RadioButton rb)
                    rb.IsChecked = true;
                targetBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
        }
    }

    private async void QuickRibbonCache_Click(object sender, RoutedEventArgs e)
    {
        if (_revitBridge == null) return;
        try
        {
            var res = await _revitBridge.RebuildSpatialCache(); TracadoResumoGeracaoText.Text = res.Summary; TracadoLogTextBox.Text = res.Summary;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Aviso ao atualizar cache LSF:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void QuickRibbonAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_revitBridge == null) return;
        try
        {
            var res = await _revitBridge.ApplyHydraulicVisualAudit(default!); MessageBox.Show(res.Summary, "Bahr Hidro - Auditoria 3D", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro na Auditoria 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void QuickRibbonLsf_Click(object sender, RoutedEventArgs e)
    {
        if (_revitBridge == null) return;
        try
        {
            var res = await _revitBridge.AuditLsfStudPunching(); MessageBox.Show(res.Summary, "Bahr Hidro - Furação LSF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro na Validação LSF:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void QuickRibbonSheets_Click(object sender, RoutedEventArgs e)
    {
        if (_revitBridge == null) return;
        try
        {
            var res = await _revitBridge.GenerateHydraulicSheets(); MessageBox.Show(res.Summary, "Bahr Hidro - Pranchas Executivas", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao Gerar Pranchas:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MostrarGuiaAtalhos_Click(object sender, RoutedEventArgs e)
    {
        string guia =
            "ATALHOS DE TECLADO — BAHR HIDRO BIM (REVIT 2026)\n" +
            "──────────────────────────────────────────────────\n" +
            "• Ctrl + 1 a Ctrl + 6  →  Alternar rapidamente entre as abas\n" +
            "• F5                   →  Atualizar Cache Espacial LSF (2D)\n" +
            "• Ctrl + Shift + A     →  Auditoria Visual 3D (NBR 5626)\n" +
            "• Ctrl + Shift + L     →  Validar Furação nos Montantes LSF\n" +
            "• Ctrl + Shift + P     →  Gerar Pranchas Executivas (1:20 / 1:25)\n" +
            "• Ctrl + S             →  Salvar Perfil do Escritório (.json)\n" +
            "• F1                   →  Exibir este guia de atalhos";

        MessageBox.Show(guia, "Bahr Hidro — Atalhos Rápidos", MessageBoxButton.OK, MessageBoxImage.Information);
    }


    private void UpdateLicenseInfoCard()
    {
        var st = LicenseService.ValidateStoredLicense();
        if (this.FindName("LicenseInfoText") is System.Windows.Controls.TextBlock tb)
        {
            tb.Text = st.IsValid
                ? $"{st.Message} | ID desta Máquina: {LicenseService.GetMachineId()}"
                : $"Não licenciado | ID desta Máquina: {LicenseService.GetMachineId()}";
        }
    }

    private void GerenciarLicenca_Click(object sender, RoutedEventArgs e)
    {
        if (LicenseService.EnsureLicensed(this, forcePrompt: true))
        {
            UpdateLicenseInfoCard();
        }
    }


        private async void ExecutarSequenciaCompletaAfAqEsgoto_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                Doc3DStatusText.Text = "Executando sequência: 1º Água Fria → 2º Água Quente → 3º Esgoto e gerando as 4 vistas 3D...";
                string msg = await _revitBridge.RunOrderedFullAutomationAsync();
                Doc3DStatusText.Text = msg;
                MessageBox.Show(msg, "Bahr Hidro — Sequência Completa & 4 Vistas 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao executar sequência completa:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExecutarSomenteAguaFria_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                string msg = await _revitBridge.RunColdWaterOnlyAsync();
                Doc3DStatusText.Text = msg;
                MessageBox.Show(msg, "Bahr Hidro — 1º Água Fria", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro na Água Fria:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExecutarSomenteAguaQuente_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                string msg = await _revitBridge.RunHotWaterOnlyAsync();
                Doc3DStatusText.Text = msg;
                MessageBox.Show(msg, "Bahr Hidro — 2º Água Quente", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro na Água Quente:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExecutarSomenteEsgoto_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                string msg = await _revitBridge.RunSanitaryOnlyAsync();
                Doc3DStatusText.Text = msg;
                MessageBox.Show(msg, "Bahr Hidro — 3º Esgoto & Ventilação", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro no Esgoto:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void AbrirVista3DAguaFria_Click(object sender, RoutedEventArgs e) => await AbrirVistaLiveAsync("Bahr Hidro - 3D Água Fria");
        private async void AbrirVista3DAguaQuente_Click(object sender, RoutedEventArgs e) => await AbrirVistaLiveAsync("Bahr Hidro - 3D Água Quente");
        private async void AbrirVista3DEsgoto_Click(object sender, RoutedEventArgs e) => await AbrirVistaLiveAsync("Bahr Hidro - 3D Esgoto");
        private async void AbrirVista3DCompleta_Click(object sender, RoutedEventArgs e) => await AbrirVistaLiveAsync("Bahr Hidro - 3D Completo (AF + AQ + Esgoto)");
        private async void AbrirSecaoGeral_Click(object sender, RoutedEventArgs e) => await AbrirVistaLiveAsync("Bahr Hidro - Seção Geral da Rede");

        private async Task AbrirVistaLiveAsync(string viewName)
        {
            if (_revitBridge == null) return;
            try
            {
                string msg = await _revitBridge.OpenLiveViewByNameAsync(viewName);
                Doc3DStatusText.Text = msg;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao abrir vista:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AbrirMemoriaisCsv_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BahrHidro", "Logs");
                Directory.CreateDirectory(logDir);
                Process.Start(new ProcessStartInfo { FileName = logDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao abrir pasta de memoriais:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnIrCaixaGordura_Click(object sender, RoutedEventArgs e)
        {
            if (NavEsgoto != null) NavEsgoto.IsSelected = true;
            SelecionarPainel("CaixaGordura");
        }

        private void AtualizarParecerTecnicoEEncadearAguaFria()
        {
            if (_ultimoResultadoDaRede == null) return;
            _snapshot.ColdWaterNetwork = _ultimoResultadoDaRede;

            var alerts = new List<string>();
            int trechosAlterados = 0;

            foreach (var seg in _ultimoResultadoDaRede.Segments)
            {
                if (_diametrosOriginaisRevit.TryGetValue(seg.SegmentId, out double origMm) && origMm > 0 && Math.Abs(origMm - seg.SelectedDiameterMm) > 0.5)
                    trechosAlterados++;

                if (seg.VelocityMetersPerSecond > 3.0)
                    alerts.Add($"[ALERTA NBR 5626] Trecho {seg.SegmentName}: velocidade de {seg.VelocityMetersPerSecond:F2} m/s excede o limite de 3,0 m/s. Recomenda-se aumentar o diâmetro comercial.");
            }

            if (trechosAlterados > 0)
                alerts.Insert(0, $"[AJUSTE DE BITOLA] {trechosAlterados} trecho(s) do traçado básico precisam de atualização de diâmetro comercial (clique em 'Aplicar no Revit' após revisar).");

            var nosConsumo = _ultimoResultadoDaRede.Nodes.Where(n => n.NodeType != HydraulicNodeType.Reservoir).ToList();
            var nosBaixaPressao = nosConsumo.Where(n => n.AvailablePressureMca < 1.0).ToList();
            if (nosBaixaPressao.Count > 0)
            {
                var pior = nosBaixaPressao.OrderBy(n => n.AvailablePressureMca).First();
                alerts.Add($"[CRÍTICO NBR 5626] {nosBaixaPressao.Count} ponto(s) com pressão dinâmica abaixo de 1,0 m.c.a (pior ponto: {pior.NodeName} com {pior.AvailablePressureMca:F2} m.c.a). Recomenda-se elevar o reservatório ou instalar pressurizador.");
            }

            try
            {
                var pump = PumpCalculator.Calculate(_ultimoResultadoDaRede, 1.0);
                _snapshot.Pump = new PumpSnapshotData
                {
                    RequiresPump = pump.RequiresPump,
                    RequiredAmtMca = pump.RequiredAmtMca,
                    TotalFlowLps = pump.TotalFlowLitersPerSecond,
                    EstimatedPowerCv = pump.EstimatedPowerCv,
                    CriticalNodeName = pump.CriticalNodeName,
                    CriticalNodePressureMca = pump.CriticalNodeCurrentPressure
                };
            }
            catch { }

            if (nosConsumo.Count > 0 && BoilerPressaoTextBox != null)
            {
                double pressaoRefBoiler = nosConsumo.Average(n => Math.Max(0.5, n.AvailablePressureMca));
                BoilerPressaoTextBox.Text = pressaoRefBoiler.ToString("F2");
            }

            if (alerts.Count == 0)
                alerts.Add("[APROVADO NBR 5626] Todos os trechos e pontos de consumo atendem aos limites normativos de velocidade (≤ 3,0 m/s) e pressão dinâmica (≥ 1,0 m.c.a).");

            _snapshot.ColdWaterDiagnosticAlerts = alerts;
            if (ParecerTecnicoAguaFriaText != null)
                ParecerTecnicoAguaFriaText.Text = string.Join("\n", alerts);
        }

        private ProjectEngineeringSnapshot BuildCurrentEngineeringSnapshot()
        {
            _snapshot.ProjectName = string.IsNullOrWhiteSpace(MemObraTextBox?.Text) ? "Projeto Hidrossanitário BIM" : MemObraTextBox.Text.Trim();
            _snapshot.ClientName = MemClienteTextBox?.Text?.Trim() ?? "";
            _snapshot.Address = MemEnderecoTextBox?.Text?.Trim() ?? "";
            _snapshot.EngineerName = MemEngenheiroTextBox?.Text?.Trim() ?? "";
            _snapshot.CreaNumber = MemCreaTextBox?.Text?.Trim() ?? "";
            _snapshot.ArtNumber = MemArtTextBox?.Text?.Trim() ?? "";
            _snapshot.Revision = string.IsNullOrWhiteSpace(MemRevisaoTextBox?.Text) ? "R00" : MemRevisaoTextBox.Text.Trim();
            _snapshot.ColdWaterNetwork = _ultimoResultadoDaRede;
            if (RainwaterView.UltimoResultado != null)
                _snapshot.Rainwater = RainwaterView.UltimoResultado;
            return _snapshot;
        }

        private async void ExportarPacoteCompletoMemoriais_Click(object sender, RoutedEventArgs e) => await ExecutarExportacaoMemoriaisAsync("ALL", false);
        private async void ExportarMemorialDescritivo_Click(object sender, RoutedEventArgs e) => await ExecutarExportacaoMemoriaisAsync("DESCRITIVO", false);
        private async void ExportarMemorialCalculoAnalitico_Click(object sender, RoutedEventArgs e) => await ExecutarExportacaoMemoriaisAsync("CALCULO", false);
        private async void AbrirMemorialUnificadoPdf_Click(object sender, RoutedEventArgs e) => await ExecutarExportacaoMemoriaisAsync("UNIFICADO", true);

        private async Task ExecutarExportacaoMemoriaisAsync(string mode, bool openAfterGenerate)
        {
            if (_revitBridge != null)
            {
                try
                {
                    double margem = TryParsePtBrDouble(MemMargemPerdaTextBox?.Text ?? "10", out double m) && m >= 0 ? m : 10.0;
                    _ultimoBomExtraido = await _revitBridge.ExtractBomFromRevit(MemObraTextBox?.Text?.Trim() ?? "", margem);
                }
                catch { }
            }

            var snap = BuildCurrentEngineeringSnapshot();
            string safeName = string.IsNullOrWhiteSpace(snap.ProjectName) ? "BahrHidro" : snap.ProjectName.Replace(" ", "_");

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = safeName,
                Title = "Escolha a pasta e o nome base para salvar os memoriais"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string snapJson = System.Text.Json.JsonSerializer.Serialize(snap);
                    string? bomJson = _ultimoBomExtraido != null ? System.Text.Json.JsonSerializer.Serialize(_ultimoBomExtraido) : null;

                    List<string> files;
                    if (_revitBridge != null)
                    {
                        files = GerarTodosMemoriaisIsoladoNoRevit(snapJson, bomJson, dialog.FileName, mode);
                    }
                    else
                    {
VerificarPressaoEstaticaMaximaVrp();
                                        files = ComprehensiveMemorialGenerator.GenerateAllFromJson(snapJson, bomJson, dialog.FileName, mode);
                    }

                    if (openAfterGenerate && files.Count > 0)
                    {
                        string htmlFile = files.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ?? files[0];
                        Process.Start(new ProcessStartInfo { FileName = htmlFile, UseShellExecute = true });
                    }

                    string lista = string.Join("\n• ", files.Select(Path.GetFileName));
                    MessageBox.Show($"Memoriais gerados com sucesso!\n\nArquivos salvos:\n• {lista}", "Bahr Hidro — Entrega Técnica Completa", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    var detalhe = ex.InnerException?.Message ?? ex.Message;
                    MessageBox.Show($"Erro ao gerar memoriais:\n{detalhe}", "Bahr Hidro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static List<string> GerarTodosMemoriaisIsoladoNoRevit(string snapJson, string? bomJson, string basePath, string mode)
        {
            string coreDllPath = typeof(ComprehensiveMemorialGenerator).Assembly.Location;
            string pluginDir = Path.GetDirectoryName(coreDllPath)!;

            var alc = new IsolatedExcelLoadContext(pluginDir);
            try
            {
                var coreAsm = alc.LoadFromAssemblyPath(coreDllPath);
                var genType = coreAsm.GetType(typeof(ComprehensiveMemorialGenerator).FullName!)!;
                var method = genType.GetMethod(nameof(ComprehensiveMemorialGenerator.GenerateAllFromJson))!;
                return (List<string>)method.Invoke(null, new object?[] { snapJson, bomJson, basePath, mode })!;
            }
            finally
            {
                alc.Unload();
            }
        }

        private void BtnIrReservatorios_Click(object sender, RoutedEventArgs e)
        {
            if (NavReservatorios != null) NavReservatorios.IsSelected = true;
            SelecionarPainel("Reservatorios");
        }

        private void BtnIrAguaQuente_Click(object sender, RoutedEventArgs e)
        {
            if (NavAguaQuente != null) NavAguaQuente.IsSelected = true;
            SelecionarPainel("AguaQuente");
        }

        private void BtnIrBomba_Click(object sender, RoutedEventArgs e)
        {
            if (NavBomba != null) NavBomba.IsSelected = true;
            SelecionarPainel("Bomba");
        }

        private void BtnIrPluvial_Click(object sender, RoutedEventArgs e)
        {
            if (NavPluvial != null) NavPluvial.IsSelected = true;
            SelecionarPainel("Pluvial");
        }

        private void CalcularCaixaGordura_Click(object sender, RoutedEventArgs e)
        {
            int cozinhas = int.TryParse("1"?.Trim(), out int c) && c > 0 ? c : 1;
            int pessoas = int.TryParse((CaixaPopulacaoTextBox?.Text ?? "5")?.Trim(), out int p) && p > 0 ? p : cozinhas * 5;

            string spec;
            string modeloCg;
            double volCg;
            string dimCg;
            int septoCg;
            int saidaCg;

            if (cozinhas == 1 && pessoas <= 2)
            {
                modeloCg = "Caixa de Gordura Pequena (CGP)";
                volCg = 18;
                dimCg = "Ø interno 30 cm";
                septoCg = 20;
                saidaCg = 75;
                spec = $"{modeloCg} — Volume útil mín.: 18 L | {dimCg} | Parte submersa septo: 20 cm | Tubo saída: DN 75 (ABNT NBR 8160).";
            }
            else if (cozinhas <= 2 && pessoas <= 12)
            {
                modeloCg = "Caixa de Gordura Simples (CGS)";
                volCg = 31;
                dimCg = "Ø interno 40 cm";
                septoCg = 20;
                saidaCg = 75;
                spec = $"{modeloCg} — Volume útil mín.: 31 L | {dimCg} | Parte submersa septo: 20 cm | Tubo saída: DN 75 (ABNT NBR 8160).";
            }
            else if (cozinhas <= 12 && pessoas <= 60)
            {
                modeloCg = "Caixa de Gordura Dupla (CGD)";
                volCg = 120;
                dimCg = "Ø interno 60 cm";
                septoCg = 35;
                saidaCg = 100;
                spec = $"{modeloCg} — Volume útil mín.: 120 L | {dimCg} | Parte submersa septo: 35 cm | Tubo saída: DN 100 (ABNT NBR 8160).";
            }
            else
            {
                modeloCg = "Caixa de Gordura Especial (CGE - Prismática)";
                volCg = (2.0 * pessoas) + 20.0;
                dimCg = "Prismática conforme volume";
                septoCg = 40;
                saidaCg = 100;
                spec = $"{modeloCg} — Volume útil mín. V = 2N + 20 = {volCg:F0} L | Parte submersa septo: 40 cm | Tubo saída: DN 100 (ABNT NBR 8160).";
            }

            _snapshot.GreaseTrap = new GreaseTrapSnapshotData
            {
                KitchenCount = cozinhas,
                Population = pessoas,
                ModelName = modeloCg,
                MinimumVolumeLiters = volCg,
                DimensionsOrDiameter = dimCg,
                SeptumSubmersionCm = septoCg,
                OutletDiameterMm = saidaCg
            };
        }

        private async void DetectarReservatoriosRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                _ultimoScanReservatorios = await _revitBridge.ScanReservoirsAndStreetFeed();

                var upperTank = _ultimoScanReservatorios.Reservoirs
                    .Where(r => !r.IsLowerCistern)
                    .OrderByDescending(r => r.ElevationMeters)
                    .FirstOrDefault();

                if (upperTank != null && CaixaCotaSuperiorTextBox != null)
                    CaixaCotaSuperiorTextBox.Text = upperTank.ElevationMeters.ToString("F2");

                if (CaixaDiagnostico3DText != null)
                    CaixaDiagnostico3DText.Text = _ultimoScanReservatorios.Summary;

                MessageBox.Show(_ultimoScanReservatorios.Summary, "Bahr Hidro — Reservatórios & Entrada da Rua (3D)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao ler reservatórios do Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AtualizarDiagnosticoReservatorioERua(double volumeExigidoTotalLiters, int comercialSuperiorSugerido)
        {
            double pressaoRua = TryParsePtBrDouble(CaixaPressaoRuaTextBox?.Text ?? "15,0", out double pr) ? pr : 15.0;
            double cotaSup = TryParsePtBrDouble(CaixaCotaSuperiorTextBox?.Text ?? "4,20", out double csVal) ? csVal : 4.20;
            if (PressaoRuaAguaFriaTextBox != null)
                PressaoRuaAguaFriaTextBox.Text = pressaoRua.ToString("F1");

            // Perda estimada no cavalete/hidrômetro + tubulação de subida (~2,5 m.c.a.) + 1,0 m.c.a. residual na boia
            double perdaEstimadaRamal = 2.50;
            double pressaoChegadaBoia = pressaoRua - cotaSup - perdaEstimadaRamal;

            var sb = new System.Text.StringBuilder();
            if (_ultimoScanReservatorios != null && _ultimoScanReservatorios.Reservoirs.Count > 0)
            {
                double volumeModeladoTotal = _ultimoScanReservatorios.Reservoirs.Sum(r => r.ModeledVolumeLiters);
                if (volumeModeladoTotal > 0)
                {
                    if (volumeModeladoTotal >= volumeExigidoTotalLiters)
                        sb.AppendLine($"✓ RESERVATÓRIO EXISTENTE NO 3D APROVADO: Volume modelado ({volumeModeladoTotal:F0} L) atende ao exigido pela NBR 5626 ({volumeExigidoTotalLiters:F0} L).");
                    else
                        sb.AppendLine($"⚠ AMPLIAÇÃO NECESSÁRIA: O reservatório modelado no 3D ({volumeModeladoTotal:F0} L) é menor que o mínimo normativo ({volumeExigidoTotalLiters:F0} L). Substitua por reservatório comercial de {comercialSuperiorSugerido} L.");
                }
                else
                {
                    sb.AppendLine($"✓ Reservatório 3D identificado na cota Z = {cotaSup:F2} m (adote capacidade comercial de {comercialSuperiorSugerido} L).");
                }
            }

            if (pressaoChegadaBoia >= 1.0)
            {
                sb.Append($"✓ ABASTECIMENTO DIRETO PELA RUA VIÁVEL: Com pressão de {pressaoRua:F1} m.c.a. na rua, a água chega na boia do reservatório (cota {cotaSup:F2} m) com ~{pressaoChegadaBoia:F2} m.c.a. disponíveis (dispensa bomba de recalque).");
            }
            else
            {
                sb.Append($"⚠ PRESSÃO DA RUA INSUFICIENTE PARA ALIMENTAÇÃO DIRETA: Pressão residual estimada na cota {cotaSup:F2} m seria de {pressaoChegadaBoia:F2} m.c.a. (< 1,0 m.c.a.). Recomenda-se cisterna inferior + motobomba de recalque.");
            }

            if (CaixaDiagnostico3DText != null)
                CaixaDiagnostico3DText.Text = sb.ToString();
        }

        private async void SubRedeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ignorandoTrocaSubRede || _revitBridge == null || SubRedeComboBox == null || SubRedeComboBox.SelectedIndex < 0)
                return;
            await RecarregarSubRedeInternoAsync();
        }

        private async void RecarregarSubRede_Click(object sender, RoutedEventArgs e)
        {
            await RecarregarSubRedeInternoAsync();
        }

        private async Task RecarregarSubRedeInternoAsync()
        {
            if (_revitBridge == null) return;
            try
            {
                int subIdx = SubRedeComboBox != null ? SubRedeComboBox.SelectedIndex : -1;
                double pressaoRua = TryParsePtBrDouble(PressaoRuaAguaFriaTextBox?.Text ?? "15,0", out double pr) && pr >= 0 ? pr : 15.0;
                var importData = await _revitBridge.ImportNetworkAutomaticallyFromRevit(subIdx, pressaoRua);
                CarregarRedeImportadaNaTela(importData);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Aviso ao carregar sub-rede:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void DimensionarEsgoto3DRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.CalculateAndApplySewer3D();

                if (EsgotoUhcText != null) EsgotoUhcText.Text = $"{res.TotalUhc} UHC";
                if (EsgotoDiametroText != null) EsgotoDiametroText.Text = $"DN {res.MainCollectorDiameterMm} mm";
                if (EsgotoInclinacaoText != null) EsgotoInclinacaoText.Text = $"{res.MainCollectorSlopePercent:F1}%";
                if (EsgotoTipoText != null) EsgotoTipoText.Text = "Rede 3D Integrada (NBR 8160)";

                if (Esgoto3DRisersText != null)
                {
                    if (res.Risers.Count > 0)
                    {
                        var lines = res.Risers.Select(r =>
                            $"• {r.RiserName} ({r.Classification}): {r.AccumulatedUhc} UHC → DN {r.DiameterMm} mm" +
                            (r.SlopePercent > 0 ? $" (i={r.SlopePercent:F1}%)" : $" (H={r.HeightMeters:F2}m)"));
                        Esgoto3DRisersText.Text = string.Join("\n", lines);
                    }
                    else
                    {
                        Esgoto3DRisersText.Text = res.Summary;
                    }
                }

                var counts = await _revitBridge.ImportAllSewerFixturesFromRevit();
                if (counts.Count > 0)
                {
                    foreach (var item in _sewerFixtures)
                        item.Quantity = counts.TryGetValue(item.Name, out int q) ? q : 0;
                    SewerItemsControl?.Items.Refresh();
                }

                _snapshot.Sewer = new SewerSnapshotData
                {
                    TotalUhc = res.TotalUhc,
                    RequiredDiameterMm = res.MainCollectorDiameterMm,
                    MinimumSlopePercent = res.MainCollectorSlopePercent,
                    ComponentType = "Coletor Predial / Prumadas 3D (ABNT NBR 8160)",
                    FixturesSummary = _sewerFixtures.Where(f => f.Quantity > 0).Select(f => $"{f.Quantity}x {f.Name} ({f.Uhc} UHC)").ToList(),
                    MandatoryRecommendations = $"Dimensionamento 3D aplicado em {res.PipesUpdated} de {res.PipesAnalyzed} trechos sanitários. PVC Esgoto Série Normal (ramais) e Série Reforçada (tubos de queda e subcoletores). Ventilação conforme ABNT NBR 8160 com terminal +0,30 m acima da cobertura."
                };

                if (MemStatusEsgotoText != null)
                    MemStatusEsgotoText.Text = $"Rede 3D: {res.TotalUhc} UHC | Coletor: DN {res.MainCollectorDiameterMm} mm (i = {res.MainCollectorSlopePercent:F1}%).";

                MessageBox.Show(res.Summary, "Bahr Hidro — Esgoto 3D (ABNT NBR 8160)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao dimensionar rede de esgoto 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        
        private void VerificarPressaoEstaticaMaximaVrp()
        {
            if (_snapshot.ColdWaterNetwork == null) return;
            try
            {
                double maxStatic = 0;
                string criticalNode = "";
                var nodesProp = _snapshot.ColdWaterNetwork.GetType().GetProperty("Nodes");
                if (nodesProp?.GetValue(_snapshot.ColdWaterNetwork) is System.Collections.IEnumerable nodes)
                {
                    foreach (var n in nodes)
                    {
                        if (n == null) continue;
                        var t = n.GetType();
                        double st = 0;
                        var pStat = t.GetProperty("StaticPressureMca") ?? t.GetProperty("AvailablePressureMca") ?? t.GetProperty("ResidualPressureMca");
                        if (pStat != null && pStat.GetValue(n) is double d) st = d;
                        if (st > maxStatic)
                        {
                            maxStatic = st;
                            criticalNode = (t.GetProperty("NodeId")?.GetValue(n) ?? t.GetProperty("Name")?.GetValue(n) ?? "Ponto Inferior")?.ToString() ?? "";
                        }
                    }
                }

                if (maxStatic > 40.0)
                {
                    string vrpAlert = $"ALERTA VRP (ABNT NBR 5626): Pressão estática máxima de {maxStatic:F2} m.c.a. detectada em '{criticalNode}' (excede limite normativo de 40,0 m.c.a.). Obrigatória instalação de Válvula Redutora de Pressão (VRP) nos pavimentos inferiores.";
                    if (!_snapshot.ColdWaterDiagnosticAlerts.Contains(vrpAlert))
                        _snapshot.ColdWaterDiagnosticAlerts.Add(vrpAlert);
                }
            }
            catch { }
        }

        private async void GerarDocumentacao3DAbnt_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                bool isos = ChkGerarIsometricos3D?.IsChecked == true;
                bool schedules = ChkCriarTabelasNativas?.IsChecked == true;
                bool sheet = ChkMontarPranchaAbnt?.IsChecked == true;
                var res = await _revitBridge.GenerateIsometricsAndAbntSheet(isos, sheet, schedules);
                if (Doc3DStatusText != null) Doc3DStatusText.Text = res.Summary;
                MessageBox.Show(res.Summary, "Bahr Hidro — Documentação 3D ABNT", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar documentação 3D ABNT:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void GerarEsquemaVertical_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.GenerateVerticalRiserSchematic();
                MessageBox.Show(
                    $"{res.Summary}\n\nA vista '{res.ViewName}' foi criada no Navegador de Projetos (Vistas de Detalhe) e aberta na tela do Revit.",
                    "Bahr Hidro — Esquema Vertical de Prumadas",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar esquema vertical de prumadas:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async System.Threading.Tasks.Task<BahrHidro.Core.Reporting.BimMaterialTakeoff?> ObterTakeoffParaSinapiAsync()
        {
            if (_revitBridge == null) return null;
            return await _revitBridge.ExtractBomFromRevit(_snapshot.ProjectName ?? "Projeto Hidrossanitário BIM", 10.0);
        }

        private sealed class EditableSinapiBudgetRow : System.ComponentModel.INotifyPropertyChanged
        {
            private readonly Action _onValueChanged;
            private string _commercialRef;
            private string _sinapiCode;
            private decimal _unitPriceBrl;
            private string _unitPriceText;

            public EditableSinapiBudgetRow(BahrHidro.Core.Reporting.SinapiBudgetLine src, Action onValueChanged)
            {
                _onValueChanged = onValueChanged;
                ItemNumber = src.ItemNumber;
                SystemName = src.SystemName;
                Description = src.Description;
                DiameterDisplay = src.DiameterDisplay;
                _commercialRef = src.CommercialRef;
                _sinapiCode = src.SinapiCode;
                Unit = src.Unit;
                Quantity = src.Quantity;
                _unitPriceBrl = src.UnitPriceBrl;
                _unitPriceText = src.UnitPriceBrl.ToString("N2", new System.Globalization.CultureInfo("pt-BR"));
            }

            public int ItemNumber { get; }
            public string SystemName { get; }
            public string Description { get; }
            public string DiameterDisplay { get; }
            public string Unit { get; }
            public double Quantity { get; }

            public string CommercialRef
            {
                get => _commercialRef;
                set
                {
                    if (_commercialRef != value)
                    {
                        _commercialRef = value ?? "";
                        OnPropertyChanged(nameof(CommercialRef));
                    }
                }
            }

            public string SinapiCode
            {
                get => _sinapiCode;
                set
                {
                    if (_sinapiCode != value)
                    {
                        _sinapiCode = value ?? "";
                        OnPropertyChanged(nameof(SinapiCode));
                    }
                }
            }

            public decimal UnitPriceBrl => _unitPriceBrl;

            public string UnitPriceText
            {
                get => _unitPriceText;
                set
                {
                    _unitPriceText = value ?? "";
                    OnPropertyChanged(nameof(UnitPriceText));
                    if (TryParseFlexibleDecimal(_unitPriceText, out decimal parsed) && parsed >= 0m)
                    {
                        _unitPriceBrl = Math.Round(parsed, 2);
                        OnPropertyChanged(nameof(UnitPriceBrl));
                        OnPropertyChanged(nameof(TotalPriceBrl));
                        _onValueChanged();
                    }
                }
            }

            public decimal TotalPriceBrl => Math.Round((decimal)Quantity * _unitPriceBrl, 2);

            public BahrHidro.Core.Reporting.SinapiBudgetLine ToBudgetLine()
            {
                return new BahrHidro.Core.Reporting.SinapiBudgetLine(
                    ItemNumber,
                    SystemName,
                    Description,
                    DiameterDisplay,
                    CommercialRef,
                    SinapiCode,
                    Unit,
                    Quantity,
                    UnitPriceBrl,
                    TotalPriceBrl);
            }

            private static bool TryParseFlexibleDecimal(string raw, out decimal result)
            {
                result = 0m;
                if (string.IsNullOrWhiteSpace(raw)) return false;
                string cleaned = raw.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (cleaned.Contains(',') && cleaned.Contains('.'))
                    cleaned = cleaned.Replace(".", "").Replace(',', '.');
                else if (cleaned.Contains(','))
                    cleaned = cleaned.Replace(',', '.');

                return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out result);
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged(string propName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propName));
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<EditableSinapiBudgetRow> _sinapiBudgetRows = new();
        private Dictionary<string, decimal> _importedSinapiPrices = new(StringComparer.OrdinalIgnoreCase);
        private string _importedSinapiSourceLabel = "";

        private double ObterBdiPercentual()
        {
            if (SinapiBdiTextBox == null || string.IsNullOrWhiteSpace(SinapiBdiTextBox.Text))
                return 0.0;
            string raw = SinapiBdiTextBox.Text.Replace("%", "").Trim().Replace(',', '.');
            return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bdi) && bdi >= 0
                ? bdi
                : 0.0;
        }

        private void AtualizarTotaisSinapi()
        {
            if (SinapiPreviewText == null) return;
            if (_sinapiBudgetRows.Count == 0)
            {
                string loadedInfo = string.IsNullOrEmpty(_importedSinapiSourceLabel)
                    ? ""
                    : $" (Tabela {_importedSinapiSourceLabel} carregada com {_importedSinapiPrices.Count} códigos — clique em 'Carregar Itens do 3D' para aplicar)";
                SinapiPreviewText.Text = "Clique em 'Carregar Itens do 3D (SINAPI)' para preencher a tabela editável, importar planilha SINAPI por estado ou configurar o BDI (%)." + loadedInfo;
                return;
            }

            var ptBr = new System.Globalization.CultureInfo("pt-BR");
            decimal directTotal = _sinapiBudgetRows.Sum(r => r.TotalPriceBrl);
            double bdiPct = ObterBdiPercentual();
            decimal bdiVal = Math.Round(directTotal * ((decimal)bdiPct / 100m), 2);
            decimal grandTotal = directTotal + bdiVal;
            string sourceTag = string.IsNullOrEmpty(_importedSinapiSourceLabel) ? "" : $"  |  Base: {_importedSinapiSourceLabel}";

            SinapiPreviewText.Text =
                $"Itens orçados: {_sinapiBudgetRows.Count}  |  " +
                $"Custo Direto: {directTotal.ToString("C2", ptBr)}  |  " +
                $"BDI ({bdiPct:F2}%): {bdiVal.ToString("C2", ptBr)}  |  " +
                $"TOTAL COM BDI: {grandTotal.ToString("C2", ptBr)}{sourceTag}";
        }

        private void SinapiBdiTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            AtualizarTotaisSinapi();
        }

        private int AplicarPrecosSinapiNaGradeAtual()
        {
            if (_importedSinapiPrices.Count == 0 || _sinapiBudgetRows.Count == 0)
                return 0;

            var ptBr = new System.Globalization.CultureInfo("pt-BR");
            int updated = 0;

            foreach (var row in _sinapiBudgetRows)
            {
                string normCode = BahrHidro.Core.Reporting.SinapiOfficialPriceImporter.NormalizeSinapiCode(row.SinapiCode);
                if (!string.IsNullOrEmpty(normCode) && _importedSinapiPrices.TryGetValue(normCode, out decimal officialPrice) && officialPrice > 0m)
                {
                    row.UnitPriceText = officialPrice.ToString("N2", ptBr);
                    updated++;
                }
            }

            AtualizarTotaisSinapi();
            return updated;
        }

        private async void ImportarTabelaSinapi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Importar Tabela Oficial SINAPI por Estado (.xlsx ou .csv)",
                    Filter = "Planilhas SINAPI (*.xlsx;*.csv)|*.xlsx;*.csv|Excel SINAPI (*.xlsx)|*.xlsx|CSV SINAPI (*.csv)|*.csv"
                };

                if (dlg.ShowDialog() != true)
                    return;

                var importRes = BahrHidro.Core.Reporting.SinapiOfficialPriceImporter.ImportFromFile(dlg.FileName);
                _importedSinapiPrices = importRes.PricesByNormalizedCode;
                _importedSinapiSourceLabel = $"SINAPI-{importRes.StateUf} ({importRes.SourceFileName})";

                if (_sinapiBudgetRows.Count == 0)
                {
                    var takeoff = await ObterTakeoffParaSinapiAsync();
                    if (takeoff != null && (takeoff.Pipes.Count > 0 || takeoff.Fittings.Count > 0 || takeoff.Kits.Count > 0))
                    {
                        var lines = BahrHidro.Core.Reporting.SinapiCommercialMapper.BuildBudgetLines(takeoff);
                        foreach (var line in lines)
                            _sinapiBudgetRows.Add(new EditableSinapiBudgetRow(line, AtualizarTotaisSinapi));

                        if (SinapiBudgetDataGrid != null)
                            SinapiBudgetDataGrid.ItemsSource = _sinapiBudgetRows;
                    }
                }

                int updatedCount = AplicarPrecosSinapiNaGradeAtual();
                AtualizarTotaisSinapi();

                MessageBox.Show(
                    $"Tabela SINAPI importada com sucesso!\n\n" +
                    $"Estado (UF) detectado: {importRes.StateUf}\n" +
                    $"Códigos lidos na planilha: {importRes.TotalCodesLoaded}\n" +
                    $"Itens atualizados automaticamente na tabela: {updatedCount} de {_sinapiBudgetRows.Count}",
                    "Bahr Hidro — Importação SINAPI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao importar planilha SINAPI:\n{ex.Message}", "Bahr Hidro — Importação SINAPI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CalcularOrcamentoSinapi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var takeoff = await ObterTakeoffParaSinapiAsync();
                if (takeoff == null || (takeoff.Pipes.Count == 0 && takeoff.Fittings.Count == 0 && takeoff.Kits.Count == 0))
                {
                    MessageBox.Show("Nenhum tubo ou conexão foi encontrado no modelo 3D para orçar no SINAPI.", "Bahr Hidro — SINAPI", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var lines = BahrHidro.Core.Reporting.SinapiCommercialMapper.BuildBudgetLines(takeoff);
                _sinapiBudgetRows.Clear();
                foreach (var line in lines)
                {
                    var adjustedLine = line;
                    string normCode = BahrHidro.Core.Reporting.SinapiOfficialPriceImporter.NormalizeSinapiCode(line.SinapiCode);
                    if (!string.IsNullOrEmpty(normCode) && _importedSinapiPrices.TryGetValue(normCode, out decimal officialPrice) && officialPrice > 0m)
                    {
                        adjustedLine = line with
                        {
                            UnitPriceBrl = officialPrice,
                            TotalPriceBrl = Math.Round((decimal)line.Quantity * officialPrice, 2)
                        };
                    }

                    _sinapiBudgetRows.Add(new EditableSinapiBudgetRow(adjustedLine, AtualizarTotaisSinapi));
                }

                if (SinapiBudgetDataGrid != null)
                {
                    SinapiBudgetDataGrid.ItemsSource = _sinapiBudgetRows;
                }

                AtualizarTotaisSinapi();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar orçamento SINAPI:\n{ex.Message}", "Bahr Hidro — SINAPI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExportarOrcamentoSinapiExcel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BahrHidro.Core.Reporting.BimMaterialTakeoff? takeoff = null;
                if (_sinapiBudgetRows.Count == 0)
                {
                    takeoff = await ObterTakeoffParaSinapiAsync();
                    if (takeoff == null || (takeoff.Pipes.Count == 0 && takeoff.Fittings.Count == 0 && takeoff.Kits.Count == 0))
                    {
                        MessageBox.Show("Modele ou extraia as tubulações no 3D antes de exportar a planilha SINAPI.", "Bahr Hidro — SINAPI", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var lines = BahrHidro.Core.Reporting.SinapiCommercialMapper.BuildBudgetLines(takeoff);
                    foreach (var line in lines)
                    {
                        var adjustedLine = line;
                        string normCode = BahrHidro.Core.Reporting.SinapiOfficialPriceImporter.NormalizeSinapiCode(line.SinapiCode);
                        if (!string.IsNullOrEmpty(normCode) && _importedSinapiPrices.TryGetValue(normCode, out decimal officialPrice) && officialPrice > 0m)
                        {
                            adjustedLine = line with
                            {
                                UnitPriceBrl = officialPrice,
                                TotalPriceBrl = Math.Round((decimal)line.Quantity * officialPrice, 2)
                            };
                        }
                        _sinapiBudgetRows.Add(new EditableSinapiBudgetRow(adjustedLine, AtualizarTotaisSinapi));
                    }

                    if (SinapiBudgetDataGrid != null)
                        SinapiBudgetDataGrid.ItemsSource = _sinapiBudgetRows;

                    AtualizarTotaisSinapi();
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Salvar Orçamento Editado — SINAPI & Catálogo Tigre/Amanco",
                    Filter = "Planilha Excel (*.xlsx)|*.xlsx",
                    FileName = "Orcamento_SINAPI_Editado_BahrHidro.xlsx"
                };

                if (dlg.ShowDialog() == true)
                {
                    var editedLines = _sinapiBudgetRows.Select(r => r.ToBudgetLine()).ToList();
                    double bdiPct = ObterBdiPercentual();

                    BahrHidro.Core.Reporting.ExcelReportGenerator.GenerateProjectReportWithBom(
                        _snapshot.ColdWaterNetwork,
                        takeoff,
                        dlg.FileName,
                        editedLines,
                        bdiPct);

                    var ptBr = new System.Globalization.CultureInfo("pt-BR");
                    decimal directTotal = editedLines.Sum(l => l.TotalPriceBrl);
                    decimal bdiVal = Math.Round(directTotal * ((decimal)bdiPct / 100m), 2);
                    decimal grandTotal = directTotal + bdiVal;

                    MessageBox.Show(
                        $"Planilha SINAPI exportada com os valores editados!\n\n" +
                        $"Arquivo: {dlg.FileName}\n" +
                        $"Aba: 'Orçamento SINAPI & Fabricante'\n" +
                        $"Custo Direto: {directTotal.ToString("C2", ptBr)}\n" +
                        $"BDI ({bdiPct:F2}%): {bdiVal.ToString("C2", ptBr)}\n" +
                        $"Total Geral: {grandTotal.ToString("C2", ptBr)}",
                        "Bahr Hidro — Orçamento SINAPI",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao exportar planilha SINAPI:\n{ex.Message}", "Bahr Hidro — SINAPI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    
        private List<ClashItem3DInfo> _lastClashItems = new();

        private void BtnIrVistas3D_Click(object sender, RoutedEventArgs e)
        {
            if (NavVistas3DVivas != null) NavVistas3DVivas.IsSelected = true;
        }

        private async void ExecutarClash3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.RunStructuralAndLsfClashAudit();
                _lastClashItems = res.Clashes;
                if (ClashResultsDataGrid != null)
                    ClashResultsDataGrid.ItemsSource = _lastClashItems;
                if (ClashAndDiagStatusText != null)
                    ClashAndDiagStatusText.Text = res.Summary;

                MessageBox.Show(res.Summary, "Bahr Hidro — Compatibilização 3D (Estrutura & LSF)", MessageBoxButton.OK,
                    res.ClashesFound == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro na auditoria de Clash 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ZoomClashElemento3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is long elemId)
            {
                try
                {
                    await _revitBridge.FocusElementIn3DView(elemId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Não foi possível focar o elemento no 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void ExportarRelatorioFurosCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_lastClashItems.Count == 0)
            {
                MessageBox.Show("Execute primeiro a auditoria 'Auditar Clash 3D (Estrutura & LSF)' para listar as passagens.", "Bahr Hidro — Furos & Passagens", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Exportar Relatório de Furos e Passagens (Estrutura & LSF)",
                Filter = "Arquivo CSV (*.csv)|*.csv",
                FileName = "Relatorio_Furos_Passagens_LSF_Estrutura.csv"
            };

            if (dlg.ShowDialog() == true)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Item;ID_Tubo_Revit;Sistema;DN_mm;Categoria_Estrutural;Elemento_Estrutural;Modelo_Origem;Nivel;Cota_Z_m;Furo_Recomendado");
                foreach (var c in _lastClashItems)
                {
                    sb.AppendLine($"{c.Index};{c.PipeElementId};{c.PipeSystem};{c.DiameterMm};{c.StructuralCategory};{c.StructuralElementName};{c.SourceModel};{c.LevelName};{c.ElevationMeters:F2};{c.RecommendedSleeve}");
                }
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                MessageBox.Show($"Relatório de furos exportado com sucesso!\n\nArquivo: {dlg.FileName}", "Bahr Hidro — Furos & Passagens", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void GerarMapaDiagnostico3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                var res = await _revitBridge.ApplyHydraulicDiagnosticColorMap(_snapshot.ColdWaterNetwork);
                if (ClashAndDiagStatusText != null)
                    ClashAndDiagStatusText.Text = res.Summary;
                MessageBox.Show(res.Summary, "Bahr Hidro — Mapa Visual 3D Hidráulico", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar Mapa Visual 3D:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string _lastSpatialContextJson = "";

        private async void ExtrairContextoEspacial3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                _lastSpatialContextJson = await _revitBridge.ExtractSpatialWetAreaJson();
                if (SpatialContextJsonTextBox != null)
                    SpatialContextJsonTextBox.Text = _lastSpatialContextJson;
                if (SpatialBridgeStatusText != null)
                    SpatialBridgeStatusText.Text = "Contexto espacial 3D extraído em JSON (pronto para envio à API externa de traçado).";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao extrair contexto espacial 3D:\n{ex.Message}", "Bahr Hidro — Ponte Espacial 3D", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GerarPlanoRoteamentoTeste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string src = !string.IsNullOrWhiteSpace(_lastSpatialContextJson) ? _lastSpatialContextJson : (SpatialContextJsonTextBox?.Text ?? "");
                if (string.IsNullOrWhiteSpace(src))
                {
                    MessageBox.Show("Clique primeiro em 'Extrair Contexto Espacial 3D (JSON)'.", "Bahr Hidro — Ponte Espacial 3D", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var ctx = System.Text.Json.JsonSerializer.Deserialize<BahrHidro.Core.Routing.SpatialWetAreaContext>(
                    src,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (ctx == null) return;
                string planJson = BahrHidro.Core.Routing.AutoRoutingSerializer.BuildSamplePlanFromContext(ctx);
                if (SpatialContextJsonTextBox != null)
                    SpatialContextJsonTextBox.Text = planJson;
                if (SpatialBridgeStatusText != null)
                    SpatialBridgeStatusText.Text = "Plano de traçado JSON gerado a partir das coordenadas reais dos aparelhos e prumadas.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar plano de traçado JSON:\n{ex.Message}", "Bahr Hidro — Ponte Espacial 3D", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ExecutarPlanoRoteamento3D_Click(object sender, RoutedEventArgs e)
        {
            if (_revitBridge == null) return;
            try
            {
                string planJson = SpatialContextJsonTextBox?.Text ?? "";
                int count = await _revitBridge.ExecuteAutoRoutingPlanJson(planJson);
                if (SpatialBridgeStatusText != null)
                    SpatialBridgeStatusText.Text = $"Execução concluída: {count} trecho(s) de tubulação criados fisicamente no Revit 3D.";
                MessageBox.Show($"{count} trecho(s) de tubulação foram criados no modelo 3D a partir do plano JSON!", "Bahr Hidro — Executor 3D", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao executar plano JSON no Revit 3D:\n{ex.Message}", "Bahr Hidro — Executor 3D", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
}
}
