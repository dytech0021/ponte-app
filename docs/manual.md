# Manual de uso

Este manual descreve o Ponte 1.17.4 para Windows do ponto de vista de quem usa o programa. Para o
funcionamento interno, veja [arquitetura.md](arquitetura.md). Para acessar um computador pelo
celular, veja [android.md](android.md); pelo iPhone ou por um navegador,
[navegador-e-iphone.md](navegador-e-iphone.md).

## 1. O que é

O Ponte permite ver a tela de outro computador com Windows e controlá-lo com o seu mouse e teclado,
ouvir o som dele, enviar e receber arquivos e copiar e colar texto entre os dois. O mesmo `Ponte.exe`
serve para acessar e para ser acessado; não há instalação, basta abrir o arquivo.

## 2. Requisitos

- Windows de 64 bits.
- **.NET Desktop Runtime 10 (x64)** instalado. Sem ele o Windows mostra um aviso ao abrir o Ponte,
  com o link para baixar. É uma instalação única por computador.
- **Uma conta liberada pelo dono do Ponte** (seção 4). Sem ela o programa abre, mas não acessa nem é
  acessado.
- Acesso de saída à internet para conectar por ID. Na mesma rede dá para conectar por IP sem internet.
- O `Ponte.exe` precisa ficar numa pasta em que o usuário possa gravar (Área de Trabalho, Documentos),
  porque a atualização automática troca o arquivo no lugar.

## 3. Primeira abertura

Na primeira vez o Ponte cria a identidade do computador e mostra o **endereço** dele, um número de 9
dígitos (por exemplo `594 843 046`). Esse número não muda enquanto a configuração do usuário existir.

O Windows pode perguntar se o Ponte pode receber conexões pela rede. Essa permissão só é necessária
para conexões diretas por IP; por ID ela não faz falta.

Se a conta do Windows for de administrador, o Ponte pede para abrir como administrador (o aviso
normal do Windows). Veja a seção 12.

Em seguida ele pede a conta.

## 4. A conta (obrigatória desde a 1.17.0)

O Ponte só funciona com uma conta liberada pelo dono, **nos dois lados** da sessão. A conta é a mesma
do FilaBox.

O botão fica embaixo de "Computador remoto":

| O que o botão mostra | Significa |
|---|---|
| **Entrar na conta (obrigatório)...** | Ninguém entrou. O Ponte não acessa nem é acessado. |
| O seu e-mail, em preto | Tudo certo. |
| O seu e-mail, em vermelho | A conta entrou, mas não está liberada, ou o bilhete venceu, ou o servidor está fora do ar. |

Na janela da conta dá para entrar com **e-mail e senha** ou com **Entrar com o Google** (abre o
navegador; depois de entrar no Google, volte para o Ponte). Também há **Criar conta no FilaBox** e
**Esqueci a senha**.

Clicando no botão com a conta já dentro, o menu traz conferir a conta de novo, sincronizar a lista de
computadores salvos, abrir o site e sair da conta. Quem é administrador vê também **Contas liberadas
do Ponte...**.

A conta guarda os seus computadores salvos, então eles aparecem em qualquer aparelho em que você
entrar. Ela **não** dá acesso a computador nenhum: quem entra em cada máquina continua sendo decidido
lá. Os detalhes estão em [conta.md](conta.md).

## 5. A janela principal

O título mostra a versão em uso, por exemplo **Ponte 1.17.4**, e "(administrador)" quando for o caso.

**Este computador** (lado esquerdo), para quando ele for acessado:

| Item | Para que serve |
|---|---|
| Seu endereço | O ID de 9 dígitos que você passa para quem vai acessar. **Copiar** põe o número na área de transferência. |
| Na mesma rede, use | Os endereços IP deste computador, para conexão direta sem internet. |
| Linha cinza | Situação do servidor de retransmissão. |
| Senha de acesso... | Define, troca ou remove a senha (seção 7). |
| Computadores autorizados... | Lista quem entra aqui sem aprovação (seção 8). |
| Aplicativo para celular... | Mostra um QR Code para instalar o aplicativo Android pela rede local, ou salva o APK num arquivo. |

