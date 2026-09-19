<#
.SYNOPSIS
  Builds a single, self-contained ScreenBlurGuard.exe for distribution — no .NET runtime
  install required on the machine that runs it.

.DESCRIPTION
  Kept as a standalone script (rather than baking these settings into the .csproj) so everyday
  `dotnet build` / `dotnet run` during development stay fast and unaffected — self-contained
  single-file publishing is opt-in, only invoked here.

.EXAMPLE
  ./publish.ps1
#>

$ErrorActionPreference = "Stop"

$outDir = Join-Path $PSScriptRoot "publish"

dotnet publish (Join-Path $PSScriptRoot "ScreenBlurGuard.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir

# The .pdb (debug symbols) isn't needed to run the app and shouldn't be handed out —
# it makes reverse-engineering easier for no benefit to the end user.
$pdb = Join-Path $outDir "ScreenBlurGuard.pdb"
if (Test-Path $pdb) {
    Remove-Item $pdb -Force
}

$exe = Join-Path $outDir "ScreenBlurGuard.exe"
$sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "완료: $exe ($sizeMB MB)"
Write-Host "이 파일 하나만 배포하면 됩니다 (별도 .NET 설치 불필요)."
