/* ---------------- i18n ---------------- */
const LANGS = {en:"English",uk:"Українська",es:"Español",de:"Deutsch",fr:"Français",it:"Italiano",pt:"Português",pl:"Polski",zh:"中文",ja:"日本語"};
const LOCALE = {en:"en-GB",uk:"uk-UA",es:"es-ES",de:"de-DE",fr:"fr-FR",it:"it-IT",pt:"pt-BR",pl:"pl-PL",zh:"zh-CN",ja:"ja-JP"};
// {lang: {key: text}}: the hub fills it in from the "panel" tables of i18n/*.json (I18n.PanelJson).
const I18N = __I18N__;
let LANG = (()=>{ const n=(navigator.language||"en").slice(0,2).toLowerCase(); return I18N[n]?n:"en"; })();
let LANG_SETTING = "auto";
function T(k, ...a){ const d=I18N[LANG]||I18N.en; let s=d[k]; if (s==null) s=I18N.en[k]; if (s==null) return k; return a.length? String(s).replace(/\{(\d)\}/g,(_,i)=>a[+i]) : s; }
const LOC = () => LOCALE[LANG]||"en-GB";
const SUPPORT_URL = "https://github.com/sponsors/FelixMUrphy";
function applyLang(){
  document.documentElement.lang = LANG;
  document.querySelectorAll("[data-i]").forEach(e=>e.textContent=T(e.dataset.i));
  $("#clear").title = T("clear_done");
  $("#sup").title = T("sup_title");
  const [ok,no]=document.querySelectorAll("#dlg button"); ok.textContent=T("ok"); no.textContent=T("cancel");
  const sel=$("#lang");
  sel.innerHTML = `<option value="auto">🌐 ${esc(T("auto"))}</option>` + Object.entries(LANGS).map(([k,v])=>`<option value="${k}">${esc(v)}</option>`).join("");
  sel.value = LANG_SETTING; sel.title = T("language");
  $("#cfg").title = T("settings"); $("#cfg").setAttribute("aria-label", T("settings"));
  $("#setback").title = T("back"); $("#setback").setAttribute("aria-label", T("back"));
  if (typeof renderSet==="function") renderSet();
  $("#theme").innerHTML = ["system","light","dark"].map(v=>`<option value="${v}">${esc(T("th_"+v))}</option>`).join("");
  $("#accent").innerHTML = ["system","taskhub"].map(v=>`<option value="${v}">${esc(T("acc_"+v))}</option>`).join("");
  $("#grp").innerHTML = ["project","source","none"].map(v=>`<option value="${v}">${esc(T("grp_"+v))}</option>`).join("");
  applyLook();
  // rebuild cards so every translated string is refreshed
  $("#list").innerHTML=""; for (const k in ui) delete ui[k];
}

let ver = 0, data = null, filt = "active", skew = 0, focusSeq = null;
const ui = {};   // per-task UI state: {ctlSig, formFor}

function snack(t){ const s=$("#snack"); s.textContent=t; s.classList.add("on"); clearTimeout(snack.h); snack.h=setTimeout(()=>s.classList.remove("on"),2600); }
async function api(method, path, body){
  const r = await fetch(path, {method, headers:H, body: body===undefined?undefined:JSON.stringify(body)});
  const j = await r.json().catch(()=>({}));
  if (!r.ok) throw new Error(j.error || r.status);
  return j;
}
function dur(s, coarse){ const u=T("u"), q=(n,i)=>n+(LANG==="zh"||LANG==="ja"?"":" ")+u[i]; s=Math.max(0,Math.floor(s));
  const p = s<60 ? [q(s,0)] : s<3600 ? [q(Math.floor(s/60),1), q(String(s%60).padStart(2,"0"),0)]
    : s<86400 ? [q(Math.floor(s/3600),2), q(String(Math.floor(s%3600/60)).padStart(2,"0"),1)] : [q(Math.floor(s/86400),3), q(Math.floor(s%86400/3600),2)];
  return coarse ? p[0] : p.join(" "); }
function num(v, d){ if (typeof v!=="number"||!isFinite(v)) return esc(v ?? "—");
  if (d!=null) return v.toFixed(d); const a=Math.abs(v);
  if (a>=1e6) return (v/1e6).toFixed(2)+"M"; if (a>=1e4) return (v/1e3).toFixed(1)+"k";
  if (Number.isInteger(v)) return String(v); return v.toFixed(a>=100?1:a>=1?2:4).replace(/\.?0+$/,""); }
const now = () => Date.now()/1000 + skew;

