#Requires -Version 5.1
<#
  DSH 客户端编译脚本
  源码在 src\，产物 DSHClient.exe 输出到包根目录（与 config.json 同级）。
  用 .NET Framework 自带的 csc.exe 编译，无需安装任何东西。
  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File src\build.ps1
#>
$ErrorActionPreference = 'Stop'
$srcDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $srcDir
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'DSHClient.exe'
$log = Join-Path $root '_compile.log'
$icon = Join-Path $root 'assets\deepseek.ico'

if (-not (Test-Path -LiteralPath $csc)) { Write-Host "找不到 csc.exe：$csc"; exit 1 }
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }

$refs = @(
  '/r:System.dll',
  '/r:System.Core.dll',
  '/r:System.Drawing.dll',
  '/r:System.Windows.Forms.dll',
  '/r:System.Web.Extensions.dll'
)
$cmd = @(
  '/nologo',
  '/target:winexe',
  '/codepage:65001',
  "/win32icon:$icon",
  "/out:$out",
  (Join-Path $srcDir 'DSHClient.cs')
)

& $csc @refs @cmd *> $log
$code = $LASTEXITCODE
"csc exit=$code" | Add-Content -LiteralPath $log -Encoding UTF8
if ($code -eq 0 -and (Test-Path -LiteralPath $out)) {
  Write-Host "编译成功：$out"
} else {
  Write-Host "编译失败，请查看：$log"
  exit 1
}
