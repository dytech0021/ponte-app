# Ponte para Android

O aplicativo permite acessar, a partir do celular, um computador que esteja com o Ponte aberto.
Este documento vale para a versão 1.6.4 do aplicativo. A primeira parte é para quem usa; a segunda,
para quem mantém o código.

O aplicativo só **acessa** computadores. Ele não permite que o celular seja acessado.

## 1. Requisitos

- Android 8.0 ou mais novo.
- No computador, o Ponte 1.3.0 ou mais novo, para o teclado do celular funcionar por inteiro. Com
  uma versão anterior a conexão funciona, mas só letras e números são digitados.

## 2. Instalar e atualizar

O aplicativo é distribuído como um arquivo `Ponte.apk`, fora da loja.

1. Leve o arquivo para o celular (pelo cabo, por uma conversa, pela nuvem) e toque nele.
2. O Android pergunta se o aplicativo que abriu o arquivo pode instalar outros aplicativos. Permita.
3. O Play Protect pode avisar que não conhece o desenvolvedor. Escolha instalar assim mesmo.

Para atualizar, instale o `Ponte.apk` novo por cima. A lista de computadores e as autorizações
continuam. **Desinstalar** apaga a lista e a identidade do aparelho: ele ganha outro endereço e
precisa ser autorizado de novo em cada computador.

Desde a 1.5.2, o aplicativo se atualiza sozinho: toda vez que é aberto (no máximo uma vez por
minuto) ele pergunta ao servidor de retransmissão se há versão nova e, se houver, já baixa e confere
o APK, sem perguntar. Depois abre a confirmação de instalação do próprio Android, que é o único toque
necessário: o Android não deixa um aplicativo se instalar sem ela. Se a instalação for recusada, o
arquivo baixado fica guardado e a confirmação volta na próxima abertura. **⋮ → Verificar
atualização** faz a mesma consulta na hora. A consulta usa o servidor e a chave configurados no
aplicativo.

Na primeira atualização o Android pede a permissão para instalar aplicativos desta fonte: o Ponte
abre a tela do sistema, você liga **Permitir desta fonte** e volta ao aplicativo, que retoma sozinho.

As versões 1.5.0 e 1.5.1 também procuram atualização, mas só a cada 6 horas e perguntando antes de
baixar; logo depois de instaladas (ou pelo menu) elas encontram a 1.5.2.

## 3. Tela inicial

| Item | Para que serve |
|---|---|
| versão · este aparelho | A versão do aplicativo e o endereço do aparelho, que é como ele aparece na lista de autorizados do computador. |
| Endereço do computador | O ID de 9 dígitos do computador, ou o IP dele na mesma rede. |
| Conectar | Abre a sessão. |
| Salvar | Guarda o computador com um nome. |
| Computadores salvos | Toque para conectar. Segure para editar ou remover. À direita aparece "entra direto" ou "pede aprovação". |
| Conexões recentes | As últimas conexões que não estão salvas. Segure para salvar ou remover. |
| ⋮ | Verificar atualização, Configurações (nome do aparelho, servidor de retransmissão e chave) e Sobre. |

## 4. Conectar

O que acontece depois de tocar em **Conectar** depende do computador:

- **Ele já autorizou este aparelho:** a sessão abre direto.
- **Ele tem senha de acesso:** o aplicativo pede a senha. Dá para digitá-la ou tocar em
  **Pedir permissão**, para alguém aceitar lá.
- **Ele não tem senha:** aparece um pedido na tela dele, que alguém precisa aceitar em 30 segundos.

### Entrar sem aprovação

Funciona como entre dois computadores:

1. Salve o computador, deixando marcada a opção **Entrar sem aprovação nas próximas vezes**.
2. Na primeira conexão, entre com a senha de acesso dele, ou peça a alguém lá que marque
   **Sempre permitir este computador** ao aceitar.
3. Daí em diante o toque no nome abre a sessão direto.

