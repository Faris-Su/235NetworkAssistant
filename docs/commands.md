# 锐捷 CLI 命令库（Source of Truth）

## 0. 规则

1. **所有 CLI 命令集中管理**，代码中唯一定义位置：
   - `Commands/ShowCommands.cs`：查看类命令
   - `Commands/VlanCommandGenerator.cs`、`PortCommandGenerator.cs`、`TrunkCommandGenerator.cs`：配置类命令生成
2. 禁止在 View / ViewModel / `Button_Click` / XAML 中出现 CLI 字符串。
3. **禁止凭猜测编造锐捷命令**。本文件中的命令全部来自项目已有资料：
   - 项目资料《锐捷交换机指令(1).pdf》（含“交换机基本配置”“锐捷交换机的指令”章节）
   - 项目提示词中列出的项目已有命令
4. 如果实际设备 CLI 与本文件不一致，**必须记录**：Device Model / Firmware Version /
   Actual CLI / Expected CLI / Difference，然后再更新本文件与命令库，不允许静默修改。
5. 设备型号/固件差异通过 `Commands/DeviceCapability.cs`（`DeviceCapability` + `CommandProfile`）预留开关；
   V0.1 默认按基础锐捷 CLI 打开全部能力。

## 1. 模式与基础操作

| 命令 | 说明 |
| --- | --- |
| `enable` | 进入特权模式 |
| `configure terminal` | 进入全局配置模式 |
| `exit` | 返回上一级 |
| `end` | 返回特权模式 |
| `write` / `write memory` / `copy running-config startup-config` | 保存配置 |
| `show running-config` | 查看当前生效配置 |
| `show version` | 查看硬件/软件版本 |
| `show clock` | 查看时钟 |
| `hostname <name>` | 配置设备名称 |

**本软件不自动执行保存类命令**（`write` / `write memory` / `copy running-config startup-config`），
保存配置必须是用户独立、明确的动作。

## 2. 查看类命令（ShowCommands.cs）

| 用途 | 命令 | 出处 |
| --- | --- | --- |
| 版本/硬件信息 | `show version` | 资料 |
| 当前配置 | `show running-config` | 资料 |
| 端口状态（状态/VLAN/双工/速率/介质） | `show interface status` | 资料 |
| 三层接口汇总 | `show ip interface brief` | 资料 |
| VLAN 列表 | `show vlan` | 资料 |
| 指定 VLAN | `show vlan id <id>` | 资料 |
| MAC 地址表 | `show mac-address-table` | 资料 |
| 动态 MAC 地址表 | `show mac-address-table dynamic` | 资料 |
| MAC 过滤查询 | `show mac-address-table \| in <片段>` | 资料 |
| 端口详细信息 | `show interface <port>` | 资料 |
| 端口模式/switchport 信息 | `show interface <port> switchport` | 资料 |
| 日志 | `show logging` / `show log` | 资料 |
| CPU 利用率 | `show cpu` | 资料 |
| ARP 表 | `show arp` | 资料 |
| 聚合端口信息 | `show aggregatePort summary` | 资料 |
| Trunk 端口 | `show int trunk` | 项目已有资料 |
| LLDP 邻居 | `show lldp neighbors` | 资料 |
| LLDP 邻居详情（含管理 IP） | `show lldp neighbors interface <port> detail` | 资料 |
| DHCP Snooping 绑定 | `show ip dhcp snooping binding` | 项目已有资料 |
| DHCP 已分配地址 | `show ip dhcp binding` | 资料 |

> 说明：资料中同一命令出现过 `show log` 与 `show logging` 两种写法（PDF 第 13 页 / 第 5 页）。
> 命令库统一使用 `show logging`，`show log` 记录在此以备设备差异。

## 3. 端口配置

| 操作 | 命令 | 出处 |
| --- | --- | --- |
| 进入单端口 | `interface gigabitethernet 0/1` | 资料 |
| 进入端口范围 | `interface range g0/1-3`、`interface range fa 0/1-2,0/5,0/7-9` | 资料 |
| 关闭端口 | `shutdown` | 资料 |
| 开启端口 | `no shutdown` | 资料 |
| 速率 | `speed {10 \| 100 \| 1000 \| auto}` | 资料 |
| 双工 | `duplex {auto \| full \| half}` | 资料 |
| 介质类型 | `medium-type {fiber \| copper}` | 资料 |

