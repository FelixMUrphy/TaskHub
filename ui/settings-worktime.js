/* ---------------- work hours in Settings: the switch and what's asked when it's turned on, the calendar, a day's window, the settings ----------------
   Hours go through the HTTP API like the panel's buttons; the salary and the rest of the settings only through the host's messages. Every change to hours is
   shown first: the same request with ?preview=1 says what it would do to the totals, then Save sends it. */
let WC={month:null, loaded:null, rows:{}, marks:{}, today:null, start:null, many:false, sel:new Set(), day:null, dayData:null, edit:null, pv:null, err:false, setupDays:null, closed:[], names:{}, close:null, hol:null, holOff:new Set(), holOwn:[]};
const wcMonth=d=>d.slice(0,7);
const wcDate=d=>new Date(d+"T12:00:00");
function wtErr(e){
  const m=String(e && e.message || e);
  for (const [re,k] of [[/overlaps/,"wt_e_overlap"],[/future/,"wt_e_future"],[/before the start/,"wt_e_before"],[/is closed/,"wt_e_closed"],[/has changed since/,"wt_e_changed"],[/is open/,"wt_e_open"],[/after start/,"wt_e_end"],[/at most \d+ hours|number of hours/,"wt_e_hours"]])
    if (re.test(m)) return T(k);
  return T("wt_not_done");
}
async function wcLoad(month){
  const m=month||WC.month; if (!m) return;
  const [y,mo]=m.split("-").map(Number), last=new Date(y,mo,0).getDate();
  try{
    const j=await api("GET",`/api/worktime/breakdown?from=${m}-01&to=${m}-${String(last).padStart(2,"0")}`);
    WC.month=m; WC.loaded=m; WC.today=j.today; WC.start=j.start_date; WC.rows={}; WC.marks={}; WC.names={}; WC.closed=j.closed_months||[];
    for (const r of j.days) WC.rows[r.date]=r;
    for (const r of j.marks){ WC.marks[r.date]=r.kind; if (r.name) WC.names[r.date]=r.name; }
    WC.err=false;
    if (WC.day) await wcOpenDay(WC.day,true);
  }catch(e){ WC.err=!WC.loaded; snack(wtErr(e)); }
  reSet();
}
async function wcOpenDay(d, quiet){
  try{ WC.dayData=await api("GET","/api/worktime/days/"+d); WC.day=d; }catch(e){ snack(wtErr(e)); }
  if (!quiet) reSet();
}
function wcFollow(){ if (setOpen() && currentTab()==="worktime" && WC.loaded) wcLoad(); else WC.loaded=null; }
function wcShift(n){ WC.sel.clear(); WC.pv=null; const [y,m]=WC.month.split("-").map(Number), d=new Date(y,m-1+n,1); wcLoad(d.getFullYear()+"-"+String(d.getMonth()+1).padStart(2,"0")); }
// ---- what a change would do; then Save sends the same request
async function wcAsk(method, path, body, what){
  const q=path+(path.includes("?")?"&":"?")+"preview=1";
  try{ const r=await api(method,q,body); WC.pv={method,path,body,r,what}; }catch(e){ snack(wtErr(e)); WC.pv=null; }
  reSet();
}
async function wcCommit(){
  const p=WC.pv; if (!p) return; WC.pv=null; WC.edit=null;
  try{ await api(p.method,p.path,p.body); if (p.what==="many"){ WC.sel.clear(); WC.many=false; } if (p.what==="hol"){ WC.holOff.clear(); WC.holOwn=[]; } await wtLoad(); }catch(e){ snack(wtErr(e)); }
  WC.close=null; if (p.what==="close"||p.what==="hol") WC.hol=null;   // the card's numbers come from these hours
  await wcLoad();
}
function wcPreview(what){
  const p=WC.pv; if (!p || p.what!==what) return "";
  const a=p.r.before, b=p.r.after, rows=[], ch=(w,x,y,f)=>{ if (x!==y) rows.push(`${w}: ${f(x)} → ${f(y)}`); };
  ch(T("wt_pv_deficit"),a.deficit_seconds,b.deficit_seconds,hm);
  for (const [k,w] of [["weekday_seconds","wt_pv_weekday"],["weekend_seconds","wt_pv_weekend"],["holiday_seconds","wt_pv_holiday"]]) ch(T(w),a.pools[k],b.pools[k],hm);
  if (a.money.set) ch(T("wt_pv_earned"),a.money.earned,b.money.earned,v=>wtMoney(v,a.money.currency));
  return `<div class="wcpv" role="status"><div class="sd">${esc(T("wt_pv_t"))}</div><div>${rows.length? rows.map(esc).join("<br>") : esc(T("wt_pv_none"))}</div>`
    +`<div class="wcb"><button class="btn primary" data-wc="commit">${esc(T("wt_save"))}</button><button class="btn" data-wc="uncommit">${esc(T("wt_cancel"))}</button></div></div>`;
}
// ---- the month
function wcCalendar(){
  if (!WC.loaded) return WC.err? `<div class="srow"><div class="sl">${esc(T("wt_load_failed"))}</div><button class="btn" data-wc="retry">${esc(T("wt_retry"))}</button></div>`
    : `<div class="srow"><div class="sl sd">…</div></div>`;
  const [y,m]=WC.month.split("-").map(Number), first=new Date(y,m-1,1), days=new Date(y,m,0).getDate(), pad=(first.getDay()+6)%7;
  const title=first.toLocaleDateString(LOC(),{month:"long",year:"numeric"});
  const heads=[...Array(7)].map((_,i)=>`<span class="wch">${esc(new Date(2024,0,1+i).toLocaleDateString(LOC(),{weekday:"short"}))}</span>`).join("");
  let cells="<span></span>".repeat(pad);
  for (let n=1;n<=days;n++){
    const d=`${WC.month}-${String(n).padStart(2,"0")}`, r=WC.rows[d], kind=WC.marks[d]||(r?r.kind:""), worked=r&&r.worked_seconds>0? hm(r.worked_seconds) : "";
    const short=r&&r.closed&&r.to_deficit_seconds>0, cls=["wcd",d===WC.today?"now":"",d===WC.day&&!WC.many?"on":"",WC.sel.has(d)?"sel":"",short?"short":""].filter(Boolean).join(" ");
    const word=WC.names[d] || (kind? T("wt_k_"+kind) : "");
    cells+=`<button class="${cls}" data-wd="${d}"${kind?` data-kd="${esc(kind)}"`:""} aria-pressed="${WC.many? WC.sel.has(d) : d===WC.day}" aria-label="${esc([wcDate(d).toLocaleDateString(LOC(),{day:"numeric",month:"long"}),word,worked].filter(Boolean).join(", "))}"><b>${n}</b><span>${esc(worked)}</span></button>`;
  }
  const many=WC.many? `<div class="srow sub"><div class="sl">${esc(T("wt_selected",WC.sel.size))}</div><div class="wcb"><input class="kin num" type="number" min="0" max="24" step="any" data-wf="many_hours" aria-label="${esc(T("wt_total_hours"))}" placeholder="8"><button class="btn" data-wc="many_set"${WC.sel.size?"":" disabled"}>${esc(T("wt_set_total"))}</button>`
      +`<select data-wc-kind="many" aria-label="${esc(T("wt_day_type"))}"${WC.sel.size?"":" disabled"}>${wcKinds("")}</select></div></div>` : "";
  return `<div class="wcn"><button class="ib" data-wc="prev" aria-label="${esc(T("wt_prev"))}">‹</button><b>${esc(title)}</b><button class="ib" data-wc="next" aria-label="${esc(T("wt_next"))}">›</button><span class="sp"></span>`
    +(WC.closed.includes(WC.month)? `<span class="sd">${esc(T("wt_closed"))}</span>`+(WC.closed.slice().sort().pop()===WC.month? `<button class="btn" data-wc="reopen">${esc(T("wt_reopen"))}</button>` : "") : "")
    +`<button class="btn${WC.many?" primary":""}" data-wc="many" aria-pressed="${WC.many}">${esc(T("wt_many"))}</button></div><div class="wcg">${heads}${cells}</div>${many}${WC.many?wcPreview("many"):""}`;
}
function wcKinds(cur){ return [["","wt_k_pick"],["normal","wt_k_normal"],["holiday","wt_k_holiday"],["vacation","wt_k_vacation"]].filter(([v])=>v||!cur).map(([v,k])=>`<option value="${v}"${(cur||"")===v?" selected":""}>${esc(T(k))}</option>`).join(""); }
// ---- a day
const wcClock=iso=>new Date(iso).toLocaleTimeString(LOC(),{hour:"2-digit",minute:"2-digit"});
const wcLocal=iso=>{ const d=new Date(iso), p=n=>String(n).padStart(2,"0"); return `${d.getFullYear()}-${p(d.getMonth()+1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`; };
const wcIso=v=>{ const d=new Date(v); return isNaN(d)? "" : d.toISOString().replace(/\.\d+Z$/,"Z"); };
function wcForm(d, x){   // adds an interval (no x) or changes one
  const id=x? x.id : "new", dur=x && !x.start;
  const v=(k,fall)=>`value="${esc(fall)}" data-wf="${d}_${id}_${k}"`;   // keyed by the day: what was typed for one day never shows on another
  return `<div class="wcf"><label>${esc(T("wt_from"))}<input class="kin" type="datetime-local" ${v("start",x&&x.start? wcLocal(x.start) : dur? "" : d+"T09:00")}></label>`
    +`<label>${esc(T("wt_to"))}<input class="kin" type="datetime-local" ${v("end",x&&x.end? wcLocal(x.end) : dur? "" : d+"T17:00")}></label>`
    +`<label>${esc(T("wt_note"))}<input class="kin" maxlength="500" ${v("note",x?x.note:"")}></label>`
    +`<div class="wcb"><button class="btn" data-wc="${x?"save_edit":"add"}" data-wi="${id}">${esc(T(x?"wt_check":"wt_add"))}</button>${x?`<button class="btn" data-wc="edit_off">${esc(T("wt_cancel"))}</button>`:""}</div></div>`;
}
function wcDay(){
  const j=WC.dayData; if (!j || !WC.day || WC.many) return "";
  const r=j.row, kind=j.mark? j.mark.kind : "normal", lines=[];
  if (r){
    lines.push(T("wt_r_worked",hm(r.worked_seconds)),T("wt_r_norm",hm(r.norm_seconds)));
    if (r.to_deficit_seconds>0) lines.push(T("wt_r_short",hm(r.to_deficit_seconds)));
    if (r.paid_off_seconds>0) lines.push(T("wt_r_cover",hm(r.paid_off_seconds)));
    if (r.to_pool_seconds>0) lines.push(T("wt_overtime",T("wt_"+r.pool,hm(r.to_pool_seconds))));
  }
  const edit=j.editable, ivs=(j.sessions||[]).flatMap(s=>s.intervals);
  const rows=ivs.map(x=>{
    if (WC.edit===x.id && edit) return wcForm(WC.day,x);
    const what=x.start? wcClock(x.start)+"–"+(x.end? wcClock(x.end) : T("wt_now"))+" ("+hm(x.duration_seconds)+")" : hm(x.duration_seconds)+" · "+T("wt_no_times");
    return `<div class="srow sub"><div class="sl"><div>${esc(what)}</div>${x.note?`<div class="sd">${esc(x.note)}</div>`:""}</div>`
      +(edit && !x.running? `<div class="wcb"><button class="btn" data-wc="edit" data-wi="${x.id}">${esc(T("wt_edit"))}</button><button class="btn" data-wc="del" data-wi="${x.id}">${esc(T("wt_delete"))}</button></div>` : "")+`</div>`;
  }).join("");
  const title=wcDate(WC.day).toLocaleDateString(LOC(),{weekday:"long",day:"numeric",month:"long"});
  return `<div class="scard wcday" role="group" aria-label="${esc(title)}"><div class="srow"><div class="sl"><div><b>${esc(title)}</b></div>${j.name?`<div class="sd">${esc(j.name)}</div>`:""}${lines.length?`<div class="sd">${esc(lines.join(" · "))}</div>`:""}${j.closed_month?`<div class="sd">${esc(T("wt_closed"))}</div>`:""}</div>`
    +(j.closed_month? "" : `<select data-wc-kind="day" aria-label="${esc(T("wt_day_type"))}">${wcKinds(kind)}</select>`)+`</div>${rows}`
    +(edit? `<div class="srow sub"><div class="sl">${esc(T("wt_total_hours"))}</div><div class="wcb"><input class="kin num" type="number" min="0" max="24" step="any" data-wf="${WC.day}_hours" aria-label="${esc(T("wt_total_hours"))}"><button class="btn" data-wc="day_set">${esc(T("wt_set_total"))}</button></div></div>`
      +(WC.edit? "" : wcForm(WC.day,null)) : "")
    +(j.can_undo? `<div class="srow sub"><div class="sl"></div><button class="btn" data-wc="undo">${esc(T("wt_undo"))}</button></div>` : "")
    +wcPreview("day")+`</div>`;
}
// ---- the settings
// the API's ranges, checked here first so a refusal is said in words (the host's check stays the one that counts)
const WT_MAX={salary:1e12, weekly_norm_hours:168, manual_rate:1e9, fx_manual_rate:1e9, weekday_mult:100, weekend_mult:100, holiday_mult:100, tolerance_pct:100};
const WT_ABOVE0=new Set(["weekly_norm_hours","manual_rate","fx_manual_rate"]);
// a number typed for a setting: the value to send, or undefined if it isn't one the API takes
function wtNum(k,v){
  if (k==="salary_currency"||k==="display_currency") return /^[A-Za-z]{3}$/.test(v)? v.toUpperCase() : undefined;
  if (k==="rate_source") return v==="ecb"||v==="nbu"? v : undefined;
  if (k==="rate_time") return /^([01]\d|2[0-3]):[0-5]\d$/.test(v)? v : undefined;
  if (k==="country") return /^[A-Za-z]{2}$/.test(v)? v.toUpperCase() : undefined;
  if (k==="region") return /^[A-Za-z]{2}-[A-Za-z0-9]{1,3}$/.test(v) && v.slice(0,2).toUpperCase()===(SET.worktime.settings||{}).country? v.toUpperCase() : undefined;
  const n=Number(v); if (v==="" || !isFinite(n) || n<0 || n>WT_MAX[k] || (WT_ABOVE0.has(k) && n===0)) return undefined;
  return n;
}
function wtCountry(c){ try{ return "("+new Intl.DisplayNames([LOC()],{type:"region"}).of(c)+")"; }catch(e){ return ""; } }
const wcMonthName=m=>wcDate(m+"-01").toLocaleDateString(LOC(),{month:"long",year:"numeric"});
// ---- closing a month and the month's holidays: cards above the calendar while the panel's status asks for them
async function wcLoadClose(m){ try{ WC.close=await api("GET","/api/worktime/months/"+m); }catch(e){ WC.close={month:m,failed:true}; } reSet(); }
const wcPlace=()=>{ const s=(SET&&SET.worktime&&SET.worktime.settings)||{}; return (s.country||"")+"|"+(s.region||""); };
async function wcLoadHol(m, now){ const place=wcPlace();
  try{ const h=await api("GET","/api/worktime/holidays?month="+m+(now?"&refresh=1":"")); h.place=place;
    // what the user already has: own days listed, a day already marked otherwise (a vacation) isn't turned into a holiday by default
    WC.holOwn=(h.own||[]).map(x=>({date:x.date,name:x.name})); WC.holOff=new Set((h.holidays||[]).filter(x=>!x.marked && x.kind && x.kind!=="holiday").map(x=>x.date));
    WC.hol=h; }
  catch(e){ WC.hol={month:m,place,failed:true}; } reSet(); }
