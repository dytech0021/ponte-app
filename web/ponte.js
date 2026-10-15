// Ponte no navegador: o protocolo do Ponte (o mesmo do Windows e do Android), sobre WebSocket e com o
// canal cifrado de ponta a ponta descrito em WebChannel.cs. Roda no navegador e no Node (testes), só
// com o que os dois já têm: WebSocket, crypto.subtle, TextEncoder.

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const subtle = globalThis.crypto.subtle;

export class PonteError extends Error {}

export const Msg = {
  HELLO: 1, CHALLENGE: 2, AUTH_RESPONSE: 3, AUTH_RESULT: 4,
  FRAME: 10, CURSOR: 11, AUDIO: 12, CURSOR_POS: 13,
  MOUSE: 20, KEY: 21, TEXT: 22,
  CLIPBOARD: 30, SETTINGS: 50, NOTICE: 60, BYE: 99,
};

export const MouseAction = { MOVE: 0, LEFT_DOWN: 1, LEFT_UP: 2, RIGHT_DOWN: 3, RIGHT_UP: 4, MIDDLE_DOWN: 5, MIDDLE_UP: 6, WHEEL: 7 };

/** O que o navegador sabe fazer além do básico: desenhar o ponteiro que vem do computador. */
export const Caps = { FILES: 1, CURSOR: 2, AUDIO: 4 };

// ---------- utilidades ----------

export function concat(...parts) {
  const total = parts.reduce((sum, p) => sum + p.length, 0);
  const out = new Uint8Array(total);
  let offset = 0;
  for (const p of parts) {
    out.set(p, offset);
    offset += p.length;
  }
  return out;
}

export function toBase64(bytes) {
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

export function fromBase64(text) {
  const raw = atob(text);
  const out = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
  return out;
}

export function hex(bytes) {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
}

export async function sha256(...parts) {
  return new Uint8Array(await subtle.digest('SHA-256', concat(...parts)));
}

/** ID de 9 dígitos derivado da chave pública (SPKI), a mesma conta do Windows. */
export async function idFromSpki(spki) {
  const hash = await sha256(spki);
  let value = 0n;
  for (let i = 0; i < 8; i++) value = (value << 8n) | BigInt(hash[i]);
  return String(100000000n + (value % 900000000n));
}

export function formatId(id) {
  return id.length === 9 ? `${id.slice(0, 3)} ${id.slice(3, 6)} ${id.slice(6)}` : id;
}

/** O navegador só alcança os computadores pelo servidor, então o destino é sempre um ID. */
export function parseTarget(text) {
  const trimmed = String(text).trim();
  const digits = trimmed.replace(/\D/g, '');
  if (digits.length !== 9 || !/^[\d -]+$/.test(trimmed)) return null;
  return { id: digits, key: `id:${digits}`, shown: formatId(digits) };
}

async function pbkdf2(password, salt, iterations) {
  const key = await subtle.importKey('raw', encoder.encode(password), 'PBKDF2', false, ['deriveBits']);
  return new Uint8Array(await subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt, iterations }, key, 256));
}

async function hmacSha256(key, data) {
  const k = await subtle.importKey('raw', key, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await subtle.sign('HMAC', k, data));
}

/** Prova de que quem conecta conhece a senha, amarrada ao certificado do computador. */
export async function passwordProof(password, salt, iterations, nonce, certificateHash) {
  return hmacSha256(await pbkdf2(password, salt, iterations), concat(nonce, certificateHash));
}

/** Mensagem que o visualizador assina para provar que é dono da própria chave. */
export function pairingMessage(nonce, certificateHash) {
  return concat(encoder.encode('ponte-autorizacao\n'), nonce, certificateHash);
}

// Lê um elemento DER (tipo, tamanho, conteúdo) a partir de offset.
function der(bytes, offset) {
  const tag = bytes[offset];
  let length = bytes[offset + 1];
  let header = 2;
  if (length & 0x80) {
    const count = length & 0x7f;
    if (count < 1 || count > 3) throw new PonteError('Certificado inválido.');
    length = 0;
    for (let i = 0; i < count; i++) length = (length << 8) | bytes[offset + 2 + i];
    header += count;
  }
  if (offset + header + length > bytes.length) throw new PonteError('Certificado inválido.');
  return { tag, start: offset, content: offset + header, end: offset + header + length };
}

