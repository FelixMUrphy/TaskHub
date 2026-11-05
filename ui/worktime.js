/* ---------------- work hours: the buttons, two bars and the amount, above the list; off = nothing here ----------------
   The numbers come from /api/worktime/status: when the hub says something changed (the mark in the task snapshot), after a button, and every 30 s,
   as the bars move with the clock while nothing changes. */
let WT=null, wtMark=null, wtAsked=0, wtBusy=false;
const hm=s=>{ const m=Math.max(0,Math.floor(s/60)); return Math.floor(m/60)+":"+String(m%60).padStart(2,"0"); };
function wtMoney(v, cur){
  try{ return new Intl.NumberFormat(LOC(),{style:"currency",currency:cur,maximumFractionDigits:0}).format(v); }
  catch(e){ return Math.round(v)+" "+cur; }   // a code the browser doesn't know
}
function wtBar(name, b, deficit, pools){
  const parts=[hm(b.worked_seconds)+" / "+hm(b.full_seconds)];
  if (b.above_seconds>0) parts.push("+"+hm(b.above_seconds));
  if (deficit>0) parts.push(T("wt_deficit",hm(deficit)));
  // the overtime so far is its own item: it isn't what's above the bar's norm, a month can have overtime long before its end
  const of=pools? [["weekday_seconds","wt_weekday"],["weekend_seconds","wt_weekend"],["holiday_seconds","wt_holiday"]].filter(([k])=>pools[k]>0).map(([k,w])=>T(w,hm(pools[k]))) : [];
  if (of.length) parts.push(T("wt_overtime",of.join(", ")));
  const word=T("wt_st_"+b.state), val=parts.join(" · ");
  return `<div class="lim" data-wt="${esc(b.state)}" title="${esc(word)}" role="group" aria-label="${esc(T(name)+": "+val+". "+word)}"><div class="lt"><span>${esc(T(name))}</span><span class="lv">${esc(val)}</span></div>`
    +`<div class="lb" aria-hidden="true"><i style="width:${(Math.max(0,Math.min(1,b.fill))*100).toFixed(2)}%"></i></div></div>`;
}
// what waits for the user: a month to close, the month's holidays to confirm; both are answered in Settings (the panel has no room for them)
function wtNeed(){
  const w=[], m=(WT.unclosed_months||[])[0];
  if (m) w.push(T("wt_need_close",wcMonthName(m)));
  if (WT.holidays_to_confirm) w.push(T("wt_need_hol"));
  if (!w.length) return "";
  const t=esc(w.join(" · "));
  return `<div class="wtm wtn">${NATIVE? `<a class="klink" tabindex="0" role="link" data-wopen="1">${t}</a>` : t}</div>`;
}
function renderWt(){
  const box=$("#wt");
  let h="";
  if (WT && WT.broken) h=`<div class="wth"><span class="limh">${esc(T("wt_title"))}</span></div><div class="limn">${esc(T("wt_broken"))}</div>`;
  else if (WT){
    const s=WT.session, btn=(a,w,cls)=>`<button class="btn${cls?" "+cls:""}" data-wa="${a}">${esc(T(w))}</button>`;
    const state=s? T(s.state==="running"?"wt_running":"wt_paused",hm(s.worked_seconds)) : "";
    const btns=!s? btn("start","wt_start","primary") : (s.state==="running"? btn("pause","wt_pause") : btn("resume","wt_resume","primary"))+btn("finish","wt_finish");
    // in the display currency when a rate is there; else in the salary's, and said why
    const m=WT.money, d=m.display, c=d && !d.missing? d : m;
    const money=m.set? [T("wt_earned",wtMoney(c.earned,c.currency)), c.overtime>0? T("wt_of_overtime",wtMoney(c.overtime,c.currency)) : ""].filter(Boolean).join(" · ") : "";
    const rate=!m.set || !d? "" : d.missing? (d.why==="pending"? T("wt_rate_wait") : d.why==="unsupported"? T("wt_rate_unsupported",d.currency) : T("wt_rate_missing",d.currency))
      : d.manual? T("wt_rate_own") : d.stale && d.rate_date? T("wt_rate_of",wcDate(d.rate_date).toLocaleDateString(LOC())) : "";
    h=`<div class="wth"><span class="limh">${esc(T("wt_title"))}</span>${state?`<span class="wts">${esc(state)}</span>`:""}${NATIVE?`<a class="klink wtl" tabindex="0" role="link" data-wopen="1">${esc(T("wt_details"))}</a>`:""}<span class="wtb">${btns}</span></div>`
      +wtBar("wt_week",WT.week,WT.deficit_seconds,null)+wtBar("wt_month",WT.month,WT.deficit_seconds,WT.pools)
      +(money?`<div class="wtm">${esc(money)}${rate?` <span class="wtr">${esc(rate)}</span>`:""}</div>`:"")
      +wtNeed();
  }
  if (box.__h===h) return;
  // the keyboard stays on the same button across a redraw, which comes with the clock; only while this window has the focus, a redraw must not take it
  const f=document.activeElement, had=box.contains(f) && document.hasFocus(), was=had && f.dataset.wa, link=had && f.dataset.wopen? [...box.querySelectorAll("[data-wopen]")].indexOf(f) : -1;
  box.innerHTML=h; box.__h=h; box.classList.toggle("on",!!h);
  if (h){ box.setAttribute("role","group"); box.setAttribute("aria-label",T("wt_title")); }
  if (had) ((link>=0 && box.querySelectorAll("[data-wopen]")[link]) || (was && box.querySelector(`button[data-wa="${was}"]`)) || box.querySelector("button") || $("#list")).focus({preventScroll:true});
}
async function wtLoad(){
  const asked=++wtAsked;
  try{ const j=await api("GET","/api/worktime/status"); if (asked===wtAsked){ WT=j.enabled? j : null; renderWt(); } }
  catch(e){}   // the hub is away: the line above the list says so, the last numbers stay
}
// from each task snapshot: gone = off, another mark = something changed
function wtFollow(){
  const mark=data.worktime==null? null : data.worktime;
  if (mark===null){ wtMark=null; if (WT){ wtAsked++; WT=null; } renderWt(); return; }
  if (mark!==wtMark){ wtMark=mark; wtLoad(); wcFollow(); }
  else renderWt();   // the language may have changed
}
// today is a vacation day: remove the mark (a working day), or keep it and count the hours as overtime; the dialog's buttons say which
async function wtVacation(){
  const d=document.getElementById("dlg"), [ok,no]=d.querySelectorAll("button"), was=[ok.textContent,no.textContent];
  ok.textContent=T("wt_vac_unmark"); no.textContent=T("wt_vac_keep");
  try{ return await ask(T("wt_vac_q"))? "unmark" : "keep"; } finally{ ok.textContent=was[0]; no.textContent=was[1]; }
}
$("#wt").addEventListener("keydown",e=>{ if ((e.key==="Enter"||e.key===" ") && e.target.dataset && e.target.dataset.wopen){ e.preventDefault(); e.target.click(); } });
$("#wt").addEventListener("click",async e=>{
  if (e.target.dataset && e.target.dataset.wopen){ setTab="worktime"; if (!setOpen()) openSet(); else { renderSet(); } return; }
  // the second click of a double click would land on the button that has just taken this one's place
  const b=e.target.closest("button[data-wa]"); if (!b || wtBusy || e.detail>1) return;
  const a=b.dataset.wa; wtBusy=true; const asked=++wtAsked;
  const body=a==="start"||a==="resume"? {source:"panel"} : {};
  if (a==="start" && WT && WT.today_vacation) body.vacation=await wtVacation();   // the spec: asked, not decided for the user
  try{ const j=await api("POST","/api/worktime/"+a,body); if (asked===wtAsked){ WT=j; renderWt(); } }
  catch(er){ snack(T("wt_not_done")); wtLoad(); }   // refused (pressed elsewhere meanwhile): show what is true now
  finally{ wtBusy=false; }
});
setInterval(()=>{ if (wtMark!==null && !document.hidden) wtLoad(); },30000);
document.addEventListener("visibilitychange",()=>{ if (wtMark!==null && !document.hidden) wtLoad(); });
