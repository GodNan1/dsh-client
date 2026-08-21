#Requires -Version 5.1
<#
  DSH 客户端安装脚本
  1) 从本脚本所在位置（tools\）定位包根目录（上一级）
  2) 把客户端部署到 H:\DSHLauncher（如需换位置，改下面的 $targetDir）
  3) 清理旧版平铺文件和桌面旧快捷方式，只创建一个「DSH 客户端」
  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\install-shortcuts.ps1
#>
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $toolsDir
$targetDir = 'H:\DSHLauncher'

Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue

# --- 目标目录结构 ---
foreach ($sub in @('src', 'assets', 'tools', 'logs')) {
  New-Item -ItemType Directory -Path (Join-Path $targetDir $sub) -Force | Out-Null
}

# --- 复制客户端文件 ---
Copy-Item (Join-Path $root 'DSHClient.exe') $targetDir -Force
Copy-Item (Join-Path $root 'README.md') $targetDir -Force
# config.json 只在目标不存在时复制（保留用户已保存的设置）
if (-not (Test-Path (Join-Path $targetDir 'config.json'))) {
  Copy-Item (Join-Path $root 'config.json') $targetDir -Force
}
Copy-Item (Join-Path $root 'src\*') (Join-Path $targetDir 'src\') -Recurse -Force
Copy-Item (Join-Path $root 'assets\*') (Join-Path $targetDir 'assets\') -Recurse -Force
Copy-Item (Join-Path $root 'tools\*') (Join-Path $targetDir 'tools\') -Recurse -Force

# --- 清理旧版平铺在根目录的文件（一次性的布局迁移） ---
$obsolete = @(
  'DSHClient.cs', 'build.ps1', 'deepseek.ico', 'deepseek-whale.svg',
  'whale-256.png', 'deepseek-icon-preview.png', 'install-shortcuts.ps1',
  'DSH一键启动.bat', 'DSH停止服务.bat', 'DSH托盘客户端.bat',
  'start-dsh.ps1', 'stop-dsh.ps1', 'dsh-tray.ps1', 'app.ico',
  'dsh-web.log', 'dsh-web.err.log', '_compile.log'
)
foreach ($f in $obsolete) {
  $p = Join-Path $targetDir $f
  if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
}

$exe = Join-Path $targetDir 'DSHClient.exe'
if (-not (Test-Path -LiteralPath $exe)) {
  [System.Windows.Forms.MessageBox]::Show("找不到 $exe，请先运行 src\build.ps1 编译。", 'DSH 客户端', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
  exit 1
}

# --- 桌面：删旧快捷方式，只保留一个「DSH 客户端」 ---
$desktop = [Environment]::GetFolderPath('Desktop')
Get-ChildItem $desktop -Filter 'DSH *.lnk' -ErrorAction SilentlyContinue | Remove-Item -Force

$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut((Join-Path $desktop 'DSH 客户端.lnk'))
$sc.TargetPath = $exe
$sc.WorkingDirectory = $targetDir
$sc.IconLocation = "$exe,0"
$sc.Description = 'DSH Web 一键客户端：启动服务、打开网页、托盘常驻'
$sc.Save()

$msg = "部署完成：$targetDir`n桌面快捷方式：$desktop\DSH 客户端.lnk"
Write-Host $msg
try { [System.Windows.Forms.MessageBox]::Show($msg, 'DSH 客户端') | Out-Null } catch {}
