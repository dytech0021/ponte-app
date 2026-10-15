# Testa o Ponte no navegador contra um Ponte do Windows de verdade, nesta máquina.
#
# Sobe um servidor de retransmissão local (relay\relay.js, com o WebSocket ligado) e um Ponte com perfil
# descartável e senha aleatória registrado nele, e roda os testes de web\test com o Node: conexão pelo
# WebSocket, canal cifrado, senha, imagem e entrada sem aprovação. A janela do Ponte e o aviso de sessão
# ativa aparecem na tela por alguns segundos.
#
# Uso:  .\testar-com-o-pc.ps1 [-Exe caminho\Ponte.exe]
param([string]$Exe = (Join-Path $PSScriptRoot '..\bin\Release\net10.0-windows\Ponte.exe'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = Join-Path $env:LOCALAPPDATA 'Programs\nodejs\node.exe' }
if (-not (Test-Path $node)) { Write-Host 'Não achei o Node.js.' -ForegroundColor Red; exit 1 }
if (-not (Test-Path $Exe)) { Write-Host "Não achei $Exe. Compile o Ponte do Windows antes (dotnet build -c Release)." -ForegroundColor Red; exit 1 }

function PortaLivre {
    $l = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}
$portaRelay = PortaLivre; $portaWs = PortaLivre; $portaDireta = PortaLivre

$perfil = Join-Path ([IO.Path]::GetTempPath()) ('ponte-web-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $perfil | Out-Null
$bytes = New-Object byte[] 12; $sal = New-Object byte[] 16
$rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.GetBytes($sal)
$senha = [BitConverter]::ToString($bytes).Replace('-', '')
$kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes ([Text.Encoding]::UTF8.GetBytes($senha)), $sal, 200000, 'SHA256'
$config = @{
    PasswordSalt = [Convert]::ToBase64String($sal); PasswordVerifier = [Convert]::ToBase64String($kdf.GetBytes(32)); Iterations = 200000
    ListenPort = $portaDireta; RelayServer = "127.0.0.1:$portaRelay"; RelayKey = ''; StartWithWindows = $false; AutoUpdate = $false
}
[IO.File]::WriteAllText((Join-Path $perfil 'config.json'), ($config | ConvertTo-Json))

$relay = $null; $ponte = $null; $codigo = 1
try {
    $info = New-Object Diagnostics.ProcessStartInfo $node
    $info.Arguments = "`"$(Join-Path $PSScriptRoot '..\relay\relay.js')`" $portaRelay"
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.EnvironmentVariables['PONTE_BIND'] = '127.0.0.1'; $info.EnvironmentVariables['PONTE_KEY'] = ''
    $info.EnvironmentVariables['PONTE_WS_PORT'] = "$portaWs"; $info.EnvironmentVariables['PONTE_WS_BIND'] = '127.0.0.1'
    $relay = [Diagnostics.Process]::Start($info)

    $ponte = Start-Process -FilePath $Exe -ArgumentList '--perfil', "`"$perfil`"", '--sem-elevar' -PassThru
    $lido = $null
    for ($i = 0; $i -lt 60 -and -not ($lido -and $lido.CertPfx); $i++) {
        Start-Sleep -Milliseconds 250
        try { $lido = Get-Content (Join-Path $perfil 'config.json') -Raw | ConvertFrom-Json } catch { $lido = $null }
    }
    if (-not ($lido -and $lido.CertPfx)) { throw 'O Ponte não gravou a identidade no perfil de teste.' }
    $pfx = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($lido.CertPfx), $null, 'CurrentUser')
    $certificado = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (, $pfx)
    $cabecalho = [byte[]](0x30,0x59,0x30,0x13,0x06,0x07,0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01,0x06,0x08,0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07,0x03,0x42,0x00)
    $resumo = [Security.Cryptography.SHA256]::Create().ComputeHash($cabecalho + $certificado.PublicKey.EncodedKeyValue.RawData)
    [byte[]]$oito = $resumo[0..7]; [Array]::Reverse($oito)
    $id = ([decimal]::Remainder([decimal][BitConverter]::ToUInt64($oito, 0), 900000000) + 100000000).ToString('0')
    Start-Sleep -Seconds 3   # tempo de o Ponte se registrar no servidor local

    $env:PONTE_TESTE_WS = "ws://127.0.0.1:$portaWs/ponte/ws"; $env:PONTE_TESTE_ID = $id; $env:PONTE_TESTE_SENHA = $senha
    & $node --test (Get-ChildItem (Join-Path $PSScriptRoot 'test') -Filter '*.test.mjs' | ForEach-Object FullName)
    $codigo = $LASTEXITCODE
}
finally {
    'PONTE_TESTE_WS', 'PONTE_TESTE_ID', 'PONTE_TESTE_SENHA' | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
    if ($ponte -and -not $ponte.HasExited) { Stop-Process -Id $ponte.Id -Force -Confirm:$false }
    if ($relay -and -not $relay.HasExited) { $relay.Kill() }
    Start-Sleep -Milliseconds 500
    Remove-Item $perfil -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
}
if ($codigo -eq 0) { Write-Host 'Ponte no navegador contra o Ponte do Windows: tudo certo.' -ForegroundColor Green } else { Write-Host 'Ponte no navegador contra o Ponte do Windows: houve falha.' -ForegroundColor Red }
exit $codigo
