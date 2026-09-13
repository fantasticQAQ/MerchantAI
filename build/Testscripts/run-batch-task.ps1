<#
.SYNOPSIS
    用 AI 后端跑一个批量任务，并模拟前端的自动续跑循环。

.DESCRIPTION
    这个脚本走的是和前端的「自动继续」完全相同的路径：
    带 hasMore=true 的响应会立刻用 autoContinue=true 再发一次，直到任务做完或出现待确认卡片。

    存在的意义是：批量任务要跑很久（建 1000 个商品 ≈ 20 轮 × 60 次工具调用），
    在浏览器里盯着没法调试，用脚本跑可以把每轮的耗时和进度都记下来。

.NOTES
    本文件必须存成「UTF-8 带 BOM」。Windows PowerShell 5.1 对无 BOM 的 .ps1 按系统 ANSI(GB2312) 读，
    中文注释里的字节会把字符串引号吃掉，直接语法错误。用 write 工具改完记得补 BOM：
        $c = Get-Content -Raw -Encoding UTF8 $p; Set-Content $p $c -Encoding UTF8 -NoNewline

.EXAMPLE
    & AI\scripts\run-batch-task.ps1 -TaskFile AI\scripts\task-create-1000-products.txt -Mode full
#>
param(
    [string]$Base = 'http://localhost:5100',
    [string]$UserName = 'fantastic',
    [string]$Password = '123456',
    # 任务文本走文件而不是命令行参数：Windows PowerShell 5.1 传非 ASCII 命令行参数会乱码
    [string]$TaskFile,
    [string]$Task,
    [ValidateSet('readonly', 'approve', 'full')][string]$Mode = 'full',
    [int]$MaxRounds = 60
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 默认不是 UTF-8，中文会变乱码
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

if ($TaskFile) { $Task = (Get-Content -Raw -Encoding UTF8 $TaskFile).Trim() }
if (-not $Task) { throw '必须提供 -Task 或 -TaskFile' }

# 必须自己把 JSON 转成 UTF-8 字节再发。
# Windows PowerShell 5.1 的 ConvertTo-Json 不会把中文转成 \uXXXX，
# 而 Invoke-RestMethod 传字符串 body 时用的是系统默认编码（本机是 gb2312），
# 服务端却按 UTF-8 解析 —— 实测模型收到的是一串乱码，直接拒绝执行任务。
#
# 注意：这里刻意不封装成函数 —— PowerShell 的管道会把返回的 byte[] 逐元素展开成 object[]，
# 再传给 -Body 就不是字节流了（实测登录直接 400）。
$loginJson = @{ userName = $UserName; password = $Password } | ConvertTo-Json
$loginBody = [System.Text.Encoding]::UTF8.GetBytes($loginJson)

$login = Invoke-RestMethod -Uri "$Base/api/ai/login" -Method Post `
    -ContentType 'application/json; charset=utf-8' -Body $loginBody -TimeoutSec 60
$headers = @{ Authorization = "Bearer $($login.token)" }
Write-Host "已登录：$($login.userName)（$($login.roles -join '/')），权限级别 $Mode" -ForegroundColor Cyan
Write-Host ""

$sessionId = $null
$message = $Task
$auto = $false

for ($round = 1; $round -le $MaxRounds; $round++) {
    $payloadJson = @{
        message      = $message
        sessionId    = $sessionId
        mode         = $Mode
        autoContinue = $auto
    } | ConvertTo-Json
    $payload = [System.Text.Encoding]::UTF8.GetBytes($payloadJson)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $resp = Invoke-RestMethod -Uri "$Base/api/ai/chat" -Method Post `
        -ContentType 'application/json; charset=utf-8' -Headers $headers -Body $payload -TimeoutSec 3600
    $sw.Stop()

    if ($resp.sessionId) { $sessionId = $resp.sessionId }
    $pendingCount = @($resp.pendingActions).Count

    Write-Host ("===== 第 {0} 轮  {1}s  hasMore={2}  待确认={3} =====" -f `
        $round, [int]$sw.Elapsed.TotalSeconds, $resp.hasMore, $pendingCount) -ForegroundColor Yellow
    Write-Host $resp.reply
    Write-Host ""

    if ($resp.hasMore -and $pendingCount -gt 0) { Write-Host ">>> 异常：hasMore 与待确认卡片同时出现" -ForegroundColor Red }

    if ($pendingCount -gt 0) {
        Write-Host ">>> 停下等人工确认（$pendingCount 项）" -ForegroundColor Yellow
        break
    }
    if (-not $resp.hasMore) {
        Write-Host ">>> 完成（hasMore=false）" -ForegroundColor Green
        break
    }

    $message = ''
    $auto = $true
}

Write-Host ""
Write-Host "sessionId = $sessionId"