**Computador remoto** (lado direito), para acessar outro:

| Item | Para que serve |
|---|---|
| Endereço (ID ou IP) | Onde se digita o ID ou o IP do outro computador. Espaços e traços no ID são aceitos. |
| Conectar | Inicia a conexão. A tecla Enter faz o mesmo. |
| Lista | Computadores salvos em cima, conexões recentes embaixo, cada um com a miniatura da tela dele e uma bolinha verde (está com o Ponte aberto) ou vermelha. Um clique preenche o endereço; dois cliques conectam. |
| Salvar... / Editar... / Remover | Gerenciam a lista (seção 8). |
| Botão da conta | Seção 4. |

A **miniatura** é a última imagem que você viu da tela daquele computador, reduzida. Ela é gravada
durante a sessão (no começo, de minuto em minuto e ao fechar) e serve para reconhecer cada computador
de relance. Quem ainda não foi acessado aparece com o desenho de um monitor. A miniatura fica só no
seu computador e é apagada quando você remove o computador da lista.

No rodapé ficam uma linha de avisos, a versão com a situação da atualização, **Procurar atualização**,
o botão **Abrir como administrador** (quando o Ponte não está como administrador) e
**Configurações...**.

Fechar a janela **não fecha o Ponte**: ele continua no ícone perto do relógio, para poder ser
acessado. Para fechar de verdade, use o menu desse ícone.

## 6. Acessar e ser acessado

### Acessar outro computador

1. Peça o endereço (ID) que aparece no Ponte do outro computador.
2. Digite em **Endereço (ID ou IP)** e clique em **Conectar**.
3. O que acontece em seguida depende do outro computador:
   - **Ele já autorizou o seu:** a sessão abre direto.
   - **Ele tem senha de acesso:** o Ponte pede a senha. Dá para digitá-la ou clicar em
     **Pedir permissão** para que alguém aceite lá.
   - **Ele não tem senha:** aparece um pedido na tela dele, e alguém precisa clicar em **Aceitar**.

O pedido fica 30 segundos na tela do outro computador; sem resposta, é recusado.

### Ser acessado

Quando chega um pedido, aparece a janela **Pedido de conexão** com o nome do computador, o usuário, a
origem e o endereço de quem pede. **Recusar** é a opção padrão. A caixa **Sempre permitir este
computador** está explicada na seção 8.

Enquanto alguém controla o computador, um aviso fica visível no canto inferior direito da tela, com
o nome de quem está conectado e o botão **Encerrar sessão**. Esse aviso não pode ser escondido.

No mesmo aviso:

- **Enviar arquivo...** manda um arquivo deste computador para quem está acessando. Lá ele fica em
  `Downloads\Ponte`. O botão não aparece quando quem acessa usa o aplicativo do celular ou o
  navegador, que não recebem arquivos.
- Uma linha avisa quando **o som deste computador está sendo transmitido** (seção 10).

O Ponte aceita uma sessão por vez em cada computador. Fechar o Ponte encerra a sessão.

### Quando a conexão cai

Com **Reconectar automaticamente após quedas de rede** ligado (padrão), o Ponte tenta retomar a mesma
sessão por alguns minutos, sem pedir senha nem aceite de novo: a retomada é autenticada pela chave do
computador. Se não conseguir, a sessão termina normalmente.

## 7. Senha de acesso

A senha permite entrar sem ninguém aceitar o pedido. Em **Senha de acesso...**:

- A senha precisa ter pelo menos 8 caracteres. Não repita senha de outro serviço.
- **Remover senha** volta ao modo em que toda conexão precisa de aceite (ou de autorização salva).
- Cinco senhas erradas seguidas suspendem o acesso por senha por dez minutos.
- Ao trocar ou remover a senha, o Ponte pergunta se os computadores que ficaram autorizados usando
  a senha anterior continuam liberados.

## 8. Computadores salvos e entrada sem aprovação

### Salvar um computador

