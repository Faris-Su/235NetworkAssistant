# 架构说明（Source of Truth）

## 0. 品牌与命名（2026-09 品牌重命名）

| 项目 | 值 |
| --- | --- |
| 正式中文名称 | 235修网助手 |
| 正式英文名称 | 235 Network Assistant |
| 内部名称 / 程序集名 / EXE | 235NetworkAssistant / 235NetworkAssistant.exe |
| 版本 | V0.1.0（AssemblyVersion 0.1.0.0 / FileVersion 0.1.0.0 / InformationalVersion 0.1.0） |
| 开发者 / 版权 | 235修网助手团队 / © 2026 235修网助手团队 |

命名做法与原因（最小修改原则）：

- **用户可见名称全部使用新品牌**：窗口标题、顶部品牌栏、关于信息、日志、性能报告、README/docs、EXE 文件名与文件属性
- **C# 命名空间与内部类名保留历史名称 `RuijieNetworkAssistant`**：改名会牵动 200+ 文件的
  namespace / using / XAML `x:Class` / `clr-namespace`，收益仅是内部观感，风险却明显更高；
  因此只调整程序集名（`AssemblyName`）与元数据
- 产品名称集中定义在 `Helpers/AppInfo.cs`（名称、版本、开发者、版权、兼容性声明），
  UI 通过 `x:Static` 引用，避免多处硬编码不一致；版本号由程序集 `InformationalVersion` 读取
- 数据目录随品牌改为 `%LocalAppData%\235NetworkAssistant\`，并在启动时做**一次性迁移**
  （旧目录存在且新目录不存在时整体改名，失败则保留旧目录），`settings.json` 里指向旧目录的
  备份路径也会自动改到新目录
- 名称含“修网”仅为产品命名，**未因此新增任何排障/诊断/拓扑推理功能**，V0.1 范围不变
- 兼容性声明固定为：“支持部分锐捷交换机 CLI 操作，具体命令以设备型号及固件版本为准”，
  并明确本软件为第三方工具，避免造成官方身份误解

发布件（EXE）文件属性实测（单文件版与目录版一致）：

| 属性 | 值 |
| --- | --- |
| 文件说明 (File Description) | 235修网助手 |
| 产品名称 (Product Name) | 235 Network Assistant |
| 产品版本 (Product Version) | 0.1.0 |
| 文件版本 (File Version) | 0.1.0.0 |
| 内部名称 (Internal Name) | 235NetworkAssistant.dll |
| 原始文件名 (Original Filename) | 235NetworkAssistant.dll |
| 公司 (Company) | 235 Network Assistant Team |
| 版权 (Copyright) | © 2026 235 Network Assistant Team |

> 说明：.NET 应用的 apphost（EXE）版本资源由**托管程序集**决定，因此 Internal Name 与
> Original Filename 显示为 `235NetworkAssistant.dll`，这是 .NET 的单文件/自包含发布的标准行为，
> 无法通过项目元数据改成 `.exe`；EXE 文件名本身是 `235NetworkAssistant.exe`。

## 1. 技术栈

- .NET 8（`net8.0-windows`，LTS）+ WPF + MVVM（自研最小 MVVM 基类，不引入第三方框架）
  - 构建机使用 .NET SDK 10.0.101 编译 net8.0 目标（发布为 self-contained，目标机器无需安装 .NET）
  - **为什么不是 .NET 10**：发布测试发现本机（及同批 Windows 10 现场电脑）上 .NET 10 的 apphost 无法启动
    （退出码 `0xC0000005`，SDK 自带 `csc.exe` 同样崩溃），而 .NET 8 的自包含单文件 EXE 可正常启动，见 §15
- 仅一个 NuGet 依赖：`System.IO.Ports`（WPF 共享框架不包含串口 API，属于 Console 支持的必需依赖）
- Excel 读取自研最小 OOXML 读取器（`System.IO.Compression` + `System.Xml`），不引入 NPOI / ClosedXML / OpenXml SDK

## 2. 分层

```
GUI (Views, XAML, 无业务逻辑)
  ↓
ViewModel (状态 + 命令；不 new SerialPort、不拼 CLI)
  ↓
Configuration Model (Models：Serial/Telnet 参数、SwitchRecord、VlanRecord、CommandPlan)
  ↓
Command Generator (Commands：集中生成 CLI)
  ↓
Command Preview (Views/CommandPreviewWindow + ViewModels/CommandPreviewViewModel)
  ↓
Device Connection (Services：IDeviceConnection)
  ↓
Serial / Telnet
  ↓
Ruijie Switch

资源库流程：
Excel → Resources/XlsxWorkbookReader → Resources/ExcelImporter → Import Preview → (用户确认) → Resources/JsonResourceRepository
```

依赖方向单向向下。禁止：

- 在 `Button_Click` 或 XAML 中写 CLI 字符串
- View 直接操作设备
- ViewModel 直接 `new SerialPort`
- Excel 路径硬编码、学校资源数据硬编码
- 自动 write、未经确认的 CLI、无意义的大型依赖

## 3. 目录结构

```
RuijieNetworkAssistant/
├── App.xaml / App.xaml.cs          应用入口（含 Phase 0 界面自检模式）
├── RuijieNetworkAssistant.csproj
├── Models/                         数据模型（连接参数、资源记录、命令计划、导入预览）
├── ViewModels/                     MVVM（Shell、页面、Command Preview）
├── Views/                          XAML 视图（MainWindow + 页面 + Command Preview 对话框）
├── Services/                       IDeviceConnection、Serial/Telnet、命令执行、备份、日志、设置
├── Commands/                       命令生成器 + 命令库（ShowCommands、DeviceCapability）
├── Resources/                      资源库（JSON）、Excel 读取与导入预览
├── Helpers/                        MVVM 基类、命令、IP/接口名工具、路径、服务定位
├── Themes/Styles.xaml              轻量样式（无动画/无模糊）
├── Properties/PublishProfiles/     win-x64 self-contained single-file 发布配置
└── docs/                           product / architecture / ui / commands / roadmap
```

## 4. 设备连接架构

统一接口 `IDeviceConnection`：

- `ConnectAsync` / `DisconnectAsync`
- `SendAsync(command)`（返回该命令的原始输出）
- `WriteRawAsync(text)`（Ctrl+C / Tab / ? 等透传）
- `State` / `StateChanged` / `OutputReceived` / `LastError`
- `DeviceName` / `ManagementAddress` / `Kind`
- `IAsyncDisposable`

实现：`SerialDeviceConnection`、`TelnetDeviceConnection`；预留 `SshDeviceConnection`。
CLI 页面与命令执行只依赖接口，不关心底层链路。

资源生命周期要求：

- 断开连接必须释放 SerialPort / Socket / Stream
- 重连不得重复订阅事件（`DeviceConnectionBase` 统一管理状态与释放）
- 接收缓冲有上限（4 MB），防止设备连续输出导致内存增长

## 5. 资源库选型：JSON 还是 SQLite

结论：**V0.1 使用 JSON（%LocalAppData%\235NetworkAssistant\resources.json）+ 内存索引**，
接口 `IResourceRepository` 保证后续可替换为 SQLite。

理由：

1. 常见校园资源库规模适中，无复杂关系与索引压力
2. 零额外依赖：SQLite 需要 `Microsoft.Data.Sqlite` + 原生库（SQLitePCLRaw），
   会增加体积、启动开销与部署复杂度，与「轻 / 快 / 稳」的硬约束冲突
3. 单文件读写 + 原子替换（先写 `.tmp` 再 `Move`），对单机单用户场景足够安全
4. 仍然满足「不把 Excel 当实时数据库」「运行时不做 Excel 查询」的要求

触发迁移到 SQLite 的条件：数据量超过数万条、需要多表关联/全文检索、或需要并发写。

## 6. Excel 导入兼容性

导入器面向用户提供的本地工作簿。工作簿、工作表名称、设备清单、地址、联系人和原始行值均不随项目分发。

- 支持多个工作表、不同表头位置、可选列、合并单元格和转置布局。
- 常见字段包括设备位置/名称/管理地址、VLAN/IP/掩码/网关，以及可选的端口、备注等信息。
- 地址字段可能包含附加说明或多个地址；解析器会提取可识别地址，无法识别的原始文本只留在用户本地资源库。
- 图片及公式列不参与导入；重复记录按稳定主键合并。
- 对字段不完整或无法可靠分类的工作表，导入器给出提示，不猜测、不写入不完整记录。

### 6.3 导入策略

- 表头驱动、按 Sheet 分类（交换机表 / VLAN 汇总表 / 实验室 VLAN 表 / 业务网段表 / 已用 IP 表 / 说明页）
- 未识别的 Sheet 一律跳过并记录提示，不做猜测解析
- 合并单元格向下填充；空行跳过并计入提示
- 无法解析的 IP 计入「异常」，原始文本保留在备注
- 导入前必须展示 Preview（新增/更新/跳过/异常），确认后才写入资源库
- 原始 Excel 只读，永不修改

## 7. 日志

- 等级：Debug / Info / Warning / Error（Release 默认 Info 及以上）
- 位置：`%LocalAppData%\235NetworkAssistant\logs\app-yyyyMMdd.log`
- CLI 原始输出**不写入**普通日志（属于终端数据，由用户主动保存为文件）
- 内存中仅保留最近 200 条日志

## 8. 离线优先

启动不等待网络，不依赖 DNS、在线 API、在线字体/图标/更新。资源库、命令生成、备份、
连接局域网设备全部离线可用。

## 9. 界面自检（Phase 0 验收手段）

为便于验收 XAML、绑定与页面布局，应用支持诊断参数（不显示窗口）：

```
235NetworkAssistant.exe --shell-check
235NetworkAssistant.exe --ui-snapshot=D:\out\overview.png
235NetworkAssistant.exe --ui-snapshot=D:\out\connection.png --ui-page=connection
235NetworkAssistant.exe --ui-snapshot=D:\out\preview.png --ui-page=commandpreview
```

这些参数只做本地渲染与日志输出，不写入任何业务数据。

## 10. Phase 0 验证结果（实测）

### 10.1 导入解析

用合成测试工作簿覆盖多个工作表、变体表头、合并单元格、转置表、重复记录和异常行；
同时在本地验证过用户工作簿的导入结果。真实工作簿及其行数、位置、地址和联系人不保存在仓库中。

命令生成验证：

```
将端口划入 VLAN 100          → interface range g0/1-3 / switchport mode access / switchport access vlan 100 / exit
Trunk 放通 VLAN 200-202,210 → interface range g0/24-25 / switchport trunk allowed vlan add 200-202,210 / exit
接口名归一化                   → Gi0/1, g0/2, GigabitEthernet0/3, Gi0/5, Fa0/7 → g0/1, g0/2, g0/3, g0/5, f0/7
```

### 10.2 界面

`--shell-check` 与 `--ui-snapshot` 全部通过：MainWindow（顶部状态栏 / 左侧导航 / 内容区 / 底部状态栏）、
概览、连接（地址簿 + Serial/Telnet 参数）、CLI、端口占位页、资源库、设置、Command Preview 对话框均正常渲染。

### 10.3 构建环境兼容性（重要）

本机（开发机）上 .NET SDK 10 自带的 `csc.exe` / `vbc.exe` 以及 SDK 生成的 apphost（`*.exe`）
会在启动时直接崩溃（退出码 `-1073741819` / `0xC0000005`），与代码无关。绕过方式：

1. 使用 NuGet 包 `Microsoft.Net.Compilers.Toolset` 4.14.0 中的 net472 编译器（见 `Directory.Build.props`）：

   ```
   set RUJIE_LEGACY_CSC_PATH=%USERPROFILE%\.nuget\packages\microsoft.net.compilers.toolset\4.14.0\tasks\net472
   dotnet build RuijieNetworkAssistant.csproj
   ```

2. 运行/调试时用 `dotnet exec bin\Debug\net8.0-windows\235NetworkAssistant.dll`
   （apphost 崩溃只影响本机的 exe 启动方式，不影响目标机器上的正常发布包）。

## 11. Phase 1：CLI 终端管线

### 11.1 数据通路

```
IDeviceConnection.OutputReceived（读取线程）
        ↓ 只加锁追加，不触碰 UI
