# DFLegacy 后台管理 API

本文档对应当前 `DFLegacy.Server` 源码，路由定义以
[`Program.cs`](src/DFLegacy.Server/Program.cs) 为准，数据校验和持久化行为以
[`JsonGameStore.cs`](src/DFLegacy.Server/JsonGameStore.cs) 为准。

## 1. 基本约定

### 1.1 服务地址

后台监听地址由 `server.json` 中的 `DFLegacy.Admin` 决定，默认值是：

```text
http://127.0.0.1:8081
```

下文用 `$baseUrl` 表示该地址：

```powershell
$baseUrl = 'http://127.0.0.1:8081'
```

### 1.2 安全边界

当前后台接口没有登录、令牌、权限或 CSRF 校验。默认只监听 `127.0.0.1`，设计目标是
本机管理及客户端内嵌页面调用。

不要把 `DFLegacy.Admin.Host` 直接改成 `0.0.0.0` 并暴露到不可信网络。确需远程管理时，
应在反向代理或防火墙层增加身份认证、来源限制和 HTTPS。

### 1.3 JSON、编码与时间

- 写接口使用 `Content-Type: application/json`。
- 普通 JSON 字段使用 `camelCase`。
- Avatar 剩余期限字段因为兼容历史数据，序列化名称固定为 `AvatarExpireAt`。输入匹配
  不区分大小写，但建议始终使用文档中的准确拼写。
- `sentAt`、`expiresAt` 和 Premium 返回的 `expiresAt` 都是 UTC Unix 秒。
- 枚举默认使用数字，而不是枚举名称。
- 角色路由中的 `{characterId}` 是角色持久化 GUID，即 `GET /api/accounts` 返回的
  `characters[].id`，不是 `characterNo`、角色槽位或账号 UID。

通用 PowerShell 请求形式：

```powershell
$body = @{
    example = 'value'
} | ConvertTo-Json -Depth 10

Invoke-RestMethod `
    -Method Post `
    -Uri "$baseUrl/api/example" `
    -ContentType 'application/json; charset=utf-8' `
    -Body $body
```

非法 JSON、字段类型溢出或无法绑定请求模型时，ASP.NET Core 会直接返回 `400`。

## 2. 接口总览

| 方法 | 路径 | 用途 |
|---|---|---|
| `GET` | `/health` | 最小存活检查 |
| `GET` | `/api/status` | 服务、监听端口、随机源和 PVF 缓存状态 |
| `GET` | `/api/sessions` | 当前网络会话 |
| `GET` | `/api/packets` | 最近的协议包记录 |
| `GET` | `/api/accounts` | 账号及全部角色持久化数据 |
| `POST` | `/api/client-pay/cera` | 给账号增加点券 |
| `POST` | `/api/client-pay/diamond` | 给账号开通 30 天黑钻 |
| `POST` | `/api/client-pay/premium` | 给账号开通黑钻、霸王契约或达人契约 |
| `POST` | `/api/accounts` | 创建账号 |
| `PUT` | `/api/accounts/{userName}/password` | 使用旧密码修改密码 |
| `POST` | `/api/accounts/{userName}/password/reset` | 使用 QQ 恢复信息重置密码 |
| `POST` | `/api/accounts/{userName}/characters` | 创建角色 |
| `PUT` | `/api/characters/{characterId}/fatigue` | 修改角色当天已消耗疲劳 |
| `GET` | `/api/characters/{characterId}/mail` | 查询角色有效邮件 |
| `POST` | `/api/characters/{characterId}/mail` | 给角色发送邮件 |
| `POST` | `/api/characters/{characterId}/message` | 给在线角色发送聊天栏通知 |
| `POST` | `/api/characters/{characterId}/popup` | 给在线角色发送自定义弹窗 |

`/client`、`/client/`、`/client/client_pay` 和 `/client/client_diamond` 是客户端内嵌管理
页面入口或重定向，不是 JSON API。

