# 启动性能说明（STARTUP-PERF）

> 适用版本：1.2.8 起。本文说明 MultiDesk 的启动阶段计时日志 `startup-perf.log`、1.2.8 的启动优化内容，以及如何验证优化效果。

## 1. 背景：1.2.7 及更早版本的启动瓶颈

1.2.7 的启动顺序是：

1. 隐藏系统真实桌面图标；
2. 创建并 `Show()` 全屏桌面图层窗口 → `Loaded` 里**同步**渲染当前桌面全部图标；
3. 初始化桌面引擎；
4. 启动看门狗（第二个完整 WPF 进程）；
5. **最后**才创建托盘图标、注册全局热键。

其中第 2 步会逐个调用 Shell（`IShellItemImageFactory`）提取图标。单个图标在冷机/登录风暴期可达数百毫秒（实测最慢 3.6 秒），几十个图标串行执行会占满启动关键路径；此期间托盘图标还不存在。app.log 中「system desktop icons hidden → watchdog started」的间隔就是这段耗时：

| 启动场景 | 1.2.7 实测耗时 |
| --- | --- |
| 冷启动（登录后 30 秒内） | 12.43 s |
| 休眠恢复 + Windows Update 下载中 | 14.89 s |
| 系统空闲（热启动） | 2.02 s |

## 2. 1.2.8 的改动

- 启动顺序：**托盘图标 + 全局热键最先创建**，之后才隐藏真实桌面图标、创建图层、初始化引擎；
- 图标缓存：图标位图持久化到数据目录 `icon-cache-v1\`，续启动直接解码 PNG（毫秒级），不再逐个调用 Shell；
- 图标异步：缓存未命中的图标先显示占位图，由后台 STA 线程提取后回填，不阻塞 UI 线程；
- 看门狗延迟 3 秒启动（避免登录瞬间第二个进程争抢磁盘/CPU）；
- 托盘图标改用 exe 内嵌资源（不再调用 `Icon.ExtractAssociatedIcon` 读取 exe 文件）；
- Windows 图标度量「缓存优先 + 后台刷新」（避免冷机 explorer 忙时跨进程 `SendMessage` 卡住启动）；
- 启动阶段配置写盘合并为一次，启用多核 JIT（`ProfileOptimization`）；
- 首次运行设置窗口延迟 1.2 秒打开（避免首次启动同步加载图标列表拖慢启动）。

## 3. 如何查看启动耗时

每次启动都会在数据目录下追加一组记录到 `startup-perf.log`（默认开启，体积很小）：

```
# startup run=20261028-195414-17724 uptime_s=163554.6 args=--autostart
1ms    process_begin
62ms   tray_created
195ms  hotkeys_registered
505ms  layer_shown
514ms  start_done
3503ms watchdog_spawned
```

- 首行 `uptime_s` 是**系统 uptime**：数值很小（例如 < 300）说明是开机/恢复后不久的冷机启动，数值很大则是系统已运行很久的热启动；
- 每行格式：`距进程启动的毫秒数 <TAB> 阶段名 [<TAB> 详情]`；
- `startup-perf.log` 可以安全删除，下次启动会重新生成。

阶段名对照：

| 阶段 | 含义 |
| --- | --- |
| `process_begin` / `onstartup` | 进程启动 / WPF `OnStartup` 入口 |
| `file_unblocked` | 解除 Mark-of-the-Web（SmartScreen 锁定）完成 |
| `datadir_resolved` / `single_instance_ok` | 数据目录解析 / 单实例锁获取 |
| `controller_ctor_done` | 配置加载、引擎对象创建完成 |
| `tray_created` | **托盘图标创建完成（用户可见）** |
| `hotkeys_registered` | 全局热键注册完成 |
| `desktop_icons_hidden` | 真实桌面图标已隐藏 |
| `layer_created` / `layer_shown` | 桌面图层窗口创建 / 显示完成 |
| `engine_initialized` | 引擎初始化完成 |
| `watchdog_scheduled` | 看门狗延迟启动已排期 |
| `config_saved` | 启动后统一保存配置 |
| `start_done` | 启动流程结束（图标可能仍在后台补齐） |
| `watchdog_spawned` | 看门狗子进程实际启动（约 +3 秒） |
| `icon_shell_slow` | 后台图标提取超过 200 ms 时会额外记录一行（路径 + 耗时） |

## 4. 本机实测（2026-10-28，开发机）

测试方式：使用独立数据目录 + `--manage-only=__none__` 避免影响真实窗口；自动化环境为受限模式（`--no-integrity-elevation`），因此不隐藏真实桌面图标（正常启动会多做一步约 10 ms 的操作），但图标数量、缓存与启动代码路径一致。

**续启动（66 个图标已有磁盘缓存）**：

| 阶段 | 耗时 |
| --- | --- |
| 托盘可见 | **62 ms** |
| 图层显示完成 | 505 ms |
| 启动流程结束 | 514 ms |
| 看门狗启动 | 3503 ms（延迟 3 秒） |

**首次启动（空缓存、从真实桌面导入 66 个图标）**：托盘 136 ms、图层 540 ms；图标在后台逐个提取，最慢一个 207 ms 并记录 `icon_shell_slow`；不会阻塞托盘/图层出现。（首次运行会自动打开设置窗口，该测量早于「设置窗口延迟 1.2 秒打开」的改动，故 `start_done` 偏大，不代表最终版本。）

> 冷启动（重启电脑后第一次登录）受系统登录风暴影响，需要在真实重启后验证；可用 `uptime_s` 确认是否为冷机样本，并对比 `tray_created` / `layer_shown` / `start_done` 三个值。

## 5. 图标缓存说明

- 位置：数据目录下的 `icon-cache-v1\`（绿色版即 `MultiDesk-data\icon-cache-v1\`）；
- 文件名 = SHA1(缓存版本 + 像素尺寸 + 目标路径 + 目标文件 mtime + 大小)；
- 失效规则：目标 exe/lnk/文件夹被替换或修改（mtime/大小变化）自动重新提取；缓存超过 30 天视为过期（兜底 .lnk 指向的程序更新了图标、但快捷方式文件本身未变的情况）；
- 手动重建：右键托盘图标 →「刷新桌面图标」；
- 该目录可安全删除；删除后下次启动会重新建立（图标会在后台补齐）。

## 6. 验证命令

```powershell
# 构建 Debug + 运行全部单元测试（1.2.8 共 134 个用例）
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Tests

# 受控启动（独立数据目录，不影响真实窗口管理；运行结束后查看 startup-perf.log）
$exe = "src\bin\Run\MultiDesk.exe"
$data = "$PWD\obj\perf-data"
.\$exe --data-dir="$data" --manage-only=__none__ --exit-after=10
Get-Content "$data\startup-perf.log" -Tail 30
```

## 7. 已知边界

- 首次启动、删除图标缓存后、或更换图标大小（像素尺寸变化）时，仍需真实调用 Shell 提取图标，但已在后台进行，不阻塞启动；
- .lnk 指向的程序更新图标但快捷方式文件未变化时，最多 30 天后自动刷新，或手动「刷新桌面图标」；
- 受限启动（低完整性）模式下壁纸改为静态图片绘制，启动耗时会略高于正常模式；
- 启动耗时仍受系统登录期服务/杀毒软件扫描影响；`startup-perf.log` 中 `uptime_s` 可用于区分冷/热样本。
