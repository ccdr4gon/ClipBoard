param([string]$ArchiveUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $repo 'tools\media'
$cache = Join-Path $repo 'out\media-download'
New-Item -ItemType Directory -Path $destination, $cache -Force | Out-Null
$archive = Join-Path $cache 'ffmpeg-essentials.zip'
$ProgressPreference = 'SilentlyContinue'
$expected = ((Invoke-WebRequest -Uri "$ArchiveUrl.sha256").Content.Trim() -split '\s+')[0]
if ($expected -notmatch '^[0-9a-fA-F]{64}$') { throw '未获得有效的 FFmpeg SHA-256 校验值。' }
if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    Write-Output '正在下载 FFmpeg 媒体组件…'
    Invoke-WebRequest -Uri $ArchiveUrl -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'FFmpeg 下载校验失败。' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($fileName in @('ffmpeg.exe','ffprobe.exe')) {
        $entry = $zip.Entries | Where-Object { $_.FullName.EndsWith("/bin/$fileName") } | Select-Object -First 1
        if ($null -eq $entry) { throw "压缩包中找不到 $fileName" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $destination $fileName), $true)
    }
    $license = $zip.Entries | Where-Object { $_.Name -match '^(LICENSE|COPYING)(\.txt)?$' } | Select-Object -First 1
    if ($null -ne $license) {
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($license, (Join-Path $destination 'FFmpeg-LICENSE.txt'), $true)
    }
} finally { $zip.Dispose() }
@{ source=$ArchiveUrl; sha256=$expected; installedAt=(Get-Date).ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'ffmpeg-source.json') -Encoding utf8
& (Join-Path $destination 'ffmpeg.exe') -version | Select-Object -First 1
Write-Output "媒体组件已安装到 $destination；重新构建后会复制到程序目录。"
