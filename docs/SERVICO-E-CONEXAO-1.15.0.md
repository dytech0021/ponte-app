# Ponte 1.15.0 — serviço, reconexão e qualidade automática

## Instalar ou remover o serviço

No computador que será acessado, atualize o Ponte e abra **Configurações → Serviço: acesso antes do login**. A instalação é opcional e exige administrador. Antes, defina uma senha de acesso ou autorize pelo menos um computador. Feche pelo menu do ícone na bandeja e reabra o Ponte depois de instalar. O mesmo ID e as mesmas autorizações são mantidos; o serviço nunca aceita um desconhecido automaticamente.

O serviço `PonteHost` inicia com o Windows, antes do login, e supervisiona um agente na sessão do console. O agente atende quando não há aplicativo do usuário ou quando a área de trabalho fica bloqueada/protegida. O Windows ainda exige suas credenciais normais de login: o Ponte só transmite a tela e as entradas, não remove a senha do Windows.

Enquanto a área de trabalho comum estiver disponível e o aplicativo estiver aberto, o host continua no aplicativo, preservando as funções atuais. A passagem para o agente pode interromper brevemente a conexão; clientes novos e autorizados tentam retomá-la. O agente usa captura JPEG compatível e não captura áudio. As opções de GPU permanecem no aplicativo comum. Arquivos recebidos pelo agente ficam em `%PROGRAMDATA%\PonteHost\host\recebidos`; na sessão comum continua valendo Downloads\Ponte.

O serviço aparece nas Configurações com a opção de remoção. Removê-lo desativa o acesso anterior ao login e conserva um backup protegido do perfil. Administrador é necessário para gerenciar o aplicativo enquanto esse serviço estiver instalado. O executável do serviço fica em `%ProgramFiles%\PonteHost` e é atualizado, com backup para rollback, quando a nova versão do aplicativo é aberta como administrador.

**X/Alt+F4** continuam guardando a janela na bandeja. **Fechar o Ponte** pelo menu da bandeja encerra o aplicativo e pausa também o atendimento do serviço até o aplicativo ser reaberto ou o Windows reiniciar. Uma sessão pelo agente mostra um aviso e ícone de controle ao usuário conectado; **Encerrar acesso remoto** interrompe o acesso. Não há janela privilegiada SYSTEM na área de trabalho comum.

## Reconexão

**Configurações → Reconectar automaticamente após quedas de rede** vem ativado. Em computadores novos dos dois lados, uma queda inesperada mantém a janela e tenta reconectar por até dez minutos. Fechar a janela cancela as tentativas. A identidade do destino é conferida novamente; mudança de identidade ou revogação interrompe a retomada.

A retomada usa a chave já autorizada ou a senha que foi digitada nessa sessão, mantida apenas na memória. Um aceite temporário, sem autorização ou senha, não vira acesso permanente. Um encerramento explícito pelo usuário remoto ou por falta de uso não é considerado queda. As preferências de imagem e áudio são reenviadas ao retomar. Arquivos interrompidos precisam ser enviados novamente.

## Reinício remoto

Na janela de acesso: **Teclas → Reiniciar computador remoto**. O operador confirma antes do pedido, e o host pode recusar com **Configurações → Permitir reinício remoto em sessões autenticadas** desmarcado. A ação exige uma sessão autenticada; o ID sozinho não permite reiniciar.

O pedido usa o reinício normal do Windows, sem forçar o fechamento de trabalho não salvo. Aplicativos, atualizações do Windows e políticas locais podem atrasar ou impedir o reinício. A janela aguarda a volta do computador quando possui autorização para reconectar. Para voltar antes do login é necessário instalar o serviço no destino. A ação não se aplica a celulares nem a versões antigas do Ponte.

## Qualidade automática e modos manuais

Na janela de acesso: **Qualidade → Ajustar automaticamente à conexão**. A opção é salva para as próximas sessões, mas começa desmarcada para preservar o comportamento JPEG de 20 FPS existente.

O ajuste acompanha o tempo de ida e volta das mensagens e o custo de desenho. Após congestionamento persistente, reduz qualidade JPEG, bitrate, FPS e resolução. Uma rede estável recupera os valores gradualmente. As preferências manuais são os limites superiores; o ajuste não troca o codec, o chroma ou a preferência de baixa latência. Desmarcar a opção restaura imediatamente os valores escolhidos. Uma tela parada não é interpretada como rede ruim apenas por emitir poucos quadros.

## Implementação e segurança

- `WindowsHost.cs`: supervisor SCM, agente do console, proteção de arquivos, lease exclusivo do host, aviso sem privilégios, interrupção local e instalação/remoção.
- `ViewerSession.cs`: retomada autenticada, preservação de janela e preferências, sinais de vida e reinício.
- `AutomaticQuality.cs`: redução com histerese e recuperação gradual, sem modificar o perfil manual.
- `RemotePower.cs`: ação de reinício com argumentos fixos; testes substituem a ação para não reiniciar o PC real.

O perfil do serviço protege o PFX com DPAPI de máquina e ACL restrita a SYSTEM/administradores. Tokens da conta e a lista pessoal de computadores salvos não são copiados ao serviço. O diretório de coordenação é somente leitura para usuários comuns; a interrupção local usa um pipe que só aceita `STOP`, nega acesso por rede e nunca recebe caminhos, código ou comandos de shell.

A opção de reconexão é negociada por capacidade. Versões antigas continuam com os controles manuais e o protocolo anterior, sem receber comandos de gerenciamento que não suportam. Ctrl+Alt+Del remoto por atenção segura não faz parte desta versão.

## Verificações e limites da validação

`--autoteste-ciclo pasta` testa redução/recuperação, dimensões JPEG e H.264, ping, retomada após interrupção real do transporte, recusa de reinício, reinício simulado, encerramento explícito e revogação. Nenhum teste reinicia o computador real.

`--autoteste-servico pasta`, como administrador, instala um serviço com perfil separado, confere o agente real SYSTEM no console, recebe vídeo pelo servidor público, verifica exclusão entre hosts, autorização preservada na passagem para o aplicativo e interrupção pelo pipe local. Remove o serviço de teste ao terminar e se recusa a substituir uma instalação de produção.

Os testes de serviço ocorreram neste PC, com a área de trabalho já aberta. A passagem real pela tela de login após reboot, o desktop seguro do UAC, a troca de usuário, políticas corporativas e dois PCs físicos precisam de validação operacional separada. Não foi reiniciado nem bloqueado o Windows do usuário para testar. A arquitetura foi implementada para esses contextos, mas esses cenários não são apresentados como já validados.

A suíte geral conferiu os modos JPEG, H.264, H.265, 4:4:4, arquivos, áudio, área de transferência, autenticação e inatividade. Dois testes de arraste de janela falharam neste ambiente tanto na 1.15.0 quanto na 1.14.1, em uma comparação separada. Essa limitação preexistente não é apresentada como corrigida nesta versão. A suíte Node de relay e núcleo web passou nos 34 testes.
