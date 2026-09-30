# docs 目录索引

- 本目录随仓库分发（根 `.gitignore` 中 `/docs` 条目已注释停用，历史约定详见 `.gitignore` 注释）。
- 各文档状态与结论冲突时以代码为真；协议行为修复与新增处理的记录统一进根目录 `CHANGELOG.md`；未完成、待真机复测、未定论与存在争议的事项统一进根目录 `PENDING.md`。

| 子目录 | 内容 | 索引 |
| --- | --- | --- |
| [design/](design/README.md) | 服务端结构性设计文档 00–09（可移植性重构、构建发布与 CI、协议层重构、SQLite 存储迁移、DAF-MCP 抓包网关） | [design/README.md](design/README.md) |
| [protocol/](protocol/README.md) | 60A1 客户端协议逆向分册（帧层加密、枚举状态索引、各系统包格式） | [protocol/README.md](protocol/README.md) |
| [servers4a21/](servers4a21/README.md) | ServerS4A21 参考服资料（共有协议对比、回填方案、参考分册与生成工具）——**仅作参考，不得直接照搬，结论须经 IDA 验证** | [servers4a21/README.md](servers4a21/README.md) |
| [sql/](sql/) | [86S4A21 模式参照 schema](sql/86S4A21_schema.sql) | 本项目的基线 schema 已随存储模块落到 [Server/DFLegacy.Server/Sqlite/act1_schema.sql](../Server/DFLegacy.Server/Sqlite/act1_schema.sql)（唯一定名来源）；原 `act1_schema.sql` / `data_schema.sql` / `state.json` 已随 JSON 存储移除 |
