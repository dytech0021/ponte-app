# Publica o aplicativo de iPhone: compila no Mac do GitHub (workflow "iPhone", .github/workflows/ios.yml) o que está
# no main, traz o IPA e as fotos do simulador, põe o IPA numa versão do repositório público (ponte-app) e atualiza a
# fonte do SideStore e do AltStore (web\ios.json, no ar em https://filabox3d.com.br/ponte/ios.json), por onde os
# iPhones com o app instalado veem a versão nova. A assinatura é de quem instala: Apple ID gratuito, renovada pelo
# SideStore antes dos 7 dias.
#
# Uso:  .\publicar-ios.ps1     depois do push no main; a versão vem de MARKETING_VERSION e CURRENT_PROJECT_VERSION
#                              em ios\project.yml, e CURRENT_PROJECT_VERSION precisa ser maior que o da fonte no ar
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
$privado = 'dytech0021/ponte'
$publico = 'dytech0021/ponte-app'
$servidor = 'root@177.153.35.193'
# A chave SSH do servidor tem nomes diferentes nas máquinas de quem publica; vale a primeira que existir.
$chaveSsh = @('filabox_vps', 'filabox3d_deploy_ed25519') | ForEach-Object { Join-Path $env:USERPROFILE ".ssh\$_" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chaveSsh) { $chaveSsh = Join-Path $env:USERPROFILE '.ssh\filabox_vps' }
$acessoSsh = @('-i', $chaveSsh, '-o', 'BatchMode=yes')
$hostsDaChave = Join-Path $env:USERPROFILE '.ssh\filabox3d_known_hosts'
if ($chaveSsh -like '*filabox3d_deploy_ed25519' -and (Test-Path $hostsDaChave)) {
    $acessoSsh += @('-o', "UserKnownHostsFile=$hostsDaChave", '-o', 'StrictHostKeyChecking=yes')
}
$pastaRemota = '/var/www/ponte-web'
$fonte = Join-Path $raiz 'web\ios.json'
$enderecoFonte = 'https://filabox3d.com.br/ponte/ios.json'
$icone = 'https://filabox3d.com.br/ponte/icone-512.png'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ponte-ios-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

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

# O Mac compila o main do GitHub: a versão vem de lá, não da pasta daqui.
& git.exe -C $raiz fetch -q origin main
if ($LASTEXITCODE -ne 0) { Falhar 'não consegui buscar o main do GitHub.' }
$sha = (& git.exe -C $raiz rev-parse origin/main).Trim()
$projeto = (& git.exe -C $raiz show origin/main:ios/project.yml) -join "`n"
if ($projeto -notmatch 'MARKETING_VERSION: "(\d+\.\d+\.\d+)"') { Falhar 'não achei MARKETING_VERSION em ios/project.yml do main.' }
$versao = $Matches[1]
if ($projeto -notmatch 'CURRENT_PROJECT_VERSION: "(\d+)"') { Falhar 'não achei CURRENT_PROJECT_VERSION em ios/project.yml do main.' }
$numero = [int]$Matches[1]

# O SideStore só oferece a atualização a quem tem o app se o número de compilação subir.
$noAr = try { Invoke-RestMethod $enderecoFonte -TimeoutSec 15 } catch { $null }
if ($noAr.apps) {
    $anterior = $noAr.apps[0].versions[0]
    if ($numero -le [int]$anterior.buildVersion) {
        Falhar "a fonte já oferece a $($anterior.version) (compilação $($anterior.buildVersion)). Suba CURRENT_PROJECT_VERSION em ios\project.yml (está em $numero)."
    }
}
$versoesPublicas = @(& gh release list --repo $publico --limit 200 --json tagName --jq '.[].tagName')
if ($versoesPublicas -contains "ios-$versao") { Falhar "o repositório público já tem a ios-$versao. Suba MARKETING_VERSION em ios\project.yml." }
Write-Host "Compilando o Ponte para iPhone $versao (compilação $numero) no Mac do GitHub..."

