// settings page. Everything that used to be in the tray menu is here; the host's part (autostart, AI agents, Telegram,
// connectors, updates, folders) goes through WebView2 messages only ({"settings":...} -> {"settings_state":...}).
const setBox=$("#set");
let SET=null, setTimer=0;
const setOpen=()=>document.body.classList.contains("setmode");
function openSet(){ document.body.classList.add("setmode"); renderSet(); $("#set .sc").focus({preventScroll:true});   // keys scroll the page
  if (NATIVE){ host({settings:"get"}); clearInterval(setTimer); setTimer=setInterval(()=>{ if (setOpen()) host({settings:"get"}); },3000); } }
function closeSet(){ if (!setOpen()) return; document.body.classList.remove("setmode"); clearInterval(setTimer); }
$("#cfg").onclick=()=>setOpen()? closeSet() : openSet();
$("#setback").onclick=closeSet;
const HL=(k,f)=>(SET && SET.labels && SET.labels[k]) || f || k;
const tgl=(k,on,extra)=>`<button class="tgl${on?" on":""}" role="switch" aria-checked="${on?"true":"false"}" data-k="${k}"${extra||""}><i></i></button>`;
const srow=(title,desc,ctl,cls)=>(ctl=(ctl||"").replace('<button class="tgl',`<button aria-label="${esc(title)}" class="tgl`),   // a switch is named by its row
  `<div class="srow${cls?" "+cls:""}"><div class="sl"><div>${esc(title)}</div>${desc?`<div class="sd">${esc(desc)}</div>`:""}</div>${ctl}</div>`);
const sbtn=(what,txt,cls)=>`<button class="btn${cls?" "+cls:""}" data-do="${what}">${esc(txt)}</button>`;
const ckey=e=>[e.dataset.open,e.dataset.tgtok,e.dataset.ladd,e.dataset.lrm,e.dataset.lplus,e.dataset.csel,e.dataset.cbool,e.dataset.do,e.dataset.k,e.dataset.agent,e.dataset.kind,e.dataset.conn,e.dataset.ck,e.dataset.ckey,e.dataset.kp,e.dataset.cset,e.dataset.open,e.dataset.mid,e.dataset.gact].join("|");
