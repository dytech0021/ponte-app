# Protocolo de rede

Este documento descreve o que trafega entre dois aparelhos com o Ponte e entre eles e o servidor
de retransmissão. Vale para a versão 1.17.4. O código de referência está em `Transport.cs`,
`Protocol.cs`, `Ticket.cs`, `WebChannel.cs`, `HostService.cs`, `ViewerSession.cs` e `relay/relay.js`.
O aplicativo Android fala o mesmo protocolo; o código dele está em `android/core`. O Ponte do
navegador também, trocando o TLS pelo canal da seção 3.2.

Termos usados aqui: **visualizador** é quem acessa; **controlado** é quem é acessado.

## 1. Camadas

```
mensagens do Ponte (tipo + tamanho + conteúdo)
TLS 1.2 ou 1.3 (programas) ou WPONTE1 (navegador), de ponta a ponta entre os dois aparelhos
[só por ID] uma linha de texto para o servidor de retransmissão, que depois só repassa bytes
TCP (ou WebSocket, para o navegador)
```

Há dois caminhos para chegar à cifra:

- **Direto:** o visualizador abre TCP na porta do controlado (padrão 7411) e começa o TLS.
- **Por ID:** os dois abrem conexões de saída para o servidor de retransmissão (padrão 7412), que
  junta as duas e passa a copiar os bytes de uma para a outra. A cifra começa depois disso e é
  negociada entre os dois aparelhos, não com o servidor.

## 2. Servidor de retransmissão

### 2.1 Primeira linha

Toda conexão ao servidor começa com uma linha de texto ASCII terminada em `\n`:

```
PONTE <COMANDO> <argumento> [chave]
```

| Comando | Argumento | Quem envia | Finalidade |
|---|---|---|---|
| `HOST` | ID de 9 dígitos | controlado | Registrar-se como alcançável por aquele ID. |
| `CONNECT` | ID de 9 dígitos | visualizador | Pedir conexão ao aparelho com aquele ID. |
| `ACCEPT` | token de 32 dígitos hexadecimais | controlado | Atender um pedido, numa conexão nova. |
| `TICKET` | SHA-256 da chave do aparelho | qualquer cópia | Pedir o bilhete da conta. *Desde a 1.17.0.* |
| `MANIFEST` | `0` | qualquer cópia | Baixar o manifesto da atualização do Windows. |
| `DOWNLOAD` | `0` | qualquer cópia | Baixar o `Ponte.exe`. |
| `ANDROID_MANIFEST` | `0` | aplicativo Android | Baixar o manifesto da atualização do Android. |
| `ANDROID_DOWNLOAD` | `0` | aplicativo Android | Baixar o `Ponte.apk`. |
| `VIDEO` | SHA-256 do pacote | Ponte do Windows | Baixar o componente de vídeo (FFmpeg comprimido). |

A linha precisa casar com a expressão
`^PONTE (HOST|CONNECT|ACCEPT|MANIFEST|DOWNLOAD|ANDROID_MANIFEST|ANDROID_DOWNLOAD|VIDEO|TICKET) ([0-9a-f]{1,64})(?: ([A-Za-z0-9_-]{1,128}))?$`;
qualquer outra coisa derruba a conexão. Se o servidor foi iniciado com `PONTE_KEY`, a chave é
obrigatória em todos os comandos, e uma chave errada recebe `ERR key`.

Respostas de erro têm a forma `ERR <motivo>`:

| Resposta | Significado |
|---|---|
| `ERR key` | Chave de acesso ausente ou errada. |
| `ERR proof` | O aparelho não provou ser dono do ID que tentou registrar. |
| `ERR offline` | Não há aparelho registrado com aquele ID. |
| `ERR busy` | Há pedidos demais aguardando atendimento. |
| `ERR timeout` | O aparelho chamado não atendeu em 20 segundos. |
| `ERR token` | Token de atendimento desconhecido ou já usado. |
| `ERR none` | Não há arquivo para entregar. |
| `ERR naoliberado <email>` | A conta não está na lista de liberadas. |
| `ERR conta` | O token da conta venceu ou é inválido. |
| `ERR servidor` | O servidor não conseguiu conferir a conta agora. |

### 2.2 Registro do controlado

```
controlado → PONTE HOST 460087520 <chave>
servidor   → NONCE <64 dígitos hexadecimais>
controlado → PROOF <chave pública SPKI em base64> <assinatura em base64>
servidor   → OK
```

