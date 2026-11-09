# Arquitetura e código

Este documento explica como o Ponte 1.17.4 para Windows é organizado por dentro: os arquivos, o que
cada parte faz e como elas conversam. O formato exato do que trafega na rede está em
[protocolo.md](protocolo.md); as garantias e os limites de segurança, em [seguranca.md](seguranca.md).
O aplicativo Android tem a organização dele descrita em [android.md](android.md), e o cliente do
navegador em [navegador-e-iphone.md](navegador-e-iphone.md).

Termos: **visualizador** é o computador que acessa; **controlado** é o que é acessado.

## 1. Visão geral

- Linguagem e plataforma: C# sobre .NET 10, interface em Windows Forms, só Windows de 64 bits.
- Sem pacotes externos: tudo usa a biblioteca padrão do .NET e chamadas diretas ao Windows. A única
  peça de fora é o FFmpeg do modo vídeo, que é baixado à parte e conferido pelo resumo.
- Um único executável faz os dois papéis. Ao abrir, ele já fica pronto para ser acessado e mostra a
  janela de onde se acessa outro computador.
- Roda em modo usuário. Desde a 1.15.0 há um **serviço do Windows opcional**, que atende quando a
  área de trabalho do usuário não está disponível.
- O servidor de retransmissão é um programa separado, em Node.js, sem dependências (`relay/relay.js`).

```mermaid
flowchart LR
    subgraph A["Computador que acessa"]
        MF1["MainForm"] --> VS["ViewerSession"] --> VF["ViewerForm + RemoteView"]
    end
    subgraph B["Computador acessado"]
        HS["HostService"] --> SE["HostSession"]
        SE --> SC["ScreenCapturer"]
        SE --> II["InputInjector"]
        SE --> PW["PointerWatcher"]
        SE --> AC["AudioCapture"]
        SE --> TF["TextFocus"]
    end
    R["Servidor de retransmissao<br/>relay.js"]
    VS <-->|"TLS de ponta a ponta"| SE
    VS -.->|"por ID"| R
    HS -.->|"registro do ID"| R
    MF1 -.->|"bilhete da conta"| R
```

## 2. Mapa dos arquivos

### Núcleo

| Arquivo | Responsabilidade |
|---|---|
| `Program.cs` | Ponto de entrada: argumentos, elevação, instância única, modos de verificação e de serviço. |
| `Protocol.cs` | Tipos de mensagem (`Msg`), capacidades (`Caps`), registros JSON e o enquadramento sobre o fluxo cifrado (`Channel`). |
| `Transport.cs` | Como chegar ao outro computador: TCP direto ou servidor de retransmissão, e o TLS. |
| `Security.cs` | Chaves, ID, provas de senha e de autorização (`Security`); configuração do usuário (`AppConfig`). |
| `Native.cs` | Declarações das funções do Windows usadas: entrada e ponteiro (`user32.dll`, `gdi32.dll`), som (`winmm.dll`) e captura de som (COM do WASAPI). |
| `Elevation.cs` | Detecta e pede a execução como administrador. |
| `ErrorLog.cs` | Erros inesperados em `%APPDATA%\Ponte\erros.txt`, um por bloco, recomeçando a cada 512 KB. |
| `SessionLog.cs` | Uma linha por início e fim de sessão em `sessoes.txt`: duração, tráfego e por que terminou. |

### Lado acessado

| Arquivo | Responsabilidade |
|---|---|
| `HostService.cs` | Escuta, registra o ID, autentica quem chega (`HostService`) e conduz a sessão (`HostSession`). |
| `ScreenCapturer.cs` | Captura da tela com envio só do que mudou, e reprodução de mouse e teclado (`InputInjector`). |
| `Pointer.cs` | O ponteiro do mouse: leitura da forma (`PointerWatcher`) e uso dela na janela da sessão (`RemotePointer`). |
| `Audio.cs` | Formato e compressão (`AudioCodec`), captura (`AudioCapture`) e reprodução (`AudioPlayer`). |
| `TextFocus.cs` | *1.17.1.* Diz se o foco do teclado está num campo de texto, para o celular abrir o teclado sozinho. |
| `RemotePower.cs` | Reinício remoto, por `shutdown.exe`. |
| `WindowsHost.cs` | O serviço do Windows: supervisor na sessão 0, trabalhador na sessão do console, posse exclusiva por arquivo e o Ctrl+Alt+Del. |

### Lado que acessa

| Arquivo | Responsabilidade |
|---|---|
| `ViewerSession.cs` | Conecta, autentica, recebe quadros, envia entrada. |
| `ViewerForm.cs` | Janela da sessão (`ViewerForm`), a área que mostra a tela remota e captura mouse e teclado (`RemoteView`) e as janelas por tela. |
| `AutomaticQuality.cs` | Ajusta a qualidade conforme a conexão aguenta. |
| `VideoSettingsDialog.cs` | Os ajustes finos de vídeo. |

