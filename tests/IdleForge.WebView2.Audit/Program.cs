using IdleForge.App.Browser;
using IdleForge.App.Downloads;
using IdleForge.Core;
using IdleForge.Core.Persistence;
using IdleForge.Core.UserScripts;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace IdleForge.WebView2.Audit;

internal static class Program
{
    private static int _exitCode = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        var profileCount = args.Length > 0 && int.TryParse(args[0], out var requested) ? Math.Clamp(requested, 1, 25) : 10;
        var withScript = !args.Contains("--no-script", StringComparer.OrdinalIgnoreCase);
        var experimentalBackground = args.Contains("--experimental-background", StringComparer.OrdinalIgnoreCase);
        var soakSeconds = args.Select(x => x.StartsWith("--soak-seconds=", StringComparison.OrdinalIgnoreCase) && int.TryParse(x[15..], out var value) ? value : 0).DefaultIfEmpty(0).Max();
        var validationSeconds = args.Select(x => x.StartsWith("--validation-seconds=", StringComparison.OrdinalIgnoreCase) && int.TryParse(x[21..], out var value) ? value : 0).DefaultIfEmpty(0).Max();
        var validationRepeats = args.Select(x => x.StartsWith("--validation-repeats=", StringComparison.OrdinalIgnoreCase) && int.TryParse(x[21..], out var value) ? value : 0).DefaultIfEmpty(0).Max();
        var memoryMode = args.FirstOrDefault(x => x.StartsWith("--memory-mode=", StringComparison.OrdinalIgnoreCase))?[14..] ?? "normal";
        var panelTracking = !args.Contains("--no-panel-tracking", StringComparer.OrdinalIgnoreCase);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAuditAsync(profileCount, withScript, experimentalBackground, soakSeconds, validationSeconds, validationRepeats, memoryMode, panelTracking); _exitCode = 0; }
            catch (Exception ex) { Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = ex.ToString() })); }
            finally { app.Shutdown(_exitCode); }
        };
        app.Run(); return _exitCode;
    }

    private static async Task RunAuditAsync(int profileCount, bool withScript, bool experimentalBackground, int soakSeconds, int validationSeconds, int validationRepeats, string memoryMode, bool panelTracking)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        await using var server = new LocalServer(port); server.Start();
        var root = Path.Combine(Path.GetTempPath(), "idleforge-webview-audit", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var repository = new IdleForgeRepository(Path.Combine(root, "audit.db")); await repository.InitializeAsync();
        var scriptId = Guid.NewGuid();
        var script = new UserScript(scriptId, new UserScriptMetadata { Name = "Isolation probe", Version = "1", Matches = [$"http://127.0.0.1:{port}/*"], Grants = ["GM_getValue", "GM_setValue", "GM.getValue", "GM.setValue", "GM_download", "GM.download"], RunAt = ScriptRunAt.DocumentEnd, NoFrames = true },
            "document.documentElement.dataset.scriptRuns=String(Number(document.documentElement.dataset.scriptRuns||0)+1);document.documentElement.dataset.userTicks='0';setInterval(()=>document.documentElement.dataset.userTicks=String(Number(document.documentElement.dataset.userTicks||0)+1),100);GM_setValue('owner', new URL(location.href).searchParams.get('id')); globalThis.__isolationSecret='not-visible'; document.documentElement.dataset.gmProbe=GM_getValue('owner'); (async()=>{await GM.setValue('durable','yes');document.documentElement.dataset.gmDurable='acknowledged';await GM.download({url:'data:text/plain;base64,R00uZG93bmxvYWQ=',name:'gm-'+new URL(location.href).searchParams.get('id')+'.txt'});document.documentElement.dataset.gmDownload='ok'})();", true, null, DateTimeOffset.UtcNow);
        var noGrant = script with { Metadata = script.Metadata with { Grants = [] } };
        Ensure(!UserScriptBootstrapBuilder.Build(noGrant, Guid.NewGuid(), "test", new Dictionary<string, JsonElement>()).Contains("const GM_getValue=get", StringComparison.Ordinal), "API GM foi exposta sem @grant.");
        var rawScript = new UserScript(Guid.NewGuid(), script.Metadata with { Name = "Main world probe", Sandbox = "raw", Grants = [] },
            "globalThis.__idleforgeMainWorldProbe=(globalThis.__idleforgeMainWorldProbe||0)+1;document.documentElement.dataset.mainWorldProbe='ready';", true, null, DateTimeOffset.UtcNow, MainWorldCompatibilityApproved: true);
        var rawPanelScript = new UserScript(Guid.NewGuid(), script.Metadata with { Name = "Generic raw Shadow panel", Sandbox = "raw", Grants = [] }, """
            const GENERIC_ROOT='unrelated-dashboard-shell';document.documentElement.dataset.rawPanelTicks='0';setInterval(()=>document.documentElement.dataset.rawPanelTicks=String(Number(document.documentElement.dataset.rawPanelTicks||0)+1),100);
            globalThis.__recreateRawPanel=()=>{document.getElementById(GENERIC_ROOT)?.remove();const shell=document.createElement('div');shell.id=GENERIC_ROOT;const root=shell.attachShadow({mode:'open'});root.innerHTML='<section style="position:fixed;right:15px;top:15px;width:210px;height:120px;background:#234"><h2>Generic dashboard</h2><button>action</button></section>';document.body.appendChild(shell)};globalThis.__recreateRawPanel();
            """, true, null, DateTimeOffset.UtcNow, MainWorldCompatibilityApproved: true);
        var panelScript = new UserScript(Guid.NewGuid(), script.Metadata with { Name = "Synthetic panel suite", Grants = [], RunAt = ScriptRunAt.DocumentEnd }, """
            document.documentElement.dataset.panelTicks='0';setInterval(()=>document.documentElement.dataset.panelTicks=String(Number(document.documentElement.dataset.panelTicks||0)+1),100);
            const make=(id,left)=>{const p=document.createElement('section');p.id=id;p.style.cssText=`position:fixed;left:${left}px;top:20px;width:180px;height:100px;z-index:9999;background:rgb(20,30,40)`;const title=document.createElement('h3');title.textContent=id;const b=document.createElement('button');b.textContent='toggle';b.onclick=()=>document.documentElement.dataset.panelClicks=String(Number(document.documentElement.dataset.panelClicks||0)+1);const live=document.createElement('span');setInterval(()=>live.textContent=String(Date.now()),100);p.append(title,b,live);document.body.append(p);setInterval(()=>p.style.backgroundColor=p.style.backgroundColor==='rgb(20, 30, 40)'?'rgb(30, 40, 50)':'rgb(20, 30, 40)',250);return p};
            make('synthetic-panel',20);make('synthetic-panel-2',220);
            const host=document.createElement('div');host.id='synthetic-shadow';host.style.cssText='position:fixed;left:420px;top:20px;width:180px;height:100px;z-index:9999';const shadow=host.attachShadow({mode:'open'});const inside=document.createElement('div');inside.style.cssText='position:absolute;inset:0;background:#234';inside.innerHTML='<button>shadow</button>';shadow.append(inside);document.body.append(host);
            setTimeout(()=>make('synthetic-delayed',620),1500);
            """, true, null, DateTimeOffset.UtcNow);
        var secondPanelScript = new UserScript(Guid.NewGuid(), script.Metadata with { Name = "Second synthetic panel", Grants = [], RunAt = ScriptRunAt.DocumentEnd }, "const p=document.createElement('aside');p.id='second-script-panel';p.style.cssText='position:fixed;right:10px;bottom:10px;width:140px;height:70px;z-index:9000;background:#345';p.textContent='second';document.body.append(p);", true, null, DateTimeOffset.UtcNow);
        var noUiScript = new UserScript(Guid.NewGuid(), script.Metadata with { Name = "No interface", Grants = [], RunAt = ScriptRunAt.DocumentEnd }, "setInterval(()=>document.documentElement.dataset.noUiTicks=String(Number(document.documentElement.dataset.noUiTicks||0)+1),100);", true, null, DateTimeOffset.UtcNow);
        await repository.SaveScriptAsync(script);
        var windows = new List<Window>(); var hosts = new List<WebView2GameHost>(); var profiles = new List<GameProfile>();
        var downloads = new DownloadManager(root); var downloadFolder = Path.Combine(root, "downloads"); downloads.UpdatePreferences(downloadFolder, false);
        try
        {
            for (var i = 0; i < profileCount; i++)
            {
                var game = new GameDefinition(Guid.NewGuid(), "Audit", new($"http://127.0.0.1:{port}/?id={i}"));
                var profile = new GameProfile(Guid.NewGuid(), game.Id, $"P{i}", $"audit-{Guid.NewGuid():N}"); profiles.Add(profile);
                var host = new WebView2GameHost(repository, Path.Combine(root, "browser"), experimentalBackground, downloads, panelTracking); hosts.Add(host);
                var window = new Window { Content = host, Width = 320, Height = 240, Left = -5000 + i, Top = -5000, ShowInTaskbar = false, WindowStyle = WindowStyle.None }; windows.Add(window); window.Show();
                await host.StartAsync(game, profile, withScript ? [script, rawScript, rawPanelScript, panelScript, secondPanelScript, noUiScript] : []);
            }
            await EventuallyAsync(async () => (await Task.WhenAll(hosts.Select(x => x.ExecuteScriptForDiagnosticsAsync("document.readyState")))).All(x => x == "\"complete\""), TimeSpan.FromSeconds(30));
            await EventuallyAsync(async () => (await Task.WhenAll(hosts.Select(x => x.ExecuteScriptForDiagnosticsAsync("globalThis.__featuresReady??false")))).All(x => x == "true"), TimeSpan.FromSeconds(15));
            if (withScript && panelTracking)
            {
                await Task.Delay(2200); var panels = await hosts[0].DiscoverPanelsAsync(panelScript); Ensure(panels.Count >= 4 && panels.Any(x => x.Label.Contains("synthetic-shadow")) && panels.Any(x => x.Label.Contains("synthetic-delayed")), $"Painéis comuns, Shadow DOM ou tardios não foram associados: {JsonSerializer.Serialize(panels)}");
                Ensure((await hosts[0].DiscoverPanelsAsync(secondPanelScript)).Count == 1, "Painel do segundo script não foi separado."); Ensure((await hosts[0].DiscoverPanelsAsync(noUiScript)).Count == 0, "Script sem interface recebeu painel fictício.");
                var rawPanels = await hosts[0].DiscoverPanelsAsync(rawPanelScript); Ensure(rawPanels.Count == 1 && rawPanels[0].Label.Contains("unrelated-dashboard-shell"), $"Painel @sandbox raw com Shadow DOM não foi identificado por proveniência estática: {JsonSerializer.Serialize(rawPanels)}");
                var rawTicksBefore = await ReadIntAsync(hosts[0], "Number(document.documentElement.dataset.rawPanelTicks||0)"); await hosts[0].SetPanelVisibilityAsync(rawPanels[0].Selector, false); await Task.Delay(600); var rawTicksAfter = await ReadIntAsync(hosts[0], "Number(document.documentElement.dataset.rawPanelTicks||0)"); Ensure(rawTicksAfter - rawTicksBefore >= 5, "Ocultar painel raw interrompeu sua execução.");
                await hosts[0].ExecuteScriptForDiagnosticsAsync("globalThis.__recreateRawPanel()"); await Task.Delay(300); var recreatedRaw = await hosts[0].DiscoverPanelsAsync(rawPanelScript); Ensure(recreatedRaw.Count == 1, "Painel raw recriado não foi reassociado automaticamente.");
                Ensure(!panels.Any(x => x.Label.Contains("game-safe", StringComparison.OrdinalIgnoreCase)), "Elemento legítimo do jogo foi atribuído ao userscript.");
                var panelTicksBefore = await ReadIntAsync(hosts[0], "Number(document.documentElement.dataset.panelTicks||0)"); await hosts[0].SetPanelVisibilityAsync(panels[0].Selector, false); await Task.Delay(5000);
                var panelTicksAfter = await ReadIntAsync(hosts[0], "Number(document.documentElement.dataset.panelTicks||0)"); Ensure(panelTicksAfter - panelTicksBefore >= 48, "Ocultar painel por período prolongado interrompeu timers do script.");
                Ensure(await hosts[0].ExecuteScriptForDiagnosticsAsync("getComputedStyle(document.querySelector('#synthetic-panel')).visibility") == "\"hidden\"", "Painel não ficou visualmente oculto.");
                Ensure(await hosts[1].ExecuteScriptForDiagnosticsAsync("getComputedStyle(document.querySelector('#synthetic-panel')).visibility") == "\"visible\"", "Visibilidade vazou entre perfis.");
                await hosts[0].SetPanelVisibilityAsync(panels[0].Selector, true); await hosts[0].ExecuteScriptForDiagnosticsAsync("document.querySelector('#synthetic-panel button').click()"); Ensure(await ReadIntAsync(hosts[0], "Number(document.documentElement.dataset.panelClicks||0)") == 1, "Botão perdeu evento após reexibição.");
                var binding = new ScriptPanelBinding(Guid.NewGuid(), panelScript.Id, profiles[0].Id, panels[0].Selector, panels[0].Label, true, PanelVisibilityPreference.StartHidden, false, DateTimeOffset.UtcNow); await repository.SavePanelBindingAsync(binding); await hosts[0].ReloadAsync();
                await EventuallyAsync(async () => await hosts[0].ExecuteScriptForDiagnosticsAsync("document.querySelector('#synthetic-panel')?getComputedStyle(document.querySelector('#synthetic-panel')).visibility:null") == "\"hidden\"", TimeSpan.FromSeconds(10));
                var manualTask = hosts[0].SelectPanelManuallyAsync(); await Task.Delay(300); await hosts[0].ExecuteScriptForDiagnosticsAsync("document.querySelector('#synthetic-panel-2').click()"); var manual = await manualTask; Ensure(manual?.Selector.Contains("synthetic-panel-2", StringComparison.Ordinal) == true, "Seleção manual não retornou o painel clicado.");
            }
            if (withScript) await EventuallyAsync(async () => (await Task.WhenAll(hosts.Select(x => x.ExecuteScriptForDiagnosticsAsync("document.documentElement.dataset.gmDownload??null")))).All(x => x == "\"ok\""), TimeSpan.FromSeconds(15));
            Ensure(hosts.Count >= 4, "A auditoria de downloads requer quatro perfis.");
            await hosts[0].ExecuteScriptForDiagnosticsAsync("(()=>{const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([JSON.stringify({ok:true})],{type:'application/json'}));a.download='blob.json';a.click();return true})()");
            await hosts[1].ExecuteScriptForDiagnosticsAsync("(()=>{const a=document.createElement('a');a.href='data:text/csv,a%2Cb%0A1%2C2';a.download='data.csv';a.click();return true})()");
            await hosts[2].ExecuteScriptForDiagnosticsAsync("(()=>{const a=document.createElement('a');a.href=location.origin+'/?download=1';a.download='html.txt';a.click();return true})()");
            await hosts[3].ExecuteScriptForDiagnosticsAsync("(()=>{const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([new Uint8Array([80,75,3,4])],{type:'application/zip'}));a.download='sample.zip';a.click();return true})()");
            await downloads.SaveDataUrlAsync("P0", "duplicate-a", new Uri("data:text/plain,duplicate"), "duplicate.txt");
            await downloads.SaveDataUrlAsync("P1", "duplicate-b", new Uri("data:text/plain,duplicate"), "duplicate.txt");
            await Task.Delay(5000);
            Ensure(downloads.Items.Count(x => x.State == "Concluído") >= profileCount + 6, $"Downloads não concluíram: {string.Join(" | ", downloads.Items.Select(x => $"{x.FileName}:{x.State}:{x.Error}"))}");
            Ensure(File.ReadAllText(Path.Combine(downloadFolder, "blob.json")) == "{\"ok\":true}", "Conteúdo JSON Blob divergiu.");
            Ensure(File.ReadAllText(Path.Combine(downloadFolder, "data.csv")) == "a,b\n1,2", "Conteúdo CSV data: divergiu.");
            Ensure(File.ReadAllBytes(Path.Combine(downloadFolder, "sample.zip")).SequenceEqual(new byte[] { 80, 75, 3, 4 }), "Conteúdo binário Blob divergiu.");
            Ensure(File.Exists(Path.Combine(downloadFolder, "duplicate.txt")) && File.Exists(Path.Combine(downloadFolder, "duplicate (2).txt")), "Nomes duplicados foram sobrescritos.");
            using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); try { await downloads.SaveDataUrlAsync("P0", "cancel", new Uri("data:text/plain,cancel"), "cancel.txt", canceled.Token); throw new InvalidOperationException("Cancelamento não foi aplicado."); } catch (OperationCanceledException) { } }
            var blocker = Path.Combine(root, "not-a-folder"); await File.WriteAllTextAsync(blocker, "x"); downloads.UpdatePreferences(blocker, false);
            try { await downloads.SaveDataUrlAsync("P0", "write-failure", new Uri("data:text/plain,fail"), "fail.txt"); throw new InvalidOperationException("Falha de gravação não foi detectada."); } catch (IOException) { }
            downloads.UpdatePreferences(downloadFolder, false);
            var importPath = Path.Combine(root, "diagnostic-import.json"); await File.WriteAllTextAsync(importPath, "{\"learn\":42}");
            await hosts[0].SetFileInputForDiagnosticsAsync("#import-file", importPath);
            await hosts[0].ExecuteScriptForDiagnosticsAsync("document.querySelector('#import-file').dispatchEvent(new Event('change',{bubbles:true}))");
            await EventuallyAsync(async () => await hosts[0].ExecuteScriptForDiagnosticsAsync("document.documentElement.dataset.imported??null") == "\"{\\\"learn\\\":42}\"", TimeSpan.FromSeconds(5));
            if (withScript) Ensure(await hosts[0].ExecuteScriptForDiagnosticsAsync("document.querySelector('iframe').contentDocument.documentElement.dataset.gmProbe??null") == "null", "@noframes permitiu execução no iframe.");
            if (withScript) { var rawProbe = await hosts[0].ExecuteScriptForDiagnosticsAsync("({global:globalThis.__idleforgeMainWorldProbe??null,dom:document.documentElement.dataset.mainWorldProbe??null})"); Ensure(rawProbe.Contains("\"global\":1") && rawProbe.Contains("\"dom\":\"ready\""), $"Script @sandbox raw não executou no mundo principal: {rawProbe}"); }
            if (withScript) await EventuallyAsync(async () => (await Task.WhenAll(hosts.Select(x => x.ExecuteScriptForDiagnosticsAsync("document.documentElement.dataset.gmDurable??null")))).All(x => x == "\"acknowledged\""), TimeSpan.FromSeconds(15));

            for (var i = 0; i < hosts.Count; i++)
            {
                var js = """(async()=>{document.cookie='identity=__ID__; path=/';localStorage.setItem('identity','__ID__');sessionStorage.setItem('identity','__ID__');await new Promise((ok,bad)=>{const r=indexedDB.open('audit',1);r.onupgradeneeded=()=>r.result.createObjectStore('state');r.onerror=()=>bad(r.error);r.onsuccess=()=>{const t=r.result.transaction('state','readwrite');t.objectStore('state').put('__ID__','identity');t.oncomplete=ok;t.onerror=()=>bad(t.error)}});return true})()""".Replace("__ID__", i.ToString());
                await hosts[i].ExecuteScriptForDiagnosticsAsync(js);
            }
            await Task.Delay(1000);

            for (var i = 0; i < hosts.Count; i++)
            {
                await hosts[i].ExecuteScriptForDiagnosticsAsync("window.__idbRead=null;{const r=indexedDB.open('audit');r.onsuccess=()=>{const q=r.result.transaction('state').objectStore('state').get('identity');q.onsuccess=()=>window.__idbRead=q.result}};true");
                await EventuallyAsync(async () => await hosts[i].ExecuteScriptForDiagnosticsAsync("window.__idbRead!==null") == "true", TimeSpan.FromSeconds(5));
                const string read = "({cookie:document.cookie,local:localStorage.getItem('identity'),session:sessionStorage.getItem('identity'),idb:window.__idbRead,gm:document.documentElement.dataset.gmProbe??null,isolationSecret:globalThis.__isolationSecret??null,leaked:Object.keys(localStorage).filter(k=>k.includes('idleforge'))})";
                var raw = await hosts[i].ExecuteScriptForDiagnosticsAsync(read); using var doc = JsonDocument.Parse(raw); var state = doc.RootElement;
                Ensure(state.GetProperty("cookie").GetString()?.Contains($"identity={i}") == true, $"Cookie incorreto no perfil {i}: {raw}");
                Ensure(state.GetProperty("local").GetString() == i.ToString(), $"LocalStorage incorreto no perfil {i}: {raw}");
                Ensure(state.GetProperty("session").GetString() == i.ToString(), $"SessionStorage incorreto no perfil {i}: {raw}");
                Ensure(state.GetProperty("idb").GetString() == i.ToString(), $"IndexedDB incorreto no perfil {i}: {raw}");
                if (withScript)
                {
                    Ensure(state.GetProperty("gm").GetString() == i.ToString(), $"GM indisponível no perfil {i}: {raw}");
                    Ensure(state.GetProperty("isolationSecret").ValueKind == JsonValueKind.Null, $"Global do userscript vazou para o mundo da página no perfil {i}.");
                }
                Ensure(state.GetProperty("leaked").GetArrayLength() == 0, $"Dados GM vazaram para localStorage no perfil {i}.");
                if (withScript)
                {
                    var stored = await repository.GetScriptValuesAsync(scriptId, profiles[i].Id);
                    Ensure(stored.TryGetValue("owner", out var owner) && owner.GetString() == i.ToString(), $"Persistência GM incorreta no perfil {i}.");
                    Ensure(stored.TryGetValue("durable", out var durable) && durable.GetString() == "yes", $"Confirmação assíncrona GM ocorreu sem persistência no perfil {i}.");
                }
            }

            var pids = hosts.Select(x => x.BrowserProcessId).Distinct().ToArray(); Ensure(pids.Length == 1 && pids[0] != 0, $"Processos-base distintos: {string.Join(',', pids)}");
            var features = await hosts[0].ExecuteScriptForDiagnosticsAsync("({canvas:globalThis.__canvasOk,webgl:globalThis.__webglOk,serviceWorker:globalThis.__serviceWorkerOk,webSocket:globalThis.__ws?.readyState===1})");
            using (var featureDoc = JsonDocument.Parse(features))
            {
                foreach (var property in featureDoc.RootElement.EnumerateObject()) Ensure(property.Value.GetBoolean(), $"Recurso web falhou: {property.Name}");
            }
            if (withScript)
            {
                await hosts[0].ExecuteScriptForDiagnosticsAsync("chrome.webview.postMessage({channel:'idleforge-gm-v1',capability:'INVALID',op:'set',key:'intrusion',value:'x'})"); await Task.Delay(200);
                Ensure(!(await repository.GetScriptValuesAsync(scriptId, profiles[0].Id)).ContainsKey("intrusion"), "Mensagem sem capability alterou armazenamento GM.");
                foreach (var host in hosts) { await host.ExecuteScriptForDiagnosticsAsync("delete document.documentElement.dataset.gmProbe"); await host.ReloadAsync(); }
                await EventuallyAsync(async () => (await Task.WhenAll(hosts.Select(x => x.ExecuteScriptForDiagnosticsAsync("document.documentElement.dataset.gmProbe??null")))).All(x => x != "null"), TimeSpan.FromSeconds(15));
                for (var i = 0; i < hosts.Count; i++) Ensure(JsonSerializer.Deserialize<string>(await hosts[i].ExecuteScriptForDiagnosticsAsync("document.documentElement.dataset.gmProbe")) == i.ToString(), $"GM não persistiu após recarga no perfil {i}.");
                var otherNavigationBefore = hosts[1].NavigationCount; var updatedProbe = script with { Metadata = script.Metadata with { Version = "2" }, Source = script.Source + "\n;document.documentElement.dataset.updatedRegistration=String(Number(document.documentElement.dataset.updatedRegistration||0)+1);" };
                await hosts[0].ApplyScriptsAndReloadAsync([updatedProbe]); await EventuallyAsync(async () => await hosts[0].ExecuteScriptForDiagnosticsAsync("document.documentElement.dataset.updatedRegistration??null") == "\"1\"", TimeSpan.FromSeconds(15));
                Ensure(hosts[1].NavigationCount == otherNavigationBefore, "Atualizar uma conta recarregou outra conta."); Ensure((await repository.GetScriptValuesAsync(scriptId, profiles[0].Id)).ContainsKey("durable"), "Atualização removeu dados GM.");
                Ensure(await ReadIntAsync(hosts[0], "Number(document.documentElement.dataset.scriptRuns||0)") == 1, "Registro atualizado executou mais de uma vez.");
                if (hosts.Count >= 4)
                {
                    var beforeSelective = hosts.Select(x => x.NavigationCount).ToArray(); await hosts[0].ApplyScriptsAndReloadAsync([script]); await hosts[2].ApplyScriptsAndReloadAsync([script]);
                    await EventuallyAsync(async () => await hosts[0].ExecuteScriptForDiagnosticsAsync("document.readyState") == "\"complete\"" && await hosts[2].ExecuteScriptForDiagnosticsAsync("document.readyState") == "\"complete\"", TimeSpan.FromSeconds(15));
                    Ensure(await ReadIntAsync(hosts[0], "Number(document.documentElement.dataset.scriptRuns||0)") == 1 && await ReadIntAsync(hosts[2], "Number(document.documentElement.dataset.scriptRuns||0)") == 1, "Recarga seletiva não executou uma única instância por conta.");
                    Ensure(hosts[1].NavigationCount == beforeSelective[1] && hosts[3].NavigationCount == beforeSelective[3], "Recarga seletiva navegou contas não selecionadas.");
                    Ensure((await repository.GetScriptValuesAsync(scriptId, profiles[0].Id)).ContainsKey("durable") && (await repository.GetScriptValuesAsync(scriptId, profiles[2].Id)).ContainsKey("durable"), "Recarga seletiva perdeu dados GM.");
                    await repository.SetScriptEnabledForProfileAsync(scriptId, profiles[0].Id, false); var profileScripts = await repository.GetScriptsForProfileAsync(profiles[0].Id); Ensure(profileScripts.Single(x => x.Id == scriptId).Enabled == false, "Desativação por perfil não foi persistida.");
                    var untouchedBeforeDisable = hosts[1].NavigationCount; await hosts[0].ApplyScriptsAndReloadAsync(profileScripts); await EventuallyAsync(async () => await hosts[0].ExecuteScriptForDiagnosticsAsync("document.readyState") == "\"complete\"", TimeSpan.FromSeconds(15));
                    Ensure(await hosts[0].ExecuteScriptForDiagnosticsAsync("document.documentElement.dataset.scriptRuns??null") == "null", "Script desativado foi reinjetado após recarga."); Ensure(hosts[1].NavigationCount == untouchedBeforeDisable, "Desativar um perfil recarregou outro."); Ensure((await repository.GetScriptValuesAsync(scriptId, profiles[0].Id)).ContainsKey("durable"), "Desativação removeu dados GM.");
                    await repository.SetScriptEnabledForProfileAsync(scriptId, profiles[0].Id, true); await hosts[0].ApplyScriptsAndReloadAsync(await repository.GetScriptsForProfileAsync(profiles[0].Id));
                }
            }
            var navigationStates = new List<WebView2GameHost.NavigationState>(); hosts[0].NavigationStateChanged += (_, state) => navigationStates.Add(state);
            await hosts[0].NavigateAsync(new Uri($"http://localhost:{port}/external")); await EventuallyAsync(async () => (await hosts[0].ExecuteScriptForDiagnosticsAsync("location.hostname")) == "\"localhost\"", TimeSpan.FromSeconds(10)); Ensure(navigationStates.Any(x => x.CanGoBack), "Histórico não habilitou Voltar.");
            hosts[0].GoBack(); await EventuallyAsync(async () => (await hosts[0].ExecuteScriptForDiagnosticsAsync("location.hostname")) == "\"127.0.0.1\"", TimeSpan.FromSeconds(10)); hosts[0].GoForward(); await EventuallyAsync(async () => (await hosts[0].ExecuteScriptForDiagnosticsAsync("location.hostname")) == "\"localhost\"", TimeSpan.FromSeconds(10));
            await hosts[0].ReturnToGameAsync(); await EventuallyAsync(async () => (await hosts[0].ExecuteScriptForDiagnosticsAsync("location.hostname")) == "\"127.0.0.1\"", TimeSpan.FromSeconds(10));
            var metrics = await hosts[0].CaptureProcessMetricsAsync(TimeSpan.FromSeconds(1));
            var layoutEvidence = hosts.Count >= 4 ? await RunLayoutScenariosAsync(hosts, windows, validationSeconds, validationRepeats, memoryMode) : null;
            var soakMetrics = new List<ProcessMetricsSnapshot>();
            if (soakSeconds > 0)
            {
                var deadline = DateTime.UtcNow.AddSeconds(soakSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(9, Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds))));
                    soakMetrics.Add(await hosts[0].CaptureProcessMetricsAsync(TimeSpan.FromSeconds(1)));
                    var health = await Task.WhenAll(hosts.Select(x => x.ExecuteScriptForDiagnosticsAsync("({ready:document.readyState,ws:globalThis.__ws?.readyState,ticks:globalThis.__ticks})")));
                    Ensure(health.All(x => x.Contains("\"ready\":\"complete\"")), "Perfil deixou de responder durante soak test.");
                }
            }
            var beforeHidden = await ReadIntAsync(hosts[0], "window.__ticks"); var wsBeforeHidden = await ReadIntAsync(hosts[0], "window.__wsCount"); windows[0].Hide(); await Task.Delay(1200); windows[0].Show(); await Task.Delay(200); var afterHidden = await ReadIntAsync(hosts[0], "window.__ticks"); var wsAfterHidden = await ReadIntAsync(hosts[0], "window.__wsCount");
            Ensure(afterHidden > beforeHidden, "Timers pararam apenas por ocultar a janela.");
            Ensure(wsAfterHidden > wsBeforeHidden, "WebSocket parou com a janela oculta.");
            await hosts[0].ExecuteScriptForDiagnosticsAsync("globalThis.__ws?.close()"); await Task.Delay(200);
            await hosts[0].SuspendAsync(); Ensure(hosts[0].IsSuspended, "TrySuspendAsync não suspendeu o WebView."); await Task.Delay(1200); await hosts[0].ResumeAsync(); await Task.Delay(200);
            var afterSuspend = await ReadIntAsync(hosts[0], "window.__ticks"); Ensure(afterSuspend - afterHidden < 8, $"Timer avançou durante suspensão: {afterHidden}->{afterSuspend}");

            var recovered = false; hosts[0].StatusChanged += (_, status) => { if (status == "Ativo") recovered = true; };
            try { await hosts[0].CrashRendererForDiagnosticsAsync(); } catch { }
            await EventuallyAsync(() => Task.FromResult(recovered), TimeSpan.FromSeconds(15));

            string? navigationStatus = null; hosts[^1].StatusChanged += (_, status) => navigationStatus = status;
            await hosts[^1].NavigateAsync(new Uri("http://127.0.0.1:1/unavailable"));
            await EventuallyAsync(() => Task.FromResult(navigationStatus == "Falha na navegação"), TimeSpan.FromSeconds(10));

            object? soak = soakMetrics.Count == 0 ? null : new { seconds = soakSeconds, samples = soakMetrics.Count, startPrivateMb = metrics.PrivateMemoryMb, endPrivateMb = soakMetrics[^1].PrivateMemoryMb, maxPrivateMb = soakMetrics.Max(x => x.PrivateMemoryMb), maxWorkingSetMb = soakMetrics.Max(x => x.WorkingSetMb), averageCpuPercent = Math.Round(soakMetrics.Average(x => x.CpuPercent), 2) };
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", profiles = hosts.Count, userscripts = withScript ? 6 : 0, experimentalBackgroundFlags = experimentalBackground, sharedBrowserProcessId = pids[0], totalMetrics = metrics, layoutEvidence, soak, hiddenTimerDelta = afterHidden - beforeHidden, hiddenWebSocketDelta = wsAfterHidden - wsBeforeHidden, suspendedTimerDelta = afterSuspend - afterHidden, checks = new[] { "cookies", "localStorage", "sessionStorage", "indexedDB", "isolated world", "GM async durability", "single-profile script update", "selective multi-profile reload", "no duplicate registration", "navigation history", "return to game", "panel ownership", "panel hide/show continuity", "Shadow DOM panels", "manual panel selection", "per-profile panel isolation", "grid/focus continuity", "process tree metrics", "WebSocket", "Service Worker", "Canvas", "WebGL", "hidden timers", "suspended timers", "renderer recovery", "navigation failure" } }));
        }
        finally
        {
            foreach (var window in windows) window.Close(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<int> ReadIntAsync(WebView2GameHost host, string expression) => JsonSerializer.Deserialize<int>(await host.ExecuteScriptForDiagnosticsAsync(expression));
    private static async Task<object> RunLayoutScenariosAsync(List<WebView2GameHost> hosts, List<Window> windows, int validationSeconds, int validationRepeats, string memoryMode)
    {
        var auditWindow = new Window { Width = 1100, Height = 720, Left = 50, Top = 50, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var root = new Grid(); auditWindow.Content = root; auditWindow.Show();
        try
        {
            static void Detach(FrameworkElement element) { if (element.Parent is Panel p) p.Children.Remove(element); else if (element.Parent is ContentControl c) c.Content = null; else if (element.Parent is Decorator d) d.Child = null; }
            void Show(params WebView2GameHost[] visible)
            {
                foreach (var host in hosts) Detach(host); root.Children.Clear(); root.RowDefinitions.Clear(); root.ColumnDefinitions.Clear();
                var columns = Math.Max(1, visible.Length <= 2 ? visible.Length : 2); var rows = (int)Math.Ceiling(visible.Length / (double)columns);
                for (var c = 0; c < columns; c++) root.ColumnDefinitions.Add(new ColumnDefinition()); for (var r = 0; r < rows; r++) root.RowDefinitions.Add(new RowDefinition());
                for (var i = 0; i < visible.Length; i++) { Grid.SetColumn(visible[i], i % columns); Grid.SetRow(visible[i], i / columns); root.Children.Add(visible[i]); } root.UpdateLayout();
            }
            void ShowFocusedWithThumbnails()
            {
                foreach (var host in hosts) Detach(host); root.Children.Clear(); root.RowDefinitions.Clear(); root.ColumnDefinitions.Clear();
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
                root.Children.Add(hosts[0]);
                var side = new Grid();
                for (var i = 1; i < 4; i++) { side.RowDefinitions.Add(new RowDefinition()); Grid.SetRow(hosts[i], i - 1); side.Children.Add(hosts[i]); }
                Grid.SetColumn(side, 1); root.Children.Add(side); root.UpdateLayout();
            }
            async Task<(int[] Delta, ProcessMetricsSnapshot Metrics)> SampleAsync()
            {
                var before = await Task.WhenAll(hosts.Take(4).Select(x => ReadIntAsync(x, "window.__ticks"))); await Task.Delay(2200); var after = await Task.WhenAll(hosts.Take(4).Select(x => ReadIntAsync(x, "window.__ticks")));
                return (after.Zip(before, (a, b) => a - b).ToArray(), await hosts[0].CaptureProcessMetricsAsync(TimeSpan.FromSeconds(1)));
            }
            if (validationSeconds > 0)
            {
                var seconds = Math.Clamp(validationSeconds, 10, 600); var repeats = Math.Clamp(validationRepeats == 0 ? 3 : validationRepeats, 2, 10);
                async Task<object> MeasureAsync(string name, Action arrange, bool minimize)
                {
                    var samples = new List<object>();
                    for (var repeat = 1; repeat <= repeats; repeat++)
                    {
                        arrange(); auditWindow.WindowState = minimize ? WindowState.Minimized : WindowState.Normal;
                        for (var i = 0; i < 4; i++) hosts[i].SetMemoryTarget(memoryMode.Equals("low", StringComparison.OrdinalIgnoreCase) || memoryMode.Equals("adaptive", StringComparison.OrdinalIgnoreCase) && (minimize || name is "focused-active-thumbnails" or "focused-others-hidden") && i > 0);
                        if (memoryMode.Equals("adaptive", StringComparison.OrdinalIgnoreCase) && minimize) foreach (var host in hosts.Take(4)) host.SetMemoryTarget(true);
                        await Task.Delay(1000);
                        await Task.WhenAll(hosts.Take(4).Select(x => x.ExecuteScriptForDiagnosticsAsync("window.__resetProbe();true")));
                        var metrics = await hosts[0].CaptureProcessMetricsAsync(TimeSpan.FromSeconds(seconds));
                        var raw = await Task.WhenAll(hosts.Take(4).Select(x => x.ExecuteScriptForDiagnosticsAsync("({ticks:window.__ticks,maxDelayMs:Math.round(window.__maxDelay),missed:window.__missed,webSocketEvents:window.__wsCount,userScriptTicks:Number(document.documentElement.dataset.userTicks||0),visibility:document.visibilityState,width:Math.round(innerWidth),height:Math.round(innerHeight)})")));
                        samples.Add(new { repeat, metrics, accounts = raw.Select(x => JsonSerializer.Deserialize<JsonElement>(x)).ToArray() });
                    }
                    auditWindow.WindowState = WindowState.Normal;
                    return new { name, secondsPerRepeat = seconds, repeats, samples };
                }
                var sustained = new[]
                {
                    await MeasureAsync("single-expanded", () => Show(hosts[0]), false),
                    await MeasureAsync("two-side-by-side", () => Show(hosts[0], hosts[1]), false),
                    await MeasureAsync("four-grid", () => Show(hosts[0], hosts[1], hosts[2], hosts[3]), false),
                    await MeasureAsync("focused-active-thumbnails", ShowFocusedWithThumbnails, false),
                    await MeasureAsync("focused-others-hidden", () => Show(hosts[0]), false),
                    await MeasureAsync("window-minimized", () => Show(hosts[0], hosts[1], hosts[2], hosts[3]), true)
                };
                return new { memoryMode, sustained };
            }
            Show(hosts[0]); var individual = await SampleAsync();
            Show(hosts[0]); var tabbed = await SampleAsync();
            Show(hosts[0], hosts[1]); var sideBySide = await SampleAsync();
            Show(hosts[0], hosts[1], hosts[2], hosts[3]); var fourGrid = await SampleAsync();
            var navBefore = hosts.Take(4).Select(x => x.NavigationCount).ToArray(); var runsBefore = await Task.WhenAll(hosts.Take(4).Select(x => ReadIntAsync(x, "Number(document.documentElement.dataset.scriptRuns||0)")));
            for (var i = 0; i < 20; i++) { Show(hosts[i % 4]); await Task.Delay(20); Show(hosts[0], hosts[1], hosts[2], hosts[3]); await Task.Delay(20); }
            var navAfter = hosts.Take(4).Select(x => x.NavigationCount).ToArray(); var runsAfter = await Task.WhenAll(hosts.Take(4).Select(x => ReadIntAsync(x, "Number(document.documentElement.dataset.scriptRuns||0)")));
            Ensure(navBefore.SequenceEqual(navAfter), "Alternar grade/foco provocou navegação."); Ensure(runsBefore.SequenceEqual(runsAfter), "Alternar grade/foco reiniciou userscript.");
            var minBefore = await Task.WhenAll(hosts.Take(4).Select(x => ReadIntAsync(x, "window.__ticks"))); auditWindow.WindowState = WindowState.Minimized; await Task.Delay(2200); auditWindow.WindowState = WindowState.Normal; await Task.Delay(200); var minAfter = await Task.WhenAll(hosts.Take(4).Select(x => ReadIntAsync(x, "window.__ticks")));
            return new { individualVisible = new { timerDelta = individual.Delta[0], metrics = individual.Metrics }, tabbed = new { visibleTimerDelta = tabbed.Delta[0], hiddenTimerDelta = tabbed.Delta[1], metrics = tabbed.Metrics }, sideBySide = new { timerDelta = sideBySide.Delta.Take(2), metrics = sideBySide.Metrics }, fourGrid = new { timerDelta = fourGrid.Delta, metrics = fourGrid.Metrics }, switches = 20, navigationDelta = navAfter.Zip(navBefore, (a,b)=>a-b), scriptRestartDelta = runsAfter.Zip(runsBefore, (a,b)=>a-b), minimizedTimerDelta = minAfter.Zip(minBefore, (a,b)=>a-b) };
        }
        finally
        {
            for (var i = 0; i < hosts.Count; i++) { if (hosts[i].Parent is Panel p) p.Children.Remove(hosts[i]); windows[i].Content = hosts[i]; }
            auditWindow.Close();
        }
    }
    private static async Task EventuallyAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout; while (DateTime.UtcNow < until) { if (await predicate()) return; await Task.Delay(100); }
        throw new TimeoutException("WebViews não ficaram prontos dentro do prazo.");
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class LocalServer(int port) : IAsyncDisposable
    {
        private readonly HttpListener _listener = new(); private readonly CancellationTokenSource _stop = new(); private Task? _loop;
        public void Start() { _listener.Prefixes.Add($"http://127.0.0.1:{port}/"); _listener.Prefixes.Add($"http://localhost:{port}/"); _listener.Start(); _loop = LoopAsync(); }
        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context; try { context = await _listener.GetContextAsync(); } catch when (_stop.IsCancellationRequested) { break; }
                if (context.Request.IsWebSocketRequest) { _ = HandleSocketAsync(context); continue; }
                var isWorker = context.Request.Url?.AbsolutePath == "/sw.js";
                var isFrame = context.Request.Url?.AbsolutePath == "/frame";
                var content = isWorker
                    ? "self.addEventListener('install',()=>self.skipWaiting());self.addEventListener('activate',e=>e.waitUntil(clients.claim()));"
                    : isFrame ? "<!doctype html><meta charset=utf-8><body>frame</body>"
                    : """
                      <!doctype html><meta charset=utf-8><title>audit</title><body>IdleForge audit<div id="game-safe">game control</div><input id="import-file" type="file" multiple><iframe src="/frame"></iframe></body><script>
                      document.querySelector('#import-file').addEventListener('change',async e=>document.documentElement.dataset.imported=await e.target.files[0].text());
                      window.__ticks=0;window.__maxDelay=0;window.__missed=0;window.__lastTick=performance.now();
                      window.__resetProbe=()=>{window.__ticks=0;window.__maxDelay=0;window.__missed=0;window.__wsCount=0;window.__lastTick=performance.now();document.documentElement.dataset.userTicks='0'};
                      setInterval(()=>{const now=performance.now(),delay=now-window.__lastTick;window.__lastTick=now;window.__ticks++;window.__maxDelay=Math.max(window.__maxDelay,delay);window.__missed+=Math.max(0,Math.round(delay/100)-1)},100);
                      const c=document.createElement('canvas'),ctx=c.getContext('2d');ctx.fillStyle='#0f0';ctx.fillRect(0,0,2,2);window.__canvasOk=ctx.getImageData(0,0,1,1).data[1]===255;
                      const gl=document.createElement('canvas').getContext('webgl');window.__webglOk=!!gl;if(gl){gl.clearColor(1,0,0,1);gl.clear(gl.COLOR_BUFFER_BIT)}
                      window.__wsCount=0;window.__ws=new WebSocket('ws://'+location.host+'/socket');__ws.onmessage=()=>__wsCount++;__ws.onopen=()=>setInterval(()=>{if(__ws.readyState===1)__ws.send('ping')},100);
                      Promise.all([navigator.serviceWorker.register('/sw.js').then(()=>navigator.serviceWorker.ready).then(()=>window.__serviceWorkerOk=true),new Promise(ok=>__ws.addEventListener('open',ok,{once:true}))]).then(()=>window.__featuresReady=true);
                      </script>
                      """;
                var bytes = Encoding.UTF8.GetBytes(content); context.Response.ContentType = isWorker ? "text/javascript" : "text/html; charset=utf-8"; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
        }
        private static async Task HandleSocketAsync(HttpListenerContext context)
        {
            try
            {
                using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket; var buffer = new byte[256];
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, CancellationToken.None); if (received.MessageType == WebSocketMessageType.Close) break;
                    await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, CancellationToken.None);
                }
            }
            catch { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); if (_loop is not null) try { await _loop; } catch { } _listener.Close(); _stop.Dispose(); }
    }
}
