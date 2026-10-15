# Atualiza o repositório público do Ponte (github.com/dytech0021/ponte-app) com o código do main deste repositório,
# sem a chave que assina o APK do Android. Este repositório (dytech0021/ponte) continua privado porque o histórico dele
# tem essa chave; o público começou do zero e ganha um commit a cada vez que este script roda.
#
# Uso:  .\publicar-github.ps1     depois do push no main (publica o que está no main do GitHub, não o que está aqui)
$ErrorActionPreference = 'Stop'
$origem = $PSScriptRoot
$publico = 'https://github.com/dytech0021/ponte-app.git'
$trabalho = Join-Path $env:LOCALAPPDATA 'Ponte\ponte-app'
$autor = @('-c', 'user.name=dytech0021', '-c', 'user.email=288444726+dytech0021@users.noreply.github.com')
# Fica de fora: a chave do APK e a senha dela. Os outros segredos nem entram no git (veja .gitignore), mas a conferência
# abaixo recusa a publicação se algum deles aparecer, por nome de arquivo ou pelo conteúdo.
$fora = @('chaves', 'android/keystore.properties')
$proibidos = @('*.jks', '*.keystore', '*.p12', '*.pfx', 'keystore.properties', 'relay.properties', 'RelayDefaults.cs', '*.chave', 'local.properties')

function Falhar($mensagem) {
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    exit 1
}

function Rodar {
    & git.exe @args
    if ($LASTEXITCODE -ne 0) { Falhar "git $($args -join ' ') falhou." }
}

# Os segredos que não podem aparecer no público: senhas da chave do APK e a chave do servidor de retransmissão.
function Segredos {
    $valores = @()
    $arquivo = Join-Path $origem 'android\keystore.properties'
    if (Test-Path $arquivo) {
        $valores += Get-Content $arquivo | Where-Object { $_ -match '^\s*(storePassword|keyPassword)\s*=\s*(.+)$' } | ForEach-Object { $Matches[2].Trim() }
    }
    $arquivo = Join-Path $origem 'android\relay.properties'
    if (Test-Path $arquivo) {
        $valores += Get-Content $arquivo | Where-Object { $_ -match '^\s*key\s*=\s*(.+)$' } | ForEach-Object { $Matches[1].Trim() }
    }
    $arquivo = Join-Path $origem 'RelayDefaults.cs'
    if (Test-Path $arquivo) {
        if ((Get-Content $arquivo -Raw) -match 'Key\s*=\s*"([^"]+)"') { $valores += $Matches[1] }
    }
    return @($valores | Where-Object { $_.Length -ge 6 } | Select-Object -Unique)
}

Rodar -C $origem fetch -q origin main
$sha = (& git.exe -C $origem rev-parse origin/main).Trim()

if (-not (Test-Path (Join-Path $trabalho '.git'))) {
    New-Item -ItemType Directory -Force (Split-Path $trabalho) | Out-Null
    Rodar clone -q $publico $trabalho
}
Rodar -C $trabalho fetch -q origin
& git.exe -C $trabalho rev-parse -q --verify origin/main *> $null
if ($LASTEXITCODE -eq 0) { Rodar -C $trabalho checkout -q -B main origin/main }
else { Rodar -C $trabalho symbolic-ref HEAD refs/heads/main }   # repositório ainda vazio

# O que já foi publicado: o commit do privado fica anotado na última mensagem do público.
$anterior = ''
& git.exe -C $trabalho rev-parse -q --verify HEAD *> $null
if ($LASTEXITCODE -eq 0) {
    $ultima = (& git.exe -C $trabalho log -1 --format=%B) -join "`n"
    if ($ultima -match 'Espelho-de: ([0-9a-f]{40})') { $anterior = $Matches[1] }
}
if ($anterior -eq $sha) {
    Write-Host 'O repositório público já está com o main de agora.' -ForegroundColor Green
    exit 0
}

# Troca todo o conteúdo pelo do main (sem o que fica de fora).
Get-ChildItem -LiteralPath $trabalho -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force
$tar = Join-Path ([IO.Path]::GetTempPath()) "ponte-publico-$($sha.Substring(0, 12)).tar"
$excluir = $fora | ForEach-Object { ":(exclude)$_" }
Rodar -C $origem archive --format=tar -o $tar origin/main -- . @excluir
& (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $tar -C $trabalho
if ($LASTEXITCODE -ne 0) { Falhar 'não consegui extrair o código.' }
Remove-Item $tar -Force

$achados = Get-ChildItem -LiteralPath $trabalho -Recurse -Force -File | Where-Object {
    $nome = $_.Name
    $_.FullName -notlike '*\.git\*' -and @($proibidos | Where-Object { $nome -like $_ }).Count -gt 0
}
if ($achados) { Falhar "arquivo que não pode ser público: $(($achados | ForEach-Object { $_.Name }) -join ', '). Nada foi publicado." }
Rodar -C $trabalho add -A
foreach ($segredo in Segredos) {
    & git.exe -C $trabalho grep --cached -q -F -e $segredo
    if ($LASTEXITCODE -eq 0) {
        Rodar -C $trabalho reset -q
        Falhar 'uma senha ou chave apareceu no código que seria publicado. Nada foi publicado.'
    }
}
& git.exe -C $trabalho diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host 'Nada mudou no código público.' -ForegroundColor Green
    exit 0
}

# A mensagem conta o que mudou no privado desde a última publicação.
$intervalo = if ($anterior) { "$anterior..$sha" } else { $sha }
$assuntos = @(& git.exe -C $origem log --format=%s $intervalo)
if (-not $anterior) {
    $titulo = 'Código do Ponte'
    $corpo = 'Primeira publicação. O histórico anterior fica no repositório privado, que guarda a chave do APK.'
} elseif ($assuntos.Count -gt 1) {
    $titulo = "Atualização com $($assuntos.Count) mudanças"
    $corpo = ($assuntos | ForEach-Object { "- $_" }) -join "`n"
} else {
    $titulo = $assuntos[0]
    $corpo = ''
}
$mensagem = "$titulo`n`n$(if ($corpo) { "$corpo`n`n" })Espelho-de: $sha`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`n"
$arquivo = Join-Path ([IO.Path]::GetTempPath()) 'ponte-publico-mensagem.txt'
[IO.File]::WriteAllText($arquivo, $mensagem, [Text.UTF8Encoding]::new($false))
Rodar -C $trabalho @autor commit -q -F $arquivo
Remove-Item $arquivo -Force
Rodar -C $trabalho push -q origin main
Write-Host "Repositório público atualizado: https://github.com/dytech0021/ponte-app ($titulo)" -ForegroundColor Green
