# 一键启动商户 AI 助手（后端 + 前端）
# 用法：在 PowerShell 中运行  .\start.ps1
$root = $PSScriptRoot

$backend = Join-Path $root "backend\MerchantAdmin.AI.API"
$frontend = Join-Path $root "frontend"

Write-Host ""
Write-Host "== 商户 AI 助手 启动脚本 ==" -ForegroundColor Cyan

# 检查前端依赖是否已安装
if (-not (Test-Path (Join-Path $frontend "node_modules"))) {
    Write-Host "[准备] 首次运行，正在安装前端依赖..." -ForegroundColor Yellow
    Set-Location $frontend
    npm install
    Set-Location $root
}

# 1. 启动后端（独立窗口；工作目录指向 backend，dotnet run 自动读取 launchSettings 端口 5100）
Write-Host "[1/2] 启动后端 http://localhost:5100  (Swagger: /swagger)" -ForegroundColor Green
Start-Process -FilePath "dotnet" -ArgumentList "run" -WorkingDirectory $backend

# 2. 启动前端（独立窗口）
Write-Host "[2/2] 启动前端 http://localhost:5174" -ForegroundColor Green
Start-Process -FilePath "cmd.exe" -ArgumentList "/c npm run dev" -WorkingDirectory $frontend

Write-Host ""
Write-Host "启动完成，请打开 http://localhost:5174 使用。" -ForegroundColor Cyan
Write-Host "提示：首次使用前请先在 backend\MerchantAdmin.AI.API\appsettings.json"
Write-Host "      填写 DeepSeek.ApiKey 和 MerchantApi.ServiceToken，否则无法对话。" -ForegroundColor Yellow
Write-Host "停止：分别关闭后端、前端两个命令行窗口即可。" -ForegroundColor DarkGray
Write-Host ""