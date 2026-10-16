'use strict';
// PWA da ponte. Token: chega pelo #token= do QR (não vai pro servidor em nenhuma requisição
// de página) e fica no localStorage; toda chamada /api leva Authorization: Bearer.
const $ = s => document.querySelector(s);
let token = localStorage.getItem('orbit.token');
const fromHash = location.hash.match(/token=([^&]+)/);
if (fromHash) {
  token = decodeURIComponent(fromHash[1]);
  localStorage.setItem('orbit.token', token);
  history.replaceState(null, '', location.pathname);
}

function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.remove('hidden');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => t.classList.add('hidden'), 2600);
}

function showPair(show) { $('#pair').classList.toggle('hidden', !show); }

async function api(path, { method = 'GET', json, body } = {}) {
  if (!token) { showPair(true); throw new Error('Pareie com o PC primeiro'); }
  const headers = { Authorization: `Bearer ${token}` };
  if (json !== undefined) { headers['Content-Type'] = 'application/json'; body = JSON.stringify(json); }
  const r = await fetch('/api' + path, { method, headers, body });
  if (r.status === 401) { showPair(true); throw new Error('Token inválido — pareie de novo'); }
  if (r.status === 429) throw new Error('Calma: muitas ações seguidas');
  if (!r.ok) throw new Error(`O PC respondeu ${r.status}`);
  setOnline(true);
  return r.status === 204 ? null : r.json();
}

async function run(fn, ok) {
  try { await fn(); if (ok) toast(ok); }
  catch (e) { toast(e.message || 'Falhou'); }
}

function setOnline(on) {
  $('#dot').className = 'dot ' + (on ? 'on' : 'off');
  $('#state').textContent = on ? 'PC conectado' : 'PC fora do ar';
}

async function ping() {
  try { setOnline((await fetch('/health', { cache: 'no-store' })).ok); }
  catch { setOnline(false); }
}

// --- Parear ---
$('#saveToken').onclick = () => {
  const v = $('#tokenInput').value.trim();
  if (!v) return;
  token = v;
  localStorage.setItem('orbit.token', v);
  showPair(false);
  toast('Token salvo');
  refresh();
};
showPair(!token);

// --- Link ---
$('#pasteUrl').onclick = () => run(async () => { $('#url').value = await navigator.clipboard.readText(); });
$('#openUrl').onclick = () => run(
  () => api('/links/open', { method: 'POST', json: { url: $('#url').value.trim() } }), 'Aberto no PC');

// --- Clipboard ---
$('#sendClip').onclick = () => run(
  () => api('/clipboard', { method: 'PUT', json: { text: $('#clip').value } }), 'Enviado pro PC');
$('#getClip').onclick = () => run(async () => {
  const { text } = await api('/clipboard');
  $('#clip').value = text;
  try { await navigator.clipboard.writeText(text); toast('Copiado no iPhone'); }
  catch { toast('Texto do PC abaixo'); }
});

// --- Arquivos (XHR pra ter progresso) ---
$('#files').onchange = e => {
  const files = [...e.target.files];
  if (!files.length) return;
  const form = new FormData();
  files.forEach(f => form.append('file', f, f.name));
  const xhr = new XMLHttpRequest();
  xhr.open('POST', '/api/files');
  xhr.setRequestHeader('Authorization', `Bearer ${token}`);
  $('#progress').classList.remove('hidden');
  xhr.upload.onprogress = p => { if (p.lengthComputable) $('#bar').style.width = (p.loaded / p.total * 100) + '%'; };
  xhr.onloadend = () => {
    $('#progress').classList.add('hidden');
    $('#bar').style.width = '0';
    e.target.value = '';
    if (xhr.status === 200) toast(files.length === 1 ? 'Arquivo enviado' : `${files.length} arquivos enviados`);
    else if (xhr.status === 401) { showPair(true); toast('Token inválido — pareie de novo'); }
    else toast('Falhou o envio');
  };
  xhr.send(form);
};

// --- Mídia ---
document.querySelectorAll('[data-media]').forEach(b => b.onclick = () => run(async () => {
  await api('/media/' + b.dataset.media, { method: 'POST' });
  setTimeout(refreshMedia, 400);
}));
let volTimer;
$('#volume').oninput = e => {
  clearTimeout(volTimer);
  volTimer = setTimeout(() => run(() => api('/media/volume', { method: 'PUT', json: { percent: +e.target.value } })), 150);
};
async function refreshMedia() {
  try {
    const m = await api('/media');
    $('#now').textContent = m.title ? `${m.playing ? '▶' : '⏸'} ${m.title}${m.artist ? ' · ' + m.artist : ''}` : 'Nada tocando';
    if (document.activeElement !== $('#volume')) $('#volume').value = m.volume;
  } catch { /* offline: mantém o que está na tela */ }
}

// --- Stats ---
async function refreshStats() {
  try {
    const s = await api('/stats');
    $('#cpu').textContent = `${s.cpu}%`;
    $('#ram').textContent = `${s.ramUsedGb}/${s.ramTotalGb}`;
    $('#disk').textContent = `${s.diskUsedGb}/${s.diskTotalGb}`;
    $('#bat').textContent = s.batteryPercent == null ? '—' : `${s.batteryPercent}%${s.charging ? '⚡' : ''}`;
  } catch { /* idem */ }
}

// --- Energia ---
document.querySelectorAll('[data-power]').forEach(b => b.onclick = () => {
  const label = b.textContent.trim();
  if (b.classList.contains('danger') && !confirm(`${label} o PC agora?`)) return;
  run(() => api('/power/' + b.dataset.power, { method: 'POST' }), `${label}: enviado`);
});

// Polling só com o app visível (nada roda em segundo plano no iPhone).
function refresh() { ping(); if (token) { refreshStats(); refreshMedia(); } }
let loop;
function startLoop() { clearInterval(loop); refresh(); loop = setInterval(refresh, 5000); }
document.addEventListener('visibilitychange', () => document.hidden ? clearInterval(loop) : startLoop());
startLoop();

if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});
