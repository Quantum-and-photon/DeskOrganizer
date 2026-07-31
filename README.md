# 桌面布局小工具 (DeskOrganizer v2.0)

基于 .NET 8 的 Windows 桌面图标管理工具，支持围栏分组、虚拟桌面、便签、Dashboard 可视化管理。

## 项目结构

```
桌面布局小工具net8.0-windows/
├── src/                        # 主程序源码
│   ├── MainWindow.xaml.cs      # 主窗口 + IPC HTTP 服务
│   ├── FenceWindow.cs          # 围栏窗口
│   ├── FenceManager.cs         # 围栏管理器
│   ├── ConfigService.cs        # 配置持久化
│   ├── StickyNoteWindow.cs     # 便签窗口
│   ├── VirtualDesktop.cs       # 虚拟桌面
│   └── Win32/                  # Win32 API 封装
├── DesktopManagerDashboard/    # Dashboard 管理界面
│   ├── MainWindow.xaml         # WPF 主窗口
│   ├── MainWindow.xaml.cs      # WebView2 宿主 + IPC 代理
│   └── dashboard/              # 前端页面
│       └── pages/dashboard.html
├── DeskOrganizer软件/          # 编译输出目录
│   ├── DeskOrganizer_v2.exe    # 主程序
│   ├── DesktopManagerDashboard.exe
│   ├── WebView2Loader.dll
│   ├── dashboard/              # Dashboard 前端资源
│   └── 数据/                   # 用户数据目录
└── DeskOrganizer.csproj        # 主程序项目文件
```

## 功能特性

### 围栏管理
- 创建/删除/重命名围栏分组
- 拖拽桌面图标到围栏
- 围栏颜色自定义（8 种配色）
- 围栏折叠/展开
- 围栏窗口拖动与位置记忆

### 虚拟桌面
- 4 个虚拟桌面切换
- 每个桌面独立管理围栏和图标
- 快捷键 `Win + 1/2/3/4` 切换

### Dashboard 可视化管理
- 基于 WebView2 的 Web 界面
- 桌面布局图（支持拖动围栏调整位置）
- 围栏列表管理
- 便签管理
- 文件类型分布统计
- 存储占用与备份管理

### 便签
- 创建/编辑/删除便签
- 颜色与透明度自定义
- 置顶功能

## IPC 通信架构

主程序通过 HTTP 服务（`localhost:19600`）提供 IPC 接口：

- `GET /status/` — 获取运行状态
- `GET /fences/` — 获取围栏列表
- `POST /cmd/update-fence` — 更新围栏配置
- `POST /cmd/switch-desktop` — 切换虚拟桌面
- `POST /cmd/show-dashboard` — 显示 Dashboard

Dashboard 通过 WebView2 宿主代理 fetch 请求：
1. 页面 `fetch('localhost:19600/...')` 被重写
2. `chrome.webview.postMessage` 发送到宿主
3. 宿主在后台线程执行 HTTP 请求
4. `PostWebMessageAsString` 返回结果
5. 页面通过 `message` 事件监听接收

## 编译与发布

```powershell
# 编译主程序
dotnet build DeskOrganizer.csproj -c Release

# 编译 Dashboard
dotnet build DesktopManagerDashboard/DesktopManagerDashboard.csproj -c Release

# 发布 - 自包含单文件（无需用户另装 .NET Runtime，约 146MB）
dotnet publish DeskOrganizer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o DeskOrganizer软件

# 发布 - 依赖框架（需用户机器已装 .NET 8 Desktop Runtime，约 2.5MB）
# 注意：framework-dependent + PublishSingleFile 在 .NET 8 WPF+WinForms 下无法启动，必须用多文件模式
dotnet publish DeskOrganizer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o DeskOrganizer软件_FD
# 补充 Dashboard 资源（从自包含版本拷贝）
Copy-Item DeskOrganizer软件\dashboard DeskOrganizer软件_FD\dashboard -Recurse -Force
Copy-Item DeskOrganizer软件\DesktopManagerDashboard.exe DeskOrganizer软件_FD\ -Force
Copy-Item DeskOrganizer软件\WebView2Loader.dll DeskOrganizer软件_FD\ -Force

dotnet publish DesktopManagerDashboard/DesktopManagerDashboard.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o DesktopManagerDashboard/publish_sc
```

## 技术栈

- .NET 8 (win-x64)
- WPF + WebView2
- HTML/CSS/JavaScript (Dashboard 前端)
- Tailwind CSS (CDN)
- Win32 API (窗口管理、桌面图标操作)

## 运行环境要求

- Windows 10/11 (x64)
- .NET 8 Desktop Runtime
- Microsoft Edge WebView2 Runtime
