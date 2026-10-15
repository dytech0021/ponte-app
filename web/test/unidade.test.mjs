// Contas do núcleo do Ponte no navegador, conferidas com valores de outra implementação (hashlib do
// Python, os mesmos dos testes do Android). Rodar com: node --test web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  idFromSpki, formatId, parseTarget, passwordProof, pairingMessage, parseFrame, parsePointer, versionAtLeast,
  toBase64, fromBase64, newIdentity, PonteError,
} from '../ponte.js';

const enc = new TextEncoder();
const hex = (bytes) => Buffer.from(bytes).toString('hex');

test('ID sai da chave pública', async () => {
  assert.equal(await idFromSpki(enc.encode('ponte')), '817984555');
  assert.equal(await idFromSpki(Uint8Array.from({ length: 91 }, (_, i) => i)), '307488558');
  assert.equal(formatId('817984555'), '817 984 555');
});

test('prova da senha confere com a referência', async () => {
  const proof = await passwordProof('password', enc.encode('salt'), 2, new Uint8Array(32), new Uint8Array(32).fill(7));
  assert.equal(hex(proof), '395ac04ed09a60394468f3dca26dc27f30e64e330014121b3fc630abe8d92938');
});

test('destino: só ID de 9 dígitos', () => {
  assert.deepEqual(parseTarget(' 460 087-520 '), { id: '460087520', key: 'id:460087520', shown: '460 087 520' });
  assert.equal(parseTarget('192.168.0.50'), null);
  assert.equal(parseTarget('1234567890'), null);
});

test('assinatura do navegador sai em r||s e confere', async () => {
  const identity = await newIdentity();
  assert.equal(identity.spki.length, 91);
  const message = pairingMessage(new Uint8Array(32).fill(1), new Uint8Array(32).fill(2));
  const signature = await identity.sign(message);
  assert.equal(signature.length, 64);
  assert.ok(await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, identity.pair.publicKey, signature, message));
});

test('quadro de imagem e ponteiro', () => {
  const u16 = (v) => [v & 0xff, v >> 8];
  const jpeg = [0xff, 0xd8, 0xff];
  const frame = parseFrame(Uint8Array.from([...u16(1920), ...u16(1080), ...u16(1), ...u16(64), ...u16(128), ...u16(32), ...u16(16), 3, 0, 0, 0, ...jpeg]));
  assert.equal(frame.width, 1920);
  assert.deepEqual([frame.tiles[0].x, frame.tiles[0].y, frame.tiles[0].width, frame.tiles[0].height], [64, 128, 32, 16]);
  assert.deepEqual(Array.from(frame.tiles[0].jpeg), jpeg);
  assert.throws(() => parseFrame(Uint8Array.from([...u16(100), ...u16(100), ...u16(1), ...u16(90), ...u16(0), ...u16(32), ...u16(16), 3, 0, 0, 0, ...jpeg])), PonteError);
  const pointer = parsePointer(Uint8Array.from([1, ...u16(3), ...u16(4), 0x89, 0x50]));
  assert.deepEqual([pointer.hotX, pointer.hotY, pointer.png.length], [3, 4, 2]);
  assert.equal(parsePointer(Uint8Array.from([0, 0, 0, 0, 0, 1])), null);
});

test('versão e base64', () => {
  assert.ok(versionAtLeast('1.5.5', 1, 3));
  assert.ok(!versionAtLeast('1.2.1', 1, 3));
  assert.ok(!versionAtLeast('', 1, 3));
  const bytes = Uint8Array.from({ length: 70000 }, (_, i) => i & 0xff);
  assert.deepEqual(fromBase64(toBase64(bytes)), bytes);
});