TerminalSession._pending（接收缓冲）
        ↓ DispatcherTimer 批量刷新（默认 80 ms；缓冲越大越保守：150 / 250 ms）
TerminalBuffer（显示缓冲，有上限，裁剪时按行边界）
        ↓ 增量 Append / 裁剪时整体 Rewrite（300 ms 节流）
ITerminalDisplay（Views/Controls/TerminalTextBox）

同时：设备原始输出 → SessionRecorder（work\session-*.log，1 s 刷盘）
```

- `TerminalSession` 不依赖任何 UI 框架，因此可以在自动化脚本里用假显示面验证（见 11.3）
- 显示缓冲与原始输出**分离**：显示有上限保证内存，原始输出完整保证“保存输出”不丢数据
- 会话切换时先解绑旧连接的 `OutputReceived`，避免重复订阅；退出时停止定时器并释放会话文件

### 11.2 交互与命令发送

- 命令行结束符可配置（Serial 默认 `\r`、Telnet 默认 `\r\n`），连接成功后自动发送一次结束符唤醒提示符
- 交互输入走 `IDeviceConnection.WriteRawAsync`（透传），命令执行走 `SendAsync`（等待输出稳定后返回原始输出）
- Tab → `\t` 透传；`?` → 立即发送“当前输入 + ?”；Ctrl+C → `0x03`（存在选中文本时保留复制）

### 11.3 验证结果（无真机）

自检脚本启动本地 TCP 模拟锐捷交换机，验证：

| 检查项 | 结果 |
| --- | --- |
| Telnet 登录流程 | 模拟设备依次收到 `<username>` / `<password>`，随后收到唤醒回车 |
| 命令收发 | `SendAsync("show vlan")` 返回模拟设备输出（含 `FAKE-VLAN-TABLE`） |
| Telnet 协商 | 客户端对 `IAC DO 1` 应答 `IAC WONT 1`（不启用选项，纯终端透传） |
| 连接状态 | `ConnectionService`：Connected → Disconnected，SessionId 正确生成 |
| 终端管线 | 4000 字符分 200 块推送 → 1 次整体刷新；显示限制在 1000 字符内；原始记录 4000 字符完整 |
| 显示缓冲 | 上限 2000 字符时累计 24000 字符，裁剪 22032，且从完整行开始 |
| 命令历史 | 空命令不入栈；↑ / ↑ / ↓ 顺序与草稿恢复正确 |
| 原始输出留档 | `SessionRecorder` 记录 47 字符 → 另存文件读回一致（含结尾提示符） |

尚未实机验证（留到现场）：不同型号提示符差异、`Ctrl+C` 对 `show` 输出的中断效果、
Console 结束符差异、GBK 中文输出（当前支持 UTF-8 / ASCII / Latin1）。

## 12. Phase 2：资源库写入、地址簿与查询

### 12.1 数据流

```
Excel（只读）
  ↓ XlsxWorkbookReader（自研最小 OOXML 读取）
ExcelImporter（表头驱动 + Sheet 分类 + 合并单元格按行填充 + 跨工作表去重）
  ↓
ImportPreview（新增 / 更新 / 跳过 / 异常 + 未解析说明）
  ↓ 用户在【设置】页确认
JsonResourceRepository（本地 JSON + 内存索引，原子写入：先写 .tmp 再替换）
  ↓ Changed 事件
地址簿（连接页） / 资源库查询页 / 概览统计 立即刷新
```

要点：

- **导入主键稳定**：交换机 = 管理 IP；VLAN 汇总表 = `vlan-<id>`；实验室行 = `lab|工作表|房间|实验室|VLAN|IP范围`；
  业务网段 = `segment|工作表|业务类型`。因此同一份表重复导入只产生“更新”，不会重复堆积。
- **去重跨工作表生效**：同一 IP/VLAN 在多张 Sheet 重复出现时记入“跳过”，保证预览计数与写入结果一致。
- **导入只读源文件**：不修改 Excel；资源库运行期不再读取 Excel。
- **编辑隔离**：查询返回记录副本（`Clone()`），界面上未保存的修改不会影响资源库内容。
- **IP 反查**：候选按“原表 IP 范围命中 + 与网关/掩码推算网段一致 + 掩码最长前缀”排序；
  仅范围命中时在界面标注“原表该行 IP 范围可能写错，请核对来源工作表”。
- **导出**：交换机 / VLAN / 场所三个 CSV，UTF-8 BOM，Excel 可直接打开。

### 12.2 诊断开关

```
--resource-db=<path>   使用指定的资源库文件（多库/演示/验证场景，默认仍是 %LocalAppData%）
```

### 12.3 验证结果

见 `docs/roadmap.md` Phase 2 表格（导入计数、幂等、查询、编辑、导出、清空）。
自检使用临时库文件，不会写入真实资源库。

## 13. Phase 3：Show 输出解析与设备页面

### 13.1 双轨设计（结构化 + 原始输出）

```
show 命令 → CommandService（原始输出）
   ├→ ShowOutputParser（容忍式解析）
   │     ├ 结构化行 → DataGrid
   │     └ UnparsedLines（未识别行，不丢弃）
   └→ RawOutput（界面可展开、可复制，永远保留）
```

解析规则（`Services/ShowOutputParser.cs`）：

- `show interface status`：按空白列切分；接口名支持 `Gi0/1` 与 `GigabitEthernet 0/1` 两种写法
  （通过 `InterfaceNameHelper` 归一化）；列顺序按 Status / Vlan / Duplex / Speed / Type
- `show vlan`：首列 VLAN ID + 状态关键字定位（active / suspended…），名称允许包含空格
- `show int trunk`：按段落解析（Mode / Vlans allowed on trunk / allowed and active / forwarding state），
  段落内的行按端口合并成一条记录
- `show lldp neighbors`：块状详情型（`LLDP neighbor-information of port …` + `Key : Value`）与
  表格式（Local Intf / Neighbor Dev / Neighbor Intf）都支持；管理地址用 IPv4 提取
- 过滤设备提示符（`Ruijie#`、`Ruijie(config)#`）、命令回显与分隔线，避免它们被记为未识别行
- 一条都没解析成功时返回明确提示，界面只显示原始输出

### 13.2 页面与安全

- 端口 / VLAN / Trunk / LLDP 四页继承 `DeviceShowPageViewModel`：连接状态、忙碌状态、原始输出、
  解析摘要、`RunShowAsync`、Command Preview 入口统一实现