No computador, o aparelho aparece em **Computadores autorizados...** com o nome dele e o usuário
"Android", e pode ser removido ali. O computador precisa estar com o Ponte aberto e o Windows
desbloqueado.

## 5. A sessão

A tela mostra o computador. Deitar o aparelho aproveita melhor o espaço. No alto fica a barra de
comandos; o botão **▲** a recolhe e **▼** a traz de volta.

| Botão | O que faz |
|---|---|
| Teclado | Abre ou fecha o teclado do celular. |
| Toque / Touchpad | Alterna o modo de toque (veja abaixo). O botão mostra o modo em uso. |
| Tela cheia | Esconde a barra de comandos e faz a imagem cobrir o aparelho inteiro, sem faixas, inclusive na área da câmera. O que não cabe se alcança arrastando. O gesto de voltar traz os comandos de volta. A escolha fica guardada para as próximas sessões. |
| Teclas | Mostra a barra com as teclas que o teclado do celular não tem. |
| Mais | Atalhos de teclado, ver a tela inteira, digitar o texto copiado, qualidade, monitor e dados da conexão. |
| Sair | Encerra a sessão, depois de confirmar. O gesto de voltar faz o mesmo (fora da tela cheia). |

### Gestos

| Gesto | Modo Toque | Modo Touchpad |
|---|---|---|
| Tocar | Clica onde o dedo encostou. | Clica onde o ponteiro está. |
| Tocar duas vezes | Clique duplo. | Clique duplo. |
| Segurar | Botão direito no ponto tocado. | Botão direito onde o ponteiro está. |
| Arrastar | Move a imagem, quando ela está ampliada. | Move o ponteiro. |
| Tocar duas vezes e arrastar | Segura o botão esquerdo: arrasta janelas, seleciona texto. | O mesmo, a partir do ponteiro. |
| Dois dedos, arrastando | Rola a página. | Rola a página. |
| Dois dedos, tocando | Botão direito. | Botão direito. |
| Pinça | Amplia e reduz. | Amplia e reduz. |

O ponteiro aparece com a forma que tem no computador (seta, barra de texto, mão). No modo Toque ele
fica onde o ponteiro está lá: com o Ponte 1.5.1 ou mais novo no computador, a posição vem de lá e
acompanha até o que a pessoa na frente dele faz com o mouse; com um anterior, ele aparece depois do
primeiro toque, onde o clique saiu. Quando o ponteiro está em outro monitor do computador, ele não
aparece. Um círculo rápido também marca onde saiu cada clique. No modo Touchpad o ponteiro é movido
pelo dedo, e a imagem acompanha o ponteiro quando está ampliada.

### Teclado

Com o teclado aberto, o que você digita é digitado no computador, com acentos, símbolos e emoji, em
qualquer layout de teclado de lá.

A barra **Teclas** traz Ctrl, Alt, Shift e Win, que ficam **armadas** ao toque (o botão muda de cor)
e valem para a próxima tecla ou o próximo clique. Assim se faz Ctrl+C (Ctrl, depois a letra c) ou
Ctrl+clique. A mesma barra tem Esc, Tab, Del, as setas, Home, End, PgUp, PgDn, Ins e F1 a F12.

Em **Mais → Atalhos de teclado** estão as combinações mais usadas: copiar, colar, recortar, desfazer,
selecionar tudo, Alt+Tab, Alt+F4, tecla Windows, Windows+D, Windows+R, Ctrl+Shift+Esc e Print Screen.

Um teclado físico ligado ao celular (Bluetooth ou USB) também funciona, inclusive com atalhos.

### Texto copiado

- **Do computador para o celular:** o que for copiado no computador durante a sessão fica disponível
  para colar no celular.
- **Do celular para o computador:** **Mais → Digitar o texto copiado no celular** digita o texto lá,
  como se viesse do teclado.

### Som, ponteiro e identificação dos computadores

