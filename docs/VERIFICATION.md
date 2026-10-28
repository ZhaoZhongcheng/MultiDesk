# MultiDesk 1.0.0 验证记录（VERIFICATION）

- 验证日期：2026-10-25
- 验证人：ve verifier（测试与交付验证）
- 结论：**通过（PASS）**。单元测试 91/91、引擎自测 `--selftest` 28/28、`--ui-only` 端到端验证与安全（金丝雀）检查、退出残留检查、发布包独立运行验证全部通过。

---

## 1. 验证环境

| 项目 | 值 |
| --- | --- |
| 操作系统 | Microsoft Windows 11 家庭版 中文版，版本 10.0.26200，Build 26200（x64） |
| 硬件 | 82WK 笔记本，内存 15.8 GB |
| 显示 | 2560 × 1600，150% 缩放（截图 2560×1600，DPI-aware） |
| 运行时 | .NET Framework 4.8（CLR 4.0.30319.42000） |
| 构建工具 | 系统自带 MSBuild 4.8.9221.0（`%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe`，未安装 .NET SDK / Visual Studio） |
| PowerShell | Windows PowerShell 5.1.26100.9711 |
| 网络 | 未使用网络；无任何第三方 NuGet 包 |
| 被测产物 | `src\bin\Release\MultiDesk.exe`（同时复制 `src\bin\Run\MultiDesk.exe`），以及 `dist\MultiDesk\MultiDesk.exe` |

---

## 2. 验证命令一览

```powershell
# 1) Release 构建 + 单元测试（会生成 src\bin\Run 运行副本）
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Release -Tests

# 2) 引擎自动化自测（只管理自测进程窗口，安全）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-selftest.ps1 -ExePath "src\bin\Release\MultiDesk.exe"

# 3) --ui-only 端到端（桌面切换截图 + 设置窗口 + 退出残留检查）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-app.ps1 -ExePath "src\bin\Release\MultiDesk.exe" -Fresh -SwitchMode auto

# 4) 发布打包（Release 构建 + dist + 独立启动冒烟 + zip）
powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1

# 5) 发布包独立自测（可选，强验证）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-selftest.ps1 -ExePath "dist\MultiDesk\MultiDesk.exe" -DataDir "obj\dist-selftest"
```

---

## 3. 构建与单元测试

- 构建：`build.ps1 -Release -Tests` → src Release 构建成功，测试项目构建成功，`[test] OK`，退出码 0。
- 单元测试（Debug 与 Release 两种配置各跑一次，结果一致）：

```
Total: 91  Passed: 91  Failed: 0
RESULT: PASSED
```

测试项目：`tests\TestRunner.csproj`（老式 .NET Framework 4.8 控制台项目，直接编译 `src\Core\**\*.cs` 核心源码，排除依赖 UI/Native 的 `AppController.cs`、`SelfTest.cs`）。测试运行器为自写最小实现（`[Test]` 特性 + 反射发现 + Assert 断言 + PASS/FAIL 输出 + Total/Passed/Failed 汇总 + 失败退出码 1），**不使用任何第三方测试框架**，不联网。

| 测试文件 | 用例数 | 覆盖内容 |
| --- | --- | --- |
| `tests\TestsAppConfig.cs` | 13 | AppConfig.Sanitize：空桌面列表补 1 个、null 桌面、重复/空 id、ActiveDesktopId 修复、Options 重建、版本修复、字段/图标空值修复、幂等性 |
| `tests\TestsConfigStore.cs` | 8 | 保存-加载往返（中文桌面名/图标名/壁纸路径/坐标）、缺失文件回退默认且不写盘、损坏 JSON 回退默认、备份恢复、Save(null)、嵌套目录创建、覆盖保存 |
| `tests\TestsIconLayout.cs` | 16 | ColumnsFor/RowsFor 边界、CellToPoint/PointToCell 夹取、ArrangeAll 列优先、IsOccupied、PlaceIcon 碰撞绕开/网格满溢出、FindNearestFreeCell、NextFreeCell、Normalize（NaN/重复/越界/off-grid/多显示器偏移 area） |
| `tests\TestsDesktopEngine.cs` | 26 | 初始化接管已有可见窗口、新窗口归属当前桌面、切换 hide/show、owner 继承、同进程继承、外部重新显示后再隐藏、暂停模式全显示/恢复再隔离、RestoreAllWindows/Shutdown/ForgetWindow、忽略自身进程/壳窗口/cloaked/无尺寸/WindowFilter、窗口销毁清理、事件、会话日志写入/清空、JournalEnabled、RecoverFromPreviousSession（pid 校验、过期 boot 不恢复） |
| `tests\TestsSessionJournal.cs` | 8 | SessionJournal 默认值、CurrentBootMarker、IsFromCurrentBoot 容差、JournalRestorer（pid 校验/死句柄/null 安全/无日志回调）、JSON 往返 |
| `tests\TestsModels.cs` | 6 | IconEntry/DesktopProfile/AppOptions 默认值与 Clone、CreateDefault、FindDesktop/IndexOfDesktop、GetActiveDesktop 修复 |
| `tests\TestsMisc.cs` | 14 | Launcher.CreateFromPath 显示名规则/空路径、Launch/OpenFileLocation 参数校验、JsonIo 空串/往返/非法 JSON、DesktopImporter 路径与 null 桌面、ScreenLayout 默认/Capture/EqualsTo |

> 环境说明：本验证会话的沙箱禁止子进程写系统 `%TEMP%`，单元测试已实现回退（优先系统临时目录，失败则写 `tests\bin\<Configuration>\test-temp`），保证测试在受限环境/CI 下可稳定运行。

---

## 4. 引擎自动化自测（`--selftest`）

- 命令：`scripts\run-selftest.ps1 -ExePath "src\bin\Release\MultiDesk.exe"`（数据目录 `obj\selftest-data`）
- 结果：`exit code: 0`，`Total=28 Passed=28 Failed=0`，`SELFTEST RESULT: PASSED`
- 自测内容（摘要）：两个测试窗口被引擎接管 → 切换桌面窗口隐藏/进程存活 → 切回恢复 → 单窗口移动到另一桌面 → 暂停/恢复 → 会话日志记录 → 日志恢复器恢复 → 引擎重新隔离 → 恢复全部 → 退出恢复 → 进程存活。
- 安全校验（由 `run-selftest.ps1` 额外执行）：
  - 启动一个**真实 notepad 金丝雀窗口**：自测全程未被隐藏/移动（`safety canary window is still visible`）；
  - 自测退出后无 `--test-window` 子进程残留；
  - `session.json` 已被清理（不存在 / 无已隐藏窗口记录）。
- 发布包产物同样通过：`dist\MultiDesk\MultiDesk.exe --selftest` → `Total=28 Passed=28 Failed=0`，退出码 0，金丝雀窗口未被触碰。

---

## 5. `--ui-only` 端到端验证

