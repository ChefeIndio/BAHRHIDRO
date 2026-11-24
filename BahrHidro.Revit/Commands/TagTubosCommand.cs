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

            if (activeView is View3D && !((View3D)activeView).IsLocked)
            {
                TaskDialog.Show("Bahr Hidro", "Para aplicar identificadores numa vista 3D, deve primeiro trancar a vista.");
                return Result.Cancelled;
            }

            FilteredElementCollector collector = new FilteredElementCollector(doc, activeView.Id);
            IList<Element> pipes = collector.OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType().ToElements();

            if (pipes.Count == 0) return Result.Succeeded;

            FamilySymbol? pipeTagSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_PipeTags)
                .FirstOrDefault() as FamilySymbol;

            if (pipeTagSymbol == null)
            {
                TaskDialog.Show("Aviso", "Nenhuma família de identificador de tubagem encontrada.");
                return Result.Failed;
            }

            using (Transaction t = new Transaction(doc, "Bahr Hidro: Identificar Tubos"))
            {
                t.Start();
                if (!pipeTagSymbol.IsActive) { pipeTagSymbol.Activate(); doc.Regenerate(); }

                int tagCount = 0;
                
                foreach (Element pipe in pipes)
                {
                    if (!pipe.IsValidObject) continue;
                    LocationCurve? locCurve = pipe.Location as LocationCurve;
                    if (locCurve?.Curve != null)
                    {
                        Curve curve = locCurve.Curve;
                        XYZ midPoint = curve.Evaluate(0.5, true);

                        try
                        {
                            Reference pipeRef = new Reference(pipe);
                            IndependentTag newTag = IndependentTag.Create(
                                doc, pipeTagSymbol.Id, activeView.Id, pipeRef,
                                false, TagOrientation.Horizontal, midPoint);
                            
                            XYZ direction = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
                            XYZ perp = direction.CrossProduct(XYZ.BasisZ).Normalize();
                            if (perp.IsAlmostEqualTo(XYZ.Zero)) perp = XYZ.BasisX;
                            if (tagCount % 2 != 0) perp = -perp;
                            
                            double offsetDist = 1.0; 
                            XYZ headPos = midPoint + (perp * offsetDist);
                            
                            newTag.HasLeader = true;
                            newTag.LeaderEndCondition = LeaderEndCondition.Free;
                            
                            // Correção: Passando a referência do tubo e o ponto exato
                            newTag.SetLeaderEnd(pipeRef, midPoint); 
                            newTag.TagHeadPosition = headPos;
                            
                            tagCount++;
                        }
                        catch { continue; }
                    }
                }
                t.Commit();
                TaskDialog.Show("Bahr Hidro", $"{tagCount} identificadores inseridos com afastamento automático.");
            }
            return Result.Succeeded;
        }
    }
}
