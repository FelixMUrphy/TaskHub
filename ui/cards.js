/* ---------------- cards ---------------- */
function cardSkeleton(t){
  const el=document.createElement("div");
  el.className="card"; el.id="t-"+t.id;
  el.innerHTML=`<div class="hd"><span class="name"></span><span class="chip prj"></span><span class="chip src"></span><span class="st"></span><span class="sp"></span><span class="tm"></span><button class="x" title="${T("remove")}">×</button><span class="fold">›</span></div>
   <div class="desc"></div><div class="attn"></div><div class="bar"><i></i></div><div class="l1"></div><div class="status"></div><div class="stage"></div>
   <div class="wg"></div><div class="msg"></div><div class="ctl"></div><div class="form"></div>
   <details class="lgx"><summary></summary><pre></pre></details><div class="meta"></div>`;
  $(".x",el).onclick=()=>api("DELETE",`/api/tasks/${t.id}`).catch(e=>snack(e.message));
  $("details",el).addEventListener("toggle",()=>{ if($("details",el).open) fillLog(el, findTask(t.id), true); });
  // a finished task's card folds back into its row (a failed one also leaves "needs attention")
  $(".hd",el).addEventListener("click",e=>{ if (e.target.closest("button,a")) return; const x=findTask(t.id); if (!x || ACTIVE.has(x.state)) return;
    if (!x.seen) markSeen([x.id]); fold(x.id); });
  ui[t.id]=Object.assign(ui[t.id]||{}, {ctlSig:null, formFor:null});
  return el;
}
const findTask = id => data && data.tasks.find(x=>x.id===id);
function fillLog(el, t, scroll){
  const pre=$("pre",el), atBottom = pre.scrollTop+pre.clientHeight >= pre.scrollHeight-8;
  pre.innerHTML=(t.log||[]).slice(-300).map(l=>`<span class="${esc(l.level)}">${new Date(l.t*1000).toLocaleTimeString(LOC())}  ${esc(l.msg)}</span>`).join("\n");
  if (scroll||atBottom) pre.scrollTop=pre.scrollHeight;
}
function times(t){
  const end = t.finished || now(), el = end - t.started;
  let s = dur(el);
  if (t.state==="running" && t.eta_at && !(t.progress>=1)) s += " · "+T("eta")+" " + dur(Math.max(0,t.eta_at-now()));
  if (t.state==="stale") s += " · "+T("silence")+" " + dur(now()-t.last_seen);
  return s;
}
function updCard(el, t){
  el.dataset.s=t.state; if (ACTIVE.has(t.state)) el.dataset.active=1; else delete el.dataset.active;
  $(".name",el).textContent=t.name; $(".name",el).title=t.name+" ["+t.id+"]";
  const src=$(".src",el); src.textContent=t.source; src.style.display=t.source?"":"none";
  $(".st",el).textContent=T("st_"+t.state);
  $(".tm",el).textContent=times(t);
  const d=$(".desc",el); d.textContent=t.description||""; d.style.display=t.description?"":"none";
  const at=$(".attn",el), att=ACTIVE.has(t.state) && t.attention; at.textContent=att||""; at.style.display=att?"":"none";
  if (att) el.dataset.attn=1; else delete el.dataset.attn;
  const bar=$(".bar",el), hasP = t.progress!=null;
  bar.style.display = (hasP || ACTIVE.has(t.state)) ? "" : "none";
  bar.classList.toggle("ind", !hasP && t.state==="running");
  $("i",bar).style.width = hasP ? (t.progress*100).toFixed(2)+"%" : "0";
  let l1=[];
  if (hasP) l1.push(`<b>${(t.progress*100).toFixed(1)}%</b>`);
  if (t.current!=null) l1.push(`${num(t.current)}${t.total!=null?" / "+num(t.total):""} ${esc(t.unit)}`);
  if (t.state==="running" && t.current!=null && t.rate!=null) l1.push(`${num(t.rate)} ${t.unit?esc(t.unit)+"/s":T("per_s")}`);
  $(".l1",el).innerHTML=l1.join("<span>·</span>"); $(".l1",el).style.display=l1.length?"":"none";
  $(".status",el).textContent=t.status||""; $(".status",el).style.display=t.status?"":"none";
  $(".stage",el).textContent=t.stage?("▸ "+t.stage):""; $(".stage",el).style.display=t.stage?"":"none";
  const wg=$(".wg",el), wh=renderWidgets(t); if (wg.__h!==wh){ wg.innerHTML=wh; wg.__h=wh; } wg.style.display=wh?"":"none";
  const m=$(".msg",el); m.textContent=t.message||""; m.style.display=(t.message&&!ACTIVE.has(t.state))?"":"none";
  // controls (rebuild only if changed and no open form)
  const avail=(t.controls||[]).filter(c=>c.states.includes(t.state));
  const sig=JSON.stringify(avail)+t.state;
  if (ui[t.id].ctlSig!==sig && !ui[t.id].formFor){
    ui[t.id].ctlSig=sig;
    const box=$(".ctl",el); box.innerHTML="";
    for (const c of avail){
      const b=document.createElement("button");
      b.className="btn "+(c.style||""); b.textContent=c.label + (c.input?" …":""); if (c.hint) b.title=c.hint;
      b.onclick=()=>onControl(el,t.id,c,b);
      box.appendChild(b);
    }
  }
  // log
  $("summary",el).textContent=`${T("log")} (${(t.log||[]).length})` + (t.commands&&t.commands.some(c=>c.status==="pending")?" · "+T("cmd_queued"):"");
  if ($("details",el).open) fillLog(el,t);
  // meta / links
  const links=(t.links||[]).map(l=>typeof l==="string"?{label:l,url:l}:l).filter(l=>l&&l.url);
  const meta=[];
  if (t.pid) meta.push("pid "+t.pid);
  if (t.cwd) meta.push(esc(t.cwd));
  links.forEach((l,i)=>meta.push(`<a href="#" data-i="${i}" title="${esc(l.url)}">${esc(l.label||l.url)}</a>`));   // the real target as a tooltip
  const mt=$(".meta",el), mh=meta.join("<span>·</span>");
  if (mt.__h!==mh){ mt.innerHTML=mh; mt.__h=mh; mt.querySelectorAll("a").forEach(a=>a.onclick=e=>{e.preventDefault(); const u=links[+a.dataset.i].url; if (NATIVE) host({open:u}); else if (/^(https?|mailto):/i.test(u)) window.open(u,"_blank","noopener");}); }
}
async function onControl(el, tid, c, btn){
  if (!c.input){
    if (c.confirm && !(await ask(c.confirm))) return;
    return send(tid,c,undefined,btn);
  }
  const f=$(".form",el), i=c.input; ui[tid].formFor=c.id;
  const dv = c.last_value ?? i.default;   // last sent value, else the declared default
  let field;
  if (i.type==="select") field=`<select>${i.options.map(o=>`<option ${o==dv?"selected":""}>${esc(o)}</option>`).join("")}</select>`;
  else if (i.type==="bool") field=`<input type="checkbox" ${dv?"checked":""}>`;
  else if (i.type==="number") field=`<input type="number" value="${esc(dv??"")}" ${fin(i.min)!=null?`min="${i.min}"`:""} ${fin(i.max)!=null?`max="${i.max}"`:""} step="${fin(i.step)??"any"}">`;
  else field=`<input type="text" value="${esc(dv??"")}" placeholder="${esc(i.placeholder??"")}">`;
  f.innerHTML=`<label>${esc(i.label||c.label)}</label>${field}<button class="btn primary">OK</button><button class="btn">✕</button>`;
  f.classList.add("on");
  const inp=f.querySelector("input,select"); inp.focus(); if (inp.select) try{inp.select();}catch(e){}
  const close=()=>{ f.classList.remove("on"); f.innerHTML=""; ui[tid].formFor=null; ui[tid].ctlSig=null; const t=findTask(tid); if(t) updCard(el,t); };
  const ok=async ()=>{
    if (inp.reportValidity && !inp.reportValidity()) return;   // number outside min/max, not a number
    let v = i.type==="bool"? inp.checked : i.type==="number"? (inp.value===""?null:Number(inp.value)) : inp.value;
    if (c.confirm && !(await ask(c.confirm))) return;
    close(); send(tid,c,v);
  };
  const [bok,bx]=f.querySelectorAll("button"); bok.onclick=ok; bx.onclick=close;
  inp.addEventListener("keydown",e=>{ if(e.key==="Enter") ok(); if(e.key==="Escape"){ e.stopPropagation(); close(); } });
}
async function send(tid,c,value,btn){
  if (c.panel_only){   // irreversible: only through the WebView2 bridge of the native panel, never the HTTP API
    if (!NATIVE){ snack(T("error")); return; }
    host({control:{task:tid,id:c.id,value:value===undefined?null:value}}); return;
  }
  if (btn) btn.disabled=true;
  try{ await api("POST",`/api/tasks/${tid}/controls/${c.id}`, value===undefined?{}:{value}); snack(T("sent",c.label)); }
  catch(e){ snack(T("error")+": "+e.message); }
  finally{ if (btn) btn.disabled=false; }
}

