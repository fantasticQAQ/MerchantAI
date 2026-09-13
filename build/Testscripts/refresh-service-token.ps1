<#
.SYNOPSIS
    用服务账号登录云上 Identity，把拿到的 JWT 写进 appsettings.Development.json 的 MerchantApi:ServiceToken。

.DESCRIPTION
    AI 后端调业务接口用的是配置里的 ServiceToken，它有有效期（实测云上签发的是 2 天）。
    过期后的表现是「AI 突然查不到数据 / 工具全部报错」，不会有明显提示，所以过期了重跑一次这个脚本就行。

    刻意没有做「自动续期」：那需要在后端存一份服务账号密码并自己管理刷新时机，
    对这个规模的项目来说，一个能重跑的脚本更简单也更透明。

.EXAMPLE
    & AI\scripts\refresh-service-token.ps1
#>
param(
    [string]$Base = 'http://1.14.205.214:8080',
    [string]$UserName = 'fantastic',
    [string]$Password = '123456',
    [string]$Settings = "$PSScriptRoot\..\..\src\MerchantAI.API\appsettings.Development.json"
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

$loginUrl = "$($Base.TrimEnd('/'))/api/identity/auth/login"
Write-Host "登录 $loginUrl ..." -ForegroundColor Cyan

# 必须自己转 UTF-8 字节再发：Windows PowerShell 5.1 的 Invoke-RestMethod 传字符串 body 时
# 用的是系统默认编码（GBK），服务端按 UTF-8 解会得到乱码。
$body = [System.Text.Encoding]::UTF8.GetBytes((@{ userName = $UserName; password = $Password } | ConvertTo-Json))
$login = Invoke-RestMethod -Uri $loginUrl -Method Post -ContentType 'application/json; charset=utf-8' `
    -Body $body -TimeoutSec 30

if (-not $login.token) { throw "登录成功但响应里没有 token：" + ($login | ConvertTo-Json -Compress) }

# 读一下到期时间，好告诉用户这枚 token 能用多久（不验签，只看 exp）
$expiry = '未知'
try {
    $payload = $login.token.Split('.')[1].Replace('-', '+').Replace('_', '/')
    $payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
    $claims = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
    $expiry = ([DateTimeOffset]::FromUnixTimeSeconds($claims.exp).ToLocalTime()).ToString('yyyy-MM-dd HH:mm')
} catch { }

# 写回配置。**必须显式用 UTF8 读写** —— Get-Content 不带 -Encoding 在 PS 5.1 下按 ANSI 解，
# 会把文件里的中文全部毁掉（这个项目已经因此出过一次事故）。
$path = (Resolve-Path $Settings).Path
$json = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
$updated = [regex]::Replace(
    $json,
    '("ServiceToken":\s*)"[^"]*"',
    { param($m) $m.Groups[1].Value + '"' + $login.token + '"' })
[System.IO.File]::WriteAllText($path, $updated, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "已写入 $path" -ForegroundColor Green
Write-Host "登录账号：$($login.userName)   有效至：$expiry" -ForegroundColor Green
Write-Host ""
Write-Host "记得重启 AI 后端让新 token 生效。" -ForegroundColor Yellow