注意事项（来自资料）：

- 光口不能修改速率与双工，只能 `auto`
- 两端速率/双工不匹配会导致规律性丢包，排查时需要考虑

## 4. VLAN 配置

| 操作 | 命令 | 出处 |
| --- | --- | --- |
| 创建 VLAN | `vlan 100` | 资料 |
| VLAN 名称 | `name VLAN-Example` | 资料 |
| 删除 VLAN | `no vlan 888` | 资料 |
| 端口设为 Access | `switchport mode access` | 资料 |
| 端口划入 VLAN | `switchport access vlan 100` | 资料 |
| 端口恢复默认 VLAN | `no switchport access vlan` | 资料 |
| 端口设为 Trunk | `switchport mode trunk` | 资料 |

批量端口示例（资料原文）：

```
int ran g 0/2-9
switchport access vlan 200
```

## 5. Trunk 配置

| 操作 | 命令 | 出处 |
| --- | --- | --- |
| 设为 Trunk | `switchport mode trunk` | 资料 |
| 放通 VLAN | `switchport trunk allowed vlan add 200` | 资料 |
| 移除 VLAN | `switchport trunk allowed vlan remove 2-9,11-19,...` | 资料 |
| 取消 VLAN 修剪 | `no switchport trunk allowed vlan` | 资料 |
| Native VLAN | `switchport trunk native vlan 10` | 资料 |

V0.1 不做复杂 VLAN Matrix，只做 allowed vlan 的 add / remove 与 native vlan。

## 6. 其它资料中已有的命令（V0.1 不实现，仅登记以备后续版本）

聚合端口：`interface aggregateport 1`、`port-group 1`、`no port-group 1`、`show aggregatePort summary`

生成树：`spanning-tree`、`spanning-tree mode stp|rstp|mstp`、`spanning-tree priority 4096`

三层/SVI：`ip routing`、`no switchport`、`interface vlan 10` + `ip address`、`ip route`、`ip default-gateway`

DHCP：`service dhcp`、`ip dhcp pool`、`network`、`dns-server`、`default-router`、`lease`、
`ip dhcp excluded-address`、`client-identifier`、`host`、`show ip dhcp binding`、`ip helper-address`

ACL：`ip access-list standard|extended <name>`、`deny|permit`、`ip access-group <name> in|out`

端口安全：`switchport port-security`、`switchport port-security maximum 8`、
`switchport port-security mac-address <mac> ip-address <ip>`、`switchport port-security violation shutdown`

防 ARP 欺骗：`arp <ip> <mac> arpa <interface>`、`anti-ARP-Spoofing ip <ip>`

防 STP 攻击：`spanning-tree bpduguard enable|disable`

防 DOS：`ip deny spoofing-source`、`system-guard enable`

端口镜像：`monitor session 1 destination interface G0/2`、`monitor session 1 source interface G0/1 both`

VRRP：`standby <group> ip <vip>`、`standby <group> priority <n>`

远程登录：`line vty 0 4` + `password`；设备侧远程登录下一跳交换机：`tel <ip>`

> 上述命令登记在文档中，**不在 V0.1 命令库与界面中提供**，避免超出范围。

## 7. 接口名归一化规则

资料与实际环境同时存在 `Gi0/1`、`g0/1`、`GigabitEthernet0/1`、`Gi0/1-3`、`F0/1`、`fa 0/1-2` 等写法。

软件内部统一规则（`Helpers/InterfaceNameHelper.cs`）：

- 归一化为 `<前缀><槽位>/<端口>`，例如 `g0/1`、`f0/24`
- `gigabitethernet` / `gi` / `g` → `g`；`fastethernet` / `fa` / `f` → `f`
- 生成 range 时只压缩**同一前缀同一槽位**的连续端口：`g0/1,g0/2,g0/3,g0/5` → `g0/1-3,0/5`
- 跨槽位不合并，拆成多条 `interface range` 块，避免生成设备可能不支持的写法
- VLAN 列表压缩为 `10-12,15` 形式

## 8. 设备差异记录模板

```
Device Model:      S2652G-E
Firmware Version:  xxx
Actual CLI:        <设备实际接受的命令>
Expected CLI:      <命令库中的命令>
Difference:        <差异说明与处理方式>
Recorded By / At:  <记录人 / 日期>
```
