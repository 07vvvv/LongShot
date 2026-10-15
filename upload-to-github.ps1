<#
.SYNOPSIS
把 LongShot 发布到 GitHub。**请在你自己的终端里运行**（见下方说明）。

.DESCRIPTION
DSH 沙箱阻断了 TLS 出网（连 https://www.microsoft.com 都是 SEC_E_NO_CREDENTIALS），
所以推送这一步无法代跑。这个脚本把构建、打包、打标签、推仓库、发 Release 串成一条命令。

首次使用：
  1. 先在 GitHub 网页上建一个**空仓库**（不要勾 README / .gitignore / license）
  2. 在仓库目录里执行：
       .\upload-to-github.ps1 -Repo 你的用户名/LongShot -Tag v1.0.0

后续发版：
       .\upload-to-github.ps1 -Repo 你的用户名/LongShot -Tag v1.0.1

没有 gh CLI 也能用：脚本会推源码，然后告诉你去哪儿手动传附件。
#>
param(
    [Parameter(Mandatory = $true)][string]$Repo,     # 例如 07vvvv/LongShot
    [string]$Tag = "v1.0.0",
    [switch]$SkipBuild,
    [switch]$SkipRelease
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Push-Location $projectRoot

try {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "找不到 git，请先装 Git for Windows" }

    $hasGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)

    if (-not $SkipBuild) {
        Write-Host "==> 发布构建" -ForegroundColor Cyan
        dotnet publish "$projectRoot\LongShot.csproj" -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=none `
            -o "$projectRoot\publish" --nologo -v m

        if ($LASTEXITCODE -ne 0) { throw "构建失败" }
    }

    $exe = Join-Path $projectRoot "publish\LongShot.exe"
    if (-not (Test-Path $exe)) { throw "找不到 $exe，请先构建" }

    # 打一个给用户下载的压缩包，附带校验和
    $zip = Join-Path $projectRoot "LongShot-$Tag-win-x64.zip"
    Write-Host "==> 打包 $zip" -ForegroundColor Cyan
    Remove-Item $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path $exe, (Join-Path $projectRoot "README.md") -DestinationPath $zip -Force

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    "$hash  LongShot-$Tag-win-x64.zip" | Set-Content "$zip.sha256" -Encoding ascii
    Write-Host "    SHA256 $hash"

    Write-Host "==> 提交源码" -ForegroundColor Cyan
    if (-not (Test-Path "$projectRoot\.git")) { git init | Out-Null }
    git add -A
    git diff --cached --quiet || git commit -m "LongShot $Tag"

    $remoteUrl = "https://github.com/$Repo.git"
    $remotes = git remote 2>$null
    if ($remotes -contains 'origin') { git remote set-url origin $remoteUrl }
    else { git remote add origin $remoteUrl }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    Write-Host "==> 推送 $branch → $remoteUrl" -ForegroundColor Cyan
    git push -u origin $branch

    if ($SkipRelease) { return }

    if ($hasGh) {
        Write-Host "==> 创建 Release $Tag" -ForegroundColor Cyan
        gh release create $Tag $zip "$zip.sha256" --repo $Repo --title "LongShot $Tag" --generate-notes
        Write-Host "完成：https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
    }
    else {
        Write-Host ""
        Write-Host "源码已推送。未检测到 gh CLI，Release 附件请手动上传：" -ForegroundColor Yellow
        Write-Host "  1. 打开 https://github.com/$Repo/releases/new?tag=$Tag"
        Write-Host "  2. 附件选这两个文件："
        Write-Host "       $zip"
        Write-Host "       $zip.sha256"
        Write-Host "  3. 或者装上 gh 后重跑本脚本：winget install GitHub.cli"
    }
}
finally {
    Pop-Location
}