### Interface

| Arquivo | Responsabilidade |
|---|---|
| `MainForm.cs` | Janela principal: endereço deste computador, conexão, listas, conta, atualização, ícone na bandeja. |
| `ComputerList.cs` | A lista de computadores salvos e recentes, com miniatura e bolinha de online. |
| `Dialogs.cs` | Janelas menores (pedido de conexão, senhas, salvar computador, autorizados, configurações, aviso de sessão) e utilitários de interface (`Ui`). |
| `Presence.cs` | Quais computadores salvos estão com o Ponte aberto, para a bolinha verde ou vermelha. |
| `AndroidApp.cs`, `AndroidAppDialog.cs` | Oferecem o APK do aplicativo Android por link na rede local ou arquivo, conferindo a assinatura. |
| `QrCode.cs` | QR Code próprio (ISO/IEC 18004), para o celular apontar a câmera. |

### Conta (1.17.0)

| Arquivo | Responsabilidade |
|---|---|
| `Account.cs` | A conta do FilaBox no Supabase: login por e-mail/senha e por Google, renovação do token, lista de computadores salvos. |
| `AccountDialog.cs` | A janela "Conta - Ponte". |
| `Ticket.cs` | O bilhete assinado pelo servidor: formato, assinatura, conferência e pedido. |
| `DesktopShell.cs` | *1.17.4.* Abre um endereço no navegador como o usuário, mesmo com o Ponte como administrador. |

### Vídeo (1.6.0)

| Arquivo | Responsabilidade |
|---|---|
| `VideoProtocol.cs` | `VideoOptions` (FPS, codec, 4:4:4, latência) e `StreamProfile`, os perfis de transmissão. |
| `VideoCodec.cs` | Codificação e decodificação por FFmpeg, em processo separado. |
| `VideoRuntime.cs` | Baixa e confere o FFmpeg (LGPL) em `%LOCALAPPDATA%\Ponte\video`. |

### Navegador

| Arquivo | Responsabilidade |
|---|---|
| `WebChannel.cs` | O canal cifrado `WPONTE1`, que faz no navegador o papel do TLS. |

### Atualização e testes

| Arquivo | Responsabilidade |
|---|---|
| `Updater.cs` | Atualização automática: checar, baixar, conferir assinatura, trocar o executável. |
| `UpdateKey.cs` | Chave pública que confere a assinatura das atualizações. |
| `RelayDefaults.cs` | Servidor e chave de retransmissão padrão. Fica fora do git; há um modelo em `RelayDefaults.cs.exemplo`. |
| `SelfTest.cs` | Autoteste de ponta a ponta numa máquina só. |
| `SelfTestVideo.cs`, `SelfTestAccount.cs` | As partes de vídeo e de conta do autoteste. |
| `LifecycleSelfTest.cs` | Autoteste do ciclo de vida (`--autoteste-ciclo`). |
| `ServiceSelfTest.cs` | Autoteste do serviço do Windows (`--autoteste-servico`). |

### Fora do executável

| Caminho | Conteúdo |
|---|---|
| `relay/relay.js` | Servidor de retransmissão, arquivos de atualização e assinatura dos bilhetes. |
| `relay/ponte-relay.service` | Unidade do systemd. |
| `supabase/ponte-liberados.sql` | A tabela e as funções da lista de contas liberadas. |
| `publicar.ps1`, `publicar-github.ps1` | Publicam a versão e o repositório público. |
| `android/`, `web/`, `ios/` | Os outros três programas, cada um com o seu documento. |

## 3. Partida do programa

`Program.Main` segue esta ordem:

0. Escolhe o tema das janelas (`Ui.ApplyTheme`), que precisa valer antes de a primeira existir. O
   tema vem de `--tema` ou do campo `Theme` da configuração, lido sozinho (`AppConfig.SavedTheme`),
   sem carregar nem criar o resto.
1. `--verificar` sai com código 0. É o "teste de partida" que a atualização usa para saber se um
   executável novo abre.
2. **Modos de serviço** (`--servico`, `--agente-servico`, `--aviso-servico`) e autotestes de ciclo e
   de serviço executam e saem, sem janela.
3. Ferramentas de publicação (`--gerar-chave-atualizacao`, `--assinar-atualizacao`,
   `--empacotar-video`) executam e saem.
4. **Elevação.** Se o processo não está como administrador e não recebeu `--ja-elevou` nem
   `--sem-elevar`, ele se reabre elevado quando há `--elevar`, ou quando a configuração pede
   (`RunElevated`) e a conta é de administrador ainda não elevada. Os modos de verificação só
   elevam com `--elevar` explícito.
