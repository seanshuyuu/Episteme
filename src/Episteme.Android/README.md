# Episteme.Android · P5 安卓端 head（骨架）

复用 `Episteme.UI.Avalonia` 的 `MobileShell.axaml` / `MainViewModel` / `EpistemeApiClient` / `EpistemeMcpClient`，
本工程只负责平台启动、Activity 生命周期与清单配置。业务逻辑与 Web / 桌面端 100% 共用同一个 REST/MCP 契约。

## 为什么不在默认解决方案里

`src/Episteme.Client.slnx` 只包含 `UI.Avalonia` / `Desktop` / `Client.Probe`。
Android head 的 TFM 是 `net10.0-android`，构建需要 `android` workload 与 Android SDK（数 GB）。
当前构建容器 `mcr.microsoft.com/dotnet/sdk:10.0` 的 `dotnet workload list` 为空，因此**本工程在本轮没有编译验证**，只做源码交付；
把它放进默认解决方案会让整个客户端构建失败。CI 中应作为独立 job。

## 构建步骤（在有 Android SDK 的机器上）

```bash
dotnet workload install android
# ANDROID_HOME 指向 SDK；需安装 API Level 35 与对应 build-tools，以及 JDK 17+
dotnet build src/Episteme.Android/Episteme.Android.csproj -c Release -f net10.0-android
dotnet publish src/Episteme.Android/Episteme.Android.csproj -c Release -f net10.0-android
adb install -r src/Episteme.Android/bin/Release/net10.0-android/publish/cn.episteme.app-Signed.apk
```

只验证 C# 与 XAML 是否可编译，不需要 Android SDK：

```bash
dotnet build src/Episteme.Client.slnx -c Release
```

## 连接后端

`EpistemeApiClient.DefaultBaseUrl` 依次读取 `EPISTEME_API` → `WORKBENCH_API`（兼容旧别名）→ `http://127.0.0.1:5080`。
真机调试必须改成开发机局域网 IP 或公网域名；`AndroidManifest.xml` 已开 `android:usesCleartextTraffic="true"`，**仅限开发**，生产请改 HTTPS 并移除该属性。

## 本轮实现的骨架能力

| 能力 | 状态 |
|---|---|
| 动态目录树 / 空间切换 | 已接线 `GET /spaces/{id}/tree` |
| Schema 驱动表单（由 `/render` 生成控件） | 已接线，文本类字段统一 TextBox |
| 文档列表 / 详情 / 标注审阅 | 已接线 |
| 版本历史 | 已接线 `GET /commits` |
| Pack 预览 / 安装 / 导出 / 导入 | 已接线 |
| 动态 MCP 工具面板与调用 | 已接线 `POST /mcp` |
| 四套浅色主题 | 已接线 |

## 明确边界（本轮未做，不做假验证）

| 能力 | 原因 / 落地方式 |
|---|---|
| Android APK 编译与真机运行 | 容器内无 `android` workload / Android SDK |
| 富 PDF / Web 阅读器 | 按 `docs/05` 结论，移动端优先跳 Web 端或用 WebView 承载；原生只做标注审阅 |
| stdio MCP | 服务端目前只有 Streamable HTTP `/mcp`，`GET /mcp` 已明确标注 stdio 未实现 |
| 离线队列 / 推送 / deeplink | 骨架占位，需本地 SQLite + 平台 SDK |
| 安全区 / 返回键 / 软键盘细节 | 需要在真机上按平台细化 |