A assinatura é ECDSA P-256 com SHA-256 sobre os 32 bytes do `NONCE`, no formato IEEE P1363 (`r`
seguido de `s`). O servidor só aceita se a assinatura conferir **e** se o ID calculado a partir da
chave pública enviada for o ID pedido. Um registro novo do mesmo ID derruba o anterior.

Depois do `OK` a conexão fica aberta como canal de controle:

- O controlado manda `PING` a cada 25 segundos e o servidor responde `PONG`.
- O controlado desiste da conexão se passar 70 segundos sem `PONG` e registra-se de novo.
- O servidor remove um registro que fique 90 segundos sem atividade.
- Quando alguém pede conexão, o servidor manda `INCOMING <token>` por esse canal.

### 2.3 Conexão por ID

```mermaid
sequenceDiagram
    participant V as Visualizador
    participant S as Servidor
    participant C as Controlado
    C->>S: PONTE HOST id (canal de controle, com prova da chave)
    V->>S: PONTE CONNECT id
    S->>C: INCOMING token (pelo canal de controle)
    C->>S: PONTE ACCEPT token (conexao nova)
    S->>V: OK
    S->>C: OK
    Note over V,C: daqui em diante o servidor so copia bytes
    V->>C: TLS e mensagens do Ponte
```

O que cada lado tiver enviado logo depois da própria linha é repassado ao outro quando as conexões
são unidas. Quando uma das duas fecha, o servidor fecha a outra.

### 2.4 Bilhete da conta (1.17.0)

```
cópia    → PONTE TICKET <sha256 da chave do aparelho, minúsculo> <chave>
cópia    → <token de acesso da conta>
servidor → TICKET <bilhete>
```

O servidor chama `public.ponte_conta()` no Supabase do FilaBox com esse token. Se a conta estiver
liberada, ele assina o bilhete com `PONTE_TICKET_KEY` (EC P-256). Senão responde `ERR naoliberado`,
`ERR conta` ou `ERR servidor`.

**Formato do bilhete:** `<corpo em base64url>.<assinatura em base64url>`, com a assinatura ECDSA
P-256/SHA-256 (64 bytes, `r||s`) sobre os bytes do corpo. O corpo é um JSON de quatro campos:

| Campo | Conteúdo |
|---|---|
| `v` | Versão do formato. Hoje `1`. |
| `e` | O e-mail da conta. |
| `k` | SHA-256 (hexadecimal maiúsculo) da chave pública do aparelho, em SPKI. |
| `x` | Quando vence, em segundos desde 1970. |

O bilhete vale 14 dias e é renovado quando faltam 7. Quem confere exige: assinatura válida pela
chave do emissor embutida no programa, `v` igual a 1, `e` não vazio, `k` igual ao resumo da chave
daquele aparelho e `x` no futuro. Os detalhes de uso estão em [conta.md](conta.md).

### 2.5 Arquivos de atualização e de vídeo

```
cópia    → PONTE MANIFEST 0 <chave>
servidor → OK <tamanho em bytes>
servidor → <tamanho> bytes e fim da conexão
```

| Comando | Arquivo | Limite |
|---|---|---|
| `MANIFEST` | `manifest.json` | 8 KiB |
| `DOWNLOAD` | `Ponte.exe` | 64 MiB |
| `ANDROID_MANIFEST` | `android-manifest.json` | 8 KiB |
| `ANDROID_DOWNLOAD` | `Ponte.apk` | 64 MiB |
| `VIDEO` | `video-<sha256>.br` | 128 MiB |

Sem TLS: a proteção é a assinatura conferida pelo programa, e no caso do vídeo o resumo fixado
dentro do executável assinado (veja [seguranca.md](seguranca.md)).

### 2.6 Limites do servidor

| Limite | Valor |
|---|---|
| Conexões simultâneas no total | 600 |
| Conexões simultâneas de um mesmo endereço | 40 |
| Prazo para a primeira linha | 15 s |
| Tamanho máximo de uma linha | 2048 bytes |
| Pedidos aguardando atendimento | 500 |
| Prazo para o controlado atender | 20 s |
| Registro sem atividade | 90 s |

### 2.7 WebSocket, para o navegador

O servidor também atende WebSocket, atrás do nginx, na porta de `PONTE_WS_PORT` (o caminho público
é `/ponte/ws`). Pela WebSocket só valem **`CONNECT`** e **`TICKET`**: a página só acessa, nunca é
acessada. A primeira mensagem é `PONTE CONNECT <id>`; depois os bytes passam direto para o aparelho
chamado, já cifrados de ponta a ponta pela própria página.