- 配置命令全部来自 `Commands/`（VlanCommandGenerator / PortCommandGenerator / TrunkCommandGenerator）
- 端口输入支持 `g0/1-3,g0/5`（`InterfaceNameHelper.ExpandAll`，单范围上限 128 个端口），
  VLAN 列表支持 `200,210-212`（`VlanListHelper`）
- LLDP → 端口页通过 `IShellNavigator.NavigateTo(key, parameter)` + `INavigationAware` 传参预选端口
- 刷新一律由用户点击触发，无任何自动轮询

### 13.3 诊断开关（用于界面验证）

```
--demo-telnet=host:port   诊断模式下先连接该 Telnet 设备，并自动执行一次当前页面的刷新
```

### 13.4 验证结果

见 `docs/roadmap.md` Phase 3 表格：合成样本解析、无法解析时的降级、以及经本地模拟交换机的
端到端（连接 → show → 解析 → 界面渲染）。实机校正清单同时列在该表中。

## 14. Phase 4：MAC/IP 查询、Show Center、配置备份

### 14.1 数据流

```
设备：show mac-address-table ─┐
      show ip dhcp snooping ─┴→ ShowOutputParser → MAC 表 / 绑定表（内存）
                                        ↓ 本地过滤（不随输入发命令）
                                    查询结果：IP → MAC → VLAN → Port
                                        +
                                    资源库（规划数据）：VLAN / 网关 / 掩码 / 位置

Show Center：命令分类（结构化 / 原始文本）→ CommandService → 解析或原样显示
备份：show running-config → 解析 hostname → 保存 设备名_YYYYMMDD_HHmmss.cfg → 目录扫描成历史
```

### 14.2 关键设计

- **MAC 归一化**：`MacAddressHelper` 统一 12 位十六进制比较，显示为 `02:00:00:00:00:01`；
  支持 `0200.0000.0001` / `02:00:00:00:00:01` / `0200-0000-0001` 三种写法
- **查询在本地做**：刷新一次后输入任意 MAC/IP 都是内存过滤，避免“每敲一个字符就查一次设备”
- **实时 + 规划对照**：IP 查询结果除设备数据外，追加资源库（`IResourceRepository.FindVlanByIp`）的
  规划 VLAN / 网关 / 掩码 / 位置，并保留“原表范围可能写错”的提示
- **设备名回填**：任何 show 输出都会尝试从提示符（`Hostname#` / `Hostname(config)#`）提取设备名；
  运行配置额外解析 `hostname` 行；备份文件命名优先使用 running-config 里的 hostname
- **剪贴板抽象**：`IClipboardService`（WPF 实现放在 `Helpers/WpfClipboardService.cs`），
  使 ViewModel 不依赖 WPF，可在自动化脚本中直接驱动验证
- **Show Center 分类策略**：`show version / interface status / vlan / mac-address-table /
  ip interface brief` 结构化；`show cpu / logging / running-config` 只显示原始文本

### 14.3 验证结果

见 `docs/roadmap.md` Phase 4 表格：解析（MAC/绑定/三层接口/版本）、设备名提取、MAC/IP 查询
（含资源库对照）、配置备份与历史列表；其中 MAC/IP 查询直接驱动 `MacIpViewModel` 完成。

## 15. Phase 5：集成验证、性能与发布

### 15.1 最终验收场景（自检脚本自动执行，7/7 通过）

| 场景 | 结果 |
| --- | --- |
| 1 地址簿 → Telnet IP 自动填充 → 连接 | PASS：示例交换机地址命中，连接后 Session 正常 |
| 2 CLI → show vlan / show interface status | PASS：终端显示设备输出，解析 3 行 / 3 行（Console 与 Telnet 共用 `IDeviceConnection`，Console 需真机） |
| 3 多选端口 → Access VLAN → 预览 → 执行 | PASS：`interface range g0/1-2` + `switchport mode access` + `switchport access vlan 200`，执行成功 |
| 4 LLDP → 邻居 → 管理 IP → Telnet 到邻居 | PASS：连上邻居后自动跳转 CLI 页 |
| 5 VLAN 查询 | PASS：网关 / 掩码 / IP 范围 / 位置齐全 |
| 6 IP → VLAN / 网关 / 掩码 / 位置 | PASS：192.0.2.50 → VLAN 200（范围命中 + 网关网段一致） |
| 7 备份 running-config | PASS：`Example-SW_YYYYMMDD_HHmmss.cfg`，内容含配置；配置修改仍走 Command Preview |

### 15.2 性能实测（同一台开发机，使用代表性资源库数据）

| 指标 | 单文件版（62.97 MB，1 个文件） | 目录版（145 MB，244 个文件） |
| --- | --- | --- |
| 启动（服务初始化 + 资源库加载） | 68 ms | 57 ms |
| 首次界面布局（1280×800） | 111 ms | 86 ms |
| 启动后内存 | 工作集 174 MB / 私有 98.5 MB | **工作集 84 MB / 私有 44 MB** |
| 空闲 5 秒 CPU | 78 ms（占单核 1.6%，仅状态栏时钟） | 78 ms |
| 页面首次打开（最慢） | 连接页 204 ms | 连接页 153 ms |
| 全部页面访问后托管堆 | 14.9 MB | 15.1 MB |

解析与终端管线（自检脚本）：

- 10000 行 MAC 表解析：14 ms
- 终端管线写入 100 万字符：7 ms；显示缓冲稳定在 200,000 字符上限，原始输出 1,002,000 字符完整落盘；GC 堆 17.5 MB
- 资源库查询（示例 VLAN 数据）：< 1 ms
- 启动 + 两份 Excel 导入：96 ms

**结论：低配外勤电脑推荐目录版（内存约为单文件版的一半）；需要单文件分发时用单文件版。**

### 15.3 发布结果

```
# 一键：双击项目根目录的 发布.cmd（自动准备兼容编译器 → 目录版 → 单文件版）

dotnet publish -c Release -p:PublishProfile=win-x64-folder        # 推荐：目录版
dotnet publish -c Release -p:PublishProfile=win-x64-single-file   # 单文件版
```

- 目标框架：`net8.0-windows`；`SelfContained=true` → 目标机器无需安装 .NET
- 分发方式：目录版拷整个 `bin\publish\win-x64-folder\`（243 个文件 / 145 MB）；单文件版只拷 `bin\publish\win-x64\235NetworkAssistant.exe`（63 MB）
- 单文件版验证：在本机直接双击运行 `235NetworkAssistant.exe` 成功（`--shell-check` 退出码 0），
  并且用发布出的 EXE 渲染界面快照、跑性能报告均正常
- 本机环境中 `.NET 10` 的 apphost 无法启动、`.NET 8` 正常，因此目标框架锁定 .NET 8 LTS

## 16. 小屏适配实现（V0.1 收尾）

```
窗口尺寸变化（SizeChanged）
  ├→ MainViewModel.UpdateAdaptiveLayout(宽度)
  │     └ 宽度 < 1180 DIP → IsNavigationCollapsed = true（渲染为仅图标导航；用户手动切换后不再自动改）
  └→ 页面 SizeChanged → ResponsiveColumns.ApplyForWidth(DataGrid, 页面宽度)
        └ 宽度 < 960 DIP → 隐藏被标记为 IsSecondary 的列（重要列保留，列宽仍可拖动）

专注 CLI 模式：IShellNavigator.SetFocusMode → MainViewModel 隐藏导航/品牌/底部状态栏
              → FocusModeChanged 事件 → CliViewModel 隐藏页面头部与说明，终端最大化

长页面整页滚动（设置页）：
  Grid SettingsPageHost → ScrollViewer(VerticalScrollBarVisibility=Auto)
      → Grid MinHeight={Binding ActualHeight, ElementName=SettingsPageHost}
         ├ 内容比窗口矮：Grid 被撑到宿主高度 → 星号行照旧拉伸填满，不出现滚动条
         └ 内容比窗口高：Grid 自然变高 → 右侧出现滚动条，所有信息都能滚到
  （MinHeight 绑定宿主而不是 ScrollViewer.ViewportHeight，避免“滚动条出现→视口变窄→布局回环”）
```

- 附带属性实现见 `Views/Controls/ResponsiveColumns.cs`（`IsSecondary` + `ApplyForWidth`），阈值集中在
  `ResponsiveColumns.CompactWidthThreshold`
- 工具条与表单统一使用 `WrapPanel` 自动换行；表格行高 22、面板内边距 8，压缩空白但**不缩小正文字号**
- 字体档位由 `AppSettings.UiFontScale / CliFontScale` 驱动，`Helpers/AppearanceService.cs` 负责应用与通知
  （仅切换时执行一次，无定时器、无持续重排）
- Command Preview 窗口按 `SystemParameters.WorkArea` 限制最大尺寸，命令区内部滚动、按钮固定
- 布局验收：`Helpers/UiSnapshot.FindClippedContent` 在每次 `--ui-snapshot` 后遍历可视树，
  报告「被裁掉且滚不到」的元素（跨 DataGrid 子树时不报，表格自己的滚动机制不算丢失）；
  `--ui-font=` 诊断参数可在不写回设置的前提下验证大字体布局

## 17. 连接故障可诊断性（现场验收后加固）

现场反馈「Console 和 Telnet 都连不上、且看不出原因」，因此连接层做三件事：

```
Services/ConnectionDiagnostics.cs
  ├ DescribeSerialFailure(port, ex)   .NET 原生异常 → 中文可操作提示（含本机实际串口列表）
  ├ DescribeTelnetFailure(host, port, timeout, ex)
  │     SocketError 分类：ConnectionRefused / TimedOut / HostNotFound / NetworkUnreachable / AccessDenied
  │     超时（OperationCanceledException / OperationAborted）单独识别，不再显示「已取消该项任务」
  └ RunSelfCheckAsync(...)  只读探测：串口枚举 → 地址解析 → 本机网段 → Ping → 端口探测
