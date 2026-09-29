<p align="center">
  <img src="src/ProjectFileHub.App/Assets/ProjectFileHub-256.png" width="112" alt="Project File Hub icon" />
</p>

<h1 align="center">Project File Hub</h1>

<p align="center">
  Windows 11 项目级文件管理器：稳定导航、聚焦文件工作流和接近 macOS Quick Look 的 Space Preview。
</p>

<p align="center">
  <img alt="Version 1.0.0" src="https://img.shields.io/badge/version-1.0.0-0ea5e9" />
  <img alt="Windows 11 x64" src="https://img.shields.io/badge/platform-Windows%2011%20x64-2563eb" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-7c3aed" />
  <img alt="Status Stable" src="https://img.shields.io/badge/status-stable-16a34a" />
</p>

Project File Hub 只管理用户明确登记的本机项目。一次只激活一个项目，普通导航、拖放、文件操作、索引和可选 MCP 读取都不能越过当前项目根目录。核心浏览与文件操作在本机完成，不依赖云服务。

## 主要功能

- **项目工作空间**：登记多个项目，显式切换当前项目，并恢复每个项目上次的文件夹、筛选、排序和视图状态。
- **文件浏览**：惰性目录树、网格/列表、自然排序、文件名与中文首拼搜索，以及图片、视频、音频、文档和代码等类型筛选。
- **明确的递归范围**：默认只看当前层；需要时开启“含子文件夹”，通过本地 SQLite 索引读取当前目录树。
- **Space Preview**：预览图片、Markdown、文本、代码、Word 正文、音视频、文件夹摘要和 Windows 可提供的其他缩略图。
- **Markdown 工作流**：安全显示表格、代码块和项目内相对链接；链接图片可在原文上方打开并复制，不改变当前文件夹。
- **图片浏览**：滚轮缩放、拖动查看、方向键切图，并以图片、原文件和路径三种剪贴板格式复制。
- **文件操作**：F2 重命名、复制/粘贴、移动、批量选择、内部拖放、拖到外部应用、回收站删除和可用时的撤销。
- **Windows 集成**：资源管理器打开/定位、当前用户安装器、开始菜单与可选桌面快捷方式、通知区域驻留和开机启动设置。
- **本地恢复**：项目列表保留主记录、独立备份和上一份有效快照；设置、索引和项目记录位于安装目录之外。
- **Focus Canvas**：Dark · Midnight、Dark · Graphite 和 Light · Mist 三套主题，可调整项目树和详情栏宽度。

1.0.0 为缩略图和大图预览增加了有界 LRU 缓存、相同图片加载去重、并发/队列上限、过期结果保护和界面引用释放。递归索引的查询、搜索和排序也移出 UI 线程。这些改动降低了已知任务堆积和旧结果回填风险。核心回归测试通过 36/36，另完成了有限的原生候选检查；这不代表历史原生 WinUI 崩溃已经被证明只有一个根因。详细版本说明见 [docs/RELEASE_NOTES.md](docs/RELEASE_NOTES.md)。

## 下载与安装

