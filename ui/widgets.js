/* ---------------- widgets ---------------- */
// values that go into style/SVG attributes: only plain CSS colours and finite numbers (the hub checks too; old state may not be)
const cssc = c => typeof c==="string" && /^(#[0-9a-f]{3,8}|[a-z]{1,30}|var\(--[a-z0-9-]{1,30}\)|(rgb|rgba|hsl|hsla)\([0-9.,%\s/]{1,60}\))$/i.test(c.trim()) ? c.trim() : "";
const fin = (x, d) => typeof x==="number" && isFinite(x) ? x : d;
function lvl(w, v){ // threshold color
  if (typeof v!=="number") return "";
  const inv=!!w.invert;
  if (w.crit!=null && (inv? v<=w.crit : v>=w.crit)) return "var(--red)";
  if (w.warn!=null && (inv? v<=w.warn : v>=w.warn)) return "var(--amber)";
  return cssc(w.color);
}
function lab(w, right=""){ return w.label||right ? `<div class="lb"><span>${esc(w.label)}</span>${right?`<span class="lv">${right}</span>`:""}</div>` : ""; }
const W = {
  text(w){ return lab(w) + `<div class="v ${w.mono?"mono":""}">${esc(w.value ?? "—")}</div>`; },
  progress(w){
    let v=w.value, tot=w.total ?? w.max, f=null, txt="";
    if (typeof v==="number"){ if (tot){ f=v/tot; txt=`${num(v)} / ${num(tot)} ${esc(w.unit)}`; } else { f = v>1? v/100 : v; txt=(f*100).toFixed(1)+"%"; } }
    if (f!=null && tot) txt += ` · ${(f*100).toFixed(0)}%`;
    f = f==null?0:Math.max(0,Math.min(1,f));
    return lab(w, txt) + `<div class="pb" style="--wc:${lvl(w,f*100)||"var(--acc)"}"><i style="width:${(f*100).toFixed(2)}%"></i></div>`;
  },
  gauge(w){
    const v=w.value, mn=w.min??0, mx=w.max??100, f=typeof v==="number"?Math.max(0,Math.min(1,(v-mn)/(mx-mn||1))):0;
    const R=34, cx=44, cy=40, a0=Math.PI, a1=Math.PI*(1-f);
    const P=a=>[cx+R*Math.cos(a), cy-R*Math.sin(a)];
    const [x0,y0]=P(a0),[x1,y1]=P(a1),[xe,ye]=P(0);
    const col=lvl(w,v)||"var(--acc)";
    return `<div style="display:flex;align-items:center;gap:8px">
      <svg viewBox="0 0 88 46" width="88" height="46"><path d="M${x0},${y0} A${R},${R} 0 0 1 ${xe},${ye}" fill="none" stroke="var(--o2)" stroke-width="8" stroke-linecap="round"/>
      ${f>0.002?`<path d="M${x0},${y0} A${R},${R} 0 0 1 ${x1.toFixed(2)},${y1.toFixed(2)}" fill="none" stroke="${col}" stroke-width="8" stroke-linecap="round"/>`:""}</svg>
      <div style="min-width:0">${lab(w)}<div class="big" style="color:${lvl(w,v)||"inherit"}">${num(v,w.decimals)}<small>${esc(w.unit)}</small></div></div></div>`;
  },
  stat(w){
    const v=w.value; let d="";
    if (typeof v==="number" && typeof w.prev==="number" && v!==w.prev){
      const up=v>w.prev, good=w.invert? !up : up;
      d=`<span class="dl ${good?"up":"dn"}">${up?"▲":"▼"} ${num(Math.abs(v-w.prev))}</span>`; }
    return lab(w) + `<div class="big" style="color:${lvl(w,v)||"inherit"}">${num(v,w.decimals)}<small>${esc(w.unit)}</small>${d}</div>`;
  },
  led(w){
    let v=w.value, txt=w.text, cls="off";
    if (v && typeof v==="object"){ txt=v.text??txt; v=v.state; }
    if (v===true) cls="ok"; else if (v===false) cls="error";
    else if (["ok","warn","error","busy","off"].includes(v)) cls=v;
    return `<div class="led"><i class="${cls}"></i><div style="min-width:0">${lab(w)}<div class="v">${esc(txt ?? (typeof v==="string"?v:""))}</div></div></div>`;
  },
  kv(w){
    const o=w.value||{}; return lab(w)+`<div class="kv">${Object.entries(o).map(([k,v])=>`<span>${esc(k)}</span><span>${typeof v==="number"?num(v,w.decimals):esc(v)}</span>`).join("")}</div>`;
  },
  table(w){
    const rows=(w.value||[]).slice(-50), cols=w.columns&&w.columns.length?w.columns:(rows[0]&&!Array.isArray(rows[0])?Object.keys(rows[0]):[]);
    const cell=v=>typeof v==="number"?num(v,w.decimals):esc(v);
    if (!rows.length) return lab(w)+`<div class="v wait">${T("waiting")}</div>`;
    return lab(w)+`<table class="tb">${cols.length?`<tr>${cols.map(c=>`<th>${esc(c)}</th>`).join("")}</tr>`:""}
      ${rows.map(r=>`<tr>${(Array.isArray(r)?r:cols.map(c=>r[c])).map(v=>`<td>${cell(v)}</td>`).join("")}</tr>`).join("")}</table>`;
  },
  chart(w){
    const pts=w.points||[], S=w.series||[w.id], hgt=Math.min(400,Math.max(20,fin(w.height,54))), Wd=300;
    const last = pts.length? pts[pts.length-1] : null;
    const right = last ? S.map((s,i)=> last[i+1]==null?"":`${S.length>1?esc(s)+" ":""}${num(last[i+1],w.decimals)}`).filter(Boolean).join(" · ")+(w.unit?" "+esc(w.unit):"") : "";
    if (pts.length<2) return lab(w,right)+`<div class="v wait">${T("waiting")}</div>`;
    let lo=Infinity, hi=-Infinity;
    for (const p of pts) for (let i=1;i<p.length;i++) if (p[i]!=null){ lo=Math.min(lo,p[i]); hi=Math.max(hi,p[i]); }
    if (w.y_min!=null) lo=w.y_min; if (w.y_max!=null) hi=w.y_max;
    if (w.kind==="bar" && w.y_min==null) lo=Math.min(0,lo);
    if (hi===lo){ hi+=1; lo-=1; }
    const t0=pts[0][0], t1=pts[pts.length-1][0]||t0+1, n=pts.length;
    const X = (p,k)=> w.kind==="bar" ? (k+0.5)/n*Wd : ((p[0]-t0)/((t1-t0)||1))*Wd;
    const Y = v => 4 + (1-(v-lo)/(hi-lo))*(hgt-8);
    let g = `<line x1="0" x2="${Wd}" y1="${Y(hi)}" y2="${Y(hi)}" stroke="var(--ln)" stroke-dasharray="2 3"/><line x1="0" x2="${Wd}" y1="${Y(lo)}" y2="${Y(lo)}" stroke="var(--ln)"/>`;
    if (w.warn!=null && w.warn>lo && w.warn<hi) g+=`<line x1="0" x2="${Wd}" y1="${Y(w.warn)}" y2="${Y(w.warn)}" stroke="var(--amber)" stroke-dasharray="3 3" opacity=".7"/>`;
    if (w.crit!=null && w.crit>lo && w.crit<hi) g+=`<line x1="0" x2="${Wd}" y1="${Y(w.crit)}" y2="${Y(w.crit)}" stroke="var(--red)" stroke-dasharray="3 3" opacity=".7"/>`;
    S.forEach((s,i)=>{
      const col=`var(--s${i%6+1})`;
      if (w.kind==="bar"){
        const bw=Math.max(1,Wd/n/S.length*0.8);
        pts.forEach((p,k)=>{ const v=p[i+1]; if(v==null) return; const x=(k+0.1)/n*Wd+i*bw, y0=Y(Math.max(lo,0)), y=Y(v);
          g+=`<rect x="${x.toFixed(1)}" y="${Math.min(y,y0).toFixed(1)}" width="${bw.toFixed(1)}" height="${Math.max(0.5,Math.abs(y0-y)).toFixed(1)}" fill="${col}" opacity=".85"/>`; });
      } else {
        let d="", first=null, lastx=0;
        pts.forEach((p,k)=>{ const v=p[i+1]; if(v==null) return; const x=X(p,k).toFixed(1), y=Y(v).toFixed(1); d+=(d?"L":"M")+x+","+y; if(first==null) first=x; lastx=x; });
        if (w.kind==="area" && d) g+=`<path d="${d}L${lastx},${hgt-4}L${first},${hgt-4}Z" fill="${col}" opacity=".15"/>`;
        g+=`<path d="${d}" fill="none" stroke="${col}" stroke-width="1.6" stroke-linejoin="round" vector-effect="non-scaling-stroke"/>`;
      }
    });
    const legend = S.length>1 ? `<div class="lg">${S.map((s,i)=>`<span><i style="background:var(--s${i%6+1})"></i>${esc(s)}</span>`).join("")}</div>` : "";
    return lab(w,right)+`<div class="chw"><svg class="ch" viewBox="0 0 ${Wd} ${hgt}" preserveAspectRatio="none" style="height:${hgt}px">${g}</svg>`
      +`<div class="ax" style="height:${hgt}px"><span>${num(hi)}</span><span>${num(lo)}</span></div></div>`+legend;
  },
};
function renderWidgets(t){
  const ws=t.widgets||[]; let html="";
  for (const w of ws){
    const f=W[w.type]||W.text, wide = w.span===2 || (["chart","table","kv","progress"].includes(w.type) && w.span!==1);
    let inner; try{ inner=f(w); }catch(e){ inner=`<div class="v">widget error: ${esc(e.message)}</div>`; }
    html += `<div class="w ${wide?"span2":""}">${inner}</div>`;
  }
  const m=t.metrics||{}, mk=Object.keys(m);
  if (mk.length) html += `<div class="w span2"><div class="kv">${mk.map(k=>`<span>${esc(k)}</span><span>${typeof m[k]==="number"?num(m[k]):esc(m[k])}</span>`).join("")}</div></div>`;
  return html;
}

