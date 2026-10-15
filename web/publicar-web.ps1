# Publica o Ponte no navegador (a versão para iPhone): testa o núcleo, envia a pasta web ao servidor e confere
# o que ficou no ar. A página fica em https://filabox3d.com.br/ponte/ (o nginx serve /var/www/ponte-web) e fala
# com o servidor de retransmissão pelo WebSocket em /ponte/ws. Quem já abriu a página pega a versão nova ao
# abrir de novo (os arquivos vão com "Cache-Control: no-cache").
#
# Uso:  .\publicar-web.ps1     a versão vem de VERSION em app.js e precisa ser maior que a do servidor
$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$servidor = 'root@177.153.35.193'
$chaveSsh = @('filabox_vps', 'filabox3d_deploy_ed25519') | ForEach-Object { Join-Path $env:USERPROFILE ".ssh\$_" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chaveSsh) { $chaveSsh = Join-Path $env:USERPROFILE '.ssh\filabox_vps' }
$acessoSsh = @('-i', $chaveSsh, '-o', 'BatchMode=yes')
$hostsDaChave = Join-Path $env:USERPROFILE '.ssh\filabox3d_known_hosts'
if ($chaveSsh -like '*filabox3d_deploy_ed25519' -and (Test-Path $hostsDaChave)) {
    $acessoSsh += @('-o', "UserKnownHostsFile=$hostsDaChave", '-o', 'StrictHostKeyChecking=yes')
}
$pastaRemota = '/var/www/ponte-web'
$endereco = 'https://filabox3d.com.br/ponte/'
$arquivos = 'index.html', 'app.js', 'app.css', 'ponte.js', 'manifest.webmanifest', 'icone-180.png', 'icone-192.png', 'icone-512.png',
    'assistir/index.html', 'assistir/assistir.js', 'assistir/assistir.css', 'assetlinks.json',
    'baixar/index.html', 'baixar/baixar.js'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ponte-web-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

function Falhar($mensagem) {
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force -Confirm:$false }
    exit 1
}

function Remoto($comando) {
    $saida = & ssh @acessoSsh -o ConnectTimeout=15 $servidor $comando
    if ($LASTEXITCODE -ne 0) { Falhar "o servidor não respondeu ao comando: $comando" }
    return $saida
}

$app = Get-Content (Join-Path $raiz 'app.js') -Raw -Encoding UTF8
if ($app -notmatch "const VERSION = '(\d+\.\d+\.\d+)'") { Falhar 'não achei VERSION em app.js.' }
$versao = [Version]$Matches[1]
$atual = "$(Remoto "cat $pastaRemota/versao.txt 2>/dev/null || true")".Trim()
$versaoServidor = if ($atual) { [Version]$atual } else { [Version]'0.0.0' }
if ($versao -le $versaoServidor) { Falhar "o servidor já tem a $versaoServidor. Suba VERSION em app.js (está em $versao) antes de publicar." }
Write-Host "Publicando o Ponte no navegador $versao (o servidor tem a $versaoServidor)."

# Os testes do núcleo que não dependem de nada no ar (o teste contra um Ponte de verdade é o testar-com-o-pc.ps1).
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = Join-Path $env:LOCALAPPDATA 'Programs\nodejs\node.exe' }
if (-not (Test-Path $node)) { Falhar 'não achei o Node.js para rodar os testes.' }
& $node --test (Join-Path $raiz 'test\unidade.test.mjs')
if ($LASTEXITCODE -ne 0) { Falhar 'os testes do núcleo falharam; nada foi publicado.' }
foreach ($arquivo in $arquivos) { if (-not (Test-Path (Join-Path $raiz $arquivo))) { Falhar "falta web\$arquivo." } }

# Tudo vai para uma pasta nova, que só então assume o lugar: ninguém abre a página pela metade.
New-Item -ItemType Directory $temp | Out-Null
[IO.File]::WriteAllText((Join-Path $temp 'versao.txt'), "$versao`n")
Remoto "rm -rf $pastaRemota.novo && mkdir -p $pastaRemota.novo/assistir $pastaRemota.novo/baixar" | Out-Null
foreach ($arquivo in $arquivos) {
    & scp @acessoSsh -q (Join-Path $raiz $arquivo) "${servidor}:$pastaRemota.novo/$arquivo"
    if ($LASTEXITCODE -ne 0) { Falhar "o envio de $arquivo falhou." }
}
& scp @acessoSsh -q (Join-Path $temp 'versao.txt') "${servidor}:$pastaRemota.novo/versao.txt"
if ($LASTEXITCODE -ne 0) { Falhar 'o envio de versao.txt falhou.' }
Remoto ("find $pastaRemota.novo -type d -exec chmod 755 {} + && find $pastaRemota.novo -type f -exec chmod 644 {} + && rm -rf $pastaRemota.antigo && " +
        "(test -d $pastaRemota && mv $pastaRemota $pastaRemota.antigo || true) && mv $pastaRemota.novo $pastaRemota") | Out-Null

# Confere pela internet, do jeito que o iPhone vai baixar.
foreach ($arquivo in $arquivos) {
    $baixado = Join-Path $temp ("baixado-" + $arquivo.Replace('/', '-'))
    & curl.exe -s -f -o $baixado "$endereco$arquivo"
    if ($LASTEXITCODE -ne 0) { Falhar "$endereco$arquivo não respondeu." }
    if ((Get-FileHash $baixado).Hash -ne (Get-FileHash (Join-Path $raiz $arquivo)).Hash) { Falhar "$arquivo no ar não confere com o daqui." }
}
Remove-Item $temp -Recurse -Force -Confirm:$false
Write-Host "Ponte no navegador $versao publicado em $endereco" -ForegroundColor Green
