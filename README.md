<p align="center">
  <img src="src/ProjectFileHub.App/Assets/ProjectFileHub-256.png" width="112" alt="Project File Hub icon" />
</p>

<h1 align="center">Project File Hub</h1>

<p align="center">
  一个面向 Windows 11 的项目级文件管理器。<br />
  Stable project navigation, focused file workflows, and a macOS-like Space Preview.
</p>

<p align="center">
  <img alt="Version 0.0.6" src="https://img.shields.io/badge/version-0.0.6-0ea5e9" />
  <img alt="Windows 11 x64" src="https://img.shields.io/badge/platform-Windows%2011%20x64-2563eb" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-7c3aed" />
  <img alt="Status Development Preview" src="https://img.shields.io/badge/status-development%20preview-f59e0b" />
</p>

> [!IMPORTANT]
> `0.0.6` 是开发预览版本，适合本机试用和继续开发。GitHub Release 同时提供当前用户安装版 `Setup.exe` 和解压即用的便携 ZIP；两种产物都有 SHA-256 校验文件。安装器目前尚未进行代码签名，Windows SmartScreen 可能显示“未知发布者”。

## 为什么做这个项目

Windows 资源管理器适合浏览整个系统，却不一定适合长期管理越来越多的创作和开发项目。Project File Hub 把工作空间限制在用户明确添加的项目中：一次只打开一个项目，切换项目时恢复之前的位置，并尽量减少误跳目录、迷失上下文和重复查找文件的成本。

它不是 Windows Shell 的完整替代品，也不是知识库，而是一套更专注、更稳定的项目文件工作流。

## 0.0.6 功能概览

- **项目级工作空间**：可以登记多个项目，但同一时间只激活一个；所有导航和文件操作都受当前项目根目录约束。
- **可靠的项目记忆**：项目列表使用带修订号的主记录、独立 Roaming 备份和上一版本快照；主记录被清空、损坏或丢失时可以恢复。
- **稳定的目录树**：惰性加载子目录，支持单击选择、双击展开/收起、自然排序和更大的树节点点击区域。
- **网格与列表浏览**：30 个常用文件图标家族覆盖 221 种扩展名；图片优先显示真实缩略图，列表保持统一图标尺寸与独立类型列。
- **清晰的文件状态**：网格文件名使用稳定的两行区域，悬停可查看完整名称，网格与列表都有明确的选中反馈。
- **Space Preview**：选中文件后按空格打开 Hub 内单文件预览；方向键或预览箭头切换相邻文件，`Esc` 关闭。
- **阅读与代码预览**：Markdown 支持跨段落拖选、双击选词、三击选段、右键复制、受项目边界约束的文件/文件夹链接跳转；代码预览与代码块支持整块复制。
- **图片查看与复制**：鼠标滚轮以视口中心缩放，放大后可以拖动查看；预览中的“复制图片”、右键菜单或 Ctrl+C 会同时提供图片、文件和路径三种剪贴板格式，便于手动粘贴到其他软件。
- **常用文件操作**：右键菜单、F2 重命名、内部拖放移动、Ctrl 拖放复制、批量选择、复制/粘贴、复制到、移动到、回收站和可用时的撤销。
- **拖到外部软件**：可以把一个或多个文件、文件夹作为标准 Windows 拖放对象交给资源管理器、编辑器及其他支持文件拖放的桌面软件，同时保留路径文字作为兼容回退。
- **Windows Shell 协作**：可以从文件、文件夹或当前目录直接跳转到资源管理器；详情和预览文字支持选择与复制。
- **可控筛选范围**：按图片、视频、音频、文档、代码等类别筛选当前层，也可以显式开启“含子文件夹”读取当前文件夹整棵子树；结果显示相对位置并保持项目根目录边界。
- **Focus Canvas 界面**：提供 Dark · Midnight、Dark · Graphite 和 Light · Mist 三套主题；顶栏可快速切换主题，项目树与详情栏可拖动调整宽度。
- **自定义项目管理中心**：项目切换、状态检查和“移出管理”确认使用应用自身的 Focus Canvas 界面；Windows 文件夹选择器只负责安全选择路径。
- **GitHub 版本同步**：设置中可手动检查最新稳定 Release，也可选择启动时每天最多检查一次；应用不会跟随 `main`、静默下载或自行替换程序。
- **标准 Windows 安装器**：提供无需管理员权限的当前用户安装、开始菜单与可选桌面快捷方式、Windows“已安装的应用”登记、同 AppId 原位升级和标准卸载；安装与卸载不会删除项目列表、设置或索引数据。
- **Windows 集成**：原生圆形 F 图标、任务栏身份、通知区域驻留、后台索引、左键双击恢复并置前主窗口，以及完整退出菜单。
- **可选 MCP 适配器**：独立、默认关闭、只读；桌面应用本身不依赖 MCP 或云服务。