## 3. Cifra de ponta a ponta

### 3.1 TLS, entre programas

- Versões aceitas: TLS 1.2 e 1.3.
- O controlado é o lado servidor do TLS e apresenta o certificado próprio: autoassinado, ECDSA
  P-256, `CN=Ponte`, válido por 30 anos, criado na primeira abertura.
- O visualizador não apresenta certificado no TLS; ele se identifica depois, por assinatura (seção 5).
- Não há autoridade certificadora. O visualizador confere a identidade assim:
  - **Por ID:** o ID calculado a partir da chave pública do certificado tem de ser o ID digitado.
  - **Sempre:** a impressão digital do certificado (SHA-256 do certificado inteiro) é guardada na
    primeira conexão. Se mudar numa conexão posterior, o usuário é avisado e decide.

### 3.2 WPONTE1, para o navegador

O navegador não faz TLS com um certificado sem autoridade certificadora. O canal `WPONTE1` faz o
mesmo papel com o que a criptografia do navegador oferece (ECDH, ECDSA, HKDF e AES-GCM, todos P-256
e SHA-256):

```
navegador  → computador: "WPONTE1" | chave efêmera ECDH (65 bytes, ponto não comprimido) | 32 bytes aleatórios
computador → navegador : chave efêmera (65) | tamanho do certificado (2 bytes) | certificado | assinatura (64, r||s)
```

A assinatura é da chave do computador (a mesma do TLS, da qual sai o ID) sobre
`"ponte-web-1\n" | SHA-256(mensagem do navegador) | chave efêmera do computador | SHA-256(certificado)`.
O navegador confere o ID e a assinatura antes de mandar qualquer coisa.

As chaves vêm de HKDF-SHA256 sobre o segredo ECDH, com sal igual ao SHA-256 das duas mensagens e
informação `"ponte-web-1 c2s"` (navegador para computador) ou `"ponte-web-1 s2c"` (o contrário),
32 bytes cada. Depois disso as mensagens do Ponte seguem em AES-GCM.

O mesmo canal está em `WebChannel.cs` e em `core/.../WebSecure.kt`.

### 3.3 Cálculo do ID

```
resumo = SHA-256(chave pública em formato SPKI)
ID     = 100000000 + (primeiros 8 bytes do resumo, como inteiro big-endian) mod 900000000
```

O resultado tem sempre 9 dígitos. O servidor e o programa fazem a mesma conta.

## 4. Mensagens

Dentro da cifra, cada mensagem tem um cabeçalho de 5 bytes e o conteúdo:

| Posição | Tamanho | Conteúdo |
|---|---|---|
| 0 | 1 byte | tipo da mensagem |
| 1 | 4 bytes | tamanho do conteúdo, inteiro little-endian |
| 5 | variável | conteúdo |

Antes da autenticação, uma mensagem maior que 8 KiB derruba a conexão; depois dela o limite sobe
para 64 MiB. Inteiros dentro do conteúdo são little-endian.

| Tipo | Nome | Sentido | Conteúdo |
|---|---|---|---|
| 1 | Hello | visualizador → controlado | JSON |
| 2 | Challenge | controlado → visualizador | JSON |
| 3 | AuthResponse | visualizador → controlado | JSON |
| 4 | AuthResult | controlado → visualizador | JSON |
| 10 | Frame | controlado → visualizador | binário, seção 6.1 |
| 11 | Cursor | controlado → visualizador | binário, seção 6.9. *Desde a 1.5.0.* |
| 12 | Audio | controlado → visualizador | binário, seção 6.10. *Desde a 1.5.0.* |
| 13 | CursorPos | controlado → visualizador | binário, seção 6.11. *Desde a 1.5.1.* |
| 20 | Mouse | visualizador → controlado | binário, seção 6.2 |
| 21 | Key | visualizador → controlado | binário, seção 6.3 |
| 22 | Text | visualizador → controlado | texto UTF-8, seção 6.8. *Desde a 1.3.0.* |
| 30 | Clipboard | os dois | texto UTF-8 |
| 40 | FileStart | os dois | JSON |
| 41 | FileChunk | os dois | bytes do arquivo |
| 42 | FileEnd | os dois | vazio |
| 50 | Settings | visualizador → controlado | JSON |
| 51 | VideoStart | controlado → visualizador | JSON, seção 6.12. *Desde a 1.6.0.* |
| 52 | VideoData | controlado → visualizador | binário, seção 6.12 |
| 53 | VideoStatus | controlado → visualizador | texto UTF-8 (até 2048 bytes) |
| 54 | VideoFeedback | visualizador → controlado | JSON (até 1024 bytes) |
| 60 | Notice | controlado → visualizador | texto UTF-8 |
| 70 | Ping | visualizador → controlado | 8 bytes |
| 71 | Pong | controlado → visualizador | os mesmos 8 bytes |
| 72 | Restart | visualizador → controlado | vazio. *Desde a 1.15.0.* |
| 73 | RestartResult | controlado → visualizador | vazio se deu certo; texto UTF-8 com o motivo se não |
| 74 | SecureAttention | visualizador → controlado | vazio (Ctrl+Alt+Del). *Desde a 1.17.0.* |
| 75 | TextFocus | controlado → visualizador | 1 byte. *Desde a 1.17.1.* |
| 99 | Bye | os dois | vazio |

