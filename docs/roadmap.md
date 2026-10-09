# 开发路线图

## Phase 0（已完成）

范围：项目架构 + WPF UI Shell + 基础 Service 接口 + Resource Repository 基础结构 + Command Preview 基础组件。

交付内容：

- MainWindow：左侧导航 + 顶部设备状态栏 + 中央内容区 + 底部状态栏
- MVVM 基础结构（ObservableObject、RelayCommand、AsyncRelayCommand、ViewModelBase、按需初始化）
- 12 个页面（概览 / 连接 / CLI / 端口 / VLAN / Trunk / LLDP / MAC-IP / 设备 / 备份 / 资源库 / 设置）
- `IDeviceConnection` + `SerialDeviceConnection` + `TelnetDeviceConnection` + 工厂 + 会话管理
- `CommandService`（命令执行）、`BackupService`（备份）、`FileLogService`（轻量日志）、`SettingsService`
- 命令库与命令生成器：`ShowCommands`、`VlanCommandGenerator`、`PortCommandGenerator`、`TrunkCommandGenerator`
  （含 `DeviceCapability` / `CommandProfile` 预留）
- 资源库：`XlsxWorkbookReader`（自研最小 OOXML 读取）+ `ExcelImporter`（表头驱动、Sheet 分类、
  合并单元格填充、导入预览）+ `JsonResourceRepository`（JSON + 内存索引）
- Command Preview 对话框（操作说明 / 影响范围 / 命令 / 复制 / 取消 / 执行）
- docs：product / architecture / ui / commands / roadmap

Phase 0 明确未做：CLI 终端实机交互、端口/VLAN/Trunk/LLDP 页面功能、资源库写入（Phase 2 开放）。

## Phase 1（已完成）：Serial Console + Telnet + CLI Terminal

交付内容：

- **Console → CLI、Telnet → CLI 全链路打通**：CLI 只依赖 `IDeviceConnection`，Serial 与 Telnet 共用同一 UI
- **CLI 终端**：输出区（只读终端控件）、单行输入区、发送、清空、保存输出、命令历史、
  自动滚动、本地回显开关、Ctrl+C 中断（0x03）、Tab 透传、`?` 立即提问
- **批量刷新管线** `Services/TerminalSession.cs`：
  `设备流 → 接收缓冲（后台线程加锁追加）→ 批量刷新（默认 80 ms，缓冲越大间隔越保守）→ 终端`
  显示缓冲有上限（默认 200000 字符，可配置），裁剪时整体重写（带 300 ms 节流），
  显示内容不会无限增长；**原始输出按原样写入会话文件**，[保存输出] 导出的是完整原始输出
- **连接参数**：新增命令行结束符可选（Serial 默认 CR、Telnet 默认 CRLF）；连接成功后自动发送一次回车唤醒提示符
- **登录与错误处理**：Telnet 用户名/密码提示符识别、连接/命令超时、断线提示、错误原因显示
- **资源释放**：会话切换先解绑旧连接事件（避免重复订阅）、退出时停止刷新定时器并释放会话文件与网络资源

验证方式（无真机也能验证到链路层）：

- 自检脚本启动本地模拟锐捷交换机（TCP），验证登录、唤醒回车、`show vlan` 收发与 IAC 协商应答
- 终端管线验证：4000 字符分 200 块推送 → 1 次整体刷新，显示被限制在 1000 字符内，原始记录 4000 字符完整

仍待实机确认（Phase 5 前的现场验证清单）：不同型号的提示符格式、`Ctrl+C` 是否能中断 `show` 输出、
Console 是否需要 CR 以外的结束符、GBK 中文输出（当前编码为 UTF-8 / ASCII / Latin1）。

## Phase 2（已完成）：资源库

交付内容：

- **导入写库**：【设置】页 1) 选择 Excel 生成导入预览（新增/更新/跳过/异常）→ 2) 用户确认后写入本地资源库；
  支持两份表分别导入、重复导入幂等（同一份表再次导入全部按“更新”处理，不产生重复记录）