Em **Salvar...**, informe um nome (Escritório, Casa, Loja) e o endereço. A opção **Entrar sem
aprovação nas próximas vezes** já vem marcada. O computador passa a aparecer em "Computadores
salvos", e dois cliques no nome conectam.

Selecionar uma conexão recente antes de clicar em **Salvar...** já preenche o endereço.

Com a conta dentro, a lista de salvos é sincronizada: ela aparece em qualquer aparelho em que você
entrar com a mesma conta.

### Autorizar a entrada direta

Salvar um computador na sua lista não libera nada sozinho. Quem libera é o computador acessado, uma
única vez, de um destes dois jeitos:

1. **Pelo pedido de conexão:** alguém lá marca **Sempre permitir este computador, sem perguntar de
   novo** e clica em **Aceitar**.
2. **Pela senha:** você conecta a um computador salvo e digita a senha de acesso dele. Ele autoriza
   o seu computador na mesma hora, sem precisar de ninguém lá.

Depois disso a terceira linha do computador na sua lista muda de **pede aprovação** para **entra
direto**, e as conexões seguintes abrem sem senha e sem aceite.

A autorização vale para o seu computador e a sua conta do Windows. Outro computador, ou outra conta
no mesmo computador, precisa ser autorizado à parte.

### Ver e desfazer autorizações

No computador acessado, **Computadores autorizados...** mostra quem entra direto, quando foi
autorizado, se foi "aceito aqui" ou "pela senha", e o último acesso. **Remover autorização** vale a
partir da conexão seguinte. A lista comporta até 50 computadores.

Remover um computador da sua lista de salvos não desfaz a autorização no outro lado; ela continua
até ser removida lá.

### O que a entrada direta exige

O computador acessado precisa estar ligado, com o Ponte aberto e com a sessão do Windows aberta e
desbloqueada — a não ser que o **serviço do Windows** esteja instalado (seção 13), que é o que permite
atender na tela de bloqueio e antes do login. A cópia de uso inicia minimizada após o login no
Windows; a opção pode ser desligada nas configurações.

## 9. A janela da sessão

A janela mostra a tela do outro computador, ajustada ao tamanho da janela. O título traz o nome e o
endereço dele; a barra de baixo mostra a versão do Ponte de lá, o tamanho da tela, quadros por
segundo, consumo de rede e, no modo vídeo, o estado da codificação.

| Comando | O que faz |
|---|---|
| Tela | Escolhe qual monitor do outro computador aparece, ou abre **cada tela em uma janela** (seção 9.1). |
| Qualidade | **Ajustar automaticamente à conexão**, Baixa (mais rápida), Média ou Alta. |
| Vídeo | FPS alvo, codec (JPEG, H.264 ou H.265 pela GPU), Chroma 4:4:4, ultra baixa latência (seção 9.2). |
| Teclas | Envia combinações que o seu Windows interceptaria: **Ctrl+Alt+Del**, Alt+Tab, tecla Windows, Windows+R, Windows+D, Ctrl+Shift+Esc e Print Screen. Também **Reiniciar computador remoto...**. |
| Tela cheia | A tela do outro computador ocupa o monitor inteiro, sem borda e sem barras. A barra de comandos aparece por cima da imagem quando o mouse encosta no alto da tela. |
| Enviar arquivo... | Envia um arquivo, que fica em `Downloads\Ponte` do outro computador. Limite de 8 GB por arquivo. |
| Salvar imagem... | Grava em PNG o que a tela remota está mostrando. |
| Área de transferência | Ligado, o texto copiado num computador pode ser colado no outro. Só texto. |
| Som | Ligado, você ouve o que o outro computador está tocando. Veja a seção 10. |
| Desconectar | Encerra a sessão. |

Ao acessar um **celular Android**, a barra ganha ◀ Voltar, ● Início e ▮▮ Apps no lugar dos comandos
que não fazem sentido lá.

Com a janela em foco, todas as teclas vão para o outro computador, inclusive Tab, Alt e F4.

### 9.1 Cada tela em uma janela

Quando o computador acessado tem mais de um monitor, o menu **Tela** oferece:

