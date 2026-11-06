// Discord, set up right on the settings page. Not set up: the steps, a link to the how-to page and the token field. Waiting:
// the link that adds the bot to a server, Check again, Disconnect. Linked: the two switches, Send test, Disconnect. The words
// come from the host. The token goes to the host once and is never shown again.
let DCBUSY=0;   // something sent, the host is asking Discord: "Checking…" and no buttons until the answer
const dcRows=d=>{
  const x=d.texts||{}, sub=(inner,ctl)=>`<div class="srow sub"><div class="sl">${inner}</div>${ctl||""}</div>`;
  let h=srow("Discord", x.state||"", "");
  if (d.warning) h+=srow("⚠ "+d.warning,"","","sub");
  if (DCBUSY) return h+srow(T("s_key_checking"),"","","sub");
  const btns=(...b)=>`<div class="srow sub tgb">${b.map(([w,txt,q])=>`<button class="btn" data-do="${w}"${q?` data-q="${esc(q)}"`:""}>${esc(txt)}</button>`).join("")}</div>`;
  const link=(url,txt)=>`<a class="klink" tabindex="0" role="link" data-open="${esc(url)}">${esc(txt)}</a>`;
  const field=()=>sub(`<div>${esc(x.token||"")}</div>`, `<input class="kin" type="password" autocomplete="off" spellcheck="false" data-dctok="dc" aria-label="${esc(x.token||"")}">`
    + `<button class="btn" data-do="dc_pair">${esc(x.connect||"")}</button>`);
  const out=["dc_disconnect",x.disconnect,x.disconnect_q];
  if (!d.configured) return h+sub(`<div class="sd">${esc(x.intro||"").replace(/\n/g,"<br>")}</div><div class="sd">${link(d.help_url,T("s_key_help"))}</div>`)+field();
  if (d.status==="invalid token") return h+sub(`<div class="sd">${link(d.help_url,T("s_key_help"))}</div>`)+field()+btns(out);
  if (!d.linked && d.invite) h+=sub(`<div class="sd">${link(d.invite,x.invite||"")}</div>`);
  if (d.linked) h+=srow(x.away||"","",tgl("dc_away",d.away),"sub")+srow(x.progress||"","",tgl("dc_progress",d.progress),"sub");
  return h+btns(...(d.linked? [["dc_test",x.test]] : [["dc_retry",x.retry]]), out);
};
function dcBusy(send){ clearTimeout(DCBUSY); DCBUSY=setTimeout(()=>{ DCBUSY=0; reSet(); },20000); reSet(); send(); }
function dcPair(){
  const i=$("#sethost input[data-dctok]"), v=i? i.value.trim() : ""; if (!v){ if (i) i.focus(); return; }
  i.value=""; dcBusy(()=>host({settings:"do",what:"dc_pair",token:v}));
}
