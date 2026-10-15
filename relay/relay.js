#!/usr/bin/env node
// Servidor de retransmissão do Ponte.
//
// Serve para dois computadores atrás de roteadores se encontrarem pelo ID: os dois abrem conexões
// de saída para cá e o servidor só repassa os bytes de um para o outro. O conteúdo já chega cifrado
// de ponta a ponta (TLS entre os dois computadores), então nada aqui consegue ler a sessão.
//
// Uso:  node relay.js [porta]        (padrão 7412; PONTE_BIND escolhe o endereço de escuta)
//       PONTE_KEY=chave               só atende quem mandar essa chave (recomendado em servidor público)
//       PONTE_UPDATE_DIR=pasta        arquivos de atualização Windows e Android e o componente de vídeo (padrão: ./atualizacao)
//       PONTE_WS_PORT=porta           também atende o Ponte no navegador, por WebSocket, nessa porta
//       PONTE_WS_BIND=endereço        onde a porta do WebSocket escuta (padrão 127.0.0.1: atrás do nginx, que faz o HTTPS)
//       PONTE_AUTH_PORT=porta         responde ao servidor de mídia (MediaMTX) quem pode transmitir e assistir (só 127.0.0.1)
//       PONTE_MEDIAMTX_API=url        API do MediaMTX, para contar quem está assistindo (padrão http://127.0.0.1:9997)
// Sem dependências: só o Node.js.
'use strict';

const net = require('net');
const http = require('http');
const crypto = require('crypto');
const { Duplex } = require('stream');
const fs = require('fs');
const path = require('path');

const PORT = parseInt(process.argv[2] || process.env.PONTE_PORT || '7412', 10);
const BIND = process.env.PONTE_BIND || '0.0.0.0';
const KEY = process.env.PONTE_KEY || '';
const UPDATE_DIR = process.env.PONTE_UPDATE_DIR || path.join(__dirname, 'atualizacao');
const MAX_UPDATE_BYTES = 64 * 1024 * 1024;
const MAX_VIDEO_BYTES = 128 * 1024 * 1024;   // componente de vídeo (FFmpeg comprimido), baixado uma vez por computador
const MAX_CONNECTIONS = 600;
const MAX_PER_ADDRESS = 40;      // conexões simultâneas vindas de um mesmo endereço
const HANDSHAKE_MS = 15000;      // prazo para a primeira linha de quem conecta
const PENDING_MS = 20000;        // prazo para o computador chamado atender
const HOST_IDLE_MS = 90000;      // computador registrado que não manda PING nesse tempo é removido
const MAX_LINE = 2048;
const MAX_PENDING = 500;
const WS_PORT = parseInt(process.env.PONTE_WS_PORT || '0', 10);
const WS_BIND = process.env.PONTE_WS_BIND || '127.0.0.1';
const WS_MAX_MESSAGE = 1024 * 1024;   // o navegador só manda mouse, teclas e texto: mensagens pequenas
const WS_PING_MS = 25000;             // mantém a conexão viva no nginx e nas redes móveis
const AUTH_PORT = parseInt(process.env.PONTE_AUTH_PORT || '0', 10);
const MEDIAMTX_API = process.env.PONTE_MEDIAMTX_API || 'http://127.0.0.1:9997';

const hosts = new Map();     // id -> conexão de controle do computador registrado
const pending = new Map();   // token -> { viewer, takeRest, timer }
const broadcasts = new Map(); // código da transmissão -> { id, hash, socket }

// Mesmo cálculo do programa: o ID são 9 dígitos tirados do SHA-256 da chave pública.
function idFromSpki(spki) {
  const hash = crypto.createHash('sha256').update(spki).digest();
  return String(100000000n + (hash.readBigUInt64BE(0) % 900000000n));
}

