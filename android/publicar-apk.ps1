# Publica o aplicativo Android gerado por gerar-apk.ps1: envia pronto\Ponte.apk e
# pronto\android-manifest.json ao servidor de retransmissão. Os aplicativos instalados encontram a
# versão nova ao abrir (no máximo a cada 6 horas) ou pelo menu "Verificar atualização".
#
# Uso:  .\publicar-apk.ps1                  a versão do APK precisa ser maior que a do servidor
#       .\publicar-apk.ps1 -AtualizarRelay  antes, troca o relay.js do servidor pelo deste repositório
#                                           (necessário uma vez: o relay anterior não entrega o APK)
param([switch]$AtualizarRelay)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
$servidor = 'root@177.153.35.193'
# A chave SSH do servidor tem nomes diferentes nas máquinas de quem publica; vale a primeira que existir.
$chaveSsh = @('filabox_vps', 'filabox3d_deploy_ed25519') | ForEach-Object { Join-Path $env:USERPROFILE ".ssh\$_" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chaveSsh) { $chaveSsh = Join-Path $env:USERPROFILE '.ssh\filabox_vps' }
# A chave de deploy vem com o próprio registro da identidade do servidor, mais novo que o known_hosts padrão.
$acessoSsh = @('-i', $chaveSsh, '-o', 'BatchMode=yes')
$hostsDaChave = Join-Path $env:USERPROFILE '.ssh\filabox3d_known_hosts'
if ($chaveSsh -like '*filabox3d_deploy_ed25519' -and (Test-Path $hostsDaChave)) {
    $acessoSsh += @('-o', "UserKnownHostsFile=$hostsDaChave", '-o', 'StrictHostKeyChecking=yes')
}
$pastaRelay = '/opt/ponte-relay'
$pastaRemota = "$pastaRelay/atualizacao"
$pronto = Join-Path $raiz 'pronto'
$apk = Join-Path $pronto 'Ponte.apk'
$manifesto = Join-Path $pronto 'android-manifest.json'

function Falhar($mensagem) {
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    exit 1
}

function Remoto($comando) {
    & ssh @acessoSsh -o ConnectTimeout=15 $servidor $comando
    if ($LASTEXITCODE -ne 0) { Falhar "o servidor não respondeu ao comando: $comando" }
}

if (-not (Test-Path $chaveSsh)) { Falhar "falta a chave SSH do servidor ($chaveSsh)." }
if (-not (Test-Path $apk) -or -not (Test-Path $manifesto)) { Falhar 'falta pronto\Ponte.apk ou pronto\android-manifest.json. Rode antes .\gerar-apk.ps1.' }

# O manifesto tem de descrever exatamente este APK: um par trocado faria todo aparelho recusar o download.
$dados = Get-Content $manifesto -Raw | ConvertFrom-Json
$tamanho = (Get-Item $apk).Length
$resumo = (Get-FileHash $apk -Algorithm SHA256).Hash.ToLowerInvariant()
if ($dados.size -ne $tamanho -or $dados.sha256 -ne $resumo) { Falhar 'o manifesto não confere com pronto\Ponte.apk. Gere os dois de novo com .\gerar-apk.ps1.' }

if ($AtualizarRelay) {
    Write-Host 'Trocando o relay.js do servidor (o anterior fica em relay.js.anterior)...'
    & scp @acessoSsh -q (Join-Path $raiz 'relay\relay.js') "${servidor}:$pastaRelay/relay.js.novo"
    if ($LASTEXITCODE -ne 0) { Falhar 'o envio do relay.js falhou.' }
    Remoto "cd $pastaRelay && cp -f relay.js relay.js.anterior && chmod 644 relay.js.novo && mv -f relay.js.novo relay.js && systemctl restart ponte-relay && sleep 2 && systemctl is-active ponte-relay"
}
$suporta = Remoto "grep -c ANDROID_MANIFEST $pastaRelay/relay.js || true"
if ("$suporta".Trim() -eq '0') { Falhar 'o relay.js do servidor ainda não entrega o APK. Rode de novo com -AtualizarRelay.' }

# Um aparelho nunca instala uma versão menor que a dele, então publicar sem subir versionCode não chegaria a ninguém.
$atual = Remoto "cat $pastaRemota/android-manifest.json 2>/dev/null || true"
$codigoServidor = if ($atual) { [int]($atual | ConvertFrom-Json).versionCode } else { 0 }
if ([int]$dados.versionCode -le $codigoServidor) { Falhar "o servidor já oferece o versionCode $codigoServidor. Suba versionCode em app\build.gradle.kts (está em $($dados.versionCode))." }
Write-Host "Publicando o Ponte para Android $($dados.versionName) (versionCode $($dados.versionCode); o servidor oferece $codigoServidor)."

# Os arquivos sobem com outro nome e só então assumem o lugar, para ninguém baixar um arquivo pela metade.
Push-Location $pronto
try {
    & scp @acessoSsh -q Ponte.apk "${servidor}:$pastaRemota/Ponte.apk.novo"
    if ($LASTEXITCODE -ne 0) { Falhar 'o envio do APK falhou.' }
    & scp @acessoSsh -q android-manifest.json "${servidor}:$pastaRemota/android-manifest.json.novo"
    if ($LASTEXITCODE -ne 0) { Falhar 'o envio do manifesto falhou.' }
}
finally { Pop-Location }
$hashRemoto = Remoto "cd $pastaRemota && chmod 644 Ponte.apk.novo android-manifest.json.novo && mv -f Ponte.apk.novo Ponte.apk && mv -f android-manifest.json.novo android-manifest.json && sha256sum Ponte.apk | cut -d' ' -f1"
if ("$hashRemoto".Trim() -ne $resumo) { Falhar 'o APK que ficou no servidor não confere com o gerado aqui.' }

Write-Host "Ponte para Android $($dados.versionName) publicado (sha256 $($resumo.Substring(0, 16))...)." -ForegroundColor Green
