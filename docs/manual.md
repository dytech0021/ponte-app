# Manual de uso

Este manual descreve o Ponte 1.5.1 para Windows do ponto de vista de quem usa o programa. Para o
funcionamento interno, veja [arquitetura.md](arquitetura.md). Para acessar um computador pelo
celular, veja [android.md](android.md).

## 1. O que é

O Ponte permite ver a tela de outro computador com Windows e controlá-lo com o seu mouse e teclado,
ouvir o som dele, enviar e receber arquivos e copiar e colar texto entre os dois. O mesmo `Ponte.exe`
serve para acessar e para ser acessado; não há instalação, basta abrir o arquivo.

## 2. Requisitos

- Windows de 64 bits.
- **.NET Desktop Runtime 10 (x64)** instalado. Sem ele o Windows mostra um aviso ao abrir o Ponte,
  com o link para baixar. É uma instalação única por computador.
- Acesso de saída à internet para conectar por ID. Na mesma rede dá para conectar por IP sem internet.
- O `Ponte.exe` precisa ficar numa pasta em que o usuário possa gravar (Área de Trabalho, Documentos),
  porque a atualização automática troca o arquivo no lugar.

## 3. Primeira abertura

Na primeira vez o Ponte cria a identidade do computador e mostra o **endereço** dele, um número de 9
dígitos (por exemplo `460 087 520`). Esse número não muda enquanto a configuração do usuário existir.

O Windows pode perguntar se o Ponte pode receber conexões pela rede. Essa permissão só é necessária
para conexões diretas por IP; por ID ela não faz falta.

Se a conta do Windows for de administrador, o Ponte pede para abrir como administrador (o aviso
normal do Windows). Veja a seção 9.

## 4. A janela principal

O título mostra a versão em uso, por exemplo **Ponte 1.5.1**.

**Este computador** (lado esquerdo), para quando ele for acessado:

| Item | Para que serve |
|---|---|
| Seu endereço | O ID de 9 dígitos que você passa para quem vai acessar. **Copiar** põe o número na área de transferência. |
| Na mesma rede, use | Os endereços IP deste computador, para conexão direta sem internet. |
| Linha cinza | Situação do servidor de retransmissão: "Online: pronto para receber conexões pelo ID" ou o motivo de não estar. |
| Senha de acesso... | Define, troca ou remove a senha (seção 6). |
| Computadores autorizados... | Lista quem entra aqui sem aprovação (seção 7). |

**Computador remoto** (lado direito), para acessar outro:

| Item | Para que serve |
|---|---|
| Endereço (ID ou IP) | Onde se digita o ID ou o IP do outro computador. Espaços e traços no ID são aceitos. |
| Conectar | Inicia a conexão. A tecla Enter faz o mesmo. |
| Lista | Computadores salvos em cima, conexões recentes embaixo, cada um com a miniatura da tela dele. Um clique preenche o endereço; dois cliques conectam. |
| Salvar... / Editar... / Remover | Gerenciam a lista (seção 7). |

A **miniatura** é a última imagem que você viu da tela daquele computador, reduzida. Ela é gravada
durante a sessão (no começo, de minuto em minuto e ao fechar) e serve para reconhecer cada computador
de relance. Quem ainda não foi acessado aparece com o desenho de um monitor. A miniatura fica só no
seu computador e é apagada quando você remove o computador da lista.

No rodapé ficam uma linha de avisos, a versão com a situação da atualização, o botão **Abrir como
administrador** (quando o Ponte não está como administrador) e **Configurações...**.

## 5. Acessar e ser acessado

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
computador** está explicada na seção 7.

Enquanto alguém controla o computador, um aviso fica visível no canto inferior direito da tela, com
o nome de quem está conectado e o botão **Encerrar sessão**. Esse aviso não pode ser escondido.

No mesmo aviso:

- **Enviar arquivo...** manda um arquivo deste computador para quem está acessando. Lá ele fica em
  `Downloads\Ponte`. O botão não aparece quando quem acessa usa o aplicativo do celular, que não
  recebe arquivos.
- Uma linha avisa quando **o som deste computador está sendo transmitido** (seção 8).

O Ponte aceita uma sessão por vez em cada computador. Fechar o Ponte encerra a sessão.

## 6. Senha de acesso

A senha permite entrar sem ninguém aceitar o pedido. Em **Senha de acesso...**:

- A senha precisa ter pelo menos 8 caracteres. Não repita senha de outro serviço.
- **Remover senha** volta ao modo em que toda conexão precisa de aceite (ou de autorização salva).
- Cinco senhas erradas seguidas suspendem o acesso por senha por dez minutos.
- Ao trocar ou remover a senha, o Ponte pergunta se os computadores que ficaram autorizados usando
  a senha anterior continuam liberados.

