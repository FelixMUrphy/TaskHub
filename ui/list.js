/* ---------------- list layout: structure appears only when it is needed ----------------
   "seen" = the task was visible in the open panel (the hub keeps it per task). "Active": what works or waits for you
   (incl. a failure not seen yet); "All": everything - failed (also seen ones, until removed) above the successful ones,
   the ones seen in an earlier opening of the panel folded into "History · N" (the hub keeps them history_days);
   unseen ones are highlighted like unread mail, their count is on the "All" switch (red with a failure among them).
   Nothing moves while the panel is open: what is seen now changes place the next time it opens.
   Many tasks from several projects (or sources) -> groups and a quiet source filter. */
const GROUP_MIN=8;
const lsGet=(k,d)=>{ try{ const v=JSON.parse(localStorage.getItem(k)||"null"); return v==null?d:v; }catch(e){ return d; } };
const lsSet=(k,v)=>{ try{ localStorage.setItem(k,JSON.stringify(v)); }catch(e){} };
let SESSION=new Set();          // tasks seen since the panel was opened: they keep their place until it closes
let GOPEN=lsGet("th_grp",{});    // group key -> open (true / false), only once the user toggled it
let SRC=lsGet("th_src","");      // source filter ("" = all)
const isFail = t => t.state==="failed" || t.state==="lost";
const projectOf = t => t.project || (t.cwd ? String(t.cwd).replace(/[\\/]+$/,"").split(/[\\/]/).pop() : "");
const seenBefore = t => !!t.seen && !SESSION.has(t.id);
const needsMe = t => ACTIVE.has(t.state) ? !!(t.attention || t.state==="stale") : isFail(t) && !seenBefore(t);
const inHistory = t => !ACTIVE.has(t.state) && !isFail(t) && seenBefore(t);
let HOPEN=false;   // "All": history rows unfolded
function ago(sec){ return sec<60 ? T("just_now") : T("ago", dur(sec, true)); }
function place(list, els){
  const keep=new Set(els.map(e=>e.id));
  [...list.children].forEach(c=>{ if (!keep.has(c.id)) c.remove(); });
  let prev=null;
  for (const el of els){ const want=prev?prev.nextSibling:list.firstChild; if (want!==el) list.insertBefore(el,want); prev=el; }
}
function node(id, cls, click){
  let el=document.getElementById(id);
  if (!el){ el=document.createElement("div"); el.id=id; el.className=cls;
    if (click){ el.onclick=click; el.setAttribute("role","button"); el.tabIndex=0; } }   // also Enter / Space, screen readers
  return el;
}
function cardEl(t, prj){
  let el=document.getElementById("t-"+t.id); if (!el) el=cardSkeleton(t);
  updCard(el,t);
  const c=$(".prj",el); c.textContent=prj||""; c.style.display=prj?"":"none";
  return el;
}
function rowEl(t){
  const el=node("r-"+t.id, "row", ()=>unfold(t.id));
  el.dataset.s=t.state; el.classList.toggle("new", !t.seen);
  const note=[T("st_"+t.state), (t.message||t.status||"").split("\n")[0]].filter(Boolean).join(" · ");
  const h=`<i></i><span class="n">${esc(t.name)}</span><span class="r">${esc(note)}</span><span class="t">${ago(now()-(t.finished||t.updated||now()))}</span><span class="fold">›</span>`;
  if (el.__h!==h){ el.innerHTML=h; el.__h=h; el.title=t.name; }
  return el;
}
// height animation between a row and its card (none when Windows animations are off)
const MOTION = !matchMedia("(prefers-reduced-motion: reduce)").matches;
function animH(el, from, to, done){
  if (!MOTION || !el){ done && done(); return; }
  el.style.height=from+"px"; el.style.overflow="hidden"; void el.offsetHeight;
  el.style.transition="height .22s cubic-bezier(.2,0,0,1)"; el.style.height=to+"px";
  let fin=false; const end=()=>{ if (fin) return; fin=true; el.style.height=el.style.overflow=el.style.transition=""; done && done(); };
  el.addEventListener("transitionend", end, {once:true}); setTimeout(end, 320);
}
function unfold(id){
  const row=document.getElementById("r-"+id), h0=row?row.offsetHeight:0;
  ui[id]=ui[id]||{}; ui[id].open=true; render();
  const card=document.getElementById("t-"+id); if (card && h0) animH(card, h0, card.offsetHeight);
}
function fold(id){
  const card=document.getElementById("t-"+id);
  const close=()=>{ if (ui[id]) ui[id].open=false; render(); };
  if (!card || !MOTION) return close();
  animH(card, card.offsetHeight, 26, close);
}
const taskEl = (t, prj) => ACTIVE.has(t.state) || (ui[t.id] && ui[t.id].open) ? cardEl(t, prj) : rowEl(t);
const STATES=["running","paused","stale","done","failed","lost","cancelled"];
const counts = ts => STATES.map(s=>[s, ts.filter(t=>t.state===s).length]).filter(x=>x[1]).map(([s,n])=>`${T("st_"+s)} ${n}`).join(" · ");
function layout(){
  const all=data.tasks;
  // source filter: only with more than one source
  const srcs=[...new Set(all.map(t=>t.source||""))];
  if (SRC && !srcs.includes(SRC)) SRC="";
  const many = filt==="all" ? all.length > GROUP_MIN : all.filter(t=>ACTIVE.has(t.state)||needsMe(t)).length > GROUP_MIN;
  if (!many) SRC="";
  const sf=$("#srcf"), sh=many && srcs.length>1 ? [["",T("all")], ...srcs.map(x=>[x, x||T("src_other")])].map(([v,l])=>`<span data-v="${esc(v)}" class="${v===SRC?"on":""}">${esc(l)}</span>`).join("") : "";
  if (sf.__h!==sh){ sf.innerHTML=sh; sf.__h=sh; } sf.classList.toggle("on", !!sh);
  const ts=all.filter(t=>!SRC || (t.source||"")===SRC);
  const attn=ts.filter(needsMe), hist=ts.filter(inHistory);
  // "Active": what works or waits for you; "All": also the finished ones (the old successful ones folded into the history)
  const rest=ts.filter(t=>!needsMe(t) && (filt==="all" ? !inHistory(t) || HOPEN : ACTIVE.has(t.state)));
  // needs attention -> working -> failed -> the rest; newest first inside each
  const rank=t=> ACTIVE.has(t.state) ? 0 : isFail(t) ? 1 : 2, when=t=>t.finished||t.started||0;
  const order=(a,b)=> (rank(a)-rank(b)) || (when(b)-when(a));
  attn.sort(order); rest.sort(order);
  const out=[];
  if (attn.length){
    const h=node("h-attn","sec"); const hh=`<span class="dot"></span><b>${esc(T("attention"))}</b><span>· ${attn.length}</span>`;
    if (h.__h!==hh){ h.innerHTML=hh; h.__h=hh; } out.push(h);
    for (const t of attn) out.push(cardEl(t, projectOf(t)));
  }
  const by=PANEL.panel_group||"project", key=t=> by==="source" ? (t.source||"") : projectOf(t);
  const keys=[...new Set(rest.map(key))];
  if (by!=="none" && attn.length+rest.length > GROUP_MIN && keys.length > 1){
    const groups=keys.map(k=>({k, ts:rest.filter(t=>key(t)===k)}));
    const act=g=>g.ts.some(t=>ACTIVE.has(t.state)), last=g=>Math.max(0,...g.ts.map(t=>t.finished||t.updated||0));
    const bad=g=>g.ts.some(isFail);
    groups.sort((a,b)=> (act(b)-act(a)) || (bad(b)-bad(a)) || (!a.k-!b.k) || (last(b)-last(a)));
    for (const g of groups){
      const open = GOPEN[g.k]!=null ? GOPEN[g.k] : act(g);
      const id="g-"+encodeURIComponent(g.k).replace(/%/g,"_");
      const el=node(id, "grp", ()=>{ GOPEN[g.k]=!el.classList.contains("open"); lsSet("th_grp",GOPEN); render(); });
      const a=g.ts.filter(t=>ACTIVE.has(t.state)), f=g.ts.filter(t=>!ACTIVE.has(t.state));
      const sum = open ? counts(f) : [counts(g.ts), f.length ? ago(now()-last({ts:f})) : ""].filter(Boolean).join(" · ");
      const h=`<span class="chev">${open?"▾":"▸"}</span><b>${esc(g.k||T("grp_other"))}</b>${open&&a.length?`<span>${esc(counts(a))}</span>`:""}<span class="sum">${esc(sum)}</span>`;
      if (el.__h!==h){ el.innerHTML=h; el.__h=h; }
      el.classList.toggle("open",open); el.classList.toggle("closed",!open);
      out.push(el);
      if (open) for (const t of g.ts) out.push(taskEl(t));
    }
  } else for (const t of rest) out.push(taskEl(t));
  if (filt==="all" && hist.length){
    const h=node("hist","hist",()=>{ HOPEN=!HOPEN; render(); }); const hh=`<span class="fold">›</span>${esc(T("history"))} · ${hist.length}`;
    if (h.__h!==hh){ h.innerHTML=hh; h.__h=hh; } h.classList.toggle("open",HOPEN);
    if (HOPEN){ const i=out.findIndex(e=>hist.some(t=>e.id==="r-"+t.id||e.id==="t-"+t.id)); if (i>=0) out.splice(i,0,h); else out.push(h); } else out.push(h);
  }
  if (!out.length){
    // nothing works: say so plainly, the finished ones are one click away
    const e=node("empty","empty"), fin=ts.filter(t=>!ACTIVE.has(t.state)).length;
    const hh = filt==="all" || !fin ? esc(T(filt==="all"?"no_tasks":"no_active")) : `${esc(T("no_active"))}<br><span class="lnk">${esc(T("all"))} · ${fin}</span>`;
    if (e.__h!==hh){ e.innerHTML=hh; e.__h=hh; const l=$(".lnk",e); if (l) l.onclick=()=>setFilt("all"); }
    out.push(e);
  }
  // "All": how many finished ones you have not seen yet (red if one of them failed)
  const unseen=all.filter(t=>!ACTIVE.has(t.state) && !t.seen), ab=document.querySelector('.tabs button[data-f="all"]');
  const bh=esc(T("all")) + (unseen.length ? `<span class="badge${unseen.some(isFail)?" bad":""}">${unseen.length}</span>` : "");
  if (ab.__h!==bh){ ab.innerHTML=bh; ab.__h=bh; }
  for (const el of out) if (/^[rt]-/.test(el.id)) seenIO.observe(el);
  return out;
}
// plan limits as last reported: past the reset the old percentage says nothing any more, an old reading says how old it is
function renderLimits(){
  const box=$("#limits"), names={claude:"Claude Code",codex:"Codex"}, groups=[], t=now(), th=data.limits_warn||[];
  const when=r=>{ const d=new Date(r*1000), today=d.toDateString()===new Date().toDateString();
    return d.toLocaleString(LANG, today? {hour:"2-digit",minute:"2-digit"} : {weekday:"short",hour:"2-digit",minute:"2-digit"}); };
  // each agent under its own name, always in the same order, so two agents' limits don't read as one list
  const prob=data.limits_problem||{};
  for (const agent of [...new Set([...Object.keys(data.limits||{}),...Object.keys(prob)])].sort()){
    const ws=(data.limits||{})[agent]||{}, rows=[];
    for (const [w,k] of [["five_hour","limit_5h"],["seven_day","limit_7d"]]){
      const x=ws[w]; if (!x || x.used_percentage==null) continue;
      const done=x.resets_at && t>=x.resets_at, p=Math.floor(x.used_percentage), old=t-x.at>900, up=x.used_percentage, lvl=done||!th.length?"":up>=th[th.length-1]?"bad":up>=th[0]?"warn":"";
      const val=done? T("limit_reset_done")
        : [T("limit_used",p), x.resets_at? T("limit_resets",when(x.resets_at)) : "", old? T("limit_updated",ago(t-x.at)) : ""].filter(Boolean).join(" · ");
      rows.push(`<div class="lim${old||done?" old":""}"${lvl?` data-l="${lvl}"`:""}><div class="lt"><span>${esc(T(k))}</span><span class="lv">${esc(val)}</span></div><div class="lb" aria-hidden="true"><i style="width:${done?0:p}%"></i></div></div>`);
    }
    if (!rows.length && prob[agent]) rows.push(`<div class="limn">${esc(T("limit_p_"+prob[agent]))}</div>`);   // on, but no numbers: say why
    const name=esc(names[agent]||agent);
    if (rows.length) groups.push(`<div class="limg" role="group" aria-label="${name}"><div class="limh">${name}</div>${rows.join("")}</div>`);
  }
  const h=groups.join("");
  if (box.__h!==h){ box.innerHTML=h; box.__h=h; box.classList.toggle("on",groups.length>0); }
}
// an agent's hooks, offered once: nothing is written before a click, and a click needs the native panel (never HTTP)
function renderHookOffer(){
  if (!data) return;
  const box=$("#hookoffer"), rows=NATIVE? data.hooks_offer||[] : [], title=T("hooks_offer_t",rows.map(r=>r.name).join(", "));
  const h=!rows.length? "" : `<div class="ht">${esc(title)}</div><div class="hd">${esc(T("hooks_offer_d"))}</div>`
    +(data.hooks_offer_tg? `<div class="hd">${esc(T("hooks_offer_tg"))}</div>` : "")
    +(data.hooks_offer_dc? `<div class="hd">${esc(T("hooks_offer_dc"))}</div>` : "")
    +`<div class="ctl"><button class="btn primary" data-connect="1">${esc(T("hooks_offer_yes"))}</button><button class="btn" data-connect="">${esc(T("hooks_offer_no"))}</button></div>`;
  if (box.__h===h) return;
  const had=box.__clicked||box.contains(document.activeElement);
  box.__clicked=false; box.innerHTML=h; box.__h=h; box.classList.toggle("on",rows.length>0);
  box.setAttribute("role","group"); box.setAttribute("aria-label",title);
  box.__agents=rows.map(r=>r.agent);
  if (had) (rows.length? box.querySelector("button") : $("#list")).focus({preventScroll:true});   // the keyboard stays where it was
}
$("#hookoffer").addEventListener("click",e=>{ const b=e.target.closest("button"), box=$("#hookoffer"); if (!b || b.disabled) return;
  box.__clicked=true; box.querySelectorAll("button").forEach(x=>{ x.disabled=true; });   // one answer: two writes of one file would collide
  host({settings:"do",what:"hooks_offer",connect:!!b.dataset.connect,agents:box.__agents}); });
