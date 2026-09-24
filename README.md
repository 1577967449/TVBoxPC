# TVBox PC

一个 PC 端 **TVBox** 客户端：界面与功能入口对齐 TVBox，播放 / 下载 / 界面全部是纯原生 **WPF（C# / .NET 8）**，**不使用任何浏览器技术**。

> 播放固定走**内置 LibVLC**（外部播放器支持已移除，见下）。

## 功能

- **浏览 / 搜索**：分类墙、聚合搜索（并发多源、按来源分组、增量渲染）、历史与收藏。
- **源兼容**：苹果 CMS、csp_XPath 套娃源、type3+HTTP 源、drpy(.js) 规则源，以及**专属蜘蛛源（csp_XXX）**——
  这类源的规则是编译好的 dex 包，本程序用「桌面 JVM 蜘蛛桥」直接把它们跑起来，**用户无需安装 Java**。
- **播放**：内置 LibVLC，支持 m3u8 / mp4 等；自动剥离地址里的 `|User-Agent=…&Referer=…` 头参数并注入给播放器（很多防盗链流就是缺 Referer 才 403）。
- **下载**：内置 HLS 下载器（对齐「猫抓 cat-catch」）——新建下载、分片范围 / 时间裁剪、多码率选画质、暂停续传、速度 / 剩余时间 / 分片计数、手填解密密钥、原始 m3u8 留档、导出 ffmpeg / N_m3u8DL-RE / aria2c 命令、EXT-X-BYTERANGE。可选外接 `engine/N_m3u8DL-RE.exe` 与 `ffmpeg.exe`。
- **直播**：TXT(`#genre#`) 与 M3U(`group-title`)。

## 关于「外部播放器」

> **本仓库已移除外部播放器支持**（PotPlayer / VLC 外部 / 自定义路径 三个选项全部去掉）。
> 原因：简化维护、避免外部播放器路径与版本带来的兼容问题，也不在公开仓库里绑定任何第三方闭源播放器。
>
> 如果你要用其它播放器看，请在播放页信息栏点 **「复制地址」**，把地址拿去你自己的播放器打开即可。

## 构建

需要 **.NET 8 SDK（含 Desktop 负载）** 与 **Windows x64**。

```bash
dotnet build -c Release
# 产物在 bin/Release/net8.0-windows/
```

依赖（均通过 NuGet 还原，无需手动下载）：

- `LibVLCSharp` + `VideoLAN.LibVLC.Windows`（内置播放器）
- `AngleSharp`（HTML 解析）
- `Jint`（JS 规则引擎）
- `System.Text.Encoding.CodePages`

## 运行（从源码）

1. `dotnet publish -c Release -r win-x64` 或直接从 `bin/Release/net8.0-windows/` 运行 `TVBoxPC.exe`；
2. 需要本机已装 **.NET 8 Desktop Runtime**（https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0 选 Desktop Runtime / Windows x64）；
3. **蜘蛛源**需要运行时的 JRE 与依赖（约 134MB）。仓库里只保留了这些二进制的**源码与说明**：
   - `jvm/stubs-src/`：160 个 Android / catvod 仿真桩类的 Java 源码
   - `jvm/tools/`：`SpiderRunner.java` + `gen-stubs.py` + `build-stubs.py` + `ecj.jar`
   - `jvm/stubs/stubs.jar`：上面桩类编译后的产物（已随仓库提供）
   - `jvm/jre/`、`jvm/libs/`、`jvm/d2j/`：**不入库**，请从 GitHub Release 的打包 zip 里取，或自行准备后放进 `jvm/` 下对应目录。

> 最省事的方式：直接到 **Releases** 下载打包好的 `TVBoxPC_vX.Y.zip`，解压后双击 `TVBoxPC.exe` 即可，二进制全部齐备。

## 目录结构

```
TVBoxPC/
├── App.xaml / App.xaml.cs          程序入口、参数解析（--play / --dlsim / --playsim）
├── MainWindow.xaml(.cs)            主界面与全部交互逻辑
├── Core/                           核心：设置 / 解析 / 播放 / 下载 / 蜘蛛桥 / 直播
├── jvm/                            蜘蛛运行时（源码 + 说明；二进制走 Release 包）
│   ├── stubs-src/                  仿真桩 Java 源码
│   ├── tools/                      SpiderRunner + 构建脚本
│   └── stubs/stubs.jar             编译好的桩（已提供）
└── TVBoxPC.csproj
```

## 自检小工具（排错用）

在程序目录下打开命令行：

- `TVBoxPC.exe --play "<m3u8地址>"`：播放指定地址并生成 `playcheck.log`（解析出的地址与头、LibVLC 状态、探测结论）。
- `TVBoxPC.exe --dlsim`：走一遍「点选集 → 下载选中」界面流程，写 `dlcheck.log`。
- `TVBoxPC.exe --playsim "关键词"`：连播三集，把状态条 / 播放器状态 / 播放页各区块像素位置写进 `playsim.log`。

把对应日志发出来即可定位「点了没反应 / 秒播完 / 全屏空白」等问题。

## 许可证

主程序 MIT。蜘蛛运行时中用到的 **WinBox** 相关源码（Android 仿真桩与 `SpiderRunner`）按 MIT 附带，署名见 `jvm/tools/WinBox-LICENSE.txt`。
