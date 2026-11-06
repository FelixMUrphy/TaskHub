const reSet=()=>{ $("#sethost").__h=null; renderSet(); };
// what was typed in a field of a tab: survives the redraws and a visit to another tab; only fields still there are kept
let TYPED={};
const IN_KEEP="input[data-ckey],input[data-tgtok],input[data-dctok],input[data-ladd],input[data-wf]";
function keepTyped(box){ if (!box.__tab) return; const m=new Map(); for (const e of box.querySelectorAll(IN_KEEP)) m.set(ckey(e),e.value); TYPED[box.__tab]=m; }
function wipeTyped(){ TYPED={}; for (const e of $("#sethost").querySelectorAll(IN_KEEP)) e.value=""; }
function renderSet(){
  const box=$("#sethost"); if (!box || !setOpen()) return;
  renderRail();
  const tab=currentTab(), s=NATIVE? SET : null, muted=data? !!data.muted : !!(s && s.muted);
  const sec=(rows,note)=>`<div class="scard">${rows}</div>`+(note?`<div class="sd" style="margin:5px 4px 0">${esc(note)}</div>`:"");
  const mute=srow(HL("mute",T("notif_off")), T(muted?"s_mute_on":"s_mute_off"), tgl("muted",muted));
  let h="";
  if (!s){ if (tab==="notif") h=sec(mute); }   // the browser page: only what the HTTP API allows
  else {
    const tg=s.telegram, up=s.updates;
    // turned off in Windows (Settings › Notifications): toasts vanish silently, so say it and offer the switch
    const winOff=/^disabled/.test(s.notifications||"")? srow(T("s_win_off"),T("s_win_off_d"),sbtn("notif_settings",T("s_open"))) : "";
    if (tab==="notif") h=sec(winOff + mute + srow(HL("test_toast"),"",sbtn("test_toast",T("s_send"))));
    if (tab==="telegram" && tg) h=sec(tgRows(tg));
    if (tab==="discord" && s.discord) h=sec(dcRows(s.discord));
    if (tab==="worktime" && s.worktime) h=wtTab(s.worktime);
    if (tab==="connectors") h=sec((s.connectors||[]).map(connRow).join(""), T("s_conn_note"));
    if (tab==="agents") h=sec((s.agents||[]).map(a=>srow(a.name, a.detected? "" : T("s_not_found"), "")
        + ["hooks","mcp"].map(k=>srow(HL("menu_"+k),"",tgl("agent",a[k],` data-agent="${a.agent}" data-kind="${k}"`),"sub")).join("")
        + (a.limits===undefined? "" : srow(HL(a.agent==="codex"?"menu_limits_codex":"menu_limits"),HL(a.agent==="codex"?"limits_d_codex":a.limits_wraps?"limits_d_wrap":"limits_d_new"),tgl("agent",a.limits,` data-agent="${a.agent}" data-kind="limits"`),"sub"))).join(""));
    if (tab==="system") h=sec(srow(HL("autostart"),"",tgl("autostart",s.autostart))
      + (up? srow(HL("updates"), "", up.available? sbtn("update_open",HL("update_to"),"primary") : sbtn("update_check_now",HL("update_check_now")))
           + srow(HL("update_auto"),"",tgl("update_check",up.auto),"sub")
           : srow(HL("updates"), T("s_store_upd"), ""))
      + srow(HL("data_folder"),"",sbtn("data_folder",T("s_open")))
      + srow(HL("api_help"),"",sbtn("api_help",T("s_open"))));
    if (tab==="about") h=sec(srow("TaskHub "+s.version, T(s.edition==="store"?"s_ed_store":"s_ed_github"), sbtn("support",HL("support"),"heart")),T("s_about"));
  }
  if (box.__h===h && box.__tab===tab) return;
  if (WSEL && box.contains(WSEL.el)) return;   // a list being walked with the keys: a redraw would lose its place
  // keep the focus on the same control across a re-render (the state comes back every few seconds)
  const f=document.activeElement, fk=f && box.contains(f) ? ckey(f) : null;
  // a key being pasted (kept per tab), a number being typed
  keepTyped(box);
  const num={}; for (const e of box.querySelectorAll("input[data-cset],input[data-wset]")) if (e===f) num[ckey(e)]=e.value;
  box.innerHTML=h; box.__h=h; box.__tab=tab;
  const kept=TYPED[tab];
  for (const e of box.querySelectorAll(IN_KEEP)) if (kept && kept.has(ckey(e))) e.value=kept.get(ckey(e));
  for (const e of box.querySelectorAll("input[data-cset],input[data-wset]")) if (ckey(e) in num) e.value=num[ckey(e)];
  if (fk) for (const e of box.querySelectorAll("button,input,select,a[data-open]")) if (ckey(e)===fk){ e.focus({preventScroll:true}); break; }
}
// "Settings" next to the transparency hint: Windows Settings, the page that turned the blur off
{ const l=$("#transphl"), go=()=>{ if (l.dataset.do) host({settings:"do",what:l.dataset.do}); };
  l.onclick=e=>{ e.preventDefault(); go(); }; l.onkeydown=e=>{ if (e.key==="Enter"||e.key===" "){ e.preventDefault(); go(); } }; }