Com o Ponte 1.5.0 ou mais novo no PC, o modo Touchpad mostra o ponteiro do próprio Windows,
com a forma de seta, mão ou barra de texto. Em PCs antigos, aparece uma seta simples com contorno fino.

O som dos programas do PC toca no celular quando o computador permite. Em **Mais**, use
**Ligar o som / Desligar o som**; os botões de volume do celular controlam esse áudio.
O microfone não é transmitido. A qualidade Alta envia som sem compressão e usa mais rede.

A lista de computadores mostra uma miniatura da última tela vista em cada sessão. Ela é salva
após a primeira imagem, atualizada a cada minuto e ao sair. Fica no armazenamento privado do
aplicativo e não é uma imagem ao vivo de um computador desconectado.

Nas configurações, escolha a aparência automática (acompanha o Android), clara ou escura.

## 6. Limites desta versão

| Limite | Detalhe |
|---|---|
| Sair do aplicativo | A sessão não tem serviço em segundo plano. Se o Android suspender o aplicativo, a conexão cai e é preciso conectar de novo. |
| Arquivos | Não há envio nem recebimento de arquivos. |
| Ponteiro no modo Toque | Com um Ponte anterior à 1.5.1 no computador, o ponteiro só aparece depois do primeiro toque. |
| Ctrl+Alt+Del, tela de bloqueio, avisos do Windows | Fora do alcance, como no Ponte do Windows. |
| Atualização | Consulta e download automáticos ao abrir; a instalação pede a confirmação do Android. |
| Testes | A 1.0.0 funcionou num aparelho real, conforme relato do usuário. Os recursos novos e o fluxo de instalação da 1.5.0 ainda precisam de validação no aparelho. |

---

## 7. Para quem mantém

### 7.1 Estrutura

O projeto fica em `android/` e tem dois módulos:

| Módulo | Conteúdo |
|---|---|
| `core` | O protocolo do Ponte em Kotlin puro, sem nada de Android: conexão direta e pelo servidor, TLS, autenticação, assinatura, quadros de imagem, envio de mouse, teclas e texto. Sem dependências. |
| `app` | O aplicativo: telas, gestos, teclado, armazenamento e a chave do aparelho. Usa só o que já vem no Android, sem bibliotecas. |

Separar o protocolo num módulo sem Android permite testá-lo no PC contra o Ponte do Windows.

| Arquivo | Responsabilidade |
|---|---|
| `core/.../Client.kt` | `PonteClient.connect` (conexão e autenticação) e `Session` (leitura dos quadros e fila de envio). |
| `core/.../Crypto.kt` | ID, PBKDF2, prova da senha, mensagem de autorização, conversão da assinatura para o formato do .NET. |
| `core/.../Wire.kt` | Tipos de mensagem, enquadramento (`Channel`) e análise do destino digitado (`Target`). |
| `core/.../Audio.kt` | Leitura do som PCM/ADPCM e formato do ponteiro remoto. |
| `core/.../Json.kt` | JSON mínimo, para o núcleo não depender de biblioteca. |
| `app/.../MainActivity.kt` | Tela inicial, listas, salvar e editar, configurações. |
| `app/.../SessionActivity.kt` | A sessão: conexão, perguntas (senha, identidade), barra de comandos. |
| `app/.../RemoteView.kt` | A tela remota: desenho, ampliação, gestos e a ligação com o teclado do Android. |
| `app/.../SoundPlayer.kt` | Reprodução do som do PC com AudioTrack e fila limitada. |
| `app/.../Keyboard.kt` | Texto, teclas, modificadores armados e teclado físico. |
| `app/.../Store.kt` | Preferências, computadores salvos, recentes, identidades vistas; e `DeviceIdentity`, a chave do aparelho. |

### 7.2 Identidade do aparelho

A chave do aparelho é ECDSA P-256 e fica no cofre do Android (AndroidKeyStore). A parte privada não
pode ser lida nem pelo próprio aplicativo: ele só pede assinaturas. O endereço mostrado na tela
inicial é o ID calculado da chave pública, com a mesma conta do Windows.