- 命令：`scripts\run-app.ps1 -ExePath "src\bin\Release\MultiDesk.exe" -Fresh -SwitchMode auto`
- 数据目录：`obj\e2e-data`；测试配置（`scripts\make-test-config.ps1` 生成）：
  - 桌面 1 `Work`：纯色壁纸 `#FF17324F`（深蓝），图标 notepad、explorer；
  - 桌面 2 `Game`：纯色壁纸 `#FF4F1732`（紫红），图标 charmap、control、taskmgr；
  - 两桌面图标集合与壁纸颜色明显不同，便于像素对比。
- 桌面切换方式：`--auto-switch=7:2,20:1`（开发参数，不依赖全局热键，避免与其它实例的热键冲突）。
- 逐项结果（全部 PASS）：

| 检查项 | 结果 |
| --- | --- |
| 桌面图层窗口存在（按 PID 枚举可见窗口） | PASS |
| `--ui-only` 未隐藏真实窗口（notepad 金丝雀全程可见） | PASS |
| 桌面 1 图层截图（PrintWindow） | PASS → `screenshots\01-desktop-work.png` |
| 切换到桌面 2 后截图（OSD + 目标桌面） | PASS → `screenshots\02a-osd-desktop-switch.png`、`02-desktop-game.png` |
| 桌面 1 与桌面 2 像素对比 | **64000/64000 采样点不同（100%）** → 内容明显不同 |
| OSD 是否出现 | PASS（切换后 350ms 截图与原桌面 100% 不同） |
| 切回桌面 1 后截图 | PASS → `screenshots\05-desktop-work-back.png` |
| 切回后与初始桌面 1 像素对比 | 0/64000 不同（完全一致）→ 切换可逆 |
| 设置窗口（第二实例激活单实例 → 已运行实例打开设置） | PASS → `screenshots\03-settings.png` |
| 停止后无残留 MultiDesk 进程（按 data-dir 过滤） | PASS |
| 停止后该 PID 无残留可见窗口 | PASS |

- 退出/恢复验证：
  - `--selftest` 场景：自测内部断言 `all windows visible after engine shutdown` PASS，且脚本确认退出后无测试窗口残留、会话日志已清理；
  - `--ui-only` 场景：本模式不管理任何真实窗口，停止后无进程/窗口残留；真实窗口由金丝雀检查确认全程未受影响。
  - 真实窗口的隐藏/恢复由 `--selftest` 引擎级断言与 Lead 在集成阶段的真实场景测试共同覆盖。
- 全部实例均按 **PID 精确关闭**（先 `Start-Process -PassThru` 记录 PID，或按 CommandLine 中自己的 `--data-dir` 过滤），未使用按进程名杀进程。

---

## 6. 截图清单

