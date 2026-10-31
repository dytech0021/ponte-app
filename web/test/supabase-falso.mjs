// Um Supabase falso, só com o que a conta do Ponte usa: login por senha, renovação (o token muda a cada uma),
// Google por PKCE, sair e a tabela ponte_computadores de cada usuário. Serve aos testes do Node (conta.test.mjs)
// e, rodado sozinho (node supabase-falso.mjs porta), à página aberta nesta máquina com ?conta=http://127.0.0.1:porta.
import http from 'node:http';
import crypto from 'node:crypto';

export const USUARIO = { email: 'teste@ponte.local', senha: 'senha-de-teste', id: '00000000-0000-4000-8000-000000000001' };

export function startFakeSupabase(port = 0) {
  const rows = [];               // { user_id, chave, endereco, nome, auto_acesso }
  const refreshTokens = new Map();   // token -> user id
  const accessTokens = new Map();
  const codes = new Map();       // código do Google -> desafio PKCE
  const log = [];

  function session() {
    const access = crypto.randomUUID(), refresh = crypto.randomUUID();
    accessTokens.set(access, USUARIO.id);
    refreshTokens.set(refresh, USUARIO.id);
    return { access_token: access, refresh_token: refresh, expires_in: 3600, user: { id: USUARIO.id, email: USUARIO.email } };
  }

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://x');
    const send = (status, body, headers = {}) => {
      res.writeHead(status, { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Headers': 'apikey,authorization,content-type,prefer',
        'Access-Control-Allow-Methods': 'GET,POST,DELETE,OPTIONS', 'Content-Type': 'application/json', ...headers });
      res.end(body === undefined ? '' : JSON.stringify(body));
    };
    if (req.method === 'OPTIONS') return send(200);
    let text = '';
    for await (const chunk of req) text += chunk;
    const body = text ? JSON.parse(text) : null;
    log.push(`${req.method} ${url.pathname}${url.search}`);
    // A página do Google é aberta pelo navegador, sem a chave (como no Supabase de verdade); o resto exige.
    if (url.pathname !== '/auth/v1/authorize' && req.headers.apikey !== 'sb_publishable_S0LPicuJdLg-XsrxGiN0Ug_tIDZ4sLo') return send(401, { message: 'sem apikey' });

    if (url.pathname === '/auth/v1/token') {
      const grant = url.searchParams.get('grant_type');
      if (grant === 'password') {
        if (body.email !== USUARIO.email || body.password !== USUARIO.senha) return send(400, { error_code: 'invalid_credentials', msg: 'Invalid login credentials' });
        return send(200, session());
      }
      if (grant === 'refresh_token') {
        if (!refreshTokens.delete(body.refresh_token)) return send(400, { error_code: 'refresh_token_already_used', msg: 'Invalid Refresh Token: Already Used' });
        return send(200, session());
      }
      if (grant === 'pkce') {
        const challenge = codes.get(body.auth_code);
        codes.delete(body.auth_code);
        const digest = crypto.createHash('sha256').update(body.code_verifier || '').digest('base64url');
        if (!challenge || digest !== challenge) return send(404, { error_code: 'flow_state_not_found', msg: 'invalid flow state' });
        return send(200, session());
      }
      return send(400, { msg: 'grant desconhecido' });
    }
    // O "Google" daqui aprova na hora e volta com o código.
    if (url.pathname === '/auth/v1/authorize') {
      const code = crypto.randomUUID();
      codes.set(code, url.searchParams.get('code_challenge'));
      const back = new URL(url.searchParams.get('redirect_to'));
      back.searchParams.set('code', code);
      res.writeHead(303, { Location: back.href });
      return res.end();
    }

    const user = accessTokens.get(String(req.headers.authorization || '').replace(/^Bearer /, ''));
    if (!user) return send(401, { message: 'JWT expired' });
    if (url.pathname === '/auth/v1/logout') {
      accessTokens.clear();
      refreshTokens.clear();
      return send(204);
    }
    if (url.pathname === '/rest/v1/ponte_computadores') {
      if (req.method === 'GET') return send(200, rows.filter((r) => r.user_id === user).map(({ chave, endereco, nome, auto_acesso }) => ({ chave, endereco, nome, auto_acesso })));
      if (req.method === 'POST') {
        for (const r of body) {
          if (r.user_id !== user) return send(403, { message: 'RLS' });
          const existing = rows.find((x) => x.user_id === user && x.chave === r.chave);
          if (existing) Object.assign(existing, r);
          else rows.push({ ...r });
        }
        return send(201);
      }
      if (req.method === 'DELETE') {
        const key = (url.searchParams.get('chave') || '').replace(/^eq\./, '');
        for (let i = rows.length - 1; i >= 0; i--) if (rows[i].user_id === user && rows[i].chave === key) rows.splice(i, 1);
        return send(204);
      }
    }
    send(404, { message: 'não existe' });
  });
  return new Promise((resolve) => server.listen(port, '127.0.0.1', () => resolve({
    url: `http://127.0.0.1:${server.address().port}`, rows, log, refreshTokens, accessTokens, close: () => server.close(),
  })));
}

if (process.argv[1] && import.meta.url.endsWith(process.argv[1].replace(/\\/g, '/').split('/').pop())) {
  const fake = await startFakeSupabase(Number(process.argv[2] || 0));
  console.log(`Supabase falso em ${fake.url}`);
}
