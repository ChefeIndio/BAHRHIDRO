using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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

        public Dictionary<string, int> ImportSewerFixturesFromRevit(Window ownerWindow)
        {
            var contagem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                ownerWindow.Hide();

                IList<Reference> refs = _uidoc.Selection.PickObjects(
                    ObjectType.Element,
                    new CategorySelectionFilter(BuiltInCategory.OST_PlumbingFixtures),
                    "Selecione os aparelhos sanitários do ramal de esgoto e clique em 'Concluir' na barra superior:");

                foreach (Reference r in refs)
                {
                    Element elem = _doc.GetElement(r);
                    if (elem == null) continue;

                    string familia = (elem as FamilyInstance)?.Symbol?.FamilyName ?? "";
                    string tipo = elem.Name ?? "";
                    string categoria = MapearAparelhoEsgoto($"{familia} {tipo}");

                    if (!string.IsNullOrEmpty(categoria))
                    {
                        contagem[categoria] = contagem.TryGetValue(categoria, out int atual) ? atual + 1 : 1;
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
            }
            finally
            {
                ownerWindow.ShowDialog();
            }

            return contagem;
        }

        public RevitRoofData? CaptureRoofFromRevit(Window ownerWindow)
        {
            try
            {
                ownerWindow.Hide();

                Reference pickedRef = _uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new CategorySelectionFilter(BuiltInCategory.OST_Roofs),
                    "Selecione o telhado / cobertura no modelo 3D:");

                Element roof = _doc.GetElement(pickedRef);
                if (roof == null) return null;

                double areaM2 = 0;
                Parameter? areaParam = roof.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (areaParam != null && areaParam.HasValue)
                {
                    areaM2 = UnitUtils.ConvertFromInternalUnits(areaParam.AsDouble(), UnitTypeId.SquareMeters);
                }

                double widthM = 10.0;
                double projM = 6.0;
                double heightM = 1.0;

                BoundingBoxXYZ? bb = roof.get_BoundingBox(null);
                if (bb != null)
                {
                    double dx = UnitUtils.ConvertFromInternalUnits(bb.Max.X - bb.Min.X, UnitTypeId.Meters);
                    double dy = UnitUtils.ConvertFromInternalUnits(bb.Max.Y - bb.Min.Y, UnitTypeId.Meters);
                    double dz = UnitUtils.ConvertFromInternalUnits(bb.Max.Z - bb.Min.Z, UnitTypeId.Meters);

                    widthM = Math.Max(0.5, Math.Max(dx, dy));
                    double menorLado = Math.Max(0.5, Math.Min(dx, dy));
                    projM = areaM2 > 0 ? Math.Round(areaM2 / widthM, 2) : menorLado;
                    heightM = Math.Max(0.0, Math.Round(dz, 2));
                }

                if (areaM2 <= 0) areaM2 = widthM * projM;

                return new RevitRoofData(roof.Name, areaM2, Math.Round(widthM, 2), projM, heightM);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
            finally
            {
                ownerWindow.ShowDialog();
            }
        }

        public RevitPopulationEstimate EstimateBuildingPopulation()
        {
            var rooms = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            if (rooms.Count == 0)
            {
                return new RevitPopulationEstimate(
                    5, 0, 0,
                    "Nenhum ambiente (Room) inserido no modelo Revit. Mantida estimativa padrão de 5 pessoas.");
            }

            int dormitorios = 0;
            foreach (Room room in rooms)
            {
                string nomeNorm = RemoverAcentos(room.Name ?? "").ToLowerInvariant();
                if (nomeNorm.Contains("quarto") ||
                    nomeNorm.Contains("dormitorio") ||
                    nomeNorm.Contains("suite") ||
                    nomeNorm.Contains("bedroom"))
                {
                    dormitorios++;
                }
            }

            int pessoas = dormitorios > 0 ? dormitorios * 2 : Math.Max(2, rooms.Count);
            string resumo = dormitorios > 0
                ? $"Encontrados {dormitorios} dormitório(s)/suíte(s) em {rooms.Count} ambiente(s) do projeto.\nPopulação estimada (NBR 5626 — 2 pessoas/dormitório): {pessoas} pessoas."
                : $"Encontrados {rooms.Count} ambiente(s) no modelo (sem dormitórios nomeados).\nPopulação estimada: {pessoas} pessoas.";

            return new RevitPopulationEstimate(pessoas, dormitorios, rooms.Count, resumo);
        }

        private static string MapearAparelhoEsgoto(string nomeBruto)
        {
            string n = RemoverAcentos(nomeBruto).ToLowerInvariant();

            if (n.Contains("bacia") || n.Contains("vaso") || n.Contains("sanitaria") || n.Contains("toilet") || n.Contains("closet") || n.Contains("wc"))
                return "Bacia Sanitária (Sanita)";
            if (n.Contains("lavatorio") || n.Contains("cuba") || n.Contains("lavabo") || n.Contains("vanity"))
                return "Lavatório (Pia de WC)";
            if (n.Contains("cozinha") || n.Contains("kitchen") || n.Contains("gourmet") || n.Contains("pia"))
                return "Pia de Cozinha";
            if (n.Contains("chuveiro") || n.Contains("ducha") || n.Contains("shower"))
                return "Chuveiro";
            if (n.Contains("banheira") || n.Contains("tub") || n.Contains("hidro"))
                return "Banheira";
            if (n.Contains("maquina") || n.Contains("lavadora") || n.Contains("washing") || n.Contains("laundry"))
                return "Máquina de Lavar Roupa";
            if (n.Contains("tanque") || n.Contains("utility"))
                return "Tanque de Lavar Roupa";
            if (n.Contains("ralo") || n.Contains("sifonad") || n.Contains("drain"))
                return "Ralo Sifonado";
            if (n.Contains("mictorio") || n.Contains("urinal"))
                return "Mictório (Válvula)";

            return "";
        }

        private static string RemoverAcentos(string texto)
        {
            var normalized = texto.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private sealed class CategorySelectionFilter : ISelectionFilter
        {
            private readonly BuiltInCategory _category;
            public CategorySelectionFilter(BuiltInCategory category) => _category = category;
            public bool AllowElement(Element elem) => elem?.Category != null && elem.Category.Id.Value == (long)_category;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