function wcCloseCard(){
  const m=WT && (WT.unclosed_months||[])[0]; if (!m) return "";
  if (!WC.close || WC.close.month!==m){ WC.close={month:m,loading:true}; wcLoadClose(m); return ""; }
  const c=WC.close; if (c.loading) return "";
  if (c.failed) return `<div class="scard"><div class="srow"><div class="sl">${esc(T("wt_load_failed"))}</div><button class="btn" data-wc="close_retry">${esc(T("wt_retry"))}</button></div></div>`;
  const left=c.deficit_seconds, max=c.max_cover_seconds, title=T("wt_close_t",wcMonthName(m));
  const cover=max>0? `<div class="srow sub"><div class="sl">${esc(T("wt_close_cover"))}</div><input class="kin num" type="number" min="0" max="${max/3600}" step="any" data-wf="close_cover" aria-label="${esc(T("wt_close_cover"))}" value="${Math.floor(max/36)/100}"></div>` : "";
  const btns=left>0? `<button class="btn primary" data-wc="close_carry">${esc(T("wt_close_carry"))}</button><button class="btn" data-wc="close_drop">${esc(T(c.deduct_unclosed?"wt_close_deduct":"wt_close_off"))}</button>`
    : `<button class="btn primary" data-wc="close_carry">${esc(T("wt_close_ok"))}</button>`;
  return `<div class="scard" role="group" aria-label="${esc(title)}"><div class="srow"><div class="sl"><div><b>${esc(title)}</b></div>`
    +`<div class="sd">${esc(T("wt_close_d",hm(left),hm(c.weekend_seconds),hm(c.holiday_seconds)))}</div><div class="sd">${esc(T("wt_close_paid"))}</div></div></div>`
    +cover+`<div class="srow sub"><div class="sl"></div><div class="wcb">${btns}</div></div>${wcPreview("close")}</div>`;
}
function wcHolCard(){
  const m=WT && WT.holidays_to_confirm; if (!m) return "";
  if (!WC.hol || WC.hol.month!==m || WC.hol.place!==wcPlace()){ WC.hol={month:m,place:wcPlace(),loading:true}; WC.pv=null; wcLoadHol(m); }
  const h=WC.hol; if (h.loading) return `<div class="scard"><div class="srow"><div class="sl sd">…</div></div></div>`;
  if (h.failed) return `<div class="scard"><div class="srow"><div class="sl">${esc(T("wt_load_failed"))}</div><button class="btn" data-wc="hol_retry">${esc(T("wt_retry"))}</button></div></div>`;
  const day=d=>wcDate(d).toLocaleDateString(LOC(),{weekday:"short",day:"numeric",month:"long"}), title=T("wt_hol_t",wcMonthName(m));
  const rows=(h.holidays||[]).map(x=>`<div class="srow sub"><div class="sl"><div>${esc(x.name)}</div><div class="sd">${esc(day(x.date))}</div></div>${tgl("wh",!WC.holOff.has(x.date),` data-wh="${x.date}" aria-label="${esc(x.name)}"`)}</div>`).join("");
  const own=WC.holOwn.map((x,i)=>`<div class="srow sub"><div class="sl"><div>${esc(x.name)}</div><div class="sd">${esc(day(x.date))}</div></div><button class="btn" data-wc="hol_rm" data-wi="${i}">${esc(T("wt_delete"))}</button></div>`).join("");
  const note=h.source==="unknown"? T("wt_hol_unknown") : h.source==="unavailable"? T("wt_hol_unavail") : h.source==="old"? T("wt_hol_old") : !(h.holidays||[]).length? T("wt_hol_none") : T("wt_hol_d")+(h.country==="UA"? " "+T("wt_hol_ua") : "");
  const [y,mo]=m.split("-").map(Number), last=`${m}-${String(new Date(y,mo,0).getDate()).padStart(2,"0")}`;
  return `<div class="scard" role="group" aria-label="${esc(title)}"><div class="srow"><div class="sl"><div><b>${esc(title)}</b></div><div class="sd">${esc(note)}</div></div>`
    +(h.source==="unavailable"? `<button class="btn" data-wc="hol_retry">${esc(T("wt_retry"))}</button>` : "")+`</div>${rows}${own}`
    +`<div class="srow sub"><div class="wcf"><label>${esc(T("wt_hol_day"))}<input class="kin" type="date" min="${m}-01" max="${last}" data-wf="hol_date"></label><label>${esc(T("wt_hol_name"))}<input class="kin" maxlength="100" data-wf="hol_name"></label>`
    +`<div class="wcb"><button class="btn" data-wc="hol_add">${esc(T("wt_hol_add"))}</button></div></div></div>`
    +`<div class="srow sub"><div class="sl"></div><button class="btn primary" data-wc="hol_ok">${esc(T("wt_hol_ok"))}</button></div>${wcPreview("hol")}</div>`;
}
function wtSettings(w){
  const s=w.settings||{}, num=(k,title,desc)=>srow(title,desc,`<input class="kin num" type="number" step="any" min="0" max="${WT_MAX[k]}" data-wset="${k}" aria-label="${esc(title)}" value="${s[k]==null?"":esc(s[k])}">`);
  const week=[1,2,3,4,5,6,7].map(n=>`<button class="wcw${(s.work_days||[]).includes(n)?" on":""}" data-wday="${n}" aria-pressed="${(s.work_days||[]).includes(n)}">${esc(new Date(2024,0,n).toLocaleDateString(LOC(),{weekday:"short"}))}</button>`).join("");
  const sw=(k,title,desc)=>srow(title,desc,tgl("wts",!!s[k],` data-wsk="${k}"`));
  const when=d=>wcDate(d).toLocaleDateString(LOC());
  return `<div class="srow"><div class="sl sd">${esc(T("s_wt_applies"))}</div></div>`
    +num("salary",T("s_wt_salary"),T("s_wt_salary_d"))
    +srow(T("s_wt_currency"),"",`<input class="kin num" maxlength="3" style="text-transform:uppercase" data-wset="salary_currency" aria-label="${esc(T("s_wt_currency"))}" value="${esc(s.salary_currency)}">`)
    +srow(T("s_wt_display"),T("s_wt_display_d"),`<input class="kin num" maxlength="3" style="text-transform:uppercase" data-wset="display_currency" aria-label="${esc(T("s_wt_display"))}" placeholder="${esc(s.salary_currency||"")}" value="${esc(s.display_currency||"")}">`)
    +(s.display_currency && s.display_currency!==s.salary_currency?
      srow(T("s_wt_rsource"),T("s_wt_rsource_d"),`<select data-wset="rate_source" aria-label="${esc(T("s_wt_rsource"))}">${[["ecb","s_wt_ecb"],["nbu","s_wt_nbu"]].map(([v,k])=>`<option value="${v}"${(s.rate_source||"ecb")===v?" selected":""}>${esc(T(k))}</option>`).join("")}</select>`)
      +srow(T("s_wt_rtime"),T("s_wt_rtime_d"),`<input class="kin num" maxlength="5" inputmode="numeric" placeholder="08:00" data-wset="rate_time" aria-label="${esc(T("s_wt_rtime"))}" value="${esc(s.rate_time||"08:00")}">`)
      +num("fx_manual_rate",T("s_wt_fx",s.salary_currency,s.display_currency),T("s_wt_fx_d")) : "")
    +num("weekly_norm_hours",T("s_wt_norm"),"")
    +`<div class="srow"><div class="sl">${esc(T("s_wt_days"))}</div><div class="wcb" role="group" aria-label="${esc(T("s_wt_days"))}">${week}</div></div>`
    +num("manual_rate",T("s_wt_rate"),T("s_wt_rate_d"))
    +num("weekday_mult",T("s_wt_m_wd"),"")+num("weekend_mult",T("s_wt_m_we"),"")+num("holiday_mult",T("s_wt_m_ho"),T("s_wt_m_ho_d"))
    +srow(T("s_wt_country"),T("s_wt_country_d")+(s.country? " "+wtCountry(s.country) : ""),`<input class="kin num" maxlength="2" style="text-transform:uppercase" data-wset="country" aria-label="${esc(T("s_wt_country"))}" placeholder="UA" value="${esc(s.country||"")}">`)
    +(s.country? srow(T("s_wt_region"),T("s_wt_region_d"),`<input class="kin num" maxlength="6" style="text-transform:uppercase" data-wset="region" aria-label="${esc(T("s_wt_region"))}" placeholder="${esc(s.country)}-…" value="${esc(s.region||"")}">`) : "")
    +sw("holidays_reduce_norm",T("s_wt_hol"),T("s_wt_hol_d"))+sw("deduct_unclosed",T("s_wt_deduct"),T("s_wt_deduct_d"))
    +num("tolerance_pct",T("s_wt_tol"),"")+sw("count_today",T("s_wt_today"),T("s_wt_today_d"))
    +((w.versions||[]).length>1? `<div class="srow"><div class="sl"><div>${esc(T("s_wt_history"))}</div><div class="sd">${(w.versions||[]).map(v=>esc(T("s_wt_version",when(v.valid_from),v.settings.weekly_norm_hours,v.settings.salary,v.settings.salary_currency))).join("<br>")}</div></div></div>` : "");
}
function wtTab(w){
  const intro=srow(T("wt_title"),T("s_wt_on_d"),tgl("worktime",w.enabled));
  if (w.broken) return `<div class="scard">${intro}${srow(T("wt_broken"),"","")}</div>`;
  if (!w.enabled){
    // used before: turning it on continues from its start and asks nothing
    if (w.start_date) return `<div class="scard">${intro}${srow(T("s_wt_since",wcDate(w.start_date).toLocaleDateString(LOC())),"","")}</div>`;
    const f=(k,title,attrs)=>srow(title,"",`<input class="kin num" type="number" min="0" step="any" data-wf="${k}" aria-label="${esc(title)}" ${attrs||""}>`);
    const days=WC.setupDays||[1,2,3,4,5], today=new Date(now()*1000).toLocaleDateString("sv");
    const week=[1,2,3,4,5,6,7].map(n=>`<button class="wcw${days.includes(n)?" on":""}" data-wsd="${n}" aria-pressed="${days.includes(n)}">${esc(new Date(2024,0,n).toLocaleDateString(LOC(),{weekday:"short"}))}</button>`).join("");
    return `<div class="scard">${intro}<div class="srow"><div class="sl sd">${esc(T("s_wt_setup_t"))}</div></div>`
      +`<div class="srow sub"><div class="sl"><div>${esc(T("s_wt_start"))}</div><div class="sd">${esc(T("s_wt_start_d"))}</div></div><input class="kin" type="date" style="width:auto" max="${today}" data-wf="start_date" aria-label="${esc(T("s_wt_start"))}"></div>`
      +f("weekly_norm_hours",T("s_wt_norm"),`max="168" placeholder="40"`)
      +`<div class="srow sub"><div class="sl">${esc(T("s_wt_days"))}</div><div class="wcb" role="group" aria-label="${esc(T("s_wt_days"))}">${week}</div></div>`
      +f("salary",T("s_wt_salary"),`max="1e12"`)
      +srow(T("s_wt_currency"),"",`<input class="kin num" maxlength="3" style="text-transform:uppercase" data-wf="salary_currency" aria-label="${esc(T("s_wt_currency"))}" placeholder="EUR">`)
      +f("deficit_hours",T("s_wt_o_deficit"),`max="100000"`)+f("weekday_hours",T("s_wt_o_weekday"),`max="100000"`)+f("weekend_hours",T("s_wt_o_weekend"),`max="100000"`)+f("holiday_hours",T("s_wt_o_holiday"),`max="100000"`)+`</div>`;
  }
  if (!WC.loaded || (WC.today && !WC.month)) { if (!WC.month) WC.month=new Date(now()*1000).toLocaleDateString("sv").slice(0,7); if (WC.loaded===null){ WC.loaded=false; wcLoad(); } }
  return `<div class="scard">${intro}${srow(T("s_wt_since",wcDate(w.start_date).toLocaleDateString(LOC())),"","")}</div>`
    +wcCloseCard()+wcHolCard()
    +`<div class="wcsec">${esc(T("s_wt_calendar"))}</div><div class="scard">${wcCalendar()}</div>${wcDay()}`
    +`<div class="wcsec">${esc(T("settings"))}</div><div class="scard">${wtSettings(w)}</div>`;
}
const wcField=k=>{ const e=$("#sethost").querySelector(`[data-wf="${k}"]`); return e? e.value.trim() : ""; };
// true: the click was this tab's
function wtTabClick(t){
  const a=t.dataset.wc;
  if (t.dataset.wd){ const d=t.dataset.wd;
    if (WC.many){ WC.sel.has(d)? WC.sel.delete(d) : WC.sel.add(d); WC.pv=null; reSet(); }
    else { WC.edit=null; WC.pv=null; wcOpenDay(d); }
    return true; }
  if (t.dataset.wh){ const d=t.dataset.wh; WC.holOff.has(d)? WC.holOff.delete(d) : WC.holOff.add(d); WC.pv=null; reSet(); return true; }
  if (t.dataset.wsd){   // the working days asked when it's turned on
    const s=(WC.setupDays||[1,2,3,4,5]).slice(), n=+t.dataset.wsd, i=s.indexOf(n);
    if (i>=0 && s.length===1) return true;
    if (i>=0) s.splice(i,1); else s.push(n);
    WC.setupDays=s.sort(); reSet(); return true; }
  if (t.dataset.wday){
    const s=(SET.worktime.settings.work_days||[]).slice(), n=+t.dataset.wday, i=s.indexOf(n);
    if (i>=0 && s.length===1) return true;   // at least one working day
    if (i>=0) s.splice(i,1); else s.push(n);
    s.sort(); $("#sethost").__h=null; host({settings:"set",worktime_settings:{work_days:s}}); return true; }
  if (!a) return false;
  const d=WC.day;
  if (a==="close_retry"){ WC.close=null; reSet(); return true; }
  if (a==="hol_retry"){ const m=WC.hol.month; WC.hol={month:m,place:wcPlace(),loading:true}; reSet(); wcLoadHol(m,true); return true; }
  if (a==="close_carry"||a==="close_drop"){ const c=WC.close, v=wcField("close_cover"), h=v===""? 0 : +v;
    if (!(h>=0) || h*3600>c.max_cover_seconds+18){ snack(T("wt_bad_value")); return true; }
    wcAsk("POST",`/api/worktime/months/${c.month}/close`,{cover_hours:Math.min(h,c.max_cover_seconds/3600),carry:a==="close_carry"},"close"); return true; }
  if (a==="hol_add"){ const d=wcField("hol_date"), n=wcField("hol_name"), m=WC.hol&&WC.hol.month;
    if (!m || !d.startsWith(m+"-") || !n){ snack(T("wt_bad_value")); return true; }
    WC.holOwn=WC.holOwn.filter(x=>x.date!==d).concat([{date:d,name:n}]).sort((x,y)=>x.date<y.date?-1:1); WC.pv=null;
    for (const k of ["hol_date","hol_name"]){ const e=$("#sethost").querySelector(`[data-wf="${k}"]`); if (e) e.value=""; }
    reSet(); return true; }
  if (a==="hol_rm"){ WC.holOwn.splice(+t.dataset.wi,1); WC.pv=null; reSet(); return true; }
  if (a==="hol_ok"){ const h=WC.hol; wcAsk("POST",`/api/worktime/holidays/${h.month}/confirm`,{dates:(h.holidays||[]).map(x=>x.date).filter(d=>!WC.holOff.has(d)),own:WC.holOwn},"hol"); return true; }
  if (a==="reopen"){ const m=WC.month;
    ask(T("wt_reopen_q",wcMonthName(m))).then(async ok=>{ if (!ok) return; try{ await api("POST",`/api/worktime/months/${m}/reopen`,{}); WC.close=null; await wtLoad(); }catch(e){ snack(wtErr(e)); } await wcLoad(); });
    return true; }
  if (a==="retry"){ WC.err=false; WC.loaded=false; reSet(); wcLoad(); }
  else if (a==="prev") wcShift(-1);
  else if (a==="next") wcShift(1);
  else if (a==="many"){ WC.many=!WC.many; WC.sel.clear(); WC.pv=null; reSet(); }
  else if (a==="commit") wcCommit();
  else if (a==="uncommit"){ WC.pv=null; reSet(); }
  else if (a==="edit"){ WC.edit=+t.dataset.wi; WC.pv=null; reSet(); }
  else if (a==="edit_off"){ WC.edit=null; WC.pv=null; reSet(); }
  else if (a==="del") wcAsk("DELETE","/api/worktime/intervals/"+t.dataset.wi,undefined,"day");
  else if (a==="undo") wcAsk("POST",`/api/worktime/days/${d}/undo`,{},"day");
  else if (a==="day_set"){ const v=wcField(d+"_hours"); if (v!=="") wcAsk("PUT","/api/worktime/days/hours",{dates:[d],total_hours:+v},"day"); }
  else if (a==="many_set"){ const v=wcField("many_hours"); if (v!=="" && WC.sel.size) wcAsk("PUT","/api/worktime/days/hours",{dates:[...WC.sel].sort(),total_hours:+v},"many"); }
  else if (a==="add"||a==="save_edit"){
    const id=t.dataset.wi, p=`${d}_${id}_`, b={start:wcIso(wcField(p+"start")), end:wcIso(wcField(p+"end")), note:wcField(p+"note")};
    if (!b.start || !b.end){ snack(T("wt_e_end")); return true; }
    if (a==="save_edit") wcAsk("PUT","/api/worktime/intervals/"+id,b,"day");
    else { const open=(WC.dayData.sessions||[]).find(s=>!s.open); if (open) wcAsk("POST","/api/worktime/intervals",{session_id:open.id,...b},"day"); else wcAsk("POST","/api/worktime/sessions",{work_date:d,intervals:[b]},"day"); }
  }
  else return false;
  return true;
}
// a select of this tab
function wtTabSelect(e){
  const k=e.target.dataset.wcKind, v=e.target.value; if (!v) return;
  if (k==="day"){ wcAsk("PUT",`/api/worktime/days?from=${WC.day}&to=${WC.day}`,{kind:v},"day"); return; }
  const days=[...WC.sel].sort(); if (!days.length) return;
  // chosen days needn't be in a row and the API takes a range: one request per day, each logged anyway
  (async()=>{ try{ for (const d of days) await api("PUT",`/api/worktime/days?from=${d}&to=${d}`,{kind:v}); WC.sel.clear(); WC.many=false; wtLoad(); }catch(er){ snack(wtErr(er)); } WC.close=null; await wcLoad(); })();
}
// the page's "set" when it's turned on: what was filled in goes with it
function wtSetup(){
  const b={settings:{}}, st=wcField("start_date"); if (st) b.start_date=st;
  for (const k of ["weekly_norm_hours","salary","salary_currency"]){ const v=wcField(k); if (v==="") continue; const x=wtNum(k,v); b.settings[k]= x===undefined? v : x; }   // a bad one goes as typed: the host refuses it in words
  if (WC.setupDays) b.settings.work_days=WC.setupDays.slice();
  const o={}; for (const k of ["deficit_hours","weekday_hours","weekend_hours","holiday_hours"]){ const v=wcField(k); if (v!=="") o[k]=+v; }
  if (Object.keys(o).length) b.opening=o;
  return b;
}
// typing in a day's field: a shown change no longer matches what Save would send
function wtTabInput(e){
  if (!WC.pv || !e.target.dataset || e.target.dataset.wf==null) return;
  WC.pv=null; const p=$("#sethost").querySelector(".wcpv"); if (p) p.remove();
}