5. Modos que não abrem a janela principal: `--pos-atualizacao`, `--atualizar-agora`,
   `--janela-teste`, `--autoteste`, `--demo`.
6. Carrega a configuração (`AppConfig.Load`).
7. **Instância única.** Um mutex com nome derivado da pasta de configuração impede duas cópias no
   mesmo perfil, que disputariam a porta e o registro do ID. Depois de uma atualização ou de uma
   reabertura como administrador, a cópia nova espera até 15 segundos a anterior fechar.
8. Limpa sobras de atualização, inicia o `HostService` e abre a `MainForm`.

### Argumentos de linha de comando

| Argumento | Efeito |
|---|---|
| `--perfil pasta` | Usa outra pasta de configuração, com outro ID. |
| `--elevar` | Abre como administrador mesmo que a configuração diga que não. |
| `--sem-elevar` | Não tenta abrir como administrador. |
| `--tema claro\|escuro` | Usa esse tema nesta execução, sem mudar a configuração. |
| `--autoteste pasta [--relay host:porta] [--so-video] [--so-conta]` | Testa os dois lados nesta máquina e grava `autoteste.txt` na pasta. |
| `--autoteste-ciclo pasta` | Testa o ciclo de vida do programa. |
| `--autoteste-servico pasta` | Testa o serviço do Windows. |
| `--captura-ui pasta` | Grava imagens da janela principal e das secundárias e sai. |
| `--demo pasta` | Abre uma sessão com a própria máquina, grava a imagem da janela e sai. |
| `--janela-teste segundos` | Mostra uma janela em primeiro plano por 5 a 300 segundos. Com `--elevar`, reproduz um programa de administrador em primeiro plano. |
| `--atualizar-agora log [--relay host:porta]` | Procura, confere e instala a atualização sem abrir janela, registrando no arquivo. |
| `--gerar-chave-atualizacao chave fonte.cs` | Cria o par de chaves de assinatura. Não sobrescreve uma chave existente. |
| `--assinar-atualizacao exe chave manifesto` | Assina um executável e grava o manifesto. |
| `--empacotar-video ffmpeg [pacote]` | Monta o pacote do componente de vídeo. |
| `--servico`, `--agente-servico`, `--aviso-servico pid` | Papéis do serviço do Windows. |

Uso interno, passados pelo próprio programa ao se reabrir: `--ja-elevou`, `--aguardar`,
`--atualizado`, `--pos-atualizacao log`, `--verificar`.

Em `--autoteste`, `--demo` e `--captura-ui`, que rodam sem ninguém olhando, um erro inesperado não
abre janela de aviso: é gravado em `erro.txt` na pasta indicada e o processo sai com código 3.

## 4. Lado controlado

### HostService

Criado na partida, fica ativo enquanto o programa estiver aberto.

- **Conexões diretas:** escuta em todas as interfaces na porta configurada (padrão 7411). Se a
  porta estiver ocupada, as conexões diretas ficam desativadas e o resto continua.
- **Registro do ID:** mantém uma conexão de controle com o servidor de retransmissão. Se ela cai,
  tenta de novo a cada 5 segundos. Cada pedido de conexão (`INCOMING`) vira uma conexão nova ao servidor.
- **Atendimento (`HandleAsync`):** para cada conexão que chega faz o TLS (ou o canal `WPONTE1`, se
  quem chega é o navegador), lê o `Hello`, **confere o bilhete da conta**, manda o desafio, recebe a
  resposta e decide. A ordem da decisão é: autorização salva, senha, aceite na tela. O pedido na tela
  é delegado à `MainForm` pela função `AskPermission`.
- **Uma sessão por vez:** a sessão aceita ocupa o campo `_current`; uma segunda é recusada.
- **Senhas erradas:** cinco seguidas suspendem o acesso por senha por dez minutos.
- **Retomada (1.15.0):** uma sessão que caiu pode ser retomada pela mesma chave, sem senha nem
  aceite, dentro de uma janela de tempo. `BlockedResumes` guarda as que foram recusadas.

### HostSession

Uma sessão em andamento tem vários laços:

- **Captura:** a cada ciclo chama `ScreenCapturer.Capture` e envia o quadro se algo mudou. O ciclo
  mira 50 ms, então o teto é de cerca de 20 quadros por segundo no modo JPEG; no modo vídeo quem
  manda é o FPS pedido. Se a captura falha (tela bloqueada, aviso do Windows pedindo confirmação),
  espera meio segundo e tenta de novo, mandando a tela inteira quando voltar. No mesmo ciclo consulta
  o ponteiro do mouse e envia a forma (`Cursor`) e a posição (`CursorPos`) quando elas mudam.
- **Leitura:** aplica mouse e teclado, recebe arquivos e área de transferência, troca de monitor e
  de qualidade, liga ou desliga o som, atende Ctrl+Alt+Del e reinício.
