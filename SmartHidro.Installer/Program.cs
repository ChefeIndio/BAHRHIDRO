using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SmartHidro.Installer
{
    internal class Program
    {
        private const string GitHubRepo = "ChefeIndio/BAHRHIDRO";
        private const string RevitYear = "2026";

        static async Task Main(string[] args)
        {
            Console.Title = "Smart Hidro BIM - Instalador e Atualizador Automático";
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("============================================================");
            Console.WriteLine("   SMART HIDRO BIM - INSTALADOR & ATUALIZADOR AUTOMÁTICO");
            Console.WriteLine("============================================================");
            Console.ResetColor();
            Console.WriteLine();

            try
            {
                string exeDir = AppContext.BaseDirectory;
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string addinsDir = Path.Combine(appData, "Autodesk", "Revit", "Addins", RevitYear);
                string installDir = Path.Combine(addinsDir, "SmartHidro");
                string addinFile = Path.Combine(addinsDir, "SmartHidro.addin");
                string localBinDir = Path.Combine(exeDir, "bin");

                // 1. Fechar Revit se estiver aberto para não bloquear as DLLs
                var revitProcs = Process.GetProcessesByName("Revit");
                if (revitProcs.Length > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[!] O Autodesk Revit está aberto e bloqueia os arquivos do plugin.");
                    Console.Write("    Deseja fechar o Revit agora para instalar/atualizar? (S/N) [S]: ");
                    Console.ResetColor();
                    string? resp = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(resp) || resp.Trim().Equals("S", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var p in revitProcs)
                        {
                            try { p.Kill(); p.WaitForExit(5000); } catch { }
                        }
                        Console.WriteLine("    Revit encerrado.");
                    }
                    else
                    {
                        Console.WriteLine("    Operação cancelada.");
                        PauseAndExit(1);
                        return;
                    }
                }

                // 2. Baixar última versão do GitHub Releases ou usar a pasta bin/ do ZIP
                string tempExtractDir = Path.Combine(Path.GetTempPath(), "SmartHidro_Install_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempExtractDir);
                string? sourceDirToCopy = null;

                Console.WriteLine($"[1/4] Verificando última versão no GitHub ({GitHubRepo})...");
                string? downloadedZip = await TryDownloadLatestZipAsync(exeDir, installDir);

                if (!string.IsNullOrEmpty(downloadedZip) && File.Exists(downloadedZip))
                {
                    Console.WriteLine("      Extraindo pacote mais recente do GitHub...");
                    ZipFile.ExtractToDirectory(downloadedZip, tempExtractDir, true);
                    string nestedBin = Path.Combine(tempExtractDir, "bin");
                    sourceDirToCopy = Directory.Exists(nestedBin) ? nestedBin : tempExtractDir;
                }
                else if (Directory.Exists(localBinDir) && Directory.GetFiles(localBinDir, "BahrHidro.Revit.dll", SearchOption.AllDirectories).Any())
                {
                    Console.WriteLine("      Instalando arquivos empacotados neste ZIP...");
                    sourceDirToCopy = localBinDir;
                }
                else if (Directory.GetFiles(exeDir, "BahrHidro.Revit.dll", SearchOption.TopDirectoryOnly).Any())
                {
                    Console.WriteLine("      Instalando arquivos da pasta atual...");
                    sourceDirToCopy = exeDir;
                }
                else
                {
                    throw new Exception("Nenhuma release encontrada no GitHub e nenhuma DLL encontrada na pasta bin/.");
                }

                // 3. Limpar versões antigas e copiar novos arquivos
                Console.WriteLine($"[2/4] Instalando arquivos em: {installDir}");
                Directory.CreateDirectory(addinsDir);

                foreach (var oldAddin in Directory.GetFiles(addinsDir, "*.addin"))
                {
                    string name = Path.GetFileName(oldAddin);
                    if (name.Contains("BahrHidro", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("SmartHidro", StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(oldAddin); } catch { }
                    }
                }

                string legacyFolder = Path.Combine(addinsDir, "BahrHidro");
                if (Directory.Exists(legacyFolder))
                {
                    try { Directory.Delete(legacyFolder, true); } catch { }
                }

                if (Directory.Exists(installDir) &&
                    !Path.GetFullPath(sourceDirToCopy).TrimEnd('\\').Equals(Path.GetFullPath(installDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(installDir, true);
                }
                Directory.CreateDirectory(installDir);

                CopyDirectory(sourceDirToCopy, installDir);

                string? currentExe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
                {
                    string destExe = Path.Combine(installDir, "AtualizarSmartHidro.exe");
                    if (!Path.GetFullPath(currentExe).Equals(Path.GetFullPath(destExe), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(currentExe, destExe, true);
                    }
                }

                // 4. Gerar manifesto SmartHidro.addin
                Console.WriteLine($"[3/4] Criando manifesto SmartHidro.addin para Revit {RevitYear}...");
                string? mainDll = Directory.GetFiles(installDir, "BahrHidro.Revit.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (string.IsNullOrEmpty(mainDll))
                    throw new Exception("DLL principal 'BahrHidro.Revit.dll' não encontrada.");

                string escapedPath = SecurityElement.Escape(mainDll)!;
                string addinXml = $@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""no""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Name>Smart Hidro</Name>
    <Assembly>{escapedPath}</Assembly>
    <AddInId>8B5A1C3D-4E2F-49A1-9B7C-1D2E3F4A5B6C</AddInId>
    <FullClassName>BahrHidro.Revit.BahrHidroApp</FullClassName>
    <VendorId>BAHR</VendorId>
    <VendorDescription>Smart Hidro - Engenharia Hidraulica e BIM</VendorDescription>
  </AddIn>
</RevitAddIns>";

                File.WriteAllText(addinFile, addinXml, new UTF8Encoding(false));

                try { Directory.Delete(tempExtractDir, true); } catch { }

                Console.WriteLine("[4/4] Concluído!");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✅ SMART HIDRO INSTALADO E ATUALIZADO COM SUCESSO!");
                Console.ResetColor();
                Console.WriteLine($"   • Pasta     : {installDir}");
                Console.WriteLine($"   • Manifesto : {addinFile}");
                Console.WriteLine("\nAbra o Revit 2026 e clique na aba 'Smart Hidro'.");
                PauseAndExit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"❌ ERRO NA INSTALAÇÃO: {ex.Message}");
                Console.ResetColor();
                PauseAndExit(1);
            }
        }

        private static async Task<string?> TryDownloadLatestZipAsync(string exeDir, string installDir)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SmartHidroUpdater", "2.0"));

                string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                foreach (var dir in new[] { exeDir, installDir })
                {
                    string tokenFile = Path.Combine(dir, "github_token.txt");
                    if (string.IsNullOrWhiteSpace(token) && File.Exists(tokenFile))
                        token = File.ReadAllText(tokenFile).Trim();
                }
                if (!string.IsNullOrWhiteSpace(token))
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var resp = await http.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"      Aviso: Release online indisponível (HTTP {(int)resp.StatusCode}). Usando arquivos locais.");
                    return null;
                }

                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                string tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";

                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            string downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            string tempZip = Path.Combine(Path.GetTempPath(), $"SmartHidro_{tag}_{name}");
                            Console.WriteLine($"      Baixando {name} ({tag})...");
                            byte[] data = await http.GetByteArrayAsync(downloadUrl);
                            await File.WriteAllBytesAsync(tempZip, data);
                            return tempZip;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      Aviso ao acessar GitHub: {ex.Message}");
            }
            return null;
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
            }
            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
            }
        }

        private static void PauseAndExit(int code)
        {
            Console.WriteLine("\nPressione qualquer tecla para fechar...");
            try { Console.ReadKey(true); } catch { }
            Environment.Exit(code);
        }
    }
}
