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
// Últimos links abertos (só neste iPhone, no localStorage): tocar reabre no PC.
const LINKS_KEY = 'orbit.links';
function loadLinks() { try { return JSON.parse(localStorage.getItem(LINKS_KEY)) || []; } catch { return []; } }
async function openLink(url) {
  await api('/links/open', { method: 'POST', json: { url } });
  localStorage.setItem(LINKS_KEY, JSON.stringify([url, ...loadLinks().filter(u => u !== url)].slice(0, 10)));
  renderLinks();
}
function renderLinks() {
  const links = loadLinks();
  $('#linksTitle').classList.toggle('hidden', !links.length);
  $('#links').replaceChildren(...links.map(u => {
    let host = u;
    try { host = new URL(u.includes('://') ? u : 'https://' + u).host; } catch {}
    return pfRow('link', host, u, () => run(() => openLink(u), 'Aberto no PC'));
  }));
}
$('#openUrl').onclick = () => run(() => openLink($('#url').value.trim()), 'Aberto no PC');

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
    $('#tileMedia').textContent = has ? [m.title, m.artist].filter(Boolean).join(' · ') : 'Nada tocando';
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

// --- Notas ---
// Carrega ao entrar na página (não no loop de 5 s, que apagaria o que está sendo digitado);
// com edição não salva, só o "Recarregar" explícito sobrescreve.
let notesDirty = false;
async function loadNotes() {
  const { text } = await api('/notes');
  $('#notes').value = text;
  notesDirty = false;
}
$('#notes').oninput = () => { notesDirty = true; };
$('#loadNotes').onclick = () => run(loadNotes, 'Notas do PC');
$('#saveNotes').onclick = () => run(async () => {
  await api('/notes', { method: 'PUT', json: { text: $('#notes').value } });
  notesDirty = false;
}, 'Notas salvas no PC');

