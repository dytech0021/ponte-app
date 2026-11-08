// Conta opcional, a mesma do FilaBox (login pelo Supabase dele, com e-mail ou Google), como no Account.kt do
// Android: guarda a lista de computadores salvos de cada pessoa, para ela aparecer em qualquer aparelho em que a
// pessoa entrar. Cada conta só enxerga a própria lista (regra do banco). Nada aqui dá acesso a computador nenhum:
// a permissão de entrar continua em cada computador acessado.

export const SITE = 'https://filabox3d.com.br';
/** Chave pública do Supabase do FilaBox: a mesma que o site entrega a qualquer navegador. */
const PUBLIC_KEY = 'sb_publishable_S0LPicuJdLg-XsrxGiN0Ug_tIDZ4sLo';
const TABLE = 'ponte_computadores';

// Só a página aberta nesta própria máquina (testes) pode apontar outro servidor da conta, por ?conta=.
let BASE = (() => {
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(globalThis.location?.hostname);
  const asked = local ? new URLSearchParams(location.search).get('conta') : null;
  return asked && /^https?:\/\//.test(asked) ? asked.replace(/\/$/, '') : 'https://kobtvgqnitdeviddvymm.supabase.co';
})();

/** Só os testes do Node trocam, por um servidor falso nesta máquina. */
export function useServerForTests(url) {
  BASE = url;
}

/** Falha da conta, com a mensagem pronta para mostrar. */
export class AccountError extends Error {}

const strange = () => new AccountError('O servidor da conta respondeu de um jeito inesperado.');

async function http(method, path, { bearer, body, headers = {} } = {}) {
  let response;
  try {
    response = await fetch(BASE + path, {
      method,
      headers: {
        apikey: PUBLIC_KEY,
        ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}),
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        ...headers,
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
      cache: 'no-store',
      credentials: 'omit',
      signal: AbortSignal.timeout(30000),
    });
  } catch {
    throw new AccountError('Sem conexão com o servidor da conta.');
  }
  return { status: response.status, text: await response.text().catch(() => '') };
}

function parse(text) {
  try { return JSON.parse(text); } catch { return null; }
}

// As mensagens do Supabase vêm em inglês; as comuns viram português.
function authError(text) {
  const json = parse(text) || {};
  const code = String(json.error_code || json.error || '');
  const message = String(json.msg || json.error_description || json.message || '');
  if (code === 'invalid_credentials' || (code === 'invalid_grant' && /credentials/i.test(message))) return 'E-mail ou senha incorretos.';
  if (code === 'email_not_confirmed') return 'Confirme o e-mail da conta (o link chegou na sua caixa de entrada) e tente de novo.';
  if (['refresh_token_not_found', 'refresh_token_already_used', 'session_not_found'].includes(code) ||
    (code === 'validation_failed' && /refresh token/i.test(message))) return 'A conta saiu deste aparelho. Entre de novo.';
  if (code === 'over_request_rate_limit') return 'Muitas tentativas seguidas. Espere um pouco e tente de novo.';
  if (code === 'flow_state_not_found' || code === 'flow_state_expired') return 'O login com Google demorou demais. Tente de novo.';
  return message ? `A conta recusou: ${message.slice(0, 200)}` : 'A conta recusou o login.';
}

async function token(grant, body) {
  const { status, text } = await http('POST', `/auth/v1/token?grant_type=${grant}`, { body });
  if (status < 200 || status > 299) throw new AccountError(authError(text));
  return parse(text) || (() => { throw strange(); })();
}

const base64Url = (bytes) => btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

export class Account {
  constructor(email, userId, refresh) {
    this.email = email;
    this.userId = userId;
    this.refreshToken = refresh;
    this.access = '';
    this.expires = 0;
    this.refreshing = null;
    /** O token de renovação mudou (o Supabase troca a cada renovação) e precisa ser gravado de novo. */
    this.onTokenChanged = null;
  }

  static async signIn(email, password) {
    return Account.fromSession(await token('password', { email: email.trim(), password }));
  }