`screenshots\` 目录保留编号清晰的一套关键截图（其余中间过程截图归档在 `screenshots\archive\`）：

| 文件 | 内容 | 来源 |
| --- | --- | --- |
| `01-desktop-work.png` | 桌面 1「Work」：深蓝壁纸 + notepad/explorer 图标 + 右下角「1 · Work」徽章 | 本次 E2E（真实 `--ui-only` 实例，PrintWindow 截取图层） |
| `02-desktop-game.png` | 桌面 2「Game」：紫红壁纸 + 3 个不同图标 + 「2 · Game」徽章 | 本次 E2E（自动切换后截取） |
| `02a-osd-desktop-switch.png` | 切换桌面时的 OSD 提示「Game」 | 本次 E2E（切换后 350ms 截取） |
| `03-settings.png` | 设置窗口「桌面设置」页（桌面列表、名称、壁纸、图标列表） | 本次 E2E（第二实例激活已有实例后截取） |
| `04-context-menu.png` | 桌面空白处右键菜单（整理图标 / 切换桌面 / 桌面列表 / 打开设置 / 恢复所有窗口） | layer-ui 集成阶段离屏 harness（渲染真实图层窗口） |
| `04b-icon-menu.png` | 图标右键菜单（打开 / 打开文件所在位置 / 从本桌面移除 / 取消） | layer-ui 离屏 harness |
| `05-desktop-work-back.png` | 切回桌面 1 后的画面（与 `01` 一致） | 本次 E2E |
| `06-import-dropped.png` | 从资源管理器拖入文件时的导入提示 | layer-ui 离屏 harness |
| `07-settings-general.png` | 设置窗口「常规设置」页（开机自启、暂停管理、恢复窗口、数据目录等） | settings-ui 集成阶段截图 |
| `08-settings-help.png` | 设置窗口「帮助与说明」页 | settings-ui 集成阶段截图 |
| `09-settings-solid-color.png` | 壁纸「纯色」配置页 | settings-ui 集成阶段截图 |

### 截图方法说明（重要）

1. **桌面图层窗口始终置底且不抢焦点**，当其它应用窗口覆盖屏幕时，普通全屏截图拍不到图层内容。因此本验证使用 `scripts\capture-window.ps1` 调用 Win32 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` 直接渲染图层窗口（DPI-aware，2560×1600），得到干净的桌面图层画面。
2. **WPF 只处理真实鼠标输入**：实测向图层窗口发送 `SendMessage`/`PostMessage` 的 `WM_MOUSEMOVE`/`WM_RBUTTONDOWN`/`WM_RBUTTONUP`（含 `WM_MOUSEACTIVATE`、`WM_ACTIVATE` 变体）均无法触发 WPF 的 `MouseRightButtonDown`（左键选中同理无效）。而共享会话中不允许执行全局最小化/还原窗口操作（会影响用户桌面），因此右键菜单、拖放导入等**交互视觉素材**采用 layer-ui 在集成阶段制作的离屏 harness 截图（该 harness 渲染真实 `DesktopLayerWindow` 并在其内部注入输入，截图即真实图层渲染结果）。
3. **真实鼠标交互**（单击选中 / 右键两种菜单 / 拖动吸附 / 双击启动）已由 Lead 在集成阶段用真实鼠标完成验收，相关截图归档于 `screenshots\archive\`（`real-*.png`、`interact-*.png`）。

---

## 7. 发布包验证（publish.ps1 + dist）

- 命令：`powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1`（内部执行 `build.ps1 -Release`，然后收集产物、独立启动冒烟、生成 zip）。
- `dist\MultiDesk\` 内容（**不含任何框架 DLL / NLP / PDB / XML**，脚本会校验）：

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `MultiDesk.exe` | 189,440 字节（约 185 KB） | 单文件 WPF 程序 |
| `app.ico` | 26,076 字节 | 应用图标（可选，便于创建快捷方式） |
| `使用说明.txt` | 1,410 字节 | 简短使用说明（UTF-8） |

- 可执行文件信息：`FileVersion = 1.0.0.0`、`ProductVersion = 1.0.0.0`、`ProductName = MultiDesk`。
- SHA256：`3860C03ECC3696F82BF11A588CC67CAB31970D254239A65AF65AA5F6C281280B`
- 独立运行冒烟：在 `dist\MultiDesk\` 工作目录下启动
  `dist\MultiDesk\MultiDesk.exe --ui-only --data-dir=<临时目录> --exit-after=6` → **退出码 0**，`app.log` 有正常启动记录（说明该 exe 可脱离源码目录独立运行）。
- 发布包二次自测：`dist\MultiDesk\MultiDesk.exe --selftest` → `Total=28 Passed=28 Failed=0`，退出码 0。
- 压缩包：`dist\MultiDesk-1.0.0.zip`（122,649 字节，内部为 `MultiDesk\` 目录：exe + ico + 使用说明.txt）。

---

## 8. 独立代码审查发现

审查范围：`src\Core`（DesktopEngine / AppController / Models / ConfigStore / IconLayout / IWindowSystem / SelfTest 等）、`src\Native`（Win32WindowSystem / NativeMethods / WindowsJournal / Watchdog）、`src\UI`（DesktopLayerWindow 及其输入/菜单/图标、SettingsWindow、TrayIcon、HotkeyManager、WallpaperService、IconService）。

### 已在集成阶段修复的问题（上一轮审查提出）

- `SelfTest` 中「moved window hidden immediately」改为 `WaitFor` 轮询，消除 `ShowWindowAsync` 竞态导致的偶发失败；
- `SelfTest.SaveLog` 调整到汇总行之后，`selftest.log` 现在包含 `Total=/Passed=/Failed=`；
- `AppConfig.Sanitize` 对每个桌面的 Icon Id 去重，且 DisplayName 不会为 null；
- `DesktopEngine.DesktopSwitched` 注释与实际行为一致。

### 剩余问题 / 风险（按严重度）

| 严重度 | 位置 | 问题 | 建议 |
| --- | --- | --- | --- |
| 中 | `src\Core\DesktopEngine.cs` `Tick()` → `WriteJournal()`（约 :221、:553-589） | 引擎每 400ms Tick 一次，只要存在被隐藏窗口就**无条件重写** `session.json`（临时文件+删除+改名），用户停留在非首桌面期间会持续产生磁盘写入 | 缓存上次写入内容/时间戳，仅内容变化时写；或降低写日志频率（如 2~5s 或状态变化时） |
| 中 | `src\Core\AppController.cs` 构造函数（约 :116-126） | 每次启动都按当前配置同步 `HKCU\...\Run` 开机自启项；使用 `--ui-only`/`--manage-only` + 临时 data-dir 启动时会按测试配置**启用/禁用用户真实的开机自启项** | 仅在普通模式且 data-dir 为默认目录时同步注册表；或增加开关 |
| 低 | `src\Core\AppController.cs` `--manage-only` 过滤器（约 :96-109） | pid→进程名缓存无淘汰，Windows 复用 pid 时可能误判进程名 | 定期清理缓存或直接每次查询 |
| 低 | `src\Core\AppController.cs` `Log()`（约 :635-649） | `app.log` 无轮转，长期使用持续增长 | 超过阈值时轮转/截断 |
| 低 | `src\UI\IconService.cs`（约 :50-110） | `IShellItemImageFactory` COM 对象未 `Marshal.ReleaseComObject`（依赖 GC）；图标缓存无上限 | 获取后释放 COM；限制缓存条目数 |
| 低 | `src\Core\AppController.cs`（`_layout`，约 :59）与 `ScreenLayout` | 启动时只捕获一次屏幕布局；运行中改分辨率时图层窗口会自行处理，但 AppController 中用于导入/整理的布局可能过期 | 使用点附近即时 `ScreenLayout.Capture()` |
| 低 | `src\Core\DesktopEngine.cs` `ForgetWindow()`（约 :415-428） | 移出管理后，若窗口仍可见，下一次 Tick 会因 `_lastDesktopByProcess` 再次接管（当前为预留方法，未用于永久忽略） | 文档注明，或增加忽略集合 |
| 信息 | `src\UI\HotkeyManager.cs` | 全局热键进程级独占：多实例同时运行时只有第一个实例能注册全部热键 | README 已说明 |
| 信息 | `src\UI\DesktopLayerWindow*` | WPF 不处理合成鼠标消息，影响 UI 输入自动化（不影响真实用户） | 验证文档已说明 |

### 安全/健壮性确认（正面结论）

- 所有 Win32 调用、事件回调、文件读写、配置解析均有 try/catch 保护，异常不会导致崩溃（崩溃日志 + 看门狗兜底）；
- 正常退出/暂停/恢复全部窗口/`Ctrl+Alt+Shift+R` 都会恢复被隐藏窗口；
- 崩溃场景由看门狗根据会话日志（含开机标记与 pid 校验）恢复，重启后不会误恢复旧句柄；
- 组件 Dispose/定时器停止/事件退订路径完整（图层窗口关闭、托盘释放、热键注销、看门狗结束）；
- `--selftest` 有严格的窗口过滤器（只管理自测进程 pid），已用金丝雀窗口验证不触碰用户窗口。

---

## 9. 限制与遗留问题

1. **未签名 exe**：`MultiDesk.exe` 未做代码签名，首次运行/双击启动外部程序时可能触发 Windows SmartScreen「未知发布者」或「打开文件-安全警告」系统提示（属系统策略行为，不影响程序功能；详见 README）。
2. **管理员权限窗口**：普通权限运行 MultiDesk 时无法隐藏/恢复以管理员权限运行的窗口（UIPI 隔离）；需以管理员身份运行 MultiDesk 才能管理此类窗口。
3. **独占全屏游戏**：部分 DirectX 独占全屏游戏在切换桌面时可能闪烁或无法正确隐藏，建议使用无边框窗口模式。
4. **真实桌面图标被图层覆盖**：桌面图层为全屏不透明窗口且始终位于系统桌面之上，运行期间真实桌面图标被覆盖属预期行为（首次运行会把真实桌面图标导入配置）；退出程序后恢复。
5. **不支持动态壁纸**：壁纸支持系统壁纸/静态图片/纯色；不支持 Wallpaper Engine 等动态壁纸引擎。
6. **开机自启可能被策略禁用**：部分企业策略/安全软件禁止写入 `HKCU\...\Run`，此时设置开机自启会失败（程序会提示，不影响其它功能）。
7. **多显示器**：本机为单显示器 2560×1600 @150% 环境验证；代码按虚拟屏幕+主显示器工作区设计，但未在物理多显示器上实测。
8. **多实例热键冲突**：全局热键仅第一个实例可注册；多实例运行时请用托盘/设置切换（README 已说明）。
9. **验证环境限制**：本会话沙箱禁止子进程写系统 `%TEMP%`（单元测试已做回退）；共享会话不允许执行全局最小化/还原窗口操作，因此右键菜单视觉素材采用离屏 harness 截图（见第 6 节说明）。
10. **运行中分辨率变化**：图层窗口会处理 `WM_DISPLAYCHANGE` 并重排图标，但建议变更分辨率/缩放后重启程序。

---

## 10. 复现步骤（从零开始）

```powershell
cd MultiDesk

# 1) Release 构建 + 单元测试
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Release -Tests
#    期望：Release 构建成功；Total: 91  Passed: 91  Failed: 0；[test] OK；退出码 0

# 2) 引擎自测（安全，只管理测试窗口）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-selftest.ps1 -ExePath "src\bin\Release\MultiDesk.exe"
#    期望：SELFTEST RESULT: PASSED；28/28；金丝雀窗口未被触碰；退出码 0