## 3. 状态与诊断

### 3.1 `GET /health`

返回 `200`：

```json
{
  "status": "ok",
  "service": "dflegacy-emulator"
}
```

该接口只说明 ASP.NET 管理端口可响应，不代表入口 TCP、游戏 UDP 或 PVF 已全部正常。
完整检查使用 `/api/status`。

### 3.2 `GET /api/status`

主要返回字段：

| 字段 | 含义 |
|---|---|
| `startedAt` | 本次服务启动时间 |
| `totalConnections` | 启动以来累计连接数 |
| `totalPackets` | 启动以来累计接收包数 |
| `activeSessions` | 当前活动网络会话数量 |
| `entrance` | 入口 TCP 监听地址 |
| `characterDatagram` | 角色 UDP 监听地址或 `disabled` |
| `gameplayDatagram` | 游戏 UDP 主、副监听地址 |
| `gameProbe` | Probe 状态 |
| `random` | RDSEED、系统 CSPRNG 和回退源状态及计数 |
| `channelScriptConfigured` | PVF 是否存在 `etc/channel_info.etc` |
| `cachedItems` | 启动时缓存的物品定义数量 |

### 3.3 `GET /api/sessions`

返回当前会话数组。单个对象字段为：

```json
{
  "id": "会话 GUID",
  "service": "Entrance",
  "remoteEndpoint": "127.0.0.1:12345",
  "connectedAt": "2026-08-15T20:00:00+08:00",
  "receivedPackets": 10,
  "sentPackets": 12,
  "lastProtocolId": 15
}
```

### 3.4 `GET /api/packets`

返回最近最多 400 条包记录，顺序是服务端记录顺序。单条字段包括：

```json
{
  "timestamp": "2026-08-15T20:00:00+08:00",
  "sessionId": "会话 GUID",
  "service": "Entrance",
  "direction": "RX",
  "type": 1,
  "protocolId": 46,
  "totalLength": 20,
  "recordCount": 1,
  "declaredCrc32": 0,
  "hasValidCrc32": true,
  "bodyHex": "...",
  "bodyTruncated": false
}
```

`bodyHex` 的最大记录长度受 `server.json` 的 `HexDumpLimit` 控制；若未开启协议记录，
该接口不会凭空补采历史包。

### 3.5 收集其他测试者的包记录

优先在运行服务端的机器上收集，而不是去客户端 `Logs` 目录寻找。当前服务端提供的是已经
进入协议处理层的结构化记录：TCP 记录包含解析后的包字段和 body/payload，UDP 记录包含
收到或发出的 datagram 字节。它不是 Wireshark `.pcap`，也不会自动永久保存。

收集前检查实际运行目录的 `server.json`：

```json
{
  "DFLegacy": {
    "EnablePacketTracing": true,
    "HexDumpLimit": 4096
  }
}
```

- `EnablePacketTracing` 必须为 `true`。
- `HexDumpLimit` 是每条记录最多保留的字节数。默认 `64` 很容易截断较大的角色、背包、
  商城或邮件包。普通单命令复现可先使用 `4096`；涉及大型状态包时可使用 `65536`。
- 增大该值会同时增加内存和控制台日志体积。不要长期把它无条件设置成 4 MiB 最大包长；
  应在短复现后检查 `bodyTruncated`，仅在仍被截断时继续提高。
- 配置在服务启动时读取，修改后应重启服务端再复现。

复现流程：

1. 最好先重启一次测试服务端，使内存包环形队列从空状态开始。当前没有清空包队列的 HTTP
   接口。
2. 让测试者登录，并保持角色和服务端都不要退出。
3. 记录测试者账号、角色名、准确时间和单一操作，例如“21:05:12 右键使用 7518”。
4. 操作完成后立即导出 `/api/sessions` 和 `/api/packets`，不要先关闭客户端或重启服务端。
5. 一并保存本次实际使用的 `server.json`；如果服务端标准输出被重定向到文件，再附上对应
   的标准输出日志。

