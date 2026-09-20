# 重新生成 data/oui.tsv（内置离线 OUI 厂商表）
#
# 数据来源：IEEE 公开的 MAC 地址注册表（MA-L / MA-M / MA-S 三张）
#   https://standards-oui.ieee.org/oui/oui.csv
#   https://standards-oui.ieee.org/oui28/mam.csv
#   https://standards-oui.ieee.org/oui36/oui36.csv
# 直连 standards-oui.ieee.org 会返回 418，需要走代理（本机 127.0.0.1:7890）。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\make-oui.ps1
# 产物：data\oui.tsv —— 每行 "大写十六进制前缀<TAB>注册厂商名"，前缀长度 6/7/9，UTF-8 无 BOM。
# 产物已提交进仓库，正常构建不需要跑这个脚本。

param(
    [string]$Proxy = 'http://127.0.0.1:7890',
    [string]$OutFile = (Join-Path (Split-Path $PSScriptRoot -Parent) 'data\oui.tsv')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36'
$sources = [ordered]@{
    'oui.csv'   = 'https://standards-oui.ieee.org/oui/oui.csv'
    'mam.csv'   = 'https://standards-oui.ieee.org/oui28/mam.csv'
    'oui36.csv' = 'https://standards-oui.ieee.org/oui36/oui36.csv'
}

$map = New-Object 'System.Collections.Generic.Dictionary[string,string]'
foreach ($name in $sources.Keys) {
    $path = Join-Path $env:TEMP $name
    Write-Host "下载 $name ..."
    Invoke-WebRequest -Uri $sources[$name] -OutFile $path -Proxy $Proxy -UserAgent $ua -TimeoutSec 120 -UseBasicParsing
    foreach ($row in (Import-Csv $path)) {
        $prefix = ($row.Assignment -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
        $vendor = ($row.'Organization Name' -replace '\s+', ' ').Trim()
        if ($prefix.Length -lt 6 -or $vendor.Length -eq 0) { continue }
        if (-not $map.ContainsKey($prefix)) { $map[$prefix] = $vendor }
    }
}

$lines = New-Object System.Collections.Generic.List[string]
foreach ($prefix in ($map.Keys | Sort-Object)) { $lines.Add("$prefix`t$($map[$prefix])") }
[System.IO.File]::WriteAllLines($OutFile, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "写入 $OutFile ：$($lines.Count) 条 / $((Get-Item $OutFile).Length) 字节"