## 常用快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Space` | 打开或关闭单文件快速预览 |
| `←` / `→` | 在预览结果集中切换文件 |
| `Esc` | 关闭预览或退出当前临时状态 |
| `Enter` | 打开所选项目 |
| `F2` | 重命名所选文件或文件夹 |
| `Ctrl+A` | 选择当前文件视图中的全部项目 |
| `Ctrl+C` / `Ctrl+V` | 复制和粘贴 |
| `Delete` | 确认后移入 Windows 回收站 |

## Markdown 图片指引

Markdown 中使用标准链接即可把提示词和项目图片关联起来。链接以 Markdown 文件所在文件夹为起点，例如：

```md
## 镜头 03：女主回头

参考图：[女主正面定妆图](../人物图/女主-正面.png)

提示词：

女主在雨夜突然停步回头，霓虹倒映在湿润街面……
```

如果为了“复制整块”而把提示词放在 `text` 代码块中，也可以继续使用同样的链接写法。预览会让方括号中的名称保持可点击，同时保留完整的 `[名称](路径)` 原文，因此“复制整块”不会改变提示词内容；底部“自动换行”开关同时控制这类长提示词和代码块的显示。

点击图片链接会在当前 Markdown 上方打开一层图片预览，不切换文件夹、不改变 Markdown 的滚动位置；关闭图片层即可继续点下一张。图片层和普通图片预览中的“复制图片”、右键菜单及 Ctrl+C 会同时向 Windows 剪贴板提供图片画面、原文件和路径，方便用户手动粘贴到其他软件。点击文件夹链接仍会跳转到对应目录。裸写的 `../人物图/女主-正面.png` 仍是普通文字；需要点击的目标应使用 `[名称](相对路径)`，这些标记可以由 Codex 或模板自动生成。不存在的目标会在预览内明确提示，所有本地链接都会重新经过当前项目根目录和符号链接边界检查。

## 安装与便携使用

正式 Release 提供两种 Windows x64 交付方式：

- `ProjectFileHub-Setup-0.0.6-win-x64.exe`：推荐给普通用户。默认安装到 `%LOCALAPPDATA%\Programs\ProjectFileHub`，创建开始菜单快捷方式并可选择桌面快捷方式，同时登记标准卸载入口。
- `ProjectFileHub-0.0.6-win-x64.zip`：便携版。解压后直接运行 `ProjectFileHub.exe`，不登记安装与卸载信息。

安装器把可替换的自包含程序放在安装目录的 `app` 子目录中。升级时只清理这个由安装器拥有的程序区，避免旧运行时文件残留；位于 `%LOCALAPPDATA%\ProjectFileHub` 和 `%APPDATA%\Anjero\ProjectFileHub` 的项目列表、设置、备份与索引不会被升级或卸载删除。安装器使用稳定 AppId，因此后续版本会沿用用户之前选择的安装位置并原位升级。

如果电脑上正在运行 Project File Hub，安装器会通过 Windows Restart Manager 请求关闭占用程序文件的实例；它不会使用强制终止作为默认行为。安装完成页可以直接启动新版本。

## 从源码构建