Tipos desconhecidos são ignorados durante a sessão.

## 5. Autenticação

```mermaid
sequenceDiagram
    participant V as Visualizador
    participant C as Controlado
    V->>C: Hello (nome, usuario, versao, chave publica, bilhete)
    Note over C: confere o bilhete da conta de quem acessa
    C->>V: Challenge (senha ativa, sal, iteracoes, nonce, autorizado, bilhete)
    Note over V: confere o bilhete da conta do computador acessado
    V->>C: AuthResponse (prova da senha, assinatura, pedido de autorizacao)
    Note over C: decide por autorizacao salva, senha ou aceite na tela
    C->>V: AuthResult (ok, motivo, nome, telas, autorizado, versao, caps)
```

### 5.1 Campos

**Hello**

| Campo | Tipo | Conteúdo |
|---|---|---|
| `Name` | texto | Nome do aparelho do visualizador. |
| `User` | texto | Usuário do Windows no visualizador. |
| `Version` | texto | Versão do Ponte do visualizador. |
| `Key` | texto ou nulo | Chave pública do visualizador, SPKI em base64. *Desde a 1.2.0.* |
| `Caps` | inteiro ou nulo | O que o visualizador sabe receber, em bits (veja abaixo). *Desde a 1.5.0.* |
| `Resume` | lógico | Esta conexão é a retomada de uma sessão que caiu. *Desde a 1.15.0.* |
| `Ticket` | texto ou nulo | Bilhete da conta de quem acessa, preso à chave dele. *Desde a 1.17.0.* |

Bits de `Caps`, os mesmos no `Hello` e no `AuthResult`:

| Bit | Valor | No `Hello` (visualizador) | No `AuthResult` (controlado) |
|---|---|---|---|
| 0 | 1 | Grava os arquivos que chegam. | Não usado. |
| 1 | 2 | Desenha o ponteiro que chega em `Cursor`. | Envia `Cursor`. |
| 2 | 4 | Toca o som que chega em `Audio`. | Transmite o som quando pedido em `Settings`. |
| 3 | 8 | Decodifica vídeo (`VideoStart`/`VideoData`). | Envia vídeo quando pedido. |
| 4 | 16 | Não usado. | *1.14.0:* o controlado é um celular Android. Quem acessa manda texto pronto (`Text`) e oferece os botões do celular. |
| 5 | 32 | Não usado. | Gerência. |
| 6 | 64 | Não usado. | *1.16.0:* aceita `Settings` com `Screen = -1`, que transmite todas as telas numa imagem só. |
| 7 | 128 | Não usado. | *1.17.0:* entende `SecureAttention` (Ctrl+Alt+Del). Sem o serviço instalado, responde com um aviso. |
| 8 | 256 | *1.17.1:* quer saber quando o foco entra num campo de texto. | *1.17.1:* manda `TextFocus`. |

Teclas que um celular controlado entende como os botões dele (mensagem `Key`): `0xA6` (VK_BROWSER_BACK) = Voltar,
`0xAC` (VK_BROWSER_HOME) = Início, `0x5D` (VK_APPS) = Apps recentes. Ele também trata Esc como Voltar, a tecla do
Windows como Início, o botão direito do mouse como Voltar e o do meio como Início, e transforma as teclas de letras
em texto pelo teclado americano (para visualizadores anteriores à 1.14.0). O celular acessado não envia `Cursor`
nem `Audio`, e responde a arquivos com um `Notice`.

