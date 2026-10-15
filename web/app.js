// Ponte no navegador (feito para o iPhone, serve em qualquer aparelho): a tela inicial, com o ID, os
// computadores salvos (e a bolinha de online) e os recentes, e a sessão, com a imagem, o toque e o teclado.
// O protocolo e a criptografia de ponta a ponta ficam em ponte.js; aqui é só a parte que se vê.
import * as P from './ponte.js';

const VERSION = '1.8.6';
const { MouseAction } = P;

const $ = (id) => document.getElementById(id);
const clamp = (value, low, high) => Math.min(Math.max(value, low), high);
const pad = (n) => String(n).padStart(2, '0');

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

// O servidor de retransmissão atende no mesmo endereço da página, em /ponte/ws. Só a página aberta nesta
// própria máquina (testes) pode apontar outro, por ?relay=.
const RELAY_URL = (() => {
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
  const asked = new URLSearchParams(location.search).get('relay');
  if (local && asked && /^wss?:\/\//.test(asked)) return asked;
  const url = new URL('ws', location.href);
  url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  url.search = '';
  url.hash = '';
  return url.href;
})();

// ---------- o que fica guardado neste aparelho ----------

const DEFAULTS = { nome: '', salvos: [], recentes: [], identidades: {}, qualidade: 60, touchpad: false, dicaInstalar: true };

const state = (() => {
  let stored = null;
  try { stored = JSON.parse(localStorage.getItem('ponte') || 'null'); } catch { stored = null; }
  const s = { ...DEFAULTS, ...(stored && typeof stored === 'object' ? stored : {}) };
  if (!Array.isArray(s.salvos)) s.salvos = [];
  if (!Array.isArray(s.recentes)) s.recentes = [];
  if (!s.identidades || typeof s.identidades !== 'object') s.identidades = {};
  return s;
})();

function save() {
  try { localStorage.setItem('ponte', JSON.stringify(state)); } catch { /* janela privada: vale só nesta visita */ }
}

const isIos = /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
const standalone = navigator.standalone === true || matchMedia('(display-mode: standalone)').matches;

function defaultName() {
  const ua = navigator.userAgent;
  if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'iPad';
  if (/iPhone/.test(ua)) return 'iPhone';
  if (/Android/.test(ua)) return 'Android';
  return 'Navegador';
}

/** Nome com que este aparelho aparece no pedido de conexão e na lista de autorizados do computador. */
const deviceName = () => String(state.nome || '').trim().slice(0, 60) || defaultName();

// A identidade deste aparelho: um par ECDSA P-256 guardado no IndexedDB, com a parte privada não exportável
// (nem esta página consegue ler; só usar para assinar). É com ela que um computador reconhece o aparelho que
// ele autorizou. Sem IndexedDB (janela privada), vale uma identidade só desta visita.
let identityPromise = null;
let identityKept = true;

function identity() {
  identityPromise ??= loadIdentity();
  return identityPromise;
}

async function loadIdentity() {
  try {
    const db = await new Promise((resolve, reject) => {
      const request = indexedDB.open('ponte', 1);
      request.onupgradeneeded = () => request.result.createObjectStore('chaves');
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    const get = (key) => new Promise((resolve, reject) => {
      const q = db.transaction('chaves').objectStore('chaves').get(key);
      q.onsuccess = () => resolve(q.result);
      q.onerror = () => reject(q.error);
    });
    const put = (key, value) => new Promise((resolve, reject) => {
      const t = db.transaction('chaves', 'readwrite');
      t.objectStore('chaves').put(value, key);
      t.oncomplete = () => resolve();
      t.onerror = () => reject(t.error);
    });
    let pair = await get('identidade');
    if (!pair) {
      pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign', 'verify']);
      await put('identidade', pair);
    }
    return await P.identityFromPair(pair);
  } catch {
    identityKept = false;
    return P.newIdentity();
  }
}

// ---------- janelas ----------

let closeOpenDialog = null;

/**
 * Mostra uma janela e espera a escolha. spec: { title, message, note, fields, options, buttons, validate }.
 * fields: { id, label, value, type, inputmode, disabled, checkbox, multiline }; options e buttons: { label, value, kind, checked }.
 * Devolve { value, fields }; value é null quando a janela foi fechada sem escolha.
 */
function ask(spec) {
  closeOpenDialog?.(null);
  const dialog = $('dialogo');
  return new Promise((resolve) => {
    const form = el('form', 'janela');
    if (spec.title) form.append(el('h3', '', spec.title));
    if (spec.message) form.append(el('p', '', spec.message));
    const inputs = {};
    for (const f of spec.fields || []) {
      if (f.checkbox) {
        const label = el('label', 'marcar');
        const input = el('input');
        input.type = 'checkbox';
        input.checked = !!f.value;
        label.append(input, el('span', '', f.label));
        form.append(label);
        inputs[f.id] = input;
        continue;
      }
      const input = el(f.multiline ? 'textarea' : 'input', 'campo');
      if (!f.multiline) input.type = f.type || 'text';
      else input.rows = 4;
      input.id = `campo-${f.id}`;
      input.value = f.value ?? '';
      input.disabled = !!f.disabled;
      input.autocomplete = f.type === 'password' ? 'current-password' : 'off';
      input.setAttribute('autocorrect', 'off');
      input.setAttribute('autocapitalize', f.capitalize || 'off');
      if (f.inputmode) input.inputMode = f.inputmode;
      if (f.multiline) input.style.height = 'auto';
      const label = el('label', 'fraco', f.label);
      label.htmlFor = input.id;
      label.style.cssText = 'display:block;margin-top:12px;font-size:14px';
      form.append(label, input);
      inputs[f.id] = input;
    }
    const error = el('p', 'erro');
    error.hidden = true;
    form.append(error);
    if (spec.note) form.append(el('p', 'nota', spec.note));
    if (spec.options) {
      const list = el('div', 'opcoes');
      for (const o of spec.options) {
        const b = el('button', `${o.kind || ''}${o.checked ? ' marcado' : ''}`, `${o.checked ? '✓ ' : ''}${o.label}`);
        b.type = 'button';
        b.onclick = () => finish(o.value);
        list.append(b);
      }
      form.append(list);
    }
    const row = el('div', 'botoes');
    for (const b of spec.buttons || []) {
      const button = el('button', b.kind || '', b.label);
      button.type = 'button';
      button.onclick = () => (b.submit ? submit() : finish(b.value));
      row.append(button);
    }
    form.append(row);
    form.onsubmit = (event) => {
      event.preventDefault();
      submit();
    };

    function values() {
      const out = {};
      for (const [key, input] of Object.entries(inputs)) out[key] = input.type === 'checkbox' ? input.checked : input.value;
      return out;
    }
    function submit() {
      const chosen = (spec.buttons || []).find((b) => b.submit);
      if (!chosen) return;
      const problem = spec.validate?.(values()) || '';
      if (problem) {
        error.textContent = problem;
        error.hidden = false;
        return;
      }
      finish(chosen.value);
    }
    let done = false;
    function finish(value) {
      if (done) return;
      done = true;
      closeOpenDialog = null;
      const result = { value, fields: values() };
      if (dialog.open) dialog.close();
      resolve(result);
    }
    closeOpenDialog = finish;
    dialog.replaceChildren(form);
    dialog.oncancel = (event) => {
      event.preventDefault();
      finish(null);
    };
    dialog.showModal();
    const first = Object.values(inputs).find((i) => !i.disabled && i.type !== 'checkbox');
    if (first) first.focus();
  });
}

const message = (title, text) => ask({ title, message: text, buttons: [{ label: 'OK', value: true, kind: 'forte', submit: true }] });

// ---------- tela inicial ----------

const MONITOR = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true">' +
  '<rect x="3" y="4" width="18" height="12" rx="1.5"/><path d="M12 16v4M8 20h8"/></svg>';

/** Quem estava online na última consulta, por chave. O que não deu para saber fica de fora (sem bolinha). */
const online = new Map();

const titleOf = (c) => c.name || c.address;

function when(time) {
  const d = new Date(time);
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function row(r) {
  const li = el('li', 'item');
  const open = el('button', 'item-abrir');
  open.type = 'button';
  open.onclick = r.open;
  const presence = r.online === undefined ? '' : r.online ? ', online agora' : ', offline';
  open.setAttribute('aria-label', `Conectar a ${r.name}${presence}, ${r.address}, ${r.detail}`);
  const thumbnail = el('span', 'miniatura');
  thumbnail.innerHTML = MONITOR;
  const texts = el('span', 'item-textos');
  const name = el('span', 'item-nome');
  name.append(el('span', '', r.name));
  // A bolinha vai logo depois do nome. Verde: o Ponte está aberto lá agora. Vermelha: não está.
  if (r.online !== undefined) {
    const dot = el('span', `bolinha ${r.online ? 'on' : 'off'}`);
    dot.setAttribute('role', 'img');
    dot.setAttribute('aria-label', r.online ? 'online agora' : 'offline');
    dot.title = r.online ? 'Online agora: o Ponte está aberto lá.' : 'Offline: o Ponte não está aberto lá (desligado, sem internet ou fechado).';
    name.append(dot);
  }
  texts.append(name, el('span', 'item-endereco', r.address), el('span', `item-estado${r.highlight ? ' destaque' : ''}`, r.detail));
  open.append(thumbnail, texts);
  const more = el('button', 'item-mais', '⋯');
  more.type = 'button';
  more.setAttribute('aria-label', `Opções de ${r.name}`);
  more.onclick = r.menu;
  li.append(open, more);
  return li;
}

function renderHome() {
  const saved = [...state.salvos].sort((a, b) => titleOf(a).localeCompare(titleOf(b), 'pt-BR', { sensitivity: 'base' }));
  $('salvos').replaceChildren(...saved.map((c) => row({
    name: titleOf(c), address: c.address, detail: c.paired ? 'entra direto' : 'pede aprovação', highlight: c.paired,
    online: online.get(c.key), open: () => connectTo(c.address), menu: () => savedMenu(c),
  })));
  $('salvos-vazio').hidden = saved.length > 0;
  // Um computador salvo não precisa aparecer de novo entre os recentes.
  const keys = new Set(saved.map((c) => c.key));
  const recent = state.recentes.filter((r) => {
    const target = P.parseTarget(r.address);
    return target && !keys.has(target.key);
  });
  $('recentes').replaceChildren(...recent.map((r) => row({
    name: r.name || r.address, address: r.address, detail: when(r.time), open: () => connectTo(r.address), menu: () => recentMenu(r),
  })));
  $('recentes-titulo').hidden = recent.length === 0;
}

async function showVersion() {
  const id = await P.idFromSpki((await identity()).spki);
  $('versao').textContent = `versão ${VERSION} · este aparelho: ${P.formatId(id)}${identityKept ? '' : ' (só nesta janela)'}`;
}

function showAddressError(text) {
  const p = $('endereco-erro');
  p.textContent = text;
  p.hidden = !text;
}

async function savedMenu(c) {
  const { value } = await ask({
    title: titleOf(c),
    options: [{ label: 'Conectar', value: 'conectar' }, { label: 'Editar', value: 'editar' }, { label: 'Remover', value: 'remover', kind: 'perigo' }],
    buttons: [{ label: 'Cancelar', value: null }],
  });
  if (value === 'conectar') connectTo(c.address);
  else if (value === 'editar') editComputer(c.address, c.name, c.autoAccess, false);
  else if (value === 'remover') removeSaved(c);
}

async function recentMenu(r) {
  const { value } = await ask({
    title: r.name || r.address,
    options: [{ label: 'Conectar', value: 'conectar' }, { label: 'Salvar', value: 'salvar' }, { label: 'Remover', value: 'remover', kind: 'perigo' }],
    buttons: [{ label: 'Cancelar', value: null }],
  });
  if (value === 'conectar') connectTo(r.address);
  else if (value === 'salvar') editComputer(r.address, r.name, true, true);
  else if (value === 'remover') {
    state.recentes = state.recentes.filter((x) => x !== r);
    save();
    renderHome();
  }
}

async function editComputer(address, name, auto, isNew) {
  const { value, fields } = await ask({
    title: isNew ? 'Salvar computador' : 'Editar computador',
    fields: [
      { id: 'name', label: 'Nome (Escritório, Casa, Loja...)', value: name || '', capitalize: 'words' },
      { id: 'address', label: 'ID do computador (9 dígitos)', value: address || '', disabled: !isNew, inputmode: 'numeric' },
      { id: 'auto', label: 'Entrar sem aprovação nas próximas vezes', value: auto, checkbox: true },
    ],
    note: 'O computador precisa autorizar este aparelho uma vez: alguém marca "Sempre permitir este computador" no pedido ' +
      'que aparece lá, ou você entra com a senha de acesso dele. Depois disso a conexão entra direto.',
    validate: (f) => (P.parseTarget(f.address) ? '' : 'Informe o ID de 9 dígitos do computador. Pelo navegador não dá para usar o IP.'),
    buttons: [{ label: 'Cancelar', value: null }, { label: 'Salvar', value: 'salvar', kind: 'forte', submit: true }],
  });
  if (value !== 'salvar') return;
  const target = P.parseTarget(fields.address);
  const existing = state.salvos.find((c) => c.key === target.key);
  if (existing) {
    existing.name = fields.name.trim().slice(0, 100);
    existing.autoAccess = fields.auto;
  } else {
    state.salvos.push({ key: target.key, address: target.shown, name: fields.name.trim().slice(0, 100), autoAccess: fields.auto, paired: false });
  }
  save();
  renderHome();
  checkPresence();
}

async function removeSaved(c) {
  const note = c.paired ? '\n\nA autorização continua valendo no computador até ser removida lá, em "Computadores autorizados".' : '';
  const { value } = await ask({
    message: `Remover ${titleOf(c)} dos computadores salvos?${note}`,
    buttons: [{ label: 'Cancelar', value: null }, { label: 'Remover', value: 'remover', kind: 'perigo' }],
  });
  if (value !== 'remover') return;
  state.salvos = state.salvos.filter((x) => x.key !== c.key);
  save();
  renderHome();
}

async function showMenu() {
  const { value } = await ask({
    options: [{ label: 'Configurações', value: 'config' }, { label: 'Como usar no iPhone', value: 'iphone' }, { label: 'Sobre', value: 'sobre' }],
    buttons: [{ label: 'Fechar', value: null }],
  });
  if (value === 'config') {
    const { value: ok, fields } = await ask({
      title: 'Configurações',
      fields: [{ id: 'nome', label: 'Nome com que este aparelho aparece no computador', value: deviceName(), capitalize: 'words' }],
      buttons: [{ label: 'Cancelar', value: null }, { label: 'Salvar', value: 'salvar', kind: 'forte', submit: true }],
    });
    if (ok === 'salvar') {
      state.nome = fields.nome.trim().slice(0, 60);
      save();
    }
  } else if (value === 'iphone') {
    await message('Como usar no iPhone',
      '1. Abra esta página no Safari.\n2. Toque em Compartilhar (o quadrado com a seta para cima).\n' +
      '3. Escolha "Adicionar à Tela de Início".\n\nO Ponte aparece entre os seus aplicativos e abre em tela cheia, sem a barra do Safari.\n\n' +
      'Na sessão: toque para clicar, segure e solte para o botão direito, segure e arraste (ou toque duas vezes e arraste) ' +
      'para arrastar, dois dedos para rolar e a pinça para ampliar. "Touchpad" troca para um ponteiro que você move com o dedo.');
  } else if (value === 'sobre') {
    const id = P.formatId(await P.idFromSpki((await identity()).spki));
    await message('Ponte no navegador',
      `Versão ${VERSION}\n\nEndereço deste aparelho: ${id}\nÉ com esse número e o nome "${deviceName()}" que ele aparece na lista ` +
      'de computadores autorizados do Ponte no computador.\n\nA conexão é cifrada de ponta a ponta entre este aparelho e o ' +
      'computador: o servidor só repassa os dados, sem conseguir ler. Este aparelho só acessa; ele não pode ser acessado.');
  }
}

// ---------- bolinha de online ----------

let presenceBusy = false;

/** Uma linha para o servidor de retransmissão pelo WebSocket, e a resposta dele. */
function relayLine(line) {
  return new Promise((resolve, reject) => {
    let ws;
    try { ws = new WebSocket(RELAY_URL); } catch (error) { reject(error); return; }
    ws.binaryType = 'arraybuffer';
    let answer = '';
    const timer = setTimeout(() => ws.close(), 10000);
    ws.onopen = () => ws.send(line);
    ws.onmessage = (event) => { answer += typeof event.data === 'string' ? event.data : new TextDecoder().decode(event.data); };
    ws.onerror = () => {};
    ws.onclose = () => {
      clearTimeout(timer);
      if (answer) resolve(answer);
      else reject(new Error('sem resposta'));
    };
  });
}

// Confere ao abrir, quando a lista muda e a cada 30 s enquanto a tela inicial está à vista.
async function checkPresence() {
  if (presenceBusy || document.hidden || current) return;
  const ids = [...new Set(state.salvos.map((c) => P.parseTarget(c.address)?.id).filter(Boolean))].slice(0, 100);
  if (ids.length === 0) return;
  presenceBusy = true;
  try {
    const reply = /^STATUS ([01]+)\s*$/.exec(await relayLine(`PONTE STATUS ${ids.join(',')}\n`));
    if (reply && reply[1].length === ids.length) {
      online.clear();
      ids.forEach((id, i) => online.set(`id:${id}`, reply[1][i] === '1'));
      if (!current) renderHome();
    }
  } catch {
    // Servidor fora do ar: a lista fica sem bolinha, em vez de tudo vermelho.
  } finally {
    presenceBusy = false;
  }
}

// ---------- conexão ----------

/** A sessão em andamento: { session, tela, keyboard, screen, clipboard }, ou null. */
let current = null;
let connecting = null;

function setStatus(text) {
  const p = $('sessao-estado');
  p.textContent = text;
  p.hidden = !text;
}

let toastTimer = 0;
function toast(text) {
  const p = $('sessao-aviso');
  p.textContent = text;
  p.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { p.hidden = true; }, 5000);
}

// Com o teclado aberto, o iPhone encolhe só a área visível: a sessão acompanha, para a barra ficar à vista.
function fitViewport() {
  const view = window.visualViewport;
  const section = $('sessao');
  if (!view || section.hidden) return;
  section.style.height = `${view.height}px`;
  section.style.top = `${view.offsetTop}px`;
}

let wakeLock = null;

function openSessionScreen(status) {
  $('inicio').hidden = true;
  $('sessao').hidden = false;
  document.body.classList.add('em-sessao');
  $('teclas').hidden = true;
  $('b-teclas').classList.remove('ativo');
  $('b-modo').textContent = state.touchpad ? 'Touchpad' : 'Toque';
  setStatus(status);
  fitViewport();
  // Tela acesa durante a sessão (onde o navegador deixa).
  navigator.wakeLock?.request('screen').then((lock) => { wakeLock = lock; }).catch(() => {});
}

function closeSessionScreen() {
  $('sessao').hidden = true;
  $('inicio').hidden = false;
  document.body.classList.remove('em-sessao');
  $('digitar').blur();
  wakeLock?.release().catch(() => {});
  wakeLock = null;
  renderHome();
  checkPresence();
}

async function askPassword(shown) {
  const { value, fields } = await ask({
    title: `Senha de ${shown}`,
    message: 'Este computador pede a senha de acesso. Sem ela, dá para pedir que alguém aceite a conexão lá.',
    fields: [{ id: 'password', label: 'Senha de acesso', type: 'password' }],
    buttons: [
      { label: 'Cancelar', value: 'cancelar' },
      { label: 'Pedir permissão', value: 'pedir' },
      { label: 'Conectar', value: 'conectar', kind: 'forte', submit: true },
    ],
  });
  if (value === 'conectar' && fields.password) {
    setStatus('Conferindo a senha...');
    return fields.password;
  }
  if (value === 'conectar' || value === 'pedir') {
    setStatus('Esperando alguém aceitar o pedido no computador...');
    return '';
  }
  return null;
}

/** O código de uma transmissão de tela, se o texto for o link dela (deste servidor) ou só o código. */
function streamToken(text) {
  const trimmed = String(text).trim();
  if (/^[A-Za-z0-9]{24}$/.test(trimmed) && /[A-Za-z]/.test(trimmed)) return trimmed;
  try {
    const url = new URL(trimmed);
    const token = decodeURIComponent(url.hash.slice(1));
    if (url.host === location.host && /\/ponte\/assistir\/?$/.test(url.pathname) && /^[A-Za-z0-9]{24}$/.test(token)) return token;
  } catch { /* não é link */ }
  return null;
}

async function connectTo(text) {
  // Link (ou código) de uma transmissão de tela: abre a página de assistir.
  const stream = streamToken(text);
  if (stream) {
    location.href = new URL(`assistir/#${stream}`, location.href).href;
    return;
  }
  const target = P.parseTarget(text);
  if (!target) {
    showAddressError('Use o ID de 9 dígitos do computador. Pelo navegador não dá para conectar pelo IP.');
    return;
  }
  if (current || connecting) return;
  showAddressError('');
  const saved = state.salvos.find((c) => c.key === target.key);
  openSessionScreen(`Conectando a ${saved?.name || target.shown}...\nSe for a primeira vez, alguém precisa aceitar o pedido no computador.`);
  const attempt = { cancelled: false };
  connecting = attempt;
  let session;
  try {
    session = await P.connect(target.shown, {
      relayUrl: RELAY_URL, deviceName: deviceName(), userName: 'Navegador', version: VERSION,
      identity: await identity(), knownFingerprint: state.identidades[target.key] || null, remember: !!saved?.autoAccess,
      askPassword: () => askPassword(target.shown),
      confirmNewIdentity: async () => (await ask({
        title: 'A identidade mudou',
        message: `O computador ${target.shown} se apresentou com uma identidade diferente da última vez. Isso acontece quando o ` +
          'Ponte foi reinstalado lá, mas também pode ser alguém se passando por ele. Conectar mesmo assim?',
        buttons: [{ label: 'Cancelar', value: false }, { label: 'Conectar', value: true, kind: 'forte', submit: true }],
      })).value === true,
    });
  } catch (error) {
    connecting = null;
    if (attempt.cancelled) return;
    closeSessionScreen();
    // Quem cancelou foi a própria pessoa (na senha ou na identidade nova): não é erro para avisar.
    if (error?.message !== 'Conexão cancelada.') await message('Não foi possível conectar', error?.message || 'A conexão falhou.');
    return;
  }
  connecting = null;
  if (attempt.cancelled) {
    session.close();
    return;
  }

  state.identidades[target.key] = session.fingerprint;
  state.recentes = [{ address: target.shown, name: session.hostName, time: Date.now() },
    ...state.recentes.filter((r) => P.parseTarget(r.address)?.key !== target.key)].slice(0, 12);
  const entry = state.salvos.find((c) => c.key === target.key);
  if (entry) entry.paired = session.paired;
  save();
  startSession(session);
}

function startSession(session) {
  const tela = new Tela(session);
  const keyboard = new Teclado(session, tela);
  const primary = Math.max(0, session.screens.findIndex((s) => s.Primary));
  current = { session, tela, keyboard, screen: primary, clipboard: '' };
  renderKeys();
  setStatus('Recebendo a imagem...');
  session.sendSettings(primary, state.qualidade);
  session.run({
    onFrame: (frame) => tela.frame(frame),
    onPointer: (shape) => tela.setShape(shape),
    onPointerPosition: (x, y, onScreen) => tela.remotePointer(x, y, onScreen),
    onClipboard: (text) => {
      current.clipboard = text;
      toast('O computador copiou um texto. Para trazer para cá: Mais, "Copiar o que foi copiado lá".');
    },
    onNotice: (text) => toast(text),
    onClosed: (reason) => endSession(reason),
  });
}

async function endSession(reason) {
  const ending = current;
  if (!ending) return;
  current = null;
  ending.tela.dispose();
  closeOpenDialog?.(null);
  closeSessionScreen();
  if (reason) await message('Sessão encerrada', reason);
}

async function leave() {
  if (connecting) {
    connecting.cancelled = true;
    connecting = null;
    closeOpenDialog?.(null);
    closeSessionScreen();
    return;
  }
  if (!current) return;
  const { value } = await ask({
    message: `Desconectar de ${current.session.hostName || current.session.target.shown}?`,
    buttons: [{ label: 'Continuar', value: null }, { label: 'Desconectar', value: 'sair', kind: 'forte', submit: true }],
  });
  if (value === 'sair') current?.session.close();
}

// ---------- a tela do computador ----------

const LONG_PRESS_MS = 500;
const DOUBLE_TAP_MS = 300;
const DOUBLE_TAP_SLOP = 30;
const TOUCH_SLOP = 10;
const POINTER_SPEED = 1.3;
const DOUBLE_CLICK_ESCAPE = 10;
const WHEEL_STEP = 40;
const TAP_MARK_MS = 350;
const POINTER_MIN = 28;
const LOCAL_HOLD_MS = 600;
const TWO_UNDECIDED = 0;
const TWO_ZOOM = 1;
const TWO_SCROLL = 2;

async function decodeImage(bytes, type) {
  const blob = new Blob([bytes], { type });
  if (typeof createImageBitmap === 'function') {
    try { return await createImageBitmap(blob); } catch { /* cai no caminho abaixo */ }
  }
  const url = URL.createObjectURL(blob);
  try {
    const image = new Image();
    image.src = url;
    await image.decode();
    return image;
  } finally {
    URL.revokeObjectURL(url);
  }
}

/**
 * Mostra a tela do computador e transforma toque e mouse em mouse lá. Os gestos são os do aplicativo Android:
 *  - Toque direto: o toque clica onde o dedo encostou; arrastar move a imagem quando ela está ampliada.
 *  - Touchpad: arrastar move um ponteiro, e o toque clica onde ele está.
 * Nos dois: segurar e arrastar (ou tocar duas vezes e arrastar) segura o botão esquerdo; segurar e soltar sem
 * arrastar é o botão direito, assim como tocar com dois dedos; dois dedos rolam a página e a pinça amplia.
 */
class Tela {
  constructor(session) {
    this.session = session;
    this.canvas = $('tela');
    this.ctx = this.canvas.getContext('2d');
    this.image = document.createElement('canvas');
    this.imageCtx = this.image.getContext('2d');
    this.width = 0;
    this.height = 0;
    this.scale = 0;
    this.ox = 0;
    this.oy = 0;
    this.viewW = 0;
    this.viewH = 0;
    this.touchpad = !!state.touchpad;
    // shape: undefined = ainda não chegou (vale a seta daqui); null = o computador escondeu o ponteiro.
    this.pointer = { x: 0, y: 0, known: false, onScreen: true, shape: undefined, hotX: 0, hotY: 0 };
    this.lastLocalMove = 0;
    this.chain = Promise.resolve();
    this.pending = 0;
    this.waitWhole = false;
    this.touches = new Map();
    this.g = null;
    this.longTimer = 0;
    this.lastTap = { time: 0, x: 0, y: 0 };
    this.tapMark = null;
    this.wheelRemainder = 0;
    this.lastMoveSent = 0;
    this.frames = 0;
    this.raf = 0;
    this.closed = new AbortController();
    this.listen();
    this.observer = new ResizeObserver(() => this.resize());
    this.observer.observe(this.canvas);
    this.resize();
  }

  dispose() {
    this.closed.abort();
    this.observer.disconnect();
    clearTimeout(this.longTimer);
    cancelAnimationFrame(this.raf);
  }

  // ---------- imagem ----------

  frame(frame) {
    const whole = frame.tiles.length === 1 && frame.tiles[0].width === frame.width && frame.tiles[0].height === frame.height;
    // Se a decodificação ficar para trás, os quadros parciais pendentes são descartados até chegar um inteiro.
    if (this.waitWhole && !whole) return;
    if (whole) this.waitWhole = false;
    if (this.pending > 8) {
      this.waitWhole = true;
      this.askWholeFrame();
      return;
    }
    this.pending++;
    this.chain = this.chain.then(() => this.apply(frame)).catch(() => {}).finally(() => { this.pending--; });
  }

  // O computador manda a tela inteira de novo quando a qualidade muda: um passo para o lado e de volta basta.
  askWholeFrame() {
    if (!current) return;
    const q = state.qualidade;
    this.session.sendSettings(current.screen, q === 100 ? 99 : q + 1);
    this.session.sendSettings(current.screen, q);
  }

  async apply(frame) {
    const images = await Promise.all(frame.tiles.map((t) => decodeImage(t.jpeg, 'image/jpeg')));
    if (this.closed.signal.aborted) return;
    if (frame.width !== this.width || frame.height !== this.height) {
      this.width = frame.width;
      this.height = frame.height;
      this.image.width = frame.width;
      this.image.height = frame.height;
      this.scale = 0;
      if (!this.pointer.known) {
        this.pointer.x = frame.width / 2;
        this.pointer.y = frame.height / 2;
      }
      this.settle();
    }
    frame.tiles.forEach((t, i) => {
      this.imageCtx.drawImage(images[i], t.x, t.y, t.width, t.height);
      images[i].close?.();
    });
    if (++this.frames === 1) setStatus('');
    this.invalidate();
  }

  async setShape(shape) {
    if (!shape) {
      this.pointer.shape = null;
      this.invalidate();
      return;
    }
    try {
      const image = await decodeImage(shape.png, 'image/png');
      this.pointer.shape?.close?.();
      this.pointer.shape = image;
      this.pointer.hotX = shape.hotX;
      this.pointer.hotY = shape.hotY;
      this.invalidate();
    } catch { /* forma que não abriu: fica a anterior */ }
  }

  remotePointer(x, y, onScreen) {
    // Enquanto o dedo está mexendo o ponteiro daqui, vale a posição daqui.
    if (performance.now() - this.lastLocalMove < LOCAL_HOLD_MS || this.width < 2) return;
    this.pointer.x = (x * (this.width - 1)) / 65535;
    this.pointer.y = (y * (this.height - 1)) / 65535;
    this.pointer.known = true;
    this.pointer.onScreen = onScreen;
    this.invalidate();
  }

  // ---------- desenho ----------

  invalidate() {
    if (this.raf) return;
    this.raf = requestAnimationFrame(() => {
      this.raf = 0;
      this.draw();
    });
  }

  resize() {
    const rect = this.canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    this.viewW = rect.width;
    this.viewH = rect.height;
    const w = Math.max(1, Math.round(rect.width * dpr));
    const h = Math.max(1, Math.round(rect.height * dpr));
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
    this.settle();
    this.keepPointerVisible();
    this.invalidate();
  }

  minScale() {
    return Math.min(this.viewW / this.width, this.viewH / this.height);
  }

  settle() {
    if (!this.width || !this.viewW || !this.viewH) return;
    const least = this.minScale();
    if (this.scale <= 0) this.scale = least;
    this.scale = clamp(this.scale, least, Math.max(least * 8, 3));
    const shownW = this.width * this.scale;
    const shownH = this.height * this.scale;
    this.ox = shownW <= this.viewW ? (this.viewW - shownW) / 2 : clamp(this.ox, this.viewW - shownW, 0);
    this.oy = shownH <= this.viewH ? (this.viewH - shownH) / 2 : clamp(this.oy, this.viewH - shownH, 0);
  }

  fit() {
    this.scale = 0;
    this.settle();
    this.invalidate();
  }

  draw() {
    const ctx = this.ctx;
    const dpr = window.devicePixelRatio || 1;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.fillStyle = '#1e1e1e';
    ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);
    if (!this.width) return;
    ctx.setTransform(dpr * this.scale, 0, 0, dpr * this.scale, dpr * this.ox, dpr * this.oy);
    ctx.imageSmoothingEnabled = this.scale < 1.5;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(this.image, 0, 0);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    this.drawPointer(ctx);
    this.drawTapMark(ctx);
  }

  drawPointer(ctx) {
    const p = this.pointer;
    if (!p.onScreen || p.shape === null || (!this.touchpad && !p.known)) return;
    const x = this.ox + p.x * this.scale;
    const y = this.oy + p.y * this.scale;
    if (p.shape) {
      // Pequeno demais numa tela reduzida não dá para ver: o ponteiro tem um tamanho mínimo.
      const k = Math.max(this.scale, POINTER_MIN / Math.max(1, p.shape.height));
      ctx.imageSmoothingEnabled = true;
      ctx.drawImage(p.shape, x - p.hotX * k, y - p.hotY * k, p.shape.width * k, p.shape.height * k);
      return;
    }
    // Seta desenhada aqui, até a forma de lá chegar.
    ctx.save();
    ctx.translate(x, y);
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(0, 20);
    ctx.lineTo(5, 15);
    ctx.lineTo(9, 23);
    ctx.lineTo(12, 22);
    ctx.lineTo(8, 14);
    ctx.lineTo(14, 14);
    ctx.closePath();
    ctx.fillStyle = '#fff';
    ctx.strokeStyle = '#000';
    ctx.lineWidth = 1.2;
    ctx.fill();
    ctx.stroke();
    ctx.restore();
  }

  drawTapMark(ctx) {
    const mark = this.tapMark;
    if (!mark) return;
    const age = performance.now() - mark.time;
    if (age > TAP_MARK_MS) {
      this.tapMark = null;
      return;
    }
    ctx.beginPath();
    ctx.arc(mark.x, mark.y, mark.long ? 26 : 18, 0, Math.PI * 2);
    ctx.fillStyle = `rgba(97, 210, 223, ${0.35 * (1 - age / TAP_MARK_MS)})`;
    ctx.fill();
    this.invalidate();
  }

  mark(long = false) {
    this.tapMark = { x: this.ox + this.pointer.x * this.scale, y: this.oy + this.pointer.y * this.scale, time: performance.now(), long };
    this.invalidate();
  }

  // ---------- mouse lá ----------

  local(event) {
    const rect = this.canvas.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  movePointerTo(viewX, viewY) {
    if (this.scale <= 0) return;
    this.pointer.x = clamp((viewX - this.ox) / this.scale, 0, Math.max(0, this.width - 1));
    this.pointer.y = clamp((viewY - this.oy) / this.scale, 0, Math.max(0, this.height - 1));
  }

  send(action, wheel = 0) {
    if (this.width < 2 || this.height < 2) return;
    this.lastLocalMove = performance.now();
    this.pointer.known = true;
    this.pointer.onScreen = true;
    this.session.sendMouse((this.pointer.x * 65535) / (this.width - 1), (this.pointer.y * 65535) / (this.height - 1), action, wheel);
  }

  // Um movimento a cada 12 ms basta; mais que isso só enche a fila de envio.
  sendMove() {
    const now = performance.now();
    if (now - this.lastMoveSent < 12) return;
    this.lastMoveSent = now;
    this.send(MouseAction.MOVE);
  }

  click(down, up) {
    const act = () => {
      this.send(MouseAction.MOVE);
      this.send(down);
      this.send(up);
    };
    // Com Ctrl ou Shift armado na barra de teclas, o clique sai com a tecla segurada.
    if (current?.keyboard) current.keyboard.withArmed(act);
    else act();
    this.mark();
  }

  // Move o ponteiro alguns pixels na direção dada: fora do quadradinho em que o Windows junta dois cliques
  // num clique duplo (que, na barra de título, maximizaria a janela em vez de arrastar).
  nudge(dx, dy) {
    const length = Math.hypot(dx, dy);
    if (length <= 0) return;
    this.pointer.x = clamp(this.pointer.x + (dx / length) * DOUBLE_CLICK_ESCAPE, 0, this.width - 1);
    this.pointer.y = clamp(this.pointer.y + (dy / length) * DOUBLE_CLICK_ESCAPE, 0, this.height - 1);
  }

  keepPointerVisible() {
    if (!this.width || !this.touchpad) return;
    const margin = 24;
    const x = this.pointer.x * this.scale + this.ox;
    const y = this.pointer.y * this.scale + this.oy;
    if (x < margin) this.ox += margin - x;
    else if (x > this.viewW - margin) this.ox -= x - (this.viewW - margin);
    if (y < margin) this.oy += margin - y;
    else if (y > this.viewH - margin) this.oy -= y - (this.viewH - margin);
    this.settle();
  }

  setTouchpad(on) {
    this.touchpad = on;
    if (on) this.keepPointerVisible();
    this.invalidate();
  }

  // ---------- eventos ----------

  listen() {
    const options = { signal: this.closed.signal };
    const c = this.canvas;
    c.addEventListener('pointerdown', (e) => this.down(e), options);
    c.addEventListener('pointermove', (e) => this.move(e), options);
    c.addEventListener('pointerup', (e) => this.up(e), options);
    c.addEventListener('pointercancel', (e) => this.cancel(e), options);
    c.addEventListener('wheel', (e) => this.wheel(e), { ...options, passive: false });
    c.addEventListener('contextmenu', (e) => e.preventDefault(), options);
  }

  // Mouse de verdade (computador, iPad com mouse): vai direto, sem gestos.
  mouse(e, kind) {
    const point = this.local(e);
    this.movePointerTo(point.x, point.y);
    if (kind === 'move') {
      this.sendMove();
      this.invalidate();
      return;
    }
    const actions = { 0: [MouseAction.LEFT_DOWN, MouseAction.LEFT_UP], 1: [MouseAction.MIDDLE_DOWN, MouseAction.MIDDLE_UP], 2: [MouseAction.RIGHT_DOWN, MouseAction.RIGHT_UP] };
    const pair = actions[e.button];
    if (!pair) return;
    this.send(MouseAction.MOVE);
    this.send(kind === 'down' ? pair[0] : pair[1]);
  }

  down(e) {
    e.preventDefault();
    try { this.canvas.setPointerCapture(e.pointerId); } catch { /* toque que o navegador já soltou */ }
    if (e.pointerType === 'mouse') {
      this.mouse(e, 'down');
      return;
    }
    if (!this.width) return;
    const point = this.local(e);
    this.touches.set(e.pointerId, point);
    if (this.touches.size === 1) {
      const now = performance.now();
      this.g = {
        downX: point.x, downY: point.y, lastX: point.x, lastY: point.y, start: now,
        moved: false, multi: false, twoMoved: false, longPressed: false, dragging: false,
        secondTap: now - this.lastTap.time < DOUBLE_TAP_MS && Math.hypot(point.x - this.lastTap.x, point.y - this.lastTap.y) < DOUBLE_TAP_SLOP,
      };
      if (!this.g.secondTap) this.longTimer = setTimeout(() => this.longPress(), LONG_PRESS_MS);
    } else if (this.touches.size === 2 && this.g) {
      clearTimeout(this.longTimer);
      this.endDrag();
      const [a, b] = [...this.touches.values()];
      Object.assign(this.g, {
        multi: true, two: TWO_UNDECIDED, startSpan: Math.hypot(a.x - b.x, a.y - b.y),
        startFx: (a.x + b.x) / 2, startFy: (a.y + b.y) / 2,
      });
      this.g.lastSpan = this.g.startSpan;
      this.g.lastFx = this.g.startFx;
      this.g.lastFy = this.g.startFy;
      this.wheelRemainder = 0;
    }
  }

  longPress() {
    const g = this.g;
    if (!g || g.moved || g.multi) return;
    g.longPressed = true;
    if (!this.touchpad) this.movePointerTo(g.downX, g.downY);
    navigator.vibrate?.(25);
    this.mark(true);
  }

  move(e) {
    if (e.pointerType === 'mouse') {
      this.mouse(e, 'move');
      return;
    }
    if (!this.touches.has(e.pointerId) || !this.g) return;
    e.preventDefault();
    this.touches.set(e.pointerId, this.local(e));
    if (this.touches.size >= 2) this.twoFingers();
    else if (!this.g.multi) this.oneFinger(this.touches.get(e.pointerId));
  }

  oneFinger(point) {
    const g = this.g;
    const dx = point.x - g.lastX;
    const dy = point.y - g.lastY;
    if (!g.moved) {
      if (Math.hypot(point.x - g.downX, point.y - g.downY) < TOUCH_SLOP) return;
      g.moved = true;
      clearTimeout(this.longTimer);
      // Segurar e arrastar, ou tocar duas vezes e arrastar: segura o botão esquerdo no ponto pego.
      if (g.longPressed || g.secondTap) {
        g.dragging = true;
        if (!this.touchpad) this.movePointerTo(g.downX, g.downY);
        if (g.secondTap) this.nudge(point.x - g.downX, point.y - g.downY);
        this.send(MouseAction.MOVE);
        this.send(MouseAction.LEFT_DOWN);
      }
    }
    g.lastX = point.x;
    g.lastY = point.y;
    if (this.touchpad) {
      this.pointer.x = clamp(this.pointer.x + (dx / this.scale) * POINTER_SPEED, 0, this.width - 1);
      this.pointer.y = clamp(this.pointer.y + (dy / this.scale) * POINTER_SPEED, 0, this.height - 1);
      this.keepPointerVisible();
      this.sendMove();
    } else if (g.dragging) {
      this.movePointerTo(point.x, point.y);
      this.sendMove();
    } else {
      this.ox += dx;
      this.oy += dy;
      this.settle();
    }
    this.invalidate();
  }

  twoFingers() {
    const g = this.g;
    const [a, b] = [...this.touches.values()];
    const span = Math.hypot(a.x - b.x, a.y - b.y);
    const fx = (a.x + b.x) / 2;
    const fy = (a.y + b.y) / 2;
    if (g.two === TWO_UNDECIDED) {
      // A pinça muda a distância entre os dedos; a rolagem move os dois juntos.
      if (Math.abs(span - g.startSpan) > 28) g.two = TWO_ZOOM;
      else if (Math.hypot(fx - g.startFx, fy - g.startFy) > 14) g.two = TWO_SCROLL;
      if (g.two !== TWO_UNDECIDED) {
        g.twoMoved = true;
        g.lastSpan = span;
        g.lastFx = fx;
        g.lastFy = fy;
      }
      return;
    }
    if (g.two === TWO_ZOOM) {
      const before = this.scale;
      this.scale = clamp(this.scale * (span / Math.max(1, g.lastSpan)), this.minScale(), Math.max(this.minScale() * 8, 3));
      // O ponto entre os dedos continua sob os dedos.
      this.ox = fx - (g.lastFx - this.ox) * (this.scale / before);
      this.oy = fy - (g.lastFy - this.oy) * (this.scale / before);
      this.settle();
    } else {
      // Rolagem natural, como na tela do celular: o conteúdo acompanha os dedos.
      this.wheelRemainder += (fy - g.lastFy) / WHEEL_STEP;
      const steps = Math.trunc(this.wheelRemainder);
      if (steps !== 0) {
        this.wheelRemainder -= steps;
        if (!this.touchpad) this.movePointerTo(fx, fy);
        this.send(MouseAction.WHEEL, steps * 120);
      }
    }
    g.lastSpan = span;
    g.lastFx = fx;
    g.lastFy = fy;
    this.invalidate();
  }

  up(e) {
    if (e.pointerType === 'mouse') {
      this.mouse(e, 'up');
      return;
    }
    if (!this.touches.delete(e.pointerId) || !this.g) return;
    e.preventDefault();
    if (this.touches.size > 0) return;
    clearTimeout(this.longTimer);
    const g = this.g;
    this.g = null;
    if (g.dragging) {
      this.endDrag(g);
    } else if (g.multi) {
      // Dois dedos encostados e soltos sem mover: botão direito.
      if (!g.twoMoved && performance.now() - g.start < 400) {
        if (!this.touchpad) this.movePointerTo(g.startFx, g.startFy);
        this.click(MouseAction.RIGHT_DOWN, MouseAction.RIGHT_UP);
      }
    } else if (g.longPressed && !g.moved) {
      // Segurou e soltou sem arrastar: botão direito, no ponto pego.
      this.click(MouseAction.RIGHT_DOWN, MouseAction.RIGHT_UP);
    } else if (!g.moved) {
      // No toque direto, o segundo toque de um clique duplo reaproveita o ponto do primeiro: o dedo nunca cai
      // exatamente no mesmo lugar, e o Windows exige que caia.
      if (!this.touchpad && !g.secondTap) this.movePointerTo(g.downX, g.downY);
      this.click(MouseAction.LEFT_DOWN, MouseAction.LEFT_UP);
      this.lastTap = { time: performance.now(), x: g.downX, y: g.downY };
    }
  }

  cancel(e) {
    this.touches.delete(e.pointerId);
    if (this.touches.size > 0) return;
    clearTimeout(this.longTimer);
    this.endDrag(this.g);
    this.g = null;
  }

  endDrag(g = this.g) {
    if (!g?.dragging) return;
    g.dragging = false;
    this.send(MouseAction.MOVE);
    this.send(MouseAction.LEFT_UP);
  }

  wheel(e) {
    e.preventDefault();
    const unit = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 400 : 1;
    // Cerca de 100 pontos de rolagem do navegador por "dente" da roda do mouse (120 no Windows).
    this.wheelRemainder += (-e.deltaY * unit) / 100;
    const steps = Math.trunc(this.wheelRemainder);
    if (steps === 0) return;
    this.wheelRemainder -= steps;
    const point = this.local(e);
    this.movePointerTo(point.x, point.y);
    this.send(MouseAction.WHEEL, clamp(steps, -10, 10) * 120);
  }
}

// ---------- teclado ----------

/** Códigos de tecla virtual do Windows, que é o que o protocolo do Ponte carrega. */
const VK = {
  BACK: 0x08, TAB: 0x09, RETURN: 0x0d, SHIFT: 0x10, CONTROL: 0x11, ALT: 0x12, ESCAPE: 0x1b,
  PAGE_UP: 0x21, PAGE_DOWN: 0x22, END: 0x23, HOME: 0x24, LEFT: 0x25, UP: 0x26, RIGHT: 0x27, DOWN: 0x28,
  INSERT: 0x2d, DELETE: 0x2e, WIN: 0x5b, F1: 0x70,
};

/** Teclas que o Windows espera marcadas como "estendidas", para não confundir com o teclado numérico. */
const extended = (vk) => (vk >= VK.PAGE_UP && vk <= VK.DOWN) || vk === VK.INSERT || vk === VK.DELETE || vk === VK.WIN;

const KEY_CODES = {
  Enter: VK.RETURN, Backspace: VK.BACK, Tab: VK.TAB, Escape: VK.ESCAPE, Delete: VK.DELETE, Insert: VK.INSERT,
  Home: VK.HOME, End: VK.END, PageUp: VK.PAGE_UP, PageDown: VK.PAGE_DOWN,
  ArrowLeft: VK.LEFT, ArrowUp: VK.UP, ArrowRight: VK.RIGHT, ArrowDown: VK.DOWN,
};

function virtualKey(key) {
  if (key in KEY_CODES) return KEY_CODES[key];
  const f = /^F([1-9]|1[0-2])$/.exec(key);
  return f ? VK.F1 + Number(f[1]) - 1 : undefined;
}

class Teclado {
  constructor(session) {
    this.session = session;
    /** Ctrl, Alt, Shift e Win armados na barra de teclas: valem para a próxima tecla, texto ou clique. */
    this.armed = new Set();
  }

  down(vk) { this.session.sendKey(vk, false, extended(vk)); }
  up(vk) { this.session.sendKey(vk, true, extended(vk)); }

  key(vk) {
    this.down(vk);
    this.up(vk);
  }

  press(vk) {
    this.withArmed(() => this.key(vk));
  }

  withArmed(act) {
    const held = [...this.armed];
    held.forEach((vk) => this.down(vk));
    act();
    held.reverse().forEach((vk) => this.up(vk));
    if (held.length) {
      this.armed.clear();
      renderKeys();
    }
  }

  /** Texto pronto para o computador digitar; com uma tecla armada, letras e números viram atalhos (Ctrl+C...). */
  text(text) {
    if (!text) return;
    if (this.armed.size === 0) {
      this.session.sendText(text);
      return;
    }
    for (const ch of text) {
      const upper = ch.toUpperCase();
      if (/^[A-Z0-9]$/.test(upper)) this.press(upper.charCodeAt(0));
      else this.session.sendText(ch);
    }
  }
}

// Teclado físico (iPad com teclado, computador): teclas especiais e atalhos vão como teclas; o resto, como texto.
function handleKeydown(event) {
  if (!current || event.isComposing) return;
  const keyboard = current.keyboard;
  const vk = virtualKey(event.key);
  const modifiers = [];
  if (event.ctrlKey || event.metaKey) modifiers.push(VK.CONTROL);   // Cmd do iPad vale como Ctrl
  if (event.altKey) modifiers.push(VK.ALT);
  const withModifiers = (act) => {
    const held = event.shiftKey && (vk !== undefined || modifiers.length) ? [...modifiers, VK.SHIFT] : modifiers;
    held.forEach((m) => keyboard.down(m));
    act();
    [...held].reverse().forEach((m) => keyboard.up(m));
  };
  if (vk !== undefined) {
    event.preventDefault();
    withModifiers(() => keyboard.press(vk));
    return;
  }
  if (modifiers.length) {
    const code = /^(?:Key([A-Z])|Digit([0-9]))$/.exec(event.code || '');
    if (code) {
      event.preventDefault();
      withModifiers(() => keyboard.key((code[1] || code[2]).charCodeAt(0)));
    }
    return;
  }
  // Fora do campo escondido (sem teclado do celular aberto), o texto comum chega por aqui.
  if (event.target !== $('digitar') && event.key.length === 1) {
    event.preventDefault();
    keyboard.text(event.key);
  }
}

// O campo escondido abre o teclado do celular. O que é digitado não fica nele: cada letra vai para o computador.
function setupTyping() {
  const area = $('digitar');
  // Um caractere invisível no campo: com ele, o apagar do teclado do iPhone sempre gera um evento.
  const reset = () => {
    area.value = '​';
    area.setSelectionRange(1, 1);
  };
  reset();
  let composing = false;
  area.addEventListener('beforeinput', (event) => {
    const keyboard = current?.keyboard;
    if (!keyboard) return;
    switch (event.inputType) {
      case 'insertText':
        keyboard.text(event.data || '');
        break;
      case 'insertReplacementText':
      case 'insertFromPaste':
      case 'insertFromDrop':
        keyboard.text(event.dataTransfer?.getData('text/plain') || event.data || '');
        break;
      case 'insertLineBreak':
      case 'insertParagraph':
        keyboard.press(VK.RETURN);
        break;
      case 'deleteContentBackward':
        keyboard.press(VK.BACK);
        break;
      case 'deleteWordBackward':
        keyboard.down(VK.CONTROL);
        keyboard.key(VK.BACK);
        keyboard.up(VK.CONTROL);
        break;
      case 'deleteContentForward':
        keyboard.press(VK.DELETE);
        break;
      case 'insertCompositionText':
        // Composição (acentos, outros alfabetos): o texto pronto sai no fim dela.
        return;
      default:
        break;
    }
    event.preventDefault();
  });
  area.addEventListener('compositionstart', () => { composing = true; });
  area.addEventListener('compositionend', (event) => {
    composing = false;
    current?.keyboard.text(event.data || '');
    reset();
  });
  area.addEventListener('input', () => {
    if (!composing && area.value !== '​') reset();
  });
  area.addEventListener('focus', () => {
    reset();
    $('b-teclado').classList.add('ativo');
  });
  area.addEventListener('blur', () => $('b-teclado').classList.remove('ativo'));
}

const KEYS = [
  ['Ctrl', VK.CONTROL, true], ['Alt', VK.ALT, true], ['Shift', VK.SHIFT, true], ['Win', VK.WIN, true],
  ['Esc', VK.ESCAPE], ['Tab', VK.TAB], ['Del', VK.DELETE], ['←', VK.LEFT], ['↑', VK.UP], ['↓', VK.DOWN], ['→', VK.RIGHT],
  ['Home', VK.HOME], ['End', VK.END], ['PgUp', VK.PAGE_UP], ['PgDn', VK.PAGE_DOWN], ['Ins', VK.INSERT],
];

function renderKeys() {
  const panel = $('teclas');
  const keyboard = current?.keyboard;
  panel.replaceChildren(...KEYS.map(([label, vk, sticky]) => {
    const button = el('button', sticky && keyboard?.armed.has(vk) ? 'armada' : '', label);
    button.type = 'button';
    // Não tira o foco do campo escondido: o teclado do celular continua aberto.
    button.addEventListener('pointerdown', (event) => event.preventDefault());
    button.onclick = () => {
      if (!current) return;
      if (sticky) {
        if (keyboard.armed.has(vk)) keyboard.armed.delete(vk);
        else keyboard.armed.add(vk);
        renderKeys();
      } else {
        keyboard.press(vk);
      }
    };
    return button;
  }));
}

// ---------- barra da sessão ----------

async function showMore() {
  if (!current) return;
  const c = current;
  const options = [
    { label: 'Ver a tela inteira', value: 'inteira' },
    { label: 'Digitar um texto', value: 'texto' },
    { label: 'Colar aqui o texto copiado no aparelho', value: 'colar' },
  ];
  if (c.clipboard) options.push({ label: 'Copiar o que foi copiado lá', value: 'copiar' });
  options.push({ label: 'Qualidade da imagem', value: 'qualidade' });
  if (c.session.screens.length > 1) options.push({ label: 'Trocar de monitor', value: 'monitor' });
  options.push({ label: 'Sobre esta conexão', value: 'sobre' });
  const { value } = await ask({ options, buttons: [{ label: 'Fechar', value: null }] });
  if (current !== c) return;
  if (value === 'inteira') c.tela.fit();
  else if (value === 'texto') typeText(c);
  else if (value === 'colar') pasteText(c);
  else if (value === 'copiar') {
    try {
      await navigator.clipboard.writeText(c.clipboard);
      toast('Copiado neste aparelho.');
    } catch {
      typeText(c, c.clipboard, 'Texto copiado no computador');
    }
  } else if (value === 'qualidade') chooseQuality(c);
  else if (value === 'monitor') chooseScreen(c);
  else if (value === 'sobre') aboutConnection(c);
}

async function typeText(c, initial = '', title = 'Digitar um texto') {
  const { value, fields } = await ask({
    title,
    message: initial ? 'Selecione e copie o texto abaixo.' : 'O texto vai ser digitado no computador, onde estiver o cursor.',
    fields: [{ id: 'texto', label: 'Texto', value: initial, multiline: true }],
    buttons: initial ? [{ label: 'Fechar', value: null }] : [{ label: 'Cancelar', value: null }, { label: 'Digitar', value: 'ok', kind: 'forte', submit: true }],
  });
  if (value === 'ok' && current === c) c.keyboard.text(fields.texto);
}

async function pasteText(c) {
  try {
    const text = await navigator.clipboard.readText();
    if (text && current === c) {
      c.keyboard.text(text);
      toast('Texto digitado no computador.');
      return;
    }
  } catch { /* o navegador não deixou ler: a pessoa cola no campo */ }
  typeText(c);
}

async function chooseQuality(c) {
  const levels = [['Baixa (mais rápida)', 35], ['Média', 60], ['Alta', 85]];
  const { value } = await ask({
    title: 'Qualidade da imagem',
    options: levels.map(([label, q]) => ({ label, value: q, checked: state.qualidade === q })),
    buttons: [{ label: 'Fechar', value: null }],
  });
  if (value === null || current !== c) return;
  state.qualidade = value;
  save();
  c.session.sendSettings(c.screen, value);
}

async function chooseScreen(c) {
  const { value } = await ask({
    title: 'Monitor',
    options: c.session.screens.map((s, i) => ({
      label: `Monitor ${i + 1}${s.Primary ? ' (principal)' : ''} · ${s.Width}×${s.Height}`, value: i, checked: c.screen === i,
    })),
    buttons: [{ label: 'Fechar', value: null }],
  });
  if (value === null || current !== c) return;
  c.screen = value;
  c.session.sendSettings(value, state.qualidade);
  c.tela.fit();
}

async function aboutConnection(c) {
  const s = c.session;
  await message(s.hostName || s.target.shown,
    `ID: ${s.target.shown}\nPonte lá: ${s.hostVersion || 'versão antiga'}\n` +
    `${s.paired ? 'Este aparelho está autorizado: entra sem aprovação.' : 'Este aparelho ainda não está autorizado a entrar direto.'}\n\n` +
    `Identidade do computador:\n${s.fingerprint.slice(0, 32).replace(/(.{4})/g, '$1 ').trim()}…\n\n` +
    'A conexão é cifrada de ponta a ponta: o servidor só repassa os dados.');
}

function toggleKeyboard() {
  const area = $('digitar');
  if (document.activeElement === area) area.blur();
  else area.focus();
}

function toggleKeys() {
  const panel = $('teclas');
  panel.hidden = !panel.hidden;
  $('b-teclas').classList.toggle('ativo', !panel.hidden);
}

function toggleMode() {
  state.touchpad = !state.touchpad;
  save();
  $('b-modo').textContent = state.touchpad ? 'Touchpad' : 'Toque';
  current?.tela.setTouchpad(state.touchpad);
  toast(state.touchpad ? 'Touchpad: arraste para mover o ponteiro e toque para clicar onde ele está.' : 'Toque: o toque clica onde o dedo encostar.');
}

// ---------- início ----------

function formatAddress(input) {
  const digits = input.value.replace(/\D/g, '').slice(0, 9);
  const formatted = digits.replace(/(\d{3})(?=\d)/g, '$1 ');
  if (formatted !== input.value) input.value = formatted;
}

function init() {
  setupTyping();
  const address = $('endereco');
  address.addEventListener('input', () => {
    formatAddress(address);
    showAddressError('');
  });
  $('conectar').addEventListener('submit', (event) => {
    event.preventDefault();
    address.blur();
    connectTo(address.value);
  });
  $('salvar').onclick = () => editComputer(address.value.trim(), '', true, true);
  $('menu').onclick = showMenu;
  $('b-teclado').addEventListener('pointerdown', (event) => event.preventDefault());
  $('b-teclado').onclick = toggleKeyboard;
  $('b-teclas').onclick = toggleKeys;
  $('b-modo').onclick = toggleMode;
  $('b-mais').onclick = showMore;
  $('b-sair').onclick = leave;
  document.addEventListener('keydown', (event) => {
    if (current && !$('dialogo').open) handleKeydown(event);
  });
  // No iPhone, a pinça fora da imagem ampliaria a página inteira durante a sessão.
  document.addEventListener('gesturestart', (event) => { if (current || connecting) event.preventDefault(); });
  window.visualViewport?.addEventListener('resize', fitViewport);
  window.visualViewport?.addEventListener('scroll', fitViewport);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) checkPresence(); });

  if (isIos && !standalone && state.dicaInstalar) $('instalar').hidden = false;
  $('instalar-fechar').onclick = () => {
    state.dicaInstalar = false;
    save();
    $('instalar').hidden = true;
  };

  // Link com o ID já preenchido (?id=123456789).
  const shared = P.parseTarget(new URLSearchParams(location.search).get('id') || '');
  if (shared) address.value = shared.shown;

  renderHome();
  showVersion();
  checkPresence();
  setInterval(checkPresence, 30000);
}

init();
