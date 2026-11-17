# Assistente SAP

Aplicativo Windows em C# para classificar uma ordem ou solicitação pelo nome e preparar o lançamento no SAP GUI. O aplicativo trabalha com uma frota por operação e usa a sessão já autenticada no SAP GUI para Windows.

## Usar o aplicativo

1. Abra `AssistenteSap.exe`, digite o nome e clique em **Identificar pelo nome** ou pressione Enter.
2. Confira o fluxo sugerido. As opções disponíveis são ordem de serviço, pedido de peça e ordem com requisição de serviço. Se o nome permitir mais de uma interpretação, selecione o fluxo.
3. Complete avaria, recolhimento e os demais campos pedidos. Para gerar uma requisição de serviço vinculada a uma ordem já gravada, informe o número em **Ordem existente**.
4. Selecione o perfil SAP local e clique em **Verificar conexão SAP**. O botão verifica se há uma sessão compatível e em SAP Easy Access.
5. Clique em **Revisar e executar**. Confira os dados e confirme para gravar. Após o SAP confirmar uma ordem nova, o aplicativo envia o registro para a aba **Ordens** e informa o resultado.

O aplicativo registra o número confirmado no histórico local antes de prosseguir. Ao repetir, no mesmo dia, um fluxo com o mesmo nome e equipamento, o aplicativo recupera as operações anteriores que correspondem aos dados. Se o SAP não confirmou o resultado após uma tentativa de gravação, confira no SAP. Use a ordem encontrada como **Ordem existente** quando precisar prosseguir só com a requisição de serviço. Os registros ficam em `%LOCALAPPDATA%\AssistenteSap\historico`.

## Criar ordens em lote

1. Digite o nome comum às ordens e clique em **Identificar pelo nome** para carregar a codificação, a empresa e os demais valores da regra local. Escolha o perfil SAP e confira os dados.
2. Clique em **Criar ordens em lote**. Cole até 100 equipamentos, um por linha. Exemplo de formato `DEMO-0000009`. O aplicativo usa o código inteiro para a IW31 e extrai `9` para a coluna Frota.
3. Revise a lista e confirme uma vez. O lote usa a IW31 para criar uma ordem por equipamento na ordem da lista. A codificação da nota e a liberação devem constar nos passos gravados no perfil. A cada confirmação do SAP, o aplicativo guarda o número e tenta sincronizar a planilha. O fluxo em lote é destinado à criação de ordens.

Se o SAP falhar, o lote para na primeira frota afetada. Para repetir no mesmo dia com o mesmo título e lista, o histórico recupera os números já confirmados e prossegue nas frotas restantes. Se uma gravação ficou sem confirmação, verifique o resultado na IW31 antes de retomar. Se apenas o Google Planilhas falhar, as ordens continuam e ficam aguardando **Sincronizar planilha**.

## Conectar o Google Planilhas

A aba **Ordens** recebe uma linha em A:G com **número da ordem, data de abertura, frota, empresa, status, tipo e nome da ordem**, nessa sequência. A data vem do dia em que o SAP confirmou a ordem no computador. O status padrão é `Aberta`. Empresa, status e tipo podem ser definidos em `regras.local.json` como `empresa`, `statusPlanilha` e `tipoPlanilha`, ou editados na tela antes de gravar. O envio inclui ordens novas que o aplicativo confirmou; requisições e ordens existentes usadas apenas para vínculo seguem seus fluxos normalmente.

1. No Google Apps Script, crie um projeto e cole o conteúdo de `RegistrarOrdens.gs`. Em **Serviços**, adicione **Google Sheets API**.
2. Em **Configurações do projeto > Propriedades do script**, configure `ID_PLANILHA` com o trecho entre `/d/` e `/edit` do endereço da planilha. `ABA_ORDENS` é opcional e tem valor padrão `Ordens`. Configure `CHAVE_PLANILHA` com uma chave aleatória exclusiva, de 32 a 256 caracteres.
3. Publique uma **Nova implantação > Aplicativo da Web**, executada como a conta que edita a planilha. O executável envia sem sessão do Google. A implantação precisa aceitar chamadas sem login e autentica cada envio pela chave. Use essa opção apenas se sua organização permitir. A implantação aceita apenas a função de registrar a ordem na aba configurada. [Implantar um aplicativo da Web](https://developers.google.com/apps-script/guides/web) · [Ativar o serviço avançado](https://developers.google.com/apps-script/advanced/sheets)
4. Copie `planilha.exemplo.json` para `planilha.local.json` ao lado do executável. Informe a URL da implantação terminada em `/exec` e a mesma chave. Guarde a chave e o arquivo local fora do GitHub.

O envio utiliza o número da ordem como chave de deduplicação. A linha é incluída ao final e, se o número já existir na coluna A, a linha existente é preservada. Se a conexão falhar, a ordem fica pendente no histórico. O aplicativo tenta novamente ao abrir, após um fluxo SAP ou quando você clica em **Sincronizar planilha**. A sincronização manual usa apenas o histórico local e a planilha.

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

Para rodar os testes de classificação, histórico e envio simulado sem abrir o SAP:

```powershell
Start-Process .\publish\AssistenteSap.exe -ArgumentList @('--testar', 'resultado-teste.txt') -Wait
Get-Content .\resultado-teste.txt
```
