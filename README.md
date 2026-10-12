# revit-plugins

Marketplace plugin Claude Code cho phát triển add-in Revit theo BIM_KB_Base.

| Plugin | Mô tả |
|---|---|
| [revit-dev-flow](plugins/revit-dev-flow/README.md) | Luồng bước 1–10 / cổng G1–G6 với ít token: MCP server `hawee-flow` (C# .NET 8) giữ trạng thái, dựng tài liệu, chạy lint/test, chạy suite Revit trọn vòng và chấm luật; skill mỏng; hook hỏi lại người ở cổng và commit/push. |

```text
/plugin marketplace add longpl-1902/revit-plugins
/plugin install revit-dev-flow@revit-plugins
```
