const reSet=()=>{ $("#sethost").__h=null; renderSet(); };
function renderSet(){
  const box=$("#sethost"); if (!box || !setOpen()) return;
  const s=NATIVE? SET : null, muted=data? !!data.muted : !!(s && s.muted);
  const sec=(t,rows,note)=>`<h3>${esc(t)}</h3><div class="scard">${rows}</div>`+(note?`<div class="sd" style="margin:5px 4px 0">${esc(note)}</div>`:"");
  const mute=srow(HL("mute",T("notif_off")), T(muted?"s_mute_on":"s_mute_off"), tgl("muted",muted));
  let h;
  if (!s) h=sec(T("sec_notif"),mute);   // the browser page: only what the HTTP API allows
  else {
    const tg=s.telegram, up=s.updates;
    // turned off in Windows (Settings › Notifications): toasts vanish silently, so say it and offer the switch
    const winOff=/^disabled/.test(s.notifications||"")? srow(T("s_win_off"),T("s_win_off_d"),sbtn("notif_settings",T("s_open"))) : "";
    h=sec(T("sec_notif"), winOff + mute + srow(HL("test_toast"),"",sbtn("test_toast",T("s_send")))
      + (tg? srow("Telegram", tg.linked? [tg.bot?"@"+tg.bot:"",T("s_linked")].filter(Boolean).join(" · ")
          : tg.configured? (tg.status||T("s_not_linked")) : T("s_off"), sbtn("telegram",T("s_setup")))
          + (tg.warning? srow("⚠ "+tg.warning,"","","sub") : "") : ""));
    h+=sec(HL("connectors"), (s.connectors||[]).map(connRow).join(""), T("s_conn_note"));
    h+=sec(HL("integrations"), (s.agents||[]).map(a=>srow(a.name, a.detected? "" : T("s_not_found"), "")
        + ["hooks","mcp"].map(k=>srow(HL("menu_"+k),"",tgl("agent",a[k],` data-agent="${a.agent}" data-kind="${k}"`),"sub")).join("")).join(""));
    h+=sec(T("sec_system"), srow(HL("autostart"),"",tgl("autostart",s.autostart))
      + (up? srow(HL("updates"), "", up.available? sbtn("update_open",HL("update_to"),"primary") : sbtn("update_check_now",HL("update_check_now")))
           + srow(HL("update_auto"),"",tgl("update_check",up.auto),"sub")
           : srow(HL("updates"), T("s_store_upd"), ""))
      + srow(HL("data_folder"),"",sbtn("data_folder",T("s_open")))
      + srow(HL("api_help"),"",sbtn("api_help",T("s_open"))));
    h+=sec(T("sec_about"), srow("TaskHub "+s.version, T(s.edition==="store"?"s_ed_store":"s_ed_github"), sbtn("support",HL("support"),"heart")));
  }
  if (box.__h===h) return;
  // keep the focus on the same control across a re-render (the state comes back every few seconds)
  const f=document.activeElement, fk=f && box.contains(f) ? ckey(f) : null;
  // a key being pasted, a number being typed
  const typed={}; for (const e of box.querySelectorAll("input[data-ckey],input[data-cset]")) if (e.dataset.ckey? e.value : e===f) typed[ckey(e)]=e.value;
  box.innerHTML=h; box.__h=h;
  for (const e of box.querySelectorAll("input[data-ckey],input[data-cset]")) if (ckey(e) in typed) e.value=typed[ckey(e)];
  if (fk) for (const e of box.querySelectorAll("button,input")) if (ckey(e)===fk){ e.focus({preventScroll:true}); break; }
}
// "Settings" next to the transparency hint: Windows Settings, the page that turned the blur off
{ const l=$("#transphl"), go=()=>{ if (l.dataset.do) host({settings:"do",what:l.dataset.do}); };
  l.onclick=e=>{ e.preventDefault(); go(); }; l.onkeydown=e=>{ if (e.key==="Enter"||e.key===" "){ e.preventDefault(); go(); } }; }
$("#sethost").addEventListener("click",e=>{
  const a=e.target.closest("a[data-open]"); if (a){ e.preventDefault(); host({open:a.dataset.open}); return; }   // the default browser
  const t=e.target.closest("button"); if (!t || t.disabled) return;
  if (t.dataset.do){ if (t.dataset.do==="update_check_now") snack(T("s_upd_checking")); host({settings:"do",what:t.dataset.do}); return; }
  if (t.dataset.ck){ connKey(t.dataset.conn, t.dataset.ck); return; }
  if (t.dataset.gact){
    const go=()=>host({settings:"do",what:"guard",connector:t.dataset.conn,act:t.dataset.gact});
    if (t.dataset.q) ask(t.dataset.q).then(ok=>{ if (ok) go(); }); else go();   // Stop now asks first
    return;
  }
  const k=t.dataset.k, on=!t.classList.contains("on");
  t.classList.toggle("on",on); t.setAttribute("aria-checked",on?"true":"false");   // at once; the host's reply confirms or reverts
  e.currentTarget.__h=null;   // the next render (reply / poll) redraws even if the state did not change - a failed change flips back
  if (k==="muted" && !NATIVE){ api("POST","/api/settings",{muted:on}).catch(er=>snack(er.message)).finally(()=>setTimeout(renderSet,300)); return; }
  if (k==="agent") host({settings:"set",agent:t.dataset.agent,kind:t.dataset.kind,on});
  else if (k==="cguard") host({settings:"set",connector:t.dataset.conn,guard:on});
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
  const inp=keyInput(id), v=inp? inp.value.trim() : ""; if (!v){ if (inp) inp.focus(); return; }
  inp.value=""; KEYEDIT.delete(id); CHECKING.add(id); reSet();
  clearTimeout(checkTimer); checkTimer=setTimeout(()=>{ CHECKING.clear(); reSet(); },45000);   // no reply: don't say "checking" forever
  host({settings:"set",connector:id,key:v});
}
$("#sethost").addEventListener("keydown",e=>{
  const a=e.target.closest && e.target.closest("a[data-open]"); if (a && (e.key==="Enter"||e.key===" ")){ e.preventDefault(); host({open:a.dataset.open}); return; }
  const i=e.target.closest && e.target.closest("input[data-ckey]"); if (i && e.key==="Enter"){ e.preventDefault(); connKey(i.dataset.ckey,"save"); } });
// a number field of a connector (idle GPU minutes, runway hours, low balance $...): sent when changed; empty = off where the
// field allows it (data-empty), else the old value is put back
$("#sethost").addEventListener("change",e=>{
  const i=e.target.closest && e.target.closest("input[data-cset]"); if (!i) return;
  const k=i.dataset.cset, v=i.value.trim();
  if (v==="" && !i.dataset.empty){ reSet(); return; }
  host({settings:"set",connector:i.dataset.conn,[k]: v===""? null : +v});
});
function settingsReply(d){
  if (d.error || d.note || d.info){ CHECKING.clear(); clearTimeout(checkTimer); }   // the answer to a key just saved
  SET=d.settings_state; renderSet();
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
window.addEventListener("blur",()=>{
  if (!AUTOHIDE || NATIVE) return;   // the native host hides itself on deactivation
  setTimeout(async ()=>{
    if (document.hasFocus() || modal || Object.values(ui).some(u=>u.formFor)) return;
    try{ const r=await api("POST","/api/ui/hide",{}); if (!r.hidden) window.close(); }catch(e){ window.close(); }
  }, 250);
});