- **Cada tela em uma janela** (1.16.0): cada monitor de lá abre numa janela sua, todas só com a
  imagem. Os comandos aparecem ao encostar o mouse no alto da janela principal. Arrastando uma janela
  de lá para a borda, **o mouse atravessa para a janela da outra tela** (1.17.2), então dá para
  mover uma janela do Chrome de um monitor para outro.
- **Simular a disposição das telas de lá** (1.17.3): põe as janelas na mesma posição em que os
  monitores estão no Windows de lá (por exemplo, um em cima do outro), ampliadas para aproveitar ao
  máximo o monitor que você está usando. As janelas continuam podendo ser arrastadas e
  redimensionadas. A opção fica marcada e é aplicada sozinha nas próximas conexões.

### 9.2 Vídeo

O modo normal manda JPEG com só o que mudou na tela, até cerca de 20 quadros por segundo. Em
**Vídeo** dá para trocar por **H.264** ou **H.265** codificados pela GPU, com 30, 60 ou 120 quadros
por segundo e ultra baixa latência — melhor para movimento e jogos, mais pesado para a rede.

Na primeira vez que se escolhe H.264 ou H.265, o Ponte baixa o componente de vídeo (FFmpeg, cerca de
130 MB) do servidor e confere o resumo antes de usar. Ele fica em `%LOCALAPPDATA%\Ponte\video` e não
vem dentro do `Ponte.exe` para não pesar em toda atualização.

**Chroma 4:4:4** mantém as cores sem redução, o que deixa texto fino mais legível. Em
**Configurações de vídeo...** ficam o bitrate e os ajustes finos. As escolhas são guardadas por
perfil, e a próxima conexão abre no último perfil usado.

### 9.3 Ctrl+Alt+Del e reiniciar

- **Ctrl+Alt+Del** (1.17.0) só funciona se o computador acessado tiver o **serviço do Windows**
  instalado (seção 13): a tela segura do Windows está fora do alcance de um programa comum. Sem o
  serviço, o Ponte avisa. Em celulares não existe.
- **Reiniciar computador remoto...** reinicia a máquina de lá. Com a reconexão automática ligada, o
  Ponte tenta voltar sozinho por até dez minutos; sem ela, é preciso conectar e autorizar de novo.
  O dono pode desligar isso em Configurações ("Permitir reinício remoto em sessões autenticadas").

### 9.4 Ponteiro do mouse

Sobre a imagem, o ponteiro do seu mouse assume a forma que o ponteiro tem **no outro computador**:
seta, barra de texto, mão sobre um link, setas de redimensionar. Assim dá para ver o que o clique vai
fazer sem esperar a imagem. O clique cai exatamente no ponto da tela de lá que está debaixo do seu
ponteiro, inclusive com a janela menor que a tela remota.

Se o outro computador esconder o ponteiro (alguns programas fazem isso enquanto se digita), aqui
aparece a seta comum.

## 10. Som

O botão **Som** liga e desliga o som do outro computador. O que chega é o que ele está tocando nos
alto-falantes ou no fone (música, vídeo, avisos do Windows); o microfone de lá **não** é transmitido.
O outro computador continua tocando normalmente.

- A escolha fica guardada para as próximas sessões.
- Na qualidade **Alta** o som vai sem compressão (cerca de 1,5 Mbit/s enquanto houver som). Em
  **Média** e **Baixa** ele vai comprimido (cerca de 0,4 Mbit/s), com um chiado leve. Em silêncio
  nada é enviado.
- O som chega com um pequeno atraso, de dois a três décimos de segundo.
- O botão fica apagado quando o dono desligou a transmissão de som nas Configurações de lá, ou quando
  o outro lado é este mesmo computador.

## 11. Desconectar sozinho depois de um tempo sem uso

Em Configurações, **Desconectar depois de um tempo sem uso** tem dois campos:

- **Quem acessa este computador**: quanto tempo sem mouse, teclado ou toque este computador aceita
  antes de encerrar a sessão por conta própria.
- **Ao acessar outro computador**: o tempo que você quer quando é você quem acessa. Vale o seu, não o
  de lá; deixando em "O tempo de lá", vale o que o outro computador definiu.

