> Histórico: as lives e os amigos saíram do Ponte na 1.11.0 e agora ficam no Exilados (veja SEM-LIVES-1.11.0.md).

# Ponte 1.9.1 — lives e amigos

## Uso

Na janela principal, abra **Lives e amigos...**. Ela tem sua própria janela e pode ficar aberta ou minimizada enquanto a janela principal está escondida no ícone do Windows.

Escolha **Uma tela** e selecione o monitor, ou **Um programa específico** e selecione uma das janelas abertas. Atualizar recarrega a lista de programas. Escolha a qualidade e se deseja som, depois comece a transmissão. A seleção fica bloqueada enquanto a live está ativa; pare-a para trocar de origem.

Em uma tela, o áudio é o som geral do computador. Em um programa, o áudio é somente o processo proprietário da janela e os subprocessos dele. O áudio de outras janelas do mesmo programa pode pertencer à mesma árvore de processos: por exemplo, esse modo não promete isolar uma aba do navegador. Não há captura do microfone. Quem assiste não recebe controle remoto.

Mantenha o programa aberto e restaurado. Minimizar, fechar ou substituir sua janela interrompe a captura; o Ponte não passa a mostrar o desktop. Redimensionar a janela pode causar uma breve reconexão. Jogos em tela cheia exclusiva ou com conteúdo protegido podem precisar funcionar em janela/sem bordas ou impedir a captura.

## Amigos e convites

1. Clique em **Adicionar amigo: gerar e copiar link** e envie o link à pessoa. Cada link cadastra uma amizade, expira em 24 horas e perde a validade quando outro link de cadastro é gerado por esse computador.
2. A pessoa abre o link, digita o próprio nome e salva a amizade. O nome aparece na lista do Ponte.
3. Nas próximas lives, selecione o amigo e clique em **Convidar para esta live**. O amigo recebe o pedido na página de amigos e pode aceitar ou recusar. Ao aceitar, a live abre em outra aba.
4. Os convites expiram em dez minutos e só podem liberar a transmissão enquanto ela ainda existe. A página consulta novos convites a cada dez segundos. O status de página aberta é aproximado e expira após 45 segundos sem contato.

O amigo deve manter a página de amigos aberta e conectada para receber os pedidos. O botão de avisos solicita a permissão do navegador para mostrar uma notificação enquanto a página está aberta; não existe serviço de push quando ela está fechada. Salve a página nos favoritos. Os dados ficam no navegador utilizado: outro aparelho, janela privada ou limpeza dos dados exige novo cadastro.

**Remover** revoga o recebimento de próximos convites. Isso não corta uma transmissão que a pessoa já abriu por um link válido; encerrar a live invalida o link dela. **Esquecer neste navegador** apaga apenas a credencial guardada ali.

## Implementação

- `BroadcastDialog.cs`: janela, origem, qualidade, lista de amigos, convite e encerramento.
- `BroadcastWindow.cs`: enumeração de janelas e validação de HWND, PID e instante de criação do processo. Não seleciona outro processo quando um identificador é reciclado.
- `WindowVideoPump.cs`: FFmpeg `gfxcapture`, baseado em Windows Graphics Capture, captura o HWND. Um único último quadro é repetido no ritmo escolhido, mantendo vídeo e áudio mesmo em um programa parado. Não há fila crescente de imagens antigas. O Windows pode desenhar a borda de captura.
- `ProcessAudio.cs` e `Audio.cs`: WASAPI process loopback com `ActivateAudioInterfaceAsync`, `VAD\Process_Loopback` e inclusão da árvore de processos. A captura usa PCM estéreo de 16 bits a 48 kHz. Requer a API presente no Windows build 20348 ou superior; para o Windows de uso pessoal, use Windows 11. Falha de isolamento não ativa o som geral como alternativa.
- `Broadcast.cs`: codificação H.264 pela GPU e Opus, envio RTSPS ao MediaMTX, entrega WebRTC/HLS e parada do envio. Os pipes locais de imagem e áudio aceitam somente o usuário atual.
- `BroadcastFriends.cs`: pedidos de amigos por HTTPS, assinados pela chave ECDSA do computador já registrado no relay. O servidor verifica a assinatura, o horário e um nonce descartável, recusando repetições. As credenciais de convite não passam pela conexão TCP de controle. A lista é paginada.
- `relay/friends.js`: cadastro, amizade persistente, presença aproximada e convites temporários. A autorização do computador permanece baseada na prova de posse de sua chave, implementada no relay.
- `web/amigos/`: página de cadastro, recepção e aceite de convites.

As credenciais de amizade têm 256 bits aleatórios, são enviadas no corpo de pedidos HTTPS e guardadas no armazenamento local do navegador. O link inicial usa fragmento, removido da barra de endereço após a leitura. O banco do servidor guarda apenas o hash da credencial; não existe endpoint público de listagem. As chamadas do navegador exigem JSON e rejeitam uma origem externa, com limite de tamanho e frequência. A live só é devolvida depois do aceite.

## Servidor e reprodução

O relay agora precisa de `relay.js` e `friends.js` na mesma pasta. O serviço usa `StateDirectory=ponte-relay`, criando armazenamento persistente para o usuário dinâmico do systemd. O banco padrão fica em `/var/lib/ponte-relay/amigos.json`, com escrita atômica; `PONTE_FRIENDS_FILE` permite escolher outro caminho. Faça backup desse arquivo para preservar a lista. A lista pertence à identidade do computador que convida; não é sincronizada pela conta FilaBox.

O nginx encaminha `POST /ponte/ws/amigos` para a porta local 7413. A página fica em `/ponte/amigos/`. Se o domínio mudar, ajuste `PONTE_PUBLIC_ORIGIN` para a origem HTTPS correspondente. Não exponha a porta interna 7413 diretamente à internet.

`relay/instalar-amigos.sh` instala a integração no servidor atual com backup e retorno à configuração anterior se a verificação falhar. Requer enviar previamente `relay.novo.js` e `friends.novo.js`. O script é específico dos caminhos do servidor Ponte atual. `web/publicar-web.ps1` publica e confere os arquivos da página. `publicar.ps1` continua responsável pelo executável Windows e pela atualização assinada.

## Verificação

Execute os testes Node com `node --test relay/friends.test.js relay/update.test.js web/test/unidade.test.mjs`. Eles incluem cadastro de uso único, persistência, credencial armazenada apenas como hash, limites de linha, isolamento entre proprietários, aceite/recusa, expiração, remoção, origem HTTP e integração entre host autenticado e navegador.

O executável oferece `--autoteste-live pasta`, que abre uma janela de teste, cobre-a com outra janela e confere que a imagem da janela sobreposta não entrou na captura. Também reproduz dois sons baixos e curtos em processos diferentes e mede a presença do som escolhido e a ausência do som alheio. Com `--relay filabox3d.com.br:7412`, testa cadastro, convite, aceite e envio de vídeo/áudio do programa pelo servidor real, incluindo uma janela sem movimento. Essa validação usa processos locais e o servidor público; não equivale a um teste entre dois computadores independentes.
