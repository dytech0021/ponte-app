# Gera o APK do Ponte para Android: roda os testes e a análise do Android, compila, assina e copia
# o resultado e android-manifest.json para ..\pronto.
#
# Uso:  .\gerar-apk.ps1            testes que não dependem de nada no ar
#       .\gerar-apk.ps1 -ComOPc    antes, roda também os testes contra um Ponte do Windows nesta máquina
#
# A versão vem de versionCode e versionName em app\build.gradle.kts. Suba os dois a cada mudança:
# o Android só instala por cima um APK com versionCode maior, assinado com a mesma chave.
param([switch]$ComOPc)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ambiente.ps1')

foreach ($arquivo in 'relay.properties', 'keystore.properties') {
    if (-not (Test-Path (Join-Path $PSScriptRoot $arquivo))) {
        Write-Host "Falta android\$arquivo (fica fora do git). Veja docs\android.md." -ForegroundColor Red
        exit 1
    }
}

if ($ComOPc) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'testar-com-o-pc.ps1')
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Push-Location $PSScriptRoot
try {
    & .\gradlew.bat --no-daemon --console=plain -q :core:test :app:testDebugUnitTest :app:lintRelease :app:assembleRelease
    $codigo = $LASTEXITCODE
}
finally { Pop-Location }
if ($codigo -ne 0) {
    Write-Host 'Os testes, a análise ou a compilação falharam; o APK não foi atualizado.' -ForegroundColor Red
    exit 1
}

$saida = Join-Path $PSScriptRoot 'app\build\outputs\apk\release'
$metadata = Get-Content (Join-Path $saida 'output-metadata.json') -Raw | ConvertFrom-Json
if ($metadata.variantName -ne 'release' -or @($metadata.elements).Count -ne 1 -or
    $metadata.elements[0].outputFile -ne 'app-release.apk') {
    throw 'A compilação não produziu um único APK release assinado; nada foi copiado para pronto.'
}
$elemento = $metadata.elements[0]
$apk = Join-Path $saida $elemento.outputFile
$versao = [string]$elemento.versionName
$versionCode = [int]$elemento.versionCode
if ($versionCode -le 0 -or [string]::IsNullOrWhiteSpace($versao)) {
    throw 'Versão ausente ou inválida em output-metadata.json.'
}

# Confere a assinatura do arquivo de saída, sem mostrar certificados, senhas ou chaves.
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = $env:ANDROID_SDK_ROOT }
$localProperties = Join-Path $PSScriptRoot 'local.properties'
if (Test-Path $localProperties) {
    $sdkLine = Get-Content $localProperties | Where-Object { $_ -match '^\s*sdk\.dir\s*=' } | Select-Object -Last 1
    if ($sdkLine) { $sdk = ($sdkLine -replace '^\s*sdk\.dir\s*=\s*', '') -replace '\\([\\: =])', '$1' }
}
if (-not $sdk) { throw 'SDK Android não encontrado para verificar a assinatura do APK.' }
$apksigner = Get-ChildItem (Join-Path $sdk 'build-tools\*\apksigner.bat') |
    Sort-Object { [version]($_.Directory.Name -replace '-.*$', '') } -Descending | Select-Object -First 1
if (-not $apksigner) { throw 'apksigner não encontrado no SDK Android.' }
& $apksigner.FullName verify $apk
if ($LASTEXITCODE -ne 0) { throw 'Assinatura do APK inválida; nada foi copiado para pronto.' }
$tamanho = (Get-Item $apk).Length
if ($tamanho -le 0 -or $tamanho -gt 64MB) { throw 'APK vazio ou maior que o limite de 64 MiB do relay.' }
$resumo = (Get-FileHash $apk -Algorithm SHA256).Hash.ToLowerInvariant()
$manifesto = [ordered]@{
    versionCode = $versionCode
    versionName = $versao
    size = $tamanho
    sha256 = $resumo
} | ConvertTo-Json -Compress
$pronto = Join-Path $PSScriptRoot '..\pronto'
New-Item -ItemType Directory -Force $pronto | Out-Null
Copy-Item $apk (Join-Path $pronto 'Ponte.apk') -Force
[IO.File]::WriteAllText((Join-Path $pronto 'android-manifest.json'), $manifesto, [Text.UTF8Encoding]::new($false))
Write-Host "Ponte para Android $versao gerado em pronto\Ponte.apk ($tamanho bytes, sha256 $resumo). Manifesto: pronto\android-manifest.json." -ForegroundColor Green