$("#sethost").addEventListener("click",e=>{
  const a=e.target.closest("a[data-open]"); if (a){ e.preventDefault(); host({open:a.dataset.open}); return; }   // the default browser
  const t=e.target.closest("button"); if (!t || t.disabled) return;
  if (wtTabClick(t)) return;
  if (t.dataset.do==="tg_pair"){ tgPair(); return; }
  if (t.dataset.do==="dc_pair"){ dcPair(); return; }
  if (t.dataset.do==="dc_retry" || t.dataset.do==="dc_test"){ const w=t.dataset.do; dcBusy(()=>host({settings:"do",what:w})); return; }
  if (t.dataset.do==="dc_disconnect"){ ask(t.dataset.q).then(ok=>{ if (ok) host({settings:"do",what:"dc_disconnect"}); }); return; }
  if (t.dataset.do==="tg_relink"){ const go=()=>tgBusy(()=>host({settings:"do",what:"tg_relink"})); if (t.dataset.q) ask(t.dataset.q).then(ok=>{ if (ok) go(); }); else go(); return; }
  if (t.dataset.do==="tg_disconnect"){ ask(t.dataset.q).then(ok=>{ if (ok) host({settings:"do",what:"tg_disconnect"}); }); return; }
  if (t.dataset.do){ if (t.dataset.do==="update_check_now") snack(T("s_upd_checking")); host({settings:"do",what:t.dataset.do}); return; }
  if (t.dataset.ck){ connKey(t.dataset.conn, t.dataset.ck); return; }
  if (t.dataset.lrm!=null){ connList(t.dataset.conn, t.dataset.lkey, false, t.dataset.lrm); return; }
  if (t.dataset.lplus){ connList(t.dataset.conn, t.dataset.lplus, true); return; }
  if (t.dataset.gact){
    const go=()=>host({settings:"do",what:"guard",connector:t.dataset.conn,act:t.dataset.gact});
    if (t.dataset.q) ask(t.dataset.q).then(ok=>{ if (ok) go(); }); else go();   // Stop now asks first
    return;
  }
  const k=t.dataset.k, on=!t.classList.contains("on");
  t.classList.toggle("on",on); t.setAttribute("aria-checked",on?"true":"false");   // at once; the host's reply confirms or reverts
  e.currentTarget.__h=null;   // the next render (reply / poll) redraws even if the state did not change - a failed change flips back
  if (k==="muted" && !NATIVE){ api("POST","/api/settings",{muted:on}).catch(er=>snack(er.message)).finally(()=>setTimeout(renderSet,300)); return; }
  if (k==="worktime") host(on? {settings:"set",worktime:true,worktime_setup:wtSetup()} : {settings:"set",worktime:false});
  else if (k==="wts") host({settings:"set",worktime_settings:{[t.dataset.wsk]:on}});
  else if (k==="agent") host({settings:"set",agent:t.dataset.agent,kind:t.dataset.kind,on});
  else if (k==="cguard") host({settings:"set",connector:t.dataset.conn,guard:on});
  else if (k==="cbool") host({settings:"set",connector:t.dataset.conn,[t.dataset.cbool]:on});
  else if (k==="gex"){
    // "never auto-stop" for one machine: the whole list goes to the host
    const c=SET && (SET.connectors||[]).find(x=>x.id===t.dataset.conn), ex=new Set((c && c.guard && c.guard.exempt)||[]);
    if (on) ex.add(t.dataset.mid); else ex.delete(t.dataset.mid);
    host({settings:"set",connector:t.dataset.conn,guard_exempt:[...ex]});
  }
  else if (k==="conn"){
    // the rows under it appear / go at once; turned on without a key: the key field, focused
    const id=t.dataset.conn, c=SET && (SET.connectors||[]).find(x=>x.id===id);
    if (c){ c.enabled=on; if (!on) c.summary=""; KEYEDIT.delete(id); renderSet(); }
    if (c && on && c.uses_key && !c.key_set){ const i=keyInput(id); if (i) i.focus(); }
    host({settings:"set",connector:id,enabled:on});
  }
  else host({settings:"set",[k]:on});
});
function connKey(id, what){
  const c=SET && (SET.connectors||[]).find(x=>x.id===id);
  if (what==="remove"){ ask(T("s_key_remove_q", c? c.name : id)).then(ok=>{ if (ok) host({settings:"set",connector:id,forget_key:true}); }); return; }
  if (what==="replace"){ KEYEDIT.add(id); reSet(); const i=keyInput(id); if (i) i.focus(); return; }
  if (what==="cancel"){ KEYEDIT.delete(id); reSet(); return; }
  // every part of the key must be there (one field for most connectors); parts go joined with ":"
  const ins=[...$("#sethost").querySelectorAll("input[data-ckey]")].filter(e=>e.dataset.ckey===id);
  const empty=ins.find(e=>!e.value.trim()); if (empty || !ins.length){ if (empty) empty.focus(); return; }
  const v=ins.map(e=>e.value.trim()).join(":");
  for (const e of ins) e.value="";
  KEYEDIT.delete(id); CHECKING.add(id); reSet();
  clearTimeout(checkTimer); checkTimer=setTimeout(()=>{ CHECKING.clear(); reSet(); },45000);   // no reply: don't say "checking" forever
  host({settings:"set",connector:id,key:v});
}
$("#sethost").addEventListener("input",wtTabInput);
$("#sethost").addEventListener("keydown",e=>{
  const a=e.target.closest && e.target.closest("a[data-open]"); if (a && (e.key==="Enter"||e.key===" ")){ e.preventDefault(); host({open:a.dataset.open}); return; }
  if (e.key==="Enter" && e.target.dataset && e.target.dataset.tgtok!=null){ e.preventDefault(); tgPair(); return; }
  if (e.key==="Enter" && e.target.dataset && e.target.dataset.dctok!=null){ e.preventDefault(); dcPair(); return; }
  if (e.key==="Enter" && e.target.dataset && e.target.dataset.ladd){ e.preventDefault(); connList(e.target.dataset.conn, e.target.dataset.ladd, true); return; }
  const i=e.target.closest && e.target.closest("input[data-ckey]"); if (i && e.key==="Enter"){ e.preventDefault(); connKey(i.dataset.ckey,"save"); } });
