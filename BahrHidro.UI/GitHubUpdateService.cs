using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BahrHidro.UI
{
    public sealed class LocalVersionState
    {
        public string InstalledSha { get; set; } = string.Empty;
        public string InstalledDate { get; set; } = string.Empty;
        public string RepoLocalPath { get; set; } = string.Empty;
    }

    public sealed record GitHubUpdateCheckResult(
        bool UpdateAvailable,
        string LatestShaShort,
        string LatestDateFormatted,
        string CommitMessage,
        string StatusSummary);

    public static class GitHubUpdateService
    {
        private const string GitHubCommitApiUrl = "https://api.github.com/repos/ChefeIndio/BAHRHIDRO/commits/main";
        private const string GitHubRepoWebUrl = "https://github.com/ChefeIndio/BAHRHIDRO";

        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BahrHidro-Revit2026");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static string VersionStatePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BahrHidro");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "version_state.json");
            }
        }

        public static LocalVersionState LoadLocalState()
        {
            try
            {
                if (File.Exists(VersionStatePath))
                {
                    string json = File.ReadAllText(VersionStatePath);
                    var state = JsonSerializer.Deserialize<LocalVersionState>(json);
                    if (state != null) return state;
                }
            }
            catch { }
            return new LocalVersionState();
        }

        public static async Task<GitHubUpdateCheckResult> CheckForUpdatesAsync()
        {
            var local = LoadLocalState();

            using var response = await Http.GetAsync(GitHubCommitApiUrl);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string fullSha = root.TryGetProperty("sha", out var shaProp) ? (shaProp.GetString() ?? "") : "";
            string shortSha = fullSha.Length >= 7 ? fullSha[..7] : fullSha;

            string message = "Atualização disponível";
            string dateFormatted = DateTime.Now.ToString("dd/MM/yyyy HH:mm");

            if (root.TryGetProperty("commit", out var commitObj))
            {
                if (commitObj.TryGetProperty("message", out var msgProp))
                {
                    string rawMsg = msgProp.GetString() ?? "";
                    message = rawMsg.Split('\n')[0].Trim();
                }
                if (commitObj.TryGetProperty("author", out var authorObj) &&
                    authorObj.TryGetProperty("date", out var dateProp) &&
                    DateTimeOffset.TryParse(dateProp.GetString(), out var dto))
                {
                    dateFormatted = dto.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                }
            }

            bool hasLocalSha = !string.IsNullOrWhiteSpace(local.InstalledSha);
            bool updateAvailable = !hasLocalSha || !fullSha.StartsWith(local.InstalledSha, StringComparison.OrdinalIgnoreCase);

            string summary = !hasLocalSha
                ? $"Última versão no GitHub: [{shortSha}] ({dateFormatted}) — \"{message}\". Clique em 'Atualizar Plugin no Windows' para sincronizar."
                : updateAvailable
                    ? $"Nova atualização encontrada! Instalada: [{local.InstalledSha}] → GitHub: [{shortSha}] ({dateFormatted}) — \"{message}\"."
                    : $"Plugin atualizado! Você já está na última versão [{shortSha}] ({dateFormatted}).";

            return new GitHubUpdateCheckResult(updateAvailable, shortSha, dateFormatted, message, summary);
        }

        public static string TriggerWindowsUpdater()
        {
            var local = LoadLocalState();
            string? batPath = FindUpdaterBat(local.RepoLocalPath);

            if (!string.IsNullOrWhiteSpace(batPath) && File.Exists(batPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = batPath,
                    WorkingDirectory = Path.GetDirectoryName(batPath)!,
                    UseShellExecute = true
                });
                return $"Instalador iniciado ({batPath}). Feche o Revit caso o terminal solicite para substituir as DLLs.";
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = GitHubRepoWebUrl,
                UseShellExecute = true
            });
            return "Repositório aberto no navegador. Baixe ou execute 'Atualizar-BahrHidro.bat' na pasta do projeto para instalar automaticamente.";
        }

        private static string? FindUpdaterBat(string savedRepoPath)
        {
            if (!string.IsNullOrWhiteSpace(savedRepoPath))
            {
                string candidate = Path.Combine(savedRepoPath, "Atualizar-BahrHidro.bat");
                if (File.Exists(candidate)) return candidate;
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] commonDirs =
            {
                Path.Combine(userProfile, "source", "repos", "BAHRHIDRO"),
                Path.Combine(userProfile, "Documents", "GitHub", "BAHRHIDRO"),
                Path.Combine(userProfile, "Desktop", "BAHRHIDRO"),
                @"C:\BAHRHIDRO",
                @"C:\Projetos\BAHRHIDRO"
            };

            foreach (string dir in commonDirs)
            {
                string candidate = Path.Combine(dir, "Atualizar-BahrHidro.bat");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }
    }
}
