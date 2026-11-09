# A conta

Desde a **1.17.0** o Ponte só funciona com uma conta liberada pelo dono do Ponte, **dos dois lados**:
em quem acessa e no aparelho acessado. Sem conta, o programa abre, mostra o endereço e deixa mudar as
configurações, mas não acessa ninguém nem deixa ser acessado.

Este documento vale para a 1.17.4 do Windows, a 1.17.1 do Android e a 1.17.0 do navegador. O formato
exato do bilhete está em [protocolo.md](protocolo.md); o que ele protege, em [seguranca.md](seguranca.md).

## 1. Por que existe

Antes da 1.17.0 qualquer pessoa com o `Ponte.exe` em mãos podia usar o servidor de retransmissão do
dono. A chave do servidor está embutida no executável, então ela nunca foi um segredo. A conta troca
esse obstáculo por uma lista explícita: **só quem o dono liberou usa o Ponte.**

A conta não dá acesso a computador nenhum. Quem pode entrar em cada máquina continua sendo decidido
lá, pelo aceite na tela, pela senha ou pela lista de autorizados.

## 2. Qual conta

É a mesma conta do **FilaBox** (`filabox3d.com.br`), pelo Supabase dele. Dá para entrar de dois jeitos:

- **E-mail e senha**, na janela "Conta - Ponte".
- **Entrar com o Google**: o Ponte abre o navegador padrão na página do Google (pelo Supabase) e
  espera o retorno numa porta deste computador (53682). Depois de entrar no Google, volte para o Ponte.

Na mesma janela há **Criar conta no FilaBox** e **Esqueci a senha**, que abrem as páginas do site.

> Quando o Ponte está aberto como administrador, o navegador é aberto pela área de trabalho do
> Windows, para que ele abra como o usuário comum e não como administrador. Até a 1.17.3 isso era
> feito por `explorer.exe`, e o endereço longo do login com Google acabava abrindo o Explorador de
> Arquivos; a 1.17.4 corrigiu (`DesktopShell.cs`).

A conta também guarda a **lista de computadores salvos**, que assim aparece em qualquer aparelho em
que a pessoa entrar. Cada conta só enxerga a própria lista, por regra do banco.

## 3. O bilhete

O Ponte não pergunta ao servidor a cada conexão se a conta é válida. Em vez disso:

1. O programa manda ao servidor de retransmissão o resumo da chave deste aparelho e o token de acesso
   da conta (`PONTE TICKET <sha256 da chave>` e, na linha seguinte, o token).
2. O servidor pergunta ao Supabase do FilaBox se aquela conta está liberada.
3. Se estiver, ele assina um **bilhete**: *"a chave com este resumo é da conta tal, válida até tal dia"*.

O bilhete vale **14 dias** e é renovado quando faltam **7**. Ele fica gravado na configuração
(`Ticket`).

Numa sessão, cada lado manda o bilhete e confere o do outro: o computador acessado confere o de quem
acessa (vem no `Hello`), e quem acessa confere o do computador acessado (vem no `Challenge`).

Duas consequências úteis:

- **A conexão direta por IP também exige conta**, mesmo sem internet para falar com o servidor, desde
  que os dois bilhetes ainda estejam no prazo.
- **Fora do ar o servidor, quem já tem bilhete continua trabalhando** até o bilhete vencer.

A chave pública que confere a assinatura dos bilhetes está embutida no executável (`Ticket.IssuerKey`).
A privada existe só no servidor, na variável `PONTE_TICKET_KEY`.

## 4. O botão da conta

Na janela principal, embaixo de "Computador remoto":

| O que aparece | Significa |
|---|---|
| **Entrar na conta (obrigatório)...** | Ninguém entrou neste computador. O Ponte não acessa nem é acessado. |
| O e-mail, em preto | Conta liberada e bilhete no prazo. |
| O e-mail, em vermelho | Entrou, mas o bilhete não vale: conta não liberada, bilhete vencido ou servidor fora do ar. |

Clicando no botão com a conta já dentro, o menu traz:

