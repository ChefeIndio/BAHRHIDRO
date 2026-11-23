using System.Threading.Tasks;
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
using BahrHidro.Core.Reporting;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public class RevitBimBridge : IRevitBimBridge
    {
        
        
        private RevitNetworkExtractionResult? _ultimaExtracao;

        
        private readonly RevitExternalEventRunner _runner;
        private UIDocument _uidoc = null!;
        private Document _doc = null!;

        public RevitBimBridge(UIApplication uiApp, RevitExternalEventRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            if (uiApp.ActiveUIDocument != null)
            {
                _uidoc = uiApp.ActiveUIDocument;
                _doc = _uidoc.Document;
            }
        }

        public Task<(string ProjectName, string ClientName, string Address, string Author)> GetProjectInfo()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
                return GetProjectInfoCore();
            });
        }

        private (string ProjectName, string ClientName, string Address, string Author) GetProjectInfoCore()
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

        public Task SaveProjectInfo(string projectName, string clientName, string address, string author)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
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
        
                return true;
            });
        }

        public Task<string> GetModelOverviewSummary()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            try
            {
                int tubos = new FilteredElementCollector(_doc).OfClass(typeof(Pipe)).WhereElementIsNotElementType().GetElementCount();
                int aparelhos = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_PlumbingFixtures).WhereElementIsNotElementType().GetElementCount();
                int telhados = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Roofs).WhereElementIsNotElementType().GetElementCount();
                int ambientes = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().GetElementCount();
                return $"Raio-X do Modelo 3D: {tubos} tubo(s) | {aparelhos} aparelho(s) sanitário(s) | {telhados} telhado(s) | {ambientes} ambiente(s).";
            }
            catch
            {
                return "Modelo Revit pronto para detecção automática.";
            }
        
            });
        }

        public Task<RevitReservoirScanResult> ScanReservoirsAndStreetFeed()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
                return RevitNetworkExtractor.ScanReservoirsInDocument(_doc);
            });
        }

        public Task<RevitNetworkImportData> ImportNetworkAutomaticallyFromRevit(int subNetworkIndex = -1, double streetPressureMca = 15.0)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
                _ultimaExtracao = RevitNetworkExtractor.ExtractAutomaticFromDocument(_doc, subNetworkIndex, streetPressureMca);
                return new RevitNetworkImportData(
                    _ultimaExtracao.Network,
                    _ultimaExtracao.OriginalDiametersMmBySegmentId,
                    _ultimaExtracao.DetectionSummary,
                    _ultimaExtracao.SubNetworkOptions,
                    _ultimaExtracao.SelectedSubNetworkIndex,
                    _ultimaExtracao.IsStreetFeedNetwork,
                    _ultimaExtracao.SuggestedInletPressureMca);
            });
        }

        public Task ImportNetworkFromRevit(Window ownerWindow, Action<RevitNetworkImportData> onNetworkImported)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            try
            {
                ownerWindow.Hide();

                Reference pickedRef = _uidoc.Selection.PickObject(
                    ObjectType.Element,
                    "Selecione o Reservatório ou o tubo inicial de saída:");

                Element startElement = _doc.GetElement(pickedRef);
                _ultimaExtracao = RevitNetworkExtractor.ExtractFromStartElement(_doc, startElement);

                onNetworkImported(new RevitNetworkImportData(
                    _ultimaExtracao.Network,
                    _ultimaExtracao.OriginalDiametersMmBySegmentId,
                    _ultimaExtracao.DetectionSummary));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
            }
            finally
            {
                ownerWindow.ShowDialog();
            }
        
                return true;
            });
        }

        public Task<int> ApplyDiametersToRevit(NetworkCalculationResult result)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
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
        
            });
        }

        public Task<Dictionary<string, int>> ImportAllSewerFixturesFromRevit()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            var contagem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var fixtures = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (Element elem in fixtures)
            {
                string familia = (elem as FamilyInstance)?.Symbol?.FamilyName ?? "";
                string tipo = elem.Name ?? "";
                string categoria = MapearAparelhoEsgoto($"{familia} {tipo}");

                if (!string.IsNullOrEmpty(categoria))
                {
                    contagem[categoria] = contagem.TryGetValue(categoria, out int atual) ? atual + 1 : 1;
                }
            }

            return contagem;
        
            });
        }

        public Task<Dictionary<string, int>> ImportSewerFixturesFromRevit(Window ownerWindow)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
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
        
            });
        }

        public Task<RevitRoofData?> CaptureAllRoofsFromRevit()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            var roofs = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Roofs)
                .WhereElementIsNotElementType()
                .ToElements();

            if (roofs.Count == 0) return null;

            double areaTotalM2 = 0;
            double maiorLarguraM = 0;
            double maiorAlturaM = 0;

            foreach (Element roof in roofs)
            {
                Parameter? areaParam = roof.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (areaParam != null && areaParam.HasValue)
                    areaTotalM2 += UnitUtils.ConvertFromInternalUnits(areaParam.AsDouble(), UnitTypeId.SquareMeters);

                BoundingBoxXYZ? bb = roof.get_BoundingBox(null);
                if (bb != null)
                {
                    double dx = UnitUtils.ConvertFromInternalUnits(bb.Max.X - bb.Min.X, UnitTypeId.Meters);
                    double dy = UnitUtils.ConvertFromInternalUnits(bb.Max.Y - bb.Min.Y, UnitTypeId.Meters);
                    double dz = UnitUtils.ConvertFromInternalUnits(bb.Max.Z - bb.Min.Z, UnitTypeId.Meters);
                    maiorLarguraM = Math.Max(maiorLarguraM, Math.Max(dx, dy));
                    maiorAlturaM = Math.Max(maiorAlturaM, dz);
                }
            }

            if (maiorLarguraM <= 0) maiorLarguraM = 10.0;
            double projM = areaTotalM2 > 0 ? Math.Round(areaTotalM2 / maiorLarguraM, 2) : 6.0;
            if (areaTotalM2 <= 0) areaTotalM2 = maiorLarguraM * projM;

            return new RevitRoofData(
                $"{roofs.Count} Cobertura(s) do Projeto",
                areaTotalM2,
                Math.Round(maiorLarguraM, 2),
                projM,
                Math.Round(maiorAlturaM, 2));
        
            });
        }

        public Task<RevitRoofData?> CaptureRoofFromRevit(Window ownerWindow)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
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
        
            });
        }

        public Task<RevitTemplateRoutingOptions> GetRoutingTemplateOptions()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitAutoRouter.GetTemplateOptions(_doc);
        
            });
        }

        public Task<List<DetectedRoutingPoint>> DetectRoutingPointsFromModel(long? levelId)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitAutoRouter.DetectAllPoints(_doc, levelId);
        
            });
        }

        public Task<DetectedRoutingPoint?> PickManualRoutingPoint(Window ownerWindow, string fixtureCategory, long? levelId)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitAutoRouter.PickManualPoint(_uidoc, ownerWindow, fixtureCategory, levelId);
        
            });
        }

        public Task<(int TaggedCount, string Message)> TagPipesInActiveView()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitDocumentationGenerator.TagPipesInActiveView(_uidoc);
        
            });
        }

        public Task<ClashCheckSummaryResult> RunHydraulicClashCheck()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitClashAndSheetGenerator.RunHydraulicClashCheck(_doc);
        
            });
        }

        public Task<(int ViewsCreated, int TotalTagsPlaced, string Summary)> GenerateHydraulicWallElevations()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitDocumentationGenerator.GenerateHydraulicWallElevations(_uidoc);
        
            });
        }

        public Task<VisualAuditSummaryResult> ApplyHydraulicVisualAudit(NetworkCalculationResult result)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitVisualAuditManager.ApplyOverrides(_uidoc, result, new Dictionary<Guid, ElementId>());
        
            });
        }

        public Task<LsfPunchingAuditResult> AuditLsfStudPunching()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitLsfFabricationManager.RunLsfPunchingAudit(_doc);
        
            });
        }

        public Task<NetworkCleanupResult> CleanGeneratedNetwork()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
                return RevitCleanupManager.CleanGeneratedNetwork(_uidoc);
            });
        }

        public Task<SpatialCacheStatsResult> RebuildSpatialCache()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
                return RevitSpatialCache.RebuildAndGetStats(_doc);
            });
        }

        public Task<MultilevelRiserResult> GenerateMultilevelRisers()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitMultilevelRiserManager.GenerateMultilevelRisers(_doc);
        
            });
        }

        public Task<(int ViewsAnnotated, int CalloutsPlaced, string Summary)> ApplyFabricationCoordinatesToElevations()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitLsfFabricationManager.ApplyFabricationCoordinatesToElevations(_uidoc);
        
            });
        }

        public Task<(int ClearedCount, string Summary)> ClearHydraulicVisualAudit()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitVisualAuditManager.ClearOverrides(_uidoc);
        
            });
        }

        public Task<(int DeletedCount, string Summary)> ClearGeneratedRouting(string disciplineMode)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitDocumentationGenerator.ClearGeneratedRouting(_doc, disciplineMode);
        
            });
        }

        public Task<SheetGenerationResult> GenerateHydraulicSheets()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            var info = GetProjectInfoCore();
            return RevitClashAndSheetGenerator.GenerateHydraulicSheets(_uidoc, info.ProjectName);
        
            });
        }

        public Task<(int ViewsCreated, int TotalTagsPlaced, string Summary)> GenerateWetZoneIsometricViews()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitDocumentationGenerator.GenerateWetZoneIsometricViews(_uidoc);
        
            });
        }

        public Task<BimMaterialTakeoff> ExtractBomFromRevit(string projectName, double wasteMarginPercent)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitBomExtractor.ExtractFromDocument(_doc, projectName, wasteMarginPercent);
        
            });
        }

        public Task<RevitAutoRoutingResult> ExecuteAutomaticPipeRouting(RevitAutoRoutingRequest request)
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
            return RevitAutoRouter.ExecuteRouting(_doc, request);
        
            });
        }

        public Task<RevitPopulationEstimate> EstimateBuildingPopulation()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
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
        
            });
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
    
        internal RevitBimBridge(UIApplication uiApp)
        {
            _runner = null!;
            if (uiApp.ActiveUIDocument != null)
            {
                _uidoc = uiApp.ActiveUIDocument;
                _doc = _uidoc.Document;
            }
        }

        internal VisualAuditSummaryResult ExecuteVisualAuditSync()
        {
            if (_doc == null)
                throw new InvalidOperationException("Abra um projeto no Revit antes de executar a auditoria.");
            NetworkCalculationResult result = default!;
            return RevitVisualAuditManager.ApplyOverrides(_uidoc, result, new Dictionary<Guid, ElementId>());
        }

        internal SheetGenerationResult ExecuteSheetGenerationSync()
        {
            if (_uidoc == null || _doc == null)
                throw new InvalidOperationException("Abra um projeto no Revit antes de gerar as pranchas.");

            var info = GetProjectInfoCore();
            return RevitClashAndSheetGenerator.GenerateHydraulicSheets(_uidoc, info.ProjectName);
        }


        public Task<string> RunOrderedFullAutomationAsync()
        {
            return _runner.RunAsync(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Abra um projeto no Revit.");
                var pipes = SmartHydraulicRoutingEngine.GenerateOrthogonalLsfNetwork(doc);
                ActivateViewSafe(uiApp.ActiveUIDocument, LsfPanelElevationGenerator.View3DCombinedName);
                return $"Sequência completa (1º Água Fria → 2º Água Quente → 3º Esgoto) concluída! {pipes.Count} trechos gerados + 4 Vistas 3D ao Vivo + Seção Geral.";
            });
        }

        public Task<string> RunColdWaterOnlyAsync()
        {
            return _runner.RunAsync(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Abra um projeto no Revit.");
                var pipes = SmartHydraulicRoutingEngine.GenerateColdWaterOnly(doc);
                ActivateViewSafe(uiApp.ActiveUIDocument, LsfPanelElevationGenerator.View3DColdWaterName);
                return $"1º Água Fria (NBR 5626) concluída: {pipes.Count} trechos gerados. Vista '{LsfPanelElevationGenerator.View3DColdWaterName}' ativada.";
            });
        }

        public Task<string> RunHotWaterOnlyAsync()
        {
            return _runner.RunAsync(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Abra um projeto no Revit.");
                var pipes = SmartHydraulicRoutingEngine.GenerateHotWaterOnly(doc);
                ActivateViewSafe(uiApp.ActiveUIDocument, LsfPanelElevationGenerator.View3DHotWaterName);
                return $"2º Água Quente Isolada (NBR 7198) concluída: {pipes.Count} trechos gerados. Vista '{LsfPanelElevationGenerator.View3DHotWaterName}' ativada.";
            });
        }

        public Task<string> RunSanitaryOnlyAsync()
        {
            return _runner.RunAsync(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Abra um projeto no Revit.");
                var pipes = SanitarySewerRoutingEngine.GenerateSanitaryNetworkOnly(doc, out var rep);
                ActivateViewSafe(uiApp.ActiveUIDocument, LsfPanelElevationGenerator.View3DSanitaryName);
                return $"3º Esgoto & Ventilação (NBR 8160) concluído: {pipes.Count} trechos ({rep.TotalUhc} UHC, Subcoletor DN{(int)rep.MaxCollectorDnMm}). Vista '{LsfPanelElevationGenerator.View3DSanitaryName}' ativada.";
            });
        }

        public Task<string> OpenLiveViewByNameAsync(string viewName)
        {
            return _runner.RunAsync(uiApp =>
            {
                var uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Abra um projeto no Revit.");
                bool ok = ActivateViewSafe(uidoc, viewName);
                return ok
                    ? $"Vista '{viewName}' aberta ao vivo no Revit."
                    : $"Vista '{viewName}' ainda não foi gerada. Execute o traçado 3D primeiro.";
            });
        }

        private static bool ActivateViewSafe(UIDocument? uidoc, string viewName)
        {
            if (uidoc?.Document == null) return false;
            var target = new FilteredElementCollector(uidoc.Document)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name.Equals(viewName, StringComparison.OrdinalIgnoreCase));
            if (target == null) return false;
            uidoc.ActiveView = target;
            return true;
        }

        public Task<Sewer3DCalculationResult> CalculateAndApplySewer3D()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
                var (res, _) = RevitSewer3DProcessor.CalculateAndApplySewer3D(_doc);
                return res;
            });
        }

        public Task<VerticalRiserSchematicResult> GenerateVerticalRiserSchematic()
        {
            return _runner.RunAsync(uiApp =>
            {
                _uidoc = uiApp.ActiveUIDocument ?? throw new InvalidOperationException("Nenhum projeto aberto no Revit.");
                _doc = _uidoc.Document;
                var (res, viewId) = RevitSewer3DProcessor.GenerateVerticalRiserSchematic(_doc);
                if (_doc.GetElement(viewId) is View schematicView)
                {
                    _uidoc.ActiveView = schematicView;
                }
                return res;
            });
        }
}
}
