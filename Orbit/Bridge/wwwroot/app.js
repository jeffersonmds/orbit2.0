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
  const perm = r.status === 403 && [['/clipboard', 'Clipboard'], ['/notes', 'Notas'], ['/power', 'Energia']].find(([p]) => path.startsWith(p));
  if (perm) throw new Error(`Ligue "${perm[1]}" na aba Dispositivos do PC (403)`);
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
// Últimos links abertos (só neste aparelho, no localStorage): tocar reabre no PC.
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
      .then(() => toast('Copiado no aparelho'), () => blob.then(() => toast('Texto do PC abaixo'), e => toast(e.message)));
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
    else if (xhr.status === 507) {
      let n = 0;
      try { n = (JSON.parse(xhr.responseText).saved || []).length; } catch {}
      toast(n ? `Só ${n} de ${files.length} chegaram: o PC não conseguiu gravar o resto (disco cheio?)` : 'O PC não conseguiu gravar (disco cheio?)');
    }
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
// notesRev sobe a cada tecla: resposta que chega depois de você digitar não apaga nem marca como salvo.
let notesDirty = false, notesRev = 0, notesBase = null; // notesBase: versão do PC que está no campo
// Recarregar descarta o que havia até o clique; o que for digitado durante a requisição vence.
async function loadNotes() {
  const rev = notesRev;
  const { text, rev: base } = await api('/notes');
  if (rev !== notesRev) return; // digitou enquanto carregava: o que está no campo vence
  $('#notes').value = text;
  notesBase = base;
  notesDirty = false;
}
$('#notes').oninput = () => { notesDirty = true; notesRev++; };
$('#loadNotes').onclick = () => run(loadNotes, 'Notas do PC');
$('#saveNotes').onclick = () => run(async () => {
  const rev = notesRev;
  const text = $('#notes').value;
  let saved;
  try { saved = await api('/notes', { method: 'PUT', json: { text, rev: notesBase } }); }
  catch (e) {
    if (/507/.test(e.message)) throw new Error('O PC não conseguiu gravar as notas no disco');
    if (!/409/.test(e.message)) throw e;
    // mudaram no PC depois que você carregou: só sobrescreve se confirmar
    if (!confirm('As notas mudaram no PC desde que você abriu. Sobrescrever com as daqui?')) throw new Error('Nada salvo: toque em Recarregar pra ver as do PC');
    saved = await api('/notes', { method: 'PUT', json: { text } });
  }
  notesBase = saved?.rev ?? null;
  if (rev === notesRev) notesDirty = false; // editou durante o envio: continua pendente
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
// Geração de cada abertura: Parar/sair da página/segundo plano invalida a que ainda está no await,
// senão a resposta atrasada religaria câmera, tela ou microfone depois de você sair.
let camGen = 0;
const stillWanted = (gen, cur, want) => gen === cur && page === want && !document.hidden;
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
    if (r.status === 403) { camSay('Ligue "Câmera" na aba Dispositivos do PC.'); return; }
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
  const gen = ++camGen;
  try {
    const r = await fetch('/api/camera/open', {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({ deviceId: camDevice.value, name: camName(), quality: camQuality.value, fps: '24' }),
    });
    if (r.status === 403) { camSay('Ligue "Câmera" na aba Dispositivos do PC.'); return; }
    if (!r.ok) { camSay('Não consegui abrir a câmera.'); return; }
    const { ticket } = await r.json();
    if (!stillWanted(gen, camGen, 'camera')) return;
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
  camGen++;
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
const pf = { root: null, rootName: '', path: '', gen: 0 };
const PF_MAX = 2000; // = PcFiles.MaxEntries no PC
const pfJoin = (name, path = pf.path) => path ? `${path}/${name}` : name;

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

// Cada carga fotografa raiz/caminho: resposta atrasada (voltou rápido de pasta) é descartada, e cada
// linha age sobre a pasta em que foi listada, não sobre o estado global do momento do toque.
async function pfLoad() {
  const list = $('#pfList'), msg = $('#pfMsg');
  const gen = ++pf.gen, { root, rootName, path } = pf;
  msg.classList.add('hidden');
  try {
    if (!root) {
      const roots = await api('/pcfiles/roots');
      if (gen !== pf.gen) return;
      $('#pfCrumb').textContent = 'Pastas do PC';
      list.replaceChildren(...roots.map(r => pfRow('dir', r.name, '', () => { pf.root = r.id; pf.rootName = r.name; pf.path = ''; pfLoad(); })));
      return;
    }
    const entries = await api(`/pcfiles/list?root=${encodeURIComponent(root)}&path=${encodeURIComponent(path)}`);
    if (gen !== pf.gen) return;
    const parts = [rootName, ...path.split('/').filter(Boolean)];
    $('#pfCrumb').textContent = parts.join(' › ');
    const up = pfRow('up', parts.length > 1 ? parts[parts.length - 2] : 'Pastas do PC', '', () => {
      pf.root = root; pf.rootName = rootName;
      if (path) pf.path = path.split('/').slice(0, -1).join('/'); else pf.root = null;
      pfLoad();
    });
    list.replaceChildren(up, ...entries.map(e => e.dir
      ? pfRow('dir', e.name, pfDate(e.modified), () => { pf.root = root; pf.rootName = rootName; pf.path = pfJoin(e.name, path); pfLoad(); })
      : pfRow(pfKind(e.name), e.name, `${pfSize(e.size)} · ${pfDate(e.modified)}`, () => pfDownload(root, pfJoin(e.name, path)))));
    if (!entries.length) { msg.textContent = 'Pasta vazia.'; msg.classList.remove('hidden'); }
    else if (entries.length >= PF_MAX) { msg.textContent = `Pasta grande: mostrando só as pastas e os ${PF_MAX} itens mais recentes.`; msg.classList.remove('hidden'); }
  } catch (e) {
    if (gen !== pf.gen) return;
    list.replaceChildren();
    msg.textContent = /403/.test(e.message) ? 'Ligue "Arquivos" na aba Dispositivos do PC.' : e.message;
    msg.classList.remove('hidden');
  }
}

// Tíquete (com token) e depois navega pro download: o PC responde como anexo, então o iOS
// oferece baixar sem sair do app. Só o tíquete descartável vai na URL, nunca o token.
async function pfDownload(root, path) {
  try {
    const { ticket } = await api('/pcfiles/ticket', { method: 'POST', json: { root, path } });
    location.href = `/api/pcfiles/download?ticket=${encodeURIComponent(ticket)}`;
  } catch (e) { toast(e.message); }
}

// --- Tela do PC (ver + controlar) ---
// Vídeo: MJPEG por tíquete (como a câmera). Controle: WebSocket por tíquete, mensagens JSON pequenas.
// Modo toque: tocar clica ali, arrastar arrasta, segurar = botão direito, dois dedos rolam.
// Modo trackpad: o dedo move o cursor (relativo), toque clica, toque com dois dedos = botão direito.
const scr = { live: false, loaded: false, ws: null, mode: 'touch', mods: new Set(), gen: 0, lastTap: null, rx: 0, ry: 0,
  speed: +(localStorage.getItem('scrSpeed') || 2) };
const scrImg = $('#scrImg'), scrView = $('#scrView'), scrTouch = $('#scrTouch');
const TAKEN = 4001; // o PC fechou porque outro aparelho assumiu (reason = nome dele)
const scrSay = msg => { $('#scrMsg').textContent = msg || ''; $('#scrMsg').classList.toggle('hidden', !msg); };
// Escolhas da tela lembradas neste aparelho: monitor, qualidade, modo e zoom.
const scrPref = (k, v) => {
  try {
    if (v === undefined) return JSON.parse(localStorage.getItem('scr.' + k));
    localStorage.setItem('scr.' + k, JSON.stringify(v));
  } catch { return null; }
};
// O PC avisa quando o comando não chegou ao app (ex.: app como administrador).
const warnOf = data => { try { const o = JSON.parse(data); return o.t === 'warn' ? String(o.msg) : null; } catch { return null; } };
function scrWarn(data) {
  const m = warnOf(data);
  if (!m) return;
  scrSay(m);
  clearTimeout(scr.warnT);
  scr.warnT = setTimeout(() => { if ($('#scrMsg').textContent === m) scrSay(''); }, 5000);
}
const scrSend = o => { if (scr.ws && scr.ws.readyState === 1) scr.ws.send(JSON.stringify(o)); };

async function scrLoad() {
  if (!token || scr.live) return;
  try {
    const list = await api('/screen/list');
    $('#scrScreen').replaceChildren(...list.map(s => Object.assign(document.createElement('option'), { value: s.id, textContent: s.name })));
    const saved = scrPref('screen');
    if (list.some(s => s.id === saved)) $('#scrScreen').value = saved; // monitor que sumiu: fica o 1º
    scr.loaded = true;
    scrSay(list.length ? '' : 'Nenhum monitor encontrado.');
  } catch (e) { scrSay(/403/.test(e.message) ? 'Ligue "Tela" na aba Dispositivos do PC.' : e.message); }
}

// Outro aparelho assumiu a tela: encerra aqui e oferece "Retomar" (refaz o fluxo de abrir).
function scrTaken(name) {
  scrStop();
  scrSay(`O ${name} assumiu a tela`);
  $('#scrResume').classList.remove('hidden');
}

// O Safari não sinaliza o fim do MJPEG de forma confiável: pergunta ao PC quem está vendo.
async function scrCheckOwner() {
  try {
    const o = await api('/screen/owner');
    if (!scr.live) return;
    if (!o.name) { scrStop(); scrSay('Transmissão encerrada'); } // o PC não tem mais vídeo nosso
    else if (!o.mine) scrTaken(o.name);
    else scrFrozen(!!o.frozen);
  } catch (e) {
    if (/403/.test(e.message) && scr.live) { scrStop(); scrSay('Transmissão encerrada'); } // permissão desligada
    /* senão sem rede: o onerror da imagem cuida */
  }
}

// Captura parada no PC (bloqueado, aviso do UAC): a última imagem fica esmaecida, com aviso, e o toque
// não vira clique às cegas até voltar.
function scrFrozen(on) {
  if (on === !!scr.frozen) return;
  scr.frozen = on;
  scrImg.style.opacity = on ? '0.4' : '';
  scrTouch.style.pointerEvents = on ? 'none' : '';
  scrSay(on ? 'Imagem parada: o PC está bloqueado ou a captura não está disponível.' : '');
}

// Sem tocar por um tempo o iPhone apagaria a tela no meio do controle. A trava some sozinha se o app
// for pro segundo plano (e aí a tela remota também para).
let wakeLock = null, wakeWanted = false;
async function scrWake(on) {
  wakeWanted = on;
  try {
    if (on && !wakeLock && navigator.wakeLock) {
      const lock = await navigator.wakeLock.request('screen');
      if (!wakeWanted) { lock.release(); return; } // parou enquanto o pedido estava pendente
      wakeLock = lock;
      lock.addEventListener('release', () => { if (wakeLock === lock) wakeLock = null; });
    } else if (!on && wakeLock) { const lock = wakeLock; wakeLock = null; await lock.release(); }
  } catch { /* sem suporte ou negado: segue sem */ }
}

async function scrStart() {
  $('#scrResume').classList.add('hidden');
  const gen = ++scr.gen;
  try {
    const screen = $('#scrScreen').value;
    const { ticket } = await api('/screen/open', { method: 'POST', json: { screen, quality: $('#scrQuality').value } });
    if (!stillWanted(gen, scr.gen, 'tela')) return;
    scrImg.onerror = () => { scrSay('A transmissão caiu.'); scrStop(); };
    scrImg.src = `/api/screen/stream?ticket=${encodeURIComponent(ticket)}`;
    scr.live = true;
    scrWake(true);
    scrView.classList.add('live');
    $('#scrToggle').textContent = 'Parar';
    scrSay('');
    restoreView();
    if (landscape.matches) scrImmersive(true); // já deitado: vai direto pra tela cheia
    scrConnectControl(screen, gen);
    clearInterval(scr.watch);
    scr.watch = setInterval(scrCheckOwner, 5000);
  } catch (e) {
    if (/404/.test(e.message)) { scr.loaded = false; scrLoad(); scrSay('Esse monitor não está mais conectado — escolha de novo.'); return; }
    scrSay(/403/.test(e.message) ? 'Ligue "Tela" na aba Dispositivos do PC.' : e.message);
  }
}

// Controle é opcional: sem a permissão "Controle", fica só olhando.
async function scrConnectControl(screen, gen) {
  try {
    const { ticket } = await api('/remote/ticket', { method: 'POST', json: { screen } });
    if (!stillWanted(gen, scr.gen, 'tela')) return; // parou enquanto pedia o controle
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/api/remote/input?ticket=${encodeURIComponent(ticket)}`);
    ws.onopen = () => {
      $('#scrBar').classList.remove('hidden');
      if (scr.retrying) { scr.retrying = false; scrSay(''); }
    };
    ws.onmessage = e => scrWarn(e.data);
    ws.onclose = e => {
      const mine = scr.ws === ws; // false = fechei eu (Parar)
      $('#scrBar').classList.add('hidden');
      if (mine) scr.ws = null;
      if (mine && e.code === TAKEN) scrTaken(e.reason);
      // 1000 = o PC encerrou de propósito (Encerrar, permissão tirada, monitor mudou): não volta sozinho.
      // Queda de rede chega como 1006: a imagem segue, o controle volta sozinho.
      else if (mine && e.code !== 1000) scrRetryControl(screen, gen);
    };
    scr.ws = ws;
  } catch (e) {
    if (/403/.test(e.message)) scrSay('Só visualização — ligue "Controle" na aba Dispositivos pra usar mouse e teclado.');
    else scrRetryControl(screen, gen);
  }
}

function scrRetryControl(screen, gen) {
  scr.retrying = true;
  scrSay('Controle caiu — reconectando…');
  setTimeout(async () => {
    if (!scr.live || scr.ws || !stillWanted(gen, scr.gen, 'tela')) return;
    // "Encerrar" no PC durante uma falha de rede chega como queda (1006): só volta se a tela ainda é nossa.
    try {
      const o = await api('/screen/owner');
      if (!stillWanted(gen, scr.gen, 'tela') || scr.ws) return;
      if (!o.mine) { scrStop(); scrSay('A transmissão foi encerrada no PC.'); return; }
    } catch { scrRetryControl(screen, gen); return; } // ainda sem rede: tenta de novo
    scrConnectControl(screen, gen);
  }, 1500);
}

function scrStop() {
  scr.gen++;
  scrWake(false);
  scrCancelGesture();
  scrStopInertia();
  if (scr.twoTap) { clearTimeout(scr.twoTap); scr.twoTap = 0; }
  scrFrozen(false);
  clearInterval(scr.watch);
  scrImg.onerror = null;
  scrImg.src = BLANK; // aborta a <img>…
  if (token) fetch('/api/screen/close', { method: 'POST', headers: { Authorization: `Bearer ${token}` } }).catch(() => {}); // …e o Safari precisa do aviso
  if (scr.ws) { scr.ws.close(); scr.ws = null; }
  scr.live = false;
  if (scr.retrying) { scr.retrying = false; if ($('#scrMsg').textContent.startsWith('Controle caiu')) scrSay(''); }
  scrView.classList.remove('live');
  scrImmersive(false);
  $('#scrBar').classList.add('hidden');
  $('#scrToggle').textContent = 'Ver tela';
}

$('#scrResume').onclick = scrStart;
$('#scrToggle').onclick = () => scr.live ? scrStop() : scrStart();
const scrRestart = () => { if (scr.live) { scrStop(); scrStart(); } };
$('#scrScreen').onchange = () => { scrPref('screen', $('#scrScreen').value); scrRestart(); };
$('#scrQuality').onchange = () => { scrPref('quality', $('#scrQuality').value); scrRestart(); };
if (['leve', 'nitida', 'original'].includes(scrPref('quality'))) $('#scrQuality').value = scrPref('quality');

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

function applyView(save = true) {
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
  // Guarda 300 ms depois do último movimento: a pinça chama isto a cada quadro.
  clearTimeout(view.saveT);
  if (save && r.width && r.height) view.saveT = setTimeout(() => scrPref('view',
    { z: view.z, tx: view.tx, ty: view.ty, w: Math.round(r.width), h: Math.round(r.height), screen: $('#scrScreen').value }), 300);
}
function resetView(save = true) { view.z = 1; view.tx = 0; view.ty = 0; applyView(save); }
// Volta o zoom de antes ao abrir a tela: só no MESMO monitor e com a área do mesmo tamanho (girar o
// aparelho ou trocar de monitor muda o enquadramento: aí começa sem zoom).
function restoreView() {
  const v = scrPref('view'), r = scrView.getBoundingClientRect();
  const same = v && v.z > 1 && v.screen === $('#scrScreen').value
    && Math.abs(v.w - r.width) <= 2 && Math.abs(v.h - r.height) <= 2;
  if (!same) return resetView(false); // sem gravar: o zoom salvo pro outro tamanho continua guardado
  view.z = v.z; view.tx = v.tx; view.ty = v.ty;
  applyView();
}
$('#scrZoomReset').onclick = resetView;

// Marcas de toque: círculo rápido no clique (branco, vermelho = direito, verde = meio) e um círculo
// azul que segue o dedo enquanto um arraste segura o botão.
function scrFx(x, y, kind = '') {
  const r = scrView.getBoundingClientRect(), d = document.createElement('div');
  d.className = 'scr-fx ' + kind;
  d.style.left = `${x - r.left}px`; d.style.top = `${y - r.top}px`;
  scrView.append(d);
  setTimeout(() => d.remove(), 400);
}
let holdMark = null;
function scrHold(x, y) {
  const r = scrView.getBoundingClientRect();
  if (!holdMark) { holdMark = document.createElement('div'); holdMark.className = 'scr-hold'; scrView.append(holdMark); }
  holdMark.style.left = `${x - r.left}px`; holdMark.style.top = `${y - r.top}px`;
}
function scrHoldEnd() { holdMark?.remove(); holdMark = null; }

// Rolagem com dois dedos continua um pouco depois de soltar, freando (como no trackpad do notebook).
let inertia = 0;
function scrStopInertia() { cancelAnimationFrame(inertia); inertia = 0; }
function scrInertia(v) {
  let { dx, dy } = v;
  const step = () => {
    dx *= 0.9; dy *= 0.9;
    if (Math.abs(dx) + Math.abs(dy) < 1) { inertia = 0; return; }
    scrSend({ t: 'scroll', dy: Math.round(dy * 4), dx: Math.round(-dx * 4) });
    inertia = requestAnimationFrame(step);
  };
  inertia = requestAnimationFrame(step);
}

// --- Gestos ---
// Um dedo: toque/trackpad (ver acima). Dois dedos: pinça = zoom (ancorado nos dedos);
// arrastar = move a imagem se houver zoom, senão rola no PC. Toque rápido de dois dedos = botão direito.
let gesture = null;
// O Safari ignora user-scalable=no fora do modo app: barra a pinça da página toda (a imagem tem zoom próprio).
['gesturestart', 'gesturechange'].forEach(t => document.addEventListener(t, e => e.preventDefault(), { passive: false }));
document.addEventListener('touchmove', e => { if (e.touches.length > 1) e.preventDefault(); }, { passive: false });
const mid =ts => ({ x: (ts[0].clientX + ts[1].clientX) / 2, y: (ts[0].clientY + ts[1].clientY) / 2 });
const dist = ts => Math.hypot(ts[0].clientX - ts[1].clientX, ts[0].clientY - ts[1].clientY);

scrTouch.addEventListener('touchstart', e => {
  e.preventDefault();
  scrStopInertia();
  if (e.touches.length >= 3 && gesture?.two) { gesture.three = true; return; } // 3º dedo: vira clique do meio
  if (e.touches.length >= 2) {
    if (gesture?.drag) { scrSend({ t: 'up' }); scrHoldEnd(); }
    clearTimeout(gesture?.timer);
    const r = scrView.getBoundingClientRect(), m = mid(e.touches);
    gesture = {
      two: true, three: e.touches.length >= 3, kind: null, at: Date.now(), moved: false,
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
      if (!g.moved) { scrSend({ t: 'abs', ...g.p0 }); scrSend({ t: 'click', b: 'right' }); g.done = true; scrFx(g.x, g.y, 'right'); }
    }, 550);
  } else if (scr.mode === 'pad') {
    // trackpad: segurar parado = aperta o botão; mover o dedo arrasta (janela, seleção, arquivo); soltar solta
    g.timer = setTimeout(() => {
      if (!g.moved) { scrSend({ t: 'down' }); g.drag = true; scrHold(g.x, g.y); }
    }, 400);
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
        g.v = { dx, dy }; g.vAt = Date.now();
        g.last = m;
      }
    }
    return;
  }
  const t = e.touches[0];
  if (Math.hypot(t.clientX - g.x0, t.clientY - g.y0) > 8) { g.moved = true; clearTimeout(g.timer); }
  if (g.drag) scrHold(t.clientX, t.clientY);
  if (scr.mode === 'pad') {
    const dx = t.clientX - g.x, dy = t.clientY - g.y;
    // devagar = preciso; rápido = atravessa a tela (até 2,5× a mais)
    const k = scr.speed * Math.min(2.5, 1 + Math.hypot(dx, dy) / 15);
    scr.rx += dx * k; scr.ry += dy * k;
    const mx = Math.trunc(scr.rx), my = Math.trunc(scr.ry);
    if (mx || my) { scrSend({ t: 'move', dx: mx, dy: my }); scr.rx -= mx; scr.ry -= my; }
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
  if (g.two) {
    if (!g.moved && quick && g.three) { scrSend({ t: 'click', b: 'middle' }); scrFx(g.m0.x, g.m0.y, 'middle'); }
    else if (!g.moved && quick) {
      // dois toques de dois dedos = entra/sai da tela cheia; um só = botão direito (espera 300 ms pra saber)
      if (scr.twoTap) { clearTimeout(scr.twoTap); scr.twoTap = 0; scrImmersive(!document.body.classList.contains('immersive')); }
      else {
        const m = g.m0;
        scr.twoTap = setTimeout(() => { scr.twoTap = 0; scrSend({ t: 'click', b: 'right' }); scrFx(m.x, m.y, 'right'); }, 300);
      }
    }
    else if (g.kind === 'scroll' && g.v && Date.now() - g.vAt < 120) scrInertia(g.v); // parou antes de soltar: sem inércia
  }
  else if (g.drag) { scrSend({ t: 'up' }); scrHoldEnd(); }
  else if (!g.moved && !g.done) {
    if (scr.mode === 'pad') { scrSend({ t: 'click' }); scrFx(g.x, g.y); }
    else if (g.p0) {
      // 2º toque rápido perto do 1º: mesmo ponto no PC, senão o Windows não reconhece o clique duplo
      const now = Date.now(), last = scr.lastTap;
      const p = last && now - last.at < 450 && Math.hypot(g.x0 - last.x, g.y0 - last.y) < 30 ? last.p : g.p0;
      scr.lastTap = { at: now, x: g.x0, y: g.y0, p };
      scrSend({ t: 'abs', ...p }); scrSend({ t: 'click' });
      scrFx(g.x0, g.y0);
    }
  }
  gesture = null;
}, { passive: false });

// O iOS cancela o toque (notificação, gesto do sistema): sem isso o botão apertado no arraste ficava preso.
function scrCancelGesture() {
  const g = gesture;
  if (g) { clearTimeout(g.timer); if (g.drag) scrSend({ t: 'up' }); }
  scrHoldEnd();
  gesture = null;
}
scrTouch.addEventListener('touchcancel', scrCancelGesture);

// --- Tela cheia ---
// No iPhone não há Fullscreen API pra imagem; o app instalado já não tem barra do Safari, então
// basta a página cobrir tudo. Deitar o celular com a tela ligada entra sozinho; levantar sai.
const landscape = matchMedia('(orientation: landscape)');
function scrImmersive(on) {
  $('section[data-page="tela"]').classList.toggle('immersive', on);
  document.body.classList.toggle('immersive', on);
  $('#scrFs').setAttribute('aria-label', on ? 'Sair da tela cheia' : 'Tela cheia');
  requestAnimationFrame(restoreView); // a área mudou de tamanho: volta o zoom salvo pra esse tamanho, ou sem zoom
}
$('#scrFs').onclick = () => scrImmersive(!document.body.classList.contains('immersive'));
landscape.addEventListener('change', e => { if (scr.live) scrImmersive(e.matches); });

function scrSetMode(m) {
  scr.mode = m;
  $('#scrMode').textContent = m === 'touch' ? 'Toque' : 'Trackpad';
  $('#scrMode').classList.toggle('on', m === 'pad');
  $('#scrSpeed').classList.toggle('hidden', m !== 'pad');
}
$('#scrMode').onclick = () => { scrSetMode(scr.mode === 'touch' ? 'pad' : 'touch'); scrPref('mode', scr.mode); };
if (scrPref('mode') === 'pad') scrSetMode('pad');
$('#scrFsStop').onclick = () => scrStop();

// Velocidade do trackpad (fica salva no aparelho).
const SPEEDS = [1, 1.5, 2, 3, 4];
const speedLabel = () => `Vel ${String(scr.speed).replace('.', ',')}×`;
$('#scrSpeed').textContent = speedLabel();
$('#scrSpeed').onclick = () => {
  scr.speed = SPEEDS[(SPEEDS.indexOf(scr.speed) + 1) % SPEEDS.length] || 2;
  try { localStorage.setItem('scrSpeed', scr.speed); } catch {}
  $('#scrSpeed').textContent = speedLabel();
};

// Teclas soltas ↔ atalhos prontos.
$('#scrGroup').onclick = () => {
  const combos = $('#scrCombos').classList.toggle('hidden') === false;
  $('#scrKeys').classList.toggle('hidden', combos);
  $('#scrGroup').classList.toggle('on', combos);
};
document.querySelectorAll('[data-combo]').forEach(b => b.onclick = () => { // "ctrl+shift+t", ou só "volup"
  const parts = b.dataset.combo.split('+');
  scrSend({ t: 'key', k: parts.pop(), mods: parts });
});

// Colar do celular: o texto do clipboard daqui vai pro do PC e entra com Ctrl+V.
$('#scrPasteFrom').onclick = () => run(async () => {
  const text = await navigator.clipboard.readText(); // iOS mostra "Colar" pra confirmar
  if (!text) throw new Error('O clipboard do celular está vazio');
  await api('/clipboard', { method: 'PUT', json: { text } });
  scrSend({ t: 'key', k: 'v', mods: ['ctrl'] });
}, 'Colado no PC');

// Copiar pro celular: Ctrl+C no PC e o texto vem pro clipboard daqui. O Safari só aceita gravar
// dentro do toque: ClipboardItem com promessa segura o gesto enquanto o PC responde.
$('#scrCopyTo').onclick = () => {
  // Lê o clipboard antes, manda o Ctrl+C e espera ele mudar (até 2 s; igual = copiou o mesmo texto).
  const text = (async () => {
    const before = (await api('/clipboard'))?.text;
    scrSend({ t: 'key', k: 'c', mods: ['ctrl'] });
    let now = before;
    for (let i = 0; i < 8 && now === before; i++) {
      await new Promise(r => setTimeout(r, 250));
      now = (await api('/clipboard'))?.text;
    }
    if (!now) throw new Error('Nada copiado no PC');
    return now;
  })();
  const done = window.ClipboardItem && navigator.clipboard.write
    ? navigator.clipboard.write([new ClipboardItem({ 'text/plain': text.then(t => new Blob([t], { type: 'text/plain' })) })])
    : text.then(t => navigator.clipboard.writeText(t));
  done.then(() => toast('Copiado no celular'), e => toast(e.message || 'Não consegui copiar'));
};

// Ditar por cima da tela (o teclado solto manda letra a letra; o ditado precisa do texto inteiro).
$('#scrTypeBtn').onclick = () => {
  $('#scrType').classList.remove('hidden');
  $('#scrTypeText').focus();
};
$('#scrTypeClose').onclick = () => { $('#scrType').classList.add('hidden'); $('#scrTypeText').blur(); };
$('#scrTypeSend').onclick = () => {
  const f = $('#scrTypeText'), text = f.value;
  if (!text) return toast('Escreva ou dite algo primeiro');
  if (!scr.ws || scr.ws.readyState !== 1) return toast('Sem controle do PC agora');
  const msgs = [];
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  lines.forEach((line, i) => {
    for (const s of kbChunks(line)) msgs.push({ t: 'text', s });
    if (i < lines.length - 1) msgs.push({ t: 'key', k: 'enter' });
  });
  if ($('#scrTypeEnter').checked) msgs.push({ t: 'key', k: 'enter' });
  // O PC descarta acima de 300 mensagens/s: texto enorme fica no campo, não some pela metade.
  if (msgs.length > 200) return toast('Texto longo demais: use Teclado → Colar');
  msgs.forEach(scrSend);
  f.value = '';
  toast('Digitado no PC');
};

// Trocar de janela: lista as janelas do PC; tocar traz pra frente.
async function scrWindows() {
  const list = $('#scrWinsList');
  list.replaceChildren();
  $('#scrWins').classList.remove('hidden');
  try {
    const wins = await api('/remote/windows');
    if (!wins.length) { list.textContent = 'Nenhuma janela aberta.'; return; }
    list.replaceChildren(...wins.map(w => {
      const b = document.createElement('button');
      b.textContent = w.title;
      if (w.app) { const s = document.createElement('small'); s.textContent = w.app; b.append(s); }
      b.onclick = async () => {
        $('#scrWins').classList.add('hidden');
        try { await api('/remote/focus', { method: 'POST', json: { id: w.id } }); }
        catch (e) { scrSay(/404/.test(e.message) ? 'Essa janela já foi fechada.' : e.message); }
      };
      return b;
    }));
  } catch (e) { list.textContent = e.message; }
}
$('#scrWinsBtn').onclick = scrWindows;
$('#scrWinsClose').onclick = () => $('#scrWins').classList.add('hidden');

// Ctrl/Shift/Alt "grudentos": valem pra próxima tecla (Ctrl+C, Shift+Tab…) e soltam.
document.querySelectorAll('[data-mod]').forEach(b => b.onclick = () => {
  const m = b.dataset.mod;
  scr.mods.has(m) ? scr.mods.delete(m) : scr.mods.add(m);
  b.classList.toggle('on', scr.mods.has(m));
});
function scrKey(k) {
  scrSend({ t: 'key', k, mods: [...scr.mods] });
  scr.mods.clear();
  document.querySelectorAll('[data-mod]').forEach(b => b.classList.remove('on'));
}
document.querySelectorAll('[data-key]').forEach(b => b.onclick = () => scrKey(b.dataset.key));

// Teclado do iOS num campo invisível: beforeinput diz o que foi digitado. O campo guarda um espaço
// fixo: vazio, o iOS não manda nada no backspace. Enter num <input> não vira beforeinput: vem no keydown.
const scrIn = $('#scrInput');
const scrInReset = () => { scrIn.value = ' '; scrIn.setSelectionRange(1, 1); };
$('#scrKbd').onclick = () => { scrIn.focus(); scrInReset(); };
scrIn.addEventListener('input', scrInReset); // se algo escapar do beforeinput, volta ao espaço
scrIn.addEventListener('keydown', e => {
  if (e.key === 'Enter') { e.preventDefault(); scrKey('enter'); }
});
scrIn.addEventListener('beforeinput', e => {
  e.preventDefault();
  if (e.inputType === 'insertLineBreak' || e.inputType === 'insertParagraph') return; // já tratado no keydown
  if (e.inputType.startsWith('delete')) scrKey('backspace');
  else if (e.data) {
    if (scr.mods.size && /^[a-z0-9]$/i.test(e.data)) scrKey(e.data.toLowerCase());
    else scrSend({ t: 'text', s: e.data });
  }
});

// --- Microfone (independente da câmera) ---
// PCM 16 bits mono pelo WebSocket, tocado com Web Audio num buffer curto (~150 ms) que se
// re-sincroniza se atrasar. No iPhone a Web Audio obedece a chave de silêncio: audioSession
// 'playback' (iOS 17+) faz tocar como mídia, igual a um vídeo.
const mic = { ws: null, ctx: null, gain: null, next: 0, rate: 24000, gen: 0 };
const micSay = msg => { $('#micMsg').textContent = msg || ''; $('#micMsg').classList.toggle('hidden', !msg); };

async function micLoad() {
  if (!token) return;
  try {
    const list = await api('/mic/devices');
    $('#micDevice').replaceChildren(...list.map(d => Object.assign(document.createElement('option'), { value: d.id, textContent: d.name })));
    micSay(list.length ? '' : 'Nenhum microfone no PC.');
  } catch (e) { micSay(/403/.test(e.message) ? 'Ligue "Microfone" na aba Dispositivos do PC.' : e.message); }
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
    catch (e) { if (/403/.test(e.message)) { micSay('Ligue "Microfone" na aba Dispositivos do PC.'); return; } throw e; }
  };
  const gen = ++mic.gen;
  run(friendly(async () => {
    const sel = $('#micDevice');
    const { ticket } = await api('/mic/open', { method: 'POST', json: { deviceId: sel.value, name: sel.selectedOptions[0]?.textContent } });
    if (!stillWanted(gen, mic.gen, 'camera')) return;
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/api/mic/stream?ticket=${encodeURIComponent(ticket)}`);
    ws.binaryType = 'arraybuffer';
    ws.onmessage = e => { if (typeof e.data === 'string') mic.rate = JSON.parse(e.data).rate; else micPlay(e.data); };
    ws.onclose = e => {
      if (e.code === TAKEN) micSay(`O ${e.reason} assumiu o microfone`);
      else if (e.reason === 'autorize no PC') micSay('Autorize o microfone na tela do PC (aviso do Windows) e toque Ouvir de novo.');
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
  mic.gen++;
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
// App em segundo plano: câmera e tela também param (privacidade; o iOS congela a página de qualquer jeito).
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) return;
  if (mic.ws) micStop(); else mic.gen++;
  if (camStreaming) camStop(); else camGen++;
  if (scr.live) scrStop(); else scr.gen++;
});

// --- Foto direto pro clipboard do PC ---
async function photoToClipboard(input) {
  const f = input.files[0];
  input.value = '';
  if (!f) return;
  const form = new FormData();
  form.append('file', f, f.name || 'foto.jpg');
  const r = await fetch('/api/clipboard/image', { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: form });
  if (r.status === 401) { showPair(true); throw new Error('Token inválido — pareie de novo'); }
  if (r.status === 403) throw new Error('Ligue "Clipboard" na aba Dispositivos do PC (403)');
  if (!r.ok) throw new Error(r.status === 413 ? 'Foto grande demais (máx. 40 MB)' : 'O PC não aceitou a foto');
}
['#photoCam', '#photoLib'].forEach(id => $(id).onchange = e => run(() => photoToClipboard(e.target), 'Foto no clipboard do PC'));

// --- Recebidos do PC ---
// O PC manda (Enviar para, arrastar pra ilha, aba Clipboard) e avisa por push; aqui o aparelho
// decide o destino: Compartilhar abre o menu do iOS (Arquivos, Fotos, AirDrop, apps).
const SHARE_MAX = 150 * 1024 * 1024; // acima disso o iOS não segura o arquivo na memória: baixa
const inSay = msg => { $('#inMsg').textContent = msg || ''; $('#inMsg').classList.toggle('hidden', !msg); };

async function inTicketUrl(id) {
  const { ticket } = await api('/outbox/ticket', { method: 'POST', json: { id } });
  return `/api/outbox/download?ticket=${encodeURIComponent(ticket)}`;
}

async function inShare(item, btn) {
  const label = btn.textContent;
  btn.textContent = 'Baixando…';
  let blob;
  try { blob = await (await fetch(await inTicketUrl(item.id))).blob(); }
  finally { btn.textContent = label; }
  const file = new File([blob], item.name, { type: blob.type });
  if (navigator.canShare && navigator.canShare({ files: [file] })) {
    try { await navigator.share({ files: [file] }); }
    catch (e) {
      if (e.name === 'AbortError') return;
      if (e.name !== 'NotAllowedError') throw e;
      // Download demorou e o iOS não vale mais o toque como gesto: o arquivo já está aqui,
      // um segundo toque compartilha na hora (share direto no clique, sem await antes).
      const again = btn.onclick;
      btn.textContent = 'Pronto · compartilhar';
      btn.onclick = () => {
        btn.onclick = again; btn.textContent = label;
        navigator.share({ files: [file] }).catch(err => { if (err.name !== 'AbortError') toast('Não consegui compartilhar'); });
      };
    }
  } else {
    // Download como anexo (servidor manda attachment + sandbox). Nunca abrir o blob: ele herda a origem
    // do Orbit, e um .html recebido rodaria aqui dentro com acesso ao token.
    location.href = await inTicketUrl(item.id);
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
        act.onclick = () => run(() => inShare(item, act));
      } else if (item.file) {
        act.textContent = 'Baixar';
        act.onclick = () => run(async () => { location.href = await inTicketUrl(item.id); });
      } else if (isLink) {
        act.textContent = 'Abrir';
        act.onclick = () => window.open(item.text.trim(), '_blank');
      } else {
        act.textContent = 'Copiar';
        act.onclick = () => run(() => navigator.clipboard.writeText(item.text), 'Copiado no aparelho');
      }
      const del = document.createElement('button');
      del.className = 'del';
      del.textContent = '✕';
      del.setAttribute('aria-label', 'Tirar da lista');
      del.onclick = () => run(async () => { await api(`/outbox/${encodeURIComponent(item.id)}`, { method: 'DELETE' }); inLoad(); });
      row.append(info, act, del);
      return row;
    }));
    inSay(items.length ? '' : 'Nada por aqui. No PC: botão direito no arquivo → Enviar para → Dispositivo (Orbit), ou arraste pra ilha.');
  } catch (e) { inSay(e.message); }
}