## 7. Computadores salvos e entrada sem aprovação

### Salvar um computador

Em **Salvar...**, informe um nome (Escritório, Casa, Loja) e o endereço. A opção **Entrar sem
aprovação nas próximas vezes** já vem marcada. O computador passa a aparecer em "Computadores
salvos", e dois cliques no nome conectam.

Selecionar uma conexão recente antes de clicar em **Salvar...** já preenche o endereço.

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

O computador acessado precisa estar ligado, com a sessão do Windows aberta (não bloqueada) e com o
Ponte aberto. Na tela de bloqueio ou de login o Ponte não mostra nem controla nada. A cópia de uso inicia minimizada após o login no Windows; a opção pode ser desligada nas configurações.

## 8. A janela da sessão

A janela mostra a tela do outro computador, ajustada ao tamanho da janela. O título traz o nome e o
endereço dele; a barra de baixo mostra a versão do Ponte de lá, o tamanho da tela, quadros por
segundo e o consumo de rede.

| Comando | O que faz |
|---|---|
| Tela | Escolhe qual monitor do outro computador aparece (quando há mais de um). |
| Qualidade | Baixa (mais rápida), Média ou Alta. Em conexão lenta, a Baixa responde melhor. |
| Teclas | Envia combinações que o seu Windows interceptaria: Alt+Tab, tecla Windows, Windows+R, Windows+D, Ctrl+Shift+Esc e Print Screen. |
| Tela cheia | A tela do outro computador ocupa o monitor inteiro, sem borda e sem barras. A barra de comandos aparece por cima da imagem quando o mouse encosta no alto da tela; ali **Sair da tela cheia** volta ao normal. |
| Enviar arquivo... | Envia um arquivo, que fica em `Downloads\Ponte` do outro computador. Limite de 8 GB por arquivo. |
| Salvar imagem... | Grava em PNG o que a tela remota está mostrando. |
| Área de transferência | Ligado, o texto copiado num computador pode ser colado no outro. Só texto. |
| Som | Ligado, você ouve o que o outro computador está tocando. Veja abaixo. |
| Desconectar | Encerra a sessão. |

Com a janela em foco, todas as teclas vão para o outro computador, inclusive Tab, Alt e F4.
Ctrl+Alt+Del não é enviado.

### Ponteiro do mouse

Sobre a imagem, o ponteiro do seu mouse assume a forma que o ponteiro tem **no outro computador**:
seta, barra de texto, mão sobre um link, setas de redimensionar. Assim dá para ver o que o clique vai
fazer sem esperar a imagem. O clique cai exatamente no ponto da tela de lá que está debaixo do seu
ponteiro, inclusive com a janela menor que a tela remota.

Se o outro computador esconder o ponteiro (alguns programas fazem isso enquanto se digita), aqui
aparece a seta comum. Com um Ponte anterior à 1.5.0 do outro lado, o ponteiro é sempre a seta comum.

### Som

O botão **Som** liga e desliga o som do outro computador. O que chega é o que ele está tocando nos
alto-falantes ou no fone (música, vídeo, avisos do Windows); o microfone de lá **não** é transmitido.
O outro computador continua tocando normalmente.

- A escolha fica guardada para as próximas sessões.
- Na qualidade **Alta** o som vai sem compressão (cerca de 1,5 Mbit/s enquanto houver som). Em
  **Média** e **Baixa** ele vai comprimido (cerca de 0,4 Mbit/s), com um chiado leve. Em silêncio
  nada é enviado.
- O som chega com um pequeno atraso, de dois a três décimos de segundo.
- O botão fica apagado quando o outro computador tem um Ponte anterior à 1.5.0, quando o dono
  desligou a transmissão de som nas Configurações de lá, ou quando o outro lado é este mesmo computador.

## 9. Abrir como administrador

O Windows não deixa um programa comum mandar mouse e teclado para a janela de um programa aberto
como administrador. Se o computador acessado estiver com um programa assim em primeiro plano, os
cliques de quem acessa não têm efeito, e a janela da sessão avisa.

Para controlar esses programas, o Ponte **do computador acessado** precisa estar como administrador:

- Clique em **Abrir como administrador** no rodapé, ou
- deixe marcada, em Configurações, a opção "Abrir como administrador...", que faz isso a cada abertura.

Aparece o aviso normal do Windows pedindo confirmação. A opção de Configurações só age sozinha em
conta de administrador; numa conta comum, use o botão, e o Windows pede a senha de um administrador.
Quando o Ponte está elevado, o título mostra "(administrador)".

## 10. Configurações