function render(){
  if (!data) return;
  if ((data.lang && data.lang!==LANG) || (data.language && data.language!==LANG_SETTING)){ LANG=data.lang||LANG; LANG_SETTING=data.language||"auto"; applyLang(); }
  if (data.panel){ if (document.activeElement===$("#transp")) data.panel.panel_transparency=PANEL.panel_transparency;
    if (document.activeElement===$("#ctransp")) data.panel.card_transparency=PANEL.card_transparency; PANEL=data.panel; applyLook(); }
  const list=$("#list");
  const nAct=data.tasks.filter(t=>ACTIVE.has(t.state)).length;
  const cnt=$("#cnt"), cntFull=T("counts",nAct,data.tasks.length);
  cnt.textContent = nAct+" / "+data.tasks.length; cnt.title=cntFull; cnt.setAttribute("aria-label",cntFull);
  $("#mute i").textContent = data.muted ? "\uE7ED" : "\uEA8F";   // bell crossed out = muted
  $("#mute").title = data.muted ? T("notif_off") : T("notif_on");
  renderSet();
  renderHookOffer();
  renderLimits();
  place(list, layout());
  requestAnimationFrame(sbUpd);
  // focus request (tray/toast click)
  const fc=data.focus||{};
  if (focusSeq===null) focusSeq = fc.seq;
  else if (fc.seq!==focusSeq){ focusSeq=fc.seq; if (fc.task) flash(fc.task); }
}
function flash(id){
  const t=findTask(id);
  if (t){
    if (SRC && (t.source||"")!==SRC){ SRC=""; lsSet("th_src",SRC); }
    if (!ACTIVE.has(t.state)){ ui[t.id]=ui[t.id]||{}; ui[t.id].open=true; }
    const by=PANEL.panel_group||"project", k=by==="source"?(t.source||""):projectOf(t); if (GOPEN[k]===false){ GOPEN[k]=true; lsSet("th_grp",GOPEN); }
    if (inHistory(t)) HOPEN=true;
    if (!ACTIVE.has(t.state) && !needsMe(t) && filt!=="all") setFilt("all"); else render();
  }
  const el=document.getElementById("t-"+id); if (!el) return;
  el.scrollIntoView({block:"center",behavior:"smooth"}); el.classList.remove("flash"); void el.offsetWidth; el.classList.add("flash");
}
function setFilt(f){ filt=f; document.querySelectorAll(".tabs button").forEach(b=>b.classList.toggle("on",b.dataset.f===f)); try{localStorage.th_f=f;}catch(e){} render(); }