- **Som:** envia os pedaços que a captura de som põe numa fila de até 50 (1 segundo). Com a fila
  cheia, o pedaço mais antigo é descartado.
- **Campo de texto (1.17.1):** uma linha MTA própria, chamada `ponte-campo-de-texto`, pergunta a cada
  300 ms se o foco está num campo de texto e só manda mensagem quando muda.
- **Tempo sem uso (1.13.0):** encerra a sessão depois do tempo combinado sem mouse, teclado ou toque.

Ao terminar, por qualquer motivo, para a captura de som e solta todas as teclas que tenham ficado
pressionadas, e grava a linha do `SessionLog`.

### Campo de texto em foco

`TextFocus.Focused()` pergunta à **Automação da Interface** do Windows qual elemento tem o foco e
olha o tipo dele: caixa de texto (`Edit`) conta, a não ser que seja só de leitura; documento
(`Document`) só conta se for editável. Se a Automação não souber responder, vale o **cursor de texto**
do Windows, o traço piscando (`GetGUIThreadInfo` com `GUI_CARETBLINKING`), que programas antigos e o
Chrome criam.

As interfaces COM são declaradas à mão, com os métodos anteriores presentes só para manter a ordem da
tabela: `GetFocusedElement` é o sexto de `IUIAutomation`, `GetCurrentPropertyValue` é o oitavo de
`IUIAutomationElement`.

### Ponteiro do mouse

`PointerWatcher.Poll` pergunta ao Windows qual é o ponteiro atual (`GetCursorInfo`) e só faz alguma
coisa quando ele é outro. O Windows não entrega a imagem do ponteiro com transparência pronta, e
alguns pontos dela não têm cor: invertem a do que está embaixo. Por isso `Encode` desenha o ponteiro
duas vezes (`DrawIconEx`), numa superfície toda preta e numa toda branca, e compara os resultados
ponto a ponto:

- iguais nos dois: ponto opaco, daquela cor;
- preto num e branco no outro, na ordem dos fundos: ponto transparente;
- no meio do caminho: parcialmente transparente (bordas suaves e sombra), e a diferença dá a opacidade;
- mais claro sobre o preto que sobre o branco: ponto que inverte. Vira preto, e os pontos
  transparentes em volta viram brancos, para ele aparecer sobre qualquer fundo.

As duas superfícies são criadas direto no Windows (`CreateDIBSection`). Desenhar por cima de um
`Bitmap` do .NET não serve: o Windows receberia uma superfície zerada, e não o fundo pintado.

### Som

`AudioCapture` roda numa thread própria e usa o WASAPI em modo "loopback", que entrega o que está
saindo pela saída de som padrão, já misturado. Não abre o microfone.

1. Pede o som em 48 kHz, 16 bits, estéreo, com o Windows convertendo. Se ele recusar, captura no
   formato da mixagem e converte aqui (ponto flutuante ou inteiro, qualquer número de canais).
2. A cada 10 ms recolhe o que chegou e corta em pedaços de 20 ms.
3. Um pedaço em silêncio ainda é enviado até completar 200 ms; depois disso, nada sai até voltar o som.
4. Cada pedaço é codificado por `AudioCodec.Encode`, em PCM ou ADPCM conforme a qualidade.
5. Se a saída de som some ou muda (um fone conectado), a captura recomeça com a que for a padrão.
   `Problem` guarda o motivo enquanto não há captura, e o aviso de sessão ativa o mostra.

### Captura de tela

`ScreenCapturer` copia a tela com GDI (`Graphics.CopyFromScreen`) para um bitmap de 32 bits e
compara com a captura anterior:

1. A imagem é dividida em blocos de 64 × 64 pixels; cada bloco é comparado linha a linha.
2. Sem bloco alterado, nada é enviado.
3. Com mais de 60 % dos blocos alterados, vai a tela inteira num JPEG só, que sai menor e mais
   rápido que dezenas de pedaços.
4. Senão, blocos vizinhos são unidos em retângulos (primeiro em faixas horizontais, depois
   estendendo cada faixa para baixo) e cada retângulo vira um JPEG.

A qualidade do JPEG vem do visualizador (35, 60 ou 85 na interface; o padrão é 60). A captura atual
e a anterior ficam em dois bitmaps que trocam de papel a cada ciclo, em vez de se criar um novo a cada vez.

Desde a 1.16.0, `Settings` com `Screen = -1` pede **todas as telas numa imagem só**, do tamanho da
área de trabalho inteira; é assim que a janela por tela funciona.

### Mouse e teclado

`InputInjector` usa a função `SendInput` do Windows.

- **Mouse:** a posição chega de 0 a 65535 dentro do monitor escolhido. Ela é convertida para pixels
  desse monitor, arredondando, e depois para a escala da área de trabalho virtual, que cobre todos os
  monitores. O `SendInput` também quer a posição de 0 a 65535 e a converte de volta truncando; a
  conta mira no meio do pixel para o resultado cair no pixel pedido, e não no vizinho.
