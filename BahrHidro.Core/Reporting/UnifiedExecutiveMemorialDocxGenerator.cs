using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using BahrHidro.Core.Hydraulic.HotWater;
using BahrHidro.Core.Hydraulic.Pumping;
using BahrHidro.Core.Hydraulic.SewageTreatment;

namespace BahrHidro.Core.Reporting
{
    public sealed record UnifiedMemorialExportData(
        string ProjectName,
        string ClientOrAuthor,
        List<(string Param, string Value)> ColdWaterRows,
        List<string> ColdWaterAlerts,
        ReservoirPumpingResult? PumpingResult,
        HotWaterThermalResult? ThermalResult,
        List<(string Param, string Value)> Sewage3DRows,
        SewageTreatmentResult? SewageTreatmentResult,
        List<(string Param, string Value)> ClashAndSinapiRows);

    public static class UnifiedExecutiveMemorialDocxGenerator
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        public static void GenerateDocx(UnifiedMemorialExportData data, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Caminho de saída inválido.", nameof(outputPath));

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);

            WriteXmlEntry(zip, "[Content_Types].xml", BuildContentTypes());
            WriteXmlEntry(zip, "_rels/.rels", BuildRootRels());
            WriteXmlEntry(zip, "word/_rels/document.xml.rels", BuildDocumentRels());
            WriteXmlEntry(zip, "word/styles.xml", BuildStyles());
            WriteXmlEntry(zip, "word/document.xml", BuildDocument(data));
        }

        private static void WriteXmlEntry(ZipArchive zip, string entryPath, XDocument doc)
        {
            var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            doc.Save(writer);
        }

        private static XDocument BuildContentTypes()
        {
            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            return new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(ct + "Types",
                    new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                    new XElement(ct + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
                    new XElement(ct + "Override", new XAttribute("PartName", "/word/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"))
                ));
        }

        private static XDocument BuildRootRels()
        {
            XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
            return new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(rel + "Relationships",
                    new XElement(rel + "Relationship",
                        new XAttribute("Id", "rId1"),
                        new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                        new XAttribute("Target", "word/document.xml"))
                ));
        }

        private static XDocument BuildDocumentRels()
        {
            XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
            return new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(rel + "Relationships",
                    new XElement(rel + "Relationship",
                        new XAttribute("Id", "rId1"),
                        new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                        new XAttribute("Target", "styles.xml"))
                ));
        }

        private static XDocument BuildStyles()
        {
            return new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(W + "styles",
                    new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
                    new XElement(W + "docDefaults",
                        new XElement(W + "rPrDefault",
                            new XElement(W + "rPr",
                                new XElement(W + "rFonts",
                                    new XAttribute(W + "ascii", "Calibri"),
                                    new XAttribute(W + "hAnsi", "Calibri"),
                                    new XAttribute(W + "cs", "Calibri")),
                                new XElement(W + "sz", new XAttribute(W + "val", "21")),
                                new XElement(W + "color", new XAttribute(W + "val", "1E293B"))
                            )
                        )
                    )
                ));
        }

        private static XDocument BuildDocument(UnifiedMemorialExportData data)
        {
            var pt = new CultureInfo("pt-BR");
            string projectTitle = string.IsNullOrWhiteSpace(data.ProjectName) ? "Projeto Hidrossanitário BIM" : data.ProjectName.Trim();
            string issueDate = DateTime.Now.ToString("dd/MM/yyyy HH:mm", pt);

            var bodyElements = new List<XElement>();

            // Cabeçalho Institucional / Banner
            bodyElements.Add(BuildBannerBox(
                "MEMORIAL DESCRITIVO E DE CÁLCULO EXECUTIVO — INSTALAÇÕES HIDROSSANITÁRIAS",
                $"Empreendimento / Modelo BIM: {projectTitle}  |  Emissão: {issueDate}"));

            bodyElements.Add(BuildParagraph(
                "Este documento consolida o dimensionamento hidráulico, sanitário, térmico e de compatibilização BIM elaborado no Smart Hidro (Autodesk Revit 2026), em conformidade com as normas técnicas vigentes da Associação Brasileira de Normas Técnicas (ABNT):",
                spaceAfter: 100));

            bodyElements.Add(BuildKeyValueTable("PREMISSAS NORMATIVAS E IDENTIFICAÇÃO TÉCNICA", new List<(string, string)>
            {
                ("Empreendimento / Arquivo BIM", projectTitle),
                ("Responsável / Origem", string.IsNullOrWhiteSpace(data.ClientOrAuthor) ? "Engenharia Hidrossanitária — Smart Hidro BIM" : data.ClientOrAuthor),
                ("Data de Consolidação", issueDate),
                ("Normas Aplicáveis", "ABNT NBR 5626 (Água Fria), NBR 7198 (Água Quente), NBR 15569 (Solar), NBR 8160 (Esgoto), NBR 10844 (Pluvial), NBR 7229 e NBR 13969 (Tratamento Individual)")
            }));

            int chapter = 1;

            // Capítulo 1: Água Fria (NBR 5626)
            if (data.ColdWaterRows.Count > 0)
            {
                bodyElements.Add(BuildSectionHeading($"{chapter++}. SISTEMA PREDIAL DE ÁGUA FRIA (ABNT NBR 5626)"));
                bodyElements.Add(BuildParagraph(
                    "O dimensionamento da rede de distribuição de água fria considera o método dos pesos relativos (somatório de pesos das peças de utilização), verificação de velocidades máximas de escoamento (≤ 3,0 m/s) e controle das pressões estáticas máximas (≤ 40,0 m.c.a.) e dinâmicas mínimas (≥ 1,0 m.c.a.)."));
                bodyElements.Add(BuildKeyValueTable("Parâmetros e Resultados — Rede de Água Fria", data.ColdWaterRows));

                if (data.ColdWaterAlerts.Count > 0)
                    bodyElements.Add(BuildAlertCallout("Alertas Normativos — Água Fria (NBR 5626)", data.ColdWaterAlerts));
            }

            // Capítulo 2: Reservatórios, Recalque e Bombeamento (NBR 5626)
            if (data.PumpingResult != null)
            {
                var p = data.PumpingResult;
                bodyElements.Add(BuildSectionHeading($"{chapter++}. RESERVATÓRIOS, RECALQUE E BOMBEAMENTO (ABNT NBR 5626)"));
                bodyElements.Add(BuildParagraph(
                    "A reservação predial foi dimensionada para atender à autonomia de consumo potável somada à Reserva Técnica de Incêndio (RTI). As tubulações de sucção e recalque foram dimensionadas pela Fórmula de Bresse e equação de perda de carga de Hazen-Williams."));
                bodyElements.Add(BuildKeyValueTable("Dimensionamento de Reservatórios e Conjunto Motobomba", new List<(string, string)>
                {
                    ("Consumo Diário Predial", $"{p.DailyConsumptionLiters.ToString("N0", pt)} L/dia"),
                    ("Reserva Potável Total", $"{p.TotalPotableReserveLiters.ToString("N0", pt)} L"),
                    ("Reservatório Inferior (Calculado → Comercial)", $"{p.RequiredLowerLiters.ToString("N0", pt)} L  →  {p.CommercialLowerReservoirLiters.ToString("N0", pt)} L"),
                    ("Reservatório Superior c/ RTI (Calculado → Comercial)", $"{p.RequiredUpperWithFireLiters.ToString("N0", pt)} L  →  {p.CommercialUpperReservoirLiters.ToString("N0", pt)} L"),
                    ("Vazão de Recalque", $"{p.PumpingFlowLps.ToString("N3", pt)} L/s  ({p.PumpingFlowM3h.ToString("N2", pt)} m³/h)"),
                    ("Tubulação de Sucção / Recalque", $"Sucção DN {p.SuctionNominalDiameterMm} mm ({p.SuctionVelocityMs:F2} m/s)  |  Recalque DN {p.DischargeNominalDiameterMm} mm ({p.DischargeVelocityMs:F2} m/s)"),
                    ("Altura Manométrica Total (AMT)", $"{p.TotalDynamicHeadAmtMeters:F2} m.c.a. (H_geo = {p.GeometricHeadMeters:F2} m | ΔH_suc = {p.SuctionHeadLossMeters:F2} m | ΔH_rec = {p.DischargeHeadLossMeters:F2} m)"),
                    ("NPSH Disponível", $"{p.NpshAvailableMeters:F2} m.c.a."),
                    ("Conjunto Motobomba Especificado", p.SelectedPumpSpec)
                }));

                if (p.NormativeAlerts.Count > 0)
                    bodyElements.Add(BuildAlertCallout("Recomendações Executivas — Reservatórios e Recalque", p.NormativeAlerts));
            }

            // Capítulo 3: Aquecimento de Água — Boiler, Gás e Solar (NBR 7198 / NBR 15569)
            if (data.ThermalResult != null)
            {
                var t = data.ThermalResult;
                bodyElements.Add(BuildSectionHeading($"{chapter++}. SISTEMA DE AQUECIMENTO DE ÁGUA: BOILER, GÁS E SOLAR (NBR 7198 / NBR 15569)"));
                bodyElements.Add(BuildParagraph(
                    "O dimensionamento térmico contempla a demanda diária de água quente na temperatura de armazenamento, o volume comercial do reservatório térmico (boiler), a vazão corrigida para aquecedores de passagem a gás (ΔT = 20 °C), a área de coletores solares e a dilatação térmica linear das tubulações."));
                bodyElements.Add(BuildKeyValueTable("Dimensionamento Térmico — Boiler, Aquecedor a Gás e Solar", new List<(string, string)>
                {
                    ("Demanda Diária de Água Quente", $"{t.DailyConsumption60CLiters.ToString("N1", pt)} L/dia  |  Energia: {t.DailyThermalEnergyKwh.ToString("N2", pt)} kWh/dia"),
                    ("Reservatório Térmico (Boiler NBR 7198)", $"{t.RecommendedBoilerLiters} Litros  |  Apoio Elétrico: {t.ElectricBackupPowerKw:F2} kW"),
                    ("Aquecedor a Gás (Vazão Uso / ΔT 20°C)", $"Q_uso = {t.SimultaneousFlow40CLpm:F2} L/min  |  Q_20 = {t.EquivalentFlowDelta20Lpm:F2} L/min ({t.RequiredGasPowerKcalH:N0} kcal/h)"),
                    ("Equipamento a Gás e Chaminé", $"{t.RecommendedGasHeaterModel}  |  Chaminé Ø {t.FlueDuctDiameterMm} mm"),
                    ("Sistema Solar (ABNT NBR 15569)", $"Área Coletora: {t.RequiredCollectorAreaM2:F2} m² ({t.CollectorPlates2M2Count} placas de 2,0 m²)  |  Boiler Solar: {t.RecommendedSolarBoilerLiters} L"),
                    ("Dilatação Térmica Linear (PPR)", $"{t.LinearExpansionPprMm:F1} mm (prever pontos fixos, deslizantes e liras de dilatação conforme NBR 7198)")
                }));

                if (t.NormativeAlerts.Count > 0)
                    bodyElements.Add(BuildAlertCallout("Recomendações Executivas — Água Quente e Solar", t.NormativeAlerts));
            }

            // Capítulo 4: Esgoto Sanitário e Ventilação 3D (ABNT NBR 8160)
            if (data.Sewage3DRows.Count > 0)
            {
                bodyElements.Add(BuildSectionHeading($"{chapter++}. SISTEMA PREDIAL DE ESGOTO SANITÁRIO E VENTILAÇÃO (ABNT NBR 8160)"));
                bodyElements.Add(BuildParagraph(
                    "As tubulações de esgoto primário, secundário e ventilação foram dimensionadas pelo método das Unidades Hunter de Contribuição (UHC), aplicando declividades mínimas normativas (2,0% até DN 75 mm e 1,0% a partir de DN 100 mm) e verificação das distâncias máximas dos desconectores aos ramais de ventilação."));
                bodyElements.Add(BuildKeyValueTable("Parâmetros e Resultados — Rede de Esgoto 3D", data.Sewage3DRows));
            }

            // Capítulo 5: Caixa de Gordura, Tanque Séptico, Filtro Anaeróbio e Sumidouro (NBR 8160 / 7229 / 13969)
            if (data.SewageTreatmentResult != null)
            {
                var s = data.SewageTreatmentResult;
                bodyElements.Add(BuildSectionHeading($"{chapter++}. CAIXA DE GORDURA E TRATAMENTO INDIVIDUAL DE ESGOTO (NBR 8160 / 7229 / 13969)"));
                bodyElements.Add(BuildParagraph(
                    "As unidades de retenção de gordura e tratamento biológico anaeróbio foram dimensionadas conforme a contribuição diária de despejos, período de detenção hidráulica, taxa de acumulação de lodo digerido e coeficiente de infiltração do solo local."));
                bodyElements.Add(BuildKeyValueTable("Dimensionamento — Caixa de Gordura, Fossa, Filtro e Sumidouro", new List<(string, string)>
                {
                    ("Caixa de Gordura (NBR 8160)", $"{s.GreaseTrapModelCode} — {s.GreaseTrapDescription} | Saída DN {s.GreaseTrapOutletDnMm} mm"),
                    ("Contribuição Diária de Esgoto", $"{s.DailySewageFlowLiters.ToString("N0", pt)} L/dia  |  Detenção T = {s.DetentionTimeDays:F2} dia  |  Fator Lodo K = {s.SludgeAccumulationFactorK} dias"),
                    ("Tanque Séptico (NBR 7229)", $"Volume Útil = {s.SepticTankUsefulVolumeLiters.ToString("N0", pt)} L ({s.SepticTankUsefulVolumeLiters / 1000.0:F2} m³)  |  Ø {s.SepticTankDiameterMeters:F2} m × H_útil {s.SepticTankUsefulDepthMeters:F2} m"),
                    ("Filtro Anaeróbio (NBR 13969)", $"Volume Útil = {s.AnaerobicFilterUsefulVolumeLiters.ToString("N0", pt)} L ({s.AnaerobicFilterUsefulVolumeLiters / 1000.0:F2} m³)  |  Ø {s.AnaerobicFilterDiameterMeters:F2} m × H_total {s.AnaerobicFilterTotalHeightMeters:F2} m"),
                    ("Sumidouro (NBR 13969)", $"Área Infiltração = {s.RequiredInfiltrationAreaM2:F2} m² (Ci = {s.SoilInfiltrationCoeffLpm2Day:F0} L/m².d)  |  {s.SumidouroUnitsCount} un. × Ø {s.SumidouroDiameterMeters:F2} m × H_útil {s.SumidouroUsefulDepthMeters:F2} m")
                }));

                if (s.NormativeAlerts.Count > 0)
                    bodyElements.Add(BuildAlertCallout("Recomendações Executivas — Tratamento de Esgoto", s.NormativeAlerts));
            }

            // Capítulo 6: Compatibilização BIM 3D & Síntese Orçamentária SINAPI
            if (data.ClashAndSinapiRows.Count > 0)
            {
                bodyElements.Add(BuildSectionHeading($"{chapter++}. COMPATIBILIZAÇÃO BIM 3D (CLASH LSF/ESTRUTURA) E SÍNTESE ORÇAMENTÁRIA SINAPI"));
                bodyElements.Add(BuildParagraph(
                    "Síntese da auditoria tridimensional de interferências entre instalações hidrossanitárias e elementos estruturais/LSF, acompanhada do resumo estimativo de materiais mapeados ao catálogo Tigre/Amanco e tabela SINAPI."));
                bodyElements.Add(BuildKeyValueTable("Indicadores BIM 3D e Orçamento Estimativo", data.ClashAndSinapiRows));
            }

            
            // Capítulo Executivo A: Especificações Técnicas de Materiais e Componentes (ABNT)
            bodyElements.Add(BuildSectionHeading($"{chapter++}. CADERNO DE ESPECIFICAÇÕES TÉCNICAS DE MATERIAIS E COMPONENTES"));
            bodyElements.Add(BuildParagraph(
                "Todos os materiais empregados na execução das instalações hidrossanitárias devem ser novos, de primeira qualidade, isentos de defeitos de fabricação e possuir certificação de conformidade com as normas técnicas brasileiras (ABNT / PBQP-H):"));

            bodyElements.Add(BuildKeyValueTable("Especificação Executiva por Subsistema Hidrossanitário", new List<(string, string)>
            {
                ("Água Fria — Alimentador, Barrilete, Colunas e Ramais", "Tubos e conexões em PVC rígido soldável marrom (ABNT NBR 5648), classe de pressão PN 750 kPa (75 m.c.a. a 20 °C). Nos pontos de utilização com rosca metálica (chuveiros, lavatórios, torneiras, engates), utilizar obrigatoriamente joelhos ou tês azuis com bucha de latão (LR) para evitar trincas por torque."),
                ("Água Quente — Barrilete e Ramais", "Tubos e conexões em CPVC (ABNT NBR 15884) ou PPR PN 20/25 termofusão (ABNT NBR 15813), dimensionados para operação contínua a 70 °C e picos de 80 °C. Prever isolamento térmico com espuma elastomérica de células fechadas (espessura mín. 9 mm) em trechos embutidos ou em shafts."),
                ("Esgoto Sanitário — Ramais de Descarga e Esgoto", "Tubos e conexões em PVC rígido branco ponta e bolsa com anel de borracha elastomérica (ABNT NBR 5688 — Série Normal) para ramais horizontais internos aos pavimentos."),
                ("Esgoto Sanitário — Tubos de Queda e Subcoletores", "Tubos e conexões em PVC rígido Série Reforçada (ABNT NBR 5688 — cor cinza) para tubos de queda verticais, subcoletores enterrados sob áreas de tráfego e desvios horizontais de prumadas, garantindo resistência mecânica e atenuação acústica."),
                ("Ventilação Primária e Secundária", "Tubos e conexões em PVC rígido (ABNT NBR 5688), interligados acima do nível de transbordamento dos aparelhos mais elevados e prolongados no mínimo 0,30 m acima da cobertura (ou 2,00 m quando o terraço for utilizável), dotados de terminal de ventilação."),
                ("Águas Pluviais — Calhas e Condutores", "Calhas em chapa de aço galvanizado nº 24 (espessura 0,65 mm) com pintura anticorrosiva ou PVC específico; condutores verticais e horizontais em PVC rígido Série R / Pluvial (ABNT NBR 10844), com ralos hemisféricos (abacaxi) nas saídas de lajes e calhas."),
                ("Registros, Válvulas e Metais Sanitários", "Registros de gaveta brutos/acabamento (ABNT NBR 15705) para bloqueio setorial de barriletes, colunas e ambientes molhados; registros de pressão (ABNT NBR 15704) para controle de vazão em chuveiros; válvulas de retenção horizontal/vertical nas saídas de bombas e reservatórios.")
            }));

            // Capítulo Executivo B: Metodologia de Cálculo & Equações Normativas
            bodyElements.Add(BuildSectionHeading($"{chapter++}. METODOLOGIA DE CÁLCULO E FORMULAÇÃO NORMATIVA APLICADA"));
            bodyElements.Add(BuildParagraph(
                "Os parâmetros hidráulicos, sanitários e térmicos apresentados neste memorial foram obtidos através das formulações consagradas pelas normas brasileiras:"));
            bodyElements.Add(BuildKeyValueTable("Equações e Critérios Normativos de Dimensionamento", new List<(string, string)>
            {
                ("Vazão Provável (ABNT NBR 5626)", "Q = 0,30 × √(ΣP)  [L/s], onde ΣP representa o somatório dos pesos relativos das peças de utilização alimentadas pelo trecho."),
                ("Perda de Carga Distribuída (Fair-Whipple-Hsiao)", "Plásticos (PVC/CPVC/PPR): J = 8,69 × 10⁶ × Q^1,75 × D^-4,75  [m.c.a./m]. Perdas localizadas convertidas em comprimentos equivalentes de conexões conforme tabela normativa."),
                ("Limites de Velocidade e Pressão (NBR 5626)", "Velocidade máxima admitida: V ≤ 3,00 m/s (recomendado ≤ 2,00 m/s para controle acústico). Pressão estática máxima: ≤ 40,0 m.c.a. Pressão dinâmica mínima: ≥ 1,00 m.c.a. nos pontos gerais e ≥ 2,00 m.c.a. em chuveiros/aquecedores."),
                ("Dimensionamento de Recalque (Bresse & Hazen-Williams)", "D_rec = k × √Q_rec (k entre 0,90 e 1,30); D_suc adotado um diâmetro comercial imediatamente superior ao de recalque. Perda de carga pela equação de Hazen-Williams (C = 140 a 150)."),
                ("Esgoto Sanitário (ABNT NBR 8160)", "Dimensionamento por Unidades Hunter de Contribuição (UHC). Declividade mínima: i = 2,0% para DN ≤ 75 mm e i = 1,0% para DN ≥ 100 mm. Fecho hídrico mínimo de desconectores: 50 mm."),
                ("Tratamento Individual (NBR 7229 / NBR 13969)", "Tanque Séptico: V = 1000 + N × (C × Td + K × Lf) [Litros]. Filtro Anaeróbio: Vu = 1,6 × N × C × Td [Litros, mín. 1000 L]. Sumidouro: A_inf = (N × C) / Ci [m²]."),
                ("Drenagem Pluvial (ABNT NBR 10844)", "Vazão de projeto: Q = (I × A) / 60  [L/min], com período de retorno T = 5 anos para coberturas e chuva crítica de 5 minutos. Capacidade de escoamento pela fórmula de Manning-Strickler.")
            }));

            // Capítulo Executivo C: Diretrizes de Execução, Ensaios e Manutenção Preventiva
            bodyElements.Add(BuildSectionHeading($"{chapter++}. DIRETRIZES DE EXECUÇÃO EM OBRA, ENSAIOS E RECEBIMENTO"));
            bodyElements.Add(BuildParagraph(
                "A execução das instalações deverá obedecer rigorosamente ao projeto executivo compatibilizado (BIM 3D) e às boas práticas de engenharia:"));
            bodyElements.Add(BuildKeyValueTable("Protocolo Executivo, Alturas Padrão e Ensaios de Estanqueidade", new List<(string, string)>
            {
                ("Cotas Padrão de Pontos (Piso Acabado)", "Bacia c/ caixa acoplada: H = 0,20 m (deslocado 0,15 m à esquerda) | Lavatório: AF/AQ H = 0,60 m, Esgoto H = 0,50 m | Chuveiro: Ponto H = 2,10 m, Registro H = 1,10 m | Pia de Cozinha: AF/AQ H = 1,00 m, Esgoto H = 0,50 m | Tanque: H = 0,90 m | Registro de Gaveta Geral: H = 1,80 m."),
                ("Assentamento e Recobrimento de Tubulações", "Tubulações enterradas devem ser assentadas sobre berço de areia fina isenta de pedras (espessura mín. 10 cm) com recobrimento mínimo de 0,30 m em passeios e 0,60 m sob áreas de veículos. Proibido o embutimento diagonal em alvenaria estrutural."),
                ("Ensaio de Estanqueidade — Água Fria e Quente", "Antes do fechamento de rasgos, forros ou shafts, pressurizar toda a rede com água limpa a 1,5 vezes a pressão estática máxima prevista (mínimo de 60 m.c.a. / 6,0 kgf/cm²) durante no mínimo 1 hora, sem queda de pressão no manômetro ou vazamentos visuais."),
                ("Ensaio de Estanqueidade — Esgoto e Pluvial", "Preencher os trechos de esgoto e condutores pluviais com coluna d'água de no mínimo 3,00 m.c.a. acima do ponto mais alto do trecho testado por período mínimo de 15 minutos, inspecionando todas as juntas e bolsas."),
                ("Manutenção Preventiva e Operação", "1) Limpeza semestral dos reservatórios de água potável com hipoclorito de sódio; 2) Remoção trimestral da crosta de gordura na Caixa de Gordura; 3) Limpeza do lodo digerido do Tanque Séptico conforme intervalo de projeto (mantendo 10% do lodo como inóculo biológico); 4) Limpeza periódica de calhas e ralos de cobertura.")
            }));

            // Bloco Final de Assinatura
            bodyElements.Add(BuildParagraph("", spaceAfter: 240));
            bodyElements.Add(BuildParagraph("____________________________________________________________", alignCenter: true, spaceAfter: 40));
            bodyElements.Add(BuildParagraph("RESPONSÁVEL TÉCNICO PELO PROJETO HIDROSSANITÁRIO", bold: true, alignCenter: true, spaceAfter: 20));
            bodyElements.Add(BuildParagraph("Engenharia Civil / Hidrossanitária — Documento gerado via Smart Hidro BIM (Revit 2026)", colorHex: "64748B", alignCenter: true, spaceAfter: 0));

            // Configuração de página A4 com margens de 2,0 cm
            bodyElements.Add(new XElement(W + "sectPr",
                new XElement(W + "pgSz", new XAttribute(W + "w", "11906"), new XAttribute(W + "h", "16838")),
                new XElement(W + "pgMar",
                    new XAttribute(W + "top", "1134"),
                    new XAttribute(W + "right", "1134"),
                    new XAttribute(W + "bottom", "1134"),
                    new XAttribute(W + "left", "1134"),
                    new XAttribute(W + "header", "708"),
                    new XAttribute(W + "footer", "708"),
                    new XAttribute(W + "gutter", "0"))
            ));

            return new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(W + "document",
                    new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
                    new XElement(W + "body", bodyElements)
                ));
        }

        private static XElement BuildBannerBox(string title, string subtitle)
        {
            return new XElement(W + "tbl",
                new XElement(W + "tblPr",
                    new XElement(W + "tblW", new XAttribute(W + "w", "9638"), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "tblCellMar",
                        new XElement(W + "top", new XAttribute(W + "w", "180"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "bottom", new XAttribute(W + "w", "180"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "left", new XAttribute(W + "w", "220"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "right", new XAttribute(W + "w", "220"), new XAttribute(W + "type", "dxa"))
                    )
                ),
                new XElement(W + "tr",
                    new XElement(W + "tc",
                        new XElement(W + "tcPr",
                            new XElement(W + "tcW", new XAttribute(W + "w", "9638"), new XAttribute(W + "type", "dxa")),
                            new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", "0F172A"))
                        ),
                        BuildParagraph(title, bold: true, fontSizeHalfPt: 26, colorHex: "FFFFFF", spaceAfter: 60),
                        BuildParagraph(subtitle, bold: false, fontSizeHalfPt: 19, colorHex: "CBD5E1", spaceAfter: 0)
                    )
                )
            );
        }

        private static XElement BuildSectionHeading(string text)
        {
            return new XElement(W + "p",
                new XElement(W + "pPr",
                    new XElement(W + "spacing", new XAttribute(W + "before", "220"), new XAttribute(W + "after", "80")),
                    new XElement(W + "pBdr",
                        new XElement(W + "bottom", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "8"), new XAttribute(W + "space", "4"), new XAttribute(W + "color", "2563EB"))
                    )
                ),
                new XElement(W + "r",
                    new XElement(W + "rPr",
                        new XElement(W + "b"),
                        new XElement(W + "sz", new XAttribute(W + "val", "24")),
                        new XElement(W + "color", new XAttribute(W + "val", "0F172A"))
                    ),
                    new XElement(W + "t", text)
                )
            );
        }

        private static XElement BuildParagraph(
            string text,
            bool bold = false,
            int fontSizeHalfPt = 21,
            string colorHex = "1E293B",
            bool alignCenter = false,
            int spaceAfter = 90)
        {
            var pPr = new XElement(W + "pPr",
                new XElement(W + "spacing", new XAttribute(W + "after", spaceAfter.ToString(CultureInfo.InvariantCulture))));
            if (alignCenter)
                pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", "center")));

            var rPr = new XElement(W + "rPr",
                new XElement(W + "sz", new XAttribute(W + "val", fontSizeHalfPt.ToString(CultureInfo.InvariantCulture))),
                new XElement(W + "color", new XAttribute(W + "val", colorHex)));
            if (bold) rPr.Add(new XElement(W + "b"));

            return new XElement(W + "p",
                pPr,
                new XElement(W + "r", rPr, new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text))
            );
        }

        private static XElement BuildKeyValueTable(string tableTitle, List<(string Param, string Value)> rows)
        {
            var tbl = new XElement(W + "tbl",
                new XElement(W + "tblPr",
                    new XElement(W + "tblW", new XAttribute(W + "w", "9638"), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "tblBorders",
                        new XElement(W + "top", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "CBD5E1")),
                        new XElement(W + "bottom", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "CBD5E1")),
                        new XElement(W + "left", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "CBD5E1")),
                        new XElement(W + "right", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "CBD5E1")),
                        new XElement(W + "insideH", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "E2E8F0")),
                        new XElement(W + "insideV", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "E2E8F0"))
                    ),
                    new XElement(W + "tblCellMar",
                        new XElement(W + "top", new XAttribute(W + "w", "80"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "bottom", new XAttribute(W + "w", "80"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "left", new XAttribute(W + "w", "130"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "right", new XAttribute(W + "w", "130"), new XAttribute(W + "type", "dxa"))
                    )
                ),
                // Linha de cabeçalho da tabela
                new XElement(W + "tr",
                    new XElement(W + "tc",
                        new XElement(W + "tcPr",
                            new XElement(W + "tcW", new XAttribute(W + "w", "9638"), new XAttribute(W + "type", "dxa")),
                            new XElement(W + "gridSpan", new XAttribute(W + "val", "2")),
                            new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", "E2E8F0"))
                        ),
                        BuildParagraph(tableTitle, bold: true, fontSizeHalfPt: 20, colorHex: "0F172A", spaceAfter: 0)
                    )
                )
            );

            bool alt = false;
            foreach (var (param, val) in rows)
            {
                string fill = alt ? "F8FAFC" : "FFFFFF";
                alt = !alt;

                tbl.Add(new XElement(W + "tr",
                    new XElement(W + "tc",
                        new XElement(W + "tcPr",
                            new XElement(W + "tcW", new XAttribute(W + "w", "3600"), new XAttribute(W + "type", "dxa")),
                            new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", fill))
                        ),
                        BuildParagraph(param, bold: true, fontSizeHalfPt: 19, colorHex: "334155", spaceAfter: 0)
                    ),
                    new XElement(W + "tc",
                        new XElement(W + "tcPr",
                            new XElement(W + "tcW", new XAttribute(W + "w", "6038"), new XAttribute(W + "type", "dxa")),
                            new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", fill))
                        ),
                        BuildParagraph(val, bold: false, fontSizeHalfPt: 19, colorHex: "0F172A", spaceAfter: 0)
                    )
                ));
            }

            return tbl;
        }

        private static XElement BuildAlertCallout(string title, List<string> alerts)
        {
            var cell = new XElement(W + "tc",
                new XElement(W + "tcPr",
                    new XElement(W + "tcW", new XAttribute(W + "w", "9638"), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", "FFFBEB"))
                ),
                BuildParagraph(title, bold: true, fontSizeHalfPt: 19, colorHex: "92400E", spaceAfter: 40)
            );

            for (int i = 0; i < alerts.Count; i++)
            {
                cell.Add(BuildParagraph($"• {alerts[i]}", bold: false, fontSizeHalfPt: 18, colorHex: "78350F", spaceAfter: i == alerts.Count - 1 ? 0 : 30));
            }

            return new XElement(W + "tbl",
                new XElement(W + "tblPr",
                    new XElement(W + "tblW", new XAttribute(W + "w", "9638"), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "tblBorders",
                        new XElement(W + "left", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "18"), new XAttribute(W + "color", "F59E0B")),
                        new XElement(W + "top", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "FDE68A")),
                        new XElement(W + "bottom", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "FDE68A")),
                        new XElement(W + "right", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "color", "FDE68A"))
                    ),
                    new XElement(W + "tblCellMar",
                        new XElement(W + "top", new XAttribute(W + "w", "80"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "bottom", new XAttribute(W + "w", "80"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "left", new XAttribute(W + "w", "140"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "right", new XAttribute(W + "w", "120"), new XAttribute(W + "type", "dxa"))
                    )
                ),
                new XElement(W + "tr", cell)
            );
        }
    }
}
