# 桌面布局图围栏拖动修复记录

## 问题描述

在 Dashboard 桌面布局图中拖动围栏矩形调整位置时，整个页面向上滚动，导致桌面布局图移出可视范围。

## 根因分析

经过多轮排查，发现问题由多个因素叠加导致：

### 1. 主滚动容器自动滚动（核心问题）

Dashboard 页面的主内容区 `<div data-scroll-region="primary" class="overflow-y-auto">` 是可滚动容器。当用户在围栏矩形上 `mousedown` 时，WebView2/Edge 浏览器引擎会自动将滚动容器滚动到被点击元素的位置（`scrollIntoView` 行为），即使 `e.preventDefault()` 也不完全阻止。

### 2. WebView2 fetch 代理的 ExecuteScriptAsync 副作用

原版 fetch 代理通过 `ExecuteScriptAsync` 在页面中执行 JS 返回结果，这会导致 WebView2 重新布局，可能触发滚动位置变化。

### 3. checkConnection 定时刷新

30 秒的 `checkConnection` 定时器会调用 `loadFenceList` → `renderDesktopMap` 重建围栏矩形，可能导致页面跳动。

## 修复方案

### 修复 1：拖动时锁定所有滚动容器

在 `mousedown` 时禁用 `html`、`body`、`data-scroll-region` 三个层面的 `overflow`：

```javascript
// mousedown 时
htmlEl.style.overflow = 'hidden';
bodyEl.style.overflow = 'hidden';
if (scrollRegion) scrollRegion.style.overflow = 'hidden';

// mouseup 时恢复
s.htmlEl.style.overflow = '';
s.bodyEl.style.overflow = '';
if (s.scrollRegion) s.scrollRegion.style.overflow = '';
// 先恢复 scrollTop 再恢复 overflow
s.scrollRegion.scrollTop = s.savedScrolls.region;
```

### 修复 2：WebView2 fetch 代理改用 PostWebMessageAsString

将 `ExecuteScriptAsync` 替换为 `PostWebMessageAsString` + 事件监听，避免 `ExecuteScriptAsync` 触发重新布局：

```csharp
// 旧方式（有副作用）
webView.CoreWebView2.ExecuteScriptAsync($"window.__ipcResults[{id}] = {result};");

// 新方式
webView.CoreWebView2.PostWebMessageAsString($"{{\"id\":{id},\"result\":{result}}}");
```

页面端改用事件监听替代 `setInterval` 轮询：

```javascript
window.chrome.webview.addEventListener('message', function(e) {
    var data = JSON.parse(e.data);
    if (data.id && window.__ipcResolvers[data.id]) {
        window.__ipcResolvers[data.id](data.result || {});
        delete window.__ipcResolvers[data.id];
    }
});
```

### 修复 3：拖动后 5 秒内阻止 checkConnection 刷新

```javascript
var recentlyDragged = (Date.now() - _lastDragTime) < 5000;
if (!_dragState && !recentlyDragged) {
    updateDashboardData(status);
}
```

### 修复 4：围栏拖动坐标计算改为绝对位置

从增量方式改为绝对位置计算，避免累积误差：

```javascript
var newLeft = (e.clientX - s.grabX - vpBox.left - _mapPanX) / _mapZoom;
var newTop = (e.clientY - s.grabY - vpBox.top - _mapPanY) / _mapZoom;
```

### 修复 5：update-fence 后端不再移动围栏窗口

后端 `update-fence` 只保存配置，不调用 `SetWindowPos` 移动实际围栏窗口，避免 `FenceChanged` 回调副作用：

```csharp
// 只保存配置，不移动围栏窗口
ConfigService.Instance.Save();
```

## 涉及文件

- `DesktopManagerDashboard/dashboard/pages/dashboard.html` — 前端拖动逻辑
- `DesktopManagerDashboard/MainWindow.xaml.cs` — WebView2 fetch 代理
- `src/MainWindow.xaml.cs` — 后端 update-fence 处理
