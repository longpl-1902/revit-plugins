# revit-plugins

Marketplace plugin Claude Code cho phát triển add-in Revit theo BIM_KB_Base.

**Phạm vi:** plugin chỉ dùng khi phát triển add-in (plugin) Revit — repo có `hawee.project.yaml` với `profiles` chứa
`revit-addin`. Repo khác: skill / agent không áp quy trình, tool `hawee-flow` từ chối, hook không can thiệp
(chi tiết: [Phạm vi](plugins/revit-dev-flow/README.md#phạm-vi--chỉ-khi-phát-triển-add-in-revit)).

| Plugin | Mô tả |
|---|---|
| [revit-dev-flow](plugins/revit-dev-flow/README.md) | Luồng bước 1–10 / cổng G1–G6 với ít token: MCP server `hawee-flow` (C# .NET 8) giữ trạng thái, dựng tài liệu, chạy lint/test, chạy suite Revit trọn vòng và chấm luật; skill mỏng; hook hỏi lại người ở cổng và commit/push. |

```text
/plugin marketplace add longpl-1902/revit-plugins
/plugin install revit-dev-flow@revit-plugins
```
