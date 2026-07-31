# DeskOrganizer — 桌面快捷方式管理工具

## 项目概述

| 属性 | 值 |
|---|---|
| 项目名称 | DeskOrganizer |
| 版本 | 2.0.0 |
| 描述 | 桌面快捷方式管理工具 |
| 运行时 | .NET 8.0 (net8.0-windows) |
| 目标平台 | Windows 10/11 (x64) |
| 构建配置 | Release |
| 应用类型 | WPF + WinForms 混合桌面应用 |
| 单实例运行 | 是 (Mutex) |
| 多语言支持 | 暂无（界面中文） |

## 核心功能

DeskOrganizer 是一款 Windows 桌面管理工具，集成以下子功能模块：

1. **桌面围栏 (Fence / FenceWindow)** — 在桌面上创建可拖拽、可调整大小的收纳区域，将桌面快捷方式/文件分门别类管理
2. **全局快捷搜索 (SearchWindow)** — 全局热键呼出搜索窗口，快速索引并查找桌面及指定路径下的文件
3. **便签 (StickyNote)** — 可贴在桌面上的电子便签，支持 Markdown 渲染、多主题配色、文件附件、自动保存
4. **系统托盘驻留 (NotifyIcon)** — 最小化到系统托盘，右键菜单提供快捷操作入口
5. **设置管理 (SettingsWindow)** — 开机自启、最小化到托盘等全局配置
6. **配置持久化与备份 (ConfigService)** — JSON 格式配置文件，支持自动备份与数据丢失恢复

---

## 架构设计

### 技术栈

- **UI 框架**: WPF (主窗口、搜索窗口、便签窗口、设置窗口) + WinForms (围栏窗口)
- **数据序列化**: System.Text.Json
- **并发模型**: async/await + CancellationToken + ConcurrentDictionary + SemaphoreSlim
- **Win32 互操作**: P/Invoke 调用 (窗口管理、热键注册、Shell 操作、图标提取、DWM 模糊特效)

### 单例模式

应用通过 `Mutex` 保证单实例运行 (`App.OnStartup`)。核心服务使用线程安全的单例：

- `ConfigService.Instance` — 配置管理
- `FenceManager.Instance` — 围栏管理
- `SearchService.Instance` — 搜索服务

### 项目结构

```
DeskOrganizer
├── App                          // WPF 应用入口，启动逻辑，全局异常处理
├── MainWindow                   // 主窗口，系统托盘，热键注册，便签管理
├── SearchWindow                 // 文件搜索窗口 (WPF)
├── SettingsWindow               // 设置窗口 (WPF)
├── StickyNoteWindow             // 便签窗口 (WPF)
│
├── Model/
│   ├── AppConfig                // 应用配置数据模型
│   ├── ConfigService            // 配置服务 (加载/保存/备份/恢复)
│   ├── FenceInfo                // 围栏数据模型
│   ├── FenceInfoConverter       // FenceInfo 的 JSON 序列化转换器
│   ├── FenceManager             // 围栏生命周期管理 (创建/删除/显示/隐藏)
│   └── StickyNote               // 便签数据模型
│
├── NoFences/                    // 围栏子模块 (WinForms 实现)
│   ├── FenceWindow              // 围栏窗口，自绘 UI，拖放支持
│   ├── EditDialog               // 围栏重命名对话框
│   ├── HeightDialog             // 标题栏高度调节对话框
│   ├── Model/
│   │   ├── EntryType            // 条目类型枚举 (File/Folder)
│   │   └── FenceEntry           // 围栏条目模型 (图标提取/打开)
│   ├── Util/
│   │   ├── Extensions           // 图形辅助扩展方法
│   │   ├── ThrottledExecution  // 节流执行器 (防抖)
│   │   └── ThumbnailProvider   // 缩略图异步生成器 (缓存+信号量)
│   └── Win32/
│       ├── BlurUtil             // 窗口背景模糊特效 (SetWindowCompositionAttribute)
│       ├── DesktopUtil          // 桌面集成 (防止最小化/粘贴到桌面)
│       ├── DropShadow           // 窗口阴影效果 (DWM API)
│       ├── IconUtil             // 系统文件夹图标获取
│       └── WindowUtil           // 窗口样式管理 (Alt-Tab 隐藏/Z-Order)
│
├── Win32/                       // 全局 Win32 互操作
│   ├── AutoStartHelper          // 开机自启 (注册表操作)
│   ├── IconHelper               // 文件图标提取 (SHGetFileInfo)
│   ├── ModifierKeys              // 修饰键枚举
│   ├── SecurityHelper            // 路径安全校验/输入消毒/正则缓存
│   └── Win32Helper              // 综合 Win32 API 封装 (热键/窗口定位/Hook)
│
├── SearchService                // 文件索引与搜索服务
├── SearchResult                  // 搜索结果模型
├── SearchResultItem              // 搜索结果展示模型 (含图标/格式化)
├── FileIndexItem                // 文件索引条目
├── IndexProgressEventArgs       // 索引进度事件
└── MatchType                    // 匹配类型枚举 (精确/前缀/包含/扩展名)
```

