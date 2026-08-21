# DSH 客户端（一键启动）

把每次都要敲 `pnpm dsh web` 再手动开浏览器的手动操作，变成一个桌面应用。

## 使用

双击桌面 **「DSH 客户端」** 即可：

- 服务未运行 → 自动在后台启动 `node --import tsx/esm apps/cli/src/bin.ts web`（等价于 `pnpm dsh web`）→ 就绪后自动打开浏览器并托盘提示
- 服务已在运行 → 直接打开主窗口（或唤起已驻留托盘的实例）

主窗口里包含全部功能：**启动服务 / 停止服务 / 打开网页 / 查看日志 / 设置 / 退出**。

- **时段提醒**：实时显示当前是「高峰期」还是「空闲期」（默认高峰为**北京时间 9:00–12:00、14:00–18:00**，可在设置里改）；每个时段切换前 5 分钟弹 Windows 通知提醒剩余时间
- **余额显示**：窗口实时显示 DeepSeek 账户余额（自动读取 DSH 凭据里的 API Key，无需手动配置；也可在设置里手动填），15 分钟自动刷新，也可点「刷新」
- **托盘常驻**：最小化窗口即隐藏到托盘；图标颜色实时反映状态（绿=运行中、灰=已停止）；双击图标开网页/启动，右键菜单可启动/停止/看日志/退出
- 客户端已在托盘运行时，再双击桌面快捷方式会把主窗口调到前台

## 首次安装

1. 双击 `build.ps1` 编译生成 `DSHClient.exe`（用系统自带 csc，无需安装任何东西）；
2. 双击 `install-shortcuts.ps1`：把程序复制到 `H:\DSHLauncher`，并在桌面创建唯一一个「DSH 客户端」快捷方式。

之后每次只需要双击这一个图标。想开机自启：把「DSH 客户端」快捷方式复制到 `shell:startup` 文件夹（或给它加 `--tray` 参数直接入托盘）。

## 配置（config.json）

```json
{
  "checkoutPath": "%USERPROFILE%\\deepseek-harness",
  "nodePath": "",
  "port": 3080,
  "autoOpenBrowser": true,
  "minimizeToTray": true,
  "peakWindows": [
    { "start": 9, "end": 12 },
    { "start": 14, "end": 18 }
  ],
  "apiKey": ""
}
```

- **跨电脑可移植**：路径支持 `%环境变量%`（如 `%USERPROFILE%`）和 `~`（用户主目录），**留空则自动检测**；换电脑只需在「设置」里点「自动检测」或改一下路径
  - `checkoutPath`：DSH 仓库目录，留空自动检测（`DSH_CHECKOUT_PATH` 环境变量 > 常见位置）
  - `nodePath`：node.exe 路径，留空自动检测（常见安装位置 + PATH）
- `port`：Web 服务端口，默认 3080
- `peakWindows`：高峰期时段列表（**北京时间** UTC+8，每段 `start`-`end` 小时 0-23，`end<=start` 表示跨天），其余时间为空闲期；默认 9-12、14-18
- `apiKey`：DeepSeek API Key（留空则自动读取 `DEEPSEEK_API_KEY` 环境变量或 `$DSH_HOME\.credentials.yaml`）
- 也可直接在客户端「设置」窗口里改（含路径自动检测按钮）

## 目录结构

```
H:\DSHLauncher\
├── DSHClient.exe      ← 客户端主程序（桌面快捷方式指向它）
├── config.json        ← 配置（必须在 EXE 旁边）
├── src\               ← 源码与编译脚本（DSHClient.cs、build.ps1）
├── assets\            ← 图标素材（deepseek.ico、鲸鱼 SVG、渲染图）
├── tools\             ← 安装脚本（install-shortcuts.ps1）
├── logs\              ← 运行日志（dsh-web.log / dsh-web.err.log）
└── README.md
```

## 文件说明

| 文件 | 作用 |
|---|---|
| `DSHClient.exe` | 客户端主程序（单一入口，窗口+托盘+启停+日志+设置全在这里） |
| `src\DSHClient.cs` | 源码（C#，.NET Framework 4.x） |
| `src\build.ps1` | 重新编译（产物输出到根目录） |
| `tools\install-shortcuts.ps1` | 部署到 `H:\DSHLauncher` 并创建桌面快捷方式 |
| `config.json` | 配置 |
| `assets\deepseek.ico` | 应用图标（DeepSeek 蓝底白鲸） |
| `assets\deepseek-whale.svg` / `whale-256.png` | 官方鲸鱼矢量图与渲染位图（图标素材） |
| `logs\dsh-web.log` / `dsh-web.err.log` | 服务输出日志（客户端里可查看） |

## 日志与进程

- 日志写在 `logs\dsh-web.log` / `logs\dsh-web.err.log`，客户端「查看日志」实时滚动显示
- 服务由客户端直接 spawn 的 node 进程运行；「停止服务」按进程号结束整棵进程树
- 如果服务不是本客户端启动的（例如之前手动 `pnpm dsh web` 起的），停止时会弹窗询问是否按端口强制结束

## 常见问题

- **启动失败/超时**：多半是仓库缺少构建产物。先在 DSH 仓库根目录执行一次 `pnpm run build`，再看日志。
- **找不到 node**：在「设置」里手动填 `node.exe` 的完整路径。
- **桌面图标没变**：Windows 会缓存图标，按 F5 刷新桌面，或重启资源管理器。

## 卸载

- 托盘图标上右键「退出」；
- 删除桌面「DSH 客户端」快捷方式；
- 删除 `H:\DSHLauncher` 目录。