// Entrega linha a linha. takeRest() para a leitura e devolve o que sobrou no buffer,
// porque depois do "OK" os bytes seguintes já pertencem à sessão cifrada.
function readLines(socket, onLine) {
  let buffer = Buffer.alloc(0);
  let stopped = false;
  const takeRest = () => {
    stopped = true;
    socket.removeListener('data', onData);
    const rest = buffer;
    buffer = Buffer.alloc(0);
    return rest;
  };
  function onData(chunk) {
    buffer = Buffer.concat([buffer, chunk]);
    let end;
    while (!stopped && (end = buffer.indexOf(10)) >= 0) {
      const line = buffer.subarray(0, end).toString('latin1').replace(/\r$/, '');
      buffer = buffer.subarray(end + 1);
      onLine(line, takeRest);
    }
    if (!stopped && buffer.length > MAX_LINE) socket.destroy();
  }
  socket.on('data', onData);
}

function refuse(socket, message) {
  socket.end(`ERR ${message}\n`);
}

function registerHost(socket, id) {
  const nonce = crypto.randomBytes(32);
  let registered = false;
  socket.write(`NONCE ${nonce.toString('hex')}\n`);
  return (line) => {
    if (!registered) {
      // Só registra o ID quem assina o desafio com a chave privada da qual o ID foi derivado.
      const parts = line.split(' ');
      let valid = false;
      try {
        const spki = Buffer.from(parts[1] || '', 'base64');
        const signature = Buffer.from(parts[2] || '', 'base64');
        const key = crypto.createPublicKey({ key: spki, format: 'der', type: 'spki' });
        valid = parts[0] === 'PROOF' && idFromSpki(spki) === id &&
          crypto.verify('sha256', nonce, { key, dsaEncoding: 'ieee-p1363' }, signature);
      } catch {
        valid = false;
      }
      if (!valid) return refuse(socket, 'proof');

      const previous = hosts.get(id);
      if (previous && previous !== socket) previous.destroy();
      hosts.set(id, socket);
      registered = true;
      socket.setTimeout(HOST_IDLE_MS, () => socket.destroy());
      socket.on('close', () => {
        if (hosts.get(id) === socket) hosts.delete(id);
        // A transmissão vale enquanto o computador está registrado; reconectado, ele pede o mesmo código de novo.
        for (const [token, b] of broadcasts) if (b.socket === socket) broadcasts.delete(token);
      });
      socket.write('OK\n');
      return;
    }
    if (line === 'PING') return socket.write('PONG\n');

    // Transmissão de tela: o computador manda o resumo (SHA-256) da senha com que vai enviar o vídeo ao
    // servidor de mídia e recebe o código que vai no link. Com um código já dele, retoma a mesma transmissão.
    const start = /^TRANSMITIR ([0-9a-f]{64})(?: ([A-Za-z0-9]{24}))?$/.exec(line);
    if (start) return socket.write(`TRANSMISSAO ${startBroadcast(socket, id, start[1], start[2])}\n`);
    const stop = /^PARAR ([A-Za-z0-9]{24})$/.exec(line);
    if (stop) {
      if (broadcasts.get(stop[1])?.id === id) broadcasts.delete(stop[1]);
      return;
    }
    const count = /^ESPECTADORES ([A-Za-z0-9]{24})$/.exec(line);
    if (count && broadcasts.get(count[1])?.id === id) {
      viewers(count[1]).then((n) => { if (!socket.destroyed) socket.write(`ESPECTADORES ${count[1]} ${n}\n`); });
    }
  };
}

// ---------- transmissão de tela ----------
//
// O vídeo vai do computador para o MediaMTX (nesta mesma máquina), que entrega a quem abrir o link. O MediaMTX
// pergunta aqui, a cada conexão, se ela pode: transmitir só com a senha registrada pelo computador; assistir,
// com o código do link, enquanto a transmissão existir.

const TOKEN_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';

function randomToken() {
  let token = '';
  while (token.length < 24) {
    for (const byte of crypto.randomBytes(32)) {
      // Descarta o que passaria de 248 (4 × 62), para cada letra ter a mesma chance.
      if (byte < 248 && token.length < 24) token += TOKEN_ALPHABET[byte % 62];
    }
  }
  return token;
}

function startBroadcast(socket, id, hash, wanted) {
  const current = wanted ? broadcasts.get(wanted) : null;
  const token = wanted && (!current || current.id === id) ? wanted : randomToken();
  // Uma transmissão por computador: a nova substitui a anterior.
  for (const [other, b] of broadcasts) if (b.id === id && other !== token) broadcasts.delete(other);
  broadcasts.set(token, { id, hash, socket });
  return token;
}