Sem `Caps` no `Hello` (visualizador anterior à 1.5.0), o controlado não envia `Cursor` e trata o
visualizador como capaz de receber arquivos, a não ser que o `User` seja `Android`.

**Challenge**

| Campo | Tipo | Conteúdo |
|---|---|---|
| `HasPassword` | lógico | O controlado aceita senha agora (tem senha e não está em suspensão). |
| `Salt` | texto | Sal da senha em base64 (16 bytes). |
| `Iterations` | inteiro | Iterações do PBKDF2. O visualizador recusa valores fora de 1.000 a 2.000.000. |
| `Nonce` | texto | 32 bytes aleatórios em base64, novos a cada conexão. |
| `Trusted` | lógico | A chave enviada no Hello está na lista de autorizados. *Desde a 1.2.0.* |
| `Ticket` | texto ou nulo | Bilhete da conta do computador acessado, preso à chave do certificado dele. *Desde a 1.17.0.* |

**AuthResponse**

| Campo | Tipo | Conteúdo |
|---|---|---|
| `Proof` | texto ou nulo | Prova da senha em base64. Nulo significa "peça permissão na tela". |
| `Signature` | texto ou nulo | Assinatura do visualizador em base64. *Desde a 1.2.0.* |
| `Remember` | lógico | Pede para ficar autorizado ao entrar com a senha. *Desde a 1.2.0.* |

**AuthResult**

| Campo | Tipo | Conteúdo |
|---|---|---|
| `Ok` | lógico | A sessão foi aceita. |
| `Reason` | texto | Mensagem para o usuário quando `Ok` é falso. |
| `HostName` | texto | Nome do aparelho controlado. |
| `Screens` | lista | Monitores: `Index`, `Width`, `Height`, `Primary` e, desde a 1.16.0, `X` e `Y`, a posição na área de trabalho de lá. |
| `Paired` | lógico | O visualizador está na lista de autorizados. *Desde a 1.2.0.* |
| `Version` | texto | Versão do Ponte do controlado. *Desde a 1.2.1.* |
| `Caps` | inteiro | O que o controlado oferece, nos bits descritos no `Hello`. *Desde a 1.5.0.* |

### 5.2 Prova da senha

```
verificador = PBKDF2-HMAC-SHA256(senha, sal, iterações, 32 bytes)
prova       = HMAC-SHA256(chave = verificador, dados = nonce || SHA-256(certificado do controlado))
```

O visualizador usa o certificado que ele viu; o controlado usa o próprio. Se houver um
intermediário apresentando outro certificado, as duas contas dão resultados diferentes.

### 5.3 Assinatura do visualizador

```
mensagem   = "ponte-autorizacao\n" || nonce (32 bytes) || SHA-256(certificado do controlado)
assinatura = ECDSA P-256 com SHA-256 sobre a mensagem, com a chave privada do visualizador
```

O visualizador assina em toda conexão. O controlado confere a assinatura com a chave do `Hello` e
só então considera a chave "provada". Uma chave provada é identificada pelo SHA-256 inteiro do SPKI,
não pelo ID de 9 dígitos.

### 5.4 Decisão do controlado

```mermaid
flowchart TD
    T["Bilhete da conta de quem acessa<br/>e valido e preso a chave dele?"] -->|nao| N["Recusa"]
    T -->|sim| A["Chave do Hello esta na lista<br/>e a assinatura confere?"]
    A -->|sim| OK["Entra: autorizacao salva"]
    A -->|nao| B["AuthResponse trouxe prova de senha?"]
    B -->|sim| C["Senha ativa e prova correta?"]
    C -->|sim| P["Entra: senha<br/>Se Remember e assinatura valida,<br/>a chave entra na lista"]
    C -->|nao| N
    B -->|nao| D["Mostra o pedido na tela"]
    D -->|Aceitar| Q["Entra: aceite na tela<br/>Se marcou Sempre permitir<br/>e a assinatura e valida,<br/>a chave entra na lista"]
    D -->|Recusar ou 30 s| N
```

Depois de aceita, a sessão ainda pode ser recusada com "Já existe uma sessão em andamento neste
computador", porque o controlado atende uma por vez. Uma conexão com `Resume` verdadeiro cuja chave
está na lista de retomadas bloqueadas também é recusada: ela foi encerrada à mão do outro lado.

### 5.5 Prazos