/** A chave pública (SPKI) de dentro de um certificado X.509. */
export function spkiFromCertificate(certificate) {
  const outer = der(certificate, 0);
  const tbs = der(certificate, outer.content);
  let item = der(certificate, tbs.content);
  if (item.tag === 0xa0) item = der(certificate, item.end);   // versão
  // número de série, algoritmo, emissor, validade, titular: a chave pública vem logo depois
  for (let i = 0; i < 5; i++) item = der(certificate, item.end);
  if (item.tag !== 0x30) throw new PonteError('Certificado inválido.');
  return certificate.slice(item.start, item.end);
}

// ---------- identidade deste navegador ----------

/** Cria uma identidade em memória (testes). O aplicativo guarda a dele no IndexedDB. */
export async function newIdentity(extractable = false) {
  const pair = await subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, extractable, ['sign', 'verify']);
  return identityFromPair(pair);
}

export async function identityFromPair(pair) {
  const spki = new Uint8Array(await subtle.exportKey('spki', pair.publicKey));
  return {
    spki,
    pair,
    async sign(data) {
      // O navegador já entrega r||s, o formato que o Ponte do Windows confere.
      return new Uint8Array(await subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, pair.privateKey, data));
    },
  };
}

// ---------- transporte: WebSocket com fila de bytes ----------

class ByteQueue {
  constructor() {
    this.chunks = [];
    this.length = 0;
    this.waiter = null;
    this.closed = null;
  }

  push(bytes) {
    this.chunks.push(bytes);
    this.length += bytes.length;
    this.wake();
  }

  close(reason) {
    this.closed = reason || 'A conexão com o outro computador caiu.';
    this.wake();
  }

  wake() {
    if (this.waiter) {
      const w = this.waiter;
      this.waiter = null;
      w();
    }
  }

  async read(count) {
    while (this.length < count) {
      if (this.closed !== null) throw new PonteError(this.closed);
      await new Promise((resolve) => { this.waiter = resolve; });
    }
    const out = new Uint8Array(count);
    let filled = 0;
    while (filled < count) {
      const first = this.chunks[0];
      const take = Math.min(first.length, count - filled);
      out.set(first.subarray(0, take), filled);
      filled += take;
      if (take === first.length) this.chunks.shift();
      else this.chunks[0] = first.subarray(take);
    }
    this.length -= count;
    return out;
  }
}

function relayError(reply) {
  switch (reply) {
    case 'ERR offline': return 'Esse computador não está online.';
    case 'ERR timeout': return 'O outro computador não respondeu a tempo.';
    case 'ERR busy': return 'O servidor de retransmissão está ocupado. Tente de novo.';
    default: return `O servidor de retransmissão recusou a conexão (${reply}).`;
  }
}

async function openRelay(url, id) {
  const ws = new WebSocket(url);
  ws.binaryType = 'arraybuffer';
  const queue = new ByteQueue();
  let firstLine = null;
  const lineArrived = new Promise((resolve, reject) => {
    firstLine = { resolve, reject };
  });
  ws.onmessage = (event) => {
    if (firstLine) {
      const text = typeof event.data === 'string' ? event.data : decoder.decode(event.data);
      const line = firstLine;
      firstLine = null;
      line.resolve(text.replace(/\r?\n$/, ''));
      return;
    }
    queue.push(new Uint8Array(event.data));
  };
  ws.onclose = () => {
    if (firstLine) {
      firstLine.reject(new PonteError('Não foi possível falar com o servidor de retransmissão.'));
      firstLine = null;
    }
    queue.close();
  };
  ws.onerror = () => {};
  await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    const failed = () => reject(new PonteError('Não foi possível falar com o servidor de retransmissão.'));
    ws.addEventListener('close', failed, { once: true });
    ws.addEventListener('open', () => ws.removeEventListener('close', failed), { once: true });
  });
  ws.send(`PONTE CONNECT ${id}\n`);
  const reply = await withTimeout(lineArrived, 30000, 'O outro computador não respondeu a tempo.');
  if (reply !== 'OK') {
    ws.close();
    throw new PonteError(relayError(reply));
  }
  return { ws, queue };
}

function withTimeout(promise, ms, message) {
  let timer;
  return Promise.race([
    promise,
    new Promise((_, reject) => { timer = setTimeout(() => reject(new PonteError(message)), ms); }),
  ]).finally(() => clearTimeout(timer));
}

// ---------- canal cifrado (lado do navegador de WebChannel.cs) ----------

class SecureChannel {
  constructor(ws, queue, sendKey, receiveKey) {
    this.ws = ws;
    this.queue = queue;
    this.sendKey = sendKey;
    this.receiveKey = receiveKey;
    this.sendCounter = 0n;
    this.receiveCounter = 0n;
    this.plain = new ByteQueue();
    this.sending = Promise.resolve();
    this.limit = 8 * 1024;
    this.pump();
  }

