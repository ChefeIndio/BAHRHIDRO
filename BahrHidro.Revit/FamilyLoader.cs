using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace BahrHidro.Revit
{
    public static class FamilyLoader
    {
        public static bool EnsureEssentialFittings(Document doc)
        {
            if (doc == null || doc.IsFamilyDocument) return false;

            var fittingSymbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            bool hasElbow = fittingSymbols.Any(s =>
            {
                var p = s.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                return p != null && p.AsInteger() == (int)PartType.Elbow;
            });

            bool hasTee = fittingSymbols.Any(s =>
            {
                var p = s.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                return p != null && p.AsInteger() == (int)PartType.Tee;
            });

            if (hasElbow && hasTee) return true;

            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BahrHidro",
                "Families");

            if (!Directory.Exists(appData)) return false;

            bool loadedAny = false;
            if (doc.IsModifiable)
            {
                foreach (string rfa in Directory.GetFiles(appData, "*.rfa"))
                {
                    try { if (doc.LoadFamily(rfa)) loadedAny = true; } catch { }
                }
            }
            else
            {
                using (Transaction t = new Transaction(doc, "Carregar Famílias Bahr Hidro"))
                {
                    t.Start();
                    foreach (string rfa in Directory.GetFiles(appData, "*.rfa"))
                    {
                        try { if (doc.LoadFamily(rfa)) loadedAny = true; } catch { }
                    }
                    t.Commit();
                }
            }

            return loadedAny;
        }
    }
}