导出命令：

```powershell
$baseUrl = 'http://127.0.0.1:8081'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$captureDir = Join-Path $PWD "packet-capture-$stamp"
New-Item -ItemType Directory -Path $captureDir | Out-Null

Invoke-WebRequest `
    -Uri "$baseUrl/api/sessions" `
    -OutFile (Join-Path $captureDir 'sessions.json')
Invoke-WebRequest `
    -Uri "$baseUrl/api/packets" `
    -OutFile (Join-Path $captureDir 'packets.json')
Copy-Item -LiteralPath '.\server.json' -Destination $captureDir
```

`/api/packets` 只保留最近 400 条，服务端重启后清空。流量较大时旧记录会很快被覆盖，
因此应缩短复现步骤并立即导出。`sessionId` 用来关联 `/api/packets` 和 `/api/sessions`；
`/api/sessions` 中的 `remoteEndpoint` 可区分不同测试者。会话断开后会从 `/api/sessions`
消失，所以不能等测试者退出后再做映射。

服务端本身只配置控制台日志，不会自动选择一个固定 `.log` 文件。使用 PowerShell 启动时，
可以显式保留长期日志：

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Start-Process `
    -FilePath '.\DFLegacy.Server.exe' `
    -WorkingDirectory $PWD `
    -WindowStyle Hidden `
    -RedirectStandardOutput ".\DFLegacy.Server-$stamp.stdout.log" `
    -RedirectStandardError ".\DFLegacy.Server-$stamp.stderr.log"
```

控制台/重定向日志会记录 TCP 包方向、会话 ID、类型、协议号、长度和十六进制 body，并包含
业务日志；其十六进制内容同样受 `HexDumpLimit` 限制。客户端自身的 `DF/Logs`、`DNF.trc`
或 `rs_log.txt` 可作为崩溃和客户端行为辅助材料，但不能替代服务端 `/api/packets`。

如果问题必须保留完整原始 IP/TCP/UDP 帧、传输头、重传或时序，则应另外在服务端机器使用
Wireshark/Npcap 生成 `.pcapng`。本机客户端连接 `127.0.0.1` 时需要选择 Npcap Loopback
Adapter；这种原始抓包应与上述服务端结构化记录一起提供。

## 4. 账号与角色

### 4.1 `GET /api/accounts`

返回 `PublicAccountRecord[]`。每个账号包含：

- `id`：账号存储 GUID。
- `userName`：账号名。
- `accountUid`：游戏协议使用的账号 UID。
- `kind`：账号 UID 分类枚举数字。
- `cash`：账号级点券。
- `characters`：完整角色记录，包括属性、金币、背包、装备、仓库、Avatar、宠物、
  技能、任务和邮件。

该接口不会返回密码哈希、QQ 恢复信息或账号 Premium 列表，但角色数据体积可能很大。

查找指定角色 GUID：

```powershell
$accounts = Invoke-RestMethod -Uri "$baseUrl/api/accounts"
$character = $accounts.characters |
    Where-Object name -eq 'Berserker' |
    Select-Object -First 1
$character.id
```

角色名在全服范围内唯一，但自动化仍应先取得 GUID，再调用角色接口。

### 4.2 `POST /api/accounts`

请求：

```json
{
  "userName": "account01",
  "password": "password123",
  "testAccount": false,
  "oicqCode": "123456789"
}
```

规则：

- `userName` 去除首尾空白后必须为 4～32 个字符，大小写不敏感且不可重复。
- `password` 必须为 6～128 个字符。
- `oicqCode` 必须非空且不超过 32 个字符。
- `testAccount` 默认为 `false`。普通账号创建后没有角色；测试账号会创建当前代码定义的
  预置角色，并使用测试账号 UID 段。
- 新账号初始账号级点券为 50,000。

成功返回 `201` 和公开账号对象；账号已存在返回 `409`；其他校验失败返回 `400`。