  static nonce(counter) {
    const nonce = new Uint8Array(12);
    new DataView(nonce.buffer).setBigUint64(4, counter);
    return nonce;
  }

  /**
   * Aperto de mão. Confere o ID (pela chave do certificado) e a assinatura do computador antes de
   * qualquer coisa sair daqui. Devolve o canal, o certificado e o resumo dele.
   */
  static async open(ws, queue, expectedId, knownFingerprint, confirmNewIdentity) {
    const ephemeral = await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
    const ownPoint = new Uint8Array(await subtle.exportKey('raw', ephemeral.publicKey));
    const clientHello = concat(encoder.encode('WPONTE1'), ownPoint, globalThis.crypto.getRandomValues(new Uint8Array(32)));
    ws.send(clientHello);

    const serverPoint = await queue.read(65);
    const lengthBytes = await queue.read(2);
    const certLength = (lengthBytes[0] << 8) | lengthBytes[1];
    if (certLength < 32 || certLength > 8192) throw new PonteError('O outro computador respondeu fora do esperado.');
    const certificate = await queue.read(certLength);
    const signature = await queue.read(64);

    const spki = spkiFromCertificate(certificate);
    if ((await idFromSpki(spki)) !== expectedId)
      throw new PonteError('A identidade do computador remoto não confere com o ID digitado. A conexão foi recusada.');
    const certificateHash = await sha256(certificate);
    const identity = await subtle.importKey('spki', spki, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
    const signed = concat(encoder.encode('ponte-web-1\n'), await sha256(clientHello), serverPoint, certificateHash);
    if (!(await subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, identity, signature, signed)))
      throw new PonteError('O outro computador não provou a identidade dele. A conexão foi recusada.');
    const fingerprint = hex(certificateHash);
    if (knownFingerprint && knownFingerprint !== fingerprint && !(await confirmNewIdentity()))
      throw new PonteError('Conexão cancelada.');

    const serverKey = await subtle.importKey('raw', serverPoint, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    const secret = await subtle.deriveBits({ name: 'ECDH', public: serverKey }, ephemeral.privateKey, 256);
    const base = await subtle.importKey('raw', secret, 'HKDF', false, ['deriveKey']);
    const salt = await sha256(clientHello, serverPoint, lengthBytes, certificate, signature);
    const key = (info, usage) => subtle.deriveKey(
      { name: 'HKDF', hash: 'SHA-256', salt, info: encoder.encode(info) }, base, { name: 'AES-GCM', length: 256 }, false, [usage]);
    const channel = new SecureChannel(ws, queue, await key('ponte-web-1 c2s', 'encrypt'), await key('ponte-web-1 s2c', 'decrypt'));
    return { channel, certificateHash, fingerprint };
  }

  // Decifra os registros na ordem em que chegam.
  async pump() {
    try {
      for (;;) {
        const header = await this.queue.read(4);
        const length = new DataView(header.buffer).getUint32(0);
        if (length < 16 || length > (1 << 20) + 16) throw new PonteError('O outro computador mandou um registro inválido.');
        const cipher = await this.queue.read(length);
        const plain = await subtle.decrypt({ name: 'AES-GCM', iv: SecureChannel.nonce(this.receiveCounter++), tagLength: 128 }, this.receiveKey, cipher);
        this.plain.push(new Uint8Array(plain));
      }
    } catch (error) {
      this.plain.close(error instanceof PonteError ? error.message : 'A conexão com o outro computador caiu.');
    }
  }

  /** Envia uma mensagem do Ponte (tipo, tamanho, conteúdo) como um registro cifrado. */
  send(type, payload = new Uint8Array(0)) {
    const message = new Uint8Array(5 + payload.length);
    message[0] = type;
    new DataView(message.buffer).setUint32(1, payload.length, true);
    message.set(payload, 5);
    // Cifrar é assíncrono; a corrente de promessas mantém a ordem e o contador certos.
    this.sending = this.sending.then(async () => {
      if (this.ws.readyState !== 1) return;
      const iv = SecureChannel.nonce(this.sendCounter++);
      const cipher = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv, tagLength: 128 }, this.sendKey, message));
      const record = new Uint8Array(4 + cipher.length);
      new DataView(record.buffer).setUint32(0, cipher.length);
      record.set(cipher, 4);
      if (this.ws.readyState === 1) this.ws.send(record);
    }).catch(() => {});
    return this.sending;
  }

  sendJson(type, value) {
    return this.send(type, encoder.encode(JSON.stringify(value)));
  }

  /** Próxima mensagem: { type, payload }. Lança quando a conexão acaba. */
  async read() {
    const header = await this.plain.read(5);
    const length = new DataView(header.buffer).getUint32(1, true);
    if (length > this.limit) throw new PonteError('O outro computador enviou uma mensagem com tamanho inválido.');
    return { type: header[0], payload: await this.plain.read(length) };
  }

  async expectJson(type) {
    const message = await this.read();
    if (message.type !== type) throw new PonteError('O outro computador respondeu fora do esperado.');
    try {
      return JSON.parse(decoder.decode(message.payload));
    } catch {
      throw new PonteError('O outro computador respondeu fora do esperado.');
    }
  }
}