function hashMatches(password, hash) {
  const given = crypto.createHash('sha256').update(password).digest();
  const expected = Buffer.from(hash, 'hex');
  return given.length === expected.length && crypto.timingSafeEqual(given, expected);
}

/** O que o MediaMTX pergunta: { user, password, ip, action, path, protocol, ... }. */
function authorize(request) {
  if (request.action === 'api') return ['127.0.0.1', '::1'].includes(String(request.ip));
  const match = /^ao\/([A-Za-z0-9]{24})$/.exec(String(request.path || ''));
  const broadcast = match && broadcasts.get(match[1]);
  if (!broadcast) return false;
  if (request.action === 'read') return true;
  if (request.action === 'publish') return request.user === 'ponte' && hashMatches(String(request.password || ''), broadcast.hash);
  return false;
}

/** Quantos estão assistindo agora, pela API do MediaMTX; -1 quando não deu para saber. */
function viewers(token) {
  return new Promise((resolve) => {
    const request = http.get(`${MEDIAMTX_API}/v3/paths/get/ao/${token}`, { timeout: 3000 }, (response) => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', (chunk) => { body += chunk; });
      response.on('end', () => {
        if (response.statusCode === 404) return resolve(0);
        try {
          const readers = JSON.parse(body).readers;
          resolve(Array.isArray(readers) ? readers.length : -1);
        } catch {
          resolve(-1);
        }
      });
    });
    request.on('timeout', () => request.destroy());
    request.on('error', () => resolve(-1));
  });
}

if (AUTH_PORT > 0) {
  const auth = http.createServer((request, response) => {
    // O nginx pergunta, antes de entregar o APK pela página de baixar, se o código do link é de uma transmissão no
    // ar: o aplicativo só sai para quem recebeu o link de uma transmissão, e não para qualquer um na internet.
    if (request.method === 'GET' && request.url === '/ponte/apk') {
      const original = String(request.headers['x-original-uri'] || '');
      const token = new URLSearchParams(original.includes('?') ? original.slice(original.indexOf('?') + 1) : '').get('t') || '';
      response.writeHead(/^[A-Za-z0-9]{24}$/.test(token) && broadcasts.has(token) ? 204 : 403);
      response.end();
      return;
    }
    if (request.method !== 'POST' || request.url !== '/mediamtx/auth') {
      response.writeHead(404);
      response.end();
      return;
    }
    let body = '';
    request.setEncoding('utf8');
    request.on('data', (chunk) => {
      body += chunk;
      if (body.length > 16384) request.destroy();
    });
    request.on('end', () => {
      let allowed = false;
      try { allowed = authorize(JSON.parse(body)); } catch { allowed = false; }
      response.writeHead(allowed ? 200 : 401);
      response.end();
    });
  });
  auth.on('error', (error) => {
    console.error(`Não foi possível escutar a autorização da transmissão em 127.0.0.1:${AUTH_PORT}: ${error.message}`);
    process.exit(1);
  });
  auth.listen(AUTH_PORT, '127.0.0.1', () => console.log(`Ponte: autorização da transmissão de tela em 127.0.0.1:${AUTH_PORT}`));
}

// "STATUS 1010": um "1" para cada ID (separados por vírgula) que está registrado aqui agora, na ordem pedida.
function onlineLine(ids) {
  return `STATUS ${ids.split(',').map((id) => (hosts.get(id) && !hosts.get(id).destroyed ? '1' : '0')).join('')}\n`;
}

function connectViewer(socket, id, takeRest) {
  const host = hosts.get(id);
  if (!host || host.destroyed) return refuse(socket, 'offline');
  if (pending.size >= MAX_PENDING) return refuse(socket, 'busy');

  const token = crypto.randomBytes(16).toString('hex');
  const timer = setTimeout(() => {
    pending.delete(token);
    refuse(socket, 'timeout');
  }, PENDING_MS);
  pending.set(token, { viewer: socket, takeRest, timer });
  socket.on('close', () => {
    if (pending.get(token)?.viewer === socket) {
      clearTimeout(timer);
      pending.delete(token);
    }
  });
  host.write(`INCOMING ${token}\n`);
}

