using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BahrHidro.Revit.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class TagTubosCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            // Verificar se a vista permite a inserção de identificadores
            if (activeView is View3D && !((View3D)activeView).IsLocked)
            {
                TaskDialog.Show("Aviso Bahr Hidro", "Para aplicar identificadores (Tags) numa vista 3D, deve primeiro trancar a vista.");
                return Result.Cancelled;
            }

            // Selecionar todos os tubos visíveis na vista atual
            FilteredElementCollector collector = new FilteredElementCollector(doc, activeView.Id);
            IList<Element> pipes = collector.OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType().ToElements();

            if (pipes.Count == 0)
            {
                TaskDialog.Show("Bahr Hidro", "Nenhum tubo visível encontrado nesta vista.");
                return Result.Succeeded;
            }

            // Procurar a Família de Identificador de Tubulação
            FilteredElementCollector tagCollector = new FilteredElementCollector(doc);
            tagCollector.OfClass(typeof(FamilySymbol));
            tagCollector.OfCategory(BuiltInCategory.OST_PipeTags);
            
            FamilySymbol pipeTagSymbol = tagCollector.FirstOrDefault() as FamilySymbol;

            if (pipeTagSymbol == null)
            {
                TaskDialog.Show("Aviso", "Nenhuma família de identificador de tubulação (Pipe Tag) foi encontrada no projeto. Carregue uma família e tente novamente.");
                return Result.Failed;
            }

            using (Transaction t = new Transaction(doc, "Bahr Hidro: Identificar Tubos"))
            {
                t.Start();
                
                // Ativar o símbolo da família caso não esteja
                if (!pipeTagSymbol.IsActive)
                {
                    pipeTagSymbol.Activate();
                    doc.Regenerate();
                }

                int tagCount = 0;
                
                foreach (Element pipe in pipes)
                {
                    // Evitar etiquetar elementos que não permitem (ex: dentro de assemblies ou já ocultos por outros motivos)
                    if (!pipe.IsValidObject) continue;

                    // Encontrar a linha base do tubo para calcular o ponto médio
                    LocationCurve locCurve = pipe.Location as LocationCurve;
                    if (locCurve != null)
                    {
                        Curve curve = locCurve.Curve;
                        if (curve != null)
                        {
                            // Calcular o ponto intermédio (parâmetro 0.5)
                            XYZ midPoint = curve.Evaluate(0.5, true);

                            // Criar o identificador
                            try
                            {
                                IndependentTag newTag = IndependentTag.Create(
                                    doc,
                                    pipeTagSymbol.Id,
                                    activeView.Id,
                                    new Reference(pipe),
                                    false,
                                    TagOrientation.Horizontal,
                                    midPoint
                                );
                                
                                // Opcional: Aqui poderíamos incluir uma verificação de sobreposição
                                // e um deslocamento da linha de chamada (Leader)
                                
                                tagCount++;
                            }
                            catch (Exception)
                            {
                                // Ignorar tubos que não suportem identificadores (raro)
                                continue;
                            }
                        }
                    }
                }

                t.Commit();
                TaskDialog.Show("Bahr Hidro", $"{tagCount} identificadores de tubo inseridos com sucesso na vista atual.");
            }

            return Result.Succeeded;
        }
    }
}