// ---------- conexão e sessão ----------

/**
 * Conecta e autentica. options: { relayUrl, deviceName, userName, version, identity, knownFingerprint,
 * remember, askPassword(): Promise<string|null> ("" pede permissão, null desiste), confirmNewIdentity(): Promise<boolean> }.
 */
export async function connect(targetText, options) {
  const target = parseTarget(targetText);
  if (!target) throw new PonteError('Use o ID de 9 dígitos do computador. No navegador não dá para conectar pelo IP.');
  const { ws, queue } = await openRelay(options.relayUrl, target.id);
  try {
    const { channel, certificateHash, fingerprint } = await SecureChannel.open(ws, queue, target.id, options.knownFingerprint, options.confirmNewIdentity);
    await channel.sendJson(Msg.HELLO, {
      Name: options.deviceName, User: options.userName, Version: options.version,
      Key: toBase64(options.identity.spki), Caps: Caps.CURSOR,
    });
    const challenge = await withTimeout(channel.expectJson(Msg.CHALLENGE), 20000, 'O outro computador não respondeu a tempo.');
    const nonce = fromBase64(challenge.Nonce || '');
    if (nonce.length !== 32) throw new PonteError('O outro computador enviou um desafio inválido.');
    // A assinatura vai sempre: é ela que deixa o outro lado reconhecer este navegador, agora ou depois.
    const signature = toBase64(await options.identity.sign(pairingMessage(nonce, certificateHash)));

    let proof = null;
    if (challenge.HasPassword && !challenge.Trusted) {
      const password = await options.askPassword();
      if (password === null || password === undefined) throw new PonteError('Conexão cancelada.');
      if (password.length > 0) {
        const iterations = challenge.Iterations | 0;
        if (iterations < 1000 || iterations > 2000000) throw new PonteError('O outro computador enviou parâmetros de senha inválidos.');
        proof = toBase64(await passwordProof(password, fromBase64(challenge.Salt || ''), iterations, nonce, certificateHash));
      }
    }
    await channel.sendJson(Msg.AUTH_RESPONSE, { Proof: proof, Signature: signature, Remember: !!options.remember });
    const result = await withTimeout(channel.expectJson(Msg.AUTH_RESULT), 75000, 'O outro computador não respondeu a tempo.');
    if (!result.Ok) throw new PonteError(result.Reason || 'Conexão recusada.');
    channel.limit = 64 * 1024 * 1024;
    return new Session(channel, target, result, fingerprint);
  } catch (error) {
    ws.close();
    throw error instanceof PonteError ? error : new PonteError('A conexão caiu durante a identificação.');
  }
}

export class Session {
  constructor(channel, target, result, fingerprint) {
    this.channel = channel;
    this.target = target;
    this.hostName = String(result.HostName || '');
    this.screens = Array.isArray(result.Screens) ? result.Screens : [];
    this.paired = !!result.Paired;
    this.hostVersion = String(result.Version || '').slice(0, 20);
    this.hostCaps = result.Caps | 0;
    this.fingerprint = fingerprint;
    this.closedLocally = false;
    this.acceptsText = versionAtLeast(this.hostVersion, 1, 3);
  }

