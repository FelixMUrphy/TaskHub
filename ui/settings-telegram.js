// Telegram, set up right on the settings page (it used to be a window of its own). Not set up: how to get a bot token
// (@BotFather), the token field and Connect. Waiting: the t.me link and its QR code (the Telegram account may be on the
// phone), New link, Disconnect. Linked: the two switches, Send test, New link, Disconnect. The words come from the host.
const tgRows=tg=>{
  const x=tg.texts||{}, sub=(inner,ctl)=>`<div class="srow sub"><div class="sl">${inner}</div>${ctl||""}</div>`;
  let h=srow("Telegram", x.state||"", "");
  if (tg.warning) h+=srow("\u26a0 "+tg.warning,"","","sub");
  const btns=(...b)=>`<div class="srow sub tgb">${b.map(([w,txt,q])=>`<button class="btn" data-do="${w}"${q?` data-q="${esc(q)}"`:""}>${esc(txt)}</button>`).join("")}</div>`;
  if (!tg.configured){
    // the steps; "@BotFather" opens the bot in Telegram
    const intro=esc(x.intro||"").replace("@BotFather",`<a class="klink" tabindex="0" role="link" data-open="https://t.me/BotFather">@BotFather</a>`).replace(/\n/g,"<br>");
    h+=sub(`<div class="sd">${intro}</div>`);
    h+=sub(`<div>${esc(x.token||"")}</div>`, `<input class="kin" type="password" autocomplete="off" spellcheck="false" data-tgtok="" aria-label="${esc(x.token||"")}" placeholder="123456789:AA...">`
      + `<button class="btn" data-do="tg_pair">${esc(x.connect||"")}</button>`);
    return h;
  }
  if (!tg.linked && tg.link)
    h+=`<div class="srow sub tgqr"><div class="sl"><div class="sd">${esc(x.qr||"")}</div>`
      + `<div class="sd">${esc(x.open||"")} <a class="klink" tabindex="0" role="link" data-open="${esc(tg.link)}">${esc(tg.link)}</a></div></div>`
      + (tg.qr? `<img class="qr" src="${esc(tg.qr)}" alt="QR">` : "") + `</div>`;
  if (tg.linked)
    h+=srow(x.away||"","",tgl("tg_away",tg.away),"sub")+srow(x.progress||"","",tgl("tg_progress",tg.progress),"sub");
  h+=btns(...(tg.linked? [["tg_test",x.test]] : []), ["tg_relink",x.relink], ["tg_disconnect",x.disconnect,x.disconnect_q]);
  return h;
};
// Connect: the token goes to the host once (it keeps it in the Credential Manager), the field is emptied at once
function tgPair(){
  const i=$("#sethost input[data-tgtok]"), v=i? i.value.trim() : ""; if (!v){ if (i) i.focus(); return; }
  i.value=""; snack(T("s_key_checking")); host({settings:"do",what:"tg_pair",token:v});
}
