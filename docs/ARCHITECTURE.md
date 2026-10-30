# Arquitetura e pesquisa técnica

## Benchmark público: IdleDeck

A documentação pública descreve sessões isoladas, execução contínua, recuperação de quedas, alertas, grupos/rotação, grade/foco/dashboard, cofre local, textos observados e instalação de extensões/userscripts. IdleForge usa essa categoria de produto como referência funcional, sem copiar código, textos, marca ou identidade visual.

Fontes consultadas:

- https://idledeck.com.br/
- https://idledeck.com.br/termos
- https://idledeck.com.br/privacidade

## Alternativas

| Critério | WPF + WebView2 | WPF + CefSharp/CEF | Electron | Tauri |
|---|---|---|---|---|
| Chromium moderno | Evergreen do Edge | CEF empacotado | Chromium empacotado | WebView do SO |
| Tamanho da distribuição | baixo | alto | alto | baixo |
| Perfis isolados | API nativa `ProfileName` | `RequestContext`/cache por contexto | partições de sessão | dependente da plataforma |
| Compartilhamento do processo-base | um ambiente/UDF | possível, exige desenho CEF | processo Electron compartilhado | dependente do WebView |
| Integração Windows/.NET | excelente | excelente | indireta | indireta/Rust |
| Canvas/WebGL | Chromium/Edge | Chromium/CEF | Chromium | variável por SO |
| Atualizações de segurança | Runtime Evergreen | responsabilidade do aplicativo | atualizar Electron | runtime do SO |
| Portabilidade | Windows | Windows (no desenho atual) | alta | alta |

## Decisão

O primeiro backend é C#/.NET 8/WPF/WebView2. Um único diretório de dados contém múltiplos perfis. Cada `ProfileName` mantém cookies, cache, permissões e armazenamento separados, enquanto controles do mesmo ambiente podem compartilhar o processo de navegador e serviços auxiliares. Isso reduz a duplicação de processos observada quando se usa um `UserDataFolder` por conta.

O domínio não referencia WebView2. `IGameBrowserHost` delimita o adaptador; persistência, jogos, perfis, scripts e permissões permanecem reutilizáveis caso um backend CEF ou multiplataforma seja adicionado.

Fontes técnicas:

- https://learn.microsoft.com/microsoft-edge/webview2/concepts/multi-profile-support
- https://learn.microsoft.com/microsoft-edge/webview2/concepts/process-model
- https://learn.microsoft.com/microsoft-edge/webview2/concepts/performance
- https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.wpf.corewebview2creationproperties.profilename
- https://github.com/cefsharp/CefSharp/wiki/General-Usage

## Recursos e segundo plano

- Controles permanecem vivos ao alternar de sessão; apenas a árvore visual muda.
- `TrySuspendAsync` é uma ação explícita para perfis que não devem continuar calculando.
- Perfis em farming não são suspensos automaticamente.
- A distribuição usa o comportamento suportado do WebView2 e aceita throttling de controles ocultos. Flags experimentais contra throttling existem apenas no harness; a Microsoft desaconselha flags de navegador em produção.
- Falhas de processo são observadas por `ProcessFailed`.
- O próximo benchmark deve medir working set privado, CPU, tempo de ativação e estabilidade com 1, 5, 10 e 25 perfis, usando jogos Canvas, DOM e WebGL controlados.

Não existe otimização universal que preserve toda lógica de um jogo e simultaneamente elimine seu uso de CPU. A política deve ser configurável: ativo, visível, ocioso com execução, ou suspenso.

## Segurança

O WebView não recebe host objects .NET. Cada userscript é registrado por CDP em um `worldName` exclusivo e recebe um binding CDP próprio. O DOM permanece acessível, mas globais e APIs GM não aparecem no mundo principal. O armazenamento GM usa SQLite delimitado por script/perfil. O bridge WebMessage com capability permanece apenas como fallback quando o CDP isolado não estiver disponível.