从 [GitHub Releases](https://github.com/anjero-sudo/Project-File-Hub/releases) 下载 Windows x64 版本：

- `ProjectFileHub-Setup-1.0.0-win-x64.exe`：推荐。安装到当前用户目录，不需要管理员权限，并登记标准卸载入口。
- `ProjectFileHub-1.0.0-win-x64.zip`：便携版。解压后运行 `ProjectFileHub.exe`，不登记安装信息。

每个产物旁都有 `.sha256` 文件。安装器目前没有 Authenticode 代码签名，Windows 可能显示“未知发布者”或 SmartScreen 提示；运行前请核对 SHA-256。

安装、升级或重新构建前，如果程序正在通知区域运行，请右键圆形 F 图标并选择“完全退出”。升级和卸载只替换程序文件，不删除项目列表、设置、备份或索引数据。完整安装模型见 [docs/INSTALLER.md](docs/INSTALLER.md)。

## 基本使用

1. 打开项目管理，添加一个本机项目目录。
2. 从顶部项目选择框切换项目；在左侧目录树中选择文件夹。
3. 使用搜索框、类型筛选和“含子文件夹”缩小结果范围。
4. 选中文件并按 `Space` 预览；用 `←` / `→` 切换当前结果集中的文件。
5. 使用右键菜单、快捷键或多选操作栏完成复制、移动、重命名和回收站删除。

常用快捷键：

| 快捷键 | 功能 |
| --- | --- |
| `Space` | 打开或关闭单文件预览 |
| `←` / `→` | 切换预览文件 |
| `Esc` | 关闭预览或退出当前临时状态 |
| `Enter` | 打开所选文件或进入文件夹 |
| `F2` | 重命名 |
| `Ctrl+F` | 定位文件名搜索框 |
| `Alt+↑` | 返回上一级，不越过项目根目录 |
| `Ctrl+A` | 全选当前文件视图 |
| `Ctrl+C` / `Ctrl+V` | 复制和粘贴 |
| `Delete` | 确认后移入 Windows 回收站 |

Markdown 项目链接以当前 Markdown 文件所在目录为起点，例如：

```md
参考图：[女主正面定妆图](../人物图/女主-正面.png)
```

本地链接会重新经过项目根目录和符号链接边界检查。裸写路径仍是普通文字，需要点击的目标应使用标准 `[名称](相对路径)` 语法。

## 系统要求与限制

- 支持 Windows 11 x64；发布包自包含运行时。
- 应用内图片预览保留最长边不超过 4096 像素的显示数据。复制图片或使用默认应用打开时使用未修改的原文件。
- 图片格式仍依赖 Windows 解码器；系统无法解码的格式会显示不可用提示。
- Word 预览读取受限长度的已保存 `.doc` / `.docx` 正文和表格文字，不还原原始分页、图片、页眉页脚或完整排版。
- 文本预览拒绝二进制、不支持的编码和超过限制的文件；不会执行代码、宏或 Markdown 中的远程内容。
- 当前安装器未签名。代码签名与可验证发布者身份仍是独立发布条件。
- 稳定性检查覆盖核心自动化测试和有限的原生目录/大图切换。长期高频切换、关闭预览后导航及通知区域隐藏/恢复的完整压力验证尚未完成。

## 从源码构建

需要 Windows 11 x64、PowerShell，以及 [`global.json`](global.json) 指定的 .NET SDK。

```powershell
# 核心回归测试
.\eng\run-core-tests.ps1

# 完整 Release 构建
.\eng\build.ps1 -Configuration Release

# 生成自包含便携 ZIP 和 SHA-256；会执行真实窗口启动检查
.\eng\package-release.ps1 -Configuration Release -Runtime win-x64

# 需要 Inno Setup 6：生成当前用户安装器
.\eng\build-installer.ps1 -Runtime win-x64

# 在隔离目录验证安装、原位升级、启动、卸载和数据保留
.\eng\test-installer.ps1 -Runtime win-x64
```

项目结构：

```text
src/ProjectFileHub.App/         WinUI 3 桌面应用与 Windows 集成
src/ProjectFileHub.Core/        项目边界、浏览、索引、预览和文件操作
src/ProjectFileHub.McpServer/   可选、默认关闭的只读 MCP 适配器
tests/ProjectFileHub.Core.Tests/核心回归测试
eng/                            构建、测试与发布脚本
installer/                      Inno Setup 安装定义
docs/                           安装、MCP、版本与第三方许可说明
```

推送与 `Directory.Build.props` 完全一致的版本标签（1.0.0 对应 `v1.0.0`）后，`Publish Windows release` 工作流会重新执行测试、Release 构建、便携包启动检查和安装器安装/升级/卸载检查，再使用 [docs/RELEASE_NOTES.md](docs/RELEASE_NOTES.md) 创建 GitHub Release。提交、标签和发布均由维护者显式执行。

## 数据与隐私

- 项目列表：`%LOCALAPPDATA%\ProjectFileHub\projects.json`
- 项目列表独立备份：`%APPDATA%\Anjero\ProjectFileHub\projects.backup.json`
- 界面和项目工作区设置：`%LOCALAPPDATA%\ProjectFileHub\settings.json`
- 本地 SQLite 索引与诊断日志：应用本地数据目录

应用不会把这些状态文件写入所管理的项目。可选 MCP 适配器是独立、只读、默认关闭的 STDIO 进程；桌面应用本身不依赖 MCP。配置与工具说明见 [docs/MCP.md](docs/MCP.md)。

第三方组件声明随发布包保留，源文件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 [docs/licenses](docs/licenses)。
