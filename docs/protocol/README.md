# 60A1 协议文档索引

- 版本称谓统一为 **60A1**（历史提法 60CN-ACT1 等已收敛；`*-60cn.md` 文件名暂保留原名）。
- 权威层级：帧层约定（packet-frame-crypto）→ 各系统 `packet-detail-*.md` 分册 → 枚举状态索引（client-packet-enums）。
- 登记归属：包格式一律进对应 `packet-detail` 分册（暂存文档 new-packet-formats-60cn.md 已收敛停用）；函数/地址回填 `client-function-register-60cn.md`；协议行为修复记录进根目录 `CHANGELOG.md`。

## 帧层与加密

| 文档 | 定位 |
| --- | --- |
| [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md) | 帧层权威：12B/6B 帧头、LCG 密钥调度、CHECK_CONNECTION 例外、字节示例约定 |
| [client-send-crypto-analysis-60cn.md](client-send-crypto-analysis-60cn.md) | 发包加密实证附录：密钥调度推导、157 帧抓包回放、服务端实现建议 |
| [60a4-handshake-protocol.md](60a4-handshake-protocol.md) | 60A4 样本（另一版本客户端）入口握手；与 60A1 帧协议互不适用 |
| [confirm-reply-packet-format-60cn.md](confirm-reply-packet-format-60cn.md) | 回包格式确认方法论（五步法）；§5 公会组布局（当前唯一登记处）、§6 喇叭缺陷定位（历史留档） |

## 枚举与登记

| 文档 | 定位 |
| --- | --- |
| [client-packet-enums-60cn.md](client-packet-enums-60cn.md) | 全包状态索引：CMD↔NOTI 分组表 + 附录全表 + 版本差异备注 |
| [client-function-register-60cn.md](client-function-register-60cn.md) | 客户端函数/数据地址全项目登记表 |
| [ida-call-chain-convention.md](ida-call-chain-convention.md) | 调用链记录规范 |
| [client-event-system.md](client-event-system.md) | 活动系统（NOTI 69/86/120）逆向 |
| [client-camera-zoom-60cn.md](client-camera-zoom-60cn.md) | 镜头拉近（命中画面放大）逆向：选项 3 `ZOOM EFFECT` 开关 + 整画面缩放状态机；**纯客户端表现，无协议面** |

## 各系统分册（packet-detail-*）

[connection](packet-detail-connection-60cn.md)（连接/握手）· [login-character](packet-detail-login-character-60cn.md)（登录/选角，NOTI 2 mode 3 见组队分册）· [town-sync](packet-detail-town-sync-60cn.md)（城镇同步）· [dungeon](packet-detail-dungeon-60cn.md)（副本流程）· [party](packet-detail-party-60cn.md)（组队）· [chat](packet-detail-chat-60cn.md)（聊天，含喇叭 CMD 17 变体与 NOTI 130）· [item-inventory](packet-detail-item-inventory-60cn.md)（物品/背包）· [shop-currency](packet-detail-shop-currency-60cn.md)（商店/货币）· [compound-upgrade](packet-detail-compound-upgrade-60cn.md)（合成/强化）· [skill-quest](packet-detail-skill-quest-60cn.md)（技能/任务）· [mailbox](packet-detail-mailbox-60cn.md)（邮箱）· [creature](packet-detail-creature-60cn.md)（宠物）· [character-state](packet-detail-character-state-60cn.md)（角色状态）· [misc](packet-detail-misc-60cn.md)（未分组已实现包）· [new-packet-formats](new-packet-formats-60cn.md)（已收敛停用，保留作历史引用锚点）

## 对比与参照（ServerS4A21）

ServerS4A21 相关资料已统一迁至 [../servers4a21/](../servers4a21/README.md)（共有协议对比、功能回填方案与参考分册）。**仅作参考：两版协议与用法存在系统性差异，不得直接照搬；任何结论须经客户端主程序 IDA 数据库验证后方可写入本目录文档。**
