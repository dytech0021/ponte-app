# Ponte no navegador e no iPhone

O Ponte tem um cliente que roda **no navegador**, feito para o iPhone mas que serve em qualquer
aparelho, e um **aplicativo do iPhone** que é essa mesma página dentro de um app. Os dois só
**acessam** computadores; nenhum dos dois pode ser acessado.

Este documento vale para a versão 1.17.0 do navegador e a 1.12.1 do iPhone.

## 1. O navegador

Endereço: **https://filabox3d.com.br/ponte/**

| Peça | Onde |
|---|---|
| Página e lógica | `web/index.html`, `web/app.js`, `web/app.css` |
| Protocolo e criptografia | `web/ponte.js` |
| Conta do FilaBox | `web/conta.js` |
| Arquivos no servidor | `/var/www/ponte-web`, servidos pelo nginx |
| Caminho até o servidor de retransmissão | WebSocket em `/ponte/ws` |

A página é instalável como aplicativo (`manifest.webmanifest`): no iPhone, **Compartilhar →
Adicionar à Tela de Início**.

Os arquivos vão com `Cache-Control: no-cache`, então quem já abriu a página pega a versão nova ao
abrir de novo.

### 1.1 Por que o navegador não usa TLS

O navegador não consegue fazer TLS com o certificado próprio de cada computador: ele exigiria uma
autoridade certificadora. Em vez disso o Ponte tem um canal próprio equivalente, o **`WPONTE1`**,
feito com o que a criptografia do navegador oferece (ECDH, ECDSA, HKDF e AES-GCM, todos P-256 e
SHA-256). O mesmo canal está em `WebChannel.cs` (Windows) e em `core/.../WebSecure.kt` (Android).

```
navegador  → computador: "WPONTE1" | chave efêmera ECDH (65 bytes) | 32 bytes aleatórios
computador → navegador : chave efêmera (65) | tamanho do certificado (2) | certificado | assinatura (64)
```

A assinatura é da chave do computador (a mesma do TLS, da qual sai o ID) e cobre a mensagem do
navegador, a chave efêmera e o resumo do certificado. **O navegador confere o ID e a assinatura antes
de mandar qualquer coisa**, como o TLS faria. As chaves de sessão saem de HKDF-SHA256, uma para cada
sentido, e o conteúdo vai em AES-GCM.

O servidor de retransmissão continua só copiando bytes: ele não vê a sessão.

### 1.2 O que a página faz

- Conta do FilaBox obrigatória (1.17.0), igual à dos outros programas: e-mail e senha ou Google, com
  bilhete assinado preso à chave daquele navegador.
- Lista de computadores salvos da conta, com a bolinha de online, e as conexões recentes.
- Conexão por ID (pelo servidor). Conexão direta por IP não existe aqui: o navegador não abre TCP.
- Sessão: imagem, toque e touchpad, teclado (com acentos), som, qualidade, troca de monitor, texto
  copiado, tela cheia e tela acesa.
- Desde a 1.13.0, uma seta recolhe a barra de comandos.
- Desde a 1.14.0 também acessa **celulares Android** que estejam com o acesso ligado, com os botões
  Voltar, Início e Apps.

### 1.3 Limites

| Limite | Motivo |
|---|---|
| Só acessa | O navegador não pode ser acessado: ele não escuta conexões. |
| Só por ID | Sem TCP direto no navegador. |
| Sem arquivos | Não envia nem recebe. |
| Depende do servidor | Sem o servidor de retransmissão no ar, não há conexão nenhuma. |

## 2. O aplicativo do iPhone

A Apple não deixa instalar um aplicativo fora da App Store sem um caminho próprio. O Ponte usa o
**SideStore**, que instala o app assinado com a conta Apple gratuita da própria pessoa.

| Peça | Onde |
|---|---|
| Projeto | `ios/` (`project.yml`, `ios/Ponte/`) |
| Tela | `PonteViewController.swift`, um `WKWebView` que abre a página do Ponte |
| Fonte do SideStore | `web/ios.json`, publicada junto com a página |
| Script | `ios\publicar-ios.ps1` |

O aplicativo é a página do Ponte dentro de um app, com três coisas que o navegador não dá no iPhone:
**login do Google** funcionando direto, **tela acesa** durante a sessão e **tela cheia** de verdade.

### Instalar

1. Instale o SideStore no iPhone (processo do próprio SideStore).
2. Adicione a fonte `https://filabox3d.com.br/ponte/ios.json`.
3. Instale o Ponte por ela.

O aplicativo assinado com conta gratuita **vence em 7 dias**; o SideStore renova sozinho enquanto
estiver configurado.

### Publicar uma versão

```
powershell -ExecutionPolicy Bypass -File .\ios\publicar-ios.ps1
```

Ele gera o `.ipa`, atualiza `web/ios.json` com a versão nova e envia tudo. Como a pasta da página é
trocada inteira, publicar o navegador (`web\publicar-web.ps1`) leva o `ios.json` junto — por isso os
dois andam juntos.

## 3. Publicar a página

```
powershell -ExecutionPolicy Bypass -File .\web\publicar-web.ps1
```

A versão vem de `VERSION` em `web/app.js` e precisa ser maior que a que está no servidor. O script
envia os arquivos e confere o que ficou no ar.

Para testar contra um Ponte de verdade nesta máquina, há `web\testar-com-o-pc.ps1`; os testes da
página ficam em `web/test`.
