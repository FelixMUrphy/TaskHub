// Telegram, set up right on the settings page (it used to be a window of its own). Not set up: how to get a bot token
// (@BotFather), the token field and Connect. Waiting: the t.me link and its QR code (the Telegram account may be on the
// phone), the token field again (a rejected token, another bot), New link, Disconnect. Linked: the two switches, Send test,
// New link, Disconnect. The words come from the host.
let TGBUSY=0;   // a token sent, the host is checking it with Telegram (up to ~10 s): "Checking…" instead of the field
const tgRows=tg=>{
  const x=tg.texts||{}, sub=(inner,ctl)=>`<div class="srow sub"><div class="sl">${inner}</div>${ctl||""}</div>`;
  let h=srow("Telegram", x.state||"", "");
  if (tg.warning) h+=srow("⚠ "+tg.warning,"","","sub");
  const btns=(...b)=>`<div class="srow sub tgb">${b.map(([w,txt,q])=>`<button class="btn" data-do="${w}"${q?` data-q="${esc(q)}"`:""}>${esc(txt)}</button>`).join("")}</div>`;
  if (TGBUSY && tg.configured) h+=srow(T("s_key_checking"),"","","sub");   // a token / New link being checked
  const field=()=>TGBUSY? (tg.configured? "" : srow(T("s_key_checking"),"","","sub"))
    : sub(`<div>${esc(x.token||"")}</div>`, `<input class="kin" type="password" autocomplete="off" spellcheck="false" data-tgtok="" aria-label="${esc(x.token||"")}" placeholder="123456789:AA...">`
      + `<button class="btn" data-do="tg_pair">${esc(x.connect||"")}</button>`);
  if (!tg.configured){
    // the steps; "@BotFather" opens the bot in Telegram
    const intro=esc(x.intro||"").replace("@BotFather",`<a class="klink" tabindex="0" role="link" data-open="https://t.me/BotFather">@BotFather</a>`).replace(/\n/g,"<br>");
    h+=sub(`<div class="sd">${intro}</div>`);
    return h+field();
  }
  if (!tg.linked && tg.link)
    h+=`<div class="srow sub tgqr"><div class="sl"><div class="sd">${esc(x.qr||"")}</div>`
      + `<div class="sd">${esc(x.open||"")} <a class="klink" tabindex="0" role="link" data-open="${esc(tg.link)}">${esc(tg.link)}</a></div></div>`
      + (tg.qr? `<img class="qr" src="${esc(tg.qr)}" alt="QR">` : "") + `</div>`;
  if (!tg.linked) h+=field();
  if (tg.linked)
    h+=srow(x.away||"","",tgl("tg_away",tg.away),"sub")+srow(x.progress||"","",tgl("tg_progress",tg.progress),"sub");
  // New link while linked unlinks the working chat until Start is pressed again: asks first, like Disconnect
  h+=btns(...(tg.linked? [["tg_test",x.test]] : []), ["tg_relink",x.relink,tg.linked?x.relink_q:""], ["tg_disconnect",x.disconnect,x.disconnect_q]);
  return h;
};
// Connect: the token goes to the host once (it keeps it in the Credential Manager), the field is emptied at once;
// "Checking…" until the host answers (an error, or "Waiting for Start"), at most 20 s
function tgPair(){
  const i=$("#sethost input[data-tgtok]"), v=i? i.value.trim() : ""; if (!v){ if (i) i.focus(); return; }
  i.value=""; tgBusy(()=>host({settings:"do",what:"tg_pair",token:v}));
}
function tgBusy(send){ clearTimeout(TGBUSY); TGBUSY=setTimeout(()=>{ TGBUSY=0; reSet(); },20000); reSet(); send(); }