function acceptFromHost(socket, token, takeRest) {
  const entry = pending.get(token);
  if (!entry) return refuse(socket, 'token');
  clearTimeout(entry.timer);
  pending.delete(token);

  const viewer = entry.viewer;
  const fromViewer = entry.takeRest();
  const fromHost = takeRest();
  viewer.write('OK\n');
  socket.write('OK\n');
  if (fromViewer.length) socket.write(fromViewer);
  if (fromHost.length) viewer.write(fromHost);
  viewer.pipe(socket);
  socket.pipe(viewer);
  const closeBoth = () => { viewer.destroy(); socket.destroy(); };
  viewer.on('close', closeBoth);
  socket.on('close', closeBoth);
}

// Atualização do programa: o servidor só entrega os arquivos da pasta. Quem garante que eles são
// legítimos é a assinatura conferida pelo próprio programa; este servidor não tem a chave que assina.
function sendUpdateFile(socket, name, maxBytes) {
  fs.readFile(path.join(UPDATE_DIR, name), (error, data) => {
    if (socket.destroyed) return;
    if (error || data.length === 0 || data.length > maxBytes) return refuse(socket, 'none');
    socket.setTimeout(120000, () => socket.destroy());
    socket.write(`OK ${data.length}\n`);
    socket.end(data);
  });
}

// O componente de vídeo é grande demais para ir para a memória a cada pedido: segue do disco aos poucos.
// O nome traz o SHA-256 do conteúdo, que o programa confere depois de baixar.
function streamUpdateFile(socket, name, maxBytes) {
  const file = path.join(UPDATE_DIR, name);
  fs.stat(file, (error, stats) => {
    if (socket.destroyed) return;
    if (error || !stats.isFile() || stats.size === 0 || stats.size > maxBytes) return refuse(socket, 'none');
    socket.setTimeout(60000, () => socket.destroy());
    socket.write(`OK ${stats.size}\n`);
    const input = fs.createReadStream(file);
    input.on('error', () => socket.destroy());
    socket.on('close', () => input.destroy());
    input.pipe(socket);
  });
}

// Compara pelo resumo das duas chaves, para o tempo da comparação não revelar nada sobre a chave certa.
function keyMatches(given) {
  const digest = (text) => crypto.createHash('sha256').update(text).digest();
  return crypto.timingSafeEqual(digest(given || ''), digest(KEY));
}

const perAddress = new Map();

const server = net.createServer((socket) => {
  const address = socket.remoteAddress || '';
  const count = (perAddress.get(address) || 0) + 1;
  perAddress.set(address, count);
  socket.on('close', () => {
    const left = (perAddress.get(address) || 1) - 1;
    if (left > 0) perAddress.set(address, left);
    else perAddress.delete(address);
  });
  socket.on('error', () => socket.destroy());
  if (count > MAX_PER_ADDRESS) return socket.destroy();

  socket.setNoDelay(true);
  socket.setKeepAlive(true, 30000);
  socket.setTimeout(HANDSHAKE_MS, () => socket.destroy());

  let hostHandler = null;
  readLines(socket, (line, takeRest) => {
    if (hostHandler) return hostHandler(line);

    // Quais destes computadores estão com o Ponte aberto agora (registrados aqui): um "1" ou "0" por ID, na ordem.
    // É o que acende a bolinha verde ou vermelha na lista de salvos. Não revela nada além de estar online.
    const status = /^PONTE STATUS (\d{9}(?:,\d{9}){0,99})(?: ([A-Za-z0-9_-]{1,128}))?$/.exec(line);
    if (status) {
      if (KEY && !keyMatches(status[2])) return refuse(socket, 'key');
      return socket.end(onlineLine(status[1]));
    }

    const match =/^PONTE (HOST|CONNECT|ACCEPT|MANIFEST|DOWNLOAD|ANDROID_MANIFEST|ANDROID_DOWNLOAD|VIDEO) ([0-9a-f]{1,64})(?: ([A-Za-z0-9_-]{1,128}))?$/.exec(line);
    if (!match) return socket.destroy();
    const [, command, argument, key] = match;
    if (KEY && !keyMatches(key)) return refuse(socket, 'key');
    if (command === 'MANIFEST') return sendUpdateFile(socket, 'manifest.json', 8192);
    if (command === 'DOWNLOAD') return sendUpdateFile(socket, 'Ponte.exe', MAX_UPDATE_BYTES);
    if (command === 'ANDROID_MANIFEST' && argument === '0') return sendUpdateFile(socket, 'android-manifest.json', 8192);
    if (command === 'ANDROID_DOWNLOAD' && argument === '0') return sendUpdateFile(socket, 'Ponte.apk', MAX_UPDATE_BYTES);
    if (command === 'VIDEO' && /^[0-9a-f]{64}$/.test(argument)) return streamUpdateFile(socket, `video-${argument}.br`, MAX_VIDEO_BYTES);
    if (command === 'HOST' && /^\d{9}$/.test(argument)) {
      hostHandler = registerHost(socket, argument);
    } else if (command === 'CONNECT' && /^\d{9}$/.test(argument)) {
      socket.setTimeout(0);
      connectViewer(socket, argument, takeRest);
    } else if (command === 'ACCEPT' && /^[0-9a-f]{32}$/.test(argument)) {
      socket.setTimeout(0);
      acceptFromHost(socket, argument, takeRest);
    } else {
      socket.destroy();
    }
  });
});