async function loop(){
  let fails=0;
  for(;;){
    try{
      const r=await fetch(`/api/tasks?since=${ver}&wait=25`, {headers:TOKEN?{Authorization:H.Authorization}:{}});
      if (!r.ok) throw new Error(r.status);
      const j=await r.json(); skew=j.now-Date.now()/1000; ver=j.version; data=j;
      $("#off").style.display="none"; fails=0; render();
    }catch(e){ $("#off").textContent=T("offline")+" ("+(e&&e.message||e)+")"; $("#off").style.display="block"; await new Promise(r=>setTimeout(r, Math.min(5000, 800*++fails))); }
  }
}
document.querySelectorAll(".tabs button").forEach(b=>b.onclick=()=>setFilt(b.dataset.f));
document.addEventListener("keydown",e=>{ const b=e.target.closest && e.target.closest("[role=button]");
  if (b && (e.key==="Enter"||e.key===" ")){ e.preventDefault(); b.click(); } });
$("#srcf").onclick=e=>{ const sp=e.target.closest("span[data-v]"); if (!sp) return; SRC=sp.dataset.v; lsSet("th_src",SRC); render(); };
$("#mute").onclick=()=>api("POST","/api/settings",{muted:!(data&&data.muted)}).catch(e=>snack(e.message));
$("#clear").onclick=()=>api("POST","/api/clear",{}).catch(e=>snack(e.message));
$("#sup").onclick=()=>{ if (NATIVE) host({open:SUPPORT_URL}); else window.open(SUPPORT_URL,"_blank"); };
