# IdleForge

IdleForge é um aplicativo Windows para organizar jogos e sites web em múltiplos perfis independentes. Ele combina WPF, .NET 8 e Microsoft Edge WebView2 para manter contas separadas e executar userscripts compatíveis.

> Estado: **Beta 1.3.4**. Faça backup dos dados importantes e instale apenas scripts de fontes confiáveis.

## Principais recursos

- múltiplas contas com perfis WebView2 isolados;
- visualização em grade, foco e miniaturas ativas;
- navegação por conta e retorno ao endereço cadastrado do jogo;
- instalação, ativação, atualização e gerenciamento de userscripts;
- armazenamento GM persistente e execução em mundos isolados quando compatível;
- controle de painéis de userscripts compatíveis;
- gerenciador de downloads;
- modos de desempenho e monitoramento básico de recursos;
- restauração da organização da última sessão e atualização local do aplicativo.

O IdleForge é universal: nenhum jogo, conta ou userscript é incluído na distribuição.

## Requisitos

- Windows 10 ou Windows 11 de 64 bits;
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) instalado;
- acesso à internet para os sites que você decidir abrir.

O pacote `IdleForge-Windows-x64.zip` é publicado como **self-contained** e não exige instalação separada do .NET. Para compilar o código-fonte, é necessário o SDK do .NET 8.

## Baixar e executar

1. Baixe `IdleForge-Windows-x64.zip` na página da versão.
2. Extraia todo o conteúdo para uma pasta gravável.
3. Execute `IdleForge.App.exe`.
4. Se o Windows SmartScreen alertar sobre um aplicativo ainda não assinado, confira a origem e o hash da versão antes de continuar.

Na primeira abertura, o aplicativo cria dados novos em `%LocalAppData%\IdleForge`. O ZIP não contém contas, cookies, bancos pessoais ou userscripts.

## Adicionar jogos e contas

1. Clique em **Adicionar jogo**.
2. Informe um nome, um endereço HTTP/HTTPS e o nome do perfil/conta.
3. Repita o processo para criar outros perfis.
4. Selecione uma conta para focá-la ou use **Grade** para visualizar várias contas.

Cada perfil WebView2 possui armazenamento próprio. Cookies, logins, Local Storage e IndexedDB não são compartilhados entre perfis.

## Importar userscripts

Use **Userscripts → Instalar arquivo .user.js** ou a instalação por URL. Revise o código, os endereços de correspondência e as permissões solicitadas antes de confirmar.

Userscripts têm capacidade de ler e alterar páginas compatíveis. Um script malicioso pode expor dados exibidos no site. Instale apenas código que você compreenda e em que confie. O IdleForge não garante compatibilidade completa com todas as APIs de Tampermonkey/Greasemonkey.

## Limitações conhecidas

- alguns serviços, especialmente autenticação de terceiros, podem bloquear login em navegadores incorporados;
- Shadow DOM fechado e interfaces incomuns podem limitar a detecção de painéis;
- a continuidade de timers em segundo plano depende também das políticas do Chromium, do Windows e do próprio site;
- uma página descarregada por navegação não pode ter seu estado JavaScript temporário restaurado;
- esta Beta ainda não possui assinatura digital de código;
- compatibilidade de userscripts varia conforme permissões e APIs utilizadas.

## Compilar o código-fonte

Pré-requisito: SDK do .NET 8 e Windows x64.

```powershell
dotnet restore .\IdleForge.sln
dotnet test .\tests\IdleForge.Core.Tests\IdleForge.Core.Tests.csproj -c Release
dotnet publish .\src\IdleForge.App\IdleForge.App.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish\win-x64
dotnet publish .\src\IdleForge.Updater\IdleForge.Updater.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish\updater
```

## Privacidade

Os dados operacionais ficam fora da pasta do aplicativo, em `%LocalAppData%\IdleForge`. Não inclua essa pasta, perfis WebView2, bancos SQLite, logs, downloads ou userscripts pessoais em relatórios ou distribuições.

## Licença

O IdleForge é disponibilizado sob a [Apache License 2.0](LICENSE). Ela permite uso, modificação e redistribuição, inclusive comercial, desde que seus termos e avisos sejam preservados.

