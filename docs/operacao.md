# Operação: servidor, publicação e chaves

Este documento é para quem mantém o Ponte: publicar versões, cuidar do servidor de retransmissão e
guardar as chaves. Vale para a versão 1.17.4 do Windows, 1.17.1 do Android, 1.17.0 do navegador e
1.12.1 do iPhone.

## 1. As peças fora do código

| Peça | Onde fica | Para que serve |
|---|---|---|
| Servidor de retransmissão | VPS com Linux, serviço `ponte-relay` | Junta os dois aparelhos numa conexão por ID, entrega os arquivos de atualização e assina os bilhetes da conta. |
| Chave de acesso do servidor | `/etc/ponte-relay.env` no servidor e `RelayDefaults.cs` na máquina de desenvolvimento | Impede o uso do servidor por estranhos. As duas precisam ser iguais. |
| Chave que assina os bilhetes | `PONTE_TICKET_KEY` em `/etc/ponte-relay.env` | Assina o bilhete da conta (1.17.0). A pública está em `Ticket.IssuerKey`, dentro de cada programa. |
| Lista de contas liberadas | Supabase do FilaBox, tabela `public.ponte_liberados` | Quem pode usar o Ponte. Veja [conta.md](conta.md). |
| Chave de assinatura das atualizações | `chaves\atualizacao.chave` na máquina de desenvolvimento | Assina cada versão publicada. |
| Chave SSH do servidor | `%USERPROFILE%\.ssh\filabox_vps` | Permite aos scripts enviar os arquivos. |
| Chave de assinatura do APK | `chaves\android.jks`, com a senha em `android\keystore.properties` | Assina o aplicativo Android. Sem ela o aplicativo instalado não aceita atualização. |

Só a chave de assinatura do APK está no repositório (privado), por decisão do dono, para que outra
máquina de quem mantém o projeto consiga gerar atualizações do aplicativo. As demais ficam fora dele.

**Nunca mostre em tela, em registro ou numa captura:** a chave do servidor, a senha do keystore, a
chave de serviço do Supabase e a `PONTE_TICKET_KEY`. As capturas de `--captura-ui` das
Configurações mostram a chave do servidor; não as publique.

### Os quatro programas

| Programa | Versão em | Script de publicação |
|---|---|---|
| Windows | `<Version>` em `Ponte.csproj` | `publicar.ps1` |
| Android | `versionCode` e `versionName` em `android/app/build.gradle.kts` | `android\gerar-apk.ps1` e `android\publicar-apk.ps1` |
| Navegador | `VERSION` em `web/app.js` | `web\publicar-web.ps1` |
| iPhone | `ios/project.yml` e `web/ios.json` | `ios\publicar-ios.ps1` |

Há ainda o **repositório público** `dytech0021/ponte-app`, atualizado por `publicar-github.ps1`. O
repositório privado `dytech0021/ponte` **nunca** pode virar público: ele tem a chave do APK.

## 2. Publicar uma versão

### 2.1 Regra

Toda mudança no programa sai com um número de versão novo. O número aparece no título da janela e
é o que permite saber, olhando para um computador, se ele já recebeu a mudança.

- Correção ou ajuste pequeno: sobe o último número (1.2.1 → 1.2.2).
- Recurso novo: sobe o número do meio e zera o último (1.2.2 → 1.3.0).

Duas razões tornam isso obrigatório, e não só boa prática:

1. Uma cópia instalada só aceita versão **maior** que a dela. Publicar sem subir o número não chega a ninguém.
2. Dois executáveis diferentes com o mesmo número tornam impossível saber qual está em cada computador.

### 2.2 Passo a passo

1. Altere `<Version>` em `Ponte.csproj`.
2. Rode, na pasta do projeto:

   ```
   powershell -ExecutionPolicy Bypass -File .\publicar.ps1
   ```