---

## 子功能详解

### 1. 桌面围栏 (Fence)

桌面围栏是本工具的核心功能，在桌面上创建可自定义的收纳区域。

**数据模型** (`FenceInfo`):

| 属性 | 类型 | 说明 |
|---|---|---|
| Id | string | 唯一标识符 |
| Name | string | 围栏名称 |
| X / Y / Width / Height | double | 位置和尺寸 |
| PosX / PosY | int | 像素级位置 |
| Locked / IsLocked | bool | 是否锁定 (禁止拖动/调整大小) |
| CanMinify | bool | 是否可最小化 |
| TitleHeight | int | 标题栏高度 |
| FilePaths / Files | List\<string\> | 围栏内收纳的文件路径列表 |
| BackgroundColor | string | 背景颜色 |
| Opacity | double | 不透明度 |
| CornerRadius | int | 圆角半径 |
| IconSize | int | 图标大小 |
| CreatedAt / ModifiedAt | DateTime | 创建/修改时间 |

**围栏窗口特性** (`FenceWindow`，WinForms 自绘):
- 自绘 UI (非控件组装)，包含标题栏 + 内容区网格布局
- 支持文件拖放 (DragEnter / DragDrop)
- 自定义绘制 (Paint 事件)：图标 + 文件名，支持悬停/选中高亮
- 滚动支持 (MouseWheel)
- 右键菜单：锁定、最小化、重命名、删除条目、新建围栏、调整标题高度
- 粘贴到桌面 (GlueToDesktop)：围栏窗口成为桌面子窗口，不影响 Alt-Tab
- 防止最小化 (PreventMinimize)
- 窗口阴影效果 (DWM DropShadow)
- 背景模糊特效 (BlurUtil)
- 从 Alt-Tab 隐藏 (WS_EX_TOOLWINDOW)
- 缩略图异步加载 (ThumbnailProvider + ConcurrentDictionary 缓存)
- 移动/缩放节流处理 (ThrottledExecution)
- Shell 上下文菜单集成 (ShellContextMenu)

**围栏管理** (`FenceManager`):
- `CreateFence(name)` — 创建新围栏
- `RemoveFence(fence)` — 删除围栏
- `UpdateFence(fence)` — 更新围栏配置
- `ShowAllFences()` / `HideAllFences()` / `ToggleAllFences()` — 批量显示/隐藏
- `CloseAllFences()` — 关闭所有围栏
- 自动保存位置和尺寸

### 2. 文件搜索 (Search)

**搜索服务** (`SearchService`，单例):