O padrão é nunca desconectar.

## 12. Abrir como administrador

O Windows não deixa um programa comum mandar mouse e teclado para a janela de um programa aberto
como administrador. Se o computador acessado estiver com um programa assim em primeiro plano, os
cliques de quem acessa não têm efeito, e a janela da sessão avisa.

Para controlar esses programas, o Ponte **do computador acessado** precisa estar como administrador:

- Clique em **Abrir como administrador** no rodapé, ou
- deixe marcada, em Configurações, a opção "Abrir como administrador...", que faz isso a cada abertura.

Aparece o aviso normal do Windows pedindo confirmação. A opção de Configurações só age sozinha em
conta de administrador; numa conta comum, use o botão, e o Windows pede a senha de um administrador.
Quando o Ponte está elevado, o título mostra "(administrador)".

## 13. Serviço do Windows (opcional)

Em Configurações, **Serviço: acesso antes do login...** instala um serviço do Windows que mantém o
computador alcançável mesmo com ninguém logado, na tela de bloqueio e na tela de login, e é o que
permite o **Ctrl+Alt+Del** remoto.

- Instalar e remover pedem a confirmação do Windows (é uma mudança no sistema).
- Depois de instalar ou remover, feche o Ponte pelo ícone na bandeja e abra de novo.
- Com o serviço instalado, a linha das Configurações passa a dizer **Serviço do Windows: instalado...**
- Enquanto a área de trabalho do usuário está disponível, quem atende continua sendo o programa comum;
  o serviço assume quando ela não está.

Esses contextos (antes do login, desktop protegido) ainda não foram validados em uso real; veja
[SERVICO-E-CONEXAO-1.15.0.md](SERVICO-E-CONEXAO-1.15.0.md).

## 14. Configurações

| Opção | Significado |
|---|---|
| Servidor de retransmissão | Endereço e porta do servidor que permite conectar por ID. Em branco, só funcionam conexões diretas por IP. |
| Chave do servidor | Chave de acesso do servidor de retransmissão. Precisa ser a mesma configurada nele. |
| Aceitar conexões diretas nesta porta | Liga ou desliga a conexão por IP e define a porta (padrão 7411). |
| Abrir como administrador | Veja a seção 12. |
| Atualizar o Ponte automaticamente | Veja a seção 15. |
| Iniciar o Ponte com o Windows, minimizado | Padrão: ligado. |
| Transmitir o som deste computador a quem o acessa | Desmarcado, ninguém que acessa este computador ouve o som dele. |
| Reconectar automaticamente após quedas de rede | Padrão: ligado. |
| Permitir reinício remoto em sessões autenticadas | Padrão: ligado. |
| Desconectar depois de um tempo sem uso | Veja a seção 11. |
| Serviço: acesso antes do login | Veja a seção 13. |
| Aparência | **Acompanhar o Windows**, **Clara** ou **Escura (modo noturno)**. |

As mudanças valem na próxima vez que o Ponte for aberto.

## 15. Atualização automática

O Ponte procura versão nova pouco depois de abrir e a cada meia hora. Quando encontra, baixa,
confere a assinatura e troca o arquivo sozinho assim que não houver sessão em andamento; ele fecha
e reabre já na versão nova. **Procurar atualização**, no rodapé, faz a consulta na hora. A linha de
versão mostra o andamento:

- "versão mais recente"
- "baixando a versão X"
- "versão X pronta; instala quando a sessão terminar"
- "atualizado agora"

Uma atualização que não tenha a assinatura correta é ignorada, e o motivo aparece nessa mesma linha.
A versão 1.0.0 não tem atualização automática e precisa ser trocada à mão por um `Ponte.exe` novo.

## 16. Aviso de identidade alterada

O Ponte guarda a identidade de cada computador a que você se conecta. Se, numa conexão posterior, o
mesmo endereço responder com outra identidade, aparece um aviso perguntando se você quer conectar
assim mesmo.

- **Em conexão por IP** isso é esperado quando outro computador passou a usar aquele IP, ou quando o
  Ponte de lá perdeu a configuração.
