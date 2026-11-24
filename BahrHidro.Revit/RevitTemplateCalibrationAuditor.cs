using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using BahrHidro.UI;

namespace BahrHidro.Revit
{
    public static class RevitTemplateCalibrationAuditor
    {
        public static TemplateCalibrationAuditResult RunAudit(Document doc)
        {
            var items = new List<TemplateCheckItem>();

            // 1. Níveis (Pavimentos)
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            items.Add(new TemplateCheckItem(
                "Geometria Base",
                "Níveis / Pavimentos",
                levels.Count >= 2 ? "OK" : (levels.Count == 1 ? "ATENÇÃO" : "CRÍTICO"),
                $"{levels.Count} nível(is) encontrado(s)",
                levels.Count >= 2
                    ? "Estrutura de pavimentos pronta para prumadas e pressão estática."
                    : "Cadastre ao menos 2 níveis (ex.: Térreo e Cobertura) para cálculo de altura manométrica e prumadas."));

            // 2. Tipos de Tubulação e Preferências de Roteamento (Conexões)
            var pipeTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .ToList();

            int configuredRoutingTypes = 0;
            foreach (var pt in pipeTypes)
            {
                try
                {
                    var rpm = pt.RoutingPreferenceManager;
                    int elbows = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows);
                    int junctions = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions);
                    if (elbows > 0 && junctions > 0)
                        configuredRoutingTypes++;
                }
                catch { }
            }

            items.Add(new TemplateCheckItem(
                "Tubulações & Conexões",
                "Tipos de Tubo (PipeType) c/ Preferências",
                configuredRoutingTypes > 0 ? "OK" : (pipeTypes.Count > 0 ? "ATENÇÃO" : "CRÍTICO"),
                $"{configuredRoutingTypes} de {pipeTypes.Count} tipo(s) com joelhos e tês configurados",
                configuredRoutingTypes > 0
                    ? "Preferências de roteamento válidas para conexões automáticas."
                    : "Configure Joelhos e Tês em 'Preferências de Roteamento' do PipeType para conexões automáticas 3D."));

            // 3. Famílias de Conexões (Pipe Fittings)
            int fittingTypesCount = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .WhereElementIsElementType()
                .GetElementCount();

            items.Add(new TemplateCheckItem(
                "Tubulações & Conexões",
                "Famílias de Conexões (Tigre / Amanco)",
                fittingTypesCount >= 4 ? "OK" : (fittingTypesCount > 0 ? "ATENÇÃO" : "CRÍTICO"),
                $"{fittingTypesCount} tipo(s) de conexão carregado(s)",
                fittingTypesCount >= 4
                    ? "Catálogo de conexões disponível para quantitativo SINAPI e modelagem."
                    : "Carregue famílias de joelhos 90°/45°, tês e reduções (PVC Soldável / Esgoto Série Normal)."));

            // 4. Sistemas Hidrossanitários (PipingSystemType)
            var sysTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .ToList();

            bool hasCold = sysTypes.Any(s => s.SystemClassification == MEPSystemClassification.DomesticColdWater);
            bool hasHot = sysTypes.Any(s => s.SystemClassification == MEPSystemClassification.DomesticHotWater);
            bool hasSanitary = sysTypes.Any(s => s.SystemClassification == MEPSystemClassification.Sanitary);
            bool hasVent = sysTypes.Any(s => s.SystemClassification == MEPSystemClassification.Vent);

            int sysPresentCount = (hasCold ? 1 : 0) + (hasHot ? 1 : 0) + (hasSanitary ? 1 : 0) + (hasVent ? 1 : 0);
            var missingSys = new List<string>();
            if (!hasCold) missingSys.Add("Água Fria");
            if (!hasHot) missingSys.Add("Água Quente");
            if (!hasSanitary) missingSys.Add("Esgoto Sanitário");
            if (!hasVent) missingSys.Add("Ventilação");

            items.Add(new TemplateCheckItem(
                "Sistemas MEP",
                "Classificações ABNT (AF, AQ, Esgoto, Vent.)",
                sysPresentCount == 4 ? "OK" : (sysPresentCount >= 2 ? "ATENÇÃO" : "CRÍTICO"),
                sysPresentCount == 4 ? "4/4 sistemas normativos ativos" : $"{sysPresentCount}/4 ativos (Faltam: {string.Join(", ", missingSys)})",
                sysPresentCount == 4
                    ? "Todos os sistemas hidráulicos e sanitários estão prontos."
                    : "Clique em 'Auto-Corrigir Sistemas Base no Template' para criar os sistemas ausentes."));