// --- Stats ---
async function refreshStats() {
  try {
    const s = await api('/stats');
    const pct = (a, b) => b ? Math.round(a / b * 100) : 0;
    const gb = n => (+n).toLocaleString('pt-BR', { maximumFractionDigits: 1 });
    const meter = (id, value, sub, percent) => {
      $('#' + id).textContent = value;
      if (sub != null) $('#' + id + 'Sub').textContent = sub;
      $('#' + id + 'Bar').style.width = Math.min(100, Math.max(0, percent)) + '%';
    };
    const cpu = Math.round(s.cpu);
    meter('cpu', `${cpu}%`, null, cpu);
    meter('ram', `${pct(s.ramUsedGb, s.ramTotalGb)}%`, `${gb(s.ramUsedGb)} de ${gb(s.ramTotalGb)} GB`, pct(s.ramUsedGb, s.ramTotalGb));
    meter('disk', `${pct(s.diskUsedGb, s.diskTotalGb)}%`, `${gb(s.diskTotalGb - s.diskUsedGb)} GB livres`, pct(s.diskUsedGb, s.diskTotalGb));
    const b = s.batteryPercent;
    meter('bat', b == null ? '—' : `${b}%`, b == null ? 'Sem bateria' : (s.charging ? 'Carregando' : 'Na bateria'), b ?? 0);
    $('#tileStats').textContent = `CPU ${cpu}% · RAM ${pct(s.ramUsedGb, s.ramTotalGb)}%`;
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
    camView.classList.add('live');
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
  camView.classList.remove('live');
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

// --- Arquivos do PC (só leitura) ---
// Nomes vindos do PC só entram por textContent; innerHTML só recebe os ícones constantes abaixo.
const PF_ICON = {
  dir: '<svg viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>',
  other: '<svg viewBox="0 0 24 24"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/></svg>',
  doc: '<svg viewBox="0 0 24 24"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5M9 13h6M9 17h6"/></svg>',
  image: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="M21 16l-5-5-9 9"/></svg>',
  video: '<svg viewBox="0 0 24 24"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M10 9.5v5l4.5-2.5z"/></svg>',
  audio: '<svg viewBox="0 0 24 24"><path d="M9 18V5l11-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="17" cy="16" r="3"/></svg>',
  up: '<svg viewBox="0 0 24 24"><path d="M15 5l-7 7 7 7"/></svg>',
  link: '<svg viewBox="0 0 24 24"><path d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7"/><path d="M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7"/></svg>',
};
const PF_KIND = [
  ['image', /\.(jpe?g|png|gif|webp|heic|bmp|svg)$/i],
  ['video', /\.(mp4|mov|mkv|avi|webm|m4v)$/i],
  ['audio', /\.(mp3|m4a|wav|flac|ogg|aac)$/i],
  ['doc', /\.(pdf|docx?|xlsx?|pptx?|txt|md|csv|odt)$/i],
];
const pfKind = name => (PF_KIND.find(([, re]) => re.test(name)) || ['other'])[0]; // 'other', não 'file': .file é o botão de upload no CSS

// "hoje 09:12", "ontem 18:35", "15 out", "02/03/2025"
function pfDate(iso) {
  const d = new Date(iso), now = new Date();
  if (isNaN(d)) return '';
  const day = x => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((day(now) - day(d)) / 86400000);
  const hm = d.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
  if (diff === 0) return `hoje ${hm}`;
  if (diff === 1) return `ontem ${hm}`;
  if (d.getFullYear() === now.getFullYear()) return d.toLocaleDateString('pt-BR', { day: 'numeric', month: 'short' }).replace('.', '');
  return d.toLocaleDateString('pt-BR');
}
const pf = { root: null, rootName: '', path: '' };
const pfJoin = name => pf.path ? `${pf.path}/${name}` : name;

function pfSize(b) {
  if (b < 1024) return `${b} B`;
  const u = ['KB', 'MB', 'GB'];
  let i = -1;
  do { b /= 1024; i++; } while (b >= 1024 && i < u.length - 1);
  return `${b.toLocaleString('pt-BR', { maximumFractionDigits: b < 10 ? 1 : 0 })} ${u[i]}`;
}

function pfRow(icon, name, sub, onclick) {
  const b = document.createElement('button');
  b.className = 'frow ' + icon;
  b.innerHTML = `<span class="fico">${PF_ICON[icon]}</span>`;
  const t = document.createElement('span'); t.className = 'ftext';
  const n = document.createElement('span'); n.className = 'fname'; n.textContent = name;
  t.append(n);
  if (sub) { const s = document.createElement('span'); s.className = 'fsub'; s.textContent = sub; t.append(s); }
  b.append(t);
  b.onclick = onclick;
  return b;
}

async function pfLoad() {
  const list = $('#pfList'), msg = $('#pfMsg');
  msg.classList.add('hidden');
  try {
    if (!pf.root) {
      const roots = await api('/pcfiles/roots');
      $('#pfCrumb').textContent = 'Pastas do PC';
      list.replaceChildren(...roots.map(r => pfRow('dir', r.name, '', () => { pf.root = r.id; pf.rootName = r.name; pf.path = ''; pfLoad(); })));
      return;
    }
    const entries = await api(`/pcfiles/list?root=${encodeURIComponent(pf.root)}&path=${encodeURIComponent(pf.path)}`);
    const parts = [pf.rootName, ...pf.path.split('/').filter(Boolean)];
    $('#pfCrumb').textContent = parts.join(' › ');
    const up = pfRow('up', parts.length > 1 ? parts[parts.length - 2] : 'Pastas do PC', '', () => {
      if (pf.path) pf.path = pf.path.split('/').slice(0, -1).join('/'); else pf.root = null;
      pfLoad();
    });
    list.replaceChildren(up, ...entries.map(e => e.dir
      ? pfRow('dir', e.name, pfDate(e.modified), () => { pf.path = pfJoin(e.name); pfLoad(); })
      : pfRow(pfKind(e.name), e.name, `${pfSize(e.size)} · ${pfDate(e.modified)}`, () => pfDownload(e.name))));
    if (!entries.length) { msg.textContent = 'Pasta vazia.'; msg.classList.remove('hidden'); }
  } catch (e) {
    list.replaceChildren();
    msg.textContent = /403/.test(e.message) ? 'Ligue "Arquivos" na aba iPhone do PC.' : e.message;
    msg.classList.remove('hidden');
  }
}

// Tíquete (com token) e depois navega pro download: o PC responde como anexo, então o iOS
// oferece baixar sem sair do app. Só o tíquete descartável vai na URL, nunca o token.
async function pfDownload(name) {
  try {
    const { ticket } = await api('/pcfiles/ticket', { method: 'POST', json: { root: pf.root, path: pfJoin(name) } });
    location.href = `/api/pcfiles/download?ticket=${encodeURIComponent(ticket)}`;
  } catch (e) { toast(e.message); }
}

// --- Tela do PC (ver + controlar) ---
// Vídeo: MJPEG por tíquete (como a câmera). Controle: WebSocket por tíquete, mensagens JSON pequenas.
// Modo toque: tocar clica ali, arrastar arrasta, segurar = botão direito, dois dedos rolam.
// Modo trackpad: o dedo move o cursor (relativo), toque clica, toque com dois dedos = botão direito.
const scr = { live: false, loaded: false, ws: null, mode: 'touch', ctrl: false };
const scrImg = $('#scrImg'), scrView = $('#scrView'), scrTouch = $('#scrTouch');
const scrSay = msg => { $('#scrMsg').textContent = msg || ''; $('#scrMsg').classList.toggle('hidden', !msg); };
const scrSend = o => { if (scr.ws && scr.ws.readyState === 1) scr.ws.send(JSON.stringify(o)); };

async function scrLoad() {
  if (!token || scr.live) return;
  try {
    const list = await api('/screen/list');
    $('#scrScreen').replaceChildren(...list.map(s => Object.assign(document.createElement('option'), { value: s.id, textContent: s.name })));
    scr.loaded = true;
    scrSay(list.length ? '' : 'Nenhum monitor encontrado.');
  } catch (e) { scrSay(/403/.test(e.message) ? 'Ligue "Tela" na aba iPhone do PC.' : e.message); }
}

async function scrStart() {
  try {
    const screen = $('#scrScreen').value;
    const { ticket } = await api('/screen/open', { method: 'POST', json: { screen, quality: $('#scrQuality').value } });
    scrImg.onerror = () => { scrSay('A transmissão caiu.'); scrStop(); };
    scrImg.src = `/api/screen/stream?ticket=${encodeURIComponent(ticket)}`;
    scr.live = true;
    scrView.classList.add('live');
    $('#scrToggle').textContent = 'Parar';
    scrSay('');
    resetView();
    if (landscape.matches) scrImmersive(true); // já deitado: vai direto pra tela cheia
    scrConnectControl(screen);
  } catch (e) { scrSay(/403/.test(e.message) ? 'Ligue "Tela" na aba iPhone do PC.' : e.message); }
}

// Controle é opcional: sem a permissão "Controle", fica só olhando.
async function scrConnectControl(screen) {
  try {
    const { ticket } = await api('/remote/ticket', { method: 'POST', json: { screen } });
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/api/remote/input?ticket=${encodeURIComponent(ticket)}`);
    ws.onopen = () => $('#scrBar').classList.remove('hidden');
    ws.onclose = () => { $('#scrBar').classList.add('hidden'); if (scr.ws === ws) scr.ws = null; };
    scr.ws = ws;
  } catch (e) {
    if (/403/.test(e.message)) scrSay('Só visualização — ligue "Controle" na aba iPhone pra usar mouse e teclado.');
  }
}

function scrStop() {
  scrImg.onerror = null;
  scrImg.src = BLANK; // aborta a <img>…
  if (token) fetch('/api/screen/close', { method: 'POST', headers: { Authorization: `Bearer ${token}` } }).catch(() => {}); // …e o Safari precisa do aviso
  if (scr.ws) { scr.ws.close(); scr.ws = null; }
  scr.live = false;
  scrView.classList.remove('live');
  scrImmersive(false);
  $('#scrBar').classList.add('hidden');
  $('#scrToggle').textContent = 'Ver tela';
}

$('#scrToggle').onclick = () => scr.live ? scrStop() : scrStart();
const scrRestart = () => { if (scr.live) { scrStop(); scrStart(); } };
$('#scrScreen').onchange = scrRestart;
$('#scrQuality').onchange = scrRestart;

// Ponto do toque em 0..1 dentro da imagem (object-fit: contain deixa faixas pretas). null = fora dela.
function scrPoint(t) {
  const r = scrImg.getBoundingClientRect(), iw = scrImg.naturalWidth, ih = scrImg.naturalHeight;
  if (!iw || !ih) return null;
  const k = Math.min(r.width / iw, r.height / ih), w = iw * k, h = ih * k;
  const x = (t.clientX - r.left - (r.width - w) / 2) / w, y = (t.clientY - r.top - (r.height - h) / 2) / h;
  return x >= 0 && x <= 1 && y >= 0 && y <= 1 ? { x, y } : null;
}

// --- Zoom (pinça) ---
// Zoom pelo tamanho real da imagem na página (width/left), NÃO por transform: com transform o Safari
// rasteriza no tamanho sem zoom e estica — o texto borra mesmo com a qualidade Original. scrPoint usa
// o retângulo da imagem, então o toque continua acertando o ponto certo no PC com qualquer zoom.
const view = { z: 1, tx: 0, ty: 0 };
const MAX_ZOOM = 5;

function applyView() {
  const r = scrView.getBoundingClientRect();
  view.z = Math.min(MAX_ZOOM, Math.max(1, view.z));
  // não deixa a imagem sair da área: com zoom 1 fica parada; com zoom, as bordas não descolam
  view.tx = Math.min(0, Math.max(r.width - r.width * view.z, view.tx));
  view.ty = Math.min(0, Math.max(r.height - r.height * view.z, view.ty));
  scrImg.style.width = scrImg.style.height = `${view.z * 100}%`;
  scrImg.style.left = `${view.tx}px`;
  scrImg.style.top = `${view.ty}px`;
  $('#scrZoomReset').classList.toggle('hidden', view.z <= 1.01);
  $('#scrZoomReset').textContent = `${view.z.toFixed(1).replace('.', ',')}× ✕`;
}
function resetView() { view.z = 1; view.tx = 0; view.ty = 0; applyView(); }
$('#scrZoomReset').onclick = resetView;

// --- Gestos ---
// Um dedo: toque/trackpad (ver acima). Dois dedos: pinça = zoom (ancorado nos dedos);
// arrastar = move a imagem se houver zoom, senão rola no PC. Toque rápido de dois dedos = botão direito.
let gesture = null;
const mid = ts => ({ x: (ts[0].clientX + ts[1].clientX) / 2, y: (ts[0].clientY + ts[1].clientY) / 2 });
const dist = ts => Math.hypot(ts[0].clientX - ts[1].clientX, ts[0].clientY - ts[1].clientY);

scrTouch.addEventListener('touchstart', e => {
  e.preventDefault();
  if (e.touches.length >= 2) {
    if (gesture?.drag) scrSend({ t: 'up' });
    clearTimeout(gesture?.timer);
    const r = scrView.getBoundingClientRect(), m = mid(e.touches);
    gesture = {
      two: true, kind: null, at: Date.now(), moved: false,
      d0: dist(e.touches), m0: m, last: m, z0: view.z, tx0: view.tx, ty0: view.ty,
      // ponto da imagem (sem zoom) que está sob os dedos: fica sob eles durante a pinça
      u: { x: (m.x - r.left - view.tx) / view.z, y: (m.y - r.top - view.ty) / view.z },
    };
    return;
  }
  const t = e.touches[0];
  const g = gesture = { x0: t.clientX, y0: t.clientY, x: t.clientX, y: t.clientY, at: Date.now(), moved: false, drag: false, done: false, p0: scrPoint(t) };
  if (scr.mode === 'touch' && g.p0) {
    g.timer = setTimeout(() => {
      if (!g.moved) { scrSend({ t: 'abs', ...g.p0 }); scrSend({ t: 'click', b: 'right' }); g.done = true; }
    }, 550);
  }
}, { passive: false });

scrTouch.addEventListener('touchmove', e => {
  e.preventDefault();
  const g = gesture;
  if (!g) return;
  if (g.two) {
    if (e.touches.length < 2) return;
    const m = mid(e.touches), d = dist(e.touches), r = scrView.getBoundingClientRect();
    // decide uma vez: mudou a distância entre os dedos = pinça; senão é arrasto
    if (!g.kind) {
      if (Math.abs(d - g.d0) > 14) g.kind = 'pinch';
      else if (Math.hypot(m.x - g.m0.x, m.y - g.m0.y) > 8) g.kind = view.z > 1.01 ? 'pan' : 'scroll';
      else return;
      g.moved = true;
    }
    if (g.kind === 'pinch') {
      view.z = Math.min(MAX_ZOOM, Math.max(1, g.z0 * d / g.d0));
      view.tx = m.x - r.left - g.u.x * view.z;
      view.ty = m.y - r.top - g.u.y * view.z;
      applyView();
    } else if (g.kind === 'pan') {
      view.tx = g.tx0 + (m.x - g.m0.x);
      view.ty = g.ty0 + (m.y - g.m0.y);
      applyView();
    } else {
      const dy = m.y - g.last.y, dx = m.x - g.last.x;
      if (Math.abs(dy) + Math.abs(dx) > 2) {
        scrSend({ t: 'scroll', dy: Math.round(dy * 4), dx: Math.round(-dx * 4) });
        g.last = m;
      }
    }
    return;
  }
  const t = e.touches[0];
  if (Math.hypot(t.clientX - g.x0, t.clientY - g.y0) > 8) { g.moved = true; clearTimeout(g.timer); }
  if (scr.mode === 'pad') {
    scrSend({ t: 'move', dx: Math.round((t.clientX - g.x) * 2), dy: Math.round((t.clientY - g.y) * 2) });
  } else if (g.moved && !g.done && g.p0) {
    if (!g.drag) { scrSend({ t: 'abs', ...g.p0 }); scrSend({ t: 'down' }); g.drag = true; }
    const p = scrPoint(t);
    if (p) scrSend({ t: 'abs', ...p });
  }
  g.x = t.clientX; g.y = t.clientY;
}, { passive: false });

scrTouch.addEventListener('touchend', e => {
  e.preventDefault();
  const g = gesture;
  if (!g || e.touches.length) return;
  clearTimeout(g.timer);
  const quick = Date.now() - g.at < 300;
  if (g.two) { if (!g.moved && quick) scrSend({ t: 'click', b: 'right' }); }
  else if (g.drag) scrSend({ t: 'up' });
  else if (!g.moved && !g.done) {
    if (scr.mode === 'pad') scrSend({ t: 'click' });
    else if (g.p0) { scrSend({ t: 'abs', ...g.p0 }); scrSend({ t: 'click' }); }
  }
  gesture = null;
}, { passive: false });

// --- Tela cheia ---
// No iPhone não há Fullscreen API pra imagem; o app instalado já não tem barra do Safari, então
// basta a página cobrir tudo. Deitar o celular com a tela ligada entra sozinho; levantar sai.
const landscape = matchMedia('(orientation: landscape)');
function scrImmersive(on) {
  $('section[data-page="tela"]').classList.toggle('immersive', on);
  document.body.classList.toggle('immersive', on);
  $('#scrFs').setAttribute('aria-label', on ? 'Sair da tela cheia' : 'Tela cheia');
  requestAnimationFrame(resetView); // a área mudou de tamanho
}
$('#scrFs').onclick = () => scrImmersive(!document.body.classList.contains('immersive'));
landscape.addEventListener('change', e => { if (scr.live) scrImmersive(e.matches); });

$('#scrMode').onclick = () => {
  scr.mode = scr.mode === 'touch' ? 'pad' : 'touch';
  $('#scrMode').textContent = scr.mode === 'touch' ? 'Toque' : 'Trackpad';
  $('#scrMode').classList.toggle('on', scr.mode === 'pad');
};

// Ctrl "grudento": vale pra próxima tecla (Ctrl+C, Ctrl+V…) e solta.
$('#scrCtrl').onclick = () => { scr.ctrl = !scr.ctrl; $('#scrCtrl').classList.toggle('on', scr.ctrl); };
function scrKey(k) {
  scrSend({ t: 'key', k, mods: scr.ctrl ? ['ctrl'] : [] });
  if (scr.ctrl) { scr.ctrl = false; $('#scrCtrl').classList.remove('on'); }
}
document.querySelectorAll('[data-key]').forEach(b => b.onclick = () => scrKey(b.dataset.key));

// Teclado do iOS num campo invisível: beforeinput diz o que foi digitado; o campo fica sempre vazio.
$('#scrKbd').onclick = () => $('#scrInput').focus();
$('#scrInput').addEventListener('beforeinput', e => {
  e.preventDefault();
  if (e.inputType === 'insertLineBreak') scrKey('enter');
  else if (e.inputType === 'deleteContentBackward') scrKey('backspace');
  else if (e.data) {
    if (scr.ctrl && /^[a-z0-9]$/i.test(e.data)) scrKey(e.data.toLowerCase());
    else scrSend({ t: 'text', s: e.data });
  }
});

// --- Microfone (independente da câmera) ---
// PCM 16 bits mono pelo WebSocket, tocado com Web Audio num buffer curto (~150 ms) que se
// re-sincroniza se atrasar. No iPhone a Web Audio obedece a chave de silêncio: audioSession
// 'playback' (iOS 17+) faz tocar como mídia, igual a um vídeo.
const mic = { ws: null, ctx: null, gain: null, next: 0, rate: 24000 };
const micSay = msg => { $('#micMsg').textContent = msg || ''; $('#micMsg').classList.toggle('hidden', !msg); };

async function micLoad() {
  if (!token) return;
  try {
    const list = await api('/mic/devices');
    $('#micDevice').replaceChildren(...list.map(d => Object.assign(document.createElement('option'), { value: d.id, textContent: d.name })));
    micSay(list.length ? '' : 'Nenhum microfone no PC.');
  } catch (e) { micSay(/403/.test(e.message) ? 'Ligue "Microfone" na aba iPhone do PC.' : e.message); }
}

function micUi(on) {
  $('#micToggle').textContent = on ? 'Parar de ouvir' : 'Ouvir';
  $('#tileCam').textContent = on ? 'Ouvindo agora' : 'Ver e ouvir';
  if (!on) $('#micLevel').style.width = '0';
}

function micPlay(buf) {
  const pcm = new Int16Array(buf), f = new Float32Array(pcm.length);
  let sum = 0;
  for (let i = 0; i < pcm.length; i++) { f[i] = pcm[i] / 32768; sum += f[i] * f[i]; }
  $('#micLevel').style.width = Math.min(100, Math.sqrt(sum / (pcm.length || 1)) * 400 * mic.gain.gain.value) + '%';
  if (!pcm.length) return;
  const b = mic.ctx.createBuffer(1, f.length, mic.rate);
  b.copyToChannel(f, 0);
  const src = mic.ctx.createBufferSource();
  src.buffer = b;
  src.connect(mic.gain);
  const now = mic.ctx.currentTime;
  if (mic.next < now + 0.03 || mic.next > now + 0.8) mic.next = now + 0.15; // começo, engasgo ou atraso acumulado
  src.start(mic.next);
  mic.next += b.duration;
}

function micStart() {
  // o AudioContext tem de nascer/destravar dentro do toque (iOS), antes de qualquer await
  if (!mic.ctx) {
    mic.ctx = new (window.AudioContext || window.webkitAudioContext)();
    mic.gain = mic.ctx.createGain();
    mic.gain.gain.value = +$('#micGain').value;
    mic.gain.connect(mic.ctx.destination);
  }
  try { if (navigator.audioSession) navigator.audioSession.type = 'playback'; } catch {}
  mic.ctx.resume();
  const friendly = fn => async () => {
    try { await fn(); }
    catch (e) { if (/403/.test(e.message)) { micSay('Ligue "Microfone" na aba iPhone do PC.'); return; } throw e; }
  };
  run(friendly(async () => {
    const sel = $('#micDevice');
    const { ticket } = await api('/mic/open', { method: 'POST', json: { deviceId: sel.value, name: sel.selectedOptions[0]?.textContent } });
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/api/mic/stream?ticket=${encodeURIComponent(ticket)}`);
    ws.binaryType = 'arraybuffer';
    ws.onmessage = e => { if (typeof e.data === 'string') mic.rate = JSON.parse(e.data).rate; else micPlay(e.data); };
    ws.onclose = e => {
      if (e.reason === 'autorize no PC') micSay('Autorize o microfone na tela do PC (aviso do Windows) e toque Ouvir de novo.');
      else if (e.code === 1011) micSay('O microfone do PC não abriu (em uso por outro programa?).');
      if (mic.ws === ws) { mic.ws = null; micUi(false); }
    };
    mic.ws = ws;
    mic.next = 0;
    micSay('');
    micUi(true);
  }));
}

