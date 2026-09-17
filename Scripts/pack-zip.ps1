$pluginName = "QuickLook.Plugin.PbpViewer"

# Удаляем старый пакет
Remove-Item "..\$pluginName.qlplugin" -ErrorAction SilentlyContinue
Remove-Item "..\$pluginName.zip" -ErrorAction SilentlyContinue

# Берём файлы из release (или bin\Release — смотри куда у тебя реально сыпется)
$releasePath = "..\bin\release"
if (-not (Test-Path $releasePath)) {
    $releasePath = "..\release"
}

$files = Get-ChildItem -Path $releasePath -Exclude *.pdb,*.xml,*.qlplugin,*.zip

Compress-Archive -Path $files.FullName -DestinationPath "..\$pluginName.zip" -Force
Move-Item "..\$pluginName.zip" "..\$pluginName.qlplugin" -Force

Write-Host "Packed plugin -> $((Resolve-Path ..\$pluginName.qlplugin).Path)"