| Opção | Significado |
|---|---|
| Servidor de retransmissão | Endereço e porta do servidor que permite conectar por ID. Em branco, só funcionam conexões diretas por IP. |
| Chave do servidor | Chave de acesso do servidor de retransmissão. Precisa ser a mesma configurada nele. |
| Aceitar conexões diretas nesta porta | Liga ou desliga a conexão por IP e define a porta (padrão 7411). |
| Abrir como administrador | Veja a seção 9. |
| Atualizar o Ponte automaticamente | Veja a seção 11. |
| Transmitir o som deste computador a quem o acessa | Desmarcado, ninguém que acessa este computador ouve o som dele. |
| Aparência | **Acompanhar o Windows** usa o tema claro ou escuro escolhido no Windows; **Clara** e **Escura (modo noturno)** fixam um deles. |

As mudanças valem na próxima vez que o Ponte for aberto.

## 11. Atualização automática

O Ponte procura versão nova pouco depois de abrir e a cada meia hora. Quando encontra, baixa,
confere a assinatura e troca o arquivo sozinho assim que não houver sessão em andamento; ele fecha
e reabre já na versão nova. A linha de versão no rodapé mostra o andamento:

- "versão mais recente"
- "baixando a versão X"
- "versão X pronta; instala quando a sessão terminar"
- "atualizado agora"

Uma atualização que não tenha a assinatura correta é ignorada, e o motivo aparece nessa mesma linha.
A versão 1.0.0 não tem atualização automática e precisa ser trocada à mão por um `Ponte.exe` novo.

## 12. Aviso de identidade alterada

O Ponte guarda a identidade de cada computador a que você se conecta. Se, numa conexão posterior, o
mesmo endereço responder com outra identidade, aparece um aviso perguntando se você quer conectar
assim mesmo.

- **Em conexão por IP** isso é esperado quando outro computador passou a usar aquele IP, ou quando o
  Ponte de lá perdeu a configuração.
- **Em conexão por ID** não deveria acontecer, porque o ID é calculado a partir da identidade. Trate
  como sinal de que outro equipamento está respondendo no lugar.

Na dúvida, recuse e confirme com a pessoa do outro lado.

## 13. Problemas comuns

| Mensagem ou sintoma | Causa e solução |
|---|---|
| "Esse computador não está online." | O Ponte não está aberto no outro computador, ou ele está sem internet. |
| "O outro computador não respondeu a tempo." | Ninguém aceitou o pedido, ou a conexão dele caiu. |
| "O pedido foi recusado." | Alguém clicou em Recusar, ou os 30 segundos passaram. |
| "Já existe uma sessão em andamento neste computador." | Outra pessoa já está conectada lá. |
| "Muitas tentativas erradas. O acesso por senha está suspenso..." | Cinco senhas erradas seguidas. Espere dez minutos ou peça permissão. |
| "Sem contato com o servidor de retransmissão; tentando de novo." | Falta de internet ou servidor fora do ar. O Ponte tenta de novo a cada 5 segundos. |
| "O servidor de retransmissão recusou a chave de acesso." | A chave em Configurações não é a do servidor. |
| "A porta 7411 já está em uso; conexões diretas desativadas." | Outro programa usa a porta. Troque a porta em Configurações; a conexão por ID não é afetada. |
| "O Ponte já está aberto neste computador." | Já existe uma janela do Ponte aberta para o mesmo usuário. |
| Cliques e teclas não fazem efeito | Há um programa de administrador em primeiro plano no outro computador (seção 9). |
| Imagem parada | O outro computador está bloqueado, na tela de login ou com um aviso do Windows pedindo confirmação. |
| Botão Som apagado | O Ponte de lá é anterior à 1.5.0, ou a transmissão de som está desligada nas Configurações de lá. |
| Som ligado, mas nada se ouve | O outro computador não está tocando nada, está sem saída de som (nesse caso o aviso de sessão de lá diz isso) ou está com o som mudo. |
| Aviso do Windows pedindo o .NET | Falta o .NET Desktop Runtime 10 (seção 2). |
| O número de versão não muda | A atualização automática está desligada em Configurações, o arquivo está numa pasta sem permissão de gravação, ou há uma sessão em andamento. |

## 14. Onde ficam os dados

| O quê | Onde |
|---|---|
| Configuração, identidade, listas de salvos e autorizados | `%APPDATA%\Ponte\config.json` |
| Miniaturas dos computadores acessados | `%APPDATA%\Ponte\miniaturas` |
| Arquivos recebidos | `Downloads\Ponte` do usuário |

A identidade é protegida pela conta do Windows: copiar o `config.json` para outro usuário ou outro
computador não leva o ID junto, e o Ponte cria um ID novo lá. Apagar a pasta `%APPDATA%\Ponte`
também gera um ID novo e apaga as listas.