function micStop() {
  if (token) fetch('/api/mic/close', { method: 'POST', headers: { Authorization: `Bearer ${token}` } }).catch(() => {});
  if (mic.ws) { mic.ws.close(); mic.ws = null; }
  micUi(false);
}

$('#micToggle').onclick = () => mic.ws ? micStop() : micStart();
$('#micDevice').onchange = () => { if (mic.ws) { micStop(); micStart(); } };
$('#micGain').oninput = e => {
  $('#micGainVal').textContent = `${String(+e.target.value).replace('.', ',')}×`;
  if (mic.gain) mic.gain.gain.value = +e.target.value;
};
// App em segundo plano: o iOS corta o áudio; solta o microfone do PC também (privacidade).
document.addEventListener('visibilitychange', () => { if (document.hidden && mic.ws) micStop(); });

// --- Foto direto pro clipboard do PC ---
async function photoToClipboard(input) {
  const f = input.files[0];
  input.value = '';
  if (!f) return;
  const form = new FormData();
  form.append('file', f, f.name || 'foto.jpg');
  const r = await fetch('/api/clipboard/image', { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: form });
  if (r.status === 401) { showPair(true); throw new Error('Token inválido — pareie de novo'); }
  if (!r.ok) throw new Error(r.status === 413 ? 'Foto grande demais (máx. 40 MB)' : 'O PC não aceitou a foto');
}
['#photoCam', '#photoLib'].forEach(id => $(id).onchange = e => run(() => photoToClipboard(e.target), 'Foto no clipboard do PC'));

