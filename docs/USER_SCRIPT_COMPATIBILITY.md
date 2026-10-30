# Matriz de compatibilidade de userscripts

Legenda: **S** suportado, **P** parcial, **N** ainda não suportado.

| Recurso | Estado | Observação |
|---|---:|---|
| `@name`, `@version`, `@namespace` | S | Parse e exibição |
| `@description`, `@author` | S | Parse |
| `@match`, `@include`, `@exclude` | P | Curingas e regex de include; casos especiais avançados ainda precisam de conformidade Tampermonkey |
| `@run-at` | S | start, body, end e idle |
| `@noframes` | S | Bloqueio em subframes |
| `@grant` | P | APIs de armazenamento só são expostas quando declaradas; demais grants ainda pendentes |
| `@require` | P | Parse e risco; download/cache seguro ainda pendente |
| `@resource` | P | Parse; APIs de leitura pendentes |
| `@connect` | P | Parse e confirmação; requisição privilegiada pendente |
| `@updateURL`, `@downloadURL` | P | Parse; atualizador pendente |
| `GM_getValue`, `GM_setValue` | S | SQLite isolado por script/perfil; gravação no host é assíncrona |
| `GM_deleteValue`, `GM_listValues` | S | Escopo isolado por script/perfil |
| `GM.getValue`, `GM.setValue` | S | Variantes Promise básicas |
| `GM_xmlhttpRequest` / `GM.xmlHttpRequest` | N | Exige ponte mediada e política `@connect` |
| `GM_registerMenuCommand` | N | Exige integração com UI por sessão |
| `GM_getResourceText/URL` | N | Depende de cache verificado de recursos |
| `GM_download`, clipboard, notifications | N | APIs privilegiadas deliberadamente bloqueadas |
| `unsafeWindow` | N | Userscripts agora usam mundo CDP isolado; proxy controlado para o mundo principal ainda pendente |
| Greasy Fork/OpenUserJS por URL | N | Importação de arquivo funciona; cliente de loja e atualização ainda pendentes |

## Limitação importante

O runtime usa um `worldName` CDP exclusivo por script/perfil, com binding privilegiado restrito ao execution context. O DOM é compartilhado, mas globais JavaScript não são. Isso melhora o isolamento sem equivaler integralmente ao sandbox do Tampermonkey. Recursos não listados como suportados falham fechados.
