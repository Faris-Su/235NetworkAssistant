# 235修网助手（235 Network Assistant）— 产品说明

> 正式名称：235修网助手（235 Network Assistant）｜内部名称：235NetworkAssistant｜
> 当前版本：V1.0｜开发者：235修网助手团队｜版权：© 2026 235修网助手团队
>
> 本软件是独立的第三方运维辅助工具，与设备厂商无隶属关系；支持部分锐捷交换机 CLI 操作，
> 具体命令以设备型号及固件版本为准。

## 1. 定位

面向校园网管人员的**离线优先**锐捷交换机配置与运维辅助工具。GUI 是 CLI 的可视化辅助层，
不是 CLI 的替代品；使用者本身具备基本的网络排障与交换机 CLI 操作能力。

核心目标：

1. 减少重复 CLI 操作
2. 把常用锐捷交换机配置图形化
3. 提供完整可用的 CLI 终端能力（Console / Telnet / SSH）
4. 所有配置修改提供命令预览，降低误操作风险
5. 把学校现有交换机 IP 表与 VLAN/IP 资源表整合进软件
6. 无互联网环境可正常使用
7. 在性能较弱的外勤 Windows 小电脑上保持低 CPU、低内存、快速响应
8. 保留原始 CLI 输出，不因解析失败而丢失信息

## 2. 明确不做（V0.1）

本软件**不是**：修网助手、自动故障诊断工具、教学软件、AI 网络故障分析工具、自动拓扑推理工具。
基本排障能力属于网管人员自身基本素养，不纳入 V0.1。

V0.1 不实现：修网向导、自动故障定位、Ping / Tracert / ipconfig 工具、ACL、VRRP、高级 STP、
高级 DHCP、链路聚合、端口镜像、高级 SVI、静态路由、BPDU Guard、DHCP 高级安全、
自动拓扑图、自动链路诊断。

> 变更（V0.2.1）：**Ping / Tracert 已交付**（概览页「网络工具」，无状态只读探测），
> 见 README §3.0 与 `docs/architecture.md` §17.14.2；ipconfig 与其余条目仍不做。

## 3. 使用场景

网管人员携带性能较弱的 Windows 小电脑外出维护（校园机房、实训楼、教学楼、小型机房、外勤）。

典型工作流：

```
选择交换机 → 连接（Console / Telnet）→ 进入 CLI → 查看设备状态
→ 使用 GUI 辅助配置 → 查看生成的 CLI → 确认 → 执行 → 备份配置
```

## 4. 目标平台

- Windows 10 / Windows 11（x64）
- .NET 8（LTS）+ WPF + MVVM（发布为 self-contained，目标机器无需安装 .NET）
- 发布形态：self-contained / single-file EXE
- 运行时不依赖 Python / Node.js / Java / Web 服务器 / 云数据库 / 互联网 API

USB-Serial 驱动属于 Windows/硬件层面的外部前置条件。

## 5. V0.1 功能范围

| 模块 | 内容 |
| --- | --- |
| 连接 | Console / Serial（COM、波特率、数据位、停止位、校验、流控可配置）、Telnet（Host/Port/用户名/密码/超时） |
| CLI | 类真实终端界面，Serial 与 Telnet 共用；批量刷新、最大缓冲、命令历史、保存输出 |
| 交换机地址簿 | 导入本地设备清单，支持按名称/位置/IP 搜索，选中后填充 Telnet 参数 |
| 资源库 | 导入本地网络资源工作簿，支持 VLAN/IP/位置查询；离线本地存储 |
| 端口 | 状态查看、shutdown/no shutdown、Access/Trunk、速率、双工、多选批量（经命令预览） |
| VLAN | 查看、创建、删除、批量端口加入 Access VLAN |
| Trunk | 查看、设置 Trunk、Allowed VLAN 增删、Native VLAN（设备支持时） |
| LLDP | 邻居列表与详情、Telnet 到邻居（无自动拓扑） |
| MAC / IP | MAC 查询、IP 查询、DHCP Snooping Binding（设备支持时） |
| 设备信息 | Show Center：常用 show 命令，可解析则结构化，不可靠解析则显示原始文本 |
| 配置备份 | 获取 running-config 并保存本地，`DeviceName_YYYYMMDD_HHmmss.cfg` |
| 命令预览 | 所有配置操作的最后一道人工确认，危险操作明确提示影响范围 |

## 6. 安全原则（硬约束）

1. GUI 配置默认**只生成命令**
2. 必须经过 Command Preview
3. 用户确认后才 Execute
 4. 不自动 `write` / `write memory` / `copy running-config startup-config`
    （V0.2.1 起提供显式 [保存配置] 按钮，需按住 1.2 秒确认；软件仍然不会自动保存）
5. 不自动修改额外端口
6. 批量操作明确显示影响端口范围
7. 断线禁止发送命令
8. 执行结果必须显示
9. 原始输出必须保留
10. 失败必须明确提示

## 7. 性能硬约束

稳定性 > 响应速度 > 低资源占用 > 视觉效果。

- 禁止为视觉效果引入 Blur / Transparency / Shadow / Animation / 动态背景 / 实时图表 / WebView / Electron
- 禁止默认轮询设备（不做“每秒 show interface status”）
- 所有阻塞操作（Serial、Telnet、CLI、Excel 导入、备份、Show 查询）必须异步
- 页面按需初始化，切换页面复用 ViewModel
- CLI 采用「后台接收 → Buffer → 批量 UI 更新」，必须有最大显示缓冲

## 8. 最终验收场景（V0.1）

1. 打开软件 → 交换机地址簿 → 选择目标交换机 → 自动填充 Telnet IP → 连接 → 进入 CLI
2. Console 接入 → 进入 CLI → `show vlan` → `show interface status`
3. 选择多个端口 → Access VLAN → 生成 CLI → Command Preview → 确认 → 执行 → 显示结果
4. LLDP → 查看邻居 → 查看管理 IP → Telnet 到邻居 → 连接下一台交换机
5. 输入 `200` → 得到 VLAN / Gateway / Mask / IP Range / Location
6. 输入 IP → 得到 VLAN / Gateway / Mask / Location
7. 执行配置前：Backup Running Config → 保存 → Command Preview → Execute