// a number field of a connector (idle GPU minutes, runway hours, low balance $...): sent when changed; empty = off where the
// field allows it (data-empty), else the old value is put back
$("#sethost").addEventListener("change",e=>{
  const sl=e.target.closest && e.target.closest("select[data-csel]");
  if (sl){ $("#sethost").__h=null; host({settings:"set",connector:sl.dataset.conn,[sl.dataset.csel]:sl.value}); return; }   // a refused change: the redraw puts the old value back
  const wi=e.target.closest && e.target.closest("input[data-wset],select[data-wset]");
  if (wi){
    // a list walked with the keys (arrows, letters) changes at every key: the pick is sent after a pause, on Enter or when the list is left
    if (wi.tagName==="SELECT" && Date.now()-WSEL_KEY<400){ clearTimeout(WSEL && WSEL.t); WSEL={el:wi, t:setTimeout(()=>wsetSend(wi),1200)}; return; }
    wsetSend(wi); return; }
  if (e.target.dataset && e.target.dataset.wcKind){ wtTabSelect(e); return; }
  if (e.target.dataset && e.target.dataset.wf==="salary_currency"){ WC.setupCur=e.target.value; reSet(); return; }   // the setup's pick, kept across redraws
  const i=e.target.closest && e.target.closest("input[data-cset]"); if (!i) return;
  const k=i.dataset.cset, v=i.value.trim();
  if (v==="" && !i.dataset.empty){ reSet(); return; }
  host({settings:"set",connector:i.dataset.conn,[k]: v===""? null : +v});
});
// a Work hours setting sent; a refused change: the redraw puts the old value back
let WSEL=null, WSEL_KEY=0;
function wsetSend(wi){
  if (WSEL){ clearTimeout(WSEL.t); WSEL=null; }
  $("#sethost").__h=null; const v=wi.value.trim(), k=wi.dataset.wset, nullable=["manual_rate","holiday_mult","country","region","display_currency","fx_manual_rate","remind_start","remind_paused_hours","remind_open_after"].includes(k);
  if (v==="" && nullable){ host({settings:"set",worktime_settings:k==="country"? {country:null,region:null} : {[k]:null}}); return; }
  const x=v===""? undefined : wtNum(k,v);
  if (x===undefined){ if (v!=="") snack(T("wt_bad_value")); reSet(); } else host({settings:"set",worktime_settings:k==="country"? {country:x,region:null} : {[k]:x}});   // a region belongs to its country
}
$("#sethost").addEventListener("keydown",e=>{ const sl=e.target.closest && e.target.closest("select[data-wset]"); if (!sl) return;
  if (e.key==="Enter" && WSEL && WSEL.el===sl){ e.preventDefault(); wsetSend(sl); return; }
  if (e.key!=="Tab") WSEL_KEY=Date.now(); });
