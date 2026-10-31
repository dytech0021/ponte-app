> Histórico: as lives e os amigos saíram do Ponte na 1.11.0 e agora ficam no Exilados (veja SEM-LIVES-1.11.0.md).

# Ponte 1.10.0 — convites dentro do programa e entrada direta

## Adicionar amigos no Windows

Os dois computadores precisam estar no Ponte 1.10.0 ou posterior e conectados ao servidor.

Em **Lives e amigos → Adicionar por ID ou colar convite...**, use uma destas opções:

- **ID do Ponte:** envia uma solicitação de amizade ao programa do outro computador. Ao adicionar uma vez, a amizade aparece nas duas listas. A solicitação mostra o nome e o ID do remetente.
- **Link de amizade:** cole o link que o amigo gerou em **Adicionar amigo: gerar e copiar link**. O vínculo é feito diretamente com o Ponte, sem precisar cadastrar antes no navegador.
- **Amizade antiga no navegador:** abra a página de amigos onde ela foi salva, clique em **Vincular ao Ponte no Windows** e cole o código na janela do Ponte. Esse código vale por quinze minutos. O vínculo mantém o amigo na lista original, sem precisar enviar de novo o primeiro link.

O cadastro antigo só identificava o navegador: não havia como entregar um convite ao programa sem associar a amizade à identidade desse Ponte. A vinculação é feita uma vez.

## Entrar e convidar

A lista mostra **Ao vivo** para um amigo que iniciou uma transmissão. Selecione-o e clique em **Entrar na live**, ou dê dois cliques no nome. O vídeo abre em outra janela do próprio Ponte. Não existe pedido de aprovação por live entre amigos já adicionados.

Ao transmitir, **Convidar para esta live** continua disponível. No Ponte do destinatário aparece uma janela com **Assistir** e **Agora não**, mesmo com a janela principal minimizada e sem abrir a tela de lives. A verificação ocorre a cada cinco segundos enquanto o Ponte está aberto. Amigos vinculados somente ao navegador continuam recebendo lá até vincularem o programa.

Pelo navegador também existe **Entrar na live** quando o amigo está transmitindo, sem aguardar um convite. Um convite não é mais pré-requisito para um amigo assistir.

Adicionar uma amizade permite que os dois lados assistam às próximas lives um do outro. **Remover** desfaz a amizade nos dois lados e bloqueia novas consultas de acesso. Quem já abriu um link válido ainda pode assistir à live atual até ela ser encerrada, como no sistema de links existente. Essas permissões são somente para assistir; o acesso remoto ao computador mantém suas próprias regras.

## Implementação e persistência

`BroadcastFriends.cs` usa os pedidos HTTPS assinados existentes para solicitar amizade, vincular um link/código, consultar a caixa de entrada, responder a convites e obter a live de um amigo. `relay/friends.js` verifica que cada consulta e resposta pertence a um dos integrantes da amizade. A entrada direta resolve a transmissão atual pelo integrante remoto; não reutiliza o código de uma live anterior.

O banco mantém os amigos antigos e adiciona a identidade do segundo Ponte (`peer`). A mesma relação é projetada nas duas listas com o nome correto de cada lado. Pedidos de amizade por ID são persistidos por sete dias, para o destinatário poder receber quando abrir o programa. Convites de live continuam temporários, por dez minutos. Limites impedem solicitações ilimitadas; a interface exibe até três avisos ao mesmo tempo e descarta avisos expirados.

`MainForm.cs` consulta a caixa de entrada independentemente da janela de transmissão. `LiveFriendDialogs.cs` mostra os avisos e a entrada de ID/link. O botão de assistir abre `StreamViewerForm`, o mesmo visualizador de live já existente, com imagem e som. Nenhuma alteração permite controlar o computador de quem transmite.

O banco permanece em `/var/lib/ponte-relay/amigos.json`, com versão de estrutura 2 e escrita atômica. Faça backup dele junto com a configuração do relay. O procedimento de implantação continua em `relay/instalar-amigos.sh` e `web/publicar-web.ps1`. A versão anterior do documento, `LIVES-1.9.1.md`, explica a captura de janela, o isolamento de áudio e o envio de mídia.

## Validação

Os testes Node verificam amizade recíproca, entrada sem convite, troca do código entre lives, consultas de estranhos recusadas, remoção, convite nativo, aceite e recusa, pedidos persistentes por ID e vínculo de amigos antigos do navegador, além dos testes do relay e do núcleo web.

O comando `--autoteste-amigos pasta --relay filabox3d.com.br:7412` cria dois perfis isolados, abre a interface do destinatário e usa uma janela de teste como fonte. Confere a solicitação de amizade no programa, a lista de lives e o clique em **Entrar na live** com quadros efetivamente recebidos. Também minimiza a janela principal, envia um convite e verifica o aviso e a reprodução depois de clicar em **Assistir**. Registros e capturas ficam na pasta escolhida. Os perfis rodam no mesmo PC através do servidor público; isso não substitui a validação entre dois PCs físicos.
