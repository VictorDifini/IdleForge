# Atualizações locais e restauração de sessão

## Primeira migração

Execute uma vez a Release que já contém `IdleForge.Updater.exe`, preferencialmente a partir de uma pasta permanente com permissão de escrita. Os dados pessoais não ficam nessa pasta: banco SQLite, perfis WebView2, downloads e preferências permanecem em `%LocalAppData%\IdleForge`.

Não copie nem apague essa pasta de dados. Uma instalação anterior que já utilizava `%LocalAppData%\IdleForge` é reconhecida automaticamente.

## Gerar um pacote futuro

Na raiz do projeto:

```powershell
.\tools\New-IdleForgeUpdate.ps1 -Version 1.2.0 -OutputRoot C:\IdleForge-Versoes -DotNet .\work\.dotnet\dotnet.exe
```

O script publica o aplicativo e o atualizador self-contained, calcula SHA-256 de todos os arquivos e somente torna a pasta visível depois que o pacote está completo. Não altere arquivos depois da criação: a verificação de integridade bloqueará o pacote.

## Atualizar com um clique

1. Abra **Configurações** no IdleForge.
2. Selecione `C:\IdleForge-Versoes` (ou diretamente a pasta do pacote).
3. Clique em **Verificar atualizações** e depois em **Atualizar agora**.

O IdleForge grava o layout atomicamente, copia e revalida o pacote em `%LocalAppData%`, encerra, faz backup da instalação, instala e reinicia. A nova versão precisa confirmar a inicialização em até 45 segundos; caso contrário, o atualizador restaura a versão anterior.

## Recuperação

- A restauração da última sessão vem ativada e reabre apenas os perfis anteriormente abertos, reutilizando os mesmos `ProfileName` e dados WebView2.
- O estado da janela, grade/foco, Miniaturas ativas, zoom e modo de desempenho é salvo com debounce e arquivo de backup.
- Depois de duas tentativas consecutivas de restauração sem conclusão, a próxima abertura usa inicialização segura, sem reabrir contas automaticamente.
- Backups de versões ficam em `%LocalAppData%\IdleForge\UpdateBackups`; o botão **Restaurar versão anterior** usa o backup mais recente.

Pacotes locais usam integridade SHA-256, não assinatura de editor. Distribuição pública futura deve acrescentar assinatura Authenticode e uma origem autenticada antes de habilitar atualizações online.