| Etapa | Prazo |
|---|---|
| TLS, Hello e Challenge, no controlado | 20 s |
| Espera pelo AuthResponse, no controlado (o usuário pode estar digitando a senha) | 2 min |
| Hello até Challenge, no visualizador | 20 s |
| Pedido na tela do controlado | 30 s |
| Espera pelo AuthResult, no visualizador | 75 s |
| Pedido do bilhete ao servidor | 25 s |

## 6. Sessão

### 6.1 Frame (tipo 10)

Cada quadro traz só as áreas da tela que mudaram.

| Campo | Tamanho | Conteúdo |
|---|---|---|
| largura | 2 bytes | largura da tela inteira, em pixels |
| altura | 2 bytes | altura da tela inteira |
| quantidade | 2 bytes | número de retângulos que seguem |

Para cada retângulo:

| Campo | Tamanho | Conteúdo |
|---|---|---|
| x, y | 2 + 2 bytes | canto superior esquerdo |
| w, h | 2 + 2 bytes | largura e altura |
| tamanho | 4 bytes | tamanho do JPEG |
| imagem | variável | JPEG do retângulo |

O visualizador mantém uma imagem do tamanho da tela e desenha cada retângulo na posição indicada.
Um quadro com dimensões diferentes das atuais recomeça a imagem. Um quadro malformado encerra a sessão.

### 6.2 Mouse (tipo 20), 7 bytes

| Campo | Tamanho | Conteúdo |
|---|---|---|
| x | 2 bytes | posição horizontal, de 0 a 65535, relativa ao monitor escolhido |
| y | 2 bytes | posição vertical, de 0 a 65535 |
| ação | 1 byte | veja abaixo |
| roda | 2 bytes com sinal | deslocamento da roda (120 por passo) |

| Ação | Significado |
|---|---|
| 0 | mover |
| 1 / 2 | botão esquerdo: apertar / soltar |
| 3 / 4 | botão direito: apertar / soltar |
| 5 / 6 | botão do meio: apertar / soltar |
| 7 | roda vertical |
| 8 | roda horizontal (aceita pelo controlado; a interface atual não envia) |

### 6.3 Key (tipo 21), 3 bytes

| Campo | Tamanho | Conteúdo |
|---|---|---|
| tecla | 2 bytes | código de tecla virtual do Windows (1 a 0xFE) |
| marcas | 1 byte | bit 0 = soltar; bit 1 = tecla estendida |

### 6.4 Settings (tipo 50)

JSON com:

| Campo | Conteúdo |
|---|---|
| `Screen` | Índice do monitor, ou `-1` para todas as telas numa imagem só (1.16.0, com o bit 6 de `Caps`). |
| `Quality` | Qualidade do JPEG; o controlado limita a faixa de 10 a 95. |
| `Audio` | O visualizador quer o som. *Desde a 1.5.0.* |
| `Video` | Opções de vídeo (seção 6.12), ou nulo para o modo JPEG. *Desde a 1.6.0.* |
| `IdleSeconds` | Tempo sem uso depois do qual quem acessa quer a sessão encerrada: 0 é nunca, nulo deixa valer o do controlado. *Desde a 1.13.0.* |

Quando o monitor ou a qualidade mudam, o controlado manda a tela inteira no quadro seguinte.

O som começa desligado em toda sessão. Só é transmitido depois de um `Settings` com `Audio`
verdadeiro, e só se o dono do computador controlado não o tiver desligado nas Configurações.

### 6.5 Clipboard (tipo 30)

Texto em UTF-8. O receptor ignora mensagens acima de 256 KiB; o emissor não envia textos com mais
de 100.000 caracteres.

### 6.6 Arquivos (tipos 40, 41, 42)

1. `FileStart`: JSON com `Name` e `Size`. Tamanho negativo ou acima de 8 GiB encerra a sessão.
2. `FileChunk`: blocos de até 64 KiB. Mais bytes que o anunciado encerram a sessão.
3. `FileEnd`: se chegaram exatamente `Size` bytes, o arquivo é mantido; senão é apagado.

Um aparelho envia um arquivo de cada vez, nos dois sentidos: o visualizador pelo botão **Enviar
arquivo...** da janela da sessão, o controlado pelo botão de mesmo nome no aviso de sessão ativa.

### 6.7 Notice (tipo 60) e Bye (tipo 99)