- **Teclado:** chega o código de tecla virtual; o código de varredura é obtido com `MapVirtualKey`.
- **Texto:** a mensagem `Text`, usada pelo aplicativo Android, traz texto pronto. Cada caractere é
  injetado como entrada Unicode, que não depende do layout de teclado; Enter e Tab vão como teclas.
- **Bloqueio do Windows:** quando `SendInput` devolve zero, o Windows recusou a entrada. Isso
  acontece quando a janela ativa é de um programa de administrador e o Ponte não é. A sessão então
  manda um `Notice` ao visualizador, no máximo a cada 8 segundos.

## 5. Vídeo (1.6.0)

No modo normal a tela vai em JPEG, só com o que mudou. Em **Vídeo** o visualizador pode pedir H.264
ou H.265, com FPS alvo, 4:4:4 e ultra baixa latência.

- `VideoOptions` descreve o pedido e vai dentro do `Settings`. Quem não conhece essas opções (Ponte
  anterior à 1.6 e o aplicativo do celular) nunca recebe vídeo.
- `StreamProfile` guarda a qualidade e as opções por perfil; a próxima conexão abre no último usado.
- `VideoCodec` conversa com o FFmpeg num processo separado, nos dois sentidos.
- `VideoRuntime` baixa o FFmpeg (cerca de 130 MB, compilação LGPL) do servidor na primeira vez que
  alguém escolhe H.264 ou H.265, e só o aceita se o resumo bater com o que está fixado dentro do
  executável assinado. Duas sessões pedindo ao mesmo tempo esperam o mesmo download.

As mensagens `VideoStart`, `VideoData`, `VideoStatus` e `VideoFeedback` estão em
[protocolo.md](protocolo.md).

## 6. Lado visualizador

### ViewerSession

`ConnectAsync` faz a conexão e a autenticação e devolve a sessão pronta:

1. `Transport.ConnectAsync` abre o TCP (direto ou pelo servidor) e o TLS, e confere o ID.
2. Compara a impressão digital do certificado com a guardada em `KnownHosts`. Se mudou, pergunta
   ao usuário.
3. Envia o `Hello`, **com o bilhete da conta**, recebe o desafio e assina. O desafio traz o bilhete
   do outro lado, que é conferido aqui.
4. Se o controlado tem senha e não reconhece este computador, pede a senha ao usuário.
5. Envia a resposta e espera o resultado.
6. Registra a conexão: identidade vista, lista de recentes e, se o computador está salvo, se ele
   está autorizado.

Depois de `Start`, um laço de leitura aplica os quadros na imagem `Canvas` e dispara eventos para
arquivos, área de transferência, avisos, ponteiro (`PointerChanged`), som (`SoundReceived`, já
decodificado) e campo de texto. Quem lê ou desenha `Canvas` segura `CanvasLock`.

`SaveThumbnail` grava uma redução de `Canvas` (320 × 180, JPEG), que é a miniatura mostrada na lista
de computadores.

### ViewerForm e RemoteView

- `RemoteView` desenha `Canvas` ajustado à janela, mantendo a proporção. Vários quadros seguidos
  viram uma repintura só.
- A posição do mouse na janela vira primeiro o pixel da tela remota que está debaixo do ponteiro e
  depois a escala de 0 a 65535 da mensagem, arredondada. Movimentos são enviados no máximo a cada
  12 ms; o último de uma sequência nunca se perde, porque um temporizador envia o que ficou para trás.
- O ponteiro do outro computador vira o ponteiro da área (`SetPointer`): a imagem recebida é
  transformada num ponteiro do Windows com o mesmo ponto ativo (`RemotePointer`).
- As teclas são interceptadas antes do Windows Forms (`PreProcessMessage`), para que Tab, Alt e F4
  também sigam para o outro computador. Ao perder o foco, a janela manda soltar as teclas que
  estavam pressionadas.
- `ViewerForm` tem a barra de comandos (tela, qualidade, vídeo, teclas especiais, tela cheia,
  arquivo, imagem, área de transferência, som) e a barra de estado com quadros por segundo, consumo
  de rede e estado do vídeo.
- O som é tocado por `AudioPlayer` (`waveOut`). Ele guarda 150 ms de folga antes de começar, para
  os pedaços que a rede atrasa não virarem falhas, e descarta o que chega quando já tem mais de
  600 ms esperando. Com os dois lados na mesma máquina o som fica desligado: o que fosse tocado
  seria capturado de novo.
- A miniatura é gravada logo que a primeira imagem chega, de minuto em minuto e ao fechar a janela.

### Uma janela por tela (1.16.0 a 1.17.3)