```

- 失败文案统一格式：**第一行中文结论 + 下一步动作**，第二行 `原始错误：…` 保留可追溯性
- 串口：`COM 列表` 只取 `SerialPort.GetPortNames()`；不再把默认 COM1 混进检测结果；
  只有一个串口时自动选中；连接时端口不存在/被占用分别给不同提示
- Telnet 自动登录：
  - 设备已经在提示符下 → 直接跳过（原实现会白等 2 个超时≈16 s）
  - 用户名 / 密码 / 提示符**共用一个时间预算**（原实现每步各等一个超时）
  - 结果写入 `IDeviceConnection.ConnectNotice`，由【连接】页与 CLI 页显示，不再「连上了但不知道登录没成功」
- 自检覆盖（`work/selfcheck`，14/14）+ 连接回归（`work/connprobe`，含 4 个模拟设备场景）

### 17.1 设备名识别（连接即可用）

现场反馈「两种连接方式都识别不出交换机名字」，定位到两个问题：

```
问题 1：设备名只在执行过 show 命令后才解析 → 只连接、不点刷新时，界面一直显示「未获取设备名」
问题 2：DeviceName 为空时顶部状态栏一律显示「未连接设备」→ 已连接也像没连上
```

实现：

```
DeviceConnectionBase.AppendOutput（读取线程）
  └─ lock(_captureSync)：追加缓冲 + TryRecognizeDeviceName(text)   ← 同一把锁内完成
        └─ _promptTail（160 字符滚动尾部，抗 TCP/串口分片）
              └─ ShowOutputParser.TryExtractHostnameFromPrompt("Example-SW(config)#") → "Example-SW"
  └─ DeviceIdentityChanged 事件 → ConnectionService → PropertyChanged/SessionChanged
        → 顶部状态栏 / 概览页 / CLI 页标题同步刷新
```

- 设备名与缓冲**必须在同一把锁内更新**：否则 Telnet 自动登录线程可能已经读到提示符、
  却读到还没写入的 `DeviceName`（实测复现过一次，现用 20 次连接压测覆盖）
- `MainViewModel.CompactDeviceText` 区分「未连接设备」与「已连接设备（尚未识别主机名）」
- CLI 页只在「未连接 → 已连接」时重建会话记录，避免设备名识别触发的刷新重复打印「已连接」

### 17.2 本地目录不可用时的降级

`SessionRecorder` 建立留档文件失败（磁盘满、目录被策略锁住、杀软拦截）时不再抛异常：

- 终端功能完全保留，只在终端里提示「原始输出无法写入磁盘…[保存输出]不可用」
- `MainViewModel.ActivateAsync` 把「页面构造失败」也纳入捕获：以前 `item.Factory()` 在 try 之外，
  一旦抛异常就是静默失败——用户点【CLI】没有任何反应

### 17.3 连接状态与 CLI 就绪状态分离

现场反馈「明明连上了却统一显示连接失败」，根因是**底层连接**和**CLI 初始化**被当成同一个状态：
`ConnectCoreAsync` 里既做 TCP/串口连接，也做自动登录和提示符等待，任何一步抛异常都会被
`DeviceConnectionBase.ConnectAsync` 的 catch 归为 `Error` → 界面显示「连接失败」。

```
DeviceConnectionState（现在是“底层传输状态”）
  Disconnected → Connecting → Connected ⇄ Authenticating → Ready
                                 ↑                          │
                                 └──────── Error（只有连不上才是 Error）

IDeviceConnection.CliReady / CliStateText（CLI 初始化维度，独立于上面）
```

| 状态 | 含义 | 界面文案 |
| --- | --- | --- |
| `Connecting` | 正在 TCP 握手 / 打开串口 | 连接中 |
| `Connected` | 底层已连上，CLI 还没确认（登录超时、设备无输出） | 已连接（CLI 未确认） |
| `Authenticating` | 底层已连上，正在自动登录 | 已连接（正在登录） |
| `Ready` | 已确认 CLI 可交互（看到提示符） | 已连接 |
| `Error` | **只有**底层连不上 | 连接失败 |

- `DeviceConnectionStateText.IsTransportUp()` 统一判定「可以发命令」的三种状态，
  `ConnectionService.IsConnected` / `RequireConnected` / `EnsureConnected` / CLI 写命令全部改用它
- 子类在底层真正建立后调用 `MarkTransportEstablished()`；抛异常时基类检查该标志：
  已建立 → 保留连接 + 写 `ConnectNotice`，**不再抛给界面**；未建立 → 才是 `Error`
- `MarkCliReady` / `MarkCliNotReady` 在 `Error`/`Disconnected` 下不再改状态，避免连接已掉线又被标回「已连接」
- 超时语义区分：TCP/串口超时 → `连接超时：…`；登录/提示符等待超时 → `已连接，但…没有看到命令提示符`

### 17.4 CLI 未确认时的手动登录

自动登录失败（设备提示符格式不认识、账号密码不对）时，`State` 停在 `Connected`、`CliReady=false`，
用户仍然可以进 CLI 手动登录。为此：

```
读取线程 AppendOutput
  └─ TryRecognizeDeviceName → (设备名是否变化, 是否看到提示符)
        └─ 看到提示符 && TransportEstablished && !CliReady
              → MarkCliReady("检测到命令提示符") + ConnectNotice="已检测到命令提示符，CLI 就绪。"
```

- 即：**手动登录成功后状态会自动跟上**，不会一直停在「已连接（CLI 未确认）」
- 设备名与提示符解析在同一把锁内完成，避免竞态（见 17.1）
- 验收脚本：`work/cli_typing_test.js`（连上后敲 `show version`）、
  `work/cli_manual_login_test.js`（自动登录失败 → 手动敲账号密码 → 状态转为就绪）

### 17.5 报错结构化（编号 / 原因 / 怎么修）

所有连接类报错统一由 `ConnectionDiagnostics.Format(...)` 生成，固定四段：

```
[编号] 标题
原因：一句话说清是什么问题
怎么修：
  1. 可照做的第一步
  2. ……
环境：本机串口列表 / 本机网卡与目标是否同网段
原始错误：.NET 原始异常（便于追溯，永远放最后）
```

编号目录：`NET-01`…`NET-08`（地址/网段/拒绝/超时/防火墙）、`COM-02`…`COM-05`（串口）、
`CLI-01`…`CLI-04`（CLI 未就绪类，不算连接失败）。README 里有对照表。

- 【连接】页用红框 + 内部滚动展示完整报错（限高 200 px，保证长文本也能看全），右上角 [复制报错]
- 状态栏与「最近操作」只取报错**第一行**（`Headline`）：状态栏是单行控件，多行会被撑高并挤压页面
  （`MainViewModel.ReportStatus` 还会把换行压成空格并限长 160 字）
- 错误码随异常一起写日志，便于事后追溯

### 17.6 连接页 Console / Telnet 手风琴

两套连接参数改为 `Expander` 手风琴（`Views/ConnectionView.xaml`）：

- 初始状态两块都折叠（`Console / Serial（配置线连接）` / `Telnet`）；展开其中一个时另一个在 code-behind 里被收起
- **注意**：XAML 加载期间 `Expanded` 可能先于另一个 `Expander` 创建而触发，
  处理函数必须判空（否则默认展开 Console 时会 `TargetInvocationException` 直接导致页面构建失败——已踩过）
- 验收：`--ui-snapshot` 出图后会打印「手风琴自检：展开 Console 收起 Telnet=…｜展开 Telnet 收起 Console=…」，
  检查完自动恢复默认展开状态

### 17.7 按钮可用状态（CanExecute）刷新规则

现场反馈「设置页 Excel 预览出来了，但[写入资源库]点不动」，根因是**命令可用状态通知缺失**：

```
AnalyzeExcelAsync：IsBusy=true → … → HasPreview=true（此时 IsBusy 仍为 true，通知出去的是“禁用”）
                    → finally: IsBusy=false（旧代码没有把这个变化通知 WriteLibraryCommand）
