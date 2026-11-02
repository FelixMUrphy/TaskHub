/* ---------------- overlay scrollbar ---------------- */
const SC=$("#list"), SB=$("#sb"), THUMB=$("#sb i");
let sbHide=0, sbHover=false, sbDrag=null;
function sbUpd(){
  const h=SC.clientHeight, H=SC.scrollHeight, top=SC.scrollTop, more=H-h;
  SC.classList.toggle("ft", top>1); SC.classList.toggle("fb", more-top>1);
  if (more<=1){ SB.classList.add("none"); return; }
  SB.classList.remove("none");
  const r=SC.getBoundingClientRect(); SB.style.top=r.top+"px"; SB.style.height=h+"px";
  const th=Math.max(28, h*h/H); THUMB.style.height=th+"px"; THUMB.style.transform=`translateY(${((h-th)*top/more).toFixed(1)}px)`;
}
function sbShow(){ SB.classList.add("on"); clearTimeout(sbHide); sbHide=setTimeout(()=>{ if(!sbHover && !sbDrag) SB.classList.remove("on"); }, 900); }
SC.addEventListener("scroll",()=>{ sbUpd(); sbShow(); },{passive:true});
document.addEventListener("mousemove",e=>{ if (e.clientX>=innerWidth-18 && !SB.classList.contains("none")) sbShow(); },{passive:true});
SB.addEventListener("mouseenter",()=>{ sbHover=true; sbShow(); });
SB.addEventListener("mouseleave",()=>{ sbHover=false; sbShow(); });
SB.addEventListener("pointerdown",e=>{
  if (e.button!==0) return; e.preventDefault();
  const h=SC.clientHeight, H=SC.scrollHeight, th=THUMB.offsetHeight, tr=THUMB.getBoundingClientRect();
  if (e.target!==THUMB){ SC.scrollBy({top:(e.clientY<tr.top?-1:1)*h*0.9}); return; }   // track: a page up / down
  sbDrag={y:e.clientY, top:SC.scrollTop, k:(H-h)/Math.max(1,h-th)}; SB.classList.add("drag"); SB.setPointerCapture(e.pointerId);
});
SB.addEventListener("pointermove",e=>{ if (sbDrag) SC.scrollTop=sbDrag.top+(e.clientY-sbDrag.y)*sbDrag.k; });
const sbEnd=()=>{ if (!sbDrag) return; sbDrag=null; SB.classList.remove("drag"); sbShow(); };
SB.addEventListener("pointerup",sbEnd); SB.addEventListener("pointercancel",sbEnd);
new ResizeObserver(sbUpd).observe(SC);
new MutationObserver(()=>requestAnimationFrame(sbUpd)).observe(SC,{childList:true,subtree:true,attributes:true,attributeFilter:["open","class","style"]});
document.addEventListener("toggle",()=>requestAnimationFrame(sbUpd),true);
// keys (arrows, Page Up/Down, Home/End, Space) scroll the list unless a control has the focus
window.addEventListener("focus",()=>{ if (document.activeElement===document.body) SC.focus({preventScroll:true}); });
document.addEventListener("mousedown",e=>{ if (!e.target.closest("button,input,select,textarea,a,summary,#set,#sb")) setTimeout(()=>{ if (document.activeElement===document.body) SC.focus({preventScroll:true}); }); });
setInterval(()=>{ if(!data) return; for(const t of data.tasks){ const el=document.getElementById("t-"+t.id); if(el) $(".tm",el).textContent=times(t);} },1000);
setInterval(()=>{ if (data) render(); },30000);   // "5 min ago", history and attention move with the clock
try{ if(localStorage.th_f) filt=localStorage.th_f; }catch(e){}
if (NATIVE){ document.documentElement.classList.add("native"); try{ window.chrome.webview.addEventListener("message",e=>{ const d=e.data; if(d && d.look){ LOOK=d.look; applyLook(); }
  if(d && d.shown) panelShown(true);
  if(d && d.paint){ requestAnimationFrame(()=>requestAnimationFrame(()=>host("painted"))); }
  if(d && d.open_settings) openSet();
  if(d && d.settings_size) settingsRoom(d.settings_size);
  if(d && d.settings_gone) settingsGone();
  if(d && d.close_settings) closeSet(true);
  if(d && d.settings_state) settingsReply(d);
  if(d && d.control_reply){ const r=d.control_reply, t=findTask(r.task), c=t && (t.controls||[]).find(x=>x.id===r.id); snack(r.ok? T("sent",c?c.label:r.id) : T("error")+": "+(r.error||"")); }
  if(d && d.hidden){ panelShown(false); closeSet(); const a=document.activeElement; if (a && a.closest && a.closest("header,#set")) a.blur(); } }); }catch(e){} host("look"); }
else { for (const id of ["transpl","transpr","ctranspl","ctranspr"]) $("#"+id).style.display="none"; }
applyLang();
$("#lang").onchange=e=>api("POST","/api/settings",{language:e.target.value}).catch(err=>snack(err.message));
setFilt(filt); loop();
if (location.hash.length>1) setTimeout(()=>flash(location.hash.slice(1)),600);