Com **Cada tela em uma janela**, o visualizador pede todas as telas numa imagem só (`Screen = -1`) e
recorta cada monitor numa janela própria.

- `SetBarsFloating` deixa a janela principal igual às outras: sem barra de estado e com a barra de
  comandos solta sobre a imagem, escondida até o mouse encostar no alto. A faixa sensível é de 2 px
  em tela cheia e de 6 px lógicos em janela comum, porque logo acima está a barra de título.
  Solta do topo, a barra precisa de `Anchor` próprio; sem isso o Windows Forms falhava ao
  reorganizar a janela.
- `RemoteView` mantém uma lista estática das áreas abertas. Com o botão do mouse apertado, o arrasto
  fica preso à área onde começou; `Under()` descobre se o ponteiro está sobre a janela de outra tela
  da mesma sessão e passa o ponto para ela. É isso que deixa arrastar uma janela de lá entre
  monitores.
- `SimulateLayout()` (1.17.3) põe as janelas na mesma disposição dos monitores de lá. A escala é
  `min((área − colunas·moldura)/largura total, (área − linhas·moldura)/altura total)`, com linhas e
  colunas contadas pelos valores distintos de X e Y, e a moldura sendo a diferença entre `Size` e
  `ClientSize`. O conjunto é centralizado na área útil do monitor onde está a janela principal. A
  opção fica em `AppConfig.SimulateScreens` e é aplicada sozinha ao conectar.

## 7. Janela principal

`MainForm` junta os dois lados:

- Mostra o ID, os IPs locais e a situação do servidor.
- Cuida da **conta**: entrar, renovar o bilhete (quando faltam menos de 7 dias, e no máximo uma
  tentativa a cada 10 minutos), sincronizar a lista de computadores salvos e, para administradores,
  abrir a lista de contas liberadas.
- Recebe os eventos do `HostService` (sessão começou, terminou, arquivo chegou) e mostra o aviso de
  sessão ativa (`SessionIndicator`).
- Responde aos pedidos de conexão com a janela `AcceptDialog`.
- Monta a lista de computadores salvos e recentes (`ComputerList`), com a bolinha de online vinda do
  `Presence`. A lista é desenhada pelo próprio Ponte, para sair igual no tema claro e no escuro.
- Observa a área de transferência do Windows (`WM_CLIPBOARDUPDATE`) e repassa o texto à sessão em
  que este computador é controlado e às janelas de sessão com a opção ligada. O texto que acabou de
  chegar do outro lado não é devolvido.
- Cuida da atualização automática e do ícone perto do relógio: fechar a janela não fecha o programa.

## 8. Serviço do Windows (1.15.0)

`WindowsHost` implementa um serviço opcional que mantém o computador alcançável quando a área de
trabalho do usuário não está disponível.

- Um **supervisor** roda na sessão 0, sob o Gerenciador de Serviços (`--servico`).
- Um **trabalhador** sem interface roda como SYSTEM na sessão do console (`--agente-servico`), que é
  onde ele consegue capturar a tela e injetar entrada, inclusive no desktop protegido.
- Enquanto o programa normal do usuário está disponível, é ele quem atende. A posse é negociada por
  um arquivo com concessão exclusiva, para não existirem dois IDs nem dois ouvintes.
- Os arquivos ficam em `%ProgramData%\PonteHost` (perfil e coordenação) e
  `%ProgramFiles%\PonteHost` (programa).
- É o serviço que faz o **Ctrl+Alt+Del** (`SecureAttention`): a tela segura está fora do alcance de
  um programa em modo usuário. Sem o serviço, o pedido volta como aviso.

Instalar e remover passam pelas Configurações e pedem a confirmação do Windows.

## 9. Configuração

`AppConfig` é gravada em `%APPDATA%\Ponte\config.json` (ou na pasta de `--perfil`). A gravação é
feita num arquivo temporário e depois movida, para não deixar um arquivo pela metade.

