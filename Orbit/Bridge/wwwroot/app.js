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
  if (page === 'camera' && !camLoaded) camLoadDevices();
}
let loop;
function startLoop() { clearInterval(loop); refresh(); loop = setInterval(refresh, 5000); }
document.addEventListener('visibilitychange', () => document.hidden ? clearInterval(loop) : startLoop());
route(); // já faz o 1º refresh
loop = setInterval(refresh, 5000);

if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});