3. Faça o commit e envie ao repositório (`main` e `main:recursos-1.5`).
4. Atualize o repositório público e crie a release:

   ```
   powershell -ExecutionPolicy Bypass -File .\publicar-github.ps1
   gh release create v1.17.4 "pronto\Ponte.exe" --repo dytech0021/ponte-app --title "Ponte 1.17.4" --notes-file nota.txt
   ```

O autoteste mexe o mouse por alguns segundos e toca um tom bem baixo; avise antes de rodá-lo numa
máquina em uso. Se ele falhar, **não publique**: olhe as linhas que falharam. Se houver um Ponte
aberto como administrador em primeiro plano, feche-o antes.

### 2.3 O que o `publicar.ps1` faz

| Etapa | Detalhe | Se falhar |
|---|---|---|
| Confere a chave de assinatura | O arquivo `chaves\atualizacao.chave` precisa existir. | Para antes de qualquer coisa. |
| Compara a versão | Lê a versão do projeto e a do `manifest.json` do servidor. | Para se a do projeto não for maior. |
| Gera o executável | `dotnet publish` de arquivo único, numa pasta temporária. | Para. |
| Autoteste | Roda `--autoteste` pelo servidor de retransmissão. Mexe o mouse por um instante e toca um tom muito baixo. | Mostra as linhas que não passaram e para. **Nada é publicado.** |
| Assina | `--assinar-atualizacao` gera o `manifest.json`. | Para. A chave só abre na conta do Windows que a criou. |
| Envia | Copia os dois arquivos com nome provisório e só então os põe no lugar. | Para; os arquivos antigos continuam valendo. |
| Confere | Compara o SHA-256 do arquivo no servidor com o gerado. | Para e avisa. |
| Atualiza as cópias locais | `pronto\Ponte.exe` e o `Ponte.exe` da Área de Trabalho. | Se a da Área de Trabalho estiver aberta, ela se atualiza sozinha. |

`-SemTeste` pula o autoteste. Use só quando ele já foi rodado à parte para o mesmo código.

O servidor não precisa ser reiniciado: ele lê os arquivos a cada pedido.

### 2.4 Depois de publicar

As cópias instaladas checam ao abrir e a cada 30 minutos, e trocam de versão quando não há sessão em
andamento. Para conferir numa cópia sem esperar, com ela fechada:

```
Ponte.exe --atualizar-agora C:\caminho\log.txt
```

O arquivo registra a versão em uso, o que o servidor oferece e o resultado. O código de saída é 0
quando atualizou ou já estava na última, e 2 quando a atualização foi recusada (a linha começa com
`RECUSADO:` e diz o motivo).

### 2.5 Não existe volta de versão

Uma cópia nunca instala uma versão menor que a dela. Se uma versão publicada tiver defeito, a
correção é publicar outra com número maior, mesmo que o conteúdo seja o da versão anterior.

### 2.6 Cópias que não se atualizam sozinhas

- A versão 1.0.0 não tem atualização automática.
- Cópias com a atualização desligada em Configurações.
- Cópias em pasta onde o usuário não pode gravar.
- Cópias cuja chave de servidor ou endereço de servidor não é o atual (seção 4.3).

Nesses casos, substitua o `Ponte.exe` à mão.

### 2.7 Aplicativo Android

O aplicativo tem número de versão próprio, em `android\app\build.gradle.kts`; desde a 1.5.0 ele
acompanha o do Windows. A cada mudança nele, suba `versionCode` e `versionName`, rode
`android\gerar-apk.ps1` (testa, compila, assina e deixa `pronto\Ponte.apk` e
`pronto\android-manifest.json`) e depois `android\publicar-apk.ps1`, que envia os dois ao servidor.
Os aparelhos encontram e baixam a versão nova sozinhos ao abrir o aplicativo, e a instalação pede a
confirmação do Android. O `relay.js` do servidor já entrega o APK desde 14/10/2026; o
`-AtualizarRelay` só é necessário de novo se o `relay.js` mudar. Os detalhes estão em
[android.md](android.md).