- **资源库管理**：统计（交换机 / VLAN / 场所）、来源文件与最近导入时间、导出 CSV（Excel 可直接打开）、
  清空资源库（二次确认）、重新加载
- **交换机地址簿（连接页）**：位置 / 名称 / 管理 IP / 型号 / 物理地址 / 备注 / 来源表，
  支持关键词搜索 + 楼栋筛选，[填入 Telnet 参数] 与 [连接 Telnet]（一键填充→连接→跳转 CLI，验收场景 1）
- **VLAN / IP / 楼栋 / 房间查询（资源库页）**：关键词、VLAN ID、IP、房间号查询；
  IP 反查按“IP 范围 + 网关网段一致 + 最长前缀优先”选择记录，并给出匹配依据提示；
  VLAN / 交换机明细可编辑（保存修改 / 删除选中），场所列表可查看

验证（自检脚本，使用临时库文件，不触碰真实资源库）：

| 检查项 | 结果 |
| --- | --- |
| 导入交换机表 | 新增 408 / 跳过 11 / 异常 17 → 写入 408 条交换机 |
| 导入 VLAN 表 | 成功识别 VLAN 与交换机、场所记录；无解析异常 |
| 重复导入 | 新增 0 / 全部更新（幂等，无重复记录） |
| IP 反查 | 文档专用示例地址 → VLAN / 网关 / 掩码 / 位置 |
| VLAN 查询 | VLAN 200 → 匹配记录；位置关键词可用于筛选 |
| 地址簿搜索 | “实训区域” → 命中示例交换机记录 |
| 编辑 / 删除 | 交换机与 VLAN 记录修改、删除均生效并落盘 |
| 导出 | 交换机、VLAN 与场所 CSV 均可用 UTF-8 BOM 导出，Excel 可直接打开 |
| 清空 | 清空后 交换机 0 / VLAN 0 / 场所 0 |

已知限制：`实训楼已用IP` 现场表字段不完整（无 VLAN/掩码），只保留结构不导入；
原表部分 IP 范围写法有误（如 `192.0.2.2-192.0.2.8`），反查时会标注“请核对原表”。

## Phase 3（已完成）：Port / VLAN / Trunk / LLDP

交付内容：

- **解析层** `Services/ShowOutputParser.cs`：解析 `show interface status` / `show vlan` / `show int trunk` /
  `show lldp neighbors`（块状详情型与表格式都支持）。
  原则：能可靠解析的结构化显示，不能解析的行进入 UnparsedLines，**原始输出始终完整保留**并可直接复制；
  解析失败时界面明确提示并只显示原始文本，不做猜测解析。
- **端口页**：端口状态 DataGrid（Port / Status / VLAN / Duplex / Speed / Type / LLDP 邻居），
  勾选端口或手工输入（`g0/1-3,g0/5`）→ 选择操作（开启/关闭/Access VLAN/Trunk/速率/双工/介质类型）→
  **生成命令并预览**；[获取 LLDP 邻居] 会把邻居填进 LLDP 列
- **VLAN 页**：VLAN 列表（VLAN ID / Name / Status / 端口数 / Ports）、创建 VLAN、删除 VLAN（危险操作）、
  批量端口划入 Access VLAN，全部经 Command Preview
- **Trunk 页**：Trunk 端口列表（Mode / Encap / Status / Native / Allowed / Active VLANs）、
  设为 Trunk、Allowed VLAN 的 add / remove、Native VLAN（危险操作）
- **LLDP 页**：邻居列表（本地端口 / 邻居设备 / 邻居端口 / 管理 IP / 更新时间）、
  [详情]（`show lldp neighbors interface <port> detail`）、[Telnet 到邻居]（有管理 IP 时一键连接并跳转 CLI）、
  [查看本地端口]（跳转端口页并预选端口）；明确不做自动拓扑推理
- 所有页面：刷新由用户点击触发（无自动轮询）；断线时禁止发送；命令来自 `Commands/ShowCommands.cs` 集中定义

验证（自检脚本 + 本地模拟交换机）：

