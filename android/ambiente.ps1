# Prepara o ambiente para o Gradle: ele precisa de um Java 17 ou mais novo, e o que está no PATH
# desta máquina pode ser mais antigo. Usado pelos outros scripts desta pasta (". .\ambiente.ps1").
if (-not $env:JAVA_HOME -or -not (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
    $candidatos = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\jdk-21'),
        (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr')
    )
    $achado = $candidatos | Where-Object { Test-Path (Join-Path $_ 'bin\java.exe') } | Select-Object -First 1
    if (-not $achado) { Write-Host 'Não achei um Java 17 ou mais novo. Defina JAVA_HOME.' -ForegroundColor Red; exit 1 }
    $env:JAVA_HOME = $achado
}