| Campo | Conteúdo |
|---|---|
| `CertPfx` | Certificado e chave privada do computador, cifrados com DPAPI para a conta do Windows. |
| `PasswordSalt`, `PasswordVerifier`, `Iterations` | Derivado da senha de acesso (PBKDF2). A senha em si não é guardada. |
| `RelayServer`, `RelayKey` | Servidor de retransmissão e chave de acesso. |
| `ListenPort`, `DirectEnabled` | Porta e liga/desliga das conexões diretas. |
| `RunElevated` | Abrir como administrador quando a conta permitir. Padrão: sim. |
| `AutoUpdate` | Atualização automática. Padrão: sim. |
| `AutoReconnect` | Retomar a sessão depois de uma queda. Padrão: sim. |
| `AutomaticQuality` | Ajustar a qualidade à conexão. |
| `AllowRemoteRestart` | Permitir reinício remoto em sessões autenticadas. Padrão: sim. |
| `BlockedResumes` | Retomadas recusadas, por chave. |
| `StartWithWindows` | Início minimizado após o login. Padrão: sim. |
| `Theme` | `auto`, `claro` ou `escuro`. Padrão: `auto`. |
| `ShareSound`, `PlaySound` | Transmitir o som daqui; tocar aqui o som de lá. Padrão: sim. |
| `SimulateScreens` | Simular a disposição das telas de lá (1.17.3). |
| `HostIdleMinutes`, `ViewerIdleMinutes` | Desconectar por tempo sem uso, do lado acessado e do lado que acessa. |
| `Profile`, `Profiles` | Perfil de transmissão em uso e o que foi mudado em cada um. |
| `Recent` | Até 12 conexões recentes: endereço, nome e data. |
| `Saved` | Computadores salvos: chave do destino, endereço, nome, `AutoAccess`, `Paired`, último uso e marcas de sincronização com a conta. |
| `AccountEmail`, `AccountUserId`, `AccountToken`, `AccountPendingDeletes` | A conta do FilaBox e o que falta sincronizar. |
| `Ticket` | O bilhete assinado da conta (1.17.0). |
| `Trusted` | Computadores autorizados a entrar direto: resumo da chave, ID, nome, usuário, datas e `ViaPassword`. Até 50. |
| `KnownHosts` | Impressão digital do certificado visto em cada destino (`id:123456789` ou `ip:endereço`). |

As miniaturas ficam na subpasta `miniaturas`, uma por destino, com o nome derivado da chave do
destino. São apagadas quando o computador sai da lista de salvos e de recentes.

### Tema

O modo noturno usa o suporte do próprio Windows Forms (`Application.SetColorMode`): com ele as cores
do sistema (`SystemColors`) passam a ser as escuras e os controles se desenham de acordo. As poucas
cores próprias do Ponte passam por `Ui.Accent` e `Ui.Danger`, que clareiam no tema escuro.

Casos de borda no carregamento:

- JSON inválido: o arquivo é copiado para `config.json.invalido` e a configuração recomeça.
- Chave que esta conta não consegue abrir (perfil copiado de outro usuário): o arquivo é copiado
  para `config.json.outra-conta` e nasce um ID novo.

## 10. Concorrência

| Recurso | Quem usa | Proteção |
|---|---|---|
| Listas da configuração e gravação do arquivo | Janela (thread da interface) e atendimento de conexões (threads do pool) | Trava interna da `AppConfig`; todo método que altera ou grava passa por ela. |
| Escrita no canal | Laço de captura, laço de som, envio de arquivo, área de transferência | `Channel` tem um semáforo de escrita; uma mensagem nunca se mistura com outra. |
| Som capturado | Thread da captura (produz) e laço de som (envia) | Fila limitada, que descarta o mais antigo quando enche. |
| Saída de som | Laço de leitura (toca) e fechamento da janela (encerra) | Trava interna do `AudioPlayer`. |
| Envio de arquivo | Dois envios pedidos ao mesmo tempo | `Channel.FileLock`: um arquivo por vez em cada sentido. |
| Imagem da tela remota | Laço de leitura (grava) e pintura da janela (lê) | `ViewerSession.CanvasLock`. |
| Campo de texto | Linha MTA dedicada | Só ela usa a Automação da Interface; o estado vai por mensagem. |
| Controles da interface | Eventos vindos de outras threads | Sempre via `BeginInvoke` (`Post`). |

## 11. Atualização automática

```mermaid
sequenceDiagram
    participant P as Ponte instalado
    participant S as Servidor
    P->>S: MANIFEST
    S->>P: versao, tamanho, sha256, assinatura
    Note over P: confere a assinatura com a chave publica embutida
    Note over P: so segue se a versao for maior que a atual
    P->>S: DOWNLOAD
    S->>P: Ponte.exe
    Note over P: confere tamanho e sha256, grava Ponte-novo.exe
    Note over P: espera nao haver sessao em andamento
    Note over P: testa se o novo executavel abre
    Note over P: renomeia o atual para .antigo e o novo assume o nome
    Note over P: abre a versao nova e fecha
```

- A checagem roda 15 segundos depois de a janela abrir e depois a cada 30 minutos, se a
  atualização automática estiver ligada. **Procurar atualização** faz na hora.
- Só o executável de arquivo único se atualiza. A versão de desenvolvimento, que tem `Ponte.dll`
  ao lado, não tenta.
- O Windows não deixa sobrescrever um programa em execução, mas deixa renomeá-lo. Por isso o
  executável atual vira `Ponte.exe.antigo` e o novo assume o nome. Se a troca falha, o nome
  original é restaurado.