Os dois scripts procuram a chave SSH do servidor em `%USERPROFILE%\.ssh\filabox_vps` ou, se ela não
existir, em `filabox3d_deploy_ed25519`. Com essa segunda, a identidade do servidor é conferida pelo
`filabox3d_known_hosts`, que veio junto com ela: o `known_hosts` padrão daquela máquina guarda uma
identidade antiga do servidor.

Uma mudança no protocolo que o aplicativo precise aproveitar exige publicar antes a versão do
Windows que a entende, porque os computadores é que são acessados.

### 2.8 Navegador e iPhone

```
powershell -ExecutionPolicy Bypass -File .\web\publicar-web.ps1
powershell -ExecutionPolicy Bypass -File .\ios\publicar-ios.ps1
```

A versão da página vem de `VERSION` em `web/app.js` e precisa ser maior que a do servidor. Os
arquivos vão com `Cache-Control: no-cache`, então quem já abriu a página pega a versão nova ao abrir
de novo. A pasta é trocada inteira, e `web/ios.json` (a fonte do SideStore) vai junto: por isso os
dois scripts andam emparelhados. Detalhes em [navegador-e-iphone.md](navegador-e-iphone.md).

### 2.9 Repositório público

```
powershell -ExecutionPolicy Bypass -File .\publicar-github.ps1
```

Ele leva o código para `dytech0021/ponte-app`, conferindo que nenhum arquivo proibido (chaves,
`RelayDefaults.cs`, keystore) vai junto. A cada versão, crie também a release com o executável:

```
gh release create vX.Y.Z "pronto\Ponte.exe" --repo dytech0021/ponte-app --title "Ponte X.Y.Z" --notes-file nota.txt
```

**O repositório privado `dytech0021/ponte` nunca pode ficar público**, e não deve existir outro
repositório chamado `ponte`: a chave que assina o APK está dentro dele.

## 3. Servidor de retransmissão

### 3.1 O que está instalado

| Item | Caminho |
|---|---|
| Programa | `/opt/ponte-relay/relay.js` |
| Arquivos de atualização | `/opt/ponte-relay/atualizacao/manifest.json` e `Ponte.exe` |
| Chave de acesso | `/etc/ponte-relay.env` (permissão 600), com a linha `PONTE_KEY=...` |
| Serviço | `/etc/systemd/system/ponte-relay.service` |
| Porta | 7412 TCP, aberta para a internet |

O endereço do servidor e a chave SSH usados na publicação estão no início do `publicar.ps1`.

O serviço roda com usuário descartável (`DynamicUser`), sem permissão de escrita no sistema, com
teto de 160 MB de memória e 40 % de CPU. Por isso os arquivos de atualização precisam ter leitura
para todos (permissão 644), o que o `publicar.ps1` já garante.

O `ExecStart` aponta para uma instalação específica do Node.js. Se o Node for atualizado ou movido
no servidor, ajuste esse caminho na unidade do serviço.

### 3.2 Variáveis de ambiente do `relay.js`

