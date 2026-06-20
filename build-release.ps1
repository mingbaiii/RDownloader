cargo build --release
if ($LASTEXITCODE -eq 0) {
    Copy-Item -Force target/release/rdown.exe GUI/rdown.exe
    Write-Host "Copied target/release/rdown.exe → GUI/rdown.exe" -ForegroundColor Green
}
