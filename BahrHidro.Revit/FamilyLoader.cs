using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace BahrHidro.Revit
{
    public static class FamilyLoader
    {
        private sealed class SilentFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        public static bool EnsureEssentialFittings(Document doc)
        {
            if (doc == null || doc.IsFamilyDocument) return false;

            CheckLoadedFittings(doc, out bool hasElbow, out bool hasTee, out bool hasTransition);
            if (hasElbow && hasTee && hasTransition)
                return true;

            var candidateDirs = GetLibrarySearchDirectories();
            if (candidateDirs.Count == 0)
                return hasElbow && hasTee;

            string? elbowRfa = hasElbow ? null : FindBestFittingRfa(candidateDirs, new[] { "elbow", "joelho", "curva", "bend" }, new[] { "sanit", "esgoto", "dwv", "p-trap", "sifao" });
            string? teeRfa = hasTee ? null : FindBestFittingRfa(candidateDirs, new[] { "tee", "te_", "te ", "tê", "juncao", "junção" }, new[] { "sanit", "esgoto", "dwv", "wye", "y-" });
            string? transRfa = hasTransition ? null : FindBestFittingRfa(candidateDirs, new[] { "transition", "reduc", "reduç", "coupling", "luva", "bucha" }, new[] { "sanit", "esgoto", "dwv" });

            var filesToLoad = new[] { elbowRfa, teeRfa, transRfa }
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (filesToLoad.Count == 0)
                return hasElbow && hasTee;

            bool loadedAny = false;
            void LoadAll()
            {
                var opts = new SilentFamilyLoadOptions();
                foreach (string rfa in filesToLoad!)
                {
                    try
                    {
                        if (doc.LoadFamily(rfa, opts, out Family fam) && fam != null)
                        {
                            loadedAny = true;
                            foreach (ElementId symId in fam.GetFamilySymbolIds())
                            {
                                if (doc.GetElement(symId) is FamilySymbol fs && !fs.IsActive)
                                    fs.Activate();
                            }
                        }
                    }
                    catch { }
                }
            }

            if (doc.IsModifiable)
            {
                LoadAll();
                doc.Regenerate();
            }
            else
            {
                using (Transaction t = new Transaction(doc, "Carregar Conexões Hidráulicas Bahr Hidro"))
                {
                    t.Start();
                    LoadAll();
                    t.Commit();
                }
            }

            CheckLoadedFittings(doc, out hasElbow, out hasTee, out _);
            return loadedAny || (hasElbow && hasTee);
        }

        private static void CheckLoadedFittings(Document doc, out bool hasElbow, out bool hasTee, out bool hasTransition)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            hasElbow = symbols.Any(s => HasPartType(s, PartType.Elbow) || NameMatches(s, new[] { "joelho", "elbow", "curva" }));
            hasTee = symbols.Any(s => HasPartType(s, PartType.Tee) || NameMatches(s, new[] { "te ", "tê", "tee", "junção", "juncao" }));
            hasTransition = symbols.Any(s => HasPartType(s, PartType.Transition) || NameMatches(s, new[] { "redu", "transi", "luva", "bucha" }));
        }

        private static bool HasPartType(FamilySymbol s, PartType target)
        {
            var p = s.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
            return p != null && p.AsInteger() == (int)target;
        }

        private static bool NameMatches(FamilySymbol s, string[] hints)
        {
            string full = $" {s.FamilyName} {s.Name} ".ToLowerInvariant();
            return hints.Any(h => full.Contains(h));
        }

        private static List<string> GetLibrarySearchDirectories()
        {
            var dirs = new List<string>();
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BahrHidro", "Families");
            if (Directory.Exists(appData))
                dirs.Add(appData);

            string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string autodeskRoot = Path.Combine(commonData, "Autodesk");
            if (Directory.Exists(autodeskRoot))
            {
                try
                {
                    foreach (var rvtDir in Directory.GetDirectories(autodeskRoot, "RVT*").OrderByDescending(d => d))
                    {
                        string libDir = Path.Combine(rvtDir, "Libraries");
                        if (Directory.Exists(libDir))
                            dirs.Add(libDir);
                    }
                }
                catch { }
            }

            return dirs;
        }

        private static string? FindBestFittingRfa(List<string> roots, string[] includeHints, string[] avoidHints)
        {
            string? fallback = null;
            foreach (var root in roots)
            {
                try
                {
                    foreach (var file in SafeEnumerateRfaFiles(root))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        string fullLower = file.ToLowerInvariant();

                        bool isPipeFolder = fullLower.Contains("pipe") || fullLower.Contains("tubul") ||
                                            fullLower.Contains("plumbing") || fullLower.Contains("hidraul") ||
                                            fullLower.Contains("fitting") || fullLower.Contains("conex");
                        if (!isPipeFolder && !root.Contains("BahrHidro"))
                            continue;

                        if (!includeHints.Any(h => fileName.Contains(h)))
                            continue;

                        if (avoidHints.Any(a => fileName.Contains(a)))
                        {
                            fallback ??= file;
                            continue;
                        }

                        if (fullLower.Contains("pvc") || fullLower.Contains("generic") || fullLower.Contains("sch 40") || fullLower.Contains("sold"))
                            return file;

                        fallback ??= file;
                    }
                }
                catch { }
            }
            return fallback;
        }

        private static IEnumerable<string> SafeEnumerateRfaFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string current = stack.Pop();
                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(current, "*.rfa"); } catch { }
                foreach (var f in files)
                    yield return f;

                string[] subDirs = Array.Empty<string>();
                try { subDirs = Directory.GetDirectories(current); } catch { }
                foreach (var d in subDirs)
                    stack.Push(d);
            }
        }
    }
}