| Variável | Padrão | Efeito |
|---|---|---|
| `PONTE_KEY` | vazio | Com valor, só atende quem enviar essa chave. |
| `PONTE_PORT` | 7412 | Porta, quando não vem como argumento. |
| `PONTE_BIND` | `0.0.0.0` | Endereço de escuta. |
| `PONTE_UPDATE_DIR` | `atualizacao`, ao lado do `relay.js` | Pasta dos arquivos de atualização. |
| `PONTE_WS_PORT` | 0 (desligado) | Porta do WebSocket do Ponte no navegador (atrás do nginx). |
| `PONTE_TICKET_KEY` | vazio | Chave EC P-256 (PKCS#8, DER em base64) que assina os bilhetes da conta. Sem ela o comando `TICKET` responde que o servidor não confere contas, e **nenhuma cópia 1.17.0 ou mais nova funciona**. |
| `PONTE_SUPABASE_URL`, `PONTE_SUPABASE_KEY` | o projeto do FilaBox e a chave pública dele | Como o servidor fala com o Supabase para conferir a conta. A chave é a mesma que o site entrega a qualquer navegador; quem manda é a função `ponte_conta()`, que só responde sobre quem chamou. |
| `PONTE_WS_BIND` | `127.0.0.1` | Onde o WebSocket escuta (o nginx fica na frente). |

A pasta de atualização guarda hoje: `manifest.json` e `Ponte.exe` (Windows),
`android-manifest.json` e `Ponte.apk` (Android) e `video-<sha256>.br` (componente de vídeo).

O MediaMTX, que servia as lives do Ponte até a 1.10.1, agora é do Exilados: a configuração e a autorização dele
ficam no repositório `dytech0021/exilados` (pasta `servidor/`).

### 3.3 Comandos do dia a dia

```
systemctl status ponte-relay          situação e últimas linhas do registro
journalctl -u ponte-relay -n 100      registro
systemctl restart ponte-relay         reinicia
systemctl disable --now ponte-relay   desliga e impede de subir com o servidor
```

Reiniciar derruba as sessões que passam pelo servidor. Os computadores registrados voltam sozinhos
em cerca de 5 segundos.

O registro do serviço só traz a linha de partida e erros de escuta. O servidor não grava quem
conectou a quem.

### 3.4 Atualizar o `relay.js`

1. Guarde o atual: `cp /opt/ponte-relay/relay.js /opt/ponte-relay/relay.js.anterior`.
2. Copie o novo para `/opt/ponte-relay/relay.js`.
3. `systemctl restart ponte-relay` e confira com `systemctl status ponte-relay`.

Para voltar, restaure o `.anterior` e reinicie. Mudanças no `relay.js` que alterem o protocolo
precisam continuar atendendo as versões do Ponte já instaladas.

### 3.5 Instalar em outro servidor

1. Instale o Node.js. Não há dependências a instalar.
2. Copie `relay/relay.js` para `/opt/ponte-relay/` e crie `/opt/ponte-relay/atualizacao/`.
3. Crie `/etc/ponte-relay.env` com `PONTE_KEY=<chave>` e permissão 600.
4. Copie `relay/ponte-relay.service` para `/etc/systemd/system/` e ajuste o caminho do Node em `ExecStart`.
5. `systemctl daemon-reload && systemctl enable --now ponte-relay`.
6. Libere a porta 7412 TCP.

Mudar de servidor muda o endereço que as cópias instaladas usam. Veja a seção 4.3.

## 4. Chaves

### 4.1 Chave de assinatura das atualizações

É a peça mais sensível. Quem a tem consegue produzir uma atualização que todas as cópias aceitam.

- **Criação:** `Ponte.exe --gerar-chave-atualizacao chaves\atualizacao.chave UpdateKey.cs`. O comando
  grava a chave privada, cifrada com DPAPI para a conta do Windows, e escreve a chave pública em
  `UpdateKey.cs`. Ele se recusa a sobrescrever uma chave existente (código de saída 3).
- **Uso:** só pelo `publicar.ps1`, na mesma máquina e na mesma conta do Windows que a criou.
- **Não há cópia de segurança utilizável.** Copiar o arquivo para outra máquina ou outra conta não
  adianta, porque ele só abre onde foi criado.

Situações que fazem perder a chave: formatar a máquina, trocar de máquina, apagar a conta do Windows.
Em conta local, se um administrador **redefinir** a senha da conta (em vez de o próprio usuário
trocá-la), o Windows pode perder o acesso aos dados protegidos dessa forma.

**Se a chave se perder:**

1. Mova o arquivo antigo para fora de `chaves\` e gere uma chave nova com o comando acima.
2. Publique normalmente. A partir daí o executável traz outra chave pública.
3. Substitua o `Ponte.exe` à mão em **cada** computador. As cópias antigas recusam a versão nova,
   porque a assinatura não confere com a chave que elas conhecem.

**Se houver suspeita de vazamento:** faça o mesmo, o quanto antes, e troque também o acesso ao
servidor. Enquanto as cópias antigas existirem, elas continuam aceitando o que for assinado com a
chave antiga.

### 4.2 Chave que assina os bilhetes da conta (1.17.0)

Fica só no servidor, em `/etc/ponte-relay.env`, na linha `PONTE_TICKET_KEY=` (PKCS#8, DER em
base64, EC P-256). A parte pública está em `Ticket.IssuerKey`, dentro de cada programa.

| Situação | Consequência |
|---|---|
| A chave se perde | Nenhum bilhete novo é emitido. Quem tem bilhete continua até vencer (no máximo 14 dias); depois disso **nada funciona**. A volta é gerar outra chave, pôr a pública em `Ticket.IssuerKey` e publicar os quatro programas. |
| A chave vaza | Quem a tiver emite bilhete para qualquer aparelho, o que só passa pelo requisito de conta: ele ainda precisa de aceite, senha ou autorização em cada computador. Mesmo assim, troque. |

Como trocar exige publicar versão nova dos quatro programas e esperar todo mundo atualizar, planeje
uma janela: deixe o servidor aceitando **as duas** chaves durante a troca, se isso for necessário.

### 4.3 Lista de contas liberadas

Fica no Supabase do FilaBox (`supabase/ponte-liberados.sql`). A tabela tem RLS sem políticas e as
permissões revogadas: só as funções `security definer` a tocam, e só o administrador vê, libera e
remove. Ver [conta.md](conta.md).

### 4.4 Identidade de cada computador

Fica no `config.json` de cada usuário, cifrada da mesma forma. Não há o que administrar. Se a
configuração se perder, o computador ganha um ID novo, os outros precisam do endereço novo, e as
autorizações de entrada direta têm de ser refeitas.

### 4.5 Chave e endereço do servidor de retransmissão

O valor embutido no executável só é usado na **primeira** abertura. A partir daí vale o que está
gravado na configuração de cada computador. Duas consequências:

- Trocar a chave ou o endereço no código e publicar uma versão nova **não** muda os computadores
  que já têm o Ponte configurado.
- Com a chave antiga eles nem conseguem falar com o servidor para buscar a atualização.

Então, para trocar a chave ou o endereço:

1. Altere `/etc/ponte-relay.env` no servidor (ou suba o servidor novo) e reinicie o serviço.
2. Altere `RelayDefaults.cs` e publique, para que instalações novas já saiam certas.
3. Em **cada** computador já configurado, abra Configurações, corrija "Servidor de retransmissão" e
   "Chave do servidor", salve e reabra o Ponte.

Como a chave está embutida em todo executável distribuído, trate-a como um obstáculo contra uso
indevido do servidor, e não como um segredo forte.

## 5. Verificação e diagnóstico

| Comando | Para quê |
|---|---|
| `Ponte.exe --autoteste pasta` | Confere tudo por conexão direta, nesta máquina. |
| `Ponte.exe --autoteste pasta --relay host:porta` | O mesmo, passando pelo servidor e conectando por ID. |
| `Ponte.exe --autoteste pasta --so-video` | Só a parte de vídeo. |
| `Ponte.exe --autoteste pasta --so-conta` | Só a conta, contra um Supabase falso nesta máquina. |
| `Ponte.exe --autoteste-ciclo pasta` | O ciclo de vida do programa. |
| `Ponte.exe --autoteste-servico pasta` | O serviço do Windows. |
| `node --test relay/update.test.js` | O servidor de retransmissão, local e sem as chaves de produção. |
| `Ponte.exe --captura-ui pasta` | Grava imagens das janelas, para conferir a interface. |
| `Ponte.exe --demo pasta` | Abre uma sessão com a própria máquina e grava a imagem da janela. |
| `Ponte.exe --janela-teste 30 --elevar` | Abre uma janela de administrador em primeiro plano, para testar o bloqueio de mouse e teclado. |
| `Ponte.exe --perfil pasta` | Abre o programa com outra configuração e outro ID, sem tocar na do usuário. |

O `--autoteste` e o `--demo` capturam a tela de verdade. As imagens que eles gravam mostram o que
estava aberto no momento; não as publique sem olhar.

O autoteste usa perfis descartáveis dentro da pasta indicada. Esses perfis contêm a chave do
servidor; apague a pasta depois.

## 6. Repositório

O que fica fora do git, e por quê:

| Caminho | Motivo |
|---|---|
| `chaves/` | Chave de assinatura das atualizações. |
| `RelayDefaults.cs` | Chave de acesso do servidor. Há um modelo em `RelayDefaults.cs.exemplo`. |
| `bin/`, `obj/`, `pronto/` | Saídas de compilação e o executável pronto. |
| `capturas/` | Imagens dos modos de verificação; a da sessão mostra a área de trabalho de quem rodou. |
| `%APPDATA%\Ponte\miniaturas` (fora da pasta do projeto) | Miniaturas dos computadores acessados. Não fazem parte do repositório nem de cópia nenhuma. |
| `android/local.properties`, `android/relay.properties` | Caminho do SDK e chave do servidor. |
| `video-runtime/` | O FFmpeg usado para montar o pacote de vídeo. |
| `%LOCALAPPDATA%\Ponte\video` (fora da pasta do projeto) | O componente de vídeo baixado por cada computador. |

`chaves/android.jks` e `android/keystore.properties` continuam listados no `.gitignore`, mas foram
acrescentados ao repositório à força (`git add -f`), por decisão do dono. Uma troca dessa chave tem
de ser acrescentada da mesma forma.

### Montar o ambiente em outra máquina

1. Instale o SDK do .NET 10.
2. Clone o repositório.
3. Copie `RelayDefaults.cs.exemplo` para `RelayDefaults.cs` e preencha o servidor e a chave.
4. `dotnet build -c Release`.

Isso basta para compilar e testar. **Publicar** continua possível só na máquina e na conta que têm
a chave de assinatura (seção 4.1).

## 7. Pendências conhecidas

| Item | Situação |
|---|---|
| Janela por tela e "simular a disposição" (1.16.0 a 1.17.3) | Compilado e publicado, **sem teste com dois monitores**: a máquina que publica tem um monitor só. |
| Teclado automático e barra arrastável do Android (1.17.1) | Publicados sem o teste do aplicativo, a pedido do dono. |
| Ctrl+Alt+Del e serviço do Windows | O serviço existe desde a 1.15.0 e o Ctrl+Alt+Del desde a 1.17.0. Atender antes do login e no desktop protegido ainda não foi validado em uso real; veja `docs/SERVICO-E-CONEXAO-1.15.0.md`. |
| Autoteste: "registro de sessões... o envio da imagem falhou" | Falha vista na 1.17.1 e não investigada; as versões seguintes foram publicadas com `-SemTeste`. |
| Troca da chave dos bilhetes | Trocar a `PONTE_TICKET_KEY` exige publicar os quatro programas e esperar todo mundo atualizar. Não há hoje um caminho ensaiado para isso (seção 4.2). |
| Chave do APK trocada | A 1.5.0 do Android foi assinada com uma chave nova: nos aparelhos com a 1.0.0, é preciso desinstalar e instalar uma vez. |
| Som em computador com saída diferente de 48 kHz estéreo | O caminho em que o Windows recusa a conversão e o Ponte converte sozinho não foi exercitado. |
| Controle de programas de administrador | O recurso existe, mas não foi validado contra um programa elevado real numa conta de administrador. |
| Cópia de segurança da chave de assinatura das atualizações | Não existe forma de levá-la para outra máquina (seção 4.1). |
| Envio de arquivo para o celular e para o navegador | Nenhum dos dois grava arquivos; por isso o botão de enviar some quando quem acessa é um deles. |
| Acesso a um celular (1.14.0) | Testado num Android 15 emulado, pelo servidor de verdade. Falta conferir num celular de verdade (bateria, fabricantes que fecham aplicativos). |
| Lives e amigos | Saíram do Ponte na 1.11.0 e agora ficam no Exilados (veja `docs/SEM-LIVES-1.11.0.md`). |
| Apagar as telas do computador acessado | Pedido pelo dono, ainda não decidido nem implementado. |