- **Em conexão por ID** não deveria acontecer, porque o ID é calculado a partir da identidade. Trate
  como sinal de que outro equipamento está respondendo no lugar.

Na dúvida, recuse e confirme com a pessoa do outro lado.

## 17. Problemas comuns

| Mensagem ou sintoma | Causa e solução |
|---|---|
| "Sem conta, o Ponte não acessa outros aparelhos nem pode ser acessado." | Ninguém entrou na conta neste computador (seção 4). |
| "A conta X ainda não foi liberada para usar o Ponte." | O dono precisa liberar esse e-mail. |
| "Sem conexão com o servidor do Ponte para conferir a conta." | Servidor fora do ar ou sem internet. Com o bilhete no prazo, o Ponte continua funcionando. |
| O botão da conta fica vermelho | O bilhete venceu ou a conta saiu da lista. Use "Conferir a conta de novo". |
| "Esse computador não está online." | O Ponte não está aberto no outro computador, ou ele está sem internet. |
| "O outro computador não respondeu a tempo." | Ninguém aceitou o pedido, ou a conexão dele caiu. |
| "O pedido foi recusado." | Alguém clicou em Recusar, ou os 30 segundos passaram. |
| "Já existe uma sessão em andamento neste computador." | Outra pessoa já está conectada lá. |
| "Muitas tentativas erradas. O acesso por senha está suspenso..." | Cinco senhas erradas seguidas. Espere dez minutos ou peça permissão. |
| "Sem contato com o servidor de retransmissão; tentando de novo." | Falta de internet ou servidor fora do ar. O Ponte tenta de novo a cada 5 segundos. |
| "O servidor de retransmissão recusou a chave de acesso." | A chave em Configurações não é a do servidor. |
| "A porta 7411 já está em uso; conexões diretas desativadas." | Outro programa usa a porta. Troque a porta em Configurações; a conexão por ID não é afetada. |
| "O Ponte já está aberto neste computador." | Já existe uma janela do Ponte aberta para o mesmo usuário. |
| Ctrl+Alt+Del não faz nada | O computador acessado está sem o serviço do Windows (seção 13). |
| Cliques e teclas não fazem efeito | Há um programa de administrador em primeiro plano no outro computador (seção 12). |
| Imagem parada | O outro computador está bloqueado, na tela de login ou com um aviso do Windows pedindo confirmação, e não tem o serviço instalado. |
| H.264 ou H.265 indisponível | O componente de vídeo não baixou, ou a GPU daquele computador não codifica. Volte para JPEG. |
| Botão Som apagado | A transmissão de som está desligada nas Configurações de lá. |
| Som ligado, mas nada se ouve | O outro computador não está tocando nada, está sem saída de som (o aviso de sessão de lá diz isso) ou está mudo. |
| Aviso do Windows pedindo o .NET | Falta o .NET Desktop Runtime 10 (seção 2). |
| O número de versão não muda | A atualização automática está desligada, o arquivo está numa pasta sem permissão de gravação, ou há uma sessão em andamento. |
| Fechei a janela e o Ponte sumiu | Ele está no ícone perto do relógio. Para fechar de verdade, use o menu desse ícone. |

## 18. Onde ficam os dados

| O quê | Onde |
|---|---|
| Configuração, identidade, conta, bilhete, listas de salvos e autorizados | `%APPDATA%\Ponte\config.json` |
| Miniaturas dos computadores acessados | `%APPDATA%\Ponte\miniaturas` |
| Registro das sessões (início, fim, motivo da queda) | `%APPDATA%\Ponte\sessoes.txt` |
| Erros inesperados | `%APPDATA%\Ponte\erros.txt` |
| Componente de vídeo (FFmpeg) | `%LOCALAPPDATA%\Ponte\video` |
| Arquivos recebidos | `Downloads\Ponte` do usuário |

A identidade é protegida pela conta do Windows: copiar o `config.json` para outro usuário ou outro
computador não leva o ID junto, e o Ponte cria um ID novo lá. Apagar a pasta `%APPDATA%\Ponte`
também gera um ID novo e apaga as listas.