`Notice` leva um texto (até 2048 bytes) para mostrar a quem está operando: hoje é usado para avisar
que o Windows bloqueou mouse e teclado, que o Ctrl+Alt+Del precisa do serviço, ou que o celular
acessado não recebe arquivos. `Bye` encerra a sessão de forma limpa.

### 6.8 Text (tipo 22)

Texto em UTF-8, de até 4096 bytes, para o controlado digitar. Existe porque o teclado de um celular
entrega texto pronto, e não teclas. O controlado trata cada caractere assim:

| Caractere | O que é injetado |
|---|---|
| quebra de linha (`\n`) | a tecla Enter |
| tabulação (`\t`) | a tecla Tab |
| retorno de carro (`\r`) | nada |
| qualquer outro | entrada Unicode, que não depende do layout de teclado do controlado |

Um caractere fora do plano básico (emoji) ocupa duas unidades UTF-16 e vira duas entradas seguidas.
Quem envia textos longos divide em várias mensagens, sem separar essas duas unidades.

### 6.9 Cursor (tipo 11)

A imagem da tela (`Frame`) não traz o ponteiro do mouse. Ele vai à parte: uma vez no começo da
sessão e depois só quando muda de forma. O visualizador o desenha onde está o ponteiro dele.

| Campo | Tamanho | Conteúdo |
|---|---|---|
| marcas | 1 byte | bit 0 = o ponteiro está visível |
| ponto ativo x, y | 2 + 2 bytes | o ponto da imagem que marca a posição do mouse |
| imagem | variável | PNG de 32 bits com transparência; ausente quando o ponteiro está escondido |

A imagem tem no máximo 256 x 256 pixels e vem no tamanho real de lá, sem redução. Pontos que no
Windows invertem a cor do que está embaixo (a barra de texto) chegam em preto, com um contorno
branco. O visualizador ignora mensagens acima de 512 KiB.

O controlado só envia `Cursor` a quem declarou o bit 1 de `Caps` no `Hello`.

### 6.10 Audio (tipo 12)

O som que o computador controlado está tocando na saída padrão (não o microfone), em pedaços de
20 ms. Cada mensagem se decodifica sozinha.

| Campo | Tamanho | Conteúdo |
|---|---|---|
| formato | 1 byte | 1 = PCM de 16 bits; 2 = ADPCM IMA de 4 bits |
| canais | 1 byte | 2 (o controlado sempre envia estéreo; no PCM o visualizador aceita também 1) |
| amostras por segundo | 4 bytes | normalmente 48000 |
| dados | variável | conforme o formato |

**PCM (1):** amostras de 16 bits com sinal, little-endian, intercaladas (esquerda, direita).
Usado quando a qualidade pedida em `Settings` é 80 ou mais.

**ADPCM (2):** primeiro o estado de cada canal, 4 bytes por canal: a amostra prevista (2 bytes com
sinal), o índice do passo (1 byte, de 0 a 88) e 1 byte vago. Depois, um byte por instante: o código
do canal esquerdo nos 4 bits baixos e o do direito nos 4 altos. As tabelas e a conta são as do
ADPCM IMA comum. Ocupa um quarto do PCM (cerca de 390 kbit/s contra 1.540 kbit/s).

Depois de cerca de 200 ms de silêncio o controlado para de enviar, até voltar a tocar alguma coisa.
Se a rede não dá conta, o controlado descarta os pedaços mais antigos da fila de envio (até 1 s) em
vez de acumular atraso. O visualizador guarda uma folga de 150 ms antes de tocar e descarta o que
chega quando já tem mais de 600 ms na fila.

### 6.11 CursorPos (tipo 13), 5 bytes

Onde o ponteiro do mouse está no computador controlado, para o visualizador desenhá-lo no lugar
certo mesmo sem ter tocado na tela (o aplicativo do celular, no modo Toque).

| Campo | Tamanho | Conteúdo |
|---|---|---|
| marcas | 1 byte | bit 0 = o ponteiro está no monitor mostrado |
| x, y | 2 + 2 bytes | posição de 0 a 65535 nesse monitor, na mesma escala da mensagem `Mouse` |

O controlado confere a posição a cada ciclo da captura (cerca de 20 vezes por segundo) e só envia
quando ela muda, e só a quem declarou o bit 1 de `Caps` no `Hello`. Fora do monitor mostrado, x e
y vão zerados.

### 6.12 Vídeo (tipos 51 a 54), desde a 1.6.0

Quando o `Settings` traz `Video` com codec `h264` ou `h265`, o controlado para de mandar `Frame` e
passa a mandar um fluxo de vídeo.

