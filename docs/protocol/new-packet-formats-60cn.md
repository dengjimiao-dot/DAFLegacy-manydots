# 新实现/补全协议包格式速查(60A1)（已收敛，停止增长）

> **本文件不再登记新内容，仅作历史引用锚点保留。** 原登记内容已按仓库约定并入各系统分册：
>
> - §1 服务器喇叭/频道喇叭（CMD 17 变体、NOTI 130 广播、配套数据更新与服务端处理流程）→ [packet-detail-chat-60cn.md](packet-detail-chat-60cn.md) 的 CMD 17 与 NOTI 130 节；
> - NOTI 14 单条目格式 → [packet-detail-item-inventory-60cn.md](packet-detail-item-inventory-60cn.md)；
> - NOTI 126 弹窗变体、NOTI 12 → [packet-detail-chat-60cn.md](packet-detail-chat-60cn.md)。
>
> 代码注释与 CHANGELOG 中对本文档 §1/§1.1 的历史引用，一律以上述分册为准。
>
> 状态图例（✅/❌）与登记归属见 [client-packet-enums-60cn.md](client-packet-enums-60cn.md)；格式推导方法见 [confirm-reply-packet-format-60cn.md](confirm-reply-packet-format-60cn.md)；帧约定见 [packet-frame-crypto-60cn.md](packet-frame-crypto-60cn.md)。
