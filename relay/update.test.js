'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const net = require('node:net');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { once } = require('node:events');

const routes = [
  ['ANDROID_MANIFEST', 'android-manifest.json', 8192],
  ['ANDROID_DOWNLOAD', 'Ponte.apk', 64 * 1024 * 1024],
  ['MANIFEST', 'manifest.json', 8192],
  ['DOWNLOAD', 'Ponte.exe', 64 * 1024 * 1024],
];

async function startRelay(t, key, extraEnv = {}) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'ponte-relay-test-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const reservation = net.createServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const wsReservation = net.createServer();
  wsReservation.listen(0, '127.0.0.1');
  await once(wsReservation, 'listening');
  const wsPort = wsReservation.address().port;
  await new Promise(resolve => wsReservation.close(resolve));
  const child = spawn(process.execPath, [path.join(__dirname, 'relay.js'), String(port)], {
    env: { ...process.env, PONTE_BIND: '127.0.0.1', PONTE_KEY: key, PONTE_UPDATE_DIR: directory, PONTE_WS_PORT: String(wsPort), ...extraEnv },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) {
      const closed = once(child, 'close');
      child.kill();
      await closed;
    }
  });
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Relay did not start')), 5000);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`Relay exited: ${code}`)); });
    child.stdout.once('data', () => { clearTimeout(timer); resolve(); });
  });
  return {
    directory,
    port,
    wsPort,
    request(command, suppliedKey = key) {
      return new Promise((resolve, reject) => {
        const chunks = [];
        const socket = net.connect(port, '127.0.0.1', () => {
          socket.write(`PONTE ${command} 0${suppliedKey ? ` ${suppliedKey}` : ''}\n`);
        });
        socket.setTimeout(15000, () => socket.destroy(new Error('Response timeout')));
        socket.on('data', chunk => chunks.push(chunk));
        socket.on('error', reject);
        socket.on('end', () => resolve(Buffer.concat(chunks)));
      });
    },
  };
}

function assertPayload(response, expected) {
  const newline = response.indexOf(10);
  assert.equal(response.subarray(0, newline + 1).toString(), `OK ${expected.length}\n`);
  assert.deepEqual(response.subarray(newline + 1), expected);
}

test('authenticated local relay update protocol', { timeout: 60000 }, async t => {
  const relay = await startRelay(t, 'integration_test_key');
  const fixtures = [
    Buffer.from('{"versionCode":3,"versionName":"1.5.0","size":4,"sha256":"' + 'a'.repeat(64) + '"}'),
    Buffer.from([0x50, 0x4b, 0x00, 0xff]),
    Buffer.from('{"version":"1.5.0","platform":"windows"}'),
    Buffer.from([0x4d, 0x5a, 0xff, 0x00, 0x01]),
  ];
  for (let i = 0; i < routes.length; i++) {
    await fs.writeFile(path.join(relay.directory, routes[i][1]), fixtures[i]);
  }
  for (let i = 0; i < routes.length; i++) {
    const [command, file, limit] = routes[i];
    await t.test(`${command} returns only its own file with byte length framing`, async () => {
      assertPayload(await relay.request(command), fixtures[i]);
    });
    await t.test(`${command} rejects incorrect and omitted keys`, async () => {
      assert.equal((await relay.request(command, 'wrong_key')).toString(), 'ERR key\n');
      assert.equal((await relay.request(command, '')).toString(), 'ERR key\n');
    });
    await t.test(`${command} reports absent files without falling back to another platform`, async () => {
      await fs.unlink(path.join(relay.directory, file));
      assert.equal((await relay.request(command)).toString(), 'ERR none\n');
    });
    await t.test(`${command} rejects empty and oversized files`, async () => {
      await fs.writeFile(path.join(relay.directory, file), Buffer.alloc(0));
      assert.equal((await relay.request(command)).toString(), 'ERR none\n');
      await fs.truncate(path.join(relay.directory, file), limit + 1);
      assert.equal((await relay.request(command)).toString(), 'ERR none\n');
    });
    await t.test(`${command} accepts exactly the maximum byte length`, async () => {
      const boundary = Buffer.alloc(limit, 0xa5);
      await fs.writeFile(path.join(relay.directory, file), boundary);
      assertPayload(await relay.request(command), boundary);
      await fs.writeFile(path.join(relay.directory, file), fixtures[i]);
    });
  }
});

test('relay without a configured key serves all update routes', { timeout: 15000 }, async t => {
  const relay = await startRelay(t, '');
  for (const [command, file] of routes) {
    const payload = Buffer.from(`contents of ${file}\n`, 'utf8');
    await fs.writeFile(path.join(relay.directory, file), payload);
    assertPayload(await relay.request(command), payload);
  }
});