$antes = @(& gh run list --repo $privado --workflow ios.yml --limit 1 --json databaseId | ConvertFrom-Json)
$ultimaAntes = if ($antes.Count) { [long]$antes[0].databaseId } else { 0 }
& gh workflow run ios.yml --repo $privado --ref main
if ($LASTEXITCODE -ne 0) { Falhar 'o GitHub não aceitou o pedido de compilação.' }
$execucao = $null
for ($i = 0; $i -lt 40 -and -not $execucao; $i++) {
    Start-Sleep -Seconds 3
    $execucao = @(& gh run list --repo $privado --workflow ios.yml --event workflow_dispatch --limit 1 --json databaseId,headSha | ConvertFrom-Json) |
        Where-Object { [long]$_.databaseId -gt $ultimaAntes } | Select-Object -First 1
}
if (-not $execucao) { Falhar 'a compilação pedida não apareceu no GitHub.' }
if ($execucao.headSha -ne $sha) { Falhar "o GitHub compilou $($execucao.headSha), não o main buscado aqui ($sha). Rode de novo." }
& gh run watch $execucao.databaseId --repo $privado --exit-status --interval 20 | Out-Null
if ($LASTEXITCODE -ne 0) {
    & gh run view $execucao.databaseId --repo $privado --log-failed | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
    Falhar "a compilação falhou (https://github.com/$privado/actions/runs/$($execucao.databaseId)); nada foi publicado."
}

New-Item -ItemType Directory $temp | Out-Null
& gh run download $execucao.databaseId --repo $privado -n ponte-ios -D $temp
if ($LASTEXITCODE -ne 0) { Falhar 'não consegui baixar o IPA compilado.' }
$feito = "$(Get-Content (Join-Path $temp 'versao.txt') -Raw)".Trim()
if ($feito -ne "$versao $numero") { Falhar "o IPA saiu com a versão '$feito', não '$versao $numero'." }

# As fotos do simulador ficam em capturas\ (fora do git) para conferir a tela inicial e a volta do login com Google.
$capturas = Join-Path $raiz 'capturas'
New-Item -ItemType Directory -Force $capturas | Out-Null
Get-ChildItem $temp -Filter *.png | ForEach-Object {
    $destino = Join-Path $capturas "iphone-$versao-$($_.Name)"
    Copy-Item $_.FullName $destino -Force
    Write-Host "Foto do simulador: $destino"
}

$nomeIpa = "Ponte-$versao-iPhone.ipa"
$ipa = Join-Path $temp $nomeIpa
Move-Item (Join-Path $temp 'Ponte.ipa') $ipa
$tamanho = (Get-Item $ipa).Length
$resumo = (Get-FileHash $ipa -Algorithm SHA256).Hash.ToLowerInvariant()
$download = "https://github.com/$publico/releases/download/ios-$versao/$nomeIpa"

$notas = @"
## Como instalar (grátis, com o seu Apple ID)

O app é assinado no próprio iPhone pelo **SideStore**, com um Apple ID gratuito. A assinatura gratuita vale 7 dias, e o SideStore renova sozinho antes disso.