# 3) --ui-only 端到端（截图 + 设置窗口 + 残留检查）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-app.ps1 -ExePath "src\bin\Release\MultiDesk.exe" -Fresh -SwitchMode auto
#    期望：E2E RESULT: PASSED；screenshots\01/02/02a/03/05 更新；无残留进程/窗口

# 4) 发布打包 + 独立运行冒烟 + zip
powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1
#    期望：dist\MultiDesk\{MultiDesk.exe, app.ico, 使用说明.txt}；smoke run OK；dist\MultiDesk-1.0.0.zip

# 5) 发布包独立自测
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-selftest.ps1 -ExePath "dist\MultiDesk\MultiDesk.exe" -DataDir "obj\dist-selftest"
#    期望：28/28 PASS；退出码 0
```

---

## 11. 结论

MultiDesk 1.0.0 的核心引擎、配置/布局逻辑、桌面图层与设置窗口、托盘/热键、崩溃恢复与发布打包均通过本轮验证：

- 单元测试 **91/91**（Debug 与 Release 一致）；
- 引擎自测 **28/28**（源码产物与 dist 产物各一次，均退出码 0）；
- `--ui-only` 端到端：两个桌面图标/壁纸明显不同、切换可逆、OSD 与设置窗口正常、退出无残留、金丝雀窗口未被触碰；
- `dist\MultiDesk\MultiDesk.exe` 可独立运行（`--ui-only` 冒烟与 `--selftest` 均通过），无框架 DLL 依赖；
- 代码审查未发现致命问题，剩余为中/低优先级优化项（见第 8 节）。

**验证结论：通过，可交付。**


---

## 11. 1.1.0 更新验证（2026-10-26）

> 本轮为按用户反馈进行的 1.1.0 功能更新：实时壁纸、原生桌面右键菜单、图标大小/排列、双击启动窗口归属、受限启动回退。

### 11.1 构建与自动化测试

- `build.ps1 -Release -Tests`：Release 构建 OK；单元测试 **115/115 PASS**（新增 AppOptions 新字段/配置 v2 迁移、IconLayout IconMetrics 32/48/64 重载、DesktopEngine launch hint 等用例）。
- `--selftest`（Release 产物）：**28/28 PASS**，exit code 0。
- 本机环境：Windows 11 26200、2560x1600@150%、单显示器；.NET Framework 4.8（系统自带）。

### 11.2 桌面/窗口权限事实（重要）

- 从本机 bash/agent 启动的 MultiDesk 进程为 **Low 完整性**；PowerShell/Explorer 为 Medium。
- Low IL 下 Windows UIPI 禁止操作资源管理器窗口：`ShowWindowAsync` 隐藏系统桌面图标返回 False、`PostMessage(WM_CONTEXTMENU)` 转发被拦。
- 因此程序增加了「受限启动回退」，并在真实用户从资源管理器双击（Medium）时走完整功能路径。

### 11.3 完整功能路径（Medium 进程内托管验证）

将 MultiDesk 装配件加载到 Medium 的 PowerShell 进程内（反射创建 `AppController` 并 Start），验证：

- `app.log`：`[desktop-icons] hide desktop icon view hwnd=0x10160 asyncOk=True visibleNow=False`；
- 运行期间 `SysListView32` `IsWindowVisible=False`；退出后恢复为 True；marker 文件无残留；
- 调用 `ShowDesktopContextMenu(1280,800)` 返回 True，整屏截图出现**真正的 Windows 11 桌面右键菜单**（见 `screenshots/10-real-desktop-menu.png`，含查看/排序方式/新建/显示设置/个性化/右键菜单管理/游戏中心/在终端中打开/显示更多选项）。

### 11.4 受限启动回退（Low IL，agent 环境天然可测）

- `--ui-only` 启动后日志：`running at LOW integrity: system desktop icon hiding and native desktop menu are unavailable (restricted launch)`；
- System 壁纸模式回退为**不透明系统壁纸**（注册表 WallPaper），不再透出真实桌面图标：`screenshots/10-fallback-low.png`；
- 空白右键回退菜单项完整（刷新/整理图标/切换上下桌面/桌面列表/打开设置/恢复所有窗口/取消）：`screenshots/layer-110-fallback-menu.png`；
- Image/Solid 壁纸回归正常：`screenshots/layer-110-fallback-img48.png`、`layer-110-fallback-solid.png`。

### 11.5 图标大小/排列与双击启动归属

- 图标 32/48/64px 渲染与 80x88 / 96x104 / 112x120 单元格：`screenshots/layer-110-img32.png`、`layer-110-img48.png`、`layer-110-img64.png`；
- 自由摆放 vs 自动排列：`layer-110-free.png`（保留错开坐标、不吸附）vs `layer-110-auto.png`（吸附 24/16 网格并回写配置）；
- 双击启动窗口归属（进程内 Low 托管 + `--manage-only` 测试窗口）：
  - 桌面 1 的测试窗口 P1 切到桌面 2 后被隐藏；
  - 在桌面 2 调 `LaunchIcon` 启动同进程名的新窗口 P2：**P2 在桌面 2 可见**（旧行为会被同进程继承到桌面 1 而隐藏）；
  - 切回桌面 1：P1 恢复可见、P2 隐藏；日志 `launch icon ... -> pid ...` 与 `launch hint` 正常。

### 11.6 已知限制（1.1.0）

1. 受限启动（Low IL）下无法隐藏系统图标/转发原生菜单，自动回退；从资源管理器双击启动才完整。
2. 动态壁纸透出依赖透明窗口 + 隐藏系统桌面图标；若系统策略禁止（或受限启动），回退为静态系统壁纸图片。
3. 未签名 exe 的 SmartScreen 提示、开机自启可能被组策略禁止、管理员权限程序窗口、独占全屏游戏闪烁、多显示器未实测等限制仍然存在（见 README 第 9 节）。


---

## 12. 1.1.1 更新（2026-10-26，用户反馈）

用户反馈：从聊天窗口启动时「实时壁纸 / 原生右键菜单 / 双击新建桌面图标打开程序」仍不生效，并出现 SmartScreen 拦截。经查看用户机器上的 `dist\MultiDesk\MultiDesk-data\app.log` 确认根因：

- 日志明确出现 `running at LOW integrity: system desktop icon hiding and native desktop menu are unavailable (restricted launch)`，即用户是从聊天软件/沙箱启动（Low 完整性），Windows UIPI 限制导致 3 项功能不可用；同日志还有 `launch icon: 新建 文本文档 failed: 拒绝访问`（受限模式下 ShellExecute 被拒）。
- SmartScreen：程序未签名，Windows 对未签名/带保留下载标记的程序显示「Windows 已保护你的电脑」。

1.1.1 修复：

1. `AppRelauncher` + 启动检测：普通模式检测到 Low 完整性时弹窗提示「是否以完整权限重新启动」，确认 UAC 后以完整权限重启（启动参数保留）；托盘菜单在受限模式下新增「以完整权限重启（推荐）」。
2. 发布包新增 `启动多桌面助手.bat`（先 `Unblock-File` 再启动，规避 SmartScreen）与 `多桌面助手.lnk`；publish.ps1 自动解除 exe 文件锁定并打印使用提示。
3. `Launcher` 在 ShellExecute 失败时回退为通过资源管理器打开文件（受限模式下的容错）。
4. 单实例互斥增加最长 5 秒重试，避免提权重启时新旧实例竞争。
5. 版本更新为 1.1.1；README/使用说明补充 SmartScreen、受限启动与快捷方式说明。

验证：Release 构建通过、单测 115/115 PASS、`--selftest` 28/28 PASS；`--no-integrity-elevation` 可抑制受限启动弹窗用于自动化测试；Medium 进程内托管仍验证图标隐藏/恢复与原生桌面菜单正常。


---

## 13. 1.2.0 更新（2026-10-26，用户第二次反馈）

用户反馈 6 项：①图标格式/大小/间距与原桌面不同；②双击快捷方式仍弹 SmartScreen / 受限提示；③程序主界面在前台时无法使用其它软件的截图快捷键；④设置窗口"切换桌面"按钮有时不生效；⑤托盘"下一桌面"卡顿且图标闪烁；⑥原生右键菜单的查看/排列作用在真实桌面而非当前虚拟桌面。

根因与修复：

1. **图标外观**：旧实现固定 96x104 DIP 栅格（比 Windows 真实桌面更宽、边距更大）。新增 `Native/WindowsDesktopMetrics`：读取 `HKCU\...\Bags\1\Desktop\IconSize`，并用跨进程内存读取真实桌面 SysListView32 的图标位置，推导步距/原点（本机实测：icon=48、pitch≈76.7x98 DIP、origin≈(0,3.3)）；`AppOptions.MatchWindowsIconMetrics`（v3 默认 true，旧配置自动迁移），图层按该度量渲染，字体改 Segoe UI/12 DIP。
2. **SmartScreen/受限提示**：发布包 `多桌面助手.lnk` 改为 TargetPath=powershell.exe，Arguments 用 `& '<exe>'`（CreateProcess 启动，不经 ShellExecute/SmartScreen，普通权限），`启动多桌面助手.bat` 同理先 Unblock 再 `&` 启动。新增 `AppOptions.SkipRestrictedPrompt`：受限启动提示选择"否"后写入配置，以后静默回退；设置窗口可重新开启。
3. **截图快捷键失效**：根因是之前为恢复功能而 UAC 提权，提权窗口在前台时 Windows UIPI 屏蔽非提权截图工具的热键。1.2.0 快捷方式/bat 以普通权限启动，不再提权；README FAQ Q11 说明。
4. **设置切换按钮**：`Host_StateChanged`/`RefreshSettings` 刷新时保留用户选中项并重新计算按钮文案/可用状态，修复来自托盘/快捷键切换后按钮状态不同步的问题。
5. **切换闪烁**：`IDesktopSurface.PrepareDesktop(desktopId)` 在隐藏旧窗口前把图层（壁纸/图标/徽章）切到目标桌面并强制渲染（UpdateLayout + Dispatcher Render），再执行引擎切换；托盘/快捷键/设置按钮统一走该路径。
6. **右键菜单只作用虚拟桌面**：空白右键改为程序自绘菜单（查看/排序/刷新/新建/桌面列表/切换/打开设置/恢复窗口），查看=图标大小/自动排列/显示名称，新建在 `数据目录\DesktopItems\` 创建文件并只加图标到当前桌面；末尾「更多选项…」仍转发 Windows 原生菜单。移除"原生菜单后自动导入桌面文件"逻辑（之前会一次导入 63 个真实桌面图标）。

验证：Release 构建通过；单测 115+（已更新配置 v3 迁移/新选项）PASS；`--selftest` 28/28；`--ui-only` 图标与真实桌面对比截图；自绘菜单功能 E2E（改大小/新建不污染真实桌面/不自动导入）；切换桌面多帧截图；设置窗口按钮/新选项；lnk/bat 普通权限启动验证；publish 生成 1.2.0 dist+zip 并冒烟。

### 13.1 1.2.0 端到端实测记录（verifier，2026-10-26 19:34）

环境：宿主 PowerShell 进程内 Medium 完整性托管（本环境从 bash/聊天窗口启动的 exe 均为 Low，无法操作 explorer 桌面窗口，也不接受合成鼠标消息，故采用 Medium 进程内反射/直调图层方法验证；真实鼠标交互由 Lead 侧已验收）。被测产物：`src\bin\Release\MultiDesk.exe`（1.2.0）。

脚本：`scripts\run-medium-app.ps1`（Medium 进程内托管；数据目录 `obj\medium-120`，全部测试使用隔离 `--data-dir`，未触碰 `dist\MultiDesk\MultiDesk-data` 用户数据）。关键日志（`obj\medium-120\medium-run-state.log`）：

- 完整性/集成：`lowIntegrity=False`、`IsDesktopIntegrationAvailable = True`；运行中 `desktop icons visible DURING = False`、`marker exists DURING = True`；正常退出后 marker 已清理、`iconsVisible=True`（脚本外独立复查），无残留进程。
- 图标度量（与真实桌面一致）：`[icon-metrics] follow Windows desktop: icon=48 cell=76.7x98.0 origin=0.0,3.3`；控制器 `GetIconMetrics()` = `icon=48 cell=76.67x98.00 origin=0.00,3.33`；图层 Canvas 前两个图标坐标 `(0.00, 3.33)`、`(0.00, 101.33)`（步距 98 DIP，与 Windows 桌面 LVM 位置换算一致）。截图：`screenshots/12-real-desktop-icons.png`（真实桌面 PrintWindow）、`screenshots/12-compare-with-windows.png`（真实桌面 vs 图层并排，几乎一致）、`screenshots/12-icons-windows-metrics.png`。
- 自绘桌面菜单：反射调用 `ShowDesktopFunctionMenu` 后 `menu IsOpen = True`；根菜单与子菜单截图为 layer-ui 离屏 harness 对真实图层窗口的渲染结果（本环境 Medium 进程内托管时 `IsOpen=True`，但 `PrintWindow`/WPF `RenderTargetBitmap` 无法把该菜单覆盖层写入位图，故采用 harness 截图）：`screenshots/12-desktop-menu.png`、`12-desktop-menu-view.png`（查看）、`12-desktop-menu-sort.png`（排序方式）、`12-desktop-menu-new.png`（新建）。
- 查看/排列等只作用虚拟桌面：关闭「跟随 Windows 度量」后菜单改 64 → `metrics.icon=64 config.iconSize=64 cell=112x120`，改 32 → `metrics.icon=32 config.iconSize=32 cell=80x88`（截图 `12-icon-size-64.png`/`12-icon-size-32.png`）；自动排列/显示桌面名称开关落盘（`autoArrange=False showDesktopName=False`，`12-menu-toggles.png`）；按名称排序后顺序 `notepad,explorer → explorer,notepad`（`12-sorted-by-name.png`）。
- 新建只进虚拟桌面：新建文件夹后 `iconsBefore=2 → iconsAfter=3`，**真实桌面目录条目数 52 → 52 未变**，新项目创建在 `obj\medium-120\DesktopItems\新建文件夹`（隔离数据目录），截图 `12-new-folder.png`、`12-new-folder-harness.png`；配置为预置 2/3 个图标，**未自动导入真实桌面 52 个图标**（旧逻辑会导入 63 个）。
- 更多选项→原生菜单：菜单项 `ShowNativeDesktopMenuFromItem` 后 `ShowDesktopContextMenu` 返回 `True`，全屏截图 `12-more-options-native.png` 可见 Windows 原生桌面菜单。
- 切换桌面防闪烁：`PrepareDesktop` 预渲染后连续抓取 8 帧图层画面，`matchedAB=8 blank=0 other=0`（无空白/黑屏中间帧），`RequestSwitchIndex` 调用耗时 50.8ms；切换前后截图 `12-switch-desktop1.png`/`12-switch-desktop2.png`、`12-prepare-desktop-game.png`。说明：1.1.x 构建已被 1.2.0 覆盖、无归档 exe，故多帧对比只对 1.2.0 记录“无空白帧”，未做 1.1.x/1.2.0 逐帧 A/B 录屏。
- 设置窗口：外部切换（等价的 `RequestSwitchIndex`，热键/托盘同一入口）后按钮状态正确：选中桌面2 → `切换到该桌面`/enabled=True；切到桌面2后 → `当前桌面`/enabled=False；改选桌面1 → 恢复 `切换到该桌面`/enabled=True。两个新选项落盘：`MatchWindowsIconsCheck` 勾选 → `matchWindowsIconMetrics=True`；`RestrictedPromptCheck` 取消勾选 → `skipRestrictedPrompt=True`，重新勾选 → `False`（语义：勾选=启动时提示受限模式）；`AutoArrangeCheck` 取消 → `autoArrangeIcons=False`。截图 `12-settings-options.png`（1.2.0 设置窗口，含图标外观/跟随 Windows/自动排列/受限提示）。
- 单测：`build.ps1 -Release -Tests` → **126/126 PASS**（含新增 `tests/TestsCore120.cs` 11 用例：CurrentVersion=3、v2→v3 迁移（MatchWindowsIconMetrics=true）、v1→v3、v3 保留显式值、ConfigStore v3 往返、磁盘 v2 JSON 迁移、WindowsDesktopMetrics 冒烟、DesktopShell 可见性查询）。
- dist 1.2.0（Lead 已 publish）：`dist\MultiDesk\` = MultiDesk.exe(227,328B) + app.ico + 使用说明.txt + `多桌面助手.lnk` + `启动多桌面助手.bat`；`MultiDesk-data\` 用户配置保留。`多桌面助手.lnk` 实测：`TargetPath=C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`、`Arguments=-NoProfile -WindowStyle Hidden -Command "& '<dist>\MultiDesk.exe'"`、`IconLocation=MultiDesk.exe,0`；`启动多桌面助手.bat` 内容为 `Unblock-File` + `powershell ... & '<exe>'`（普通权限、不经 ShellExecute/SmartScreen）。`dist\MultiDesk\MultiDesk.exe --selftest`（隔离 data-dir）→ `Total=28 Passed=28 Failed=0`，退出码 0，无残留 --test-window、会话日志已清理、金丝雀窗口未被触碰。注：本环境无法从 Low 沙箱真实模拟“Explorer 中等权限双击 lnk”的权限链，权限提升差异按设计说明（lnk/bat 均为普通权限启动，不触发 UAC/SmartScreen），真实用户双击路径由 Lead 侧核验。
- 截图整理：`screenshots/` 顶层保留编号关键图（01/02/02a/03/10-*、12-*），1.0/1.1 中间截图已移入 `screenshots/archive/`。

---

## 14. 1.2.1 更新（2026-10-26，配置 v4）

改动摘要：`AppOptions.AutoElevateWhenRestricted`（默认 true，配置 `CurrentVersion=4`，`Sanitize` 新增 `migrateFromV3`：旧配置迁移时置 true）；受限启动时若 `SkipRestrictedPrompt==true && AutoElevateWhenRestricted==true` 则不再弹程序提示框、直接弹 Windows UAC 以完整权限重启（取消 UAC 静默回退受限模式）；`AppController` 读不到真实桌面度量时使用缓存值，无缓存回退 Windows 一致实测常量 `IconMetrics(48, 76.6667, 98.0, 0, 3.3333)`；新增 `Native/TrayIconPromoter`（HKCU NotifyIconSettings `IsPromoted=1` + `SHChangeNotify`，Windows 11 托盘常显）；版本 1.2.1。

验证记录（verifier）：

- **单测**：`powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Release -Tests` → Release 构建 OK，`Total: 127 Passed: 127 Failed: 0`，`[test] OK`，退出码 0。本轮更新/新增用例：
  - `CurrentVersion` 断言 3→4；`AppOptions` 默认 `AutoElevateWhenRestricted=true`（以及 5 个 Windows 度量缓存字段默认 0）；
  - 迁移：v3→v4（AutoElevate 由 false 迁移为 true，v3 显式 Match/Skip 保留）、v2→v4（Match 与 AutoElevate 同时迁移为 true，IconSize/AutoArrange 保留）、v1→v4（全部迁移）、v4 保留显式 `AutoElevate=false`/`Match=false`/`Skip=true`；
  - ConfigStore：v4 保存/加载往返（Match/Skip/AutoElevate + 缓存度量 48 / 76.6667 / 98.0 / 0 / 3.3333）；磁盘真实 **v2 JSON**（全部迁移）与 **v3 JSON**（仅 v3→v4 迁移，显式 Match=false/Skip=true 保留）自动迁移；
  - 既有 1.0/1.1/1.2 用例（AppConfig/ConfigStore/IconLayout/DesktopEngine/SessionJournal/Launcher/WindowsDesktopMetrics 等）全部回归通过。
- **引擎自测**：`scripts\run-selftest.ps1 -ExePath "src\bin\Release\MultiDesk.exe"` → `Total=28 Passed=28 Failed=0`，退出码 0；无 `--test-window` 残留、`session.json` 已清理、金丝雀 notepad 窗口未被触碰。Release 产物 `FileVersion = 1.2.1.0`。
- **dist 1.2.1**（由 Lead 打包，verifier 验证）：
  - `dist\MultiDesk\`：`MultiDesk.exe`（230,912B，`FileVersion=1.2.1.0`）+ `app.ico` + `使用说明.txt`(3,451B) + `多桌面助手.lnk` + `启动多桌面助手.bat`；**`MultiDesk-data\` 用户配置原样保留**（app.log/config.json/config.json.bak/DesktopItems）。
  - `多桌面助手.lnk` 实测：`TargetPath=C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`，`Arguments=-NoProfile -WindowStyle Hidden -Command "& '<dist>\MultiDesk.exe'"`（普通权限 CreateProcess，不经 ShellExecute/SmartScreen）。
  - dist 独立自测：`dist\MultiDesk\MultiDesk.exe --selftest`（隔离 data-dir=`obj\dist-121-selftest`）→ `Total=28 Passed=28 Failed=0`，退出码 0；无 `--test-window` 残留、会话日志清理、金丝雀窗口未被触碰。
  - dist 冒烟：`dist\MultiDesk\MultiDesk.exe --ui-only --data-dir=obj\dist-121-smoke --exit-after=5` → 退出码 0。
  - 压缩包：`dist\MultiDesk-1.2.1.zip` 存在，含 `MultiDesk\` 目录（exe/ico/txt/bat/lnk + MultiDesk-data 用户配置）。**提示**：zip 内包含 `MultiDesk-data`（本机测试产生的配置/日志/DesktopItems），如用于对外分发建议 publish 时排除该目录（非阻断，待 Lead 决定）。
  - 注：本环境无法从 Low 沙箱真实模拟「Explorer 普通权限双击 lnk」的权限链，lnk/bat 的结构与启动命令已验证，真实用户双击路径由 Lead 侧核验。



---

## 15. 1.2.2 更新（2026-10-26，完整模式视觉/任务栏/托盘问题）

用户 1.2.1 完整模式下反馈：图标名称重叠、图标离屏幕边框距离不同、选中白框样式不同；切换桌面后任务栏运行程序不一致；右下角托盘溢出箭头（^）卡住无法展开。

1. **图标视觉（按真实桌面 LVM 实测精修）**：真实桌面 item 宽 114px（76 DIP），图标 glyph 48 DIP 在单元格内**水平居中**（左内边距≈14.3 DIP），顶部内边距≈1.3 DIP；标签宽度=单元格宽（≈76 DIP）、行高≈16.7 DIP、最多两行。图层改为：图标 left=(CellWidth-IconSize)/2、top=1.5 DIP；标签宽=CellWidth-2、格内换行；选中改为 Windows 风格半透明圆角高亮（宽=CellWidth、覆盖图标+两行标签，fill #33FFFFFF / border #66FFFFFF / 圆角 3）。
2. **任务栏按钮同步**：新增 `Native/TaskbarList`（ITaskbarList），`Win32WindowSystem.HideWindow/ShowWindow` 在隐藏/恢复窗口时调用 DeleteTab/AddTab，避免自绘任务栏/特殊 AppUserModelID 残留按钮或运行点。
3. **任务栏/托盘浮层不再被管理**：`DesktopEngine.SkipClasses` 增加 `Shell_TrayWnd`、`Shell_SecondaryTrayWnd`、`NotifyIconOverflowWindow`、`TopLevelWindowForOverflowXamlIsland`、`Taskbar`；启动时 `RestoreShellWindowsOnce()` 自动把被旧版本误隐藏的这些窗口恢复显示（修复 ^ 箭头卡住、隐藏图标无法展开）。
4. **诊断日志**：`[launch] parent=<进程名> integrity=Normal/Low` 记录启动来源与完整性，便于定位"受限模式"是否由聊天窗口/沙箱启动引起。

验证：Release 构建 OK、单测 127/127、`--selftest` 28/28、dist 冒烟+selftest、图标对比截图更新（screenshots/12-icon-match.png、layer-121-icon-selected.png）。


---

## 16. 1.2.3 新增卸载程序（2026-10-26）

新增 `MultiDesk.exe --uninstall` 卸载流程与发布包入口：

1. **入口**：`卸载多桌面助手.lnk`（经 powershell 普通权限启动，避免 SmartScreen）、`卸载多桌面助手.bat`。
2. **实现**：`--uninstall` 先把自身复制到临时目录（`%TEMP%\MultiDeskUninstall_*`，不可写时回退到程序目录同级），以 `--uninstall-run` 启动卸载界面，避免删除自身时文件被占用；`--quit` 命名事件让正在运行的实例优雅退出（恢复隐藏窗口/桌面图标）；异常时结束进程并由看门狗恢复；`Uninstaller` 删除已知程序文件、快捷方式、`HKCU\...\Run` 自启项、`NotifyIconSettings` 托盘项，可选删除 `MultiDesk-data`，只删已知文件、目录变空才删除。
3. **GUI**：复选框（删除快捷方式 / 删除配置数据）、日志区、取消/卸载确认。
4. **测试**：
   - 静默 E2E（复制发布目录，加入 `keep.txt` 未知文件）：`MultiDesk.exe --uninstall --silent --remove-data` → 已知文件与 MultiDesk-data 全部删除、`keep.txt` 保留、目录保留，卸载日志正常；临时目录回退（受限环境不可写 %TEMP%）验证通过；
   - GUI 路径：`--uninstall` 启动卸载窗口（截图 `screenshots/13-uninstall-window.png`），关闭（取消）后程序文件未变；
   - Release 构建 OK、单测 127/127；dist 1.2.3 冒烟通过（`--ui-only`），用户数据目录在发布时保留。


---

## 17. 1.2.4 更新（2026-10-26，原桌面模式 / 图标原生右键 / 后台运行）

用户需求：①不关闭程序也能回到原桌面；②虚拟桌面图标要能像原桌面一样右键操作（剪切/复制/重命名/共享/删除/属性/第三方扩展）；③切换桌面不影响正在运行的程序。

实现：

1. **原桌面模式**：`IAppHost.IsRealDesktopActive / ToggleRealDesktop()`；`AppController.ShowRealDesktop()` 暂停窗口管理、恢复所有窗口、恢复系统桌面图标、调用 `IDesktopSurface.SetRealDesktopMode(true)` 隐藏桌面图层；`ReturnFromRealDesktop()` 恢复隐藏策略（`_desktopIcons.Hide()` 重新隐藏系统图标）、显示图层并刷新，并调用 `engine.SetPaused(false)`（其内部 Tick 立即重算窗口可见性）。
   - 托盘菜单新增「回到原桌面（真实桌面）/ 返回多桌面」；空白桌面右键菜单顶部同样入口。
   - 实测（Medium 进程内托管）：进入原桌面 → 系统图标 visible=True、图层 IsVisible=False、IsRealDesktopActive=True；返回 → 图标重新 hidden、图层 visible、状态 False；退出后图标恢复。
2. **图标原生右键菜单**：新增 `Native/ShellContextMenu`（SHParseDisplayName → SHBindToParent → IShellFolder.GetUIObjectOf(IContextMenu) → QueryContextMenu/TrackPopupMenuEx/InvokeCommand，并转发 IContextMenu2/3 消息支持子菜单与第三方扩展）。图层图标右键优先调用 `IAppHost.ShowIconContextMenu`，失败（网址/目标不存在/受限）回退内置菜单。菜单操作后 700ms 检查目标是否还存在，删除/重命名导致目标失效时自动移除该桌面图标并刷新。
3. **切换桌面不影响程序运行**：窗口仅 `SW_HIDE`/`SW_SHOWNOACTIVATE`，进程与后台任务继续；`AppController.HiddenWindowCount` + 托盘悬停显示"N 个窗口后台运行"；原桌面模式下暂停管理但不结束任何进程。

验证：Release 构建 OK、单测 127/127、`--selftest` 28/28；原桌面模式双向切换/图标隐藏恢复实测通过；图标原生菜单无效目标返回 false 且秒开路径正常；截图 screenshots/layer-124-*.png、13-uninstall-window.png。


---

## 18. 1.2.5 新增「跨桌面显示的程序」（2026-10-26）

用户需求：工作桌面正在显示的一些程序，切换游戏桌面时仍要保持显示。

实现：
1. `AppOptions.AlwaysVisibleProcesses`（进程名列表，不区分大小写；v4 配置缺字段时 Sanitize 自动补空列表，无需迁移版本）。
2. `DesktopEngine` 维护跨桌面显示集合：`SetAlwaysVisibleProcesses()` 更新后立即恢复这些程序已被隐藏的窗口并移出管理集合；`Tick()` 扫描时跳过这些进程移出管理；`IsManageable()` 也排除它们，因此切换桌面时永不隐藏。
3. `AppController` 新增 `GetAlwaysVisibleProcesses / IsProcessAlwaysVisible / SetProcessAlwaysVisible / ToggleForegroundWindowAlwaysVisible / GetProcessNameForWindow`；固定/取消固定写入配置并同步引擎与托盘。
4. 托盘右键新增「跨桌面显示的程序」子菜单：固定当前窗口（显示进程名）/ 已固定程序列表（勾选，点击取消）。
5. 版本 1.2.5。

验证（Medium 进程内托管 + 自建 PinTestWin.exe 测试窗口）：
- 普通切换：桌面1→桌面2 测试窗口隐藏、切回恢复；
- 固定到所有桌面：切到桌面2 时测试窗口仍可见；固定列表返回 `PinTestWin`；取消固定后恢复正常管理；
- Release 构建 OK、单测 127/127、`--selftest` 28/28；dist 冒烟/selftest 通过。


---

## 19. 1.2.6 行为调整：默认所有桌面共用同一套运行程序（2026-10-26）

用户反馈：不需要"固定窗口"等额外操作，要求切换桌面时，所有正在运行程序的显示状态（窗口、任务栏、托盘等）完全不变，即所有桌面共用同一程序状态；桌面之间只区分图标/壁纸。

实现：

1. `AppOptions.IsolateWindows`（默认 **false**；旧配置缺字段时也是 false）。false 时：
   - `DesktopEngine` 不再管理任何窗口：`Tick()` 直接返回（若之前有隐藏窗口则恢复并清空管理集合）；
   - `SwitchTo()` 只更新当前桌面并触发 `DesktopSwitched`，不隐藏/恢复任何窗口；
   - 任务栏按钮、托盘图标、窗口 Z 序/焦点等完全不受切换影响。
2. `AppController.SetWindowIsolation(bool)` / `IsWindowIsolationEnabled`：托盘新增「窗口隔离（切换桌面时隐藏其它桌面窗口）」勾选项；关闭时恢复所有隐藏窗口并停止窗口管理；「将当前窗口移到」「暂停窗口管理」「跨桌面显示的程序」等窗口相关菜单项在关闭隔离时隐藏。
3. 启动时先应用隔离设置再 `Initialize`，避免旧会话遗留隐藏窗口被再次隐藏。
4. 版本 1.2.6。

验证（Medium 进程内托管 + 自建 PinTestWin.exe 测试窗口）：
- 默认（isolation=false）：managed=0；桌面1→桌面2→桌面1 测试窗口始终 visible=True；
- 勾选窗口隔离（true）：窗口纳入管理（managed=1），切到桌面2 时 visible=False；
- 关闭隔离：窗口立即恢复 visible=True、isolation=false；
- Release 构建 OK、单测 127/127、`--selftest` 28/28；dist 冒烟/selftest 通过。


---

## 20. 1.2.7 修复「打开文件 - 安全警告 / 无法验证发布者」（2026-10-26）

现象：用户每次启动 MultiDesk.exe 都弹出「打开文件 - 安全警告 / 无法验证发布者。你确定要运行此软件吗？」（发布者：未知发布者，发送方：本地 dist 路径）。原因：文件带有 Mark of the Web（`Zone.Identifier`，通常因经聊天软件/网页下载传递），Windows 对未签名且带该标记的程序使用 Shell 安全警告；本地当前文件检查已无 MOTW（可能被传递方反复重新标记），程序本身未做数字签名。

修复：
1. 新增 `MultiDesk.Core.FileUnblocker`：启动时删除程序自身与同目录启动文件（exe/lnk/bat/使用说明/图标）的 `:Zone.Identifier` 标记，因此**运行过一次后不会再提示**。
2. 发布包新增 `解除安全警告.bat`：一键对本目录所有文件执行 `Unblock-File`（首次运行前双击一次即可）；Uninstaller 会一并删除它。
3. 桌面快捷方式与「启动多桌面助手.bat」在启动前先 `Unblock-File` 再以普通权限启动，从快捷方式启动不会触发该弹窗。
4. 版本 1.2.7。

验证：给测试副本手动写入 `Zone.Identifier`（MOTW before=True）→ 运行程序（`--selftest`）后标记被自动删除（MOTW after=False）；Release 构建/单测 127/127、dist 冒烟+selftest 28/28。

---

## 21. 1.2.8 启动性能优化验证（2026-10-28）

背景：用户反馈开机自启慢。定位到 1.2.7 启动关键路径在 UI 线程同步调用 Shell 提取当前桌面全部图标（冷机单个图标可达数百毫秒），且托盘图标排在最后创建。1.2.8 的改动与原理见 [`docs/STARTUP-PERF.md`](STARTUP-PERF.md) 与 [CHANGELOG 1.2.8](../CHANGELOG.md)。

验证：

1. `build.ps1 -Tests`：Debug 构建成功、单元测试 **134/134 通过**（新增 7 个图标缓存用例：写入/读取、不同尺寸隔离、目标文件变化失效、Clear、key 稳定性、Prune、30 天过期）。
2. Release 构建：`build.ps1 -Release` 成功。
3. 受控启动（独立临时数据目录 + `--manage-only=__none__` + `--no-integrity-elevation`，避免影响真实窗口）：
   - 续启动（66 个图标已有磁盘缓存）：`startup-perf.log` 显示托盘可见 62 ms、桌面图层 505 ms、启动流程结束 514 ms；看门狗按预期在 +3 秒启动；退出正常无残留。
   - 首次启动（空缓存、导入真实桌面 66 个图标）：托盘 136 ms、图层 540 ms；图标在后台逐个提取（首个慢图标记录 `icon_shell_slow` 207 ms），不阻塞托盘/图层；缓存目录生成 PNG。
4. 图标缓存失效：复制目标文件并修改 mtime 后自动 miss（单元测试覆盖）；托盘菜单「刷新桌面图标」可清空重建。
5. 托盘图标内嵌资源：确认 `MultiDesk.exe` 资源中存在 `MultiDesk.app.ico`（Reflection 枚举 manifest resource）。
6. 版本显示（用户反馈修复）：设置窗口左下角与「版本信息」不再硬编码，改为读取程序集版本；Release exe 的 AssemblyVersion 为 1.2.8.0，界面显示 `版本 1.2.8` / `多桌面助手 MultiDesk 1.2.8`。

说明：冷启动（开机登录后第一次）受系统登录风暴影响，自动化环境无法复现，需在真实重启后按 `docs/STARTUP-PERF.md` 的步骤用 `startup-perf.log` 的 `uptime_s` 校验；1.2.7 的对照数据来自用户机器 app.log（12.43 s / 14.89 s / 2.02 s）。
