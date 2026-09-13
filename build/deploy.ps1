<#
.SYNOPSIS
    AI 助手的部署脚本：构建、启动、看日志、刷新 token。

.DESCRIPTION
    对 docker compose 的薄封装，但替你处理了三件容易踩的事：

      1. 相对路径 —— compose 文件里 build context 是 ../backend、../frontend，
         脚本每次都会先切到自己的目录，从哪调用都行。
      2. .env —— 所有密钥和地址都在 .env 里，脚本会先检查它在不在，
         缺了就提示跑 init，而不是让 compose 用一堆空值把容器起起来。
      3. compose 命令 —— 新版是 `docker compose`，老版是 `docker-compose`，自动探测。

.EXAMPLE
    .\deploy.ps1 init        # 第一次：从 .env.example 生成 .env，然后去填密钥
    .\deploy.ps1 up          # 构建并启动
    .\deploy.ps1 logs ai-api # 只看后端日志
    .\deploy.ps1 token       # ServiceToken 过期了：重新登录并热更新
    .\deploy.ps1 save        # 把镜像导出成 tar，方便 scp 到服务器
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('init', 'up', 'down', 'restart', 'build', 'logs', 'ps', 'sh', 'token', 'save', 'load', 'clean', 'help')]
    [string]$Command = 'help',

    # 追加给 compose 的参数，比如 `.\deploy.ps1 logs -f ai-api` 里的 -f
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Extra
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

# compose 里的相对路径是按 compose 文件所在目录解析的，所以一切操作都锚定在脚本目录。
Set-Location $PSScriptRoot

$ProjectName = 'merchant-ai'
$EnvFile     = Join-Path $PSScriptRoot '.env'
$EnvExample  = Join-Path $PSScriptRoot '.env.example'

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "    $Text" -ForegroundColor Yellow }

# ---------- 调用外部命令 ----------
# docker / docker compose 会把**正常的进度信息**（"Container ai-redis Running"）写到 stderr。
# 在 $ErrorActionPreference='Stop' 下 PowerShell 把这类 stderr 当成终止错误抛出来，
# 结果命令明明成功了、脚本却报失败并中断后面所有步骤。所以统一走这个包装：
# 临时放宽、把两条流合并着显示、只以退出码为准。
# 输出用 Write-Host 直接打到控制台，绝不能进管道 —— 否则会和返回的退出码混成一个数组。
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments, [switch]$Quiet)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) {
            & $Exe @Arguments *> $null
        } else {
            & $Exe @Arguments 2>&1 | ForEach-Object { Write-Host "$_" }
        }
    } finally {
        $ErrorActionPreference = $prev
    }
    return $LASTEXITCODE
}

# ---------- compose 命令探测 ----------
$script:ComposeExe = $null
function Get-ComposeExe {
    if ($script:ComposeExe) { return $script:ComposeExe }
    # 命令本身不存在时 & 会抛 CommandNotFoundException，要吞掉才能继续探测下一个
    try {
        if ((Invoke-Native -Exe 'docker' -Arguments @('compose', 'version') -Quiet) -eq 0) {
            return ($script:ComposeExe = @('docker', 'compose'))
        }
    } catch { }
    try {
        if ((Invoke-Native -Exe 'docker-compose' -Arguments @('version') -Quiet) -eq 0) {
            return ($script:ComposeExe = @('docker-compose'))
        }
    } catch { }
    throw '找不到 docker compose。请先安装 Docker Desktop 或 docker-compose。'
}

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArgs)
    $exe = @(Get-ComposeExe)
    $exeName = $exe[0]
    $exeArgs = @()
    if ($exe.Count -gt 1) { $exeArgs += $exe[1..($exe.Count - 1)] }
    $exeArgs += @('-p', $ProjectName, '-f', 'docker-compose.yml')
    $exeArgs += $ComposeArgs
    $code = Invoke-Native -Exe $exeName -Arguments $exeArgs
    if ($code -ne 0) { throw "docker compose 执行失败（退出码 $code）" }
}

# ---------- .env ----------
function Read-Env {
    $map = @{}
    if (-not (Test-Path $EnvFile)) { return $map }
    foreach ($line in [System.IO.File]::ReadAllLines($EnvFile, [System.Text.Encoding]::UTF8)) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith('#')) { continue }
        $i = $t.IndexOf('=')
        if ($i -lt 1) { continue }
        $map[$t.Substring(0, $i).Trim()] = $t.Substring($i + 1).Trim()
    }
    return $map
}

