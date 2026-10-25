# Builds AimTrainer.exe and the shareable zips from AimTrainer.cs.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /codepage:65001 /target:winexe /optimize /out:AimTrainer.exe `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll AimTrainer.cs
if ($LASTEXITCODE -ne 0) { throw 'Compile failed' }

# AimTrainer.zip: the app plus the readme
$pkg = Join-Path $env:TEMP 'atpkg\AimTrainer'
if (Test-Path (Split-Path $pkg)) { Remove-Item (Split-Path $pkg) -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item AimTrainer.exe, README.txt $pkg
Compress-Archive -Path $pkg -DestinationPath AimTrainer.zip -Force
Remove-Item (Split-Path $pkg) -Recurse -Force

# AimTrainer-Share.zip: password "aim", so Discord accepts it
$7z = 'C:\Program Files\7-Zip\7z.exe'
if (Test-Path $7z) {
    if (Test-Path AimTrainer-Share.zip) { Remove-Item AimTrainer-Share.zip }
    $note = Join-Path $env:TEMP 'PASSWORD is aim.txt'
    Set-Content -Path $note -Encoding UTF8 -Value "The password for AimTrainer.zip in here is:  aim`r`n`r`n1. Double-click AimTrainer.zip (or right-click > Extract All).`r`n2. Type the password: aim`r`n3. Open the AimTrainer folder and double-click AimTrainer.exe.`r`n4. If Windows says 'Windows protected your PC', click More info > Run anyway."
    & $7z a -tzip -mem=ZipCrypto -paim AimTrainer-Share.zip AimTrainer.zip | Out-Null
    & $7z a -tzip AimTrainer-Share.zip $note | Out-Null
    Remove-Item $note
}
Write-Output "Built AimTrainer.exe, AimTrainer.zip and AimTrainer-Share.zip"
