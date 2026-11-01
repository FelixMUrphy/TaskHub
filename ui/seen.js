/* ---------------- seen: a finished task visible ~0.7 s in the open panel ---------------- */
// the docked panel: open only between the host's "shown" and "hidden" messages - the page also lives in the
// hidden window; a browser tab: while visible
let panelOpen = !NATIVE && document.visibilityState==="visible";
const VIS=new Map(), SENT=new Set();
const seenIO=new IntersectionObserver(es=>{ for (const e of es){ const id=e.target.id.slice(2);
  if (e.isIntersecting) { if (!VIS.has(id)) VIS.set(id, Date.now()); } else VIS.delete(id); } }, {root:$("#list"), threshold:0.6});
// sent once per finish: a task id can be reused (re-registered), its next finish is a new "unseen"
const runKey=id=>{ const t=findTask(id); return id+"@"+(t&&t.finished||0); };
function markSeen(ids){
  ids=ids.filter(id=>!SENT.has(runKey(id))); if (!ids.length) return;
  const keys=ids.map(runKey);
  keys.forEach(k=>SENT.add(k)); ids.forEach(id=>SESSION.add(id));
  api("POST","/api/settings",{seen:ids}).catch(()=>keys.forEach(k=>SENT.delete(k)));
}
function panelShown(on){
  if (on && !panelOpen){ SESSION=new Set(); HOPEN=false; render(); }   // a new opening: what was seen moves now
  panelOpen=on;
}
setInterval(()=>{
  if (!panelOpen || !data) return;
  const t0=Date.now()-700, ids=[];
  for (const [id,since] of VIS) if (since<=t0){ const t=findTask(id); if (t && !ACTIVE.has(t.state) && !t.seen) ids.push(id); }
  markSeen(ids);
}, 300);
if (!NATIVE) window.addEventListener("focus",()=>panelShown(true));
document.addEventListener("visibilitychange",()=>{ if (!NATIVE) panelShown(document.visibilityState==="visible"); });
document.addEventListener("keydown",e=>{ if(e.key==="Escape" && setOpen() && !modal){ e.preventDefault(); closeSet(); return; } if(e.key==="Escape" && !modal && !Object.values(ui).some(u=>u.formFor)){ if (NATIVE) host("hide"); else api("POST","/api/ui/hide",{}).catch(()=>{}); } });
// docked panel: resize by its left edge
$("#grip").addEventListener("mousedown",e=>{ if(e.button!==0) return; e.preventDefault(); host("resize"); });