| 检查项 | 结果 |
| --- | --- |
| 解析合成样本 | 端口 3 行 / VLAN 3 行 / Trunk 1 行 / LLDP 1 行（块状）+1 行（表格式）全部解析成功 |
| 无法解析的输出 | 结构化 0 行、未识别 2 行、原始输出保留、给出明确提示 |
| 端到端（Telnet 模拟设备） | `show interface status` → 解析 5 行；`show vlan` → 4 条；`show int trunk` → 1 条；`show lldp neighbors` → 2 条 |
| 界面验证 | 端口 / VLAN / Trunk / LLDP 四页在“已连接 + 已刷新”状态下渲染正常（见 outputs/ui-snapshots 11–14） |

**待实机校正**（Phase 5 前的现场清单）：不同型号/固件的列宽与列顺序差异、`show int trunk` 段落命名差异、
LLDP 邻居的 `Management address` 多地址情况、`show interface status` 中路由口/聚合口的显示方式。
解析器已按“失败即显示原始输出”设计，因此实机差异不会导致信息丢失。

## Phase 4（已完成）：MAC/IP / Device Info / Backup

交付内容：

- **MAC/IP 页**：
  - `show mac-address-table` → MAC → VLAN → Port → Type；`show ip dhcp snooping binding` → IP → MAC → VLAN → Port
  - 一次刷新后在**本地过滤**（输入 MAC 或 IP 立即得到结果，不会每次输入都向设备发命令）
  - 查询结果同时给出**资源库规划信息**（VLAN / 网关 / 掩码 / 位置 / 来源），实现“实时数据 + 规划数据”对照
  - 设备不支持 DHCP Snooping 时明确提示，并保留原始输出；MAC 地址写法自动归一化（`0200.0000.0001` ↔ `02:00:00:00:00:01`）
- **设备（Show Center）页**：8 个入口——基本信息（show version）、接口状态、VLAN、MAC 地址表、三层接口、
  CPU、日志、运行配置。可解析的做结构化表格/字段，`show cpu` / `show logging` / `show running-config`
  直接显示原始文本（不做脆弱解析）；原始输出可展开、可复制
  - 任意 show 输出都会从提示符解析设备名并回填顶部状态栏；运行配置还会解析 `hostname`
- **配置备份页**：备份 running-config 到本地（文件名 `设备名_YYYYMMDD_HHmmss.cfg`，设备名优先取
  running-config 里的 hostname）、备份历史列表（时间/设备/大小）、打开备份目录/文件、复制路径；
   明确提示“不自动执行 write，保存配置必须由用户在设备上按住确认”
- 所有配置类操作仍全部经 Command Preview（Phase 3 已完成，本阶段未新增绕过路径）

验证（自检脚本 + 本地模拟交换机）：

| 检查项 | 结果 |
| --- | --- |
| MAC 解析 | `show mac-address-table` → 3 行（VLAN/MAC/Type/Port），写法定归一化 |
| DHCP 绑定解析 | 2 行（IP/MAC/VLAN/Port/Lease/Type） |
| 三层接口解析 | 3 行，`unassigned` 正确判定为未分配 |
| show version 解析 | 型号 锐捷交换机型号 / 软件版本 / 硬件版本 / Boot 版本 / 运行时间 / 序列号 |
| 设备名提取 | running-config 的 `hostname` 与提示符两种方式都能提取设备名 |
| MAC/IP 查询 | 示例 IP → DHCP 绑定 → MAC 表 → 资源库规划信息；MAC 反查同样完整 |
| 配置备份 | 生成 `Example-SW_YYYYMMDD_HHmmss.cfg`（符合命名规范），内容包含 hostname，历史列表可读 |

**待实机校正**：不同型号的 MAC 表/DHCP 绑定表列顺序、`show version` 字段名称差异、DHCP Snooping 是否启用；
解析失败一律回落到原始输出。

## Phase 5（已完成）：整体集成、性能与发布

1. **端到端集成测试**：自检脚本自动执行最终验收场景 1–7，**7/7 通过**（明细见 `docs/architecture.md` §15.1）
   - 场景 2 的 Console 分支需要真机（Console 与 Telnet 共用 `IDeviceConnection`，代码路径一致）
