> Histórico: as lives e os amigos saíram do Ponte na 1.11.0 e agora ficam no Exilados (veja SEM-LIVES-1.11.0.md).

# Ponte 1.10.1 — imagem da live e volume

O visualizador nativo passa a criar o controle de imagem na thread da interface antes de iniciar a recepção. Antes, o primeiro quadro podia chegar enquanto o controle estava oculto e sem identificador de janela. O pedido de redesenho ficava marcado como pendente sem ter sido agendado; os próximos quadros continuavam chegando, mas a imagem exibida não avançava. A marcação agora só acontece quando existe uma janela e é liberada se o agendamento falhar.

O recebimento também monitora a chegada de quadros: quinze segundos sem o primeiro quadro ou seis segundos sem outro quadro encerram o decodificador e reconectam. Uma falha inicial ou um travamento troca a decodificação acelerada por software nessa janela. Uma saída de vídeo encerrada não deixa apenas o áudio rodando indefinidamente. Os registros de recuperação ficam em `%APPDATA%\Ponte\sessoes.txt`, sem o código privado da live.

Na barra inferior da janela de reprodução, **Volume** regula de 0 a 100%. A regulagem reduz as amostras PCM somente daquela live, sem modificar o volume do Windows, o áudio de outras lives ou o som enviado pelo transmissor. O botão **Som ligado/desligado** continua disponível e preserva a posição do controle ao reativar. Cada janela começa em 100%.

O autoteste `--autoteste-amigos pasta --relay filabox3d.com.br:7412 --com-som` usa uma janela que muda de cor e verifica quadros efetivamente desenhados, cores diferentes recebidas, áudio, regulagem PCM e recuperação após suspender apenas o decodificador criado pelo próprio teste. O teste ocorre com dois perfis no mesmo PC através do servidor público; a confirmação em dois PCs físicos permanece separada.
