using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BahrHidro.Core;
using BahrHidro.Core.Catalog;
using BahrHidro.Core.Hydraulic;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.Core.Reporting;

namespace BahrHidro.Revit
{
    [Transaction(TransactionMode.Manual)]
    public class MainCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1. Seleção do Reservatório / ponto de alimentação no modelo 3D
                Reference pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    "Selecione o Reservatório (Caixa d'água) ou o tubo inicial de saída:");

                Element startElement = doc.GetElement(pickedRef);

                // 2. Carrega o catálogo local configurado na interface BahrHidro.UI
                string catalogPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BahrHidro",
                    "pipe-catalog.json");

                if (!File.Exists(catalogPath))
                {
                    TaskDialog.Show("Bahr Hidro", "Catálogo de tubos não encontrado. Abra o BahrHidro.UI e cadastre os diâmetros comerciais primeiro.");
                    return Result.Cancelled;
                }

                var repository = new PipeCatalogRepository(catalogPath);
                var catalogService = new PipeCatalogService(repository);
                var produtos = catalogService.GetAllAsync().GetAwaiter().GetResult();

                var opcoesDiametro = produtos
                    .Select(p => new PipeDiameterOption($"{p.Manufacturer} {p.NominalDesignation}", (double)p.InternalDiameterMm))
                    .ToList();

                if (opcoesDiametro.Count == 0)
                {
                    TaskDialog.Show("Bahr Hidro", "Seu catálogo de tubos está vazio. Cadastre pelo menos uma bitola antes de dimensionar.");
                    return Result.Cancelled;
                }

                // 3. Extração automática da rede 3D do Revit
                RevitNetworkExtractionResult extraction = RevitNetworkExtractor.ExtractFromStartElement(doc, startElement);

                if (extraction.Network.Segments.Count == 0)
                {
                    TaskDialog.Show("Bahr Hidro", "Nenhuma tubulação conectada foi encontrada a partir do elemento selecionado.");
                    return Result.Cancelled;
                }

                // 4. Cálculo hidráulico no motor BahrHidro.Core
                double viscosidadeAgua = 0.000001003;
                NetworkCalculationResult resultado = HydraulicNetworkSolver.Solve(
                    extraction.Network,
                    viscosidadeAgua,
                    opcoesDiametro,
                    3.0);

                // 5. Atualiza os diâmetros dos tubos no modelo 3D do Revit
                using (Transaction tx = new Transaction(doc, "Bahr Hidro - Dimensionar Rede 3D"))
                {
                    tx.Start();

                    foreach (SegmentCalculationResult segResult in resultado.Segments)
                    {
                        if (segResult.SelectedDiameterMm <= 0) continue;

                        HydraulicPipeSegment? segModel = extraction.Network.Segments
                            .FirstOrDefault(s => s.Name == segResult.SegmentName);

                        if (segModel == null || !extraction.PipesBySegmentId.TryGetValue(segModel.Id, out var tubosDoTrecho))
                            continue;

                        double diametroEmPes = UnitUtils.ConvertToInternalUnits(segResult.SelectedDiameterMm, UnitTypeId.Millimeters);

                        foreach (Pipe tubo in tubosDoTrecho)
                        {
                            Parameter? paramDiam = tubo.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                            if (paramDiam != null && !paramDiam.IsReadOnly)
                            {
                                paramDiam.Set(diametroEmPes);
                            }
                        }
                    }

                    tx.Commit();
                }

                // 6. Exporta o memorial de cálculo Excel na Área de Trabalho
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string excelPath = Path.Combine(desktopPath, $"Memorial_Revit_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
                ExcelReportGenerator.GenerateNetworkReport(resultado, excelPath);

                double menorPressao = resultado.Nodes
                    .Where(n => n.NodeType != HydraulicNodeType.Reservoir)
                    .Select(n => n.AvailablePressureMca)
                    .DefaultIfEmpty(0)
                    .Min();

                TaskDialog.Show(
                    "Bahr Hidro BIM - Concluído",
                    $"Dimensionamento 3D concluído com sucesso!\n\n" +
                    $"• Nós mapeados: {extraction.Network.Nodes.Count}\n" +
                    $"• Trechos dimensionados: {extraction.Network.Segments.Count}\n" +
                    $"• Pressão mínima disponível: {menorPressao:F2} m.c.a.\n\n" +
                    $"Memorial salvo em:\n{excelPath}");

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Bahr Hidro BIM - Erro", $"Falha ao processar a rede:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}