$("#sethost").addEventListener("focusout",e=>{ if (WSEL && WSEL.el===e.target) wsetSend(e.target); });
function settingsReply(d){
  if (d.error || d.note || d.info){ CHECKING.clear(); clearTimeout(checkTimer); clearTimeout(TGBUSY); TGBUSY=0; clearTimeout(DCBUSY); DCBUSY=0; }   // the answer to a key just saved
  listReply(d);
  SET=d.settings_state; renderSet();
  $("#hookoffer").__h=null; renderHookOffer();   // the answer to the card (its buttons wait for it)
  if (d.error || d.note) ask(d.error || d.note, true);
  else if (d.info) snack(d.info);
}
const setPanel=b=>api("POST","/api/settings",b).then(r=>{ if(r.panel){ PANEL=r.panel; applyLook(); } }).catch(e=>snack(e.message));
$("#theme").onchange=e=>setPanel({theme:e.target.value});
$("#accent").onchange=e=>setPanel({accent:e.target.value});
$("#grp").onchange=e=>setPanel({panel_group:e.target.value});
$("#transp").oninput=e=>{ PANEL.panel_transparency=+e.target.value; applyLook(); };
$("#transp").onchange=e=>setPanel({panel_transparency:+e.target.value});
$("#ctransp").oninput=e=>{ PANEL.card_transparency=+e.target.value; applyLook(); };
$("#ctransp").onchange=e=>setPanel({card_transparency:+e.target.value});
$("#pin").onclick=()=>setPanel({panel_pinned:!PANEL.panel_pinned});
$("#push").onclick=()=>host({settings:"set",panel_push_windows:!PANEL.panel_push_windows});   // through the host only, not HTTP; the switch follows the next snapshot
window.addEventListener("blur",()=>{
  if (!AUTOHIDE || NATIVE) return;   // the native host hides itself on deactivation
  setTimeout(async ()=>{
    if (document.hasFocus() || modal || Object.values(ui).some(u=>u.formFor)) return;
    try{ const r=await api("POST","/api/ui/hide",{}); if (!r.hidden) window.close(); }catch(e){ window.close(); }
  }, 250);
});