// ---------- Ponte no navegador ----------
//
// O navegador não abre conexões TCP; ele fala WebSocket. Aqui cada WebSocket vira um "socket" como o
// dos outros visualizadores: a primeira mensagem é "PONTE CONNECT <id>", e depois os bytes passam
// direto para o computador chamado, já cifrados de ponta a ponta pela própria página. Só CONNECT e
// STATUS (a bolinha de online dos salvos) são aceitos por aqui, e sem a chave do servidor: ela ficaria
// exposta numa página pública, e quem conecta ainda precisa de senha, aceite ou autorização no computador.

class WebSocketPeer extends Duplex {
  constructor(socket) {
    super();
    this.socket = socket;
    this.buffer = Buffer.alloc(0);
    this.fragments = [];
    this.onMessage = null;
    socket.setNoDelay(true);
    socket.on('data', (chunk) => this.receive(chunk));
    socket.on('close', () => this.destroy());
    socket.on('error', () => this.destroy());
    this.ping = setInterval(() => this.frame(0x9, Buffer.alloc(0)), WS_PING_MS);
  }

  frame(opcode, payload) {
    if (this.socket.destroyed) return true;
    let header;
    if (payload.length < 126) {
      header = Buffer.from([0x80 | opcode, payload.length]);
    } else if (payload.length < 65536) {
      header = Buffer.alloc(4);
      header[0] = 0x80 | opcode;
      header[1] = 126;
      header.writeUInt16BE(payload.length, 2);
    } else {
      header = Buffer.alloc(10);
      header[0] = 0x80 | opcode;
      header[1] = 127;
      header.writeBigUInt64BE(BigInt(payload.length), 2);
    }
    return this.socket.write(Buffer.concat([header, payload]));
  }

  receive(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 2) {
      const first = this.buffer[0], second = this.buffer[1];
      const opcode = first & 0x0f, fin = (first & 0x80) !== 0, masked = (second & 0x80) !== 0;
      let length = second & 0x7f, offset = 2;
      if (length === 126) {
        if (this.buffer.length < 4) return;
        length = this.buffer.readUInt16BE(2);
        offset = 4;
      } else if (length === 127) {
        if (this.buffer.length < 10) return;
        const big = this.buffer.readBigUInt64BE(2);
        if (big > BigInt(WS_MAX_MESSAGE)) return this.destroy();
        length = Number(big);
        offset = 10;
      }
      // O navegador sempre mascara o que manda; sem máscara não é um navegador seguindo a norma.
      if (!masked || length > WS_MAX_MESSAGE) return this.destroy();
      if (this.buffer.length < offset + 4 + length) return;
      const mask = this.buffer.subarray(offset, offset + 4);
      const payload = Buffer.from(this.buffer.subarray(offset + 4, offset + 4 + length));
      for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i & 3];
      this.buffer = this.buffer.subarray(offset + 4 + length);