### 4.3 `PUT /api/accounts/{userName}/password`

请求：

```json
{
  "oldPassword": "password123",
  "newPassword": "new-password-123"
}
```

- 成功：`200`。
- 账号不存在或旧密码错误：`401`。
- 新密码不满足 6～128 个字符等输入错误：`400`。

响应中的 `failure` 是数字：`0=None`、`1=InvalidRequest`、
`2=InvalidCredentials`、`3=InvalidRecoveryInformation`。

### 4.4 `POST /api/accounts/{userName}/password/reset`

请求：

```json
{
  "oicqCode": "123456789",
  "newPassword": "new-password-123"
}
```

`oicqCode` 与持久化值做区分大小写的精确比较。成功返回 `200`，账号或 QQ 不匹配返回
`401`，输入错误返回 `400`。

### 4.5 `POST /api/accounts/{userName}/characters`

请求：

```json
{
  "name": "NewCharacter",
  "job": 0,
  "growType": 2,
  "level": 1
}
```

默认值为 `job=0`、`growType=2`、`level=60`。规则：

- 角色名去除首尾空白后为 1～20 个字符，并且全服大小写不敏感唯一。
- 等级为 1～60。
- 每个账号最多 24 个可见角色。
- 当前后台接口没有校验 `job` 与 `growType` 的合法组合。管理端必须使用目标客户端支持的
  职业和转职编号，不应把任意整数写入生产数据。

成功返回 `201` 和完整角色对象；失败返回 `400`。

### 4.6 `PUT /api/characters/{characterId}/fatigue`

请求：

```json
{
  "usedFatigue": 100
}
```

`usedFatigue` 表示“当天已经消耗的疲劳”，不是剩余疲劳。允许值为 `0～188`：

- 普通账号上限为 156，客户端剩余值通常按 `156 - usedFatigue` 显示。
- 黑钻账号上限为 188，客户端剩余值通常按 `188 - usedFatigue` 显示。
- 设置 `0` 表示当天未消耗疲劳；设置为相应上限表示疲劳耗尽。

成功返回 `200` 和更新后的角色；角色不存在返回 `404`；超过 188 返回 `400`。该接口
只持久化数据，没有主动向已在线客户端推送疲劳刷新包，联调时应重新登录或触发完整状态同步。

## 5. 邮件接口

邮件是后台发放金币和物品的主要接口。本节中的字段语义直接对应
`CreateCharacterMailRequest`、`CharacterMailAttachmentRecord`、邮件领取规划器和 DF2008
邮件包构造。

### 5.1 查询邮件

```http
GET /api/characters/{characterId}/mail
```

成功始终返回 `200` 和邮件数组；角色不存在时当前实现返回空数组，而不是 `404`。

单封邮件结构：

```json
{
  "id": 123456,
  "sender": "DFLegacy",
  "text": "测试邮件",
  "gold": 5000,
  "attachment": {
    "itemId": 3037,
    "countOrValue": 50,
    "state": 0,
    "durability": 0,
    "sealState": 0,
    "AvatarExpireAt": null,
    "avatarAbilityIndex": null,
    "equipmentQualitySeed": null
  },
  "sentAt": 1786795200,
  "expiresAt": 1789387200,
  "isNew": true,
  "state": 1
}
```

状态含义：

| 字段/值 | 含义 |
|---|---|
| `isNew=true` | 尚未向客户端发送并确认新邮件提醒 |
| `state=1` | 普通收件箱邮件 |
| `state=2` | 客户端邮件状态 2，仍属于收件箱 |
| `state=3` | 已归档到邮件保管箱 |

普通邮件创建后有效期固定为 30 天。已归档的 `state=3` 邮件不受该到期时间过滤。领取
金币和附件后，服务端把邮件的 `gold` 清零、`attachment` 置空并保留正文；玩家随后可以
归档或删除纯文本邮件。

### 5.2 发送邮件

```http
POST /api/characters/{characterId}/mail
```

