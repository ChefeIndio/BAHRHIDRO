using System;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BahrHidro.Core.Hydraulic.Network;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public class RevitBimBridge : IRevitBimBridge
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private RevitNetworkExtractionResult? _ultimaExtracao;

        public RevitBimBridge(UIDocument uidoc)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
        }

        public (string ProjectName, string ClientName, string Address, string Author) GetProjectInfo()
        {
            var info = _doc.ProjectInformation;
            if (info == null) return ("", "", "", "");
            return (
                info.Name ?? "",
                info.ClientName ?? "",
                info.Address ?? "",
                info.Author ?? ""
            );
        }

        public void SaveProjectInfo(string projectName, string clientName, string address, string author)
        {
            using (Transaction tx = new Transaction(_doc, "Bahr Hidro - Atualizar Dados do Projeto"))
            {
                tx.Start();
                var info = _doc.ProjectInformation;
                if (info != null)
                {
                    info.Name = projectName;
                    info.ClientName = clientName;
                    info.Address = address;
                    info.Author = author;
                }
                tx.Commit();
            }
        }

        public void ImportNetworkFromRevit(Window ownerWindow, Action<HydraulicNetwork> onNetworkImported)
        {
            try
            {
                ownerWindow.Hide();

                Reference pickedRef = _uidoc.Selection.PickObject(
                    ObjectType.Element,
                    "Selecione o Reservatório ou o tubo inicial de saída:");

                Element startElement = _doc.GetElement(pickedRef);
                _ultimaExtracao = RevitNetworkExtractor.ExtractFromStartElement(_doc, startElement);

                onNetworkImported(_ultimaExtracao.Network);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Usuário cancelou com ESC
            }
            finally
            {
                ownerWindow.ShowDialog();
            }
        }

        public int ApplyDiametersToRevit(NetworkCalculationResult result)
        {
            if (_ultimaExtracao == null)
                throw new InvalidOperationException("Importe uma rede do modelo 3D do Revit antes de aplicar os diâmetros.");

            int tubosAtualizados = 0;
            using (Transaction tx = new Transaction(_doc, "Bahr Hidro - Aplicar Diâmetros 3D"))
            {
                tx.Start();

                foreach (var segResult in result.Segments)
                {
                    if (segResult.SelectedDiameterMm <= 0) continue;
                    if (!_ultimaExtracao.PipesBySegmentId.TryGetValue(segResult.SegmentId, out var tubosDoTrecho))
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

            return tubosAtualizados;
        }
    }
}
