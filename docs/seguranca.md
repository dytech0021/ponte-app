# Segurança

Este documento diz o que o Ponte 1.5.1 protege, como protege e o que ele **não** protege. Os
formatos exatos das provas e assinaturas estão em [protocolo.md](protocolo.md).

Termos: **visualizador** é o computador que acessa; **controlado** é o que é acessado.

## 1. Princípios

1. **Ninguém entra sem uma decisão do computador controlado.** Essa decisão é um aceite na tela,
   uma senha definida pelo dono, ou uma autorização que o dono concedeu antes e pode desfazer.
2. **Sessão ativa é sessão visível.** Enquanto alguém controla o computador, um aviso fica na tela
   com o botão de encerrar. Não existe modo oculto.
3. **O Ponte permanece visível.** A cópia de uso inicia minimizada com o Windows; a opção pode ser desligada nas configurações. Ele só fica acessível com uma sessão do Windows aberta e desbloqueada.
4. **O servidor de retransmissão não é confiável por projeto.** Ele não vê o conteúdo das sessões e
   não consegue fabricar uma atualização válida.

## 2. Identidade

Na primeira abertura cada computador cria um par de chaves ECDSA P-256 e um certificado autoassinado.

- A chave privada fica em `config.json`, cifrada com DPAPI para a conta do Windows. Copiar o arquivo
  para outro usuário ou outra máquina não leva a identidade junto.
- No aplicativo Android a chave privada fica no cofre do sistema (AndroidKeyStore) e não pode ser
  lida nem pelo próprio aplicativo, que só pede assinaturas. Desinstalar o aplicativo apaga a chave.
- O **ID de 9 dígitos** é calculado a partir da chave pública. Quem não tem a chave privada não
  consegue se registrar com aquele ID no servidor, que exige uma assinatura a cada registro.

### O ID é curto

Nove dígitos dão cerca de 900 milhões de combinações. Isso basta para um endereço, mas não para
uma identidade: com esforço de computação moderado, alguém consegue gerar uma chave cujo ID
coincide com um ID escolhido. Por isso o ID nunca é usado sozinho como prova:

| Onde | O que é usado de verdade |
|---|---|
| Reconhecer um computador a que você já se conectou | Impressão digital do certificado inteiro (SHA-256), guardada na primeira conexão. Se mudar, o Ponte avisa. |
| Reconhecer um computador autorizado a entrar direto | SHA-256 inteiro da chave pública do visualizador. |

O risco que sobra está na **primeira** conexão a um ID: se outro equipamento conseguiu uma chave
com o mesmo ID e tomou o registro no servidor, você se conectaria a ele. Nesse caso:

- você veria a tela dele, não a do computador que queria;
- ele receberia o que você digitasse e o texto que você copiasse com a sessão aberta;
- se você digitasse a senha de acesso, ele não a receberia, mas ficaria com material para testar
  senhas por tentativa fora da rede. Uma senha fraca seria descoberta assim.

Na primeira conexão a um computador, confirme com a pessoa do outro lado que a tela é a esperada
antes de digitar qualquer coisa sensível.

## 3. Sigilo e integridade da sessão

Toda sessão corre dentro de TLS 1.2 ou 1.3 negociado **entre os dois computadores**. No caminho por
ID, o servidor de retransmissão só copia bytes cifrados.

O que o servidor de retransmissão consegue saber: quais IDs estão online, o endereço IP de cada
lado, quando uma sessão começa e termina e o volume de dados. O que ele não consegue: ver a tela,
ouvir o som, ler as teclas, os arquivos ou a senha.

Um intermediário que tentasse abrir a sessão teria de apresentar outro certificado ao visualizador.
Isso é detectado de três formas: o ID não confere com o digitado, a impressão digital não confere
com a guardada, e as provas de senha e de autorização incluem o certificado visto por cada lado,
então deixam de conferir.

## 4. Quem pode entrar

### 4.1 Aceite na tela

O pedido mostra nome do computador, usuário, origem e endereço de quem pede. O botão padrão é
Recusar e o pedido expira em 30 segundos. O nome e o usuário são informados por quem pede e não
são verificados; o endereço, quando aparece, foi provado por assinatura.

### 4.2 Senha de acesso

- O Ponte guarda um derivado da senha (PBKDF2-HMAC-SHA256, 200.000 iterações, sal de 16 bytes), e
  não a senha.
- A senha não trafega. O visualizador prova que a conhece respondendo a um desafio novo a cada conexão.
- Cinco provas erradas seguidas suspendem o acesso por senha por dez minutos.
- Mínimo de 8 caracteres, imposto pela interface.

