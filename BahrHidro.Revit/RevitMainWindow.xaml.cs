using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BahrHidro.Core;
using BahrHidro.Core.Catalog;
using BahrHidro.Core.Hydraulic;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.Core.Hydraulic.Rainwater;
using BahrHidro.Core.Reporting;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;

namespace BahrHidro.Revit
{
    public class RevitSegmentRow
    {
        public string Trecho { get; set; } = "";
        public string Ligacao { get; set; } = "";
        public string Comprimento { get; set; } = "";
        public string Vazao { get; set; } = "--";
        public string DiametroAtual { get; set; } = "--";
        public string DiametroCalculado { get; set; } = "--";
        public string Velocidade { get; set; } = "--";
        public string PerdaCarga { get; set; } = "--";
    }

    public class RevitNodeRow
    {
        public string Nome { get; set; } = "";
        public string Tipo { get; set; } = "";
        public string Cota { get; set; } = "";
        public string Pressao { get; set; } = "--";
        public string Status { get; set; } = "--";
    }

    public partial class RevitMainWindow : Window
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private List<PipeDiameterOption> _catalogoDiametros = new();
        private RevitNetworkExtractionResult? _extracaoAtual;
        private NetworkCalculationResult? _resultadoAtual;
        private readonly CultureInfo _pt = CultureInfo.GetCultureInfo("pt-BR");

        public RevitMainWindow(UIDocument uidoc)
        {
            InitializeComponent();
            _uidoc = uidoc;
            _doc = uidoc.Document;

            CarregarDadosDoRevit();
            CarregarCatalogo();
            InicializarAbaPluvial();
        }

        private void CarregarDadosDoRevit()
        {
            var info = _doc.ProjectInformation;
            if (info == null) return;

            if (!string.IsNullOrWhiteSpace(info.Name)) TxtObraNome.Text = info.Name;
            if (!string.IsNullOrWhiteSpace(info.ClientName)) TxtClienteNome.Text = info.ClientName;
            if (!string.IsNullOrWhiteSpace(info.Address)) TxtObraEndereco.Text = info.Address;
            if (!string.IsNullOrWhiteSpace(info.Author)) TxtEngenheiroNome.Text = info.Author;
        }

