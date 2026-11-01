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
  if (c.uses_key){
    const help=(!c.key_set || c.key_check==="revoked") && c.help_url
      ? `<div class="sd"><a class="klink" tabindex="0" role="link" data-open="${esc(c.help_url)}">${esc(T("s_key_help"))}</a></div>` : "";
    const row=(title,ctl)=>`<div class="srow sub"><div class="sl"><div>${esc(title)}</div>${help}</div>${ctl}</div>`;
    if (CHECKING.has(c.id)) h+=srow(T("s_key_checking"),"","","sub");
    else if (c.key_set && !KEYEDIT.has(c.id))
      h+=row(T("s_key_set"), `<button class="btn" data-ck="replace" data-conn="${id}">${esc(T("s_key_replace"))}</button>`
        + `<button class="btn" data-ck="remove" data-conn="${id}">${esc(T("s_key_forget"))}</button>`);
    else
      h+=row(T(c.key_set?"s_key_set":"s_key_none"), `<input class="kin" type="password" autocomplete="off" spellcheck="false" data-ckey="${id}" placeholder="${esc(T("s_key_ph"))}">`
        + `<button class="btn" data-ck="save" data-conn="${id}">${esc(T("s_key_save"))}</button>`
        + (c.key_set? `<button class="btn" data-ck="cancel" data-conn="${id}">${esc(T("cancel"))}</button>` : ""));
  }
  const gd=c.guard;
  const numv=(k,lab,min,max,step,v,d,emptyOk)=>srow(T(lab),d||"",`<input class="kin num" type="number" min="${min}" max="${max}" step="${step}" data-cset="${k}" data-conn="${id}"${emptyOk?' data-empty="1"':""} aria-label="${esc(T(lab))}" value="${v==null?"":esc(v)}">`,"sub");
  // the connector's own number fields, as its row describes them (label = a key of the panel's translations)
  for (const f of c.fields||[]) h+=numv(f.key,f.label,f.min,f.max,f.step,f.value,"",f.empty_ok);
  // balance guard (2.9): warn / stop thresholds, time to answer, snooze; forecast; a pending stop with Stop now / Snooze;
  // "never auto-stop" per machine; the last stops. Texts in the UI language come from the host.
  if (gd){
    h+=srow(T("s_guard"), gd.note? "⚠ "+gd.note : T("s_guard_d"), tgl("cguard",gd.enabled,` data-conn="${id}"`), "sub");
    if (gd.enabled){
      h+=numv("guard_warn","s_guard_warn",0,1000000,1,gd.warn)+numv("guard_stop","s_guard_stop",0,1000000,1,gd.stop,gd.hint)
        +numv("guard_grace_min","s_guard_grace",1,120,1,gd.grace_min)+numv("guard_snooze_min","s_guard_snooze",5,240,5,gd.snooze_min);
      if (gd.forecast) h+=srow(gd.forecast,"","","sub");
      if (gd.pending) h+=srow("⚠ "+gd.pending.text,"",`<button class="btn" data-gact="stop_now" data-conn="${id}" data-q="${esc(gd.pending.stop_q||"")}">${esc(gd.pending.stop_label)}</button>`
        +`<button class="btn" data-gact="snooze" data-conn="${id}">${esc(gd.pending.snooze_label)}</button>`,"sub");
      for (const m of gd.machines||[]) h+=srow(m.name, T("s_guard_exempt"), tgl("gex",m.exempt,` data-conn="${id}" data-mid="${esc(m.id)}"`), "sub");
      if ((gd.log||[]).length) h+=`<div class="srow sub"><div class="sl"><div>${esc(T("s_guard_log"))}</div>${gd.log.map(l=>`<div class="sd">${esc(l)}</div>`).join("")}</div></div>`;
    }
  }
  if (c.setup) h+=srow(T("s_gh_opts"),"",sbtn(c.setup,T("s_setup")),"sub");
  return h;
};
const keyInput=id=>[...$("#sethost").querySelectorAll("input[data-ckey]")].find(e=>e.dataset.ckey===id);
