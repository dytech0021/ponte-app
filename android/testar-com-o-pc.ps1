# Testa o núcleo do aplicativo Android contra um Ponte do Windows de verdade, nesta máquina.
#
# Sobe o Ponte com um perfil descartável e senha aleatória, e roda:
#   - InteropTest.kt: conexão direta e pelo servidor de retransmissão, senha, imagem e entrada sem aprovação;
#   - TelasTest.kt: a tela da sessão do aplicativo, num Android simulado, mostrando a imagem deste computador.
# As imagens das telas ficam na pasta capturas/android do projeto.
# A janela do Ponte e o aviso de sessão ativa aparecem na tela por alguns segundos.
#
# Uso:  .\testar-com-o-pc.ps1 [-Exe caminho\Ponte.exe]
param([string]$Exe = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows\Ponte.exe'))

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ambiente.ps1')
Add-Type -AssemblyName System.Security

if (-not (Test-Path $Exe)) { Write-Host "Não achei $Exe. Compile o Ponte do Windows antes (dotnet build -c Release)." -ForegroundColor Red; exit 1 }
$perfil = Join-Path ([IO.Path]::GetTempPath()) ('ponte-interop-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $perfil | Out-Null
$porta = 17420

# Senha aleatória e o derivado dela, do jeito que o Ponte guarda (PBKDF2-HMAC-SHA256).
$bytes = New-Object byte[] 12
$sal = New-Object byte[] 16
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes); $rng.GetBytes($sal)
$senha = [BitConverter]::ToString($bytes).Replace('-', '')
$kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes ([Text.Encoding]::UTF8.GetBytes($senha)), $sal, 200000, 'SHA256'
$config = @{ PasswordSalt = [Convert]::ToBase64String($sal); PasswordVerifier = [Convert]::ToBase64String($kdf.GetBytes(32)); Iterations = 200000; ListenPort = $porta; AutoUpdate = $false }
[IO.File]::WriteAllText((Join-Path $perfil 'config.json'), ($config | ConvertTo-Json))

$ponte = Start-Process -FilePath $Exe -ArgumentList '--perfil', "`"$perfil`"", '--sem-elevar' -WindowStyle Hidden -PassThru
$codigo = 1
try {
    # Na primeira abertura o Ponte cria a identidade e grava no perfil; o ID sai da chave pública dela.
    $lido = $null
    for ($i = 0; $i -lt 60 -and -not ($lido -and $lido.CertPfx); $i++) {
        Start-Sleep -Milliseconds 250
        try { $lido = Get-Content (Join-Path $perfil 'config.json') -Raw | ConvertFrom-Json } catch { $lido = $null }
    }
    if (-not ($lido -and $lido.CertPfx)) { throw 'O Ponte não gravou a identidade no perfil de teste.' }
    $pfx = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($lido.CertPfx), $null, 'CurrentUser')
    $certificado = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (, $pfx)
    # SPKI de uma chave P-256: cabeçalho fixo de 26 bytes e o ponto da curva.
    $cabecalho = [byte[]](0x30,0x59,0x30,0x13,0x06,0x07,0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01,0x06,0x08,0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07,0x03,0x42,0x00)
    $spki = $cabecalho + $certificado.PublicKey.EncodedKeyValue.RawData
    $resumo = [Security.Cryptography.SHA256]::Create().ComputeHash($spki)
    [byte[]]$oito = $resumo[0..7]
    [Array]::Reverse($oito)
    $id = ([decimal]::Remainder([decimal][BitConverter]::ToUInt64($oito, 0), 900000000) + 100000000).ToString('0')

    Start-Sleep -Seconds 4   # tempo de o ID ficar registrado no servidor de retransmissão
    $env:PONTE_TESTE_ALVO = "127.0.0.1:$porta"
    $env:PONTE_TESTE_SENHA = $senha
    $env:PONTE_TESTE_ID = $id
    $env:PONTE_TESTE_RELAY = $lido.RelayServer
    $env:PONTE_TESTE_CHAVE = $lido.RelayKey
    Push-Location $PSScriptRoot
    try {
        & .\gradlew.bat --no-daemon --console=plain :core:test :app:testDebugUnitTest
        $codigo = $LASTEXITCODE
    }
    finally { Pop-Location }
}
finally {
    'PONTE_TESTE_ALVO', 'PONTE_TESTE_SENHA', 'PONTE_TESTE_ID', 'PONTE_TESTE_RELAY', 'PONTE_TESTE_CHAVE' | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
    if (-not $ponte.HasExited) { Stop-Process -Id $ponte.Id -Force -Confirm:$false }
    Start-Sleep -Milliseconds 500
    $resolvedProfile = [IO.Path]::GetFullPath($perfil)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedProfile.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedProfile).StartsWith('ponte-interop-')) {
        Remove-Item -LiteralPath $resolvedProfile -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
    }
}
if ($codigo -eq 0) { Write-Host 'Interoperação com o Ponte do Windows: tudo certo.' -ForegroundColor Green } else { Write-Host 'Interoperação com o Ponte do Windows: houve falha.' -ForegroundColor Red }
exit $codigo
