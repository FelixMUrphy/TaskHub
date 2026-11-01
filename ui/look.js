/* ---------------- look: theme, accent, background transparency ---------------- */
let LOOK = null;   // from the host: {dark, accent:"#rrggbb", backdrop:bool, transparency:null|"energy_saver"|"off"}
let PANEL = {theme:"system",accent:"system",panel_transparency:30,card_transparency:30,panel_group:"project",panel_pinned:false};   // config.json, via /api/tasks
// relative luminance -> black or white text on the accent colour (WCAG: whichever contrasts more)
function lum(h){ const m=/^#([0-9a-f]{6})$/i.exec(h||""); if(!m) return 0; const n=parseInt(m[1],16);
  const c=[n>>16&255,n>>8&255,n&255].map(v=>{ v/=255; return v<=0.03928? v/12.92 : Math.pow((v+0.055)/1.055,2.4); });
  return 0.2126*c[0]+0.7152*c[1]+0.0722*c[2]; }
// colourfulness 0..1 (max - min of the RGB channels): a grey accent is close to 0
function chroma(h){ const n=parseInt(h.slice(1),16), c=[n>>16&255,n>>8&255,n&255]; return (Math.max(...c)-Math.min(...c))/255; }
function applyLook(){
  const r=document.documentElement, st=r.style;
  if (PANEL.theme==="light"||PANEL.theme==="dark") r.dataset.theme=PANEL.theme; else delete r.dataset.theme;
  const acc = PANEL.accent==="system" && LOOK && /^#[0-9a-f]{6}$/i.test(LOOK.accent||"") ? LOOK.accent : "";
  if (acc){ st.setProperty("--acc",acc); st.setProperty("--accfg", lum(acc)>0.179?"#000":"#fff"); }
  else { st.removeProperty("--acc"); st.removeProperty("--accfg"); }
  r.classList.toggle("acc-neutral", !!acc && chroma(acc)<0.12);
  const bd=!!(LOOK && LOOK.backdrop), pct=v=>Math.max(0,Math.min(100,+v||0));
  const tr=bd? pct(PANEL.panel_transparency) : 0, ctr=bd? pct(PANEL.card_transparency) : 0;
  // the panel's and the cards' own tint; at 100 % nothing but the blur of the windows behind is left
  st.setProperty("--alpha",(100-tr)+"%"); st.setProperty("--calpha",(100-ctr)+"%");
  // the less of the tint is left under the text, the stronger its halo (header: the panel only; cards: panel and card)
  const see=tr*ctr/100;
  st.setProperty("--halo",Math.round(Math.min(90,see*1.1))+"%"); st.setProperty("--hhalo",Math.round(Math.min(90,tr*1.1))+"%");
  r.classList.toggle("halo", see>=5 || tr>=5);
  const th=document.getElementById("theme"); if (!th || !th.options.length) return;
  th.value=PANEL.theme; document.getElementById("accent").value=PANEL.accent; document.getElementById("grp").value=PANEL.panel_group||"project";
  for (const [id,key] of [["transp","panel_transparency"],["ctransp","card_transparency"]]){
    const s=document.getElementById(id); if (document.activeElement!==s) s.value=PANEL[key];
    document.getElementById(id+"v").textContent=s.value+" %";
    s.disabled=!bd; const rg=document.getElementById(id+"r"); rg.classList.toggle("off",!bd); rg.title=bd?"":T("tr_na");
  }
  // Windows draws no blur right now (energy saver / Transparency effects off): the sliders would seem to do nothing
  const why=LOOK && LOOK.transparency, hint=document.getElementById("transph"), known=why==="energy_saver"||why==="off";
  hint.style.display=bd && known ? "" : "none";
  if (known){ document.getElementById("transpht").textContent=T(why==="off"?"tr_off":"tr_energy");
    const l=document.getElementById("transphl"); l.textContent=T("tr_open"); l.dataset.do=why==="off"?"colors_settings":"energy_settings"; }
  const p=document.getElementById("pin"); $("i",p).textContent=PANEL.panel_pinned?"\uE841":"\uE718";   /* filled pin = pinned */ p.title=T(PANEL.panel_pinned?"unpin":"pin"); p.setAttribute("aria-label",p.title); p.setAttribute("aria-pressed",PANEL.panel_pinned?"true":"false"); p.style.display=NATIVE?"":"none";
}
let modal = false;   // confirm() dialog open -> don't auto-hide
// info: a message with OK only (no choice to make)
function ask(m, info){ return new Promise(res=>{ const d=document.getElementById("dlg"); d.querySelector(".t").textContent=m; d.classList.add("on"); modal=true;
  const [ok,no]=d.querySelectorAll("button"); no.style.display=info?"none":"";
  const k=e=>{ if(e.key==="Enter"){e.preventDefault();done(true);} else if(e.key==="Escape"){e.preventDefault();e.stopPropagation();done(false);} };
  const done=v=>{ d.classList.remove("on"); document.removeEventListener("keydown",k,true); setTimeout(()=>{modal=false;},50); res(v); };
  ok.onclick=()=>done(true); no.onclick=()=>done(false); document.addEventListener("keydown",k,true); ok.focus(); }); }
const H = {"Content-Type":"application/json","X-TaskHub":"1"};
if (TOKEN) H.Authorization = "Bearer " + TOKEN;
const $ = (s, r=document) => r.querySelector(s);
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const ACTIVE = new Set(["running","paused","stale"]);