            // 5. Aparelhos Sanitários e Conectores Hidráulicos
            var fixtures = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            int fixturesWithConnectors = 0;
            foreach (var fi in fixtures)
            {
                try
                {
                    var conns = fi.MEPModel?.ConnectorManager?.Connectors;
                    if (conns != null)
                    {
                        foreach (Connector c in conns)
                        {
                            if (c.Domain == Domain.DomainPiping)
                            {
                                fixturesWithConnectors++;
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            items.Add(new TemplateCheckItem(
                "Modelagem Piloto",
                "Aparelhos Sanitários c/ Conectores MEP",
                fixturesWithConnectors > 0 ? "OK" : (fixtures.Count > 0 ? "ATENÇÃO" : "ATENÇÃO"),
                $"{fixturesWithConnectors} com conectores MEP ({fixtures.Count} inserido(s) no total)",
                fixturesWithConnectors > 0
                    ? "Aparelhos prontos para extração espacial 3D, cálculo de pesos e UHC."
                    : "Insira aparelhos sanitários com conectores hidráulicos (bacia, lavatório, chuveiro, pia) para o piloto."));

            // 6. Etiquetas de Tubulação (Pipe Tags)
            int pipeTagsCount = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeTags)
                .WhereElementIsElementType()
                .GetElementCount();

            items.Add(new TemplateCheckItem(
                "Documentação ABNT",
                "Famílias de Etiquetas de Tubo (Pipe Tags)",
                pipeTagsCount > 0 ? "OK" : "ATENÇÃO",
                $"{pipeTagsCount} família(s) de tag carregada(s)",
                pipeTagsCount > 0
                    ? "Etiquetagem automática habilitada nas vistas isométricas 3D."
                    : "Carregue uma família de Identificador de Tubulação (Tag de Diâmetro/Sistema) para anotação automática."));

            // 7. Carimbos / Folhas (TitleBlocks)
            var titleBlocks = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            items.Add(new TemplateCheckItem(
                "Documentação ABNT",
                "Carimbo / Folha Executiva (A1 / A0)",
                titleBlocks.Count > 0 ? "OK" : "ATENÇÃO",
                $"{titleBlocks.Count} formato(s) de folha carregado(s)",
                titleBlocks.Count > 0
                    ? $"Pronto para montagem automática da prancha HID-01 ({titleBlocks.First().FamilyName})."
                    : "Carregue uma família de Folha/Carimbo ABNT A1 para diagramação automática da prancha HID-01."));

            // 8. Família de Vista 3D
            bool has3DViewFamily = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .Any(v => v.ViewFamily == ViewFamily.ThreeDimensional);

            items.Add(new TemplateCheckItem(
                "Visualização 3D",
                "Tipo de Vista 3D Isométrica",
                has3DViewFamily ? "OK" : "CRÍTICO",
                has3DViewFamily ? "Disponível" : "Ausente",
                has3DViewFamily
                    ? "Suporte ativo para Isométricos 3D, Clash LSF/Estrutura e Mapa de Pressão/Velocidade."
                    : "Template sem ViewFamilyType 3D."));

            int okCount = items.Count(i => i.Status == "OK");
            int critCount = items.Count(i => i.Status == "CRÍTICO");
            int readinessPct = (int)Math.Round((okCount * 100.0) / items.Count);

            string overallBadge = critCount > 0
                ? $"BLOQUEIOS CRÍTICOS ({readinessPct}% Calibrado)"
                : (okCount == items.Count ? "100% CALIBRADO — PRONTO PARA PROJETO PILOTO" : $"PRONTO COM RESSALVAS ({readinessPct}% Calibrado)");

            string summary = $"Diagnóstico do Template '{doc.Title}': {overallBadge}  |  {okCount}/{items.Count} requisitos conformes.";
            return new TemplateCalibrationAuditResult(readinessPct, overallBadge, summary, items);
        }

        public static string AutoFixTemplateBasics(Document doc)
        {
            var createdLogs = new List<string>();

            var sysTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .ToList();

            var requiredSystems = new[]
            {
                (MEPSystemClassification.DomesticColdWater, "Água Fria - Smart Hidro"),
                (MEPSystemClassification.DomesticHotWater, "Água Quente - Smart Hidro"),
                (MEPSystemClassification.Sanitary, "Esgoto Sanitário - Smart Hidro"),
                (MEPSystemClassification.Vent, "Ventilação - Smart Hidro")
            };

            using (Transaction tx = new Transaction(doc, "Smart Hidro - Auto-Calibrar Sistemas Base do Template"))
            {
                tx.Start();

                foreach (var (classification, defaultName) in requiredSystems)
                {
                    if (!sysTypes.Any(s => s.SystemClassification == classification))
                    {
                        try
                        {
                            PipingSystemType.Create(doc, classification, defaultName);
                            createdLogs.Add($"Sistema criado: {defaultName}");
                        }
                        catch { }
                    }
                }

                tx.Commit();
            }

            return createdLogs.Count == 0
                ? "Nenhuma criação necessária: todas as classificações de sistemas hidráulicos e sanitários já existem no template."
                : $"Auto-calibração concluída ({createdLogs.Count} item(ns) adicionado(s)): {string.Join(" | ", createdLogs)}.";
        }
    }
}