        private void CarregarCatalogo()
        {
            try
            {
                string catalogPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BahrHidro",
                    "pipe-catalog.json");

                if (File.Exists(catalogPath))
                {
                    var repo = new PipeCatalogRepository(catalogPath);
                    var service = new PipeCatalogService(repo);
                    var produtos = Task.Run(() => service.GetAllAsync()).GetAwaiter().GetResult();

                    _catalogoDiametros = produtos
                        .Select(p => new PipeDiameterOption($"{p.Manufacturer} {p.NominalDesignation}", (double)p.InternalDiameterMm))
                        .ToList();
                }

                if (_catalogoDiametros.Count > 0)
                {
                    TxtCatalogoInfo.Text = $"Catálogo pessoal ativo: {_catalogoDiametros.Count} bitolas";
                }
                else
                {
                    _catalogoDiametros = new List<PipeDiameterOption>
                    {
                        new("PVC Soldável DN 20", 17.0),
                        new("PVC Soldável DN 25", 21.6),
                        new("PVC Soldável DN 32", 27.8),
                        new("PVC Soldável DN 40", 35.2),
                        new("PVC Soldável DN 50", 44.0),
                        new("PVC Soldável DN 60", 53.0),
                        new("PVC Soldável DN 75", 66.6),
                        new("PVC Soldável DN 85", 75.6),
                        new("PVC Soldável DN 110", 97.6)
                    };
                    TxtCatalogoInfo.Text = $"Catálogo padrão ABNT ativo: {_catalogoDiametros.Count} bitolas PVC";
                }

                GridCatalogo.ItemsSource = _catalogoDiametros;
            }
            catch (Exception ex)
            {
                TxtCatalogoInfo.Text = $"Aviso catálogo: {ex.Message}";
                TxtCatalogoInfo.Foreground = new WpfBrush(WpfColor.FromRgb(220, 38, 38));
            }
        }

        private void BtnSelecionar3D_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Hide();

                Reference pickedRef = _uidoc.Selection.PickObject(
                    ObjectType.Element,
                    "Selecione o Reservatório ou o tubo inicial de saída:");

                Element startElement = _doc.GetElement(pickedRef);
                _extracaoAtual = RevitNetworkExtractor.ExtractFromStartElement(_doc, startElement);
                _resultadoAtual = null;

                TxtElementoSelecionado.Text = $"Origem: {startElement.Name} (ID {startElement.Id.Value})";
                KpiTrechos.Text = $"{_extracaoAtual.Network.Segments.Count} trechos / {_extracaoAtual.Network.Nodes.Count} nós";
                KpiVazaoTotal.Text = "-- L/s";
                KpiPressaoMin.Text = "-- m.c.a.";
                KpiPerdaMax.Text = "-- m.c.a.";

                PreencherTabelasExtraidas();

                BtnCalcularRede.IsEnabled = _extracaoAtual.Network.Segments.Count > 0;
                BtnAplicarRevit.IsEnabled = false;
                BtnExportarExcel.IsEnabled = false;

                TxtStatusGeral.Text = $"Rede mapeada ({_extracaoAtual.Network.Segments.Count} trechos). Clique em '1. Calcular Rede'.";
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                TxtStatusGeral.Text = "Seleção no modelo cancelada.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao ler rede do Revit:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                this.ShowDialog();
            }
        }

        private void PreencherTabelasExtraidas()
        {
            if (_extracaoAtual == null) return;

            var linhasTrechos = new List<RevitSegmentRow>();
            foreach (var seg in _extracaoAtual.Network.Segments)
            {
                string origem = _extracaoAtual.Network.FindNode(seg.StartNodeId)?.Name ?? "?";
                string destino = _extracaoAtual.Network.FindNode(seg.EndNodeId)?.Name ?? "?";

                string diamAtual = "--";
                if (_extracaoAtual.PipesBySegmentId.TryGetValue(seg.Id, out var tubos) && tubos.Count > 0)
                {
                    Parameter? pDiam = tubos[0].get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    if (pDiam != null)
                    {
                        double mm = UnitUtils.ConvertFromInternalUnits(pDiam.AsDouble(), UnitTypeId.Millimeters);
                        diamAtual = $"{mm.ToString("F1", _pt)} mm";
                    }
                }

                linhasTrechos.Add(new RevitSegmentRow
                {
                    Trecho = seg.Name,
                    Ligacao = $"{origem} → {destino}",
                    Comprimento = seg.LengthMeters.ToString("F2", _pt),
                    DiametroAtual = diamAtual
                });
            }
            GridTrechos.ItemsSource = linhasTrechos;

            var linhasNos = _extracaoAtual.Network.Nodes.Select(n => new RevitNodeRow
            {
                Nome = n.Name,
                Tipo = n.Type switch
                {
                    HydraulicNodeType.Reservoir => "Reservatório",
                    HydraulicNodeType.Junction => "Derivação",
                    _ => "Consumo"
                },
                Cota = n.ElevationMeters.ToString("F2", _pt)
            }).ToList();
            GridNos.ItemsSource = linhasNos;
        }

        private void BtnCalcularRede_Click(object sender, RoutedEventArgs e)
        {
            if (_extracaoAtual == null) return;

            try
            {
                double velMax = ParseDouble(TxtVelMax.Text, "Velocidade Máxima");
                double pressaoMin = ParseDouble(TxtPressaoMin.Text, "Pressão Mínima");
                double viscosidade = ParseDouble(TxtViscosidade.Text, "Viscosidade");

                _resultadoAtual = HydraulicNetworkSolver.Solve(
                    _extracaoAtual.Network,
                    viscosidade,
                    _catalogoDiametros,
                    velMax);

                var linhasTrechos = new List<RevitSegmentRow>();
                foreach (var resSeg in _resultadoAtual.Segments)
                {
                    var segModel = _extracaoAtual.Network.Segments.FirstOrDefault(s => s.Id == resSeg.SegmentId);
                    string diamAtual = "--";
                    if (segModel != null && _extracaoAtual.PipesBySegmentId.TryGetValue(segModel.Id, out var tubos) && tubos.Count > 0)
                    {
                        Parameter? pDiam = tubos[0].get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (pDiam != null)
                        {
                            double mm = UnitUtils.ConvertFromInternalUnits(pDiam.AsDouble(), UnitTypeId.Millimeters);
                            diamAtual = $"{mm.ToString("F1", _pt)} mm";
                        }
                    }

                    linhasTrechos.Add(new RevitSegmentRow
                    {
                        Trecho = resSeg.SegmentName,
                        Ligacao = resSeg.Connection,
                        Comprimento = segModel?.LengthMeters.ToString("F2", _pt) ?? "--",
                        Vazao = resSeg.FlowLitersPerSecond.ToString("F3", _pt),
                        DiametroAtual = diamAtual,
                        DiametroCalculado = $"{resSeg.SelectedDiameterMm.ToString("F1", _pt)} mm",
                        Velocidade = resSeg.VelocityMetersPerSecond.ToString("F2", _pt),
                        PerdaCarga = resSeg.TotalHeadLossMeters.ToString("F2", _pt)
                    });
                }
                GridTrechos.ItemsSource = linhasTrechos;

                var linhasNos = _resultadoAtual.Nodes.Select(n => new RevitNodeRow
                {
                    Nome = n.NodeName,
                    Tipo = n.NodeType switch
                    {
                        HydraulicNodeType.Reservoir => "Reservatório",
                        HydraulicNodeType.Junction => "Derivação",
                        _ => "Consumo"
                    },
                    Cota = n.ElevationMeters.ToString("F2", _pt),
                    Pressao = n.AvailablePressureMca.ToString("F2", _pt),
                    Status = n.NodeType == HydraulicNodeType.Reservoir
                        ? "Origem"
                        : (n.AvailablePressureMca >= pressaoMin ? "OK" : "BAIXA")
                }).ToList();
                GridNos.ItemsSource = linhasNos;

                double vazaoMax = _resultadoAtual.Segments.Select(s => s.FlowLitersPerSecond).DefaultIfEmpty(0).Max();
                double menorPressao = _resultadoAtual.Nodes
                    .Where(n => n.NodeType != HydraulicNodeType.Reservoir)
                    .Select(n => n.AvailablePressureMca)
                    .DefaultIfEmpty(0)
                    .Min();
                double perdaMax = _resultadoAtual.Segments.Select(s => s.TotalHeadLossMeters).DefaultIfEmpty(0).Max();

                KpiVazaoTotal.Text = $"{vazaoMax.ToString("F3", _pt)} L/s";
                KpiPressaoMin.Text = $"{menorPressao.ToString("F2", _pt)} m.c.a.";
                KpiPressaoMin.Foreground = new WpfBrush(menorPressao >= pressaoMin
                    ? WpfColor.FromRgb(21, 128, 61)
                    : WpfColor.FromRgb(220, 38, 38));
                KpiPerdaMax.Text = $"{perdaMax.ToString("F2", _pt)} m.c.a.";

                BtnAplicarRevit.IsEnabled = true;
                BtnExportarExcel.IsEnabled = true;
                TxtStatusGeral.Text = "Cálculo concluído! Confira os diâmetros sugeridos e clique em '2. Aplicar Diâmetros no Revit'.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro no dimensionamento:\n{ex.Message}", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnAplicarRevit_Click(object sender, RoutedEventArgs e)
        {
            if (_extracaoAtual == null || _resultadoAtual == null) return;

            try
            {
                int tubosAtualizados = 0;
                using (Transaction tx = new Transaction(_doc, "Bahr Hidro - Aplicar Diâmetros 3D"))
                {
                    tx.Start();

                    foreach (var segResult in _resultadoAtual.Segments)
                    {
                        if (segResult.SelectedDiameterMm <= 0) continue;
                        if (!_extracaoAtual.PipesBySegmentId.TryGetValue(segResult.SegmentId, out var tubosDoTrecho))
                            continue;

                        double diametroEmPes = UnitUtils.ConvertToInternalUnits(segResult.SelectedDiameterMm, UnitTypeId.Millimeters);

                        foreach (Pipe tubo in tubosDoTrecho)
                        {
                            Parameter? paramDiam = tubo.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                            if (paramDiam != null && !paramDiam.IsReadOnly)
                            {
                                paramDiam.Set(diametroEmPes);
                                tubosAtualizados++;
                            }
                        }
                    }

                    tx.Commit();
                }

                BtnCalcularRede_Click(sender, e);
                TxtStatusGeral.Text = $"Sucesso! {tubosAtualizados} tubos atualizados no modelo 3D do Revit.";
                MessageBox.Show($"{tubosAtualizados} tubos atualizados com sucesso no projeto 3D!", "Modelo BIM Atualizado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Falha ao modificar tubos no Revit:\n{ex.Message}", "Erro BIM", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExportarExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_resultadoAtual == null) return;

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string obraLimpa = string.IsNullOrWhiteSpace(TxtObraNome.Text) ? "ProjetoBIM" : TxtObraNome.Text.Trim();
                foreach (char c in Path.GetInvalidFileNameChars()) obraLimpa = obraLimpa.Replace(c, '_');

                string excelPath = Path.Combine(desktop, $"Memorial_{obraLimpa}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
                ExcelReportGenerator.GenerateNetworkReport(_resultadoAtual, excelPath);

                TxtStatusGeral.Text = $"Planilha Excel exportada: {excelPath}";
                MessageBox.Show($"Memorial de cálculo salvo em:\n{excelPath}", "Exportação Concluída", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar Excel:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSalvarProjectInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (Transaction tx = new Transaction(_doc, "Bahr Hidro - Atualizar Dados do Projeto"))
                {
                    tx.Start();
                    var info = _doc.ProjectInformation;
                    if (info != null)
                    {
                        info.Name = TxtObraNome.Text.Trim();
                        info.ClientName = TxtClienteNome.Text.Trim();
                        info.Address = TxtObraEndereco.Text.Trim();
                        info.Author = $"{TxtEngenheiroNome.Text.Trim()} ({TxtEngenheiroCrea.Text.Trim()})";
                    }
                    tx.Commit();
                }
                TxtStatusGeral.Text = "Dados da obra salvos no Project Information do Revit.";
                MessageBox.Show("Dados salvos com sucesso nas propriedades do projeto Revit!", "Bahr Hidro BIM", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Não foi possível gravar no Revit:\n{ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void InicializarAbaPluvial()
        {
            var regioes = RainwaterRegionCatalog.GetRegions();
            CmbRegiao.ItemsSource = regioes;
            CmbRegiao.SelectedItem = regioes.FirstOrDefault(r => r.City == "Ponta Grossa") ?? regioes.First();
            AtualizarIntensidadePluvial();
        }

        private void CmbRegiao_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            AtualizarIntensidadePluvial();
        }

        private void CmbPeriodoRetorno_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            AtualizarIntensidadePluvial();
        }

        private void AtualizarIntensidadePluvial()
        {
            if (CmbRegiao.SelectedItem is not RainwaterRegion regiao) return;
            TxtIntensidade.IsReadOnly = !regiao.IsCustom;
            if (!regiao.IsCustom)
            {
                var periodo = ObterPeriodoRetorno();
                double i = RainwaterRegionCatalog.AdjustIntensityForReturnPeriod(regiao.Intensity5YearsMmPerHour, periodo);
                TxtIntensidade.Text = i.ToString("F1", _pt);
            }
        }

        private void BtnCalcularPluvial_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var regiao = CmbRegiao.SelectedItem as RainwaterRegion;
                var input = new RainwaterInput
                {
                    RegionName = regiao != null && !