2. **性能测试**（同一台开发机、代表性资源库数据）：
   - 启动 57–68 ms；首次界面布局 86–111 ms
   - 内存：目录版工作集 84 MB / 私有 44 MB；单文件版 174 MB / 98.5 MB
   - 空闲 CPU：5 秒内 78 ms（单核 1.6%，仅状态栏时钟在跑，无后台轮询）
   - 页面首次打开（按需初始化）31–204 ms；全部页面访问后托管堆约 15 MB
   - 解析吞吐：10000 行 MAC 表 14 ms；终端管线写入 100 万字符 7 ms（显示缓冲稳定在上限，原始输出完整）
   - 启动 + 两份 Excel 导入：96 ms
3. **发布测试**：
   - 新增 `win-x64-folder` 发布配置（推荐，内存更省）；`win-x64-single-file` 保留
   - 目标框架由 net10.0-windows 调整为 **net8.0-windows（LTS）**：发布测试发现本机 .NET 10 apphost 无法启动
     （0xC0000005），而 .NET 8 自包含 EXE 正常；调整后单文件 EXE 在本机**直接双击运行成功**，
     并用发布出的 EXE 完成界面渲染与性能报告

### 后续待办（现场）

- Console 真机联调（USB-Serial 驱动、结束符、Ctrl+C 中断效果）
- 各型号 show 输出的解析校正（见 Phase 3/4 的待实机校正清单）
- GBK 中文输出支持（当前 UTF-8 / ASCII / Latin1）

### V0.1 收尾补充：小屏 / 外勤设备 UI 优化（已完成）

- 导航自动折叠（< 1180 DIP 仅图标）+ 手动切换；页面宽度 < 960 DIP 时 DataGrid 自动隐藏次要列
- 顶部状态栏单行化（连接详情进 ToolTip）；专注 CLI 模式（Ctrl+L / Esc）
- 端口 / VLAN / LLDP / MAC / 设备页增加“选中项详情”，小屏无需横向滚动
- 工具条与表单自动换行；面板内边距与行高压缩，但正文字号不缩小
- 设置 → 外观：界面字体 / CLI 字体（小 / 标准 / 大）独立设置
- 快捷键：Ctrl+Alt+C 回 CLI、Ctrl+F 聚焦搜索框、Ctrl+L 专注模式、Esc 退出专注
- 验收：新增 --ui-size=WxH 快照参数，已在 1000×620（小屏）与 1366×768 下逐页核对；
  功能自检仍为 9/9 通过（无回归）

## V0.1 不做清单（硬约束）

❌ 修网助手 ❌ 排查向导 ❌ AI 故障诊断 ❌ 自动故障定位 ❌ 自动拓扑推理
❌ Ping 工具 ❌ Tracert 工具 ❌ ipconfig 工具 ❌ ACL ❌ VRRP ❌ 高级 STP
❌ 高级 DHCP ❌ 链路聚合（Link Aggregation）❌ 端口镜像 ❌ 高级 SVI
❌ 静态路由 ❌ DHCP 高级安全功能 ❌ BPDU Guard ❌ 强制在线更新

> 变更（V0.2.1）：**Ping / Tracert 已提前交付**，见 `docs/architecture.md` §17.14.2
> 与概览页「网络工具」。理由：它们是无状态只读探测（不写设备、不碰配置），
> 是外勤排障第一动作，且不需要为此引入任何新架构。其余条目仍然不做。
> 概览页原 "V0.1 不包含：…Ping/Tracert…" 的提示文案已删除，避免与现状矛盾。

## 后续版本（V0.2 / V0.3 / 未来）

- V0.2：链路聚合、端口镜像、SVI/管理 IP、STP、更完整 LLDP Link Tracing、设备能力自动识别
  （**已交付部分**：SNMP 独立模块与信息库、LLDP 默认取 Detail、Ping / Tracert 网络工具）
- V0.3：DHCP、ACL、端口安全、ARP 防护、BPDU Guard、VRRP、静态路由
- 未来：SSH、多设备会话、多设备批量操作、配置 Diff、设备模板、更完整的资源库管理、更多锐捷型号