基本请求：

```json
{
  "sender": "DFLegacy",
  "text": "邮件正文",
  "gold": 0,
  "attachment": null
}
```

规则：

- `sender` 去除首尾空白后必须为 1～20 个 .NET 字符。
- `text` 最多 255 个 .NET 字符，可以是空字符串。
- 邮件字段最终按 CP936 发给 DF2008。HTTP 创建接口不会像在线消息接口那样拒绝 CP936
  不可表示字符，而是可能在客户端显示为 `?`；后台文案应只使用 CP936 可表示字符。客户端
  发包为 `sender` 预留 29 字节、为 `text` 预留 511 字节，较长中文发送人名称会被截断。
- `gold` 为 `0～2147483647`。
- `attachment` 可以为 `null`；金币和附件可以同时存在。
- DF2008 每封邮件最多一个物品附件，因此 `attachment` 是单个对象，不是数组。要发送
  多种物品必须调用多次接口；同一种可堆叠物品则用一个附件的 `countOrValue` 表示总数量，
  不要为 100 个同类消耗品发送 100 封邮件。
- 当前有效邮件总数最多 255 封；归档邮件也计入该上限。到期且未归档的邮件会在创建新邮件
  时清理。
- 邮件创建成功后有效期固定为 30 天，并持久化为 `isNew=true`、`state=1`。
- 角色在线时服务端会排队发送新邮件提醒；角色离线时会在下一次进入角色或打开邮箱时同步。
- 创建成功只表示邮件已持久化，不表示附件已经放入背包。玩家领取时才做 PVF 分类、物品栏
  空间、堆叠、负重和物品有效性检查。领取失败时附件继续留在邮件中。

成功返回 `201`、`Location: /api/characters/{characterId}/mail/{mailId}` 和创建后的邮件。
任何业务错误当前统一返回 `400`，包括角色不存在、邮箱已满和基础字段非法。

#### 5.2.1 附件字段

```json
{
  "itemId": 3037,
  "countOrValue": 50,
  "state": 0,
  "durability": 0,
  "sealState": 0,
  "AvatarExpireAt": null,
  "avatarAbilityIndex": null,
  "equipmentQualitySeed": null
}
```

| 字段 | 类型 | 含义 |
|---|---|---|
| `itemId` | `ushort` | PVF 中的装备或堆叠物 ID，必须非零 |
| `countOrValue` | `uint` | 堆叠物数量；非堆叠装备和 Avatar 必须写 `1` |
| `state` | `byte` | 普通装备强化/再封装复合字节；普通物品写 `0` |
| `durability` | `ushort` | 普通装备当前耐久；堆叠物和 Avatar 写 `0` |
| `sealState` | `byte` | `0` 为未封装，`1` 为封装状态 |
| `AvatarExpireAt` | `uint?` | 历史字段名，实际表示 Avatar 剩余秒数；`0` 为无限期 |
| `avatarAbilityIndex` | `ushort?` | `[avatar select ability]` 的零基选项索引 |
| `equipmentQualitySeed` | `uint?` | 普通装备品级种子；Avatar 和堆叠物不使用 |
| `instanceId` | `Guid` | 服务端非堆叠装备实例身份；后台调用方应省略 |

HTTP 创建接口只检查 `itemId` 和 `countOrValue` 非零，不会在返回 `201` 前拒绝未知 PVF ID，
也不会替调用方推断封装、耐久或 Avatar 选项。未知物品会在客户端领取时失败。因此后台应先
从与服务端相同版本的 `Script.pvf` 确认物品 ID、类型和相关标签。

非堆叠 `.equ` 的 `instanceId` 会由服务端自动生成 UUIDv7 并出现在创建响应中。不要手工
复用一个 `instanceId`；省略该字段是正确做法。

#### 5.2.2 发送消耗品

消耗品必须是 `stackable/stackable.lst` 中存在的 `.stk`，并由 PVF 的
`[stackable type]` 分类为消耗品。服务端领取时会：