**`VideoOptions`**, dentro do `Settings`:

| Campo | Conteúdo |
|---|---|
| `Fps` | 20, 30, 60 ou 120. Outro valor vira 20. |
| `Codec` | `jpeg`, `h264` ou `h265`. Outro valor vira `jpeg`. |
| `Chroma444` | Cores sem redução. No modo compatível, usa PNG RGB sem perdas. |
| `UltraLowLatency` | Ajusta o codificador para latência mínima. |
| `BitrateMbps` | De 1 a 100. |
| `MaxHeight` | 480, 720, 900 ou 1080; 0 mantém o tamanho da tela. |

**`VideoStart` (51)**, JSON de até 4 KiB: `Id` do fluxo, `Width`, `Height`, `Codec`, `Encoder` (qual
placa está codificando) e os demais dados do fluxo. Um `Id` novo recomeça a decodificação.

**`VideoData` (52)**: os pacotes do fluxo, com o `Id` nos primeiros 4 bytes.

**`VideoStatus` (53)**: texto para a barra de estado do visualizador — "procurando a placa de vídeo
que codifica H.265", "baixando o componente de vídeo: 42 %", o motivo de ter caído para JPEG.

**`VideoFeedback` (54)**: JSON com `Id`, `Failed` e `Reason`. O visualizador usa para avisar que não
conseguiu decodificar aquele fluxo; o controlado então volta para JPEG.

### 6.13 Ping e Pong (tipos 70 e 71)

O visualizador manda 8 bytes (um instante do relógio dele) e o controlado devolve os mesmos 8 bytes.
Serve para medir o tempo de ida e volta, que alimenta a qualidade automática e a barra de estado.

### 6.14 Restart e RestartResult (tipos 72 e 73), desde a 1.15.0

`Restart` (vazio) pede o reinício do computador controlado. A resposta `RestartResult` vem vazia
quando o reinício foi pedido ao Windows, ou com um texto explicando por que não: "O dono desativou o
reinício remoto nas configurações" ou o erro do próprio Windows.

### 6.15 SecureAttention (tipo 74), desde a 1.17.0

Vazio. Pede o Ctrl+Alt+Del de verdade, a tela segura do Windows, que só o serviço do Ponte consegue
acionar. O controlado só anuncia o bit 7 de `Caps` quando entende a mensagem; sem o serviço
instalado, ele responde com um `Notice` explicando.

### 6.16 TextFocus (tipo 75), desde a 1.17.1

Um byte: `1` quando o foco do teclado entrou num campo de texto no computador controlado, `0` quando
saiu. Só é enviado a quem declarou o bit 8 de `Caps` no `Hello`, e só quando o estado muda. O
controlado confere a cada 300 ms. O aplicativo Android usa isso para abrir o teclado do celular
sozinho.

## 7. Convivência entre versões

As mensagens JSON ganham campos novos sem quebrar as versões anteriores:

- Quem recebe ignora campos que não conhece.
- Campo ausente vale o padrão: nulo, falso ou texto vazio.

Consequências práticas:

| Visualizador | Controlado | Resultado |
|---|---|---|
| 1.17.x | 1.17.x | Tudo disponível. |
| anterior à 1.17.0 | 1.17.0 ou mais novo | **Não entra**: sem bilhete da conta. |
| 1.17.0 ou mais novo | anterior à 1.17.0 | **Não entra**: o controlado não manda bilhete. |
| 1.17.1 | 1.17.0 | Sem o aviso de campo de texto: o controlado não anuncia o bit 8. |
| 1.17.x | 1.16.x | Sem Ctrl+Alt+Del nem aviso de campo de texto. |
| 1.17.x | 1.15.x | Sem telas numa imagem só, então sem janela por tela. |
| 1.6.x ou mais novo | 1.5.x | Sem vídeo: o controlado não o oferece. |
| 1.5.x | 1.2.x a 1.4.x | Sem ponteiro de lá e sem som. |
| 1.2.x | 1.0 ou 1.1 | Conecta por senha ou aceite. Sem entrada direta. |

Um tipo de mensagem novo segue a mesma lógica: quem não o conhece ignora. Por isso um visualizador
só deve usar `Text` quando o `AuthResult` informar versão 1.3.0 ou mais nova.

Mudar o significado de um campo existente, ou o formato das mensagens binárias, quebraria essa
convivência e exigiria negociar a versão no `Hello`.