- **Conferir a conta de novo** (só quando o bilhete não vale): pede um bilhete novo na hora.
- **Contas liberadas do Ponte...** (só para o administrador): a lista, com liberar e remover.
- **Sincronizar a lista agora**: puxa e manda os computadores salvos da conta.
- **Criar ou gerenciar a conta no FilaBox**: abre o site.
- **Sair da conta**.

## 5. A lista de liberados

Fica no Supabase do FilaBox, na tabela `public.ponte_liberados` (`supabase/ponte-liberados.sql`).

| Coluna | Conteúdo |
|---|---|
| `email` | O e-mail, sempre em minúsculas. É a chave. |
| `admin` | Administrador da lista: só ele vê, libera e remove. |
| `liberado_em` | Quando entrou. |
| `liberado_por` | Quem liberou. |

Hoje estão liberados o dono (`dyegofreeride2@gmail.com`, administrador) e `3dcartola@gmail.com`.

A tabela tem RLS ligada **sem nenhuma política**, e as permissões foram revogadas de `anon` e
`authenticated`: ninguém lê nem grava direto. Tudo passa por funções `security definer`:

| Função | Para quem | O que faz |
|---|---|---|
| `ponte_email()` | qualquer conta | O e-mail confirmado de quem chamou, ou nulo. |
| `ponte_conta()` | qualquer conta | `{email, liberado, admin}` de quem chamou. É o que o servidor usa. |
| `ponte_admin()` | qualquer conta | Se quem chamou é administrador. |
| `ponte_lista()` | administrador | A lista inteira. |
| `ponte_liberar(email)` | administrador | Libera um e-mail. |
| `ponte_remover(email)` | administrador | Tira da lista. |

Uma conta só conta como liberada se o **e-mail estiver confirmado** no Supabase.

### Liberar alguém

1. Peça à pessoa que crie a conta no FilaBox e confirme o e-mail.
2. No Ponte, com a conta de administrador, clique no botão da conta → **Contas liberadas do Ponte...**
3. Digite o e-mail e libere.

Também dá para rodar `select public.ponte_liberar('fulano@exemplo.com');` no SQL Editor do Supabase,
logado como o administrador.

### Tirar alguém

Remover da lista impede **bilhetes novos**. O bilhete que a pessoa já tem continua valendo até vencer
(no máximo 14 dias). Para cortar na hora, remova também o aparelho dela de "Computadores autorizados"
em cada computador.

## 6. Mensagens que aparecem

| Mensagem | Causa |
|---|---|
| "A conta X ainda não foi liberada para usar o Ponte." | O e-mail não está na lista, ou não foi confirmado. |
| "A conta saiu deste computador. Entre de novo." | O token de renovação venceu ou foi usado em outro lugar. |
| "Sem conexão com o servidor do Ponte para conferir a conta." | Servidor fora do ar ou sem internet. Com bilhete no prazo, o Ponte continua funcionando. |
| "O servidor do Ponte ainda não confere contas (falta atualizar o servidor)." | O `relay.js` do servidor é anterior à 1.17.0, ou está sem `PONTE_TICKET_KEY`. |
| "bilhete da conta de outro aparelho" | O `config.json` foi copiado de outro computador. Entre na conta de novo. |
| "Sem conta, o Ponte não acessa outros aparelhos nem pode ser acessado." | Ninguém entrou. |

## 7. Onde isso está no código

| Arquivo | Papel |
|---|---|
| `Account.cs` | A conta no Supabase do FilaBox: login por e-mail/senha e por Google, renovação do token, lista de computadores salvos. |
| `AccountDialog.cs` | A janela "Conta - Ponte". |
| `Ticket.cs` | Formato, assinatura e conferência do bilhete; pedido ao servidor. |
| `DesktopShell.cs` | Abre o navegador como o usuário quando o Ponte está como administrador (1.17.4). |
| `relay/relay.js` | O comando `TICKET`: consulta o Supabase e assina. |
| `supabase/ponte-liberados.sql` | A tabela e as funções. |
| `SelfTestAccount.cs` | Autoteste da conta, contra um Supabase falso nesta máquina. |
| `web/conta.js` | A mesma conta no Ponte do navegador. |
