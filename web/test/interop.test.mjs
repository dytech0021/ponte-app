// O Ponte no navegador contra um Ponte do Windows de verdade, passando por um servidor de retransmissão
// com WebSocket. Só roda pelo web\testar-com-o-pc.ps1, que sobe os dois e passa por variáveis de ambiente:
//   PONTE_TESTE_WS     endereço do WebSocket do servidor (ws://127.0.0.1:porta)
//   PONTE_TESTE_ID     ID do computador de teste
//   PONTE_TESTE_SENHA  senha de acesso dele
// O computador de teste mostra o aviso de sessão ativa; nada é clicado nem digitado lá além da tecla F24.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { connect, newIdentity, PonteError, MouseAction } from '../ponte.js';

const relayUrl = process.env.PONTE_TESTE_WS || '';
const id = process.env.PONTE_TESTE_ID || '';
const password = process.env.PONTE_TESTE_SENHA || '';
const skip = !relayUrl && 'sem PONTE_TESTE_WS: teste contra o Ponte do Windows pulado';

function options(identity, answers = {}) {
  return {
    relayUrl, identity, deviceName: 'teste-navegador', userName: 'node', version: '1.6.0',
    knownFingerprint: answers.known ?? null, remember: !!answers.remember,
    askPassword: async () => { answers.asked = true; return answers.password ?? null; },
    confirmNewIdentity: async () => false,
  };
}

// Lê a sessão até a primeira imagem (e, se pedido, até uma segunda tela inteira) e encerra.
async function firstFrames(session, wantSecond) {
  const frames = [];
  let resolveDone;
  const done = new Promise((resolve) => { resolveDone = resolve; });
  let closedReason = null;
  const running = session.run({
    onFrame(frame) {
      frames.push(frame);
      if (frames.length === 1 && wantSecond) wantSecond();
      const whole = frame.tiles.length === 1 && frame.tiles[0].width === frame.width && frame.tiles[0].height === frame.height;
      if ((!wantSecond && frames.length >= 1) || (wantSecond && frames.length >= 2 && whole)) resolveDone();
    },
    onClosed(reason) { closedReason = reason; resolveDone(); },
  });
  const timer = setTimeout(resolveDone, 20000);
  await done;
  clearTimeout(timer);
  session.close();
  await running;
  return { frames, closedReason };
}

const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

test('senha errada é recusada e a certa abre a sessão com imagem', { skip }, async () => {
  const identity = await newIdentity();
  await assert.rejects(connect(id, options(identity, { password: 'senha-errada' })), (e) => e instanceof PonteError && e.message === 'Senha incorreta.');

  const session = await connect(id, options(identity, { password }));
  console.log(`conectado a ${session.hostName}, Ponte ${session.hostVersion}, telas ${session.screens.map((s) => `${s.Width}x${s.Height}`)}`);
  assert.ok(session.hostName.length > 0);
  assert.ok(!session.paired, 'sem pedir, o navegador não pode ficar autorizado');
  assert.equal(session.fingerprint.length, 64);
  const primary = session.screens.find((s) => s.Primary);
  const { frames, closedReason } = await firstFrames(session, () => {
    // Trocar a qualidade faz o outro lado mandar a tela inteira de novo: prova que o que vai daqui chega.
    session.sendSettings(primary.Index, 35);
    session.sendKey(0x87, false);
    session.sendKey(0x87, true);
    session.sendMouse(0, 0, MouseAction.WHEEL, 0);
  });
  assert.ok(frames.length >= 2, `chegaram ${frames.length} quadro(s)`);
  const tile = frames[0].tiles[0];
  console.log(`primeira imagem: ${frames[0].width}x${frames[0].height}, JPEG de ${tile.jpeg.length} bytes`);
  assert.equal(frames[0].width, primary.Width);
  assert.deepEqual([tile.jpeg[0], tile.jpeg[1]], [0xff, 0xd8], 'o retângulo não começa como JPEG');
  assert.equal(closedReason, '');
  await pause(800);
});

test('identidade diferente da guardada é percebida', { skip }, async () => {
  const identity = await newIdentity();
  await assert.rejects(connect(id, options(identity, { password, known: '00'.repeat(32) })), (e) => e.message === 'Conexão cancelada.');
  await pause(500);
});

test('ID que não existe não conecta', { skip }, async () => {
  const other = id.endsWith('0') ? id.slice(0, 8) + '1' : id.slice(0, 8) + '0';
  await assert.rejects(connect(other, options(await newIdentity(), { password })), (e) => e.message === 'Esse computador não está online.');
});

test('entrar com a senha pedindo autorização e depois entrar direto', { skip }, async () => {
  const identity = await newIdentity();
  const first = await connect(id, options(identity, { password, remember: true }));
  assert.ok(first.paired, 'entrar com a senha pedindo autorização deveria autorizar');
  const fingerprint = first.fingerprint;
  await firstFrames(first);
  await pause(800);

  const answers = { known: fingerprint };
  const direct = await connect(id, options(identity, answers));
  assert.ok(!answers.asked, 'navegador autorizado não deveria ser perguntado pela senha');
  assert.ok(direct.paired);
  await firstFrames(direct);
  await pause(800);

  const stranger = {};
  await assert.rejects(connect(id, options(await newIdentity(), stranger)));
  assert.ok(stranger.asked, 'outra chave tem de ser perguntada pela senha');
});