// Uma linha qualquer para o servidor e tudo o que ele responder até fechar.
function ask(port, line) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    const socket = net.connect(port, '127.0.0.1', () => socket.write(`${line}\n`));
    socket.setTimeout(15000, () => socket.destroy(new Error('Response timeout')));
    socket.on('data', chunk => chunks.push(chunk));
    socket.on('error', reject);
    socket.on('end', () => resolve(Buffer.concat(chunks).toString()));
  });
}

// Registra um computador como o Ponte faz: HOST com o ID, e a prova assinada com a chave da qual o ID saiu.
async function registerHost(port, key) {
  const crypto = require('node:crypto');
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const spki = publicKey.export({ format: 'der', type: 'spki' });
  const hash = crypto.createHash('sha256').update(spki).digest();
  const id = String(100000000n + (hash.readBigUInt64BE(0) % 900000000n));
  const socket = net.connect(port, '127.0.0.1');
  await once(socket, 'connect');
  socket.write(`PONTE HOST ${id} ${key}\n`);
  let buffer = '';
  const nextLine = () => new Promise(resolve => {
    const onData = chunk => {
      buffer += chunk.toString();
      const end = buffer.indexOf('\n');
      if (end < 0) return;
      socket.removeListener('data', onData);
      const line = buffer.slice(0, end);
      buffer = buffer.slice(end + 1);
      resolve(line);
    };
    socket.on('data', onData);
    onData(Buffer.alloc(0));
  });
  const nonce = (await nextLine()).split(' ')[1];
  const signature = crypto.sign('sha256', Buffer.from(nonce, 'hex'), { key: privateKey, dsaEncoding: 'ieee-p1363' });
  socket.write(`PROOF ${spki.toString('base64')} ${signature.toString('base64')}\n`);
  assert.equal(await nextLine(), 'OK');
  return { id, socket, nextLine };
}

test('STATUS says which saved computers are online, only with the key', { timeout: 15000 }, async t => {
  const relay = await startRelay(t, 'integration_test_key');
  const host = await registerHost(relay.port, 'integration_test_key');
  const offline = host.id === '123456789' ? '987654321' : '123456789';
  assert.equal(await ask(relay.port, `PONTE STATUS ${host.id},${offline} integration_test_key`), 'STATUS 10\n');
  assert.equal(await ask(relay.port, `PONTE STATUS ${offline} integration_test_key`), 'STATUS 0\n');
  assert.equal(await ask(relay.port, `PONTE STATUS ${host.id} wrong_key`), 'ERR key\n');
  assert.equal(await ask(relay.port, `PONTE STATUS ${host.id}`), 'ERR key\n');

  // Fechou o Ponte (ou caiu a rede): sai do registro e passa a aparecer offline.
  host.socket.destroy();
  await new Promise(resolve => setTimeout(resolve, 200));
  assert.equal(await ask(relay.port, `PONTE STATUS ${host.id} integration_test_key`), 'STATUS 0\n');

  // Mais de 100 IDs de uma vez, ou lista malformada: o servidor só fecha a conexão.
  const tooMany = Array.from({ length: 101 }, () => offline).join(',');
  assert.equal(await ask(relay.port, `PONTE STATUS ${tooMany} integration_test_key`), '');
  assert.equal(await ask(relay.port, `PONTE STATUS 12345,678 integration_test_key`), '');
});

// O Ponte no navegador pergunta pelo WebSocket, sem a chave (ela ficaria exposta numa página pública).
function askWebSocket(port, line) {
  return new Promise(resolve => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}/ponte/ws`);
    ws.binaryType = 'arraybuffer';
    let answer = '';
    ws.onopen = () => ws.send(line);
    ws.onmessage = event => { answer += typeof event.data === 'string' ? event.data : Buffer.from(event.data).toString(); };
    ws.onclose = () => resolve(answer);
    // Conexão cortada sem resposta também vale: o resultado é a resposta vazia.
    ws.onerror = () => {};
  });
}

test('STATUS over WebSocket answers the page without the key', { timeout: 15000 }, async t => {
  const relay = await startRelay(t, 'integration_test_key');
  const host = await registerHost(relay.port, 'integration_test_key');
  const offline = host.id === '123456789' ? '987654321' : '123456789';
  assert.equal(await askWebSocket(relay.wsPort, `PONTE STATUS ${offline},${host.id}\n`), 'STATUS 01\n');
  // Pelo WebSocket só STATUS e CONNECT: o resto fecha sem resposta.
  assert.equal(await askWebSocket(relay.wsPort, 'PONTE MANIFEST 0 integration_test_key\n'), '');
  host.socket.destroy();
});

async function freePort() {
  const reservation = net.createServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  return port;
}

// O que o MediaMTX pergunta ao relay antes de deixar transmitir ou assistir.
async function mediamtxAsks(port, request) {
  const response = await fetch(`http://127.0.0.1:${port}/mediamtx/auth`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request),
  });
  return response.status;
}