**Limite:** o derivado guardado no computador controlado equivale à senha para este protocolo. Quem
conseguir ler o `config.json` de lá consegue produzir uma prova válida sem saber a senha. O arquivo
fica no perfil do usuário, então isso exige já ter acesso àquela conta.

### 4.3 Computador autorizado (entrada direta)

O controlado guarda, na lista **Computadores autorizados**, o resumo da chave pública de cada
computador que ele liberou. Numa conexão, o visualizador assina um desafio novo com a própria chave
privada; se a assinatura confere e a chave está na lista, a sessão entra sem senha e sem aceite.

Um computador só entra na lista de dois jeitos:

| Jeito | Quem decide | Marca na lista |
|---|---|---|
| Alguém marca "Sempre permitir este computador" e aceita o pedido | Quem está na frente do controlado | "aceito aqui" |
| O visualizador, com o computador salvo, entra com a senha correta | Quem conhece a senha | "pela senha" |

Propriedades:

- Salvar um computador na lista de quem acessa não concede nada. A decisão é sempre do controlado.
- A autorização é da chave do computador, não do nome. Enviar a chave pública de um computador
  autorizado sem ter a chave privada dele não funciona; o autoteste confere esse caso.
- A assinatura inclui um desafio novo e o certificado do controlado, então não pode ser reaproveitada
  em outra conexão nem repassada a outro computador.
- Remover da lista vale a partir da conexão seguinte. Uma sessão já aberta continua até ser encerrada.
- Trocar ou remover a senha faz o Ponte perguntar se as autorizações concedidas "pela senha" continuam.
- A lista comporta no máximo 50 computadores.

**Consequência a ter em mente:** quem conhece a senha de acesso pode transformar esse conhecimento
em autorização permanente. Trocar a senha depois não desfaz a autorização, a menos que se responda
"não" à pergunta feita na troca ou se remova o computador da lista.

## 5. Durante a sessão

- O aviso de sessão ativa fica sempre por cima das outras janelas, sem botão de fechar, com nome,
  usuário, origem e hora de início. "Encerrar sessão" corta a conexão na hora.
- A janela principal registra como a pessoa entrou (senha, aceite na tela ou autorização salva) e,
  ao final, a hora de início e de término.
- Uma sessão por vez em cada computador.
- Fechar o Ponte encerra a sessão; ele pede confirmação antes.
- Ao terminar, todas as teclas que ficaram pressionadas são soltas.

### Som

Quem acessa pode pedir o som do computador controlado. O que vai é o que o computador está tocando
na saída de som padrão. **O microfone não é aberto nem transmitido.**

- O som só é transmitido a pedido do visualizador, dentro de uma sessão já aceita, e para quando a
  sessão termina.
- Enquanto estiver saindo, o aviso de sessão ativa mostra "O som deste computador está sendo
  transmitido".