  /** Lê até a sessão acabar. listener: onFrame, onPointer, onPointerPosition, onClipboard, onNotice, onClosed. */
  async run(listener) {
    let reason = 'O outro computador encerrou a sessão.';
    try {
      for (;;) {
        const { type, payload } = await this.channel.read();
        if (type === Msg.FRAME) listener.onFrame?.(parseFrame(payload));
        else if (type === Msg.CURSOR) listener.onPointer?.(parsePointer(payload));
        else if (type === Msg.CURSOR_POS && payload.length >= 5) {
          const view = new DataView(payload.buffer, payload.byteOffset);
          listener.onPointerPosition?.(view.getUint16(1, true), view.getUint16(3, true), (payload[0] & 1) === 1);
        } else if (type === Msg.CLIPBOARD && payload.length <= 256 * 1024) listener.onClipboard?.(decoder.decode(payload));
        else if (type === Msg.NOTICE && payload.length <= 2048) listener.onNotice?.(decoder.decode(payload));
        else if (type === Msg.BYE) break;
      }
    } catch (error) {
      reason = error instanceof PonteError ? error.message : 'A conexão com o outro computador caiu.';
    }
    const local = this.closedLocally;
    this.close();
    listener.onClosed?.(local ? '' : reason);
  }

  sendMouse(x, y, action, wheel = 0) {
    const p = new Uint8Array(7);
    const view = new DataView(p.buffer);
    view.setUint16(0, Math.max(0, Math.min(65535, Math.round(x))), true);
    view.setUint16(2, Math.max(0, Math.min(65535, Math.round(y))), true);
    p[4] = action;
    view.setInt16(5, Math.max(-32768, Math.min(32767, wheel | 0)), true);
    this.channel.send(Msg.MOUSE, p);
  }

  sendKey(virtualKey, up, extended = false) {
    const p = new Uint8Array(3);
    new DataView(p.buffer).setUint16(0, virtualKey, true);
    p[2] = (up ? 1 : 0) | (extended ? 2 : 0);
    this.channel.send(Msg.KEY, p);
  }

  /** Texto pronto para o computador digitar, em pedaços que cabem numa mensagem e sem partir emoji. */
  sendText(text) {
    let start = 0;
    while (start < text.length) {
      let end = Math.min(text.length, start + 500);
      const code = text.charCodeAt(end - 1);
      if (end < text.length && code >= 0xd800 && code <= 0xdbff) end--;
      this.channel.send(Msg.TEXT, encoder.encode(text.slice(start, end)));
      start = end;
    }
  }

  sendSettings(screen, quality) {
    this.channel.sendJson(Msg.SETTINGS, { Screen: screen, Quality: quality, Audio: false });
  }

  sendClipboard(text) {
    this.channel.send(Msg.CLIPBOARD, encoder.encode(text));
  }

  close() {
    if (this.closedLocally) return;
    this.closedLocally = true;
    this.channel.send(Msg.BYE).finally(() => this.channel.ws.close());
  }
}

export function versionAtLeast(version, major, minor) {
  const parts = String(version).split('.').map((p) => parseInt(p, 10));
  if (parts.length < 2 || parts.some(Number.isNaN)) return false;
  return parts[0] > major || (parts[0] === major && parts[1] >= minor);
}

/** Quadro: largura, altura e quantidade (2 bytes cada); por retângulo x, y, w, h (2 bytes), tamanho (4) e o JPEG. */
export function parseFrame(p) {
  if (p.length < 6) throw new PonteError('O outro computador enviou uma imagem incompleta.');
  const view = new DataView(p.buffer, p.byteOffset, p.byteLength);
  const width = view.getUint16(0, true), height = view.getUint16(2, true), count = view.getUint16(4, true);
  if (!width || !height) throw new PonteError('O outro computador enviou uma imagem sem dimensões.');
  const tiles = [];
  let offset = 6;
  for (let i = 0; i < count; i++) {
    if (offset + 12 > p.length) throw new PonteError('O outro computador enviou uma imagem incompleta.');
    const x = view.getUint16(offset, true), y = view.getUint16(offset + 2, true);
    const w = view.getUint16(offset + 4, true), h = view.getUint16(offset + 6, true);
    const length = view.getInt32(offset + 8, true);
    offset += 12;
    if (length <= 0 || length > p.length - offset || x + w > width || y + h > height)
      throw new PonteError('O outro computador enviou uma imagem incompleta.');
    tiles.push({ x, y, width: w, height: h, jpeg: p.subarray(offset, offset + length) });
    offset += length;
  }
  return { width, height, tiles };
}

/** Ponteiro: 1 byte (bit 0 = visível), ponto ativo x e y (2 bytes cada) e a imagem em PNG. Null = ponteiro escondido. */
export function parsePointer(p) {
  if (p.length <= 5 || (p[0] & 1) === 0) return null;
  const view = new DataView(p.buffer, p.byteOffset, p.byteLength);
  return { hotX: view.getUint16(1, true), hotY: view.getUint16(3, true), png: p.subarray(5) };
}