O Java entrega a assinatura em DER; o Ponte do Windows espera `r` e `s` lado a lado. A conversão
está em `Crypto.derToP1363`.

### 7.3 Texto digitado

O teclado virtual do Android entrega texto, e não teclas. Por isso o protocolo ganhou, na 1.3.0 do
Windows, a mensagem `Text` (veja [protocolo.md](protocolo.md)): o computador injeta cada caractere
como entrada Unicode, sem depender do layout de lá.

`RemoteView` se declara editável e entrega ao Android uma conexão de entrada que não guarda texto:
cada trecho confirmado segue na hora. O campo é declarado como "senha visível", o que faz os
teclados desligarem a sugestão de palavras. Se mesmo assim um teclado "compuser" a palavra, só a
diferença entre uma composição e a seguinte é enviada.

### 7.4 Compilar

Precisa do SDK do Android e de um Java 17 ou mais novo (os scripts procuram um sozinhos). Estes
arquivos precisam existir em `android/`; os dois primeiros ficam fora do git:

| Arquivo | Conteúdo |
|---|---|
| `local.properties` | `sdk.dir=` com o caminho do SDK do Android. |
| `relay.properties` | `server=` e `key=`: o servidor de retransmissão e a chave embutidos no aplicativo. |
| `keystore.properties` | `storeFile=`, `storePassword=`, `keyAlias=`, `keyPassword=`: a chave que assina o APK. |

```
cd android
powershell -ExecutionPolicy Bypass -File .\gerar-apk.ps1
```

O script roda os testes e a análise do Android, compila, confere a assinatura com `apksigner` e
copia o release assinado para `pronto\Ponte.apk`. Só depois gera `pronto\android-manifest.json`,
com `versionCode` e `versionName` do `output-metadata.json` desse release, tamanho em bytes e
SHA-256 completo do APK. O manifesto é UTF-8 sem BOM. Builds sem assinatura ou com falha não
geram um manifesto novo. Com `-ComOPc` ele roda antes os testes contra um Ponte do Windows de verdade.

### 7.5 Testes

| Onde | O que confere |
|---|---|
| `core`: `CoreTest` | As contas de criptografia contra valores de outra implementação, o JSON, o destino digitado, o quadro de imagem. |
| `core`: `InteropTest` | Contra um Ponte de verdade: senha errada, sessão, imagem, pelo servidor por ID, autorização pela senha, entrada sem senha, outra chave recusada. |
| `app`: `ToqueTest` | Cada gesto vira as mensagens de mouse certas, nos dois modos. |
| `app`: `TecladoTest` | Texto, atalhos, modificadores armados, teclado que compõe palavras, computador antigo. |
| `app`: `TelasTest` | As telas abrem num Android simulado; a sessão conecta num Ponte de verdade e a imagem aparece. Grava capturas em `capturas/android`. |
| `relay/update.test.js` | Relay TCP local: comandos Android e Windows separados, chave errada ou ausente, arquivos ausentes/vazios, tamanho máximo e excesso. |

Na raiz do repositório, rode `node --test relay/update.test.js` para testar o relay, sem serviço
externo e sem usar as chaves de produção. Os arquivos e o processo de teste são temporários.

Os testes de `app` usam o Robolectric, que simula o Android dentro da máquina de testes. Os que
dependem de um Ponte de verdade só rodam pelo `testar-com-o-pc.ps1`, que sobe um com perfil
descartável e senha aleatória. As capturas da sessão mostram a área de trabalho de quem rodou e
ficam fora do git.

O que esses testes **não** cobrem: o cofre de chaves do aparelho, o teclado virtual real de cada
fabricante, o encolhimento da tela quando o teclado abre e o desempenho. Isso só um aparelho mostra.

### 7.6 Versão e assinatura

A cada mudança, suba `versionCode` (de um em um) e `versionName` em `app/build.gradle.kts`. O
Android só instala por cima um APK com `versionCode` maior e assinado com a mesma chave.

