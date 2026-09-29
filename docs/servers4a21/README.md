# ServerS4A21 参考资料索引

> **定位:仅作参考,不可直接照搬。** ServerS4A21(下称 A21)是另一套服务端参考项目,目标客户端版本与本仓库不同;两版协议存在系统性差异——协议号漂移、载荷布局换代、部分用法取舍不同(逐项结论见 [comparison-vs-servers4a21.md](comparison-vs-servers4a21.md))。任何用于本项目的包号、字段布局与行为,一律以 `docs/protocol/` 下 60A1 逆向文档为准,并须经客户端主程序 IDA 数据库逐 case 验证(见 AGENTS.md「协议行为修复与新增处理的验证规则」)。

| 文档 | 定位 |
| --- | --- |
| [comparison-vs-servers4a21.md](comparison-vs-servers4a21.md) | 两版共有协议逐项对比(结论层:一致 / 版本性差异 / 实现分歧) |
| [feature-backfill-plan-vs-s4a21.md](feature-backfill-plan-vs-s4a21.md) | 未实现功能回填方案(行动层,继承对比结论,文档先行) |
| [servers4a21-00-overview.md](servers4a21-00-overview.md) | 参考服协议处理总览:帧结构 + 223 项 CMD 总表 + 分册导航 |
| [servers4a21-01…10](servers4a21-01-login-character.md) | 按系统拆分的参考分册(登录选角 / 背包 / 物品附魔时装 / 副本流程 / 副本特殊 / 城镇技能任务 / 社交组队交易 / 团战 PvP / 商店事件邮件 / 宠物佣兵副职) |
| [tools/](tools/) | 分册生成脚本与源数据(`build_dispatch_*.py`;源码位置经 `S4A21_ROOT` 环境变量传入,不写死本机路径) |

- 关联:schema 模式参照 [docs/sql/86S4A21_schema.sql](../sql/86S4A21_schema.sql)(与 [60A1 正式基线](../sql/60A1_schema.sql) 的取舍说明见 [design/README.md](../design/README.md) §08 条目)。
- 本目录文档中的 file:line 均为整理时点状态,仅作对照;A21 源码不在本仓库,行号不随其演进而更新。