test('screen broadcast: code for a registered computer, publish only with its password, watch with the code', { timeout: 20000 }, async t => {
  // Uma API do MediaMTX de mentira, com dois espectadores no caminho.
  const api = require('node:http').createServer((request, response) => {
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ name: request.url, readers: [{ type: 'webRTCSession' }, { type: 'hlsMuxer' }] }));
  });
  api.listen(0, '127.0.0.1');
  await once(api, 'listening');
  t.after(() => api.close());
  const authPort = await freePort();
  const relay = await startRelay(t, 'integration_test_key', {
    PONTE_AUTH_PORT: String(authPort), PONTE_MEDIAMTX_API: `http://127.0.0.1:${api.address().port}`,
  });
  await new Promise(resolve => setTimeout(resolve, 200));
  const host = await registerHost(relay.port, 'integration_test_key');
  const crypto = require('node:crypto');
  const password = 'senha-de-transmissao-do-teste';
  const hash = crypto.createHash('sha256').update(password).digest('hex');

  host.socket.write(`TRANSMITIR ${hash}\n`);
  const [, token] = /^TRANSMISSAO ([A-Za-z0-9]{24})$/.exec(await host.nextLine()) || [];
  assert.ok(token, 'o relay devolve um código de 24 letras e números');
  const path = `ao/${token}`;

  assert.equal(await mediamtxAsks(authPort, { action: 'publish', path, user: 'ponte', password }), 200);
  assert.equal(await mediamtxAsks(authPort, { action: 'publish', path, user: 'ponte', password: 'errada' }), 401);
  assert.equal(await mediamtxAsks(authPort, { action: 'publish', path, user: '', password: '' }), 401);
  assert.equal(await mediamtxAsks(authPort, { action: 'read', path, user: '', password: '' }), 200);
  assert.equal(await mediamtxAsks(authPort, { action: 'read', path: 'ao/AAAAAAAAAAAAAAAAAAAAAAAA', user: '', password: '' }), 401);
  assert.equal(await mediamtxAsks(authPort, { action: 'read', path: 'outro/caminho', user: '', password: '' }), 401);
  assert.equal(await mediamtxAsks(authPort, { action: 'playback', path, user: '', password: '' }), 401);
  assert.equal(await mediamtxAsks(authPort, { action: 'api', ip: '127.0.0.1' }), 200);
  assert.equal(await mediamtxAsks(authPort, { action: 'api', ip: '203.0.113.9' }), 401);

  host.socket.write(`ESPECTADORES ${token}\n`);
  assert.equal(await host.nextLine(), `ESPECTADORES ${token} 2`);

  // Pedir de novo com o mesmo código (depois de reconectar) mantém o link.
  host.socket.write(`TRANSMITIR ${hash} ${token}\n`);
  assert.equal(await host.nextLine(), `TRANSMISSAO ${token}`);

  // O APK pela página de baixar: só com o código de uma transmissão no ar.
  const apk = async (uri) => (await fetch(`http://127.0.0.1:${authPort}/ponte/apk`, { headers: { 'X-Original-URI': uri } })).status;
  assert.equal(await apk(`/ponte/baixar/Ponte.apk?t=${token}`), 204);
  assert.equal(await apk('/ponte/baixar/Ponte.apk?t=AAAAAAAAAAAAAAAAAAAAAAAA'), 403);
  assert.equal(await apk('/ponte/baixar/Ponte.apk'), 403);

  // Parar tira o código do ar; quem tentar assistir depois é recusado.
  host.socket.write(`PARAR ${token}\n`);
  await new Promise(resolve => setTimeout(resolve, 100));
  assert.equal(await mediamtxAsks(authPort, { action: 'read', path, user: '', password: '' }), 401);
  assert.equal(await apk(`/ponte/baixar/Ponte.apk?t=${token}`), 403);

  // Uma transmissão nova, e o computador saindo do ar: o código também deixa de valer.
  host.socket.write(`TRANSMITIR ${hash}\n`);
  const [, second] = /^TRANSMISSAO ([A-Za-z0-9]{24})$/.exec(await host.nextLine()) || [];
  assert.ok(second && second !== token);
  assert.equal(await mediamtxAsks(authPort, { action: 'read', path: `ao/${second}`, user: '', password: '' }), 200);
  host.socket.destroy();
  await new Promise(resolve => setTimeout(resolve, 200));
  assert.equal(await mediamtxAsks(authPort, { action: 'read', path: `ao/${second}`, user: '', password: '' }), 401);
});
