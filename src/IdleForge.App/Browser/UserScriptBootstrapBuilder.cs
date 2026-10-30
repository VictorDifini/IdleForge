using IdleForge.Core;
using IdleForge.Core.UserScripts;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace IdleForge.App.Browser;

public static class UserScriptBootstrapBuilder
{
    public static string Build(UserScript script, Guid profileId, string capability, IReadOnlyDictionary<string, JsonElement> initialValues, string? isolatedBinding = null, IReadOnlyList<string>? dependencies = null, bool waitForDocumentElement = false, bool enablePanelTracking = true)
    {
        var source = JsonSerializer.Serialize(string.Join("\n;\n", dependencies ?? []) + "\n;\n" + script.Source);
        var matches = JsonSerializer.Serialize(script.Metadata.Matches.Concat(script.Metadata.Includes));
        var excludes = JsonSerializer.Serialize(script.Metadata.Excludes);
        var delay = script.Metadata.RunAt switch
        {
            ScriptRunAt.DocumentStart when waitForDocumentElement => "document.documentElement ? run() : document.addEventListener('readystatechange',run,{once:true});",
            ScriptRunAt.DocumentStart => "run();",
            ScriptRunAt.DocumentBody => "document.body ? run() : new MutationObserver((_,o)=>{if(document.body){o.disconnect();run();}}).observe(document.documentElement,{childList:true});",
            ScriptRunAt.DocumentEnd => "document.readyState==='loading' ? addEventListener('DOMContentLoaded',run,{once:true}) : run();",
            _ => "document.readyState==='complete' ? setTimeout(run) : addEventListener('load',()=>setTimeout(run),{once:true});"
        };
        var frameGuard = script.Metadata.NoFrames ? "if (window.top !== window.self) return;" : "";
        var grants = script.Metadata.Grants.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var storageGranted = grants.Overlaps(["GM_getValue", "GM_setValue", "GM_deleteValue", "GM_listValues", "GM.getValue", "GM.setValue", "GM.deleteValue", "GM.listValues"]);
        var legacyHttpGranted = grants.Contains("GM_xmlhttpRequest");
        var modernHttpGranted = grants.Contains("GM.xmlHttpRequest") || legacyHttpGranted;
        var legacyDownloadGranted = grants.Contains("GM_download");
        var modernDownloadGranted = grants.Contains("GM.download") || legacyDownloadGranted;
        var transport = isolatedBinding is null ? "globalThis.chrome?.webview?.postMessage?.bind(globalThis.chrome.webview)" : $"payload=>globalThis[{JsonSerializer.Serialize(isolatedBinding)}](JSON.stringify(payload))";
        var legacyApi = storageGranted ? "const GM_getValue=get, GM_setValue=set, GM_deleteValue=del, GM_listValues=()=>Array.from(values.keys());" : "";
        var modernApi = storageGranted ? "const GM={getValue:async(k,d)=>get(k,d),setValue:async(k,v)=>{k=String(k);values.set(k,v);await durable('set',k,v)},deleteValue:async k=>{k=String(k);values.delete(k);await durable('delete',k)},listValues:async()=>Array.from(values.keys()),info:GM_info};" : "const GM={info:GM_info};";
        var legacyHttp = legacyHttpGranted ? "const GM_xmlhttpRequest=details=>httpRequest(details,false);" : "";
        var modernHttp = modernHttpGranted ? "GM.xmlHttpRequest=details=>httpRequest(details,true);" : "";
        var legacyDownload = legacyDownloadGranted ? "const GM_download=details=>downloadRequest(details,false);" : "";
        var modernDownload = modernDownloadGranted ? "GM.download=details=>downloadRequest(details,true);" : "";
        var panelOwner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{profileId:N}:{script.Id:N}:panel")))[..24].ToLowerInvariant();
        var knownPanelIds = new UserScriptPanelSignatureAnalyzer().FindOwnedElementIds(script.Source);
        var creationTracking = isolatedBinding is null ? "" : "const ce=document.createElement.bind(document);document.createElement=(...a)=>mark(ce(...a));const cen=document.createElementNS.bind(document);document.createElementNS=(...a)=>mark(cen(...a));";
        var panelTracking = !enablePanelTracking ? "" : "try{const owner=__PANEL_OWNER__,known=new Set(__KNOWN_PANEL_IDS__);const mark=e=>{try{e?.setAttribute?.('data-idleforge-panel-owner',owner)}catch{}return e};__CREATION_TRACKING__ const scan=root=>{if(!root?.querySelectorAll)return;for(const e of root.querySelectorAll('[id]'))if(known.has(e.id))mark(e);for(const e of root.querySelectorAll('*'))if(e.shadowRoot)scan(e.shadowRoot)};const observer=new MutationObserver(records=>{for(const record of records)for(const node of record.addedNodes)if(node.nodeType===1){if(known.has(node.id))mark(node);scan(node);if(node.shadowRoot)observer.observe(node.shadowRoot,{childList:true,subtree:true})}});const start=()=>{scan(document);observer.observe(document.documentElement,{childList:true,subtree:true});for(const delay of [250,2000,5000])setTimeout(()=>scan(document),delay)};document.documentElement?start():addEventListener('DOMContentLoaded',start,{once:true})}catch(error){console.debug('[IdleForge panel tracking]',error)}".Replace("__CREATION_TRACKING__", creationTracking);
        return """
            (() => {
              __FRAME_GUARD__
              const patterns=__MATCHES__, excluded=__EXCLUDES__, url=location.href;
              const hit=p=>{if(p==='<all_urls>')return /^https?:/i.test(url);if(p.startsWith('/')&&p.lastIndexOf('/')>0){try{return new RegExp(p.slice(1,p.lastIndexOf('/')),p.slice(p.lastIndexOf('/')+1)).test(url)}catch{return false}}return new RegExp('^'+p.replace(/[.+?^${}()|[\]\\]/g,'\\$&').replace(/\*/g,'.*')+'$','i').test(url)};
              if (excluded.some(hit) || (patterns.length && !patterns.some(hit))) return;
              const values=new Map(Object.entries(__INITIAL_VALUES__)), send=__TRANSPORT__;
              __PANEL_TRACKING__
              const pending=new Map(), message=(op,key,value,requestId)=>send?.({channel:'idleforge-gm-v1',capability:__CAPABILITY__,op,key,value,requestId});
              globalThis.__idleforgeResolve=(id,ok,error,result,eventName)=>{const p=pending.get(id);if(!p)return;if(eventName==='progress'){p.details?.onprogress?.(result||{});return}if(p.http||p.download){pending.delete(id);const response=result||{};if(ok){p.details.onload?.(response);p.resolve?.(response)}else{const kind=eventName||'error';p.details['on'+kind]?.(response);p.reject?.(new Error(error||kind))}return}pending.delete(id);ok?p.resolve(result):p.reject(new Error(error||'Falha ao persistir valor'))};
              const durable=(op,key,value)=>new Promise((resolve,reject)=>{const id=crypto.randomUUID();pending.set(id,{resolve,reject});message(op,key,value,id);setTimeout(()=>{if(pending.delete(id))reject(new Error('Timeout ao persistir valor'))},10000)});
              const httpRequest=(details,asPromise)=>{if(!details||!details.url)throw new TypeError('url é obrigatória');const id=crypto.randomUUID();let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b});pending.set(id,{http:true,details,resolve:asPromise?resolve:null,reject:asPromise?reject:null});send({channel:'idleforge-gm-v1',capability:__CAPABILITY__,op:'http.request',requestId:id,request:{method:details.method||'GET',url:String(details.url),headers:details.headers||{},body:details.data==null?null:String(details.data),timeout:details.timeout||30000,responseType:details.responseType||'text'}});const abort=()=>send({channel:'idleforge-gm-v1',capability:__CAPABILITY__,op:'http.abort',requestId:id});if(asPromise){promise.abort=abort;return promise}return{abort}};
              const downloadRequest=(input,asPromise)=>{const details=typeof input==='string'?{url:input}:input;if(!details||!details.url)throw new TypeError('url é obrigatória');const id=crypto.randomUUID();let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b});pending.set(id,{download:true,details,resolve:asPromise?resolve:null,reject:asPromise?reject:null});send({channel:'idleforge-gm-v1',capability:__CAPABILITY__,op:'download.request',requestId:id,request:{url:String(details.url),name:details.name==null?null:String(details.name),headers:details.headers||{}}});const abort=()=>send({channel:'idleforge-gm-v1',capability:__CAPABILITY__,op:'download.abort',requestId:id});if(asPromise){promise.abort=abort;return promise}return{abort}};
              const get=(k,d)=>values.has(String(k))?values.get(String(k)):d;
              const set=(k,v)=>{k=String(k);values.set(k,v);message('set',k,v)};
              const del=k=>{k=String(k);values.delete(k);message('delete',k)};
              const run=()=>{
                const GM_info={script:{name:__SCRIPT_NAME__,version:__SCRIPT_VERSION__,namespace:__SCRIPT_NAMESPACE__}};
                __LEGACY_API__
                __MODERN_API__
                __LEGACY_HTTP__
                __MODERN_HTTP__
                __LEGACY_DOWNLOAD__
                __MODERN_DOWNLOAD__
                try { eval(__SOURCE__); } catch(error) { console.error('[IdleForge userscript]', __SCRIPT_NAME__, error); }
              };
              __DELAY__
            })();
            """
            .Replace("__FRAME_GUARD__", frameGuard)
            .Replace("__MATCHES__", matches).Replace("__EXCLUDES__", excludes)
            .Replace("__PROFILE_ID__", JsonSerializer.Serialize(profileId.ToString("N")))
            .Replace("__SCRIPT_ID__", JsonSerializer.Serialize(script.Id.ToString("N")))
            .Replace("__SCRIPT_NAME__", JsonSerializer.Serialize(script.Metadata.Name))
            .Replace("__SCRIPT_VERSION__", JsonSerializer.Serialize(script.Metadata.Version))
            .Replace("__SCRIPT_NAMESPACE__", JsonSerializer.Serialize(script.Metadata.Namespace))
            .Replace("__CAPABILITY__", JsonSerializer.Serialize(capability))
            .Replace("__PANEL_TRACKING__", panelTracking).Replace("__PANEL_OWNER__", JsonSerializer.Serialize(panelOwner)).Replace("__KNOWN_PANEL_IDS__", JsonSerializer.Serialize(knownPanelIds))
            .Replace("__INITIAL_VALUES__", JsonSerializer.Serialize(initialValues))
            .Replace("__SOURCE__", source).Replace("__DELAY__", delay)
            .Replace("__TRANSPORT__", transport).Replace("__LEGACY_API__", legacyApi).Replace("__MODERN_API__", modernApi)
            .Replace("__LEGACY_HTTP__", legacyHttp).Replace("__MODERN_HTTP__", modernHttp)
            .Replace("__LEGACY_DOWNLOAD__", legacyDownload).Replace("__MODERN_DOWNLOAD__", modernDownload);
    }
}
