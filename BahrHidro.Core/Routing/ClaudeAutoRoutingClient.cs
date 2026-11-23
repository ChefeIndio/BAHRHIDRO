using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BahrHidro.Core.Routing
{
    public sealed record BahrHidroApiSettings(string AnthropicApiKey, string PreferredModel);

    public sealed record ClaudeRoutingGenerationResult(
        string FormattedPlanJson,
        AutoRoutingPlanJson Plan,
        string ModelUsed,
        int InputTokens,
        int OutputTokens);

    public static class HidroApiSettingsHelper { public static BahrHidroApiSettings LoadSafe() => BahrHidroApiSettingsStore.Load(); }

    public static class BahrHidroApiSettingsStore
    {
        private static string SettingsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BahrHidro", "settings.json");

        public static BahrHidroApiSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                    var loaded = JsonSerializer.Deserialize<BahrHidroApiSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (loaded != null)
                    {
                        string key = !string.IsNullOrWhiteSpace(loaded.AnthropicApiKey)
                            ? loaded.AnthropicApiKey
                            : (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "");
                        string model = string.IsNullOrWhiteSpace(loaded.PreferredModel) ? "claude-sonnet-4-20250514" : loaded.PreferredModel;
                        return new BahrHidroApiSettings(key, model);
                    }
                }
            }
            catch { }

            return new BahrHidroApiSettings(
                Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "",
                "claude-sonnet-4-20250514");
        }

        public static void Save(string apiKey, string model)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var data = new BahrHidroApiSettings(apiKey?.Trim() ?? "", string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-20250514" : model.Trim());
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
            }
            catch { }
        }
    }

    public static class ClaudeAutoRoutingClient
    {
        private static readonly HttpClient SharedHttp = new()
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        private const string SystemPrompt = """
Você é um engenheiro especialista em projetos hidrossanitários BIM no Autodesk Revit, seguindo rigorosamente as normas brasileiras ABNT NBR 5626 (Água Fria), ABNT NBR 7198 (Água Quente) e ABNT NBR 8160 (Esgoto Sanitário e Ventilação).

Sua tarefa é receber um JSON contendo as coordenadas reais (em metros) dos aparelhos sanitários, seus conectores e as prumadas de um ambiente molhado, e retornar EXCLUSIVAMENTE um objeto JSON válido no esquema exato abaixo, sem nenhum texto explicativo antes ou depois, e sem blocos markdown:

{
  "planName": "Tracado_ABNT_Claude_3D",
  "segments": [
    {
      "systemClassification": "Água Fria",
      "diameterMm": 25.0,
      "slopePercent": 0.0,
      "waypointsMeters": [
        { "xMeters": 0.0, "yMeters": 0.0, "zMeters": 0.0 },
        { "xMeters": 1.0, "yMeters": 0.0, "zMeters": 0.0 }
      ]
    }
  ]
}

Regras obrigatórias de engenharia e geometria:
1. Utilize APENAS traçados ortogonais (mudanças de direção em 90 graus alinhadas aos eixos X, Y ou Z) para garantir conexão física limpa com cotovelos no Revit.
2. Nunca gere segmentos com comprimento menor que 0,05 m (5 cm) entre waypoints consecutivos.
3. Para Água Fria: utilize "systemClassification": "Água Fria", "diameterMm": 25.0 (ou 32.0 para barrilete/alimentador principal), "slopePercent": 0.0, mantendo cota horizontal compatível com os pontos de consumo (ex.: Z = 0,60 m para lavatório/bacia e Z = 2,10 m para chuveiro).
4. Para Água Quente: utilize "systemClassification": "Água Quente", "diameterMm": 22.0 (ou 25.0), "slopePercent": 0.0, conectando apenas pontos que possuam conector de água quente (chuveiro, lavatório, pia).
5. Para Esgoto Sanitário: utilize "systemClassification": "Esgoto Sanitário", "diameterMm": 100.0 para bacia sanitária (slopePercent: 1.0) e "diameterMm": 50.0 ou 40.0 para lavatório, chuveiro e pia (slopePercent: 2.0), direcionando com declividade descendente até a prumada de esgoto.
6. Para Ventilação: utilize "systemClassification": "Ventilação", "diameterMm": 50.0, "slopePercent": 0.0, subindo verticalmente acima do nível de transbordamento dos aparelhos até a coluna de ventilação.
7. Se não houver prumada explícita no JSON de entrada, crie um ponto de prumada coerente próximo à parede hidráulica dos aparelhos.
""";

        public static async Task<ClaudeRoutingGenerationResult> GenerateRoutingPlanAsync(
            string apiKey,
            string model,
            string spatialContextJson,
            string additionalInstructions = "",
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Informe sua API Key da Anthropic (sk-ant-...) antes de gerar o traçado.");

            if (string.IsNullOrWhiteSpace(spatialContextJson))
                throw new InvalidOperationException("O contexto espacial 3D está vazio. Extraia primeiro o contexto do Revit.");

            string targetModel = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-4-20250514" : model.Trim();

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine("Gere o plano de traçado hidrossanitário 3D em JSON para o seguinte contexto espacial extraído do Revit 2026:");
            if (!string.IsNullOrWhiteSpace(additionalInstructions))
            {
                userPrompt.AppendLine();
                userPrompt.AppendLine($"Diretrizes adicionais do projetista: {additionalInstructions.Trim()}");
            }
            userPrompt.AppendLine();
            userPrompt.AppendLine(spatialContextJson);

            var requestPayload = new
            {
                model = targetModel,
                max_tokens = 4096,
                temperature = 0.1,
                system = SystemPrompt,
                messages = new[]
                {
                    new { role = "user", content = userPrompt.ToString() }
                }
            };

            string bodyJson = JsonSerializer.Serialize(requestPayload);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", apiKey.Trim());
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var resp = await SharedHttp.SendAsync(req, cancellationToken);
            string respText = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Falha na API do Claude ({(int)resp.StatusCode} {resp.ReasonPhrase}):\n{respText}");

            using var doc = JsonDocument.Parse(respText);
            var root = doc.RootElement;

            string assistantText = "";
            if (root.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentArr.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var tProp) && tProp.GetString() == "text" &&
                        block.TryGetProperty("text", out var txtProp))
                    {
                        assistantText += txtProp.GetString();
                    }
                }
            }

            int inTokens = 0, outTokens = 0;
            if (root.TryGetProperty("usage", out var usageProp))
            {
                if (usageProp.TryGetProperty("input_tokens", out var it)) inTokens = it.GetInt32();
                if (usageProp.TryGetProperty("output_tokens", out var ot)) outTokens = ot.GetInt32();
            }

            string cleanJson = ExtractPureJsonObject(assistantText);
            var parseOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var plan = JsonSerializer.Deserialize<AutoRoutingPlanJson>(cleanJson, parseOpts)
                       ?? throw new InvalidOperationException("Não foi possível interpretar o plano de traçado JSON retornado pelo Claude.");

            if (plan.Segments == null || plan.Segments.Count == 0)
                throw new InvalidOperationException("O Claude retornou um plano sem segmentos de tubulação válidos.");

            string formattedPlan = JsonSerializer.Serialize(plan, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return new ClaudeRoutingGenerationResult(formattedPlan, plan, targetModel, inTokens, outTokens);
        }

        private static string ExtractPureJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("Resposta textual vazia recebida da API do Claude.");

            string trimmed = raw.Trim();
            int firstBrace = trimmed.IndexOf('{');
            int lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace == -1 || lastBrace <= firstBrace)
                throw new InvalidOperationException($"A resposta do Claude não contém um objeto JSON válido:\n{trimmed}");

            return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
        }
    }
}