结果：按钮停在“禁用”，直到用户碰巧触发一次 CommandManager 重新查询才恢复
```

本项目的 `RelayCommand` / `AsyncRelayCommand` **不订阅 `CommandManager.RequerySuggested`**，
按钮状态只由显式的 `CanExecuteChanged` 决定，所以**每个依赖 IsBusy 的命令都必须在忙碌状态变化时刷新**：

- `SettingsViewModel.IsBusy` → 刷新 分析 / 写入 / 导出 / 清空 / 保存设置（原来漏了写入、导出、清空）
- `DeviceShowPageViewModel.IsBusy` → 基类刷新 `RefreshCommand`，子类通过虚方法 `OnBusyChanged()` 补充：
  端口页刷新 `RefreshLldpCommand`，MAC/IP 页刷新 `RefreshMacCommand` / `RefreshBindingCommand`

验收：`--ui-excel=<xlsx>` 跳过文件对话框直接分析，日志会打印
「最后一次按钮状态通知 = 可用/禁用」——这一项直接决定按钮会不会灰着，比只看 `CanExecute()` 更准确
（`CanExecute()` 每次现算永远是对的，会掩盖“没通知”的问题）。

### 17.8 CLI 终端模式（Xshell 风格输入）

原来 CLI 页是「只读终端 + 下方输入框」，用户要求改成 Xshell 那样“点终端就能打字”。做法是**双模式并存**：

```
AppSettings.CliTerminalMode（默认 true）
  ├ true  终端模式：TerminalTextBox 自己收键盘
  │        PreviewTextInput → SendTerminalTextAsync(text)   逐字符 WriteRawAsync
  │        按键 → CliViewModel.TerminalKey 枚举 → 控制序列：
  │             Enter=连接配的结束符 / Backspace=0x08 / Tab=\t / Esc=0x1B
  │             ↑=ESC[A  ↓=ESC[B（交给设备做历史，和 Xshell 一致）/ Ctrl+C=0x03
  │        Ctrl+V=剪贴板文本原样发送；有选中文本时 Ctrl+C 保留复制
  └ false 行模式：保留原输入框 + [发送]，整行发送（原行为）
```

- 转义序列集中写在 ViewModel，View 只做「按键 → 语义」映射，不出现协议细节
- 终端模式下隐藏输入框与[发送]按钮，终端占满整页；`TerminalTextBox.IsReadOnlyCaretVisible=true` 提供光标
- 切换模式时自动把焦点放到生效的输入面（终端 / 输入框）
- 验收：`work/cli_typing_test.js` 与 `--line-mode` 各跑一遍，日志含
  「接收到的 TCP 数据块数」（逐字符发送会明显多于 1）与「输入模式=终端/行」

### 17.9 跨线程 UI 更新（现场报错 “The calling thread cannot access this object”）

现场现象：串口已连上（顶部显示「已连接（CLI 未确认）」），连接页却报
`连接失败：The calling thread cannot access this object because a different thread owns it.`

根因是一条**跨线程事件链**：

```
ConnectionService.ConnectAsync 里 await WaitForFirstOutputAsync(...).ConfigureAwait(false)
  → “串口 1.5 秒无输出”的判定在线程池线程上继续执行
     → ReportCliNotReady → SetState(Connected) → StateChanged
        → ConnectionService.RaiseSessionChanged
           → ConnectionViewModel.RefreshState → DisconnectCommand.RaiseCanExecuteChanged()
              → WPF 按钮在非 UI 线程更新 IsEnabled → InvalidOperationException
                 → 异常冒回连接调用 → 界面显示“连接失败”
```

修复（一处覆盖全部同类问题）：

- 新增 `Helpers/UiThread.cs`：可注入的 UI 线程调度钩子（无 UI 环境下直通，保持 Helpers 不依赖 WPF）
- `RelayCommand` / `AsyncRelayCommand.RaiseCanExecuteChanged()` 统一走 `UiThread.Post`
- `App.OnStartup` 注册实现：**已在 UI 线程则直接执行（保持同步语义），否则 `Dispatcher.InvokeAsync` 排队**

验收脚本：`work/serial_cross_thread_test.js`（`--ui-serial=COM1`）。
注意它必须**先渲染一次界面**再连接——只有连接页的按钮真的创建出来，才能复现这个崩溃。
实测：关掉修复 → 复现一模一样的报错；打开修复 → 状态「已连接（CLI 未确认）」+ `[CLI-03]` 排查提示、无异常。

### 17.10 登录权限（普通模式 / 管理模式）

**问题**：CLI 里能手动 `enable`，但图形化页面（VLAN / Trunk / 端口）需要特权时没有入口，
用户只看到"点了没反应"，或者被一个中途弹出的登录窗口打断。

**设计**：权限在**连接阶段**确定，作为会话状态在全界面共享，不在图形化操作中途弹登录窗口。

```
Models/DevicePrivilege.cs
  DevicePrivilegeLevel  Unknown / User / Privileged     ← 由提示符判定，全应用唯一一份
  PrivilegeMode         Normal / Manage                  ← 连接页选择，默认 Manage，随设置落盘
  PrivilegeRequest      (Mode, EnablePassword)           ← 连接时传入；密码只在内存
  PrivilegeDefaults.EnablePassword = ""                  ← 默认留空，由用户输入
```

权限状态的**唯一来源**是 `DeviceConnectionBase.PrivilegeLevel`：

- `AppendOutput`（读取线程）在同一把锁里用 `CliPromptDetector.DetectPrivilegeLevel` 判定 `#` / `>`，
  变化时触发 `PrivilegeChanged`
- `ConnectionService` 转发该事件 → `MainViewModel`（状态栏 Chip）、`ConnectionViewModel`（连接页）、
  `CommandPreviewViewModel`（预览弹窗）同时刷新，**不存在页面各自维护一份权限**
- `DisconnectAsync` 调 `ResetPrivilege()` 复位为 `Unknown`，重连不会沿用上一台设备的权限

**enable 流程**（`DeviceConnectionBase.TryEnterPrivilegedModeAsync`）：

```
已是 Privileged？ → 直接成功
写 "enable" + 行结束符
  等【Password: 提示】或【任何提示符】（超时 = CommandTimeoutMs）
    ├─ 已经回到 Host#        → 成功（设备不需要密码）
    ├─ 不是 Password 提示    → 失败：设备不接受 enable / 提示符格式不同
    ├─ 密码为空              → 失败：提示先填密码（不发送空密码）
    └─ 是 Password 提示      → 发送密码 → 等提示符
                                 ├─ Host# → 成功
                                 ├─ Host> → 失败：Enable 密码不正确
                                 └─ 没提示符 → 失败：无法确认
```

关键约束：

- **不抛异常、不改连接状态**：任何失败只写 `PrivilegeNotice` 并返回 `PrivilegeElevationResult.Failure`，
  连接保持 `Connected`/`Ready`，界面显示"设备已连接，但进入管理模式失败：<原因>"
- **密码不落地**：`AppServices.EnablePassword` 是内存静态属性（不参与 `AppSettings` 序列化），
  不发日志、不弹框；`README` / 设置页都不保存它
- 连接流程 `ConnectionService.ConnectAsync(..., PrivilegeRequest?)` 在底层连上、CLI 判定完成后才提权，
  因此**提权失败绝不可能升级成"连接失败"**

**图形化页面的权限闸门**（两道）：

1. `CommandPreviewViewModel.IsPrivilegeBlocked`：普通模式下打开预览弹窗时显示内嵌「需要管理权限」面板
   （Enable 密码 + [提升权限] + [继续普通模式]）。提权成功后 `ElevateAsync` 末尾**自动调用 `ExecuteAsync()`**
   继续原操作，用户不用再点一次
2. `CommandService.ExecuteAsync` 兜底：`RequiresPrivilege(plan)`（本项目里非 `show` 命令都是配置命令）
   且权限为 `User` 时，直接返回中文原因、**不向设备发送任何字节**

`DevicePrivilegeLevel.CanConfigure()` 只把**明确判定为 `User`** 当作拦截条件：`Unknown`（还没拿到提示符）
按"可以试"处理，避免把本来能用的设备误挡在外面。

**提示归属（一次竞态修复）**：设备已经在提示符上时，`AppendOutput` 与自动登录流程会同时想写
`ConnectNotice`，谁最后写到取决于线程调度，表现为连接提示时好时坏（自检里表现为
「设备已在命令提示符下，已跳过自动登录」被「已检测到命令提示符，CLI 就绪」覆盖）。
现在用 `AutoLoginInProgress` 标记区分：**自动登录期间「CLI 就绪」只由登录流程写**，
登录结束（成功/超时/取消，走 `finally`）之后，设备再吐提示符才允许输出线程把状态补成就绪。

验收：

- `work/selfcheck`（25 个场景）含 4 个权限场景：管理模式自动 `enable` 进入 `#`、
  Enable 密码错误仍保持已连接、普通模式不发 `enable`、Enable 密码不在 `settings.json` 里
- `work/connprobe` 的 `PrivilegeTests.cs`（6 个场景）：普通模式判定、自动提权、密码错误、
  CLI 手动 `enable` 被识别、普通模式拦截配置命令 → 提升后放行、断开/重连权限复位
- 发布件端到端：`work/demo_privilege_snapshot.js`（模拟交换机 `>` + enable 密码）→ EXE 自动提权，
  状态栏显示 🟢 特权模式 #；把设备侧密码改成别的值 → 状态栏 🟡 普通模式 >，连接保持
- 命令预览权限面板：`work/preview_privilege_snapshot.js`。诊断开关
  `--ui-privilege=normal|manage`（强制本次诊断连接的权限模式）、`--ui-plan=vlan`（用示例配置命令打开
  Command Preview）、`--ui-plan-elevate`（在弹窗里点一次[提升权限]）。
  实测：普通模式连接 → 被拦截=True → 点[提升权限] → 权限变 `Privileged`、仍被拦截=False、
  已执行=True；设备侧收到 `enable` → `<enable-password>` → `vlan 300` → `name ExampleNetwork` → `exit`
- 单选按钮链路：`work/privilege_mode_test.js` + `--ui-privilege-radio=normal|manage`（在真实界面上
  **点一下**权限模式单选，再走【连接】页的按钮命令）。实测：普通模式设备只收到唤醒回车、
  停在 `Host>`；管理模式收到 `enable` + 密码、进到 `Host#`

#### 17.10.1 "选了普通模式还是特权模式"不是 bug

现场反馈过一次"无论选哪个模式都是特权模式"。复现后确认：**权限模式只决定"连接时要不要自动 enable"，
不会给账号降权**。校园网里大量账号登录后本来就是 `Host#`（特权级别 15），所以两种模式的结果一样。

处理办法是把它讲清楚，而不是改逻辑：

- 普通模式 + 设备本身是 `#` → 连接页给一条提示"不主动执行 enable，保持设备登录后的权限 ——
  当前是特权模式（#），说明该账号登录后本身就是 #"
- [提升权限] 按钮旁边常驻一句 `PrivilegeActionHint`：未连接 / 已是 `#` / 普通模式 三种情况分别说明
  为什么能点或不能点（按钮变灰时用户必须知道原因）
- `ConnectionService` 在权限级别变化时记录 `权限级别判定：…` 到日志，便于现场回溯（不含任何密码）

#### 17.10.2 配置命令必须先 `configure terminal`（2026-09-20 修复，致命）

现场反馈："端口页点[开启端口 (no shutdown)]、VLAN 页点[新建 VLAN] 都没效果。"

**根因**：命令生成器只产出**相对配置模式**的命令块 ——
`PortCommandGenerator.SetEnabled` → `interface range … / no shutdown / exit`，
`VlanCommandGenerator.CreateVlan` → `vlan X / name Y / exit` ——
而 `CommandService.ExecuteAsync` 是**直接把这批命令发到当前提示符下**的。
设备停在特权模式 `Host#` 时，`interface range …` 与 `vlan …` 都会被判非法：

```
Host#interface range g0/2
                          ^
% Invalid input detected at '^' marker.
```

（虚拟交换机默认是"宽容模式"，会假装接受这类命令，所以本地联调看不见问题；
真机复现要用 `启动虚拟交换机.cmd --strict-modes`。）

**修法**：不在每个生成器里加 `configure terminal`，而是在 `CommandService.ExecuteAsync` 统一包一层 ——
凡计划里含非 `show` 命令（`RequiresPrivilege` 判据），就先发 `configure terminal`，
进不去（回 `% Invalid input` / 超时）**立即报错并停止，不发后续命令**；命令块执行完再发 `end` 回到特权模式，
避免后续 show 命令或用户手敲的命令落在配置子模式里。`show` 类命令完全不经过这一步。

顺带把诊断开关 `--ui-plan=` 扩成可取 `vlan[:id[:名称]]`、`ports-enable[:端口表]`、`ports-shutdown[:端口表]`，
这样端口页与 VLAN 页两条链路都能被脚本按"用户点击"的同一条路径驱动（见 README §9「界面自检」）。

**验收**：`work/config_mode_retest.js` —— 起一台 `--strict-modes` 的虚拟交换机（等价真机行为），
用发布件跑「执行配置计划 → 看设备状态 → 再只读刷新确认」，12/12 通过：

| 断言 | 实测 |
| --- | --- |
| 反向对照：不开配置模式直接发 `interface range g0/2` / `shutdown` | 设备回 `% Invalid input detected at '^' marker`（说明测试本身对 bug 敏感） |
| 新建 VLAN 300（原本不存在） | 设备日志 `configure terminal → vlan 300 → name ExampleNetwork → exit → end`；新 VLAN 建立；再刷新 `show vlan` |
| 端口 Gi0/2 `shutdown` → `no shutdown` | 设备日志每次都先 `configure terminal`；`show interfaces status` 先 `down` 后 `up` |

### 17.11 只读 DataGrid 里的复选框点不动（端口页勾选）

现场反馈"端口页没法直接勾选端口"。根因是全局样式 `Themes/Styles.xaml` 里
`<Style TargetType="DataGrid"><Setter Property="IsReadOnly" Value="True" />`，
而端口页用的是 `DataGridCheckBoxColumn`——**这种列在只读表格里不会进入编辑态，勾选框点不动**
（用户看到的就是"点了没反应"）。

修复：改用 `DataGridTemplateColumn` + 真正的 `CheckBox`（单元格模板里的控件不受 IsReadOnly 影响，
一次点击即可勾选），列头再放一个三态 `CheckBox` 绑 `PortsViewModel.IsAllPortsSelected` 做全选/取消全选。

验收：`work/ports_select_test.js`（`--ui-ports-select`）会做命中测试确认鼠标落在 CheckBox 上，
再真的勾一行，检查 `IsSelected → SelectedPorts → SelectionSummary` 这条链；
实测 4 行端口 → 单元格复选框 4 个 + 表头 1 个、命中测试=True、勾选后"将影响 1 个端口：g0/1"、
全选/取消全选都生效。

> 经验：本项目所有 DataGrid 默认只读。以后要在表格里放可交互控件，一律用模板列，
> 不要用 `DataGridCheckBoxColumn`。

### 17.12 CLI 终端键盘：空格打不出来 / 退格没反应

现场反馈："CLI 里无法输入空格，而且 backspace 没有用。"

**根因一：空格被只读 TextBox 吃掉。**
终端模式下，普通字符是走 `PreviewTextInput`（TextComposition）转发给设备的。
但终端控件是 `IsReadOnly = true` 的 `TextBox`，空格会被 TextBox 自己的按键处理吞掉，
**不会**再产生 `TextInput`，于是 `conf t` 到设备那里变成 `conft`。

真机证据（用户现场会话 `session-Telnet-*.log` 的原始字节）：

```
23 63 6f 6e 66 74 08 20 08 5c   |#conft. .\|      ← “conf t” 的空格没了，只剩 conft
23 63 6f 6e 66 69 67 75 72 65 20 74 65 72 6d 69 6e 61 6c 20 |#configure terminal |  ← 用 Tab 补全才敲出来
```

修复：在 `CliView.OnTerminalKeyDown`（PreviewKeyDown）里显式处理 `Key.Space` —— 直接发一个空格
并 `e.Handled = true`，不再依赖 TextInput。标记 handled 也保证不会重复发送。

**根因二：设备确实删了字符，是显示没跟上。**
退格是发出去了的（`\b` = 0x08），设备也执行了擦除并回显 `\b \b`；但
`TerminalSession.Normalize` 只统一换行、不处理擦除控制字符，`\b` 被原样塞进 TextBox，
屏幕上什么都不变 —— 看起来就是"退格没用"。原始输出里能看到证据：

```
Example-SW#conft 08 20 08 5c        → 设备侧已经删掉 t
Example-SW(config)#int 08 20 08 ×3  → 三次退格都生效了
```

修复（[TerminalSession.cs](Services/TerminalSession.cs) + [TerminalBuffer.cs](Services/TerminalBuffer.cs)）：
凡是含有 `\b` / DEL(0x7F) 的刷新批次，都把**当前行 + 本批内容**合并后按终端语义做擦除
（删掉前一个字符，不跨行），再替换缓冲区尾部并立刻整屏重写 —— 不等 300ms 重写节流，
否则用户看到的还是"按了没反应"。

- 擦除序列跨刷新批次（`\b` 和 ` \b` 分两次到达）同样正确：每次都基于缓冲当前行做擦除
- **原始输出不受影响**：`SessionRecorder` 里仍保存 `\b \b` 原样字节，`SaveOutput` 导出的还是原始内容

验收：

- `work/cli_editing_test.js`（`--ui-keys=show+SPACE+ver+BACK+rsion+ENTER`）用一个**会做行编辑**的
  模拟交换机跑真实界面按键链路。实测：设备收到 `show ver` + `0x08` + `rsion`、
  解析出的命令是 `show version`（说明空格只发了一个、退格也生效）、终端显示不含 `\b`
- `work/selfcheck` 新增场景「设备回显 `\b \b` 时屏幕上真的删掉字符（含跨刷新）」

**根因三：光标乱跳（擦除不能用整屏重写）。**
第一版擦除修复是"合并当前行 → 替换缓冲尾部 → 整屏 `Rewrite`"，效果对了但副作用明显：
`Rewrite` 会把 `Text` 整个赋值，WPF 随即把 `CaretIndex` 复位到 0，用户看到的就是**光标乱跳**；
而且每次退格都要重排整个缓冲，开销也大。

修复：

- `ITerminalDisplay` 增加 `Backspace(int count)`；`TerminalTextBox` 用
  `Select(末尾 N 个) + SelectedText = ""` 实现（只读控件临时放开一下），
  **不整体重写**，因此光标、滚动位置、选区都不受影响
- `TerminalSession.Flush` 的擦除分支改成算"删几个 + 补什么"：
  `keep` = 当前行与合并后行的最长公共前缀，`eraseCount = 当前行长度 - keep`，
  `appended = merged[keep..]`，分别调用 `Backspace` / `Append`；
  只有缓冲被裁剪（`ReplaceTail` 返回 true）时才回退到整屏重写
- `TerminalTextBox` 每次 `Append` / `Rewrite` / `Backspace` 后都把光标钉到末尾
  （有选区时不动，避免打断复制）

验收（`--ui-keys` 同一脚本会打印光标位置）：`光标位置=295/295（末尾）｜选区长度=0`；
selfcheck 断言 `增量删除 2 次 / 整屏重写 0 次`。

**关于 Tab 出现"一大坨"**：不是软件的问题。软件对 Tab 只发一个字节 `0x09`
（`work/cli_tab_test.js` 实测：`软件发给设备的字节：conf t<09>`，Tab 个数=1）。
真实设备按 Tab 会把补全后的整行重打一遍；候选多个时列出候选命令表 —— 这些内容全部来自设备，
软件只是把设备回显原样显示（[保存输出] 的原始输出里能看到同一坨）。

### 17.13 LLDP 默认取 Detail（一次刷新拿全）

**改前**：`刷新邻居` 只执行 `show lldp neighbors`，要看管理 IP / Chassis ID / 系统描述，
必须自己选中某一行再点[详情]，一台 24 口的接入交换机就得点二十多次。

**改后**：`LldpViewModel.RefreshAsync` 编排两步，点一次就拿到完整结果：

```
show lldp neighbors
   → ShowOutputParser.ParseLldpNeighbors（表格式 / 块状都支持）
   → 取本地端口（去重，稳定顺序）
   → foreach 端口（顺序，绝不并发）:
        show lldp neighbors interface <port> detail
          → CommandService.RunShowResultAsync（公共管线：Raw → 归一化 → ANSI/退格 → 分页续页 → 提示符）
          → LooksLikeUnsupportedDetail？→ 标记"不支持"
          → ParseLldpNeighborDetail → 合并进同一条 LldpNeighborRecord
   → 汇总 ParseSummary：邻居 N 条｜Detail 成功 X｜Detail 失败 Y｜设备不支持 Z
```

关键设计：

- **数据模型**（`Models/LldpNeighborRecord.cs`）：`LocalPort / NeighborDevice / NeighborPort /
  ManagementIp / ChassisId / SystemDescription / PortDescription / Capability / AgingTime /
  RawDetailOutput / DetailState`。表格只绑其中的关键列，其余进详情面板；
  `RawBlock` 与 `RawDetailOutput` 分别保留两层设备原文，解析失败时能直接看原文。
  缺失字段统一由 `LldpNeighborRecord.Or()` 显示成 **N/A**，不会因为缺一个字段整条解析失败。
- **不并发轰炸**：顺序 for 循环；界面在循环里持续刷新 `DetailProgress`（"正在获取 Detail 3 / 7：g0/3"），
  每条命令都是异步 I/O，UI 不阻塞。
- **失败隔离**：单端口失败只把该行标成 `Failed`（保留基础信息），不影响整页；
  设备连续 2 个端口返回 `% Invalid input` / `% Incomplete command` 之类的"不支持"提示后停止继续查询
  （`UnsupportedStreakLimit = 2`），避免在老设备上白跑几十条命令。
- **合并规则**：Detail 有值的字段优先，缺失的保留基础表格里的值；端口匹配走
  `InterfaceNameHelper` 归一化（`GigabitEthernet 0/1` == `Gi0/1` == `g0/1`）。
- **命令仍然集中在命令库**：`ShowCommands.LldpNeighborDetail(port)`，View/ViewModel 里没有裸 CLI 字符串。

界面（`Views/LldpView.xaml`）：主表只有 本地端口 / 邻居设备 / 邻居端口 / 管理 IP / Capability / **Status**（短标签，
避免小屏横向滚动）；右侧详情面板 + [重新获取详情] [复制详情] [查看本地端口] [Telnet 到邻居]，
下面再放"该端口 Detail 原始输出"。1280×720 / 1366×768 实测无裁切、无横向滚动。

验收（`work/selfcheck`，10 个 LLDP 场景）：

1. 1 个邻居 → 一次刷新自动带 Detail，管理 IP / Chassis / Capability / Aging 都有
2. 多个邻居 → 3 个端口各 1 条命令，顺序 = 基础 → g0/1 → g0/2 → g0/3
3. 没有邻居 → 一条 Detail 都不发，页面照常可用
4. Detail 触发 `--More--` → 发**空格**续页，第二页的字段（管理 IP）照样解析进来
5. Detail 跨多个接收块 + CR/LF 混用 → 仍能完整解析
6. 字段缺失 → 显示 N/A，其余字段不受影响
7. 单个端口 Detail 失败（设备不回复）→ 该行"获取失败"，另一行"已获取"，整页可用
8. 设备不支持 Detail → 保留基础信息 + 只发了 2 条命令就停止
9. 有管理 IP 才能[Telnet 到邻居]
10. [查看本地端口] → 跳 `ports:Gi0/1` 且端口页真的选中该行；[复制详情] 内容含完整字段

发布件端到端：`work/lldp_detail_test.js`（配合 `work/mock_lldp_switch.js` 模拟支持 detail 的设备）
在 1280×720 / 1366×768 出快照，实测"邻居 2 条｜Detail 成功 2"、自动续页 2 页、布局 0 裁切。

#### 17.13.1 Detail 面板全中文化 + 图形化

现场反馈：详情是一大段"英文标签 + 英文值"的纯文本（`Capability: Bridge, Router`、
`Aging Time: 1minutes 35seconds`、`Chassis type: MAC address`），既不好读也看不出链路关系。

改法（`Models/LldpNeighborRecord.cs` + `Views/LldpView.xaml`）：

1. **取值也翻译，不只翻译标签**：
   - Capability：`B,R` / `Bridge, Router` → `网桥、路由器`（`TranslateCapability`，覆盖 B/R/T/C/W/P/S/O 与全称）
   - 时长：`1minutes 35seconds` → `1 分 35 秒`（正则解析天/小时/分/秒，跳过 0 值；
     `Update time` 变成 `5 分钟前`）
   - 类型：`MAC address` → `MAC 地址`、`Interface name` → `接口名`（Chassis/Port type 各一张映射表）
   - 缺管理 IP → `未获取（无法直连 Telnet）`（比 N/A 更有行动指引）
2. **图形化**：
   - 链路示意：左「本机 · 本地端口」→ 中间 `── LLDP ──` → 右「邻居 · 远端端口」（邻居框用警示色描边）
   - 设备能力做成徽标（`ItemsControl` + `WrapPanel`），一眼看出对面是交换机还是 AP
   - 字段表用「图标 + 中文标签 + 值」三列 `ItemsControl`（🏷 邻居设备 / 🔌 本地端口 / 🔗 远端端口 /
     🌐 管理 IP / 🆔 机箱 ID / 🧩 设备能力 / 📦 设备型号描述 / 📝 端口描述 / ⏳ 信息老化时间 /
     🔄 邻居信息更新 / 📡 Detail 状态）
   - `[复制详情]` 复制出来的也是同一份中文内容（带图标），可直接贴进工单
3. 解析层补齐 `Chassis type` / `Port type` 两个字段并在合并 Detail 时一起写回
   （之前漏合并，界面只能显示"未提供"）

验收：`work/selfcheck` 断言复制内容含「设备能力：」且包含「网桥」「路由器」、
链路示意两端有数据、字段行 ≥ 10 条；`work/lldp_detail_test.js` 在 1280×720 / 1366×768
出快照且 0 裁切。

### 17.14 SNMP 信息库归位 + 概览页网络工具 + 整页滚动

现场反馈三点：SNMP 的历史数据放在【设备】页找不到；概览页那行 "V0.1 不包含：…Ping/Tracert…"
已经和实际功能自相矛盾；Ping / Tracert 这种最常用的排障动作应该留在概览页。

#### 17.14.1 SNMP 信息库：设备页 → SNMP 页

**改前**："查 SNMP"在【SNMP】页，"看 SNMP 历史"却在【设备】（Show Center）页，
同一件事被拆到两个页面，用户找不到。

**改后**：信息库面板整体搬到【SNMP】页底部（`Views/SnmpView.xaml` 的 `Expander`），
数据源不变（`AppServices.SnmpDevices` → `SnmpDeviceStore`，落盘 `snmp-devices.json`）：

```
SnmpViewModel
  ├ Devices / SelectedDevice          设备列表（按 IP）
  ├ DeviceFields / DeviceSections     选中设备的标量字段 + 分类下拉
  ├ DeviceTable (DataView)            选中设备的某张分类表
  ├ DeviceSummary / DeviceSectionSummary
  ├ IsLibraryExpanded                 Expander 绑定（默认折叠，不占常驻高度）
  ├ RefreshLibraryCommand / ClearLibraryCommand
  └ ShowLibraryCommand                顶栏[信息库] = 就地展开本页面板
构造时订阅 AppServices.SnmpDevices.Changed；每次查询成功 → SaveAsync → RefreshLibrary
```

- 顶栏按钮由 [设备信息库]（跳转）改为 **[信息库]**（就地展开），不再跨页跳转
- `DeviceInfoViewModel` / `DeviceInfoView.xaml` 中与 SNMP 相关的成员、面板与多出来的
  RowDefinition 全部删除，**设备页回到"只读设备"的单一职责**
- 信息库仍然**不含 Community**，也不写进设置文件（自检断言 `snmp-devices.json` 与
  `settings.json` 里都没有 Community 的值）

验证：`work/snmp_page_test.js` 起本地 UDP SNMP 模拟代理（使用脱敏验证数据），运行发布件
→ 信息库显示 2 台设备（127.0.0.1 / 127.0.0.2）、历史快照 12 次、字段与分类表都有数据
（`work/snapshots/snmp/snmp-page-1280x800.png`）；【设备】页快照确认再无 SNMP 面板。

#### 17.14.1.1 OID 不是字符串：GETBULK 的"单调前进"保护必须按段比数值（2026-09-20）

GETBULK（0xA5）里有一条防死循环的保护："下一批的第一个 OID 必须比上一批的最后一个更大，否则终止"。
原实现用的是 `string.CompareOrdinal`，而 **OID 的字典序是按段的数值序，不是字节序**：

| 比较对象 | 字符串序 | 正确的 OID 序 |
| --- | --- | --- |
| `...1.1.1.1.9` vs `...1.1.1.1.10` | `.10 < .9`（`'1' < '9'`） | `.9 < .10` |

于是接口索引从 9 跨到 10 的那一刻被误判成"设备没前进"，**整个 WALK 当场结束**：
26 个接口的 `ifName` 表只回 9 行，而查询状态仍然是"查询成功"——**静默截断**。
（GETNEXT 路径没有这条保护，所以只有开 GETBULK 才会踩到，这也解释了为什么它一直没被发现。）

修复两处：

1. 新增 `SnmpV2cClient.CompareOid`（逐段数值比较），保护条件改用它；
2. 保护命中、以及 GETBULK 中途超时，**都不再把"已经拿到的一半"当结果返回**，
   而是标记该目标不支持 GETBULK 并退回 GETNEXT 重走整棵子树——宁可慢，不给半张表。

验证（`work/selfcheck` 场景「GETBULK 与 GETNEXT 逐行一致」，62/62 全绿）：
同一份真机 walk，`BulkEnabled=true/false` 的 54 行表数据逐行一致，且 `GETBULK 成功步数=42`（确实走了这条路径，
不是偷偷退化成 GETNEXT）。代理侧另有第三方交叉验证：`node work/verify_agent_bulk.js`
（net-snmp 3.29.1 独立实现）对 7 棵子树用 GETBULK 与 GETNEXT 结果逐条一致。

#### 17.14.2 概览页网络工具：Ping / Tracert

新增 `Services/NetworkTools.cs`，和 SNMP / CLI 一样是**独立**的只读工具：
只对目标地址发探测包，不依赖任何连接会话（`Ping` + `PingOptions(ttl, dontFragment)` 实现逐跳，
不需要管理员权限、不需要 raw socket）。

- `PingAsync`：默认 4 次、单包超时 1.5 s，逐包显示延迟与 TTL，最后给出发送/接收/丢包/最小·最大·平均；
  全丢时附排查建议，并明确提示"**很多交换机会禁用 ICMP，Ping 不通不等于 Telnet 不通**"
- `TracertAsync`：默认最多 15 跳、每跳 1 s，`IPStatus` 翻成中文（请求超时 / 目标不可达 / TTL 到期…），
  到达目标时标 `← 已到达目标`；中间设备不回 ICMP 时显示 `*` 并说明属常见现象
- `OverviewViewModel`：`ToolHost`（默认取当前设备 IP，没连接时取 SNMP 上次查过的 IP，用户手改过不覆盖）、
  `ToolOutput`、`PingCommand` / `TracertCommand` / `StopToolCommand`（`CancellationTokenSource` 取消）
- 结果区内联在「常用操作」面板（只读 `TextBox` + 双向滚动条）；同时删掉概览页那行已经过时的
  "V0.1 不包含：修网向导、自动故障诊断、拓扑推理、Ping/Tracert 等工具"

验证：`work/selfcheck` 场景「网络工具：Ping 回环（含统计）与 Tracert 回环（1 跳到达）」
实测输出 `统计：发送 2，接收 2，丢失 0（0%）…｜Tracert=1  127.0.0.1  0 ms  ← 已到达目标`。

#### 17.14.3 概览页 / SNMP 页的整页竖向滚动

概览页多了网络工具之后，在 1000×520 这类小屏上底部会被裁掉且滚不动
（`work/overflow_check.js` 报 "8 处被裁切且无法滚动到"）。沿用【设置】页已有的模式
（`work/wrap_page_scroll.js`）：把页面根 Grid 包进 `ScrollViewer` +
`Grid MinHeight="{Binding ActualHeight, ElementName=...PageHost}"`——
**窗口够高时布局与原来完全一致（不会出现空白），不够高时才出现整页滚动条**。
`OverviewView.xaml`（`OverviewPageHost`）与 `SnmpView.xaml`（`SnmpPageHost`）两页已套用。

验证：`node work/overflow_check.js 1000x520 overview snmp device` → **0 裁切**；
`node work/overflow_check.js 1280x720`（全部 12 页）→ **0 裁切**。

### 17.15 保存配置到设备（write）：唯一会写持久配置的操作

需求原话："加一个保存按钮可以向交换机发送 write，要方便按但是也要充分防误触"。
这条命令把 running-config 写进 startup-config，是**本项目里唯一会改设备持久配置**的动作，
因此设计目标不是"少给入口"，而是**入口好按、触发很难**。

#### 17.15.1 三道闸分别在哪一层

| 层 | 文件 | 负责 |
| --- | --- | --- |
| 入口 | `Views/MainWindow.xaml`（顶栏）、`Views/CliView.xaml`（CLI 工具条） | 常驻可见、一步可达；未连接 / 冷却期时 `CanExecute=false`，ToolTip 写明原因（`ToolTipService.ShowOnDisabled`） |
| 弹窗 | `ViewModels/SaveConfigViewModel.cs`、`Views/SaveConfigWindow.xaml(.cs)` | 显示"这是哪台设备 + 要发什么"；**按住 1.2 秒**状态机；提权面板；结果面板 |
| 服务 | `Services/ConfigSaveService.cs` | 连接 / 权限（普通模式）/ 冷却三重拦截；发命令；判定设备回显是 [OK] / 被拒绝 / 未确认 |

关键点：**按住状态机放在 ViewModel 里**而不是代码后置，`WriteAsync()` 自己检查
`IsHoldSatisfied`，没有按满就只改状态文字、不发任何命令——即使有人绕过界面直接调用，
也写不了设备；同时这个门槛可以被 `work/selfcheck` 直接断言。

```csharp
BeginHold();                 // 鼠标按下：记录起点（不满足条件时直接拒绝并说明原因）
UpdateHold();                // 视图定时器 40ms 调一次：返回 elapsed >= 1.2s
CancelHold();                // 松开 / 移出 / 失去捕获：进度清零
WriteAsync();                // 只有 IsHoldSatisfied == true 才会走到 ConfigSaveService
```

鼠标事件（`PreviewMouseLeftButtonDown` / `Up` + `LostMouseCapture` + 定时器内的指针越界检查）
都挂在**一个 `Border` 上**，且 `Focusable=False`：回车 / 空格 / Tab 都不可能触发写入。

#### 17.15.2 为什么不做"再点一次确认"

双击式确认对"手滑"几乎无防护（两次点击之间只差几十毫秒），而打字确认（输入 `write`）对
每天要保存十几次的外勤场景太重。按住 1.2 秒是这两者之间的折中：**单手操作、肌肉记忆友好，
但要"误触"就得按住不放满 1.2 秒**——误触概率远低于任何点击式确认。

#### 17.15.3 结果判定（不猜、不假报成功）

`ConfigSaveService` 只按设备回显判定，并把原始回显整段保留给界面：

- 命中 `[OK]` / `configuration saved` / `saved successfully` → **已保存**，进入 5 秒冷却
- 命中 `% Invalid input` / `% Incomplete command` 等 → **设备拒绝**：不进冷却（允许立刻换写法），
  提示 `也许要用 write memory`，并给出 [改用 write memory] 按钮（切换后仍需重新按住确认）
- 发出去了但没有任何可识别标志 → **已发送、结果未确认**：同样进冷却（避免用户以为没成功而反复 write），
  提示到【CLI】页核对原文
- 被连接 / 权限 / 冷却拦截 → `Sent=false`，摘要写明原因，设备侧一个字节都没收到

#### 17.15.4 验证

`work/selfcheck` 新增 5 个场景（模拟设备侧记录收到的每一行命令）：

1. 未连接 → 拒绝写入（设备侧收不到任何命令）
2. 没按住不发送；普通模式（>）连按住都不允许，直接调服务同样被拒
3. 按住 1.2 秒是硬门槛：按 0.2 秒 `IsHoldSatisfied=False`；按满 1.3 秒才发送，
   设备侧收到 `write` 并回 `[OK]` → 结论"已保存"
4. 成功后 5 秒冷却：第二次保存 `Sent=False`，设备侧 `write` 次数仍是 1
5. 设备回 `% Invalid input` → 提示 `write memory`；改用后重试成功

界面侧：`--ui-page=saveconfig`（配合 `--demo-telnet` + `--ui-save-demo`）在离屏快照里走完整流程
（按住 1.3 秒 → 真写 → 结果面板），并输出布局检查与**按钮清单**（确认最后一排按钮真的画出来了）；
`work/mock_switch_server.js` 增加了 `write` 应答（`Building configuration... / [OK]`）。

顺带修掉一个快照工具的坑：`UiSnapshot.Render` 原来没算元素 `Margin`，对话框根 Grid 的
12px 内边距会把右下角挤到画布外，**快照少掉最后一条按钮**（看起来像"按钮没画出来"）。
现在按 `画布 - Margin` 布局并按 Margin 偏移摆放。

#### 17.15.5 设备验证摘要

已在脱敏设备环境中检查连接、CLI 输入、分页、端口/VLAN/Trunk/LLDP/MAC/IP 解析及配置保存流程。
本仓库不包含设备地址、账号密码、序列号、原始会话日志或未脱敏的设备配置；详细现场记录保留在本地。
