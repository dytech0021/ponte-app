# Ponte

Acesso remoto entre computadores Windows: ver a tela, controlar mouse e teclado, ouvir o som, trocar
arquivos e compartilhar a área de transferência. Código próprio em C# / .NET 10 (WinForms), sem pacotes externos.
Um único `Ponte.exe` faz os dois papéis, o de quem acessa e o de quem é acessado. Há também um
aplicativo Android (`android/`), que acessa os computadores a partir do celular.

Para rodar, o computador precisa de Windows de 64 bits e do .NET Desktop Runtime 10.

## Documentação

| Documento | Conteúdo |
|---|---|
| [Manual de uso](docs/manual.md) | Como acessar e ser acessado, computadores salvos, entrada sem aprovação, janela da sessão, problemas comuns. |
| [Arquitetura e código](docs/arquitetura.md) | Organização dos arquivos, partida do programa, captura de tela, entrada, configuração, atualização, autoteste. |
| [Protocolo de rede](docs/protocolo.md) | Servidor de retransmissão, TLS, formato das mensagens, autenticação, convivência entre versões. |
| [Segurança](docs/seguranca.md) | O que é protegido e como, e o que o Ponte não protege. |
| [Aplicativo Android](docs/android.md) | Instalar, conectar, gestos e teclado; e, para quem mantém, estrutura, compilação e testes. |
| [Operação](docs/operacao.md) | Publicar versões, cuidar do servidor, guardar as chaves, pendências conhecidas. |

## Como os computadores se encontram

- **Por ID** (9 dígitos): os dois lados abrem conexões de saída para o servidor de retransmissão
  (`relay/relay.js`), que só repassa bytes. Funciona atrás de roteador, sem abrir porta.
- **Direto por IP**, na mesma rede, pela porta 7411.

Nos dois casos o TLS é de ponta a ponta: o servidor não enxerga a sessão.

## Quem pode entrar

Cada computador tem uma chave própria, e o ID é derivado dela. Uma conexão só entra por um destes caminhos:

1. **Aceite na tela** de quem está no computador acessado.
2. **Senha de acesso**, se o dono definiu uma.
3. **Computador autorizado**: o computador acessado guarda a chave de quem ele liberou e deixa entrar
   direto. Entra-se nessa lista marcando "Sempre permitir este computador" no pedido de conexão, ou
   entrando uma vez com a senha a partir de um computador salvo. A lista fica em
   "Computadores autorizados..." e pode ser desfeita ali.

Durante qualquer sessão o computador acessado mostra um aviso com o botão de encerrar, que também
diz quando o som dele está sendo transmitido. Não há modo oculto. O início minimizado com o Windows pode ser desligado nas configurações.

## Limites conhecidos

- O computador acessado precisa estar com o Ponte aberto e a sessão do Windows desbloqueada.
- Tela de login, aviso do UAC e Ctrl+Alt+Del ficam de fora (exigiriam um serviço do Windows).
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
systemd, com usuário descartável e teto de memória e CPU. O servidor também entrega os dois arquivos
da atualização (`manifest.json` e `Ponte.exe`); ele não tem a chave que assina.

## Modos de verificação

```
Ponte.exe --autoteste pasta [--relay host:porta]   testa os dois lados nesta máquina e grava o resultado
Ponte.exe --captura-ui pasta [--tema escuro]       grava a imagem das janelas
Ponte.exe --demo pasta                             abre uma sessão com a própria máquina e grava a imagem
```

O autoteste move o mouse de verdade por um instante, captura a tela e toca um tom muito baixo.
