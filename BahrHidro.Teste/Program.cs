
using System;
using System.IO;
using System.Linq;
using BahrHidro.Core;
using BahrHidro.Core.Catalog;
using BahrHidro.Core.Hydraulic;
using BahrHidro.Core.Hydraulic.Network;

{
    // =========================================
    // TESTE DO CATALOGO
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DO PIPE CATALOG SERVICE");
    Console.WriteLine("====================================");

    string caminhoServico = Path.Combine(
        Path.GetTempPath(),
        "BahrHidro",
        "pipe-service-test.json");

    var repositoryServico =
        new PipeCatalogRepository(caminhoServico);

    var service = new PipeCatalogService(repositoryServico);

    var novoProduto = new PipeProduct
    {
        Manufacturer = "TESTE SERVICO",
        ProductLine = "PVC SOLDAVEL",
        Material = PipeMaterial.PvcSoldavel,
        NominalDesignation = "DN TESTE 40",
        OuterDiameterMm = 40m,
        WallThicknessMm = 3m,
        StandardReference = "TESTE",
        SourceDescription = "Produto ficticio"
    };

    await service.AddAsync(novoProduto);

    var cadastrados = await service.GetAllAsync();

    Console.WriteLine($"Cadastro: {cadastrados.Count}");

    if (cadastrados.Count != 1)
        throw new Exception("Falha no cadastro.");

    // DUPLICIDADE

    bool duplicidadeBloqueada = false;

    try
    {
        await service.AddAsync(new PipeProduct
        {
            Manufacturer = novoProduto.Manufacturer,
            ProductLine = novoProduto.ProductLine,
            Material = novoProduto.Material,
            NominalDesignation = novoProduto.NominalDesignation,
            OuterDiameterMm = 40m,
            WallThicknessMm = 3m,
            StandardReference = "TESTE",
            SourceDescription = "Produto ficticio"
        });
    }
    catch (InvalidOperationException)
    {
        duplicidadeBloqueada = true;
    }

    Console.WriteLine(
        $"Duplicidade bloqueada: {duplicidadeBloqueada}");

    if (!duplicidadeBloqueada)
        throw new Exception("Falha na duplicidade.");

    // EDICAO

    var produtoEditado = new PipeProduct
    {
        Id = novoProduto.Id,
        Manufacturer = novoProduto.Manufacturer,
        ProductLine = novoProduto.ProductLine,
        Material = novoProduto.Material,
        NominalDesignation = "DN TESTE 40 EDITADO",
        OuterDiameterMm = 40m,
        WallThicknessMm = 3m,
        StandardReference = "TESTE",
        SourceDescription = "Produto atualizado"
    };

    bool atualizado = await service.UpdateAsync(
        novoProduto.Id,
        produtoEditado);

    Console.WriteLine($"Edicao: {atualizado}");

    if (!atualizado)
        throw new Exception("Falha na edicao.");

    // EXCLUSAO

    bool excluido = await service.DeleteAsync(novoProduto.Id);

    Console.WriteLine($"Exclusao: {excluido}");

    if (!excluido)
        throw new Exception("Falha na exclusao.");

    var restantes = await service.GetAllAsync();

    Console.WriteLine($"Produtos restantes: {restantes.Count}");

    if (restantes.Count != 0)
        throw new Exception("Falha na remocao.");

    Console.WriteLine("TESTES DO CATALOGO APROVADOS!");

    // =========================================
    // TESTE DE PERDA DISTRIBUIDA
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE PERDA DISTRIBUIDA");
    Console.WriteLine("====================================");

    double perdaCarga =
        HeadLossCalculator.CalculateDarcyWeisbach(
            20.0, 20.0, 1.5, 0.025);

    Console.WriteLine(
        $"Perda distribuida: {perdaCarga:F3} m.c.a.");

    if (Math.Abs(perdaCarga - 2.868) > 0.01)
        throw new Exception("Falha na perda distribuida.");

    // =========================================
    // TESTE DE REYNOLDS
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE REYNOLDS");
    Console.WriteLine("====================================");

    ReynoldsResult reynolds =
        ReynoldsCalculator.Calculate(
            1.5, 20.0, 1.004e-6);

    Console.WriteLine(
        $"Reynolds: {reynolds.ReynoldsNumber:F0}");

    Console.WriteLine($"Regime: {reynolds.Regime}");

    if (reynolds.Regime != FlowRegime.Turbulent)
        throw new Exception("Falha no regime.");

    // =========================================
    // TESTE DO FATOR DE ATRITO
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DO FATOR DE ATRITO");
    Console.WriteLine("====================================");

    double fatorLaminar =
        FrictionFactorCalculator.Calculate(
            1000.0, 20.0, 0.001);

    Console.WriteLine(
        $"Fator laminar: {fatorLaminar:F5}");

    if (Math.Abs(fatorLaminar - 0.064) > 0.00001)
        throw new Exception("Falha no fator laminar.");

    double fatorTurbulento =
        FrictionFactorCalculator.Calculate(
            reynolds.ReynoldsNumber, 20.0, 0.001);

    Console.WriteLine(
        $"Fator turbulento: {fatorTurbulento:F5}");

    if (!double.IsFinite(fatorTurbulento) ||
        fatorTurbulento <= 0)
        throw new Exception("Fator turbulento invalido.");

    bool transicaoBloqueada = false;

    try
    {
        FrictionFactorCalculator.Calculate(
            3000.0, 20.0, 0.001);
    }
    catch (InvalidOperationException)
    {
        transicaoBloqueada = true;
    }

    Console.WriteLine(
        $"Transicao bloqueada: {transicaoBloqueada}");

    if (!transicaoBloqueada)
        throw new Exception("Falha na transicao.");

    // =========================================
    // TESTE DO TRECHO HIDRAULICO
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DO TRECHO HIDRAULICO");
    Console.WriteLine("====================================");

    HydraulicSegmentResult trecho =
        HydraulicSegmentCalculator.Calculate(
            0.30,
            20.0,
            20.0,
            0.001,
            1.004e-6);

    Console.WriteLine(
        $"Vazao: {trecho.FlowLitersPerSecond:F2} L/s");

    Console.WriteLine(
        $"Velocidade: {trecho.VelocityMetersPerSecond:F3} m/s");

    Console.WriteLine(
        $"Reynolds: {trecho.ReynoldsNumber:F0}");

    Console.WriteLine(
        $"Regime: {trecho.Regime}");

    Console.WriteLine(
        $"Fator de atrito: {trecho.FrictionFactor:F5}");

    Console.WriteLine(
        $"Perda distribuida: {trecho.HeadLossMeters:F3} m.c.a.");

    if (Math.Abs(trecho.VelocityMetersPerSecond - 0.95493) > 0.0001)
        throw new Exception("Velocidade incorreta.");

    if (Math.Abs(trecho.ReynoldsNumber - 19022.5) > 1.0)
        throw new Exception("Reynolds incorreto.");

    if (Math.Abs(trecho.FrictionFactor - 0.02626) > 0.0001)
        throw new Exception("Fator de atrito incorreto.");

    if (Math.Abs(trecho.HeadLossMeters - 1.221) > 0.01)
        throw new Exception("Perda distribuida incorreta.");

    // =========================================
    // TESTE DE PERDA LOCALIZADA
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE PERDA LOCALIZADA");
    Console.WriteLine("====================================");

    double perdaLocalizada =
        LocalLossCalculator.Calculate(2.0, 1.5);

    Console.WriteLine(
        $"Perda localizada: {perdaLocalizada:F3} m.c.a.");

    if (Math.Abs(perdaLocalizada - 0.22944) > 0.0001)
        throw new Exception("Falha na perda localizada.");

    // =========================================
    // TESTE DE PERDA TOTAL INTEGRADA
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE PERDA TOTAL INTEGRADA");
    Console.WriteLine("====================================");

    HydraulicSegmentResult trechoCompleto =
        HydraulicSegmentCalculator.Calculate(
            flowLitersPerSecond: 0.30,
            lengthMeters: 20.0,
            internalDiameterMm: 20.0,
            absoluteRoughnessMm: 0.001,
            kinematicViscosityM2s: 1.004e-6,
            totalLossCoefficientK: 3.0);

    Console.WriteLine(
        $"Perda distribuida: {trechoCompleto.HeadLossMeters:F6} m.c.a.");

    Console.WriteLine(
        $"Perda localizada: {trechoCompleto.LocalLossMeters:F6} m.c.a.");

    Console.WriteLine(
        $"Perda total: {trechoCompleto.TotalHeadLossMeters:F6} m.c.a.");

    double resultadoEsperado =
        trechoCompleto.HeadLossMeters +
        trechoCompleto.LocalLossMeters;

    if (Math.Abs(
        trechoCompleto.TotalHeadLossMeters -
        resultadoEsperado) > 0.000001)
    {
        throw new Exception("Falha na soma das perdas.");
    }

    Console.WriteLine("TESTE DE PERDA TOTAL APROVADO!");

    // =========================================
    // TESTE DA REDE HIDRAULICA
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DA REDE HIDRAULICA");
    Console.WriteLine("====================================");

    var rede = new HydraulicNetwork(
        "Rede Experimental Bahr Hidro");

    var reservatorio = new HydraulicNode
    {
        Name = "Reservatorio Superior",
        ElevationMeters = 12.0,
        Type = HydraulicNodeType.Reservoir,
        ExternalSupplyLitersPerSecond = 0.30
    };

    var conexao = new HydraulicNode
    {
        Name = "Conexao Principal",
        ElevationMeters = 8.0,
        Type = HydraulicNodeType.Junction,
        DemandLitersPerSecond = 0.10
    };

    var consumo = new HydraulicNode
    {
        Name = "Ponto de Consumo",
        ElevationMeters = 3.0,
        Type = HydraulicNodeType.ConsumptionPoint,
        DemandLitersPerSecond = 0.20
    };

    rede.AddNode(reservatorio);
    rede.AddNode(conexao);
    rede.AddNode(consumo);

    Console.WriteLine($"Rede: {rede.Name}");
    Console.WriteLine($"Nos cadastrados: {rede.Nodes.Count}");

    if (rede.Nodes.Count != 3)
        throw new Exception("Falha no cadastro dos nos.");

    var trecho1 = new HydraulicPipeSegment
    {
        Name = "Trecho 1",
        StartNodeId = reservatorio.Id,
        EndNodeId = conexao.Id,
        LengthMeters = 20.0,
        InternalDiameterMm = 20.0,
        AbsoluteRoughnessMm = 0.001,
        FlowLitersPerSecond = 0.30,
        TotalLossCoefficientK = 1.0
    };

    var trecho2 = new HydraulicPipeSegment
    {
        Name = "Trecho 2",
        StartNodeId = conexao.Id,
        EndNodeId = consumo.Id,
        LengthMeters = 15.0,
        InternalDiameterMm = 20.0,
        AbsoluteRoughnessMm = 0.001,
        FlowLitersPerSecond = 0.20,
        TotalLossCoefficientK = 2.0
    };

    rede.AddSegment(trecho1);
    rede.AddSegment(trecho2);

    Console.WriteLine(
        $"Trechos cadastrados: {rede.Segments.Count}");

    if (rede.Segments.Count != 2)
        throw new Exception("Falha no cadastro dos trechos.");

    // =========================================
    // TESTE DO CALCULADOR DA REDE
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DO CALCULADOR DA REDE");
    Console.WriteLine("====================================");

    var resultadosRede =
        HydraulicNetworkCalculator.Calculate(
            rede,
            1.004e-6);

    Console.WriteLine(
        $"Trechos calculados: {resultadosRede.Count}");

    if (resultadosRede.Count != 2)
        throw new Exception("Quantidade incorreta de resultados.");

    foreach (var resultado in resultadosRede)
    {
        var calculo = resultado.HydraulicResult;

        Console.WriteLine($"\n{resultado.SegmentName}");

        Console.WriteLine(
            $"Vazao: {calculo.FlowLitersPerSecond:F3} L/s");

        Console.WriteLine(
            $"Velocidade: {calculo.VelocityMetersPerSecond:F3} m/s");

        Console.WriteLine(
            $"Reynolds: {calculo.ReynoldsNumber:F0}");

        Console.WriteLine(
            $"Perda distribuida: {calculo.HeadLossMeters:F6} m.c.a.");

        Console.WriteLine(
            $"Perda localizada: {calculo.LocalLossMeters:F6} m.c.a.");

        Console.WriteLine(
            $"Perda total: {calculo.TotalHeadLossMeters:F6} m.c.a.");

        if (!double.IsFinite(calculo.TotalHeadLossMeters) ||
            calculo.TotalHeadLossMeters < 0)
        {
            throw new Exception(
                $"Resultado hidraulico invalido em {resultado.SegmentName}.");
        }
    }

    // =========================================
    // TESTE DE CONTINUIDADE DOS NOS
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE CONTINUIDADE DOS NOS");
    Console.WriteLine("====================================");

    var resultadosBalanco =
        HydraulicNodeBalanceCalculator.Calculate(
            rede,
            0.000001);

    foreach (var resultado in resultadosBalanco)
    {
        Console.WriteLine($"\nNo: {resultado.NodeName}");

        Console.WriteLine(
            $"Entrada: {resultado.IncomingFlowLitersPerSecond:F3} L/s");

        Console.WriteLine(
            $"Alimentacao externa: {resultado.ExternalSupplyLitersPerSecond:F3} L/s");

        Console.WriteLine(
            $"Saida: {resultado.OutgoingFlowLitersPerSecond:F3} L/s");

        Console.WriteLine(
            $"Demanda: {resultado.DemandLitersPerSecond:F3} L/s");

        Console.WriteLine(
            $"Erro de balanco: {resultado.BalanceErrorLitersPerSecond:F6} L/s");

        Console.WriteLine(
            $"Equilibrado: {resultado.IsBalanced}");
    }

    foreach (var resultado in resultadosBalanco)
    {
        if (!resultado.IsBalanced)
        {
            throw new Exception(
                $"O no '{resultado.NodeName}' nao esta equilibrado.");
        }
    }

    Console.WriteLine("\nTODOS OS NOS ESTAO EQUILIBRADOS!");
    Console.WriteLine("\nTESTE DE CONTINUIDADE APROVADO!");

    // =========================================
    // TESTE DE DISTRIBUICAO AUTOMATICA DE VAZAO
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE DISTRIBUICAO AUTOMATICA");
    Console.WriteLine("====================================");

    var distribuicao =
        HydraulicBranchedFlowCalculator.Calculate(rede);

    Console.WriteLine(
        $"Reservatorio: {distribuicao.SourceNodeName}");

    Console.WriteLine(
        $"Demanda total: {distribuicao.TotalDemandLitersPerSecond:F3} L/s");

    Console.WriteLine(
        $"Alimentacao necessaria: {distribuicao.RequiredSupplyLitersPerSecond:F3} L/s");

    foreach (var vazao in distribuicao.PipeFlows)
    {
        Console.WriteLine(
            $"{vazao.SegmentName}: {vazao.FlowLitersPerSecond:F3} L/s");
    }

    if (distribuicao.PipeFlows.Count != 2)
        throw new Exception("Quantidade incorreta de vazoes calculadas.");

    var vazaoTrecho1 = distribuicao.PipeFlows
        .Single(v => v.SegmentId == trecho1.Id);

    var vazaoTrecho2 = distribuicao.PipeFlows
        .Single(v => v.SegmentId == trecho2.Id);

    if (Math.Abs(vazaoTrecho1.FlowLitersPerSecond - 0.30) > 0.000001)
        throw new Exception("Vazao incorreta no trecho 1.");

    if (Math.Abs(vazaoTrecho2.FlowLitersPerSecond - 0.20) > 0.000001)
        throw new Exception("Vazao incorreta no trecho 2.");

    if (Math.Abs(distribuicao.TotalDemandLitersPerSecond - 0.30) > 0.000001)
        throw new Exception("Demanda total incorreta.");

    Console.WriteLine(
        "\nTESTE DE DISTRIBUICAO AUTOMATICA APROVADO!");

    // =========================================
    // TESTE DE REDE COM DUAS RAMIFICACOES
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE REDE COM DUAS RAMIFICACOES");
    Console.WriteLine("====================================");

    var redeRamificada = new HydraulicNetwork(
        "Rede Ramificada Bahr Hidro");

    var reservatorioRamificado = new HydraulicNode
    {
        Name = "Reservatorio Ramificado",
        ElevationMeters = 15.0,
        Type = HydraulicNodeType.Reservoir,
        ExternalSupplyLitersPerSecond = 0.50
    };

    var derivacaoPrincipal = new HydraulicNode
    {
        Name = "Derivacao Principal",
        ElevationMeters = 10.0,
        Type = HydraulicNodeType.Junction,
        DemandLitersPerSecond = 0.0
    };

    var consumoA = new HydraulicNode
    {
        Name = "Consumo A",
        ElevationMeters = 6.0,
        Type = HydraulicNodeType.ConsumptionPoint,
        DemandLitersPerSecond = 0.30
    };

    var consumoB = new HydraulicNode
    {
        Name = "Consumo B",
        ElevationMeters = 4.0,
        Type = HydraulicNodeType.ConsumptionPoint,
        DemandLitersPerSecond = 0.20
    };

    redeRamificada.AddNode(reservatorioRamificado);
    redeRamificada.AddNode(derivacaoPrincipal);
    redeRamificada.AddNode(consumoA);
    redeRamificada.AddNode(consumoB);

    var trechoPrincipalRamificado = new HydraulicPipeSegment
    {
        Name = "Trecho Principal",
        StartNodeId = reservatorioRamificado.Id,
        EndNodeId = derivacaoPrincipal.Id,
        LengthMeters = 25.0,
        InternalDiameterMm = 25.0,
        AbsoluteRoughnessMm = 0.001,
        TotalLossCoefficientK = 1.0
    };

    var trechoRamalA = new HydraulicPipeSegment
    {
        Name = "Ramal A",
        StartNodeId = derivacaoPrincipal.Id,
        EndNodeId = consumoA.Id,
        LengthMeters = 12.0,
        InternalDiameterMm = 20.0,
        AbsoluteRoughnessMm = 0.001,
        TotalLossCoefficientK = 1.0
    };

    var trechoRamalB = new HydraulicPipeSegment
    {
        Name = "Ramal B",
        StartNodeId = derivacaoPrincipal.Id,
        EndNodeId = consumoB.Id,
        LengthMeters = 18.0,
        InternalDiameterMm = 20.0,
        AbsoluteRoughnessMm = 0.001,
        TotalLossCoefficientK = 1.0
    };

    redeRamificada.AddSegment(trechoPrincipalRamificado);
    redeRamificada.AddSegment(trechoRamalA);
    redeRamificada.AddSegment(trechoRamalB);

    var resultadoRamificado =
        HydraulicBranchedFlowCalculator.Calculate(redeRamificada);

    Console.WriteLine(
        $"Demanda total: {resultadoRamificado.TotalDemandLitersPerSecond:F3} L/s");

    foreach (var vazao in resultadoRamificado.PipeFlows)
    {
        Console.WriteLine(
            $"{vazao.SegmentName}: {vazao.FlowLitersPerSecond:F3} L/s");
    }

    var vazaoPrincipal = resultadoRamificado.PipeFlows
        .Single(v => v.SegmentId == trechoPrincipalRamificado.Id);

    var vazaoRamalA = resultadoRamificado.PipeFlows
        .Single(v => v.SegmentId == trechoRamalA.Id);

    var vazaoRamalB = resultadoRamificado.PipeFlows
        .Single(v => v.SegmentId == trechoRamalB.Id);

    if (Math.Abs(vazaoPrincipal.FlowLitersPerSecond - 0.50) > 0.000001)
        throw new Exception("Vazao incorreta no trecho principal.");

    if (Math.Abs(vazaoRamalA.FlowLitersPerSecond - 0.30) > 0.000001)
        throw new Exception("Vazao incorreta no Ramal A.");

    if (Math.Abs(vazaoRamalB.FlowLitersPerSecond - 0.20) > 0.000001)
        throw new Exception("Vazao incorreta no Ramal B.");

    if (Math.Abs(resultadoRamificado.TotalDemandLitersPerSecond - 0.50) > 0.000001)
        throw new Exception("Demanda total incorreta na rede ramificada.");

    Console.WriteLine(
        "\nTESTE DE REDE COM DUAS RAMIFICACOES APROVADO!");

    // =========================================
    // TESTE DE SELECAO AUTOMATICA DE DIAMETROS
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE SELECAO AUTOMATICA DE DIAMETROS");
    Console.WriteLine("====================================");

    // Catalogo ficticio de diametros internos em milimetros.

    var catalogoDiametros = new[]
    {
        new PipeDiameterOption("Tubo 10 mm", 10.0),
        new PipeDiameterOption("Tubo 15 mm", 15.0),
        new PipeDiameterOption("Tubo 20 mm", 20.0),
        new PipeDiameterOption("Tubo 25 mm", 25.0)
    };

    double vazaoTeste = 0.50;
    double velocidadeMaxima = 2.0;

    var diametroSelecionado =
        PipeDiameterSelector.SelectByFlow(
            vazaoTeste,
            catalogoDiametros,
            velocidadeMaxima);

    if (diametroSelecionado == null)
        throw new Exception("Nenhum diametro adequado foi encontrado.");

    double velocidadeReal =
        PipeDiameterSelector.CalculateActualVelocity(
            vazaoTeste,
            diametroSelecionado.InternalDiameterMm);

    Console.WriteLine(
        $"Vazao de dimensionamento: {vazaoTeste:F3} L/s");

    Console.WriteLine(
        $"Velocidade maxima adotada: {velocidadeMaxima:F3} m/s");

    Console.WriteLine(
        $"Diametro selecionado: {diametroSelecionado.Description}");

    Console.WriteLine(
        $"Diametro interno: {diametroSelecionado.InternalDiameterMm:F2} mm");

    Console.WriteLine(
        $"Velocidade real: {velocidadeReal:F3} m/s");

    if (diametroSelecionado.InternalDiameterMm != 20.0)
        throw new Exception("O seletor escolheu o diametro incorreto.");

    if (velocidadeReal > velocidadeMaxima)
        throw new Exception("A velocidade ultrapassou o limite adotado.");

    Console.WriteLine(
        "\nTESTE DE SELECAO AUTOMATICA APROVADO!");

    // =========================================
    // TESTE DE AUSENCIA DE DIAMETRO ADEQUADO
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE AUSENCIA DE DIAMETRO ADEQUADO");
    Console.WriteLine("====================================");

    var catalogoInsuficiente = new[]
    {
        new PipeDiameterOption("Tubo 10 mm", 10.0),
        new PipeDiameterOption("Tubo 15 mm", 15.0)
    };

    var nenhumDiametro =
        PipeDiameterSelector.SelectByFlow(
            0.50,
            catalogoInsuficiente,
            2.0);

    Console.WriteLine(
        $"Nenhum diametro adequado: {nenhumDiametro == null}");

    if (nenhumDiametro != null)
        throw new Exception("O seletor aceitou um diametro insuficiente.");

    Console.WriteLine(
        "\nTESTE DE AUSENCIA DE DIAMETRO APROVADO!");

    // =========================================
    // TESTAR LOCALIZACAO
    // =========================================

    var noEncontrado = rede.FindNode(conexao.Id);
    var trechoEncontrado = rede.FindSegment(trecho1.Id);

    Console.WriteLine(
        $"\nLocalizacao do no: {noEncontrado?.Name}");

    Console.WriteLine(
        $"Localizacao do trecho: {trechoEncontrado?.Name}");

    if (noEncontrado == null || trechoEncontrado == null)
        throw new Exception("Falha na localizacao.");

    // =========================================
    // TESTAR PROTECAO DE EXCLUSAO
    // =========================================

    bool exclusaoBloqueada = false;

    try
    {
        rede.RemoveNode(conexao.Id);
    }
    catch (InvalidOperationException)
    {
        exclusaoBloqueada = true;
    }

    Console.WriteLine(
        $"Exclusao de no conectado bloqueada: {exclusaoBloqueada}");

    if (!exclusaoBloqueada)
        throw new Exception("Falha na protecao de exclusao.");

    // =========================================
    // REMOVER TRECHOS
    // =========================================

    bool trecho1Removido = rede.RemoveSegment(trecho1.Id);
    bool trecho2Removido = rede.RemoveSegment(trecho2.Id);

    Console.WriteLine(
        $"Remocao dos trechos: {trecho1Removido && trecho2Removido}");

    if (!trecho1Removido || !trecho2Removido)
        throw new Exception("Falha na remocao dos trechos.");

    // REMOVER NO DESCONECTADO

    bool noRemovido = rede.RemoveNode(conexao.Id);

    Console.WriteLine(
        $"Remocao do no desconectado: {noRemovido}");

    if (!noRemovido)
        throw new Exception("Falha na remocao do no.");

    Console.WriteLine(
        $"Nos restantes: {rede.Nodes.Count}");

    Console.WriteLine(
        $"Trechos restantes: {rede.Segments.Count}");

    if (rede.Nodes.Count != 2 || rede.Segments.Count != 0)
        throw new Exception("Estado final incorreto da rede.");

    Console.WriteLine(
        "\nTESTE DA REDE HIDRAULICA APROVADO!");

    // =========================================
    // TESTE DE INTEGRACAO COM CATALOGO REAL
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE DE INTEGRACAO COM CATALOGO");
    Console.WriteLine("====================================");

    string caminhoCatalogoIntegracao = Path.Combine(
        Path.GetTempPath(),
        "BahrHidro",
        $"pipe-selector-{Guid.NewGuid():N}.json");

    var repositorioIntegracao =
        new PipeCatalogRepository(caminhoCatalogoIntegracao);

    var servicoIntegracao =
        new PipeCatalogService(repositorioIntegracao);

    // Produto ficticio com diametro interno de 15 mm.
    var tubo15 = new PipeProduct
    {
        Manufacturer = "FABRICANTE TESTE",
        ProductLine = "PVC SOLDAVEL",
        Material = PipeMaterial.PvcSoldavel,
        NominalDesignation = "TUBO TESTE 15",
        OuterDiameterMm = 20m,
        WallThicknessMm = 2.5m,
        StandardReference = "DADO FICTICIO",
        SourceDescription = "Produto ficticio para teste"
    };

    // Produto ficticio com diametro interno de 18 mm.
    var tubo18 = new PipeProduct
    {
        Manufacturer = "FABRICANTE TESTE",
        ProductLine = "PVC SOLDAVEL",
        Material = PipeMaterial.PvcSoldavel,
        NominalDesignation = "TUBO TESTE 18",
        OuterDiameterMm = 24m,
        WallThicknessMm = 3m,
        StandardReference = "DADO FICTICIO",
        SourceDescription = "Produto ficticio para teste"
    };

    // Produto ficticio com diametro interno de 20 mm.
    var tubo20 = new PipeProduct
    {
        Manufacturer = "FABRICANTE TESTE",
        ProductLine = "PVC SOLDAVEL",
        Material = PipeMaterial.PvcSoldavel,
        NominalDesignation = "TUBO TESTE 20",
        OuterDiameterMm = 26m,
        WallThicknessMm = 3m,
        StandardReference = "DADO FICTICIO",
        SourceDescription = "Produto ficticio para teste"
    };

    await servicoIntegracao.AddAsync(tubo15);
    await servicoIntegracao.AddAsync(tubo18);
    await servicoIntegracao.AddAsync(tubo20);

    Console.WriteLine("Tres produtos cadastrados no catalogo.");

    var seletorCatalogo =
        new PipeCatalogDiameterSelector(servicoIntegracao);

    double vazaoIntegracao = 0.50;
    double velocidadeLimite = 2.0;

    var selecao = await seletorCatalogo.SelectAsync(
        vazaoIntegracao,
        velocidadeLimite);

    if (selecao == null)
        throw new Exception("Nenhum produto adequado foi selecionado.");

    Console.WriteLine(
        $"Fabricante: {selecao.Product.Manufacturer}");

    Console.WriteLine(
        $"Material: {selecao.Product.Material}");

    Console.WriteLine(
        $"Linha: {selecao.Product.ProductLine}");

    Console.WriteLine(
        $"Designacao: {selecao.Product.NominalDesignation}");

    Console.WriteLine(
        $"Diametro interno: {selecao.Product.InternalDiameterMm:F2} mm");

    Console.WriteLine(
        $"Diametro minimo calculado: {selecao.MinimumRequiredDiameterMm:F2} mm");

    Console.WriteLine(
        $"Velocidade real: {selecao.ActualVelocityMetersPerSecond:F3} m/s");

    if (selecao.Product.Id != tubo18.Id)
        throw new Exception("O produto selecionado foi incorreto.");

    if (selecao.ActualVelocityMetersPerSecond > velocidadeLimite)
        throw new Exception("A velocidade ultrapassou o limite.");

    Console.WriteLine(
        "\nTESTE DE INTEGRACAO COM CATALOGO APROVADO!");

    // =========================================
    // TESTE SEM PRODUTO ADEQUADO NO CATALOGO
    // =========================================

    Console.WriteLine("\n====================================");
    Console.WriteLine(" TESTE SEM PRODUTO ADEQUADO");
    Console.WriteLine("====================================");

    await servicoIntegracao.DeleteAsync(tubo18.Id);
    await servicoIntegracao.DeleteAsync(tubo20.Id);

    var selecaoInsuficiente =
        await seletorCatalogo.SelectAsync(
            vazaoIntegracao,
            velocidadeLimite);

    Console.WriteLine(
        $"Nenhum produto adequado: {selecaoInsuficiente == null}");

    if (selecaoInsuficiente != null)
        throw new Exception("O seletor aceitou um produto insuficiente.");

    Console.WriteLine(
        "\nTESTE SEM PRODUTO ADEQUADO APROVADO!");

    Console.WriteLine(
        "\nTODOS OS TESTES FORAM APROVADOS!");

}

Console.ReadKey();