| 方法 | 说明 |
|---|---|
| `BuildIndexAsync(string[] paths)` | 异步构建文件索引，支持进度回调 |
| `IndexDirectoryAsync(path, token, progress, maxDepth, currentDepth)` | 递归索引目录 |
| `IndexFile(filePath)` | 索引单个文件 |
| `Search(keyword, maxResults)` | 关键词搜索 (支持精确/前缀/包含/扩展名匹配) |
| `StopIndexing()` | 停止索引任务 |
| `ClearIndex()` | 清空索引 |

**搜索特性**:
- 最大索引文件数限制 (`MaxIndexedFiles`)
- 最大搜索结果数限制 (`MaxSearchResults`)
- 四种匹配模式 (`MatchType`): Exact, Prefix, Contains, Extension
- 搜索结果评分排序 (`Score`)
- 索引进度事件 (`IndexProgressChanged`)
- CancellationToken 支持 (可取消长时间索引)
- 异步非阻塞执行

**搜索窗口** (`SearchWindow`，WPF):
- 全局热键呼出 (Alt+Space)
- 失焦自动关闭
- ListBox 展示搜索结果 (文件名/路径/大小/时间/图标)
- 键盘导航 (上下键选择，Enter 打开)
- 鼠标双击打开文件
- 进度条显示索引进度

### 3. 便签 (StickyNote)

**数据模型** (`StickyNote`):

| 属性 | 类型 | 说明 |
|---|---|---|
| Id | string | 唯一标识符 |
| Title | string | 便签标题 |
| Content | string | 便签内容 |
| X / Y / Width / Height | double | 位置和尺寸 |
| BackgroundColor | string | 背景颜色 |
| Opacity | double | 不透明度 |
| FontSize | double | 字体大小 |
| CreatedAt / ModifiedAt | DateTime | 创建/修改时间 |

**便签窗口特性** (`StickyNoteWindow`，WPF):
- 7 种预设主题配色 (theme1 ~ theme7)
- 不透明度滑块调节
- 字体大小选择 (ComboBox + 预设字号)
- Markdown 渲染 (纯文本/预览模式切换，btnToggle)
- 自动保存 (DispatcherTimer 定时保存)
- 文件附件支持 (拖放文件到便签，AttachFileToNote)
- 保存为 Markdown 文件 (SaveAsMd)
- 窗口拖动 (MouseLeftButtonDown)
- 紧贴吸附 (SnapStickyNote — 便签靠近时自动对齐)
- 关闭时确认 (OnClosing)
- 底部工具栏悬停显示 (BottomBar MouseEnter/MouseLeave)
- 全屏切换 (BtnToggle)
- 位置变化时保存 (Window_SizeChanged)

### 4. 系统托盘 (NotifyIcon)

**托盘功能** (`MainWindow.InitializeNotifyIcon`):
- 托盘图标显示
- 右键菜单项:
  - 显示搜索窗口
  - 显示设置
  - 新建便签
  - 关于
  - 退出
- 双击托盘图标打开搜索窗口
- 最小化到托盘选项

### 5. 设置 (Settings)

**设置窗口** (`SettingsWindow`，WPF):
- TabControl 控件布局
- 开机自启 (`StartWithWindows`，通过注册表 `AutoStartHelper`)
- 最小化到托盘 (`MinimizeToTray`)
- 保存/取消按钮

**配置服务** (`ConfigService`，单例):
- JSON 文件持久化 (`System.Text.Json`)
- 配置目录自动创建 (`EnsureDirectories`)
- 自动备份 (`CreateBackup`)
- 数据丢失自动恢复 (`TryAutoRestoreIfDataLoss` / `TryRestoreFromBackup`)
- 配置验证 (`ValidateConfig`)
- 配置消毒 (`SanitizeBox` — 修正异常位置/尺寸值)
- 配置文件保护 (`ProtectConfigFile`)
- 自定义 JSON 转换器 (`FenceInfoConverter`)

**配置数据模型** (`AppConfig`):

| 属性 | 类型 | 说明 |
|---|---|---|
| Version | string | 配置版本 |
| StartWithWindows | bool | 开机自启 |
| MinimizeToTray | bool | 最小化到托盘 |
| Boxes | List\<FenceInfo\> | 围栏列表 |
| StickyNotes | List\<StickyNote\> | 便签列表 |
| LastSavedAt | DateTime | 最后保存时间 |

