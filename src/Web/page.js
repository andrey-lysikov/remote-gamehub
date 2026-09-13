const $=id=>document.getElementById(id);

// --- pairing ---
const pin=$('pin'),devname=$('devname'),said=$('said'),card=$('pair'),who=$('who'),
clients=$('clients');
async function reloadClients(){clients.innerHTML=await (await fetch('/?clients=1')).text();}
async function sendPin(){
if(!/^\d{4}$/.test(pin.value)){said.textContent='A code is four digits.';pin.focus();return;}
said.textContent='sending…';
const r=await fetch('/?pin='+encodeURIComponent(pin.value)+'&name='+encodeURIComponent(devname.value));
said.textContent=await r.text();pin.value='';
// The device appears once the client has finished its side. Asked for twice: the exchange
// takes a few hundred milliseconds, and one reload can be too early.
setTimeout(reloadClients,1500);setTimeout(reloadClients,5000);}
$('pairsave').addEventListener('click',sendPin);
// Cancel ends the attempt at the host's side. The box goes on its own a moment later,
// when the poll below finds nothing waiting any more, so nothing is hidden here.
$('paircancel').addEventListener('click',async()=>{
said.textContent='cancelling…';pin.value='';
said.textContent=await (await fetch('/?cancelpair=1')).text();});
pin.addEventListener('keydown',e=>{if(e.key==='Enter')sendPin();});
devname.addEventListener('keydown',e=>{if(e.key==='Enter')pin.focus();});
let wasWaiting=!card.classList.contains('off');
setInterval(async()=>{
const name=await (await fetch('/?waiting=1')).text();
const wanted=name.length>0;
if(wanted!==wasWaiting){
card.classList.toggle('off',!wanted);
if(wanted){said.textContent='';devname.focus();}
// The box has just gone: the attempt ended, one way or the other, and the list below is
// where the outcome shows.
else reloadClients();
wasWaiting=wanted;}
if(wanted)who.innerHTML='A device calling itself <b>'+name.replace(/&/g,'&amp;').replace(/</g,'&lt;')+'</b> wants to pair with this machine.';
const both=(await (await fetch('/?status=1')).text()).split('\n');
$('host').innerHTML=both[0];$('status').innerHTML=both[1]||'';
// The badge follows without a page reload: every tile's class is set from the one row
// the server says is running, which is cheap enough to do on every tick of this poll.
document.querySelectorAll('#games .game').forEach(el=>el.classList.toggle('running',el.dataset.id===(both[2]||'')));
// The palette follows the machine, not the browser: the attribute the stylesheet keys on
// is refreshed with the rest, so a theme flipped in Settings reaches the page in a second.
const theme=(await (await fetch('/?theme=1')).text()).trim();
if(theme&&document.documentElement.dataset.theme!==theme)document.documentElement.dataset.theme=theme;
},1000);
if(!card.classList.contains('off'))devname.focus();

// --- automatic sign-in: the button opens Windows' window on the machine, and the page
// then asks every few seconds whether the switch was made there ---
const al=$('autologon');if(al){$('alopen').addEventListener('click',async e=>{
e.target.disabled=true;
$('alsaid').textContent=await (await fetch('/?autologon=setup')).text();
const until=Date.now()+600000;const poll=setInterval(async()=>{
if(Date.now()>until){clearInterval(poll);e.target.disabled=false;return;}
if((await (await fetch('/?autologon=state')).text()).trim()==='on'){clearInterval(poll);
al.innerHTML='<p>Automatic sign-in is on now. This host comes back on its own after a restart.</p>';}
},5000);});}

// --- the log drawer ---
const logbox=$('logbox'),log=$('log');
$('logtoggle').addEventListener('click',()=>{logbox.classList.toggle('open');
if(logbox.classList.contains('open'))log.scrollTop=log.scrollHeight;});
// Only while it is open: sixty-four kilobytes every three seconds into a shut drawer is
// work for nobody.
setInterval(async()=>{if(!logbox.classList.contains('open'))return;
const atEnd=log.scrollTop+log.clientHeight>=log.scrollHeight-8;
log.textContent=await (await fetch('/?log=1')).text();
if(atEnd)log.scrollTop=log.scrollHeight;},3000);

