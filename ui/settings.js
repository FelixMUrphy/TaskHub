// settings page. Everything that used to be in the tray menu is here; the host's part (autostart, AI agents, Telegram,
// connectors, updates, folders) goes through WebView2 messages only ({"settings":...} -> {"settings_state":...}).
const setBox=$("#set");
let SET=null, setTimer=0;
const setOpen=()=>document.body.classList.contains("setmode");
const pinnedSet=()=>NATIVE && PANEL.panel_pinned;   // a pinned panel: only the close button closes settings
let typedWipe=0;
const WIPE_MS=120000;   // what was typed and not saved (a key, a name) lasts two minutes after closing, then it is gone
function openSet(){ const b=document.body; b.classList.add("setmode"); clearTimeout(typedWipe);
  if (NATIVE) host({settings_window:"open"});
  renderSet(); tabFocus();
  if (NATIVE){ host({settings:"get"}); clearInterval(setTimer); setTimer=setInterval(()=>{ if (setOpen()) host({settings:"get"}); },3000); } }
// the host narrows the window, then says "settings_gone"; the settings part stays drawn until then
function closeSet(backToGear){ if (!setOpen()) return; document.body.classList.remove("setmode"); clearInterval(setTimer); tipHide();
  clearTimeout(typedWipe); typedWipe=setTimeout(wipeTyped,WIPE_MS);
  if (NATIVE) host({settings_window:"closed"});
  if (backToGear) $("#cfg").focus(); }
$("#cfg").onclick=()=>{ if (!setOpen()) openSet(); else if (pinnedSet()) tabFocus(); else closeSet(true); };
$("#setclose").onclick=()=>closeSet(NATIVE);
// a click on the panel closes settings and still does what it does (capture: before a handler can redraw what was clicked)
document.addEventListener("click",e=>{ if (!NATIVE || !setOpen() || modal || pinnedSet() || e.target.closest("#set,#dlg,#cfg")) return; closeSet(); },true);
function settingsSize(z){ settingsSize.last=z=z||settingsSize.last; if (!z) return;
  const r=document.documentElement, k=devicePixelRatio||1;   // physical px -> this page's px (zoom included)
  r.style.setProperty("--setw",(z.set/k)+"px"); r.style.setProperty("--pad",(z.room/k)+"px"); r.classList.toggle("setover",!!z.over); }
// the host is making room (it may be sliding the panel in first): only now is the settings part drawn
function settingsRoom(z){ settingsSize(z); const b=document.body; if (setOpen() && !b.classList.contains("setwide")){ b.classList.add("setwide"); tabFocus(); } }
window.addEventListener("resize",()=>settingsSize());
function settingsGone(){ if (!setOpen()) document.body.classList.remove("setwide"); }