1. 先合并到同 ID、同状态且尚未达到 `[stack limit]` 的已有堆叠。
2. 再在消耗品分类栏创建新堆叠。
3. 分类栏不足时再使用快捷栏空位。
4. 任一数量无法放入或加入后超过负重时，整封邮件领取失败，不会只领取一部分。

发送 100 个 ID `7518` 消耗品：

```powershell
$characterId = '替换为角色 GUID'
$body = @{
    sender = 'DFLegacy'
    text = '发放 100 个疲劳恢复药剂。'
    gold = 0
    attachment = @{
        itemId = 7518
        countOrValue = 100
        state = 0
        durability = 0
        sealState = 0
    }
} | ConvertTo-Json -Depth 10

Invoke-RestMethod `
    -Method Post `
    -Uri "$baseUrl/api/characters/$characterId/mail" `
    -ContentType 'application/json; charset=utf-8' `
    -Body $body
```

不要为 `.stk` 设置 `AvatarExpireAt`、`avatarAbilityIndex`、`equipmentQualitySeed` 或
`instanceId`。

#### 5.2.3 发送普通装备

普通装备必须是 `equipment/equipment.lst` 中存在、且不属于 Avatar 或 Creature 空间的
`.equ`。推荐显式提供以下信息：

- `countOrValue=1`。每件装备是独立实例；多件装备使用多封邮件。
- `state=0` 表示未强化、未再封装。低 5 位是强化等级，高 3 位是已完成再封装次数：
  `state = (resealCount << 5) | reinforcementLevel`。例如 `+5` 且没有再封装时为 `5`。
- `durability` 是当前真实耐久。`0` 是已经损坏，不是“使用默认满耐久”。应从装备脚本
  `[durability]` 读取满耐久后显式传入。超过 PVF 满耐久的值在领取时会被截到上限；脚本
  本身没有 `[durability]` 的首饰、称号等装备才使用 `0`。
- `[attach type] [sealing]` 的新装备必须传 `sealState=1`；第一次成功穿戴后服务端会把它
  改为 `0`。`[free]`、`[trade]` 等非封装装备传 `0`。后台邮件接口不会自动推断该字段。
- 推荐显式传 `equipmentQualitySeed=0`，表示固定中等品级。`1～999999997` 是客户端品级
  随机种子范围，`999999998` 是顶级品级种子。若省略，兼容逻辑会回退使用
  `countOrValue`，不适合作为管理端默认行为。称号装备在发包时固定使用中等品级种子。
- `AvatarExpireAt` 和 `avatarAbilityIndex` 对普通装备应省略。

发送 ID `18012` 的棉布短靴。该 PVF 装备是 `[free]`，脚本满耐久是 `18`：

```powershell
$characterId = '替换为角色 GUID'
$body = @{
    sender = 'DFLegacy'
    text = '发放棉布短靴。'
    gold = 0
    attachment = @{
        itemId = 18012
        countOrValue = 1
        state = 0
        durability = 18
        sealState = 0
        equipmentQualitySeed = 0
    }
} | ConvertTo-Json -Depth 10

Invoke-RestMethod `
    -Method Post `
    -Uri "$baseUrl/api/characters/$characterId/mail" `
    -ContentType 'application/json; charset=utf-8' `
    -Body $body
```

若物品脚本是 `[attach type] [sealing]`，只把上例的 `sealState` 改为 `1`；不要根据
`[free]`、`[trade]`、`[sealing]` 的枚举编号直接填写该字段。`sealState` 是实例当前是否
封装的状态，只使用 `0/1`。

#### 5.2.4 发送 Avatar 装扮装备

Avatar 装扮也是 `.equ`，但 PVF 的 `[equipment type]` 是 `hat avatar`、
`hair avatar`、`face avatar`、`coat avatar`、`pants avatar`、`shoes avatar`、
`breast avatar`、`waist avatar` 或 `skin avatar`。领取时服务端把它放入独立 Avatar
空间，不进入普通装备栏。

