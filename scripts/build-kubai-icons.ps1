<#
.SYNOPSIS
  生成 Kubai 主题的彩色分层图标 PNG（docs/design/2026-11-05-layout-redesign/claude-code-kubai-theme.md 第 3 步）。

.DESCRIPTION
  在仓库外的临时目录浅克隆 kubai-icons，用它自带的 scripts/export.mjs 导出静止与悬停两态的静态 SVG，
  再用无头 Chrome 把每个 SVG 栅格化为 96×96 透明 PNG，写入
  src/ClipboardManager.Windows/Assets/Icons/Kubai/<键名>.png 与 <键名>-hover.png，最后删除临时目录。
  只把 PNG 与 NOTICE.txt 放进仓库；kubai-icons 的 PROMPT.md、SKILL.md 等任何源文件都不拷贝。

  需要：git、Node.js、Google Chrome（或用 -Chrome 指定浏览器路径）。
#>
param(
    [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [string]$Repository = "https://github.com/kubai087/kubai-icons"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root "src/ClipboardManager.Windows/Assets/Icons/Kubai"
if (-not (Test-Path $Chrome)) { throw "找不到 Chrome：$Chrome。用 -Chrome 指定路径。" }

# 资源键 → (Kubai 图标名, 主色)。与设计文档 3.1 的表一致。
$icons = [ordered]@{
    IconNavHistory = @("时钟", "#5393FF")
    IconNavConfig  = @("文件夹", "#F59E0B")
    IconNavTodo    = @("便签", "#EC4899")
    IconNavCalc    = @("计算器", "#22C55E")
    IconNavShare   = @("同步", "#06B6D4")
    IconRowText    = @("文档", "#6366F1")
    IconRowLink    = @("链接", "#14B8A6")
    IconRowFile    = @("文档", "#F97316")
    IconFolder     = @("文件夹", "#F59E0B")
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("kubai-icons-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    git clone --depth 1 $Repository (Join-Path $work "repo")
    if ($LASTEXITCODE -ne 0) { throw "克隆 $Repository 失败。" }
    $repo = Join-Path $work "repo"
    $env:CHROME = $Chrome
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    foreach ($key in $icons.Keys) {
        $name, $accent = $icons[$key]
        foreach ($state in "rest", "hover") {
            $svgDir = Join-Path $work "svg/$key-$state"
            node (Join-Path $repo "scripts/export.mjs") --only $name --accent $accent --size 96 --state $state --out $svgDir
            if ($LASTEXITCODE -ne 0) { throw "导出 $name（$state）失败。" }
            $svg = Join-Path $svgDir "$name.svg"
            if (-not (Test-Path $svg)) { throw "没有生成 $svg。" }

            # 透明底、1 倍像素比，图片恰好铺满 96×96 视口。
            $page = Join-Path $work "page-$key-$state.html"
            $svgUri = ([System.Uri]$svg).AbsoluteUri
            Set-Content -Encoding utf8 -Path $page -Value "<!doctype html><html><body style=`"margin:0;background:transparent`"><img src=`"$svgUri`" width=`"96`" height=`"96`" style=`"display:block`"></body></html>"
            $suffix = if ($state -eq "hover") { "-hover" } else { "" }
            $png = Join-Path $target "$key$suffix.png"
            & $Chrome --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
                --default-background-color=00000000 --window-size=96,96 `
                "--user-data-dir=$(Join-Path $work 'profile')" "--screenshot=$png" ([System.Uri]$page).AbsoluteUri 2>$null | Out-Null
            if (-not (Test-Path $png)) { throw "栅格化 $key（$state）失败。" }
            Write-Host "已生成 $png"
        }
    }

    Set-Content -Encoding utf8 -Path (Join-Path $target "NOTICE.txt") -Value @"
Kubai 主题图标（*.png）

来源：https://github.com/kubai087/kubai-icons 中 src/icons.js 的示例图标，
用其 scripts/export.mjs 导出静态 SVG 后栅格化为 96×96 PNG（scripts/build-kubai-icons.ps1）。
许可：Kubai Icons License（Copyright (c) 2026 Kubai087）。示例图标可在产品内使用，无需署名；
不得将这些图标单独作为图标集、图标包或图标库分发。
"@
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