      if (opcode === 0x8) return this.destroy();
      if (opcode === 0x9) { this.frame(0xa, payload); continue; }
      if (opcode === 0xa) continue;
      this.fragments.push(payload);
      const total = this.fragments.reduce((sum, part) => sum + part.length, 0);
      if (total > WS_MAX_MESSAGE) return this.destroy();
      if (!fin) continue;
      const message = Buffer.concat(this.fragments);
      this.fragments = [];
      if (this.onMessage) this.onMessage(message);
      else this.push(message);
    }
  }

  _read() {}

  _write(chunk, encoding, callback) {
    const data = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk, encoding);
    // Respostas de linha ("OK", "ERR ...") vão como texto; o resto, como binário.
    const text = data.length < 64 && /^(OK|ERR [a-z]+)\n$/.test(data.toString('latin1'));
    if (this.frame(text ? 0x1 : 0x2, data)) callback();
    else this.socket.once('drain', () => callback());
  }

  _final(callback) {
    this.frame(0x8, Buffer.from([0x03, 0xe8]));
    this.socket.end();
    callback();
  }

  _destroy(error, callback) {
    clearInterval(this.ping);
    this.socket.destroy();
    callback(error);
  }
}

function acceptWebSocket(request, socket) {
  const key = request.headers['sec-websocket-key'];
  if ((request.headers.upgrade || '').toLowerCase() !== 'websocket' || !key || request.headers['sec-websocket-version'] !== '13') {
    socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n');
    return;
  }
  // Atrás do nginx, o endereço de verdade vem no cabeçalho que ele põe.
  const direct = socket.remoteAddress || '';
  const address = (direct === '127.0.0.1' || direct === '::1' || direct === '::ffff:127.0.0.1') && request.headers['x-real-ip']
    ? String(request.headers['x-real-ip']) : direct;
  const count = (perAddress.get(address) || 0) + 1;
  perAddress.set(address, count);
  socket.on('close', () => {
    const left = (perAddress.get(address) || 1) - 1;
    if (left > 0) perAddress.set(address, left);
    else perAddress.delete(address);
  });
  if (count > MAX_PER_ADDRESS) return socket.destroy();

  const accept = crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n' +
    `Sec-WebSocket-Accept: ${accept}\r\n\r\n`);
  const peer = new WebSocketPeer(socket);
  peer.on('error', () => peer.destroy());
  const deadline = setTimeout(() => peer.destroy(), HANDSHAKE_MS);
  peer.onMessage = (message) => {
    peer.onMessage = null;
    clearTimeout(deadline);
    const text = message.toString('latin1');
    const status = /^PONTE STATUS (\d{9}(?:,\d{9}){0,99})\n?$/.exec(text);
    if (status) return peer.end(Buffer.from(onlineLine(status[1]), 'latin1'));
    const match = /^PONTE CONNECT (\d{9})(?: [A-Za-z0-9_-]{1,128})?\n?$/.exec(text);
    if (!match) return peer.destroy();
    connectViewer(peer, match[1], () => Buffer.alloc(0));
  };
}

if (WS_PORT > 0) {
  const web = http.createServer((request, response) => {
    response.writeHead(426, { 'Content-Type': 'text/plain; charset=utf-8', Connection: 'close' });
    response.end('Este endereço é só para o Ponte no navegador.');
  });
  web.on('upgrade', (request, socket) => {
    socket.on('error', () => socket.destroy());
    acceptWebSocket(request, socket);
  });
  web.on('clientError', (error, socket) => socket.destroy());
  web.on('error', (error) => {
    console.error(`Não foi possível escutar o WebSocket em ${WS_BIND}:${WS_PORT}: ${error.message}`);
    process.exit(1);
  });
  web.listen(WS_PORT, WS_BIND, () => console.log(`Ponte: WebSocket para o navegador em ${WS_BIND}:${WS_PORT}`));
}

server.maxConnections = MAX_CONNECTIONS;
server.on('error', (error) => {
  console.error(`Não foi possível escutar em ${BIND}:${PORT}: ${error.message}`);
  process.exit(1);
});
server.listen(PORT, BIND, () => console.log(`Ponte: servidor de retransmissao em ${BIND}:${PORT}, chave ${KEY ? 'exigida' : 'nao exigida'}`));