Avatar 必须这样构造：

- `countOrValue=1`。
- `state=0`、`durability=0`。
- `sealState` 仍由物品实例状态决定；常见 `[trade]` Avatar 写 `0`，如果脚本确实是
  `[sealing]` 且要发未穿戴新品，则写 `1`。
- 必须显式写 `AvatarExpireAt=0` 表示无限期。这个字段名虽然带 `ExpireAt`，DF2008
  客户端实际按“剩余秒数”读取；不要传 Unix 时间戳。若省略，邮件列表的兼容投影可能把
  `countOrValue=1` 当作剩余秒数，造成客户端显示即将到期。
- 必须显式写 `avatarAbilityIndex`。索引从 `0` 开始；`0` 表示装备脚本
  `[avatar select ability]` 中第一行属性。
- 不设置 `equipmentQualitySeed`，并省略 `instanceId`。

发送永久、选择第一行属性的 ID `41866` Avatar 鞋：

```powershell
$characterId = '替换为角色 GUID'
$body = @{
    sender = 'DFLegacy'
    text = '发放永久 Avatar 装扮。'
    gold = 0
    attachment = @{
        itemId = 41866
        countOrValue = 1
        state = 0
        durability = 0
        sealState = 0
        AvatarExpireAt = 0
        avatarAbilityIndex = 0
    }
} | ConvertTo-Json -Depth 10

Invoke-RestMethod `
    -Method Post `
    -Uri "$baseUrl/api/characters/$characterId/mail" `
    -ContentType 'application/json; charset=utf-8' `
    -Body $body
```

当前 Avatar 背包容量是 105 个实例槽。空间已满时邮件领取失败，附件保留在邮件中。

#### 5.2.5 同时发送金币和物品

单封邮件可以同时携带金币和一个附件：

```json
{
  "sender": "DFLegacy",
  "text": "无色小晶块与金币。",
  "gold": 5000,
  "attachment": {
    "itemId": 3037,
    "countOrValue": 50,
    "state": 0,
    "durability": 0,
    "sealState": 0
  }
}
```

金币和附件按同一次领取提交。若金币会使角色金币超过 `Int32.MaxValue`，或附件无法放入，
整次领取失败，邮件内容保持不变。

#### 5.2.6 邮件功能边界

当前 HTTP 后台只提供查询和创建邮件，不提供后台领取、归档、删除或修改邮件接口。领取、
状态 2/3 切换和删除由在线客户端对应的游戏协议处理。

## 6. 点券与 Premium

点券和 Premium 服务都是账号级数据。接口成功后会更新持久化账号，并尽可能通知该账号下
当前在线的角色。

### 6.1 `POST /api/client-pay/cera`

请求：

```json
{
  "account": "test",
  "amount": 10000
}
```

规则：

- `account` 去除首尾空白后为 1～32 个字符。
- `amount` 必须是正整数且为 100 的倍数。
- 账号必须存在并至少有一个角色。
- 点券增加到账号当前值，溢出时饱和到 `Int32.MaxValue`。

成功返回 `200`；失败返回 `400`。返回的 `amount` 是本次增加量，不是增加后的余额；需要
读取 `/api/accounts` 的 `cash` 查看当前账号余额。

### 6.2 `POST /api/client-pay/diamond`

请求：

```json
{
  "account": "test",
  "days": 30
}
```

当前只接受 `days=30`。已有有效期时从原到期时间继续延长，否则从当前时间开始。首次从未
开通状态变为有效黑钻时，会把账号下所有处于虚弱状态的角色恢复到 100%。成功返回 `200`，
失败返回 `400`。

### 6.3 `POST /api/client-pay/premium`

请求：

```json
{
  "account": "test",
  "serviceType": 22,
  "days": 7
}
```

支持的服务类型：

