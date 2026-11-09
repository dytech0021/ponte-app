# Ponte

Acesso remoto entre computadores Windows: ver a tela, controlar mouse e teclado, ouvir o som, trocar
arquivos e compartilhar a área de transferência. Código próprio em C# / .NET 10 (WinForms), sem pacotes externos.
Um único `Ponte.exe` faz os dois papéis, o de quem acessa e o de quem é acessado.

O Ponte tem quatro programas, todos falando o mesmo protocolo:

| Programa | Onde | Papéis |
|---|---|---|
| `Ponte.exe` | Windows de 64 bits | Acessa e é acessado. |
| Aplicativo Android (`android/`) | Android 8.0 ou mais novo | Acessa e, desde a 1.14.0, também é acessado. |
| Ponte no navegador (`web/`) | Qualquer aparelho com navegador | Acessa. Feito para o iPhone. |
| Ponte para iPhone (`ios/`) | iPhone, instalado pelo SideStore | Acessa. |

Versões atuais: **Windows 1.17.4**, **Android 1.17.1**, **navegador 1.17.0**, **iPhone 1.12.1**.

Para rodar o programa do Windows, o computador precisa de Windows de 64 bits e do .NET Desktop Runtime 10.

**Desde a 1.17.0 é preciso entrar com uma conta liberada pelo dono do Ponte**, dos dois lados da
sessão. Veja [a conta](docs/conta.md).

## Documentação

| Documento | Conteúdo |
|---|---|
| [Manual de uso](docs/manual.md) | Como acessar e ser acessado, conta, computadores salvos, entrada sem aprovação, janela da sessão, telas em janelas, vídeo, problemas comuns. |
| [A conta](docs/conta.md) | A conta obrigatória, o bilhete assinado, a lista de liberados e como liberar alguém. |
| [Arquitetura e código](docs/arquitetura.md) | Organização dos arquivos, partida do programa, captura, vídeo, serviço do Windows, configuração, atualização, autoteste. |
| [Protocolo de rede](docs/protocolo.md) | Servidor de retransmissão, TLS, canal do navegador, formato das mensagens, autenticação, convivência entre versões. |
| [Segurança](docs/seguranca.md) | O que é protegido e como, e o que o Ponte não protege. |
| [Aplicativo Android](docs/android.md) | Instalar, conectar, gestos e teclado; acesso ao próprio celular; e, para quem mantém, estrutura, compilação e testes. |
| [Navegador e iPhone](docs/navegador-e-iphone.md) | O Ponte que roda no navegador e o aplicativo do iPhone. |
| [Operação](docs/operacao.md) | Publicar versões, cuidar do servidor, guardar as chaves, pendências conhecidas. |
| [Histórico de versões](docs/versoes.md) | O que entrou em cada versão, da 1.0.0 à 1.17.4. |

Documentos de versões específicas, mantidos como estão: [serviço e conexão (1.15.0)](docs/SERVICO-E-CONEXAO-1.15.0.md),
[bandeja (1.14.1)](docs/BANDEJA-1.14.1.md), [sem lives (1.11.0)](docs/SEM-LIVES-1.11.0.md),
[lives 1.10.1](docs/LIVES-1.10.1.md), [1.10.0](docs/LIVES-1.10.0.md) e [1.9.1](docs/LIVES-1.9.1.md).

## Como os computadores se encontram

- **Por ID** (9 dígitos): os dois lados abrem conexões de saída para o servidor de retransmissão
  (`relay/relay.js`), que só repassa bytes. Funciona atrás de roteador, sem abrir porta.
- **Direto por IP**, na mesma rede, pela porta 7411.

Nos dois casos a cifra é de ponta a ponta: o servidor não enxerga a sessão. Entre dois programas é
TLS; com o navegador, um canal próprio equivalente (`WPONTE1`), porque o navegador não faz TLS com o
certificado de cada computador.

## Quem pode entrar

Cada computador tem uma chave própria, e o ID é derivado dela. Uma conexão só entra se **os dois
lados tiverem conta liberada** (1.17.0) e, além disso, por um destes caminhos:

1. **Aceite na tela** de quem está no computador acessado.
2. **Senha de acesso**, se o dono definiu uma.
3. **Computador autorizado**: o computador acessado guarda a chave de quem ele liberou e deixa entrar
   direto. Entra-se nessa lista marcando "Sempre permitir este computador" no pedido de conexão, ou
   entrando uma vez com a senha a partir de um computador salvo. A lista fica em
   "Computadores autorizados..." e pode ser desfeita ali.

Durante qualquer sessão o computador acessado mostra um aviso com o botão de encerrar, que também
diz quando o som dele está sendo transmitido. Não há modo oculto. O início minimizado com o Windows
pode ser desligado nas configurações.

## Limites conhecidos

- Sem o serviço opcional, o computador acessado precisa estar com o Ponte aberto e a sessão do
  Windows desbloqueada.
- O serviço do Windows (1.15.0) atende antes do login e no desktop protegido, e é o que permite o
  Ctrl+Alt+Del remoto (1.17.0). Esses contextos precisam de validação operacional separada; veja o
  documento da versão.
- Para controlar programas abertos como administrador, o Ponte de lá também precisa estar como administrador.

## Compilar

Precisa do SDK do .NET 10. Antes da primeira compilação, copie `RelayDefaults.cs.exemplo` para
`RelayDefaults.cs` e preencha o servidor e a chave.

```
dotnet build -c Release
```

## Publicar uma versão

Toda mudança sai com um número de versão novo, que aparece no título da janela.

1. Suba `<Version>` em `Ponte.csproj`.
2. Rode `.\publicar.ps1`.

O script gera o executável de arquivo único, roda o autoteste pelo servidor, assina e envia. As cópias
instaladas conferem a assinatura, baixam sozinhas (ao abrir e a cada meia hora) e trocam de versão
quando não há sessão em andamento. O script se recusa a publicar se o número não for maior que o do servidor.

Ficam fora do repositório, e são necessários para publicar:

- `chaves/atualizacao.chave`: chave privada que assina as atualizações, protegida pela conta do Windows
  de quem publica. **Se ela se perder, as cópias instaladas não aceitam mais nenhuma atualização.**
- `RelayDefaults.cs`: endereço e chave de acesso do servidor de retransmissão.
- A chave SSH do servidor.

## Servidor de retransmissão

`relay/relay.js` roda só com Node.js, sem dependências. `relay/ponte-relay.service` é a unidade do
systemd, com usuário descartável e teto de memória e CPU. O servidor também entrega os arquivos de
atualização do Windows e do Android, o pacote de vídeo e, desde a 1.17.0, assina o bilhete da conta;
ele não tem a chave que assina as atualizações.

## Modos de verificação

```
Ponte.exe --autoteste pasta [--relay host:porta]   testa os dois lados nesta máquina e grava o resultado
Ponte.exe --captura-ui pasta [--tema escuro]       grava a imagem das janelas
Ponte.exe --demo pasta                             abre uma sessão com a própria máquina e grava a imagem
```

O autoteste move o mouse de verdade por um instante, captura a tela e toca um tom muito baixo.