// --- Recebidos do PC ---
// O PC manda (Enviar para, arrastar pra ilha, aba Clipboard) e avisa por push; aqui o iPhone
// decide o destino: Compartilhar abre o menu do iOS (Arquivos, Fotos, AirDrop, apps).
const SHARE_MAX = 150 * 1024 * 1024; // acima disso o iOS não segura o arquivo na memória: baixa
const inSay = msg => { $('#inMsg').textContent = msg || ''; $('#inMsg').classList.toggle('hidden', !msg); };

async function inTicketUrl(id) {
  const { ticket } = await api('/outbox/ticket', { method: 'POST', json: { id } });
  return `/api/outbox/download?ticket=${encodeURIComponent(ticket)}`;
}

async function inShare(item) {
  const blob = await (await fetch(await inTicketUrl(item.id))).blob();
  const file = new File([blob], item.name, { type: blob.type });
  if (navigator.canShare && navigator.canShare({ files: [file] })) {
    try { await navigator.share({ files: [file] }); } catch (e) { if (e.name !== 'AbortError') throw e; }
  } else {
    location.href = URL.createObjectURL(blob);
  }
}

async function inLoad() {
  if (!token) return;
  try {
    const items = await api('/outbox');
    $('#tileInbox').textContent = items.length ? `${items.length} esperando` : 'Arquivos que o PC mandou';
    $('#inList').replaceChildren(...items.map(item => {
      const row = document.createElement('div');
      row.className = 'inrow';
      const isLink = item.text && /^https?:\/\/\S+$/i.test(item.text.trim());
      const info = item.file
        ? pfRow(pfKind(item.name), item.name, `${pfSize(item.size)} · ${pfDate(item.created)}`, () => {})
        : pfRow(isLink ? 'link' : 'doc', item.name, `Texto · ${pfDate(item.created)}`, () => {});
      const act = document.createElement('button');
      act.className = 'act primary';
      if (item.file && item.size <= SHARE_MAX) {
        act.textContent = 'Compartilhar';
        act.onclick = () => run(() => inShare(item));
      } else if (item.file) {
        act.textContent = 'Baixar';
        act.onclick = () => run(async () => { location.href = await inTicketUrl(item.id); });
      } else if (isLink) {
        act.textContent = 'Abrir';
        act.onclick = () => window.open(item.text.trim(), '_blank');
      } else {
        act.textContent = 'Copiar';
        act.onclick = () => run(() => navigator.clipboard.writeText(item.text), 'Copiado no iPhone');
      }
      const del = document.createElement('button');
      del.className = 'del';
      del.textContent = '✕';
      del.setAttribute('aria-label', 'Tirar da lista');
      del.onclick = () => run(async () => { await api(`/outbox/${encodeURIComponent(item.id)}`, { method: 'DELETE' }); inLoad(); });
      row.append(info, act, del);
      return row;
    }));
    inSay(items.length ? '' : 'Nada por aqui. No PC: botão direito no arquivo → Enviar para → iPhone (Orbit), ou arraste pra ilha.');
  } catch (e) { inSay(e.message); }
}

