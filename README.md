# Assistente SAP

Aplicativo Windows em C# para classificar uma ordem ou solicitação pelo nome e preparar o lançamento no SAP GUI. O aplicativo trabalha com uma frota por operação e usa a sessão já autenticada no SAP GUI para Windows.

## Usar o aplicativo

1. Abra `AssistenteSap.exe`, digite o nome e clique em **Identificar pelo nome** ou pressione Enter.
2. Confira o fluxo sugerido. As opções disponíveis são ordem de serviço, pedido de peça e ordem com requisição de serviço. Se o nome permitir mais de uma interpretação, selecione o fluxo.
3. Complete avaria, recolhimento e os demais campos pedidos. Para gerar uma requisição de serviço vinculada a uma ordem já gravada, informe o número em **Ordem existente**.
4. Selecione o perfil SAP local e clique em **Verificar conexão SAP**. O botão verifica se há uma sessão compatível e em SAP Easy Access.
5. Clique em **Revisar e executar**. Confira os dados e confirme para gravar. Os números confirmados aparecem em **Resultado** e podem ser copiados para a planilha.

O aplicativo registra o número confirmado no histórico local antes de prosseguir. Ao repetir, no mesmo dia, um fluxo com o mesmo nome e equipamento, o aplicativo recupera as operações anteriores que correspondem aos dados. Se o SAP não confirmou o resultado após uma tentativa de gravação, confira no SAP. Use a ordem encontrada como **Ordem existente** quando precisar prosseguir só com a requisição de serviço. Os registros ficam em `%LOCALAPPDATA%\AssistenteSap\historico`. A gravação em lote e a atualização direta do Google Planilhas ficam fora desta versão.

## Configurar a classificação

Copie `regras.exemplo.json` para `regras.local.json` ao lado do executável. Use `matchPhrase` para indicar uma expressão no nome. O campo `priority` resolve sobreposições entre regras locais, com preferência pelo maior valor. Os valores de `route` são `ServiceOrder`, `PartRequest` e `ServiceOrderAndRequest`. Avaria, recolhimento e códigos de nota são opcionais em cada regra. Complete os códigos e os padrões usando os dados confirmados pela sua operação. Mantenha arquivos `*.local.json` no computador.

## Configurar as telas do SAP

Copie `sap.exemplo.json` para `sap.local.json` ou escolha outro perfil pelo aplicativo. O exemplo ilustra a estrutura e contém IDs ilustrativos. Use a gravação de scripts do SAP GUI em homologação para obter os IDs e a ordem das ações. Preencha `sistema` e `mandante` e configure as operações que você usará. [Documentação do SAP GUI Scripting](https://help.sap.com/docs/sap_gui_for_windows/b47d018c3b9b45e897faf66a6c0885a8?locale=en-US)

| Operação no perfil | Transação |
| --- | --- |
| `ordem` | `IW31` |
| `requisicaoServico` | `ME51N` |
| `pedidoPeca` | Transação usada pela sua empresa |

Cada operação precisa dos passos de preenchimento, de uma ação `salvar` no fim e do código de sucesso observado no SAP (`mensagemClasse` e `mensagemNumero`). `numeroRegex` deve capturar o documento no grupo `numero`. Para avisos que você confirmou serem esperados, `avisosPermitidos` aceita entradas no formato `CLASSE:123`. Se houver mais de uma sessão, `sessao` identifica explicitamente qual usar.

Use `{{campo}}` em `valor` para inserir um dado da interface. Campos disponíveis: `titulo`, `frota`, `equipamento`, `avaria`, `recolhimento`, `grupoNota`, `codigoNota`, `ordem`, `material`, `quantidade`, `preco`, `centro`, `tipoOrdem`, `empresa`, `grupoCompras`, `organizacaoCompras`, `fornecedor` e `tipoDocumento`. `valoresFixos` aceita padrões específicos daquele lançamento. Valide cada perfil em homologação antes de usá-lo na sessão de produção.

O mapeamento da ordem precisa preencher equipamento e título. A requisição de serviço precisa preencher ordem, material, quantidade, preço e frota. O pedido de peça precisa preencher material e quantidade. As outras ações dependem da configuração da empresa, como seleção do tipo de documento, codificação de nota, liberação da ordem e imputação contábil.

## Compilar

Com o SDK do .NET 10 no Windows:

```powershell
dotnet publish AssistenteSap.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

O executável gerado fica em `publish` e usa o **.NET Desktop Runtime 10 x64**. [Documentação de publicação em arquivo único](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

Para rodar os testes de classificação e histórico sem abrir o SAP:

```powershell
Start-Process .\publish\AssistenteSap.exe -ArgumentList @('--testar', 'resultado-teste.txt') -Wait
Get-Content .\resultado-teste.txt
```