// --- the editor ---
const games=$('games'),editor=$('editor'),title=$('title'),command=$('command'),folder=$('folder'),
editorsaid=$('editorsaid'),coverbox=$('coverbox'),arturl=$('arturl');
let editing=0;
async function reload(){games.innerHTML=await (await fetch('/?games=1')).text();}
// Rescan. The answer says only that the scan started, so the list is fetched twice
// afterwards: a library of a hundred games takes a few seconds.
const scansaid=$('scansaid');
$('rescan').addEventListener('click',async e=>{const b=e.target;b.disabled=true;
scansaid.textContent=await (await fetch('/?rescan=1')).text();
setTimeout(reload,2000);
setTimeout(()=>{reload();scansaid.textContent='';b.disabled=false;},6000);});
function open(id,name,starts,from,pointerOn,level,cardOn){editing=id;
$('editortitle').textContent=id?'Edit game':'Add a game';
title.value=name||'';command.value=starts||'';folder.value=from||'';
quality.value=level===undefined?2:level;
if(window.pointer)pointer.checked=pointerOn==='1';
startcard.checked=cardOn!=='0';
editorsaid.textContent='';
// A game that does not exist yet has nowhere to put a cover, so that half of the window is
// shown only once there is a row to attach one to.
coverbox.style.display=id?'':'none';
arturl.value='';
editor.showModal();title.focus();}
$('cancel').addEventListener('click',()=>editor.close());
$('save').addEventListener('click',async()=>{
if(!title.value.trim()||!command.value.trim()){
editorsaid.textContent='A game needs a name and something to start.';return;}
const r=await fetch('/?save='+editing+'&title='+encodeURIComponent(title.value)+'&command='+encodeURIComponent(command.value)+'&folder='+encodeURIComponent(folder.value)+'&pointer='+(window.pointer&&pointer.checked?1:0)+'&quality='+quality.value+'&card='+(startcard.checked?1:0));
editorsaid.textContent=await r.text();await reload();editor.close();});
// --- the cover picker ---
// Searched at once for the editor's name; a portrait that does not exist falls back once.
const picker=$('picker'),pickname=$('pickname'),pickgrid=$('pickgrid'),
picksaid=$('picksaid');
async function search(){const name=pickname.value.trim();
if(!name){picksaid.textContent='Type a name to look for.';pickname.focus();return;}
picksaid.textContent='looking…';pickgrid.innerHTML='';
const found=await (await fetch('/?artlist=1&title='+encodeURIComponent(name))).json();
const none='Nothing with a picture was found under "'+name+'". Try the name a store would use.';
if(!found.length){picksaid.textContent=none;return;}
// Counted again whenever one drops out, because the count is written before a single
// picture has loaded and the answer is only true once they have.
const count=()=>{const n=pickgrid.children.length;
picksaid.textContent=n?n+' found — click the right one':none;};
count();
for(const c of found){const b=document.createElement('button');b.type='button';b.className='pick';
b.title=c.name;
const img=document.createElement('img');img.loading='lazy';img.alt='';img.src=c.src;
// Out of the window rather than shown as an empty frame: an entry whose portrait answers
// 404 has no cover to choose, and there is nothing else here worth offering instead.
img.onerror=()=>{b.remove();count();};
const cap=document.createElement('span');cap.textContent=c.name;
b.append(img,cap);pickgrid.append(b);}
count();}
$('find').addEventListener('click',()=>{pickname.value=title.value.trim();
picksaid.textContent='';pickgrid.innerHTML='';picker.showModal();search();});
$('picksearch').addEventListener('click',search);
pickname.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();search();}});
// A click on a picture chooses it; a click on the backdrop — the dialog element itself,
// outside its box — closes the window.
pickgrid.addEventListener('click',async e=>{const b=e.target.closest('.pick');if(!b)return;
const img=b.querySelector('img');if(!img)return;
picksaid.textContent='fetching…';
// The address of the picture on screen, not the entry's number: after the fallback above
// the two can differ, and what is stored should be the picture that was clicked.
const r=await fetch('/?arturl='+editing+'&url='+encodeURIComponent(img.currentSrc||img.src));
editorsaid.textContent=await r.text();picker.close();await reload();});
picker.addEventListener('click',e=>{if(e.target===picker)picker.close();});
$('fetch').addEventListener('click',async()=>{
editorsaid.textContent='fetching…';
const r=await fetch('/?arturl='+editing+'&url='+encodeURIComponent(arturl.value));
editorsaid.textContent=await r.text();await reload();});
$('file').addEventListener('change',async e=>{
if(!e.target.files.length)return;editorsaid.textContent='uploading…';
const r=await fetch('/?upload='+editing,{method:'POST',body:e.target.files[0]});
editorsaid.textContent=await r.text();e.target.value='';await reload();});

// Every tile is handled here rather than each on its own, so that the grid can be replaced
// whole after a change without anything being wired up again.
games.addEventListener('click',async e=>{
if(e.target.closest('#addtile')){open(0);return;}
const button=e.target.closest('button[data-do]');if(!button)return;
const tile=button.closest('.game');
if(button.dataset.do==='edit'){open(tile.dataset.id,tile.dataset.title,tile.dataset.command,
tile.dataset.folder,tile.dataset.pointer,tile.dataset.quality,tile.dataset.card);return;}
if(button.dataset.do==='remove'){
if(!confirm('Remove '+tile.dataset.title+' from the list?'))return;
await fetch('/?remove='+tile.dataset.id);await reload();return;}
if(button.dataset.do==='stop'){
if(!confirm('Stop '+tile.dataset.title+'?'))return;
await fetch('/?stop=1');await reload();}});

// The paired devices, the same way. Forgetting one is asked about first: it is the one
// thing here that somebody else has to undo, by pairing their device again.
clients.addEventListener('click',async e=>{
const button=e.target.closest('button[data-do=forget]');if(!button)return;
const row=button.closest('.client');
if(!confirm('Forget '+row.dataset.name+'? It will have to pair again.'))return;
await fetch('/?forget='+row.dataset.id);
await reloadClients();});

// --- the refused addresses. Absent from the page altogether while the ports are not
// forwarded, so everything here waits on the element being there at all.
const blocked=$('blocked');
if(blocked){
// Every few seconds rather than every one: the only thing moving here is the time left,
// and it is shown in minutes.
const reloadBlocked=async()=>{
blocked.innerHTML=await (await fetch('/?blocked=1')).text();};
setInterval(reloadBlocked,5000);
blocked.addEventListener('click',async e=>{
const button=e.target.closest('button[data-do=unblock]');if(!button)return;
const row=button.closest('.client');
if(!confirm('Let '+row.dataset.ip+' back in? It is counted from nothing again.'))return;
await fetch('/?unblock='+encodeURIComponent(row.dataset.ip));
await reloadBlocked();});}
