# Publica uma versão nova do Ponte: gera o executável, testa, assina e envia ao servidor de atualização.
# As cópias instaladas baixam sozinhas (ao abrir e depois a cada meia hora) e trocam quando não há sessão.
#
# Uso:  .\publicar.ps1             a versão vem de <Version> no Ponte.csproj e precisa ser maior que a do servidor
#       .\publicar.ps1 -SemTeste   pula o autoteste (ele mexe o mouse por um instante)
param([switch]$SemTeste)

$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
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
$pastaRemota = '/opt/ponte-relay/atualizacao'
$relay = 'filabox3d.com.br:7412'
$chaveAssinatura = Join-Path $raiz 'chaves\atualizacao.chave'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ponte-publicar-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$saida = Join-Path $temp 'saida'
$novo = Join-Path $saida 'Ponte.exe'

function Falhar($mensagem) {
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force -Confirm:$false }
    exit 1
}

function Remoto($comando) {
    & ssh @acessoSsh -o ConnectTimeout=15 $servidor $comando
    if ($LASTEXITCODE -ne 0) { Falhar "o servidor não respondeu ao comando: $comando" }
}

if (-not (Test-Path $chaveAssinatura)) { Falhar "falta a chave que assina as atualizações ($chaveAssinatura). Sem ela nenhuma cópia instalada aceita a versão nova." }

[xml]$projeto = Get-Content (Join-Path $raiz 'Ponte.csproj') -Encoding UTF8
$versao = [Version]$projeto.Project.PropertyGroup.Version

# Uma cópia instalada nunca volta de versão, então publicar sem subir o número não chegaria a ninguém.
$atual = Remoto "cat $pastaRemota/manifest.json 2>/dev/null || true"
$versaoServidor = if ($atual) { [Version]($atual | ConvertFrom-Json).Version } else { [Version]'0.0.0' }
if ($versao -le $versaoServidor) { Falhar "o servidor já oferece a $versaoServidor. Suba <Version> no Ponte.csproj (está em $versao) antes de publicar." }
Write-Host "Publicando a $versao (o servidor oferece a $versaoServidor)."

& dotnet publish (Join-Path $raiz 'Ponte.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o $saida -nologo -v q
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $novo)) { Falhar 'a compilação falhou.' }
if ([Version](Get-Item $novo).VersionInfo.FileVersion -ne [Version]"$versao.0") { Falhar 'o executável gerado não saiu com a versão do projeto.' }

# Componente de vídeo (FFmpeg do H.264/H.265): fica no servidor uma vez por versão dele, e os computadores o
# baixam na primeira vez que alguém escolhe H.264 ou H.265. O resumo esperado está em VideoRuntime.cs.
$fonteVideo = Get-Content (Join-Path $raiz 'VideoRuntime.cs') -Raw -Encoding UTF8
if ($fonteVideo -notmatch 'Sha256 = "([0-9a-f]{64})"') { Falhar 'não achei o resumo do componente de vídeo em VideoRuntime.cs.' }
$shaVideo = $Matches[1]
$pacoteVideo = "video-$shaVideo.br"
$temVideo = Remoto "test -s $pastaRemota/$pacoteVideo && echo sim || echo nao"
if ("$temVideo".Trim() -ne 'sim') {
    $ffmpeg = Get-ChildItem (Join-Path $raiz 'video-runtime') -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue |
        Where-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() -eq $shaVideo } | Select-Object -First 1
    if (-not $ffmpeg) { Falhar "o servidor não tem o componente de vídeo e não achei aqui (pasta video-runtime) o ffmpeg.exe com o resumo $shaVideo." }
    Write-Host 'Comprimindo o componente de vídeo para o servidor (uns 5 minutos, só quando ele muda)...'
    $localVideo = Join-Path $temp $pacoteVideo
    $empacotar = Start-Process -FilePath $novo -ArgumentList '--empacotar-video', "`"$($ffmpeg.FullName)`"", "`"$localVideo`"" -PassThru -Wait
    if ($empacotar.ExitCode -ne 0 -or -not (Test-Path $localVideo)) { Falhar 'a compressão do componente de vídeo falhou.' }
    & scp @acessoSsh -q $localVideo "${servidor}:$pastaRemota/$pacoteVideo.novo"
    if ($LASTEXITCODE -ne 0) { Falhar 'o envio do componente de vídeo falhou.' }
    Remoto "cd $pastaRemota && chmod 644 $pacoteVideo.novo && mv -f $pacoteVideo.novo $pacoteVideo" | Out-Null
    Write-Host "Componente de vídeo enviado ($([math]::Round((Get-Item $localVideo).Length / 1MB)) MB)."
}

