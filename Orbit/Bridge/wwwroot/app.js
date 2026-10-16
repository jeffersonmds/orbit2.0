'use strict';
// PWA da ponte. Token: chega pelo #token= do QR (não vai pro servidor em nenhuma requisição
// de página) e fica no localStorage; toda chamada /api leva Authorization: Bearer.
const $ = s => document.querySelector(s);
let token = localStorage.getItem('orbit.token');
const fromHash = location.hash.match(/token=([^&]+)/);
if (fromHash) {
  try { token = decodeURIComponent(fromHash[1]); } catch { token = fromHash[1]; }
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
  let r;
  try { r = await fetch('/api' + path, { method, headers, body }); }
  catch { setOnline(false); throw new Error('PC fora do ar'); }
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
$('#saveToken').onclick = async () => {
  // aceita: código de 6 dígitos (troca pelo token no PC), o link inteiro do QR, ou só o token
  const raw = $('#tokenInput').value.trim();
  const digits = raw.replace(/\D/g, '');
  if (digits.length === 6 && raw.replace(/[\s-]/g, '').length === 6) {
    try {
      const r = await fetch('/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: digits }) });
      if (r.status === 429) { toast('Muitas tentativas: espere 1 minuto'); return; }
      if (!r.ok) { toast('Código inválido ou expirado — gere outro no PC'); return; }
      const { token: t } = await r.json();
      token = t;
      localStorage.setItem('orbit.token', t);
      $('#tokenInput').value = '';
      showPair(false);
      toast('Pareado com o PC');
      refresh();
    } catch { toast('PC fora do ar'); }
    return;
  }
  let v = raw.replace(/^.*[#&]token=/, '');
  try { v = decodeURIComponent(v); } catch {}
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
// ClipboardItem com Promise criada dentro do clique: no WebKit, writeText depois de await perde o gesto.
$('#getClip').onclick = () => {
  const blob = api('/clipboard').then(({ text }) => { $('#clip').value = text; return new Blob([text], { type: 'text/plain' }); });
  if (navigator.clipboard && window.ClipboardItem) {
    navigator.clipboard.write([new ClipboardItem({ 'text/plain': blob })])
      .then(() => toast('Copiado no iPhone'), () => blob.then(() => toast('Texto do PC abaixo'), e => toast(e.message)));
  } else {
    blob.then(() => toast('Texto do PC abaixo'), e => toast(e.message));
  }
};

// --- Arquivos (XHR pra ter progresso) ---
$('#files').onchange = e => {
  const files = [...e.target.files];
  if (!files.length) return;
  if (!token) { showPair(true); toast('Pareie com o PC primeiro'); e.target.value = ''; return; }
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
    if (xhr.status === 200) {
      let where = '';
      try { const f = JSON.parse(xhr.responseText).folder || ''; where = f ? ' → ' + f.split(/[\\/]/).slice(-2).join('\\') : ''; } catch {}
      toast((files.length === 1 ? 'Arquivo enviado' : `${files.length} arquivos enviados`) + where);
    }
    else if (xhr.status === 413) toast('Arquivo grande demais (máx. 500 MB por envio)');
    else if (xhr.status === 429) toast('Calma: muitas ações seguidas');
    else if (xhr.status === 401) { showPair(true); toast('Token inválido — pareie de novo'); }
    else toast('Falhou o envio');
  };
  xhr.send(form);
};

// --- Mídia ---
// Ícones fixos (strings constantes do próprio app; nada vindo do PC passa por innerHTML).
const ICON = {
  play: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5.6v12.8a1 1 0 0 0 1.53.85l10.1-6.4a1 1 0 0 0 0-1.7L9.53 4.75A1 1 0 0 0 8 5.6z"/></svg>',
  pause: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="6" y="5" width="4.2" height="14" rx="1.3"/><rect x="13.8" y="5" width="4.2" height="14" rx="1.3"/></svg>',
};
let playing = false, lastTrack = null, coverOk = false, coverUrl = null;
function setPlaying(p) {
  playing = p;
  const b = $('#playBtn');
  b.innerHTML = p ? ICON.pause : ICON.play;
  b.setAttribute('aria-label', p ? 'Pausar' : 'Tocar');
}
function setVol(v) {
  const n = Math.round(+v);
  $('#volume').value = n;
  $('#volume').style.setProperty('--p', n + '%');
  $('#volVal').textContent = n + '%';
}
setPlaying(false);

document.querySelectorAll('[data-media]').forEach(b => b.onclick = () => run(async () => {
  if (b.dataset.media === 'playpause') setPlaying(!playing); // resposta imediata; o polling confirma
  await api('/media/' + b.dataset.media, { method: 'POST' });
  setTimeout(refreshMedia, 600);
}));
let volTimer, dragging = false;
['pointerdown'].forEach(t => $('#volume').addEventListener(t, () => { dragging = true; }));
['pointerup', 'pointercancel'].forEach(t => $('#volume').addEventListener(t, () => { dragging = false; }));
$('#volume').oninput = e => {
  setVol(e.target.value);
  clearTimeout(volTimer);
  volTimer = setTimeout(() => run(() => api('/media/volume', { method: 'PUT', json: { percent: +e.target.value } })), 150);
};

async function loadCover() {
  try {
    const r = await fetch('/api/media/cover', { headers: { Authorization: `Bearer ${token}` } });
    if (r.status !== 200) return;
    if (coverUrl) URL.revokeObjectURL(coverUrl);
    coverUrl = URL.createObjectURL(await r.blob());
    $('#coverImg').src = coverUrl;
    $('#cover').classList.add('has');
    coverOk = true;
  } catch { /* sem capa: fica o ícone */ }
}

async function refreshMedia() {
  try {
    const m = await api('/media');
    const has = !!m.title;
    $('#media').classList.toggle('idle', !has);
    $('#mTitle').textContent = has ? m.title : 'Nada tocando';
    $('#mArtist').textContent = has ? (m.artist || '') : 'Toque algo no PC';
    setPlaying(m.playing);
    if (!dragging) setVol(m.volume);
    const key = has ? `${m.title}|${m.artist}` : null;
    if (key !== lastTrack) {
      lastTrack = key;
      coverOk = false;
      $('#cover').classList.remove('has');
    }
    if (has && !coverOk) loadCover(); // a capa às vezes chega depois do título: tenta de novo no próximo ciclo
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
// Toda ação de energia pede confirmação numa folha própria (não no confirm() do navegador).
const POWER_TEXT = {
  lock: ['Bloquear o PC?', 'A tela do PC vai para a tela de bloqueio.', false],
  sleep: ['Suspender o PC?', 'A ponte fica fora do ar até o PC acordar.', false],
  restart: ['Reiniciar o PC?', 'Programas abertos serão fechados. Trabalho não salvo pode ser perdido.', true],
  shutdown: ['Desligar o PC?', 'Programas abertos serão fechados e a ponte sai do ar.', true],
};
function ask(title, text, okLabel, dangerous) {
  return new Promise(resolve => {
    $('#sheetTitle').textContent = title;
    $('#sheetText').textContent = text;
    const ok = $('#sheetOk');
    ok.textContent = okLabel;
    ok.classList.toggle('calm', !dangerous);
    $('#sheet').classList.remove('hidden');
    const done = answer => {
      $('#sheet').classList.add('hidden');
      ok.onclick = $('#sheetCancel').onclick = $('#sheet').onclick = null;
      resolve(answer);
    };
    ok.onclick = e => { e.stopPropagation(); done(true); };
    $('#sheetCancel').onclick = e => { e.stopPropagation(); done(false); };
    $('#sheet').onclick = e => { if (e.target === $('#sheet')) done(false); }; // toque fora = cancelar
  });
}
document.querySelectorAll('[data-power]').forEach(b => b.onclick = async () => {
  const label = b.textContent.trim();
  const [title, text, dangerous] = POWER_TEXT[b.dataset.power];
  if (!(await ask(title, text, label, dangerous))) return;
  run(() => api('/power/' + b.dataset.power, { method: 'POST' }), `${label}: enviado`);
});

// --- Câmera ---
const camImg = $('#camImg'), camToggle = $('#camToggle'), camShot = $('#camShot');
const camDevice = $('#camDevice'), camQuality = $('#camQuality'), camView = $('#camView'), camMsg = $('#camMsg');
let camStreaming = false, camLoaded = false;
const camName = () => camDevice.selectedOptions[0]?.textContent || '';

function camSay(msg) {
  camMsg.textContent = msg || '';
  camMsg.classList.toggle('hidden', !msg);
}

// Lista as câmeras do PC (não abre nenhuma: a luz não acende aqui). Repetida pelo loop até dar certo,
// então para; não recarrega durante o stream pra não zerar o seletor.
async function camLoadDevices() {
  if (!token || camStreaming) return;
  try {
    const r = await fetch('/api/camera/devices', { headers: { Authorization: `Bearer ${token}` } });
    if (r.status === 403) { camSay('Ligue "Câmera" na aba iPhone do PC.'); return; }
    if (!r.ok) return;
    const list = await r.json();
    camDevice.innerHTML = '';
    for (const d of list) {
      const o = document.createElement('option');
      o.value = d.id; o.textContent = d.name;
      camDevice.appendChild(o);
    }
    camLoaded = true;
    camSay(list.length ? '' : 'Nenhuma câmera encontrada.');
  } catch { /* offline: o loop tenta de novo */ }
}

async function camStart() {
  try {
    const r = await fetch('/api/camera/open', {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ deviceId: camDevice.value, name: camName(), quality: camQuality.value, fps: '24' }),
    });
    if (r.status === 403) { camSay('Ligue "Câmera" na aba iPhone do PC.'); return; }
    if (!r.ok) { camSay('Não consegui abrir a câmera.'); return; }
    const { ticket } = await r.json();
    camImg.onerror = () => { camSay('Câmera em uso por outro programa, ou o stream caiu.'); camStop(); };
    // só o tíquete (descartável) vai na URL; o token fica no cabeçalho do /open
    camImg.src = `/api/camera/stream?ticket=${encodeURIComponent(ticket)}`;
    camStreaming = true;
    camToggle.textContent = 'Parar';
    camView.classList.remove('hidden');
    camSay('');
  } catch { camSay('PC fora do ar'); }
}

// GIF 1x1 transparente: atribuir um src NOVO aborta a conexão MJPEG (o Safari não a fecha só
// removendo o atributo), e aí o PC solta a câmera e apaga a luz.
const BLANK = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==';
function camStop() {
  camImg.onerror = null;
  camImg.src = BLANK; // solta a <img>
  camStreaming = false;
  camToggle.textContent = 'Ver câmera';
  camView.classList.add('hidden');
  // o Safari não aborta a conexão MJPEG sozinho: manda o PC fechar a câmera (apaga a luz)
  if (token) fetch('/api/camera/close', { method: 'POST', headers: { Authorization: `Bearer ${token}` } }).catch(() => {});
}

camToggle.onclick = () => camStreaming ? camStop() : camStart();

// Trocar câmera/qualidade com o stream ligado reabre direto na nova (novo tíquete; o <img> novo
// aborta o anterior e o PC solta a câmera antiga).
const camSwitch = () => { if (camStreaming) camStart(); };
camDevice.onchange = camSwitch;
camQuality.onchange = camSwitch;

camShot.onclick = async () => {
  try {
    const r = await fetch('/api/camera/photo', {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ deviceId: camDevice.value, name: camName() }),
    });
    if (!r.ok) { toast('Não consegui tirar a foto'); return; }
    const blob = await r.blob();
    const file = new File([blob], `orbit-${Date.now()}.jpg`, { type: 'image/jpeg' });
    if (navigator.canShare && navigator.canShare({ files: [file] })) {
      await navigator.share({ files: [file] }); // iOS: "Salvar imagem" → Fotos
    } else {
      window.open(URL.createObjectURL(blob), '_blank'); // fallback: segurar a imagem → Adicionar às Fotos
    }
  } catch (e) {
    if (e && e.name === 'AbortError') return; // folha cancelada
    toast('Não consegui salvar a foto');
  }
};

// Polling só com o app visível (nada roda em segundo plano no iPhone).
function refresh() { ping(); if (token) { refreshStats(); refreshMedia(); if (!camLoaded) camLoadDevices(); } }
let loop;
function startLoop() { clearInterval(loop); refresh(); loop = setInterval(refresh, 5000); }
document.addEventListener('visibilitychange', () => document.hidden ? clearInterval(loop) : startLoop());
startLoop();

if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});