1. **No PC com Windows:** instale o iTunes (o da Apple) e o [iloader](https://github.com/nab138/iloader/releases/latest) (``iloader-windows-x64.msi``). Ligue o iPhone no cabo, toque em Confiar, abra o iloader, entre com o seu Apple ID, escolha o iPhone e toque em **Install SideStore (Stable)**.
2. **No iPhone:** em Ajustes → Geral → VPN e Gerenciamento de Dispositivos, confie no seu Apple ID. Em Ajustes → Privacidade e Segurança, ligue o **Modo de Desenvolvedor** (o iPhone reinicia). Instale o **LocalDevVPN** da App Store e toque em Connect.
3. **No SideStore:** entre com o mesmo Apple ID. Em Sources, toque em + e adicione ``$enderecoFonte``. O Ponte aparece lá para instalar, e as versões novas também. (Também dá para baixar o ``.ipa`` abaixo e abrir com o SideStore.)

Com o Apple ID gratuito, o iPhone aceita até 3 apps assim (o SideStore conta como um). Se os 7 dias passarem sem renovar, o Ponte não abre até você tocar no contador de dias em My Apps, no SideStore (com o LocalDevVPN ligado); nada se perde.

O app mostra a página do Ponte (https://filabox3d.com.br/ponte/): o que muda nela chega ao app sem instalar nada.

## Conferência (SHA-256)

``````
$resumo  $nomeIpa
``````
"@
$arquivoNotas = Join-Path $temp 'notas.md'
[IO.File]::WriteAllText($arquivoNotas, $notas, [Text.UTF8Encoding]::new($false))
& gh release create "ios-$versao" --repo $publico --target main --title "Ponte para iPhone $versao" --notes-file $arquivoNotas --latest=false $ipa
if ($LASTEXITCODE -ne 0) { Falhar 'não consegui criar a versão no repositório público.' }
$baixado = Join-Path $temp 'baixado.ipa'
& curl.exe -s -L -f -o $baixado $download
if ($LASTEXITCODE -ne 0 -or (Get-FileHash $baixado -Algorithm SHA256).Hash.ToLowerInvariant() -ne $resumo) { Falhar "o IPA em $download não confere com o compilado." }

# A fonte no formato do AltStore, que o SideStore também lê. Os campos soltos no app (version, downloadURL...) são do
# formato antigo, para versões mais velhas do SideStore.
$descricao = 'Acesse o seu computador pelo iPhone: tela, mouse, teclado, som e a lista de computadores da sua conta do FilaBox. É a página do Ponte num app, com login do Google, tela acesa e tela cheia.'
$novidades = "Ponte para iPhone $versao."
$hoje = Get-Date -Format 'yyyy-MM-dd'
$dados = [ordered]@{
    name = 'Ponte'
    identifier = 'com.dytech.ponte.fonte'
    subtitle = 'Acesso remoto ao seu computador'
    description = $descricao
    iconURL = $icone
    website = 'https://filabox3d.com.br/ponte/'
    tintColor = '#006978'
    nsfw = $false
    apps = @([ordered]@{
        name = 'Ponte'
        bundleIdentifier = 'com.dytech.ponte'
        developerName = 'dytech0021'
        subtitle = 'Acesso remoto ao seu computador'
        localizedDescription = $descricao
        iconURL = $icone
        tintColor = '#006978'
        category = 'utilities'
        versions = @([ordered]@{
            version = $versao
            buildVersion = "$numero"
            date = $hoje
            localizedDescription = $novidades
            downloadURL = $download
            size = $tamanho
            minOSVersion = '16.0'
        })
        version = $versao
        versionDate = $hoje
        versionDescription = $novidades
        downloadURL = $download
        size = $tamanho
        appPermissions = [ordered]@{ entitlements = @(); privacy = [ordered]@{} }
    })
    news = @()
}
[IO.File]::WriteAllText($fonte, ($dados | ConvertTo-Json -Depth 10) + "`n", [Text.UTF8Encoding]::new($false))

& scp @acessoSsh -q $fonte "${servidor}:$pastaRemota/ios.json.novo"
if ($LASTEXITCODE -ne 0) { Falhar 'o envio de ios.json falhou.' }
Remoto "cd $pastaRemota && chmod 644 ios.json.novo && mv -f ios.json.novo ios.json" | Out-Null
$conferir = Join-Path $temp 'ios.json'
& curl.exe -s -f -o $conferir $enderecoFonte
if ($LASTEXITCODE -ne 0 -or (Get-FileHash $conferir).Hash -ne (Get-FileHash $fonte).Hash) { Falhar "$enderecoFonte no ar não confere com web\ios.json." }
Remove-Item $temp -Recurse -Force -Confirm:$false

Write-Host "Ponte para iPhone $versao publicado: https://github.com/$publico/releases/tag/ios-$versao" -ForegroundColor Green
Write-Host "Fonte do SideStore: $enderecoFonte (web\ios.json mudou: faça o commit dele)."
