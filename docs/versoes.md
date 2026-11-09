# Histórico de versões

O que entrou em cada versão do Ponte. O número do Windows manda; o Android acompanha desde a 1.5.0, e
o navegador desde a 1.8.x. Quando só um dos programas mudou, isso está dito na linha.

Regra que vale desde o começo: **toda mudança sai com um número novo**, porque uma cópia instalada só
aceita versão maior que a dela, e dois executáveis com o mesmo número tornam impossível saber o que
está em cada computador. Veja [operacao.md](operacao.md).

## 1.17 — conta obrigatória e telas em janelas

| Versão | O que mudou |
|---|---|
| **1.17.4** | O "Entrar com o Google" abria o Explorador de Arquivos quando o Ponte estava como administrador. Agora o endereço vai pela área de trabalho do Windows e abre no navegador padrão (`DesktopShell.cs`). |
| **1.17.3** | **Simular a disposição das telas de lá**: as janelas das telas ficam na mesma posição do Windows do computador acessado, ampliadas para aproveitar o monitor daqui. Fica marcado para as próximas conexões. |
| **1.17.2** | A janela da tela principal ficou igual às outras: só imagem, com os comandos aparecendo ao encostar o mouse no alto. O mouse atravessa de uma janela de tela para outra, então dá para arrastar uma janela de lá entre monitores. |
| **1.17.1** | Android: o teclado abre sozinho ao tocar num campo de texto do computador (mensagem `TextFocus`); botões "1" e "2" para trocar de monitor na barra; a barra abre recolhida e a seta pode ser arrastada para qualquer canto. |
| **1.17.0** | **Conta obrigatória**: só contas liberadas pelo dono usam o Ponte, dos dois lados, por um bilhete assinado pelo servidor (veja [conta.md](conta.md)). **Ctrl+Alt+Del** de verdade, pelo serviço do Windows. O botão do Exilados saiu. |

## 1.13 a 1.16 — telas, celular acessado e serviço

| Versão | O que mudou |
|---|---|
| **1.16.0** | Cada tela do computador acessado pode abrir numa janela própria (menu **Tela**), e o `Settings` ganhou o pedido de "todas as telas numa imagem só". |
| **1.15.0** | **Serviço do Windows** opcional, que atende antes do login e no desktop protegido; retomada de sessão autenticada depois de uma queda; **reinício remoto**; **qualidade automática** conforme a conexão. Veja [SERVICO-E-CONEXAO-1.15.0.md](SERVICO-E-CONEXAO-1.15.0.md). |
| **1.14.1** | Fechar a janela mantém o Ponte no ícone perto do relógio. Veja [BANDEJA-1.14.1.md](BANDEJA-1.14.1.md). |
| **1.14.0** | **O celular Android também pode ser acessado**, por um computador, outro celular ou o navegador. Botões Voltar, Início e Apps no Ponte do Windows. |
| **1.13.0** | Desconectar depois de um tempo sem uso (vale o de quem acessa); seta para recolher os comandos no navegador. |

## 1.8 a 1.12 — navegador, iPhone e lives

| Versão | O que mudou |
|---|---|
| **1.12.1** | **Ponte para iPhone**: aplicativo de verdade, instalado de graça pelo SideStore. |
| **1.12.0** | Ponte no navegador faz o mesmo que o Android. |
| **1.11.0** | **Lives e amigos saíram do Ponte** e foram para o Exilados. Veja [SEM-LIVES-1.11.0.md](SEM-LIVES-1.11.0.md). |
| **1.10.1** | Corrige imagem congelada da live; volume. Veja [LIVES-1.10.1.md](LIVES-1.10.1.md). |
| **1.10.0** | Convites no programa e entrada direta em lives de amigos. Veja [LIVES-1.10.0.md](LIVES-1.10.0.md). |
| **1.9.1** | Lives de programas com áudio isolado e amigos. Veja [LIVES-1.9.1.md](LIVES-1.9.1.md). |
| **1.8.5 / 1.8.6** | Botão "Procurar atualização"; transmissão sem tela preta. |
| **1.8.4** | Aviso de versão nova e botão "Atualizar" com o aplicativo aberto (Windows e Android). |
| **1.8.0 a 1.8.3** | Transmissão de tela por link; assistir dentro do Ponte; leitor de QR no Android; o link abre direto no aplicativo. Ponte no navegador (iPhone). |

## 1.5 a 1.7 — vídeo, conta e aplicativo Android

| Versão | O que mudou |
|---|---|
| **1.7.0 / 1.7.1** | Conta **opcional** (a do FilaBox) com a lista de computadores salvos; registro de sessões com o motivo de cada queda; bolinha verde (online) e vermelha nos computadores salvos. |
| **1.6.7** | Inicia só no ícone perto do relógio; tela cheia sem erro. |
| **1.6.6** | Perfis de transmissão; a sessão não cai ao trocar de vídeo. |
| **1.6.5** | Erro inesperado fica registrado (`erros.txt`) e não fecha o Ponte. |
| **1.6.4** | O aviso de sessão ativa abre minimizado na barra de tarefas. Primeira versão do Android com o mesmo número. |
| **1.6.3** | Abre sempre como administrador, também ao ligar o computador. |
| **1.6.2** | Atualização automática volta a funcionar no modo escuro do Windows. |
| **1.6.0** | **Vídeo H.264/H.265 pela GPU**, 4:4:4, 30/60/120 FPS e ultra baixa latência. O FFmpeg é baixado à parte (`VideoRuntime`). |
| **1.5.5** | O aplicativo Android é oferecido pelo programa do Windows (QR na rede local). |
| **1.5.3 / 1.5.4** | Gestos: segurar e arrastar leva a janela; tocar duas vezes e arrastar não maximiza. |
| **1.5.1** | Ponteiro no modo Toque (`CursorPos`) e tela cheia de verdade. |
| **1.5.0** | **Ponteiro do mouse de lá, som, modo noturno e miniaturas.** `Caps` nasceu aqui. |

## 1.0 a 1.4 — o começo

| Versão | O que mudou |
|---|---|
| **1.4.0** | Início com o Windows, minimizado. |
| **1.3.0** | Mensagem `Text` (texto pronto), para o teclado do celular. Aplicativo Android 1.0.0. |
| **1.2.0 / 1.2.1** | Chave e assinatura do visualizador: **entrada direta** por computador autorizado. |
| **1.1** | Ajustes. |
| **1.0.0** | Primeira versão. **Não tem atualização automática**: precisa ser trocada à mão. |

## Compatibilidade entre versões

O protocolo foi feito para conviver: campos JSON novos são ignorados por quem não os conhece, e
tipos de mensagem novos também. O que cada combinação perde está na seção 7 de
[protocolo.md](protocolo.md).

Dois cortes importantes:

- **1.17.0**: os dois lados precisam de conta liberada. Um Ponte anterior à 1.17.0 não manda bilhete,
  e por isso **não entra** num computador 1.17.0 ou mais novo.
- **1.5.0**: antes dela não havia `Caps`; sem ela não há ponteiro de lá nem som.

## Números por programa

| Programa | Versão | Onde o número fica |
|---|---|---|
| Windows | 1.17.4 | `<Version>` em `Ponte.csproj` |
| Android | 1.17.1 (`versionCode` 19) | `android/app/build.gradle.kts` |
| Navegador | 1.17.0 | `VERSION` em `web/app.js` |
| iPhone | 1.12.1 | `ios/project.yml` e `web/ios.json` |