  /** Volta a conta gravada neste aparelho (sem pedir login de novo). */
  static restore(email, userId, refresh) {
    return userId && refresh ? new Account(email, userId, refresh) : null;
  }

  static fromSession(session) {
    const user = session?.user;
    if (!user) throw new AccountError('O servidor da conta não informou quem entrou.');
    const account = new Account(String(user.email || ''), String(user.id || ''), String(session.refresh_token || ''));
    account.access = String(session.access_token || '');
    account.expires = Date.now() + Math.max(60, (session.expires_in | 0 || 3600) - 60) * 1000;
    if (!account.userId || !account.access) throw strange();
    return account;
  }

  /**
   * Começo do login com Google: um segredo que fica neste aparelho (PKCE) e o endereço do Google. O Google volta
   * para returnUrl com ?code=, que vai para finishGoogle junto com o segredo.
   */
  static async startGoogle(returnUrl) {
    const verifier = base64Url(crypto.getRandomValues(new Uint8Array(32)));
    const challenge = base64Url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))));
    const url = `${BASE}/auth/v1/authorize?provider=google&redirect_to=${encodeURIComponent(returnUrl)}` +
      `&code_challenge=${challenge}&code_challenge_method=s256`;
    return { url, verifier };
  }

  static async finishGoogle(code, verifier) {
    return Account.fromSession(await token('pkce', { auth_code: code, code_verifier: verifier }));
  }

  // ---------- lista de computadores ----------

  async list() {
    const { status, text } = await this.authorized('GET', `/rest/v1/${TABLE}?select=chave,endereco,nome,auto_acesso`);
    check(status, text);
    const rows = parse(text);
    if (!Array.isArray(rows)) throw strange();
    return rows.filter((r) => r && typeof r === 'object').map((r) => ({
      key: String(r.chave || ''), address: String(r.endereco || ''), name: String(r.nome || ''), autoAccess: !!r.auto_acesso,
    }));
  }

  async upsert(computers) {
    if (computers.length === 0) return;
    const rows = computers.map((c) => ({ user_id: this.userId, chave: c.key, endereco: c.address, nome: c.name, auto_acesso: !!c.autoAccess }));
    const { status, text } = await this.authorized('POST', `/rest/v1/${TABLE}?on_conflict=user_id,chave`, {
      body: rows, headers: { Prefer: 'resolution=merge-duplicates,return=minimal' },
    });
    check(status, text);
  }

  async delete(key) {
    const { status, text } = await this.authorized('DELETE', `/rest/v1/${TABLE}?chave=eq.${encodeURIComponent(key)}`);
    check(status, text);
  }

  async signOut() {
    try { await this.authorized('POST', '/auth/v1/logout?scope=local'); } catch { /* saiu daqui de qualquer jeito */ }
  }

  /** Token de acesso atual (renovado se preciso), para o servidor do Ponte conferir a conta. */
  async accessToken() {
    await this.ensureAccess();
    return this.access;
  }

  async ensureAccess() {
    if (Date.now() < this.expires && this.access) return;
    // Duas chamadas ao mesmo tempo usariam o mesmo token de renovação, e a segunda seria recusada.
    this.refreshing ??= (async () => {
      try {
        const session = await token('refresh_token', { refresh_token: this.refreshToken });
        this.access = String(session.access_token || '');
        this.refreshToken = String(session.refresh_token || '');
        this.expires = Date.now() + Math.max(60, (session.expires_in | 0 || 3600) - 60) * 1000;
        if (!this.access || !this.refreshToken) throw strange();
        await this.onTokenChanged?.(this);
      } finally {
        this.refreshing = null;
      }
    })();
    await this.refreshing;
  }

  async authorized(method, path, options = {}) {
    await this.ensureAccess();
    return http(method, path, { ...options, bearer: this.access });
  }
}

function check(status, text) {
  if (status >= 200 && status <= 299) return;
  throw new AccountError(status === 404 ? 'A conta ainda não está pronta no servidor (falta a tabela do Ponte).'
    : `O servidor da conta recusou o pedido (${status}). ${text.slice(0, 200)}`);
}