### 6. 安全辅助 (SecurityHelper)

- `IsPathSafe(path, allowedRootDir)` — 路径安全校验 (路径遍历防护)
- `IsValidLocalPath(path)` — 本地路径合法性校验
- `GetSafeFileName(fileName)` — 文件名消毒
- `SafeRegexMatch(input, pattern, ignoreCase)` — 带超时的安全正则匹配
- `SanitizeForDisplay(input, maxLength)` — 显示文本截断消毒
- `IsValidColorString(color)` — 颜色字符串合法性校验
- 正则表达式缓存 (`ConcurrentDictionary`)，避免 DoS

### 7. Win32 互操作层

**全局热键** (`Win32Helper`):
- `RegisterGlobalHotKey` / `UnregisterGlobalHotKey` — 全局热键注册
- 默认热键: Alt+Space (HOTKEY_SEARCH)

**窗口管理**:
- `SetTopMost` / `SetWindowPos` — 窗口层级控制
- `SetToolWindow` — 设置工具窗口样式
- `SetWindowsHookEx` — 全局鼠标/键盘钩子
- `GetForegroundWindow` / `GetParent` / `GetAncestor` — 窗口关系查询
- `ShellExecuteW` / `ShellExecuteEx` — Shell 操作

**图标提取** (`IconHelper`):
- `GetFileIcon(path, large)` — 获取文件图标
- `GetDefaultIconForExtension(extension, large)` — 按扩展名获取图标
- `GetFolderIcon(large)` — 获取文件夹图标
- 基于 SHGetFileInfo Win32 API

**开机自启** (`AutoStartHelper`):
- 注册表读写 (`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`)
- `SetAutoStart` / `IsAutoStartEnabled` / `SyncAutoStartState`

---

## 启动流程

```
App.Main()
  └── App.OnStartup()
        ├── 单实例检查 (Mutex)
        ├── 全局异常处理器注册
        │     ├── ThreadException
        │     ├── UnhandledException
        │     ├── UnobservedTaskException
        │     └── DispatcherUnhandledException
        ├── ConfigService.Instance.Load()
        │     ├── EnsureDirectories()
        │     ├── 读取 JSON 配置
        │     └── TryAutoRestoreIfDataLoss()
        └── MainWindow 初始化
              ├── InitializeApplication()
              │     ├── FenceManager.Instance.LoadFences()
              │     └── LoadStickyNotes()
              ├── InitializeNotifyIcon()
              ├── RegisterHotKeys() (Alt+Space)
              └── WndProc 消息处理
```

## 退出流程

```
ExitApplication()
  ├── 注销全局热键
  ├── 关闭所有围栏 (FenceManager.CloseAllFences)
  ├── 保存便签
  ├── 保存配置 (ConfigService.Save)
  │     ├── TryWriteConfig()
  │     └── CreateBackup()
  ├── 释放 NotifyIcon
  └── Application.Shutdown()
```

---

## 文件清单

| 文件 | 说明 |
|---|---|
| `DeskOrganizer_v2.exe` | 主程序可执行文件（单文件发布，自包含） |
| `DeskOrganizer_v2.dll` | 主程序集 (WPF + WinForms 混合) |
| `DeskOrganizer_v2.pdb` | 调试符号 |
| `DeskOrganizer_v2.runtimeconfig.json` | 运行时配置 |
| `DeskOrganizer_v2.deps.json` | 依赖清单 |
| `*_cor3.dll` | WPF 原生依赖（自包含发布时随附） |

## 运行环境要求

- Windows 10 或更高版本（.NET 8 要求）
- .NET 8.0 Desktop Runtime（框架依赖发布时需要；自包含发布无需单独安装）
- Microsoft Edge WebView2 Runtime（Dashboard 功能需要）
- x64 架构