// --- Teclado do PC (escrever/ditar no iPhone → app em foco no PC) ---
// Compor e mandar: o ditado do iOS corrige palavras enquanto você fala, então nada vai letra a letra.
// "Digitar" manda o texto como teclas; "Colar" põe no clipboard do PC e dá Ctrl+V (texto longo,
// editores de código com autocompletar). O WebSocket só fica aberto com a página aberta.
const kb = { ws: null, connecting: null, ctrl: false };
const kbSay = msg => { $('#kbMsg').textContent = msg || ''; $('#kbMsg').classList.toggle('hidden', !msg); };

function kbConnect() {
  if (kb.ws && kb.ws.readyState === 1) return Promise.resolve(kb.ws);
  if (kb.connecting) return kb.connecting;
  kb.connecting = (async () => {
    const { ticket } = await api('/keyboard/ticket', { method: 'POST' });
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/api/keyboard/input?ticket=${encodeURIComponent(ticket)}`);
    await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = () => fail(new Error('Não conectou ao PC')); });
    ws.onclose = () => { if (kb.ws === ws) kb.ws = null; };
    kb.ws = ws;
    return ws;
  })().finally(() => { kb.connecting = null; });
  return kb.connecting;
}

function kbClose() { if (kb.ws) { kb.ws.close(); kb.ws = null; } }

async function kbSend(o) {
  try { (await kbConnect()).send(JSON.stringify(o)); }
  catch (e) {
    if (/403/.test(e.message)) { kbSay('Ligue "Teclado" na aba iPhone do PC.'); throw new Error('Teclado desligado no PC'); }
    throw e;
  }
  return true;
}

// Quebra de linha vira Enter de verdade; pedaços de até 400 caracteres sem partir emoji ao meio.
async function kbTypeText(text) {
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  for (let l = 0; l < lines.length; l++) {
    const chars = Array.from(lines[l]);
    for (let i = 0; i < chars.length; i += 400)
      if (!(await kbSend({ t: 'text', s: chars.slice(i, i + 400).join('') }))) return false;
    if (l < lines.length - 1 && !(await kbSend({ t: 'key', k: 'enter' }))) return false;
  }
  return true;
}

async function kbAfter() {
  if ($('#kbEnter').checked) await kbSend({ t: 'key', k: 'enter' });
  $('#kbText').value = '';
  kbSay('');
}

$('#kbType').onclick = () => run(async () => {
  const text = $('#kbText').value;
  if (!text) throw new Error('Escreva ou dite algo primeiro');
  if (await kbTypeText(text)) await kbAfter();
}, 'Digitado no PC');

$('#kbPaste').onclick = () => run(async () => {
  const text = $('#kbText').value;
  if (!text) throw new Error('Escreva ou dite algo primeiro');
  await api('/clipboard', { method: 'PUT', json: { text } });
  if (await kbSend({ t: 'key', k: 'v', mods: ['ctrl'] })) await kbAfter();
}, 'Colado no PC');

$('#kbCtrl').onclick = () => { kb.ctrl = !kb.ctrl; $('#kbCtrl').classList.toggle('on', kb.ctrl); };
document.querySelectorAll('[data-kkey]').forEach(b => b.onclick = () => run(async () => {
  await kbSend({ t: 'key', k: b.dataset.kkey, mods: kb.ctrl ? ['ctrl'] : [] });
  if (kb.ctrl) { kb.ctrl = false; $('#kbCtrl').classList.remove('on'); }
}));
document.addEventListener('visibilitychange', () => { if (document.hidden) kbClose(); });

// --- Páginas ---
// Uma página por feature, escolhida pelo #hash; hash vazio/desconhecido = início (atalhos).
let page = '';
function route() {
  const want = location.hash.slice(1).replace(/^energia$/, 'pc'); // atalho antigo do Energia
  const pages = [...document.querySelectorAll('.page')];
  const shown = pages.find(p => p.dataset.page === want) || pages.find(p => p.dataset.page === '');
  pages.forEach(p => p.classList.toggle('hidden', p !== shown));
  page = shown.dataset.page;
  $('#title').textContent = shown.dataset.title;
  $('#back').classList.toggle('hidden', !page);
  document.body.classList.toggle('home', !page);
  window.scrollTo(0, 0);
  if (page !== 'camera' && camStreaming) camStop(); // saiu da câmera: solta e apaga o LED
  if (page !== 'tela' && scr.live) scrStop();         // saiu da tela: encerra vídeo e controle
  if (page === 'tela' && !scr.loaded) scrLoad();
  if (page === 'camera' && !mic.ws) micLoad();
  if (page === 'recebidos') inLoad();
  if (page !== 'teclado') kbClose(); // fora da página, o PC não fica aceitando teclas
  if (page === 'notas' && token && !notesDirty) loadNotes().catch(e => toast(e.message));
  if (page === 'pcfiles' && token) pfLoad();
  if (page === 'link') renderLinks();
  refresh();
}
window.addEventListener('hashchange', route);

// Polling só com o app visível (nada roda em segundo plano no iPhone) e só do que está na tela.
function refresh() {
  ping();
  if (!token) return;
  if (page === '' || page === 'midia') refreshMedia();
  if (page === '' || page === 'pc') refreshStats();
  if (page === '') inLoad();
  if (page === 'camera' && !camLoaded) camLoadDevices();
}
let loop;
function startLoop() { clearInterval(loop); refresh(); loop = setInterval(refresh, 5000); }
document.addEventListener('visibilitychange', () => document.hidden ? clearInterval(loop) : startLoop());
route(); // já faz o 1º refresh
loop = setInterval(refresh, 5000);

if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});

// --- Avisos do PC (Web Push) ---
// iOS só entrega push pro app aberto pelo ícone da Tela de Início (16.4+) e só pede permissão
// dentro de um toque: por isso requestPermission é a 1ª coisa do clique, antes de qualquer await.
const pushSupported = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
const b64urlBytes = s => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));

async function pushRender() {
  const btn = $('#pushBtn'), off = $('#pushOff'), state = $('#pushState');
  if (!pushSupported()) {
    state.textContent = 'Abra pelo ícone da Tela de Início pra receber';
    btn.classList.add('hidden'); off.classList.add('hidden');
    return;
  }
  const reg = await navigator.serviceWorker.ready;
  const on = !!(await reg.pushManager.getSubscription()) && Notification.permission === 'granted';
  state.textContent = on ? 'Ligados neste iPhone' : 'Claude aguardando, Pomodoro…';
  btn.textContent = on ? 'Testar' : 'Ativar';
  btn.classList.remove('hidden');
  off.classList.toggle('hidden', !on);
}

$('#pushBtn').onclick = () => {
  if (!pushSupported()) return;
  const perm = Notification.requestPermission(); // no gesto, antes de qualquer await
  run(async () => {
    if ((await perm) !== 'granted') throw new Error('Permissão negada — libere em Ajustes › Notificações › Orbit');
    const reg = await navigator.serviceWorker.ready;
    let sub = await reg.pushManager.getSubscription();
    if (!sub) {
      const { publicKey } = await api('/push/key');
      sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64urlBytes(publicKey) });
    }
    await api('/push/subscribe', { method: 'POST', json: sub.toJSON() });
    await api('/push/test', { method: 'POST' });
    await pushRender();
  }, 'Aviso de teste enviado');
};

$('#pushOff').onclick = () => run(async () => {
  const sub = await (await navigator.serviceWorker.ready).pushManager.getSubscription();
  if (sub) {
    await api('/push/unsubscribe', { method: 'POST', json: { endpoint: sub.endpoint } }).catch(() => {});
    await sub.unsubscribe();
  }
  await pushRender();
}, 'Avisos desligados');

if (token) pushRender().catch(() => {});
