// A conta do Ponte no navegador (conta.js) contra um Supabase falso nesta máquina. Rodar com: node --test web/test/
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { Account, AccountError, useServerForTests } from '../conta.js';
import { startFakeSupabase, USUARIO } from './supabase-falso.mjs';

const fake = await startFakeSupabase();
useServerForTests(fake.url);
after(() => fake.close());

test('senha errada é recusada em português', async () => {
  await assert.rejects(Account.signIn(USUARIO.email, 'errada'), (e) => e instanceof AccountError && e.message === 'E-mail ou senha incorretos.');
});

test('entrar, gravar, listar e apagar computadores', async () => {
  const account = await Account.signIn(` ${USUARIO.email} `, USUARIO.senha);
  assert.equal(account.email, USUARIO.email);
  await account.upsert([
    { key: 'id:460087520', address: '460 087 520', name: 'Escritório', autoAccess: true },
    { key: 'id:123456789', address: '123 456 789', name: 'Casa', autoAccess: false },
  ]);
  await account.upsert([{ key: 'id:123456789', address: '123 456 789', name: 'Casa nova', autoAccess: true }]);
  let list = await account.list();
  assert.deepEqual(list.map((c) => [c.key, c.name, c.autoAccess]).sort(), [['id:123456789', 'Casa nova', true], ['id:460087520', 'Escritório', true]]);
  await account.delete('id:460087520');
  list = await account.list();
  assert.deepEqual(list.map((c) => c.key), ['id:123456789']);
});

test('token vencido é renovado uma vez só, mesmo com pedidos ao mesmo tempo, e o novo é avisado', async () => {
  const account = await Account.signIn(USUARIO.email, USUARIO.senha);
  const changed = [];
  account.onTokenChanged = (a) => { changed.push(a.refreshToken); };
  const old = account.refreshToken;
  account.expires = 0;
  await Promise.all([account.list(), account.list(), account.list()]);
  assert.equal(changed.length, 1);
  assert.notEqual(changed[0], old);
  // O token antigo não serve mais (o Supabase troca a cada renovação).
  const stale = Account.restore(USUARIO.email, USUARIO.id, old);
  await assert.rejects(stale.list(), (e) => e.message === 'A conta saiu deste aparelho. Entre de novo.');
  // O novo, gravado, volta a conta sem pedir login.
  const restored = Account.restore(USUARIO.email, USUARIO.id, changed[0]);
  assert.ok(Array.isArray(await restored.list()));
});

test('login com Google por PKCE: só o segredo certo troca o código', async () => {
  const { url, verifier } = await Account.startGoogle('https://filabox3d.com.br/ponte/');
  assert.match(url, /\/auth\/v1\/authorize\?provider=google&redirect_to=https%3A%2F%2Ffilabox3d\.com\.br%2Fponte%2F&code_challenge=[\w-]{43}&code_challenge_method=s256$/);
  const back = new URL((await fetch(url, { redirect: 'manual', headers: { apikey: 'sb_publishable_S0LPicuJdLg-XsrxGiN0Ug_tIDZ4sLo' } })).headers.get('location'));
  assert.equal(back.origin + back.pathname, 'https://filabox3d.com.br/ponte/');
  const code = back.searchParams.get('code');
  await assert.rejects(Account.finishGoogle(code, 'segredo-errado'), AccountError);
  const again = await Account.startGoogle('https://filabox3d.com.br/ponte/');
  const code2 = new URL((await fetch(again.url, { redirect: 'manual', headers: { apikey: 'sb_publishable_S0LPicuJdLg-XsrxGiN0Ug_tIDZ4sLo' } })).headers.get('location')).searchParams.get('code');
  const account = await Account.finishGoogle(code2, again.verifier);
  assert.equal(account.email, USUARIO.email);
  assert.ok(verifier.length >= 43);
});

test('sair encerra a sessão no servidor', async () => {
  const account = await Account.signIn(USUARIO.email, USUARIO.senha);
  await account.signOut();
  await assert.rejects(Account.restore(USUARIO.email, USUARIO.id, account.refreshToken).list(), AccountError);
});
