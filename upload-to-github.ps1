<#
.SYNOPSIS
把 LongShot 发布到 GitHub：构建 → 打包 → 提交 → 推送 → 发 Release，一条命令走完。

.DESCRIPTION
产物布局：
  - 源码进仓库（bin / obj / publish / zip / 启动日志都在 .gitignore 里）
  - Release 附件 = LongShot-<Tag>-win-x64.zip（里面只有 exe）+ 同名 .sha256

首次使用：
  1. 先在 GitHub 网页上建一个**空仓库**（不要勾 README / .gitignore / license）
  2. 在仓库目录里执行：
       .\upload-to-github.ps1 -Repo 你的用户名/LongShot -Tag v1.0.0

后续发版（Tag 已存在的会改为覆盖附件，不会重复建 Release）：
       .\upload-to-github.ps1 -Repo 你的用户名/LongShot -Tag v1.0.1

没有 gh CLI 也能用：脚本会推源码，然后告诉你去哪儿手动传附件。

若 push 失败，先确认本机能正常访问 github.com——公司代理或防火墙常拦这一跳。
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
    # 只放 exe：README 在仓库页面和 Release 说明里都看得到，压缩包里不必再塞一份
    Compress-Archive -Path $exe -DestinationPath $zip -Force

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    "$hash  LongShot-$Tag-win-x64.zip" | Set-Content "$zip.sha256" -Encoding ascii
    Write-Host "    SHA256 $hash"

    Write-Host "==> 提交源码" -ForegroundColor Cyan
    if (-not (Test-Path "$projectRoot\.git")) { git init | Out-Null }
    git add -A
    # 这里绝不能用 pwsh 7 的 `||`：Windows PowerShell 5.1（双击 .ps1 时的默认引擎）
    # 会直接报「标记 || 不是此版本中的有效语句分隔符」，整个脚本一行都执行不了。
    git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) { git commit -m "LongShot $Tag" }

    $remoteUrl = "https://github.com/$Repo.git"
    $remotes = git remote 2>$null
    if ($remotes -contains 'origin') { git remote set-url origin $remoteUrl }
    else { git remote add origin $remoteUrl }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    Write-Host "==> 推送 $branch → $remoteUrl" -ForegroundColor Cyan
    git push -u origin $branch
    # 原生命令的非零退出码不会触发 $ErrorActionPreference='Stop'，必须自己查；
    # 否则推送失败会一路静默走下去，用户还以为发布成功了。
    if ($LASTEXITCODE -ne 0) {
        throw "推送失败（退出码 $LASTEXITCODE）。请确认本机能访问 github.com，且 git 凭据有效。"
    }

    if ($SkipRelease) { return }

    if ($hasGh) {
        # Tag 已存在时 create 会直接报错，所以先探一下：存在就只覆盖附件，
        # 并且**不动**已经写好的 Release 说明（--generate-notes 会把它冲掉）。
        gh release view $Tag --repo $Repo *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "==> Release $Tag 已存在，覆盖附件" -ForegroundColor Cyan
            gh release upload $Tag $zip "$zip.sha256" --repo $Repo --clobber
        }
        else {
            Write-Host "==> 创建 Release $Tag" -ForegroundColor Cyan
            gh release create $Tag $zip "$zip.sha256" --repo $Repo --title "LongShot $Tag" --generate-notes
        }

        if ($LASTEXITCODE -ne 0) { throw "Release 处理失败（退出码 $LASTEXITCODE）" }
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