// --- Teclado do PC (escrever/ditar no aparelho → app em foco no PC) ---
// Compor e mandar: o ditado do iOS corrige palavras enquanto você fala, então nada vai letra a letra.
// "Digitar" manda o texto como teclas; "Colar" põe no clipboard do PC e dá Ctrl+V (texto longo,
// editores de código com autocompletar). O WebSocket só fica aberto com a página aberta.
const kb = { ws: null, connecting: null, ctrl: false, gen: 0 };
const kbSay = msg => { $('#kbMsg').textContent = msg || ''; $('#kbMsg').classList.toggle('hidden', !msg); };

function kbConnect() {
  if (kb.ws && kb.ws.readyState === 1) return Promise.resolve(kb.ws);
  if (kb.connecting) return kb.connecting;
  const gen = kb.gen;
  const gone = () => gen !== kb.gen || page !== 'teclado' || document.hidden;
  kb.connecting = (async () => {
    const { ticket } = await api('/keyboard/ticket', { method: 'POST' });
    if (gone()) throw new Error('Saiu do teclado');
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/api/keyboard/input?ticket=${encodeURIComponent(ticket)}`);
    await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = () => fail(new Error('Não conectou ao PC')); });
    if (gone()) { ws.close(); throw new Error('Saiu do teclado'); }
    ws.onclose = e => { if (kb.ws === ws) kb.ws = null; if (e.code === TAKEN) kbSay(`O ${e.reason} assumiu o teclado`); };
    ws.onmessage = e => { const m = warnOf(e.data); if (m) kbSay(m); };
    kb.ws = ws;
    return ws;
  })().finally(() => { if (gen === kb.gen) kb.connecting = null; });
  return kb.connecting;
}

function kbClose() { kb.gen++; kb.connecting = null; if (kb.ws) { kb.ws.close(); kb.ws = null; } }

async function kbSend(o) {
  try { (await kbConnect()).send(JSON.stringify(o)); }
  catch (e) {
    if (/403/.test(e.message)) { kbSay('Ligue "Teclado" na aba Dispositivos do PC.'); throw new Error('Teclado desligado no PC'); }
    throw e;
  }
  return true;
}

// Pedaços de até 400 unidades UTF-16 (o limite do PC conta assim: emoji vale 2) sem partir emoji ao meio.
function kbChunks(line, max = 400) {
  const out = [];
  let cur = '';
  for (const c of line) {
    if (cur.length + c.length > max) { out.push(cur); cur = ''; }
    cur += c;
  }
  if (cur) out.push(cur);
  return out;
}

// Quebra de linha vira Enter de verdade. O PC descarta acima de 300 mensagens/s: texto longo vai
// em lotes de 50 com uma pausa, senão linhas sumiriam caladas.
async function kbTypeText(text) {
  const msgs = [];
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  lines.forEach((line, l) => {
    for (const s of kbChunks(line)) msgs.push({ t: 'text', s });
    if (l < lines.length - 1) msgs.push({ t: 'key', k: 'enter' });
  });
  const gen = kb.gen;
  for (let i = 0; i < msgs.length; i++) {
    if (i > 0 && i % 50 === 0) await new Promise(r => setTimeout(r, 400));
    if (gen !== kb.gen) return false; // saiu do teclado no meio: o resto não vai (e o texto fica no campo)
    if (!(await kbSend(msgs[i]))) return false;
  }
  return true;
}

// Limpa só se o campo ainda tem o que foi mandado: o que você ditou durante o envio fica.
async function kbAfter(text) {
  if ($('#kbEnter').checked) await kbSend({ t: 'key', k: 'enter' });
  const f = $('#kbText');
  if (f.value === text) f.value = '';
  else if (f.value.startsWith(text)) f.value = f.value.slice(text.length).replace(/^\s+/, '');
  kbSay('');
}

// Um envio por vez: dois toques seguidos não intercalam as letras nem mandam o texto duas vezes.
const kbOnce = fn => () => {
  if (kb.busy) return;
  kb.busy = true;
  return fn().finally(() => { kb.busy = false; });
};

$('#kbType').onclick = kbOnce(() => run(async () => {
  const text = $('#kbText').value;
  if (!text) throw new Error('Escreva ou dite algo primeiro');
  if (await kbTypeText(text)) await kbAfter(text);
}, 'Digitado no PC'));

$('#kbPaste').onclick = kbOnce(() => run(async () => {
  const text = $('#kbText').value;
  if (!text) throw new Error('Escreva ou dite algo primeiro');
  await api('/clipboard', { method: 'PUT', json: { text } });
  if (await kbSend({ t: 'key', k: 'v', mods: ['ctrl'] })) await kbAfter(text);
}, 'Colado no PC'));

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
  // Saiu da página: encerra o que está ligado e invalida o que ainda está abrindo (voltar rápido
  // não pode aceitar a resposta atrasada da abertura antiga).
  if (page !== 'camera') { if (camStreaming) camStop(); else camGen++; if (!mic.ws) mic.gen++; }
  if (page !== 'tela') { if (scr.live) scrStop(); else scr.gen++; }
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

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('sw.js').catch(() => {});
  // toque numa notificação com o app aberto: troca de página sem recarregar
  navigator.serviceWorker.addEventListener('message', e => {
    if (!e.data || typeof e.data.go !== 'string') return;
    if (location.hash !== e.data.go) location.hash = e.data.go;
    else if (page === 'recebidos') inLoad(); // já está lá: mostra o arquivo novo
  });
}

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
  state.textContent = on ? 'Ligados neste aparelho' : 'Claude aguardando, Pomodoro…';
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
