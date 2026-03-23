namespace ScriptNodePlugin
{
    /// <summary>Embedded Alien browser editor (dark theme, WebSocket sync).</summary>
    public static class EditorHtml
    {
        public static string GetPage(int port, System.Guid nodeGuid)
        {
            var g = nodeGuid.ToString();
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<meta name=""viewport"" content=""width=device-width, initial-scale=1""/>
<title>Alien — " + g + @"</title>
<style>
:root{--bg:#0d0d0d;--surface:#1a1a1a;--border:#2a2a2a;--text:#e0e0e0;--muted:#888;--accent:#00c853;--warn:#c8a000;}
*{box-sizing:border-box}
body{margin:0;font-family:Segoe UI,system-ui,sans-serif;background:var(--bg);color:var(--text);min-height:100vh}
header{display:flex;align-items:center;gap:16px;padding:12px 16px;background:var(--surface);border-bottom:1px solid var(--border);flex-wrap:wrap}
header h1{margin:0;font-size:16px;font-weight:600;letter-spacing:.04em}
header .path{font-size:11px;color:var(--muted);max-width:50vw;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.toolbar{margin-left:auto;display:flex;align-items:center;gap:12px}
.switch{position:relative;width:44px;height:24px;background:#333;border-radius:12px;cursor:pointer;border:1px solid var(--border)}
.switch.on{background:rgba(0,200,83,.35)}
.switch::after{content:'';position:absolute;width:18px;height:18px;background:#fff;border-radius:50%;top:2px;left:3px;transition:left .15s}
.switch.on::after{left:22px;background:var(--accent)}
button{background:var(--accent);color:#000;border:none;padding:8px 16px;border-radius:6px;font-weight:600;cursor:pointer}
button:disabled{opacity:.4;cursor:not-allowed}
button.secondary{background:#333;color:var(--text)}
#banner{display:none;background:#3a2200;color:#ffb;border:1px solid var(--warn);padding:8px 16px;font-size:12px}
#banner.show{display:block}
main{padding:16px;max-width:720px}
.param{margin-bottom:14px;padding:12px;background:var(--surface);border:1px solid var(--border);border-radius:8px}
.param.wired{opacity:.55;pointer-events:none}
.param h3{margin:0 0 4px;font-size:13px;display:flex;align-items:center;gap:8px}
.badge{font-size:10px;padding:2px 6px;border-radius:4px;background:#333;color:var(--muted)}
.dot{width:8px;height:8px;border-radius:50%;background:var(--muted);flex-shrink:0}
.dot.live{background:var(--accent)}
.meta{font-size:11px;color:var(--muted);margin-bottom:8px}
.row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
input[type=number],input[type=text],textarea{width:100%;max-width:280px;padding:6px 8px;border-radius:4px;border:1px solid var(--border);background:#111;color:var(--text)}
textarea{min-height:48px;resize:vertical}
input[type=range]{width:180px}
label.small{font-size:11px;color:var(--muted)}
footer{padding:12px 16px;font-size:11px;color:var(--muted);border-top:1px solid var(--border)}
</style>
</head>
<body>
<div id=""banner"">Disconnected — reconnecting…</div>
<header>
<h1>ALIEN</h1>
<span class=""path"" id=""scriptPath""></span>
<div class=""toolbar"">
<span style=""font-size:12px;color:var(--muted)"">Live</span>
<div id=""liveSw"" class=""switch"" title=""Live mode""></div>
<button id=""applyBtn"">Apply</button>
</div>
</header>
<main id=""params""></main>
<footer><span id=""status"">Connecting…</span></footer>
<script>
(function(){
var PORT=" + port + @";
var GUID='" + g + @"';
var ws, reconnect=1000, maxReconnect=30000, live=false, pending=false;
var liveSw=document.getElementById('liveSw');
var applyBtn=document.getElementById('applyBtn');
var banner=document.getElementById('banner');
var statusEl=document.getElementById('status');
document.getElementById('scriptPath').textContent='';

function setBanner(show){banner.className=show?'show':'';}
function setStatus(t){statusEl.textContent=t;}

liveSw.onclick=function(){
  live=!live;
  liveSw.className=live?'switch on':'switch';
  applyBtn.style.display=live?'none':'inline-block';
  if(ws&&ws.readyState===1) ws.send(JSON.stringify({type:'setLive',guid:GUID,enabled:live}));
};

applyBtn.onclick=function(){
  var o={};
  document.querySelectorAll('[data-pname]').forEach(function(el){
    var n=el.getAttribute('data-pname');
    var v=readControl(el);
    if(v!==undefined) o[n]=v;
  });
  if(ws&&ws.readyState===1) ws.send(JSON.stringify({type:'apply',guid:GUID,values:o}));
};

function readControl(root){
  var t=root.getAttribute('data-ctype');
  if(t==='bool') return root.querySelector('input').checked;
  if(t==='int'||t==='float'||t==='number') return parseFloat(root.querySelector('input').value);
  if(t==='str') return root.querySelector('textarea')?root.querySelector('textarea').value:root.querySelector('input').value;
  if(t==='xyz'){
    var ins=root.querySelectorAll('input');
    return [parseFloat(ins[0].value)||0,parseFloat(ins[1].value)||0,parseFloat(ins[2].value)||0].join(',');
  }
  if(t==='domain'){
    var ins=root.querySelectorAll('input');
    return (parseFloat(ins[0].value)||0)+','+(parseFloat(ins[1].value)||1);
  }
  if(t==='colour') return root.querySelector('input[type=color]').value;
  return root.querySelector('input')?root.querySelector('input').value:'';
}

function connect(){
  setStatus('Connecting WebSocket…');
  ws=new WebSocket('ws://127.0.0.1:'+PORT+'/ws/node/'+GUID);
  ws.onopen=function(){setBanner(false);reconnect=1000;setStatus('Connected');};
  ws.onclose=function(){setBanner(true);setStatus('Disconnected');scheduleReconnect();};
  ws.onerror=function(){};
  ws.onmessage=function(ev){
    try{
      var msg=JSON.parse(ev.data);
      if(msg.type==='state') renderState(msg);
    }catch(e){}
  };
}
function scheduleReconnect(){
  setTimeout(function(){reconnect=Math.min(reconnect*2,maxReconnect);connect();},reconnect);
}

function renderState(msg){
  document.getElementById('scriptPath').textContent=msg.scriptPath||'';
  pending=msg.pendingChanges;
  if(typeof msg.liveMode==='boolean'){
    live=msg.liveMode;
    liveSw.className=live?'switch on':'switch';
    applyBtn.style.display=live?'none':'inline-block';
  }
  var box=document.getElementById('params');
  box.innerHTML='';
  (msg.params||[]).forEach(function(p){
    var div=document.createElement('div');
    div.className='param'+(p.isWired?' wired':'');
    var h=document.createElement('h3');
    h.textContent=p.name||'';
    var b=document.createElement('span');b.className='badge';b.textContent=p.typeHint||'';
    h.appendChild(b);
    var dot=document.createElement('span');dot.className='dot'+(p.isWired?' live':'');
    h.appendChild(dot);
    div.appendChild(h);
    if(p.meta){var m=document.createElement('div');m.className='meta';m.textContent=p.meta;div.appendChild(m);}
    var ctrl=buildControl(p);
    div.appendChild(ctrl);
    box.appendChild(div);
  });
}

function buildControl(p){
  var wrap=document.createElement('div');
  var th=(p.typeHint||'').toLowerCase();
  function mark(el){el.setAttribute('data-pname',p.name);return el;}
  if(p.isWired){
    wrap.textContent='(wired from canvas)';
    return wrap;
  }
  var v=p.manualValue;
  if(th==='bool'||th==='boolean'){
    wrap.setAttribute('data-ctype','bool');
    var cb=document.createElement('input');cb.type='checkbox';cb.checked=!!v;
    cb.onchange=function(){if(live) sendUpdate(p.name,cb.checked);};
    wrap.appendChild(cb);
    return mark(wrap);
  }
  if(th==='int'||th==='integer'){
    wrap.setAttribute('data-ctype','int');
    var inp=document.createElement('input');inp.type='number';inp.step=1;inp.value=v!=null?v:0;
    inp.oninput=function(){if(live) throttleSend(p.name,parseFloat(inp.value)||0);};
    wrap.appendChild(inp);
    return mark(wrap);
  }
  if(th==='float'||th==='double'||th==='number'){
    wrap.setAttribute('data-ctype','float');
    var inp=document.createElement('input');inp.type='number';inp.step=0.001;inp.value=v!=null?v:0;
    if(p.min!=null&&p.max!=null){
      var rg=document.createElement('input');rg.type='range';
      rg.min=p.min;rg.max=p.max;rg.step=p.step||((p.max-p.min)/100);
      rg.value=v!=null?v:(p.min+p.max)/2;
      inp.value=rg.value;
      rg.oninput=function(){inp.value=rg.value;if(live) throttleSend(p.name,parseFloat(rg.value));};
      inp.oninput=function(){rg.value=inp.value;if(live) throttleSend(p.name,parseFloat(inp.value));};
      wrap.appendChild(rg);
    } else {
      inp.oninput=function(){if(live) throttleSend(p.name,parseFloat(inp.value)||0);};
    }
    wrap.appendChild(inp);
    return mark(wrap);
  }
  if(th==='point3d'||th==='point'||th==='vector3d'||th==='vector'){
    wrap.setAttribute('data-ctype','xyz');
    var arr=[0,0,0];
    if(typeof v==='string'){var ps=v.split(/[,; ]+/);for(var i=0;i<3;i++) arr[i]=parseFloat(ps[i])||0;}
    else if(Array.isArray(v)&&v.length>=3){arr=[v[0],v[1],v[2]];}
    ['X','Y','Z'].forEach(function(axis,i){
      var lab=document.createElement('label');lab.className='small';lab.textContent=axis;
      var inp=document.createElement('input');inp.type='number';inp.value=arr[i];
      inp.oninput=function(){if(live) sendXyz(wrap);};
      wrap.appendChild(lab);wrap.appendChild(inp);
    });
    function sendXyz(root){
      var ins=root.querySelectorAll('input');
      var s=[ins[0].value,ins[1].value,ins[2].value].join(',');
      throttleSend(p.name,s);
    }
    return mark(wrap);
  }
  if(th==='domain'){
    wrap.setAttribute('data-ctype','domain');
    var a=document.createElement('input');a.type='number';
    var b=document.createElement('input');b.type='number';
    if(typeof v==='string'){var ps=v.split(',');a.value=ps[0]||0;b.value=ps[1]||1;}
    wrap.appendChild(a);wrap.appendChild(b);
    return mark(wrap);
  }
  if(th==='color'||th==='colour'){
    wrap.setAttribute('data-ctype','colour');
    var inp=document.createElement('input');inp.type='color';
    inp.value=(typeof v==='string'&&v[0]==='#')?v:'#000000';
    inp.oninput=function(){if(live) throttleSend(p.name,inp.value);};
    wrap.appendChild(inp);
    return mark(wrap);
  }
  wrap.setAttribute('data-ctype','str');
  var ta=document.createElement('textarea');ta.value=v!=null?String(v):'';
  ta.oninput=function(){if(live) throttleSend(p.name,ta.value);};
  wrap.appendChild(ta);
  return mark(wrap);
}

var _throttleTimer=null;
function throttleSend(name,val){
  if(_throttleTimer) clearTimeout(_throttleTimer);
  _throttleTimer=setTimeout(function(){sendUpdate(name,val);},50);
}
function sendUpdate(name,val){
  if(ws&&ws.readyState===1) ws.send(JSON.stringify({type:'update',guid:GUID,param:name,value:val}));
}

connect();
})();
</script>
</body>
</html>";
        }
    }
}
