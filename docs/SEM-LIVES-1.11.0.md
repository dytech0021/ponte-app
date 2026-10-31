# Ponte 1.11.0: lives e amigos saem do Ponte

Data: 16/10/2026.

As lives (transmissão de tela) e os amigos do Ponte passaram para o **Exilados**
(https://filabox3d.com.br/exilados/, repositório `dytech0021/exilados`), que junta texto, voz e lives de um grupo
de amigos. O Ponte volta a ser só acesso remoto.

## O que mudou

| Parte | Antes (1.9.1 a 1.10.1) | Agora (1.11.0) |
|---|---|---|
| Windows | Janela "Lives e amigos...", assistir a lives de amigos, convites, avisos de amigos | Botão "Lives no Exilados..." abre o site do Exilados. Link ou código de live antiga mostra o aviso. |
| Android | "Assistir transmissão" (leitor de QR Code e tela de assistir), links `/ponte/assistir` | Botão "Lives no Exilados" abre o site. Sem câmera e sem a biblioteca de QR Code (APK menor). |
| Navegador | Páginas `/ponte/assistir/`, `/ponte/amigos/` e `/ponte/baixar/` | Só o acesso remoto. Link de live antiga mostra o aviso. |
| Servidor de retransmissão | Comandos `TRANSMITIR`, `PARAR`, `ESPECTADORES`, autorização do MediaMTX e cadastro de amigos | Sem nada disso. Pedidos de transmissão de versões antigas ficam sem resposta, e o programa antigo avisa que o servidor não tem transmissão de tela. |
| MediaMTX | Caminhos `ao/...` autorizados pelo relay | Só os caminhos `ex/...` do Exilados, autorizados pelo exilados-api. A configuração dele mora agora no repositório do Exilados. |

Arquivos removidos: `Broadcast.cs`, `BroadcastDialog.cs`, `BroadcastFriends.cs`, `BroadcastWindow.cs`,
`LiveFriendDialogs.cs`, `LiveSelfTest.cs`, `NativeLiveSelfTest.cs`, `SelfTestBroadcast.cs`, `StreamViewerForm.cs`,
`WindowVideoPump.cs`, `ProcessAudio.cs`; no Android, `AssistirActivity`, `LerQrActivity`, `StreamLink`, `QrReader` e
os testes deles; na web, `assistir/`, `amigos/` e `baixar/`; no relay, `friends.js` e a configuração do MediaMTX.

## Convivência com as versões anteriores

- Um Ponte 1.10.x que ainda não se atualizou continua acessando computadores normalmente. O botão "Lives e amigos"
  dele passa a dar erro ("o servidor não respondeu ... versão sem transmissão de tela"), e a lista de amigos fica
  vazia. Ao se atualizar para a 1.11.0, o botão vira "Lives no Exilados...".
- A configuração antiga (`Broadcast` no `config.json`) é ignorada.

## Publicação

- Android 1.11.0: publicado deste PC (`android\publicar-apk.ps1`).
- Navegador 1.11.0: publicado deste PC (`web\publicar-web.ps1`).
- Relay: publicado deste PC (seção 3.4 do `operacao.md`).
- **Windows 1.11.0: falta publicar no outro PC**, que tem a chave de atualização do Ponte (`publicar.ps1`).