### 环境要求

- Windows 11 x64
- PowerShell 7 或 Windows PowerShell
- 仓库指定的 .NET SDK（见 [`global.json`](global.json)）
- Windows App SDK/WinUI 3 所需的本机构建环境

### 构建与测试

```powershell
# 自动化核心测试
.\eng\run-core-tests.ps1

# 还原并构建完整解决方案
.\eng\build.ps1

# 在本机生成 0.0.6 便携 ZIP 与 SHA-256（不会上传）
.\eng\package-release.ps1

# 使用已有便携发布目录生成 Setup.exe 与 SHA-256
.\eng\build-installer.ps1

# 在隔离目录验证首次安装、原位升级、启动、卸载和用户数据保留
.\eng\test-installer.ps1
```

调试版本默认输出到：

```text
src/ProjectFileHub.App/bin/Debug/net10.0-windows10.0.22621.0/win-x64/
```

如果程序正在通知区域后台运行，请先在圆形 F 图标的右键菜单中选择“完全退出”，再重新构建。

## 项目结构

```text
src/
  ProjectFileHub.App/        WinUI 3 桌面应用与 Windows 集成
  ProjectFileHub.Core/       项目边界、浏览、索引、预览和文件操作服务
  ProjectFileHub.McpServer/  可选、默认关闭的只读 MCP 适配器
tests/
  ProjectFileHub.Core.Tests/ 轻量自动化核心测试
eng/                         构建、测试和图标生成脚本
installer/                   Inno Setup 安装、升级与卸载定义
docs/                        面向使用者和开发者的补充文档
```

## 数据与隐私

Project File Hub 的核心浏览和文件操作完全在本机完成，不要求云服务。

用户状态不会写进所管理的项目目录：

- 项目列表主记录：`%LOCALAPPDATA%\ProjectFileHub\projects.json`
- 项目列表独立备份：`%APPDATA%\Anjero\ProjectFileHub\projects.backup.json`
- 界面和项目工作区设置：`%LOCALAPPDATA%\ProjectFileHub\settings.json`
- 本地 SQLite 索引：应用本地数据目录

符号链接、目录联接、拖放和可选 MCP 读取都必须遵守当前活动项目根目录边界。

## 可选 MCP

MCP 服务器不是桌面应用依赖项，也不会默认启动。当前适配器仅提供活动项目信息、受边界约束的列表/搜索和有限文本读取。配置与工具说明见 [`docs/MCP.md`](docs/MCP.md)。

## GitHub 发布

推送与 `Directory.Build.props` 一致的 `v0.0.0` 标签后，仓库的 `Publish Windows release` 工作流会在 Windows runner 上重新测试、构建、启动检查并创建 GitHub Release。每个版本附带自包含的 x64 便携 ZIP、当前用户 `Setup.exe` 及二者各自的 SHA-256；安装器还会在隔离目录执行首次安装、同 AppId 原位升级、真实窗口启动、标准卸载、快捷方式和用户数据保留检查。标签、推送与 Release 发布仍是维护者的显式操作。

应用内更新功能只读取该仓库的最新稳定 GitHub Release 元数据；若仓库尚无 Release，会明确显示“尚未发布”，不会把 `main` 分支或普通提交误当成可安装更新。

## 后续计划

- 将 Windows Shell Preview Handler 放入独立进程，降低第三方预览提供程序影响主界面的风险。
- 配置组织控制的 Windows 代码签名证书，为应用与安装器提供可验证发布者身份并降低 SmartScreen 提示。
- 持续改进键盘操作、可访问性、高对比度和缩放体验。

## 反馈

这是一个仍在快速迭代的自用工具项目。欢迎通过 GitHub Issues 提交可复现的问题、交互建议和 Windows 文件工作流需求。

## 界面预览

当前 Focus Canvas 主界面方向：项目树、文件类型筛选、资源网格与详情面板。

![Project File Hub Focus Canvas 界面预览](design/reference/focus-canvas-normal-browser-draft-v1.png)