- O dono pode desligar de vez em Configurações ("Transmitir o som deste computador a quem o
  acessa"). Aí o computador nem anuncia que tem som, e um pedido é ignorado.
- Vale lembrar que o som pode carregar o que a imagem não mostra: uma chamada em andamento, um
  áudio tocando em outra aba. Quem aceita uma sessão está deixando ouvir isso também.

### Miniaturas

Quem acessa guarda, em `%APPDATA%\Ponte\miniaturas`, uma imagem reduzida (320 × 180) da última tela
vista de cada computador, para reconhecê-lo na lista. Ela fica só no computador de quem acessou,
não é enviada a lugar nenhum e é apagada quando o computador sai da lista. Mesmo reduzida, é uma
imagem da tela de outra pessoa: quem tem acesso à conta do Windows de quem acessou consegue vê-la.

### Arquivos enviados pelo controlado

O botão **Enviar arquivo...** do aviso de sessão ativa manda um arquivo escolhido por quem está na
frente do computador controlado. O visualizador não tem como pedir nem escolher um arquivo de lá.

### Arquivos recebidos

- Vão sempre para `Downloads\Ponte`. O nome informado pelo outro lado é reduzido ao último
  componente, sem caracteres inválidos e sem nomes reservados do Windows, então não há como
  gravar fora dessa pasta.
- Nunca são abertos nem executados pelo Ponte.
- Um arquivo existente não é sobrescrito: o novo recebe um sufixo "(1)", "(2)".
- Um arquivo que chega incompleto ou maior que o anunciado é apagado.

### Programas de administrador

O Windows impede um programa comum de mandar mouse e teclado para a janela de um programa elevado.
O Ponte respeita essa barreira: para controlar esses programas ele próprio precisa ser aberto como
administrador, pelo aviso normal do Windows. Isso tem um custo: **um Ponte elevado dá a quem entra
o poder de um administrador naquele computador.** Use só onde for necessário.

## 6. Atualização automática

O programa baixa a versão nova do mesmo servidor de retransmissão, sem TLS, e confia apenas na assinatura.

- O manifesto traz versão, tamanho, SHA-256 e uma assinatura ECDSA P-256 sobre esses três valores.
- A chave pública que confere a assinatura está embutida no executável (`UpdateKey.cs`).
- A chave privada existe só na máquina de quem publica, cifrada com DPAPI para a conta do Windows dela.
- O executável baixado tem de bater com o tamanho e o SHA-256 assinados, e é conferido de novo
  logo antes de ser executado.
- Uma cópia nunca instala uma versão menor ou igual à que está em uso, o que impede reinstalar uma
  versão antiga com defeito conhecido.
- Uma versão nova que não consiga nem abrir não substitui a atual.

Com isso, nem o servidor nem alguém no meio do caminho consegue entregar um executável falso: o
pior que conseguem é impedir a atualização.

**A chave de assinatura é o ponto mais sensível do sistema.**

| Situação | Consequência |
|---|---|
| A chave se perde | Nenhuma cópia instalada aceita mais atualização. Cada computador teria de receber à mão um executável com outra chave pública. |
| A chave vaza, junto com o controle do servidor ou da rede | O atacante consegue instalar o que quiser em todas as cópias, inclusive nas que rodam como administrador. |

Hoje a chave está presa à conta do Windows de quem publica e não tem cópia de segurança utilizável
em outra máquina. Veja [operacao.md](operacao.md).

## 7. Servidor de retransmissão

- **Chave de acesso:** impede que estranhos usem o servidor. Ela está embutida em todo executável
  distribuído, então deve ser tratada como um obstáculo, não como um segredo. A proteção das
  sessões não depende dela.
- **Registro de ID:** exige assinatura com a chave da qual o ID foi derivado.
- **Limites contra abuso:** teto de conexões no total e por endereço, prazo para a primeira linha,
  tamanho máximo de linha e de fila de pedidos.
- **Isolamento no servidor:** o serviço roda com usuário descartável, sem escrita no sistema, com
  teto de memória e de CPU, para que um abuso não derrube o que mais houver na máquina.

## 8. O que o Ponte não protege

| Situação | Por quê |
|---|---|
| Outro programa rodando na mesma conta do Windows do controlado | Ele pode ler e alterar o `config.json`: acrescentar um computador à lista de autorizados, trocar o derivado da senha ou apagar tudo. O Ponte não se defende de quem já está dentro da conta. |
| Quem tem o ID e a chave do servidor pode fazer pedidos de conexão repetidos | Cada um vira uma janela de pedido no controlado. É incômodo, não invasão: sem aceite ninguém entra. |
| Senha fraca | O limite de cinco tentativas atrasa a adivinhação, mas não substitui uma senha boa. |
| Pessoa que aceita um pedido de um desconhecido | O aceite é uma decisão humana. O Ponte mostra quem pede, mas nome e usuário podem ser inventados. |
| Tela de login, tela de bloqueio e avisos do UAC | Ficam fora do alcance de um programa em modo usuário. Para o controlado isso é uma proteção; para quem acessa, uma limitação. |
| Disponibilidade | Quem controla o servidor ou a rede pode impedir conexões por ID e atualizações. |
| Celular autorizado que cai em outras mãos | Um aparelho autorizado entra sem senha. Se ele for perdido ou roubado desbloqueado, remova-o em "Computadores autorizados" de cada computador. O aplicativo não pede senha nem biometria para abrir. |
| Registro permanente dos acessos | Não há arquivo de histórico. O que existe é a linha de estado da janela principal e a data do último acesso de cada computador autorizado. |
| Miniaturas guardadas por quem acessou | São imagens da tela de outro computador gravadas sem cifra no perfil de quem acessou. |

## 9. Resumo das primitivas

| Uso | Algoritmo |
|---|---|
| Identidade de cada computador | ECDSA P-256, certificado autoassinado |
| Canal | TLS 1.2 ou 1.3 |
| ID | SHA-256 da chave pública, reduzido a 9 dígitos |
| Fixação de identidade | SHA-256 do certificado |
| Senha guardada | PBKDF2-HMAC-SHA256, 200.000 iterações |
| Prova da senha | HMAC-SHA256 sobre desafio e certificado |
| Prova do computador autorizado | ECDSA P-256 com SHA-256 sobre desafio e certificado |
| Assinatura da atualização | ECDSA P-256 com SHA-256 |
| Proteção em disco das chaves privadas | DPAPI, conta do Windows |