| `serviceType` | 服务 | 天数规则 |
|---:|---|---|
| `16` | 黑钻 | 必须为 30 天 |
| `22` | 霸王契约 | 任意正整数天数 |
| `27` | 达人契约 | 任意正整数天数 |

有效服务会从当前到期时间累加；已过期服务从当前时间重新计算。成功返回 `200`，失败返回
`400`。响应中的 `expiresAt` 是 UTC Unix 秒，`remainingSeconds` 是本次写入后的剩余秒数。

## 7. 在线通知

### 7.1 `POST /api/characters/{characterId}/message`

请求：

```json
{
  "message": "测试消息",
  "messageType": 16,
  "targetAreaUserId": 0
}
```

规则：

- 角色必须在线，否则返回 `409`。
- `message` 必须非空、可由 CP936 编码，编码后长度必须小于 256 字节。
- `messageType` 必须是已定义的 `0～18`。
- `targetAreaUserId` 是客户端区域用户 ID，通常使用默认值 `0`。它不是角色 GUID。
- 省略 `messageType` 时默认为 `16`。

当前消息类型枚举：

| 值 | 服务端名称/客户端表现 |
|---:|---|
| `0` | `Type00DualRouteBrown` |
| `1` | `Type01LightGreen` |
| `2` | `Type02LightCyan` |
| `3` | `Type03White` |
| `4` | `Type04Brown` |
| `5` | `Type05White` |
| `6` | `Type06Magenta` |
| `7` | `Type07LightGreen` |
| `8` | `Type08Orange` |
| `9` | `Type09White` |
| `10` | `Type10White` |
| `11` | `Type11LightCyan` |
| `12` | `Type12Yellow` |
| `13` | `Type13SpeakerYellow` |
| `14` | `Type14SpeakerYellow` |
| `15` | `Type15SpeakerYellow` |
| `16` | `Type16Brown`，服务端自构建消息默认类型 |
| `17` | `Type17ClientFormattedNotice` |
| `18` | `Type18DirectOutput` |

成功返回 `200`：

```json
{
  "queued": true,
  "characterId": "角色 GUID",
  "messageType": 16,
  "targetAreaUserId": 0,
  "encodedLength": 8
}
```

### 7.2 `POST /api/characters/{characterId}/popup`

请求：

```json
{
  "message": "这是自定义提示弹窗。"
}
```

文本规则与 `/message` 相同：非空、CP936 可编码且编码后小于 256 字节。角色必须在线；
成功返回 `200`，离线返回 `409`，文本错误返回 `400`。

## 8. 常见错误

| 现象 | 原因与处理 |
|---|---|
| 邮件创建返回 `201`，客户端却无法领取 | `itemId` 不在当前 PVF、分类空间已满、快捷栏也满、超重，或装备/Avatar 字段不符合语义 |
| 可堆叠物被拆成很多邮件 | 调用方错误地按单个数量循环；应在一封邮件中设置总 `countOrValue` |
| 装备领取后耐久为 0 | 后台把 `durability` 省略或写成 0；0 是真实损坏状态，应传 PVF `[durability]` |
| `[sealing]` 装备收到后已解封 | 后台没有显式传 `sealState=1` |
| 普通装备品级异常 | 省略了 `equipmentQualitySeed`，兼容逻辑回退到 `countOrValue`；后台建议显式传 0 或合法种子 |
| Avatar 显示即将到期 | 没有显式传 `AvatarExpireAt=0`，或错误地传入了 Unix 到期时间 |
| Avatar 可选属性为空或不正确 | 没传 `avatarAbilityIndex`，或索引超出该 `.equ` 的 `[avatar select ability]` 行数 |
| 在线消息返回 `409` | `characterId` 对应角色当前不在线 |
| 中文消息返回 `400` | 文本含 CP936 无法表示的字符，或编码后达到 256 字节 |
| 设置疲劳后客户端没有立即变化 | 当前疲劳后台接口只保存数据，不主动推送在线状态刷新 |