- O arquivo baixado é conferido de novo imediatamente antes de ser executado.
- A versão nova abre com os mesmos argumentos, mais `--atualizado`, e apaga as sobras na partida.
- Uma cópia nunca instala uma versão menor ou igual à que está em uso.

O manifesto assinado e as garantias dessa assinatura estão descritos em [seguranca.md](seguranca.md).

## 12. Elevação

`Elevation` consulta o tipo de elevação do token do processo:

- `IsElevated`: o processo já é administrador.
- `CanElevateSameUser`: a conta é de administrador, mas o processo não está elevado. Nesse caso o
  Windows eleva com uma confirmação simples, sem pedir a senha de outra conta.

A reabertura usa o verbo `runas`, que é o caminho normal do UAC. Nada é contornado: se o usuário
recusa, o Ponte segue sem administrador.

Um Ponte elevado abriria o navegador elevado também, o que atrapalha o login da conta. Por isso
`DesktopShell` (1.17.4) pede à área de trabalho do Windows — que roda como o usuário comum — para
abrir o endereço, pela cadeia `IShellWindows` → `IShellBrowser` → `IShellDispatch2.ShellExecute`, numa
linha STA própria. Se ela não atender, o Ponte abre direto em vez de não abrir.

## 13. Autoteste

`--autoteste` sobe o lado controlado em `127.0.0.1` e conecta o lado visualizador no mesmo
processo, com perfis descartáveis. Com `--relay`, o caminho passa pelo servidor e a conexão é feita
pelo ID. Para não depender de uma conta real, os perfis de teste recebem um bilhete assinado por uma
chave de teste (`Ticket.GiveTestTicket`). O resultado vai para `autoteste.txt`, uma linha por
verificação:

- registro do ID no servidor (só com `--relay`);
- senha errada recusada; pedido recusado na tela não entra; pedido aceito entra;
- aceite simples não deixa o computador autorizado;
- senha correta entra; primeira imagem recebida; imagem do tamanho da tela;
- segunda sessão simultânea recusada;
- a forma do ponteiro do mouse chega ao visualizador, e os ponteiros de texto, mão, redimensionar e
  cruz viram imagem;
- a miniatura da tela é gravada;
- mouse remoto move o cursor; tecla remota pressiona e solta;
- o mouse remoto cai no pixel exato, com a imagem em tamanho real e reduzida;
- o som comprimido volta parecido com o original e o sem compressão volta idêntico;
- o som do computador chega ao visualizador, e para quando ele o dispensa;
- texto remoto é digitado caractere a caractere, com acento, símbolo, emoji e Enter;
- o foco num campo de texto é percebido e avisado (1.17.1);
- arquivo nos dois sentidos, com conteúdo idêntico;
- texto da área de transferência chega ao outro lado;
- a sessão termina no controlado;
- entrar com a senha sem estar salvo não autoriza;
- visualizador de versão anterior ainda entra com a senha;
- computador salvo que entra com a senha fica autorizado, e depois entra sem senha e sem aceite;
- outro computador continua precisando de aceite;
- chave pública alheia sem a chave privada não entra;
- autorização removida deixa de valer;
- "sempre permitir" no aceite autoriza as conexões seguintes.

`--so-video` e `--so-conta` rodam só a parte de vídeo (`SelfTestVideo`) ou a da conta
(`SelfTestAccount`, contra um Supabase falso nesta máquina). `--autoteste-ciclo` e
`--autoteste-servico` cuidam do ciclo de vida e do serviço do Windows.

Cada linha também vai, na hora, para `andamento.txt`; se o teste travar, é ali que se vê onde.

O teste move o cursor de verdade (e o devolve ao lugar), usa a tecla F24, que não existe em teclado
comum, e não toca na área de transferência real. Para o som, toca por um instante um tom grave e
muito baixo; se o computador não tem saída de som, ou ela está muda, a verificação sai como "NÃO
VERIFICADO". Para conferir o texto digitado sem digitar no programa que a pessoa estiver usando, ele
instala por um instante um gancho de teclado que observa as teclas injetadas e as retém; o que a
pessoa digitar de verdade nesse instante passa normalmente. Se houver uma janela de administrador em
primeiro plano, as verificações de mouse e teclado saem como "NÃO VERIFICADO", e o teste confere
apenas se o visualizador foi avisado do bloqueio.

O processo sai com código 0 quando tudo passa e 1 quando há falha.

## 14. Compilação

O projeto (`Ponte.csproj`) é um executável Windows Forms para `net10.0-windows`, com tipos anuláveis
ligados e código `unsafe` permitido (usado na comparação de blocos da captura, na imagem do
ponteiro e nas amostras de som).

```
dotnet build -c Release
```

O executável distribuído é de arquivo único e depende do .NET Desktop Runtime 10 instalado:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false
```

Na prática quem gera o executável distribuído é o `publicar.ps1`, descrito em [operacao.md](operacao.md).
