// connectors (GitHub, Runpod, Vast.ai...), all the same way. Off: name, "Off", the switch - nothing else. On: the key (once one
// is saved only "Key saved" with Replace / Remove - the host never sends the key back, not even a part of it), "How to get a
// key" while there is no working key (none saved, or the service stopped accepting it), then the connector's other settings.
// Off keeps the key in the Credential Manager; only Remove deletes it.
const KEYEDIT=new Set(), CHECKING=new Set();   // a saved key being replaced (the field again) / a key being checked by the host
let checkTimer=0;
const connRow=c=>{
  const id=esc(c.id);
  let h=srow(c.name, !c.enabled? T("s_off") : c.summary || T("s_on"), tgl("conn",c.enabled,` data-conn="${id}"`));
  if (!c.enabled) return h;
  // a choice of the connector (GitHub: own token / Git Credential Manager), before the key it decides about
  if (c.choice){ const ch=c.choice;
    h+=srow(ch.label, ch.note||"", `<select data-csel="${esc(ch.key)}" data-conn="${id}" aria-label="${esc(ch.label)}">`
      + (ch.options||[]).map(o=>`<option value="${esc(o.v)}"${o.v===ch.value?" selected":""}>${esc(o.t)}</option>`).join("")+`</select>`,"sub stack"); }
  if (c.uses_key){
    const help=(!c.key_set || c.key_check==="revoked" || c.key_check==="denied" || c.help_needed) && c.help_url
      ? `<div class="sd"><a class="klink" tabindex="0" role="link" data-open="${esc(c.help_url)}">${esc(T("s_key_help"))}</a></div>` : "";
    const row=(title,ctl)=>`<div class="srow sub"><div class="sl"><div>${esc(title)}</div>${help}</div>${ctl}</div>`;
    if (CHECKING.has(c.id)) h+=srow(T("s_key_checking"),"","","sub");
    else if (c.key_set && !KEYEDIT.has(c.id))
      h+=row(T("s_key_set"), `<button class="btn" data-ck="replace" data-conn="${id}">${esc(T("s_key_replace"))}</button>`
        + `<button class="btn" data-ck="remove" data-conn="${id}">${esc(T("s_key_forget"))}</button>`);
    else if (c.key_parts)   // a key of several parts (AWS: access key ID + secret), each its own field, sent joined with ":"
      h+=`<div class="srow sub stack kparts"><div class="sl"><div>${esc(T(c.key_set?"s_key_set":"s_key_none"))}</div>${help}</div>`
        + c.key_parts.map((p,k)=>`<input class="kin wide" type="password" autocomplete="off" spellcheck="false" data-ckey="${id}"${k?` data-kp="${k}"`:""} placeholder="${esc(p.ph)}" aria-label="${esc(p.ph)}">`).join("")
        + `<button class="btn" data-ck="save" data-conn="${id}">${esc(T("s_key_save"))}</button>`
        + (c.key_set? `<button class="btn" data-ck="cancel" data-conn="${id}">${esc(T("cancel"))}</button>` : "") + `</div>`;
    else
      h+=row(T(c.key_set?"s_key_set":"s_key_none"), `<input class="kin" type="password" autocomplete="off" spellcheck="false" data-ckey="${id}" placeholder="${esc(T("s_key_ph"))}">`
        + `<button class="btn" data-ck="save" data-conn="${id}">${esc(T("s_key_save"))}</button>`
        + (c.key_set? `<button class="btn" data-ck="cancel" data-conn="${id}">${esc(T("cancel"))}</button>` : ""));
  }
  // a list the connector keeps (GitHub: repositories): each item with its state and Remove; a field + Add at the end.
  // The whole list goes to the host on every change; it checks it (a bad name -> a message, nothing changed)
  if (c.list){ const ls=c.list;
    h+=`<div class="srow sub"><div class="sl"><div>${esc(ls.label)}</div>${(ls.items||[]).length?"":`<div class="sd">${esc(ls.empty||"")}</div>`}</div></div>`;
    for (const it of ls.items||[])
      h+=`<div class="srow sub li"><div class="sl"><div class="lv">${esc(it.v)}</div>${it.note?`<div class="sd">${esc(it.note)}</div>`:""}</div>`
        +`<button class="ib" data-lrm="${esc(it.v)}" data-lkey="${esc(ls.key)}" data-conn="${id}" title="${esc(ls.remove)}" aria-label="${esc(ls.remove+" "+it.v)}"><i>&#xE711;</i></button></div>`;
    h+=`<div class="srow sub li"><input class="kin wide" type="text" autocomplete="off" spellcheck="false" data-ladd="${esc(ls.key)}" data-conn="${id}" placeholder="${esc(ls.placeholder||"")}" aria-label="${esc(ls.label)}">`
      +`<button class="btn" data-lplus="${esc(ls.key)}" data-conn="${id}">${esc(ls.add)}</button></div>`;
  }
  const gd=c.guard;
  const numv=(k,lab,min,max,step,v,d,emptyOk)=>{ const tl=lab&&lab.t!=null? lab.t : T(lab);
    return srow(tl,d||"",`<input class="kin num" type="number" min="${min}" max="${max}" step="${step}" data-cset="${k}" data-conn="${id}"${emptyOk?' data-empty="1"':""} aria-label="${esc(tl)}" value="${v==null?"":esc(v)}">`,"sub"); };
  // the connector's own number fields, as its row describes them (label = a key of the panel's translations, or the text itself)
  for (const f of c.fields||[]) h+=numv(f.key,f.text!=null?{t:f.text}:f.label,f.min,f.max,f.step,f.value,"",f.empty_ok);
  // on / off options of the connector (texts from the host); "head" starts a group with a heading and a hint
  for (const s of c.switches||[]){
    if (s.head) h+=`<div class="srow sub"><div class="sl"><div class="gh">${esc(s.head)}</div>${s.hint?`<div class="sd">${esc(s.hint)}</div>`:""}</div></div>`;
    h+=srow(s.text,"",tgl("cbool",s.value,` data-conn="${id}" data-cbool="${esc(s.key)}"`),"sub");
  }
  if ((c.notes||[]).length) h+=`<div class="srow sub"><div class="sl">${c.notes.map(n=>`<div class="sd">${esc(n)}</div>`).join("")}</div></div>`;
  // balance guard (2.9): warn / stop thresholds, time to answer, snooze; forecast; a pending stop with Stop now / Snooze;
  // "never auto-stop" per machine; the last stops. Texts in the UI language come from the host.
  if (gd){
    h+=srow(T("s_guard"), gd.note? "⚠ "+gd.note : T("s_guard_d"), tgl("cguard",gd.enabled,` data-conn="${id}"`), "sub");
    if (gd.enabled) h+=numv("guard_warn","s_guard_warn",0,1000000,1,gd.warn)+numv("guard_stop","s_guard_stop",0,1000000,1,gd.stop,gd.hint);
    // the time to answer, snooze, a pending stop, "never auto-stop" and the log serve the auto-stop on GPU idle too
    if (gd.enabled || gd.idle_stop){
      h+=numv("guard_grace_min","s_guard_grace",1,120,1,gd.grace_min)+numv("guard_snooze_min","s_guard_snooze",5,240,5,gd.snooze_min);
      if (gd.enabled && gd.forecast) h+=srow(gd.forecast,"","","sub");
      if (gd.pending) h+=srow("⚠ "+gd.pending.text,"",`<button class="btn" data-gact="stop_now" data-conn="${id}" data-q="${esc(gd.pending.stop_q||"")}">${esc(gd.pending.stop_label)}</button>`
        +`<button class="btn" data-gact="snooze" data-conn="${id}">${esc(gd.pending.snooze_label)}</button>`,"sub");
      for (const m of gd.machines||[]) h+=srow(m.name, T("s_guard_exempt"), tgl("gex",m.exempt,` data-conn="${id}" data-mid="${esc(m.id)}"`), "sub");
      if ((gd.log||[]).length) h+=`<div class="srow sub"><div class="sl"><div>${esc(T("s_guard_log"))}</div>${gd.log.map(l=>`<div class="sd">${esc(l)}</div>`).join("")}</div></div>`;
    }
  }
  if (c.setup) h+=srow(T("s_gh_opts"),"",sbtn(c.setup,T("s_setup")),"sub");
  return h;
};
// the list of a connector: Add (button / Enter) and Remove send the whole new list
// a list change waiting for the host's answer: a second change before it builds on it (not on the older SET)
let LPEND=null, lpendTimer=0;
function connList(id, key, add, remove){
  const c=SET && (SET.connectors||[]).find(x=>x.id===id), cur=((c && c.list && c.list.items)||[]).map(x=>x.v);
  const base=LPEND && LPEND.id===id && LPEND.key===key ? LPEND.list : cur;
  const i=[...$("#sethost").querySelectorAll("input[data-ladd]")].find(e=>e.dataset.conn===id && e.dataset.ladd===key);
  let next, v="";
  if (remove!=null){ next=base.filter(x=>x!==remove); if (i) i.focus(); }   // the clicked row goes: the focus to the Add field
  else {
    v=i? i.value.trim() : "";
    if (!v){ if (i) i.focus(); return; }
    if (base.some(x=>x.toLowerCase()===v.toLowerCase())){ if (c && c.list && c.list.dup) snack(c.list.dup); i.select(); return; }
    next=[...base, v];   // the text stays until the host takes it (a refused name can be corrected, not retyped)
  }
  LPEND={id,key,list:next,before:cur,add:v}; clearTimeout(lpendTimer); lpendTimer=setTimeout(()=>{ LPEND=null; },15000);
  host({settings:"set",connector:id,[key]:next});
}
// the host's answer to a list change: refused -> the pending change goes (the text stays); taken -> the added text goes
function listReply(d){
  if (!LPEND) return;
  const p=LPEND, c=(d.settings_state && d.settings_state.connectors||[]).find(x=>x.id===p.id);
  const now=((c && c.list && c.list.items)||[]).map(x=>x.v);
  if (d.error){ LPEND=null; return; }
  // the host may write the names its own way (a pasted URL -> owner/repo, duplicates dropped): any change from the list
  // before the edit means it was taken; the same list as before = an older state, still waiting
  if (now.length===p.before.length && now.every((x,k)=>x===p.before[k])) return;
  LPEND=null;
  if (p.add){ const i=[...$("#sethost").querySelectorAll("input[data-ladd]")].find(e=>e.dataset.conn===p.id && e.dataset.ladd===p.key); if (i && i.value.trim()===p.add) i.value=""; }
}
const keyInput=id=>[...$("#sethost").querySelectorAll("input[data-ckey]")].find(e=>e.dataset.ckey===id);