function Set-EnvValue([string]$Key, [string]$Value) {
    # 一定显式 UTF8 读写。Get-Content / Set-Content 不带 -Encoding 在 PS 5.1 下按 ANSI 解，
    # 会把文件里的中文注释全部毁掉（这个项目已经因此出过一次事故）。
    $text = [System.IO.File]::ReadAllText($EnvFile, [System.Text.Encoding]::UTF8)
    $pattern = '(?m)^' + [regex]::Escape($Key) + '=.*$'
    $replacement = "$Key=$Value"
    if ([regex]::IsMatch($text, $pattern)) {
        $text = [regex]::Replace($text, $pattern, $replacement)
    } else {
        $text = $text.TrimEnd() + "`r`n$replacement`r`n"
    }
    [System.IO.File]::WriteAllText($EnvFile, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Assert-Env {
    if (-not (Test-Path $EnvFile)) {
        Write-Host "`n还没有 .env —— 先运行：  .\deploy.ps1 init`n" -ForegroundColor Red
        exit 1
    }
}

# ============================================================
# 命令
# ============================================================

function Cmd-Init {
    if (Test-Path $EnvFile) {
        Write-Warn '.env 已存在，没有覆盖。要重来请自己删掉它。'
        return
    }
    Copy-Item $EnvExample $EnvFile
    Write-Ok "已生成 $EnvFile"
    Write-Host ''
    Write-Warn '现在去填这几个值，其它可以先不动：'
    Write-Host '      MERCHANT_SERVICE_TOKEN   —— 跑 `.\deploy.ps1 token` 会自动写进去'
    Write-Host '      DEEPSEEK_API_KEY         —— 模型密钥'
    Write-Host '      SERVICE_USER / SERVICE_PASSWORD —— 刷新 token 用的服务账号（如果要用 token 命令）'
    Write-Host ''
    Write-Warn '填完再跑：  .\deploy.ps1 up'
}

function Cmd-Token {
    Assert-Env
    $cfg = Read-Env
    $base     = if ($cfg['MERCHANT_BASE_URL']) { $cfg['MERCHANT_BASE_URL'] } else { 'http://1.14.205.214:8080' }
    $identity = if ($cfg['IDENTITY_BASE_URL']) { $cfg['IDENTITY_BASE_URL'] } else { "$($base.TrimEnd('/'))/api/identity/" }
    $user     = $cfg['SERVICE_USER']
    $pass     = $cfg['SERVICE_PASSWORD']

    if (-not $user -or -not $pass) {
        throw '.env 里缺 SERVICE_USER / SERVICE_PASSWORD —— 没有服务账号就没法换 token。'
    }

    $loginUrl = $identity.TrimEnd('/') + '/auth/login'
    Write-Step "登录 $loginUrl（账号 $user）"

    # 必须自己转 UTF-8 字节再发：PS 5.1 的 Invoke-RestMethod 传字符串 body 时用系统默认编码（GBK），
    # 服务端按 UTF-8 解会得到乱码。
    $body = [System.Text.Encoding]::UTF8.GetBytes((@{ userName = $user; password = $pass } | ConvertTo-Json))
    $login = Invoke-RestMethod -Uri $loginUrl -Method Post -ContentType 'application/json; charset=utf-8' `
        -Body $body -TimeoutSec 30
    if (-not $login.token) { throw "登录成功但响应里没有 token：$($login | ConvertTo-Json -Compress)" }

    # 读一下到期时间，好告诉用户这枚 token 能用多久（不验签，只看 exp）
    $expiry = '未知'
    try {
        $p = $login.token.Split('.')[1].Replace('-', '+').Replace('_', '/')
        $p = $p.PadRight($p.Length + (4 - $p.Length % 4) % 4, '=')
        $claims = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json
        $expiry = ([DateTimeOffset]::FromUnixTimeSeconds([int64]$claims.exp).ToLocalTime()).ToString('yyyy-MM-dd HH:mm')
    } catch { }

    Set-EnvValue 'MERCHANT_SERVICE_TOKEN' $login.token
    Write-Ok "已写入 .env    账号 $($login.userName)    有效至 $expiry"

    Write-Step '重启 ai-api 让新 token 生效'
    Invoke-Compose 'up' '-d' 'ai-api'
    Write-Ok '完成'
}

function Cmd-Up {
    Assert-Env
    Write-Step '构建并启动'
    Invoke-Compose 'up' '-d' '--build'
    Write-Ok '已启动'
    Cmd-Ps
}

function Cmd-Build {
    Assert-Env
    Write-Step '构建镜像'
    Invoke-Compose 'build'
    Write-Ok '构建完成'
}

function Cmd-Down {
    Write-Step '停止并移除容器'
    Invoke-Compose 'down'
    Write-Ok '已停止（Redis 数据卷保留）'
}

function Cmd-Restart {
    Assert-Env
    Write-Step '重启'
    Invoke-Compose 'restart'
    Write-Ok '已重启'
}

function Cmd-Logs {
    $target = if ($Extra -and $Extra.Count -gt 0) { $Extra } else { @() }
    Invoke-Compose (@('logs', '--tail', '200', '-f') + $target)
}

function Cmd-Ps {
    Write-Step '容器状态'
    Invoke-Compose 'ps'
    Write-Host ''
    $cfg = Read-Env
    $port = if ($cfg['HTTP_PORT']) { $cfg['HTTP_PORT'] } else { '8090' }
    Write-Host "    AI 助手前端：  http://localhost:$port" -ForegroundColor Green
}

function Cmd-Sh {
    $target = if ($Extra -and $Extra.Count -gt 0) { $Extra[0] } else { 'ai-api' }
    Invoke-Compose 'exec' $target 'sh'
}

function Cmd-Save {
    Assert-Env
    $cfg = Read-Env
    $tag = if ($cfg['IMAGE_TAG']) { $cfg['IMAGE_TAG'] } else { 'latest' }
    $out = Join-Path $PSScriptRoot 'ai-images.tar'
    Write-Step "导出镜像到 $out"
    $code = Invoke-Native -Exe 'docker' -Arguments @('save', '-o', $out, "merchant-admin-ai-api:$tag", "merchant-admin-ai-frontend:$tag")
    if ($code -ne 0) { throw "docker save 失败（退出码 $code）" }
    $mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
    Write-Ok "已导出（$mb MB）"
    Write-Host ''
    Write-Warn '拷到服务器上（和 build 目录一起）：'
    Write-Host "      scp ai-images.tar docker-compose.yml .env user@server:/opt/merchant-ai/"
    Write-Host '      ssh user@server "cd /opt/merchant-ai && docker load -i ai-images.tar && docker compose up -d"'
}

function Cmd-Load {
    $file = if ($Extra -and $Extra.Count -gt 0) { $Extra[0] } else { Join-Path $PSScriptRoot 'ai-images.tar' }
    if (-not (Test-Path $file)) { throw "找不到 $file" }
    Write-Step "导入镜像 $file"
    $code = Invoke-Native -Exe 'docker' -Arguments @('load', '-i', $file)
    if ($code -ne 0) { throw "docker load 失败（退出码 $code）" }
    Write-Ok '导入完成'
}

function Cmd-Clean {
    Write-Host ''
    Write-Host '这会删掉 ai-redis 数据卷 —— 所有会话历史、审计轨迹、软删除记录一起没。' -ForegroundColor Red
    $answer = Read-Host '真的要继续吗？输入 yes 确认'
    if ($answer -ne 'yes') { Write-Warn '已取消'; return }
    Write-Step '停止并清除数据卷'
    Invoke-Compose 'down' '-v'
    Write-Ok '已清除'
}

function Cmd-Help {
    @'

AI 助手部署脚本

  .\deploy.ps1 init              从 .env.example 生成 .env（第一次用）
  .\deploy.ps1 token             重新登录拿 ServiceToken 写进 .env 并重启后端
  .\deploy.ps1 up                构建并启动（最常用）
  .\deploy.ps1 build             只构建，不启动
  .\deploy.ps1 ps                看容器状态和访问地址
  .\deploy.ps1 logs [服务]       跟踪日志，可跟 ai-api / ai-frontend / ai-redis
  .\deploy.ps1 restart           重启所有容器
  .\deploy.ps1 down              停止并移除容器（数据卷保留）
  .\deploy.ps1 sh [服务]         进容器，默认 ai-api
  .\deploy.ps1 save              导出镜像 tar，用于拷到服务器
  .\deploy.ps1 load [文件]       从 tar 导入镜像
  .\deploy.ps1 clean             停止并**删除数据卷**（会丢掉全部会话和轨迹）

第一次部署：init -> 填 .env -> token -> up

'@ | Write-Host
}

switch ($Command) {
    'init'    { Cmd-Init }
    'token'   { Cmd-Token }
    'up'      { Cmd-Up }
    'build'   { Cmd-Build }
    'down'    { Cmd-Down }
    'restart' { Cmd-Restart }
    'logs'    { Cmd-Logs }
    'ps'      { Cmd-Ps }
    'sh'      { Cmd-Sh }
    'save'    { Cmd-Save }
    'load'    { Cmd-Load }
    'clean'   { Cmd-Clean }
    default   { Cmd-Help }
}