/* ---------------- tabs: a strip of icons, one section at a time ---------------- */
const TAB_ICON={look:"\uE790",notif:"\uEA8F",telegram:"\uE724",discord:"\uE8BD",connectors:"\uE703",agents:"\uE99A",system:"\uE770",about:"\uE946"};
let setTab=(()=>{ try{ return localStorage.th_settab||"look"; }catch(e){ return "look"; } })();
const TAB_TIP=500;
function tabList(){
  if (!NATIVE) return ["look","notif"];   // a browser tab has only what the HTTP API allows
  return ["look","notif",...(SET && !SET.telegram? [] : ["telegram"]),...(SET && !SET.discord? [] : ["discord"]),"connectors","agents","system","about"];
}
const tabName=id=>({look:T("sec_look"),notif:T("sec_notif"),telegram:"Telegram",discord:"Discord",connectors:T("tab_connectors"),agents:T("tab_agents"),system:T("sec_system"),about:T("sec_about")})[id];
// a section with something the user has to do: the strip shows it, as its title can't be read without hovering
function tabAttention(id){
  if (!NATIVE || !SET) return false;
  if (id==="notif") return /^disabled/.test(SET.notifications||"");
  if (id==="telegram") return !!(SET.telegram && SET.telegram.warning);
  if (id==="discord") return !!(SET.discord && SET.discord.warning);
  if (id==="system") return !!(SET.updates && SET.updates.available);
  if (id==="connectors") return (SET.connectors||[]).some(c=>c.enabled && ((c.uses_key && !c.key_set) || c.key_check==="revoked" || c.key_check==="denied" || c.help_needed || (c.guard && c.guard.pending)));
  return false;
}
function currentTab(){ const l=tabList(); return l.includes(setTab)? setTab : l[0]; }
function renderRail(){
  const r=$("#rail"), cur=currentTab(), rows=tabList().map(id=>{ const a=tabAttention(id), n=tabName(id);
    return {id,n,a,lab:a? n+", "+T("tab_attention") : n}; });
  const sig=JSON.stringify([rows,cur]); if (r.__sig===sig) return; r.__sig=sig;
  const hadFocus=r.contains(document.activeElement);
  const btn=x=>`<button class="rt" role="tab" id="st-${x.id}" data-tab="${x.id}" data-name="${esc(x.n)}" aria-label="${esc(x.lab)}" aria-selected="${x.id===cur}" aria-controls="setsc" tabindex="${x.id===cur?0:-1}"><i>${TAB_ICON[x.id]}</i>${x.a?'<span class="dot"></span>':""}</button>`;
  r.innerHTML=rows.filter(x=>x.id!=="about").map(btn).join("")+'<span class="sp"></span>'+rows.filter(x=>x.id==="about").map(btn).join("");
  if (hadFocus) tabFocus();
  $("#settitle").textContent=tabName(cur); $("#setsc").setAttribute("aria-labelledby","st-"+cur);
  $("#lookcard").style.display=cur==="look"?"":"none";
}
function tabFocus(){ const b=$("#st-"+currentTab()); if (b) b.focus({preventScroll:true}); }
function pickTab(id, byKey){
  if (id===currentTab()){ if (byKey) tabFocus(); return; }
  setTab=id; try{ localStorage.th_settab=id; }catch(e){}
  tipHide(); renderSet(); $("#setsc").scrollTop=0;
  if (byKey) tabFocus();
}
let tipTimer=0;
function tipShow(b,delay){ clearTimeout(tipTimer); tipTimer=setTimeout(()=>{ const t=$("#settip"); t.textContent=b.dataset.name; t.style.top=(b.offsetTop+7)+"px"; t.classList.add("on"); },delay); }
function tipHide(){ clearTimeout(tipTimer); $("#settip").classList.remove("on"); }
{ const r=$("#rail");
  r.addEventListener("click",e=>{ const b=e.target.closest(".rt"); if (b){ tipHide(); pickTab(b.dataset.tab); } });
  r.addEventListener("mouseover",e=>{ const b=e.target.closest(".rt"); if (b) tipShow(b,TAB_TIP); });
  r.addEventListener("mouseout",e=>{ if (e.target.closest(".rt")) tipHide(); });
  r.addEventListener("focusin",e=>{ const b=e.target.closest(".rt"); if (b && b.matches(":focus-visible")) tipShow(b,0); });
  r.addEventListener("focusout",tipHide);
  r.addEventListener("keydown",e=>{ if (!["ArrowDown","ArrowUp","Home","End"].includes(e.key)) return; e.preventDefault();
    const l=[...r.querySelectorAll(".rt")].map(x=>x.dataset.tab), i=l.indexOf(currentTab()), n=l.length;
    pickTab(l[e.key==="ArrowDown"?(i+1)%n : e.key==="ArrowUp"?(i-1+n)%n : e.key==="Home"?0 : n-1],true); }); }
const HL=(k,f)=>(SET && SET.labels && SET.labels[k]) || f || k;
const tgl=(k,on,extra)=>`<button class="tgl${on?" on":""}" role="switch" aria-checked="${on?"true":"false"}" data-k="${k}"${extra||""}><i></i></button>`;
const srow=(title,desc,ctl,cls)=>(ctl=(ctl||"").replace('<button class="tgl',`<button aria-label="${esc(title)}" class="tgl`),   // a switch is named by its row
  `<div class="srow${cls?" "+cls:""}"><div class="sl"><div>${esc(title)}</div>${desc?`<div class="sd">${esc(desc)}</div>`:""}</div>${ctl}</div>`);
const sbtn=(what,txt,cls)=>`<button class="btn${cls?" "+cls:""}" data-do="${what}">${esc(txt)}</button>`;
const ckey=e=>[e.dataset.open,e.dataset.tgtok,e.dataset.ladd,e.dataset.lrm,e.dataset.lplus,e.dataset.csel,e.dataset.cbool,e.dataset.do,e.dataset.k,e.dataset.agent,e.dataset.kind,e.dataset.conn,e.dataset.ck,e.dataset.ckey,e.dataset.kp,e.dataset.cset,e.dataset.open,e.dataset.mid,e.dataset.gact].join("|");