A chave de assinatura fica em `chaves/android.jks`, com a senha em `android/keystore.properties`
(os dois no repositório privado; veja abaixo). **Se ela se perder, não dá mais para atualizar o aplicativo
instalado:** seria preciso desinstalar e instalar um assinado com outra chave, perdendo a lista de
computadores e as autorizações.

**A chave mudou na 1.5.0.** A chave original, com que a 1.0.0 foi assinada, não estava à mão, e o
dono do projeto preferiu criar outra. Por isso a 1.5.0 não instala por cima de uma versão anterior:
é preciso desinstalar o aplicativo antigo uma vez (o que apaga a lista de computadores e a identidade
do aparelho) e instalar a 1.5.0. Daí em diante as atualizações seguem pelo próprio aplicativo.

A chave nova (`chaves/android.jks`) e a senha dela (`android/keystore.properties`) estão no
repositório, que é privado, por decisão do dono: assim qualquer máquina de quem mantém o projeto
gera um APK que atualiza os aparelhos. Quem tiver acesso ao repositório consegue assinar um APK que
o Android aceita como atualização do Ponte; por isso o repositório não pode ficar público.

### 7.7 Atualização Android pelo relay

Na pasta definida por `PONTE_UPDATE_DIR`, os arquivos Android são `android-manifest.json` e
`Ponte.apk`. Os arquivos Windows continuam sendo `manifest.json` e `Ponte.exe`, atendidos pelos
comandos `MANIFEST` e `DOWNLOAD` existentes.

| Requisição (terminada em nova linha) | Arquivo | Limite |
|---|---|---|
| `PONTE ANDROID_MANIFEST 0 [key]` | `android-manifest.json` | 8192 bytes |
| `PONTE ANDROID_DOWNLOAD 0 [key]` | `Ponte.apk` | 64 MiB (67108864 bytes) |

`[key]` representa a chave configurada no relay, sem os colchetes. Pode ser omitida se o servidor
não exigir chave. A resposta é `OK <length>\n`, seguida de exatamente `<length>` bytes do arquivo.
Chave errada ou ausente quando exigida retorna `ERR key\n`; arquivo ausente, vazio ou acima do
limite retorna `ERR none\n`.

O manifesto contém exatamente quatro campos; os valores abaixo de tamanho e hash são ilustrativos:

```json
{"versionCode":3,"versionName":"1.5.0","size":1234567,"sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}
```

O manifesto e o APK usam o mesmo transporte do relay. O manifesto não é uma raiz de confiança:
um hash recebido por esse canal só permite comparar o download com o que o servidor anunciou.
Antes de oferecer a instalação, o aplicativo confere o tamanho e o SHA-256, além do pacote,
versão e certificado reais do APK. O pacote deve ser o do Ponte, a versão deve corresponder à
anunciada e ser mais nova, e o certificado deve corresponder ao aplicativo instalado. A assinatura
do APK é a autoridade para a atualização; o Android também verifica criptograficamente a assinatura
e a compatibilidade com a instalação existente ao instalar.

Para publicar, depois do `gerar-apk.ps1`:

```
cd android
powershell -ExecutionPolicy Bypass -File .\publicar-apk.ps1
```

O script confere que o manifesto descreve o `pronto\Ponte.apk`, que o `versionCode` é maior que o
do servidor e que o `relay.js` de lá entrega o APK; envia os dois arquivos com nome provisório, os
põe no lugar e confere o SHA-256 do que ficou no servidor. Usa a mesma chave SSH do `publicar.ps1`.

O `relay.js` anterior à 1.5.0 não conhece `ANDROID_MANIFEST` e `ANDROID_DOWNLOAD`. Na primeira
publicação, rode com `-AtualizarRelay`: o script guarda o atual em `relay.js.anterior`, põe o deste
repositório no lugar e reinicia o serviço. O Ponte do Windows não é afetado: os comandos dele continuam
os mesmos.