if (-not $SemTeste) {
    Write-Host 'Autoteste pelo servidor de retransmissão...'
    $pastaTeste = Join-Path $temp 'autoteste'
    $teste = Start-Process -FilePath $novo -ArgumentList '--autoteste', "`"$pastaTeste`"", '--relay', $relay -PassThru -Wait
    $arquivoRegistro = Join-Path $pastaTeste 'autoteste.txt'
    if (-not (Test-Path $arquivoRegistro)) {
        $erro = Join-Path $pastaTeste 'erro.txt'
        if (Test-Path $erro) { Get-Content $erro -Encoding UTF8 | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" } }
        Falhar "o autoteste não chegou a terminar (código de saída $($teste.ExitCode)); nada foi publicado."
    }
    $registro = Get-Content $arquivoRegistro -Encoding UTF8
    $registro | Where-Object { $_ -notmatch '^\[OK\]' } | ForEach-Object { Write-Host "  $_" }
    if ($teste.ExitCode -ne 0) { Falhar 'o autoteste falhou; nada foi publicado.' }
}

$manifesto = Join-Path $saida 'manifest.json'
$assinatura = Start-Process -FilePath $novo -ArgumentList '--assinar-atualizacao', "`"$novo`"", "`"$chaveAssinatura`"", "`"$manifesto`"" -PassThru -Wait
if ($assinatura.ExitCode -ne 0) { Falhar "a assinatura falhou (código $($assinatura.ExitCode)). A chave só abre nesta conta do Windows e precisa ser o par da que está em UpdateKey.cs." }

# Os arquivos sobem com outro nome e só então assumem o lugar, para ninguém baixar um arquivo pela metade.
Push-Location $saida
try {
    & scp @acessoSsh -q Ponte.exe "${servidor}:$pastaRemota/Ponte.exe.novo"
    if ($LASTEXITCODE -ne 0) { Falhar 'o envio do executável falhou.' }
    & scp @acessoSsh -q manifest.json "${servidor}:$pastaRemota/manifest.json.novo"
    if ($LASTEXITCODE -ne 0) { Falhar 'o envio do manifesto falhou.' }
}
finally { Pop-Location }
$hashRemoto = Remoto "cd $pastaRemota && chmod 644 Ponte.exe.novo manifest.json.novo && mv -f Ponte.exe.novo Ponte.exe && mv -f manifest.json.novo manifest.json && sha256sum Ponte.exe | cut -d' ' -f1"
$hashLocal = (Get-FileHash $novo -Algorithm SHA256).Hash.ToLower()
if ("$hashRemoto".Trim() -ne $hashLocal) { Falhar 'o arquivo que ficou no servidor não confere com o gerado aqui.' }

New-Item -ItemType Directory -Force (Join-Path $raiz 'pronto') | Out-Null
# Este PC usa uma cópia só, a de pronto; a Área de Trabalho tem um atalho para ela. Se ela estiver
# aberta, não pode ser sobrescrita, e ela mesma se atualiza pelo servidor.
$destino = Join-Path $raiz 'pronto\Ponte.exe'
try { Copy-Item $novo $destino -Force }
catch { Write-Host "Em uso, vai se atualizar sozinha: $destino" }
Remove-Item $temp -Recurse -Force -Confirm:$false

Write-Host "Versão $versao publicada (sha256 $($hashLocal.Substring(0, 16))...)." -ForegroundColor Green
