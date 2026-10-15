---
name: process
description: Luồng làm việc BIM_KB_Base (HAWEE) CHỈ khi phát triển add-in (plugin) Revit — repo có hawee.project.yaml với profiles chứa revit-addin. Bước 1 → 10, cổng G1–G6, tier T0–T3. Dùng khi bắt đầu hoặc tiếp tục task/issue/bug của add-in Revit trong repo đó, hoặc khi không rõ bước tiếp theo. KHÔNG dùng cho repo khác (không có hawee.project.yaml, profile khác như autocad-addin) hay việc không phải phát triển add-in Revit.
user-invocable: false
---

# Luồng HAWEE — bản ngắn

## Phạm vi — đọc trước
Quy trình này **chỉ** dùng khi phát triển add-in (plugin) **Revit**. Nhận biết: từ thư mục dự án đi lên có `hawee.project.yaml`
và `profiles` chứa `revit-addin`.
- Không thấy file đó, hoặc `profiles` không có `revit-addin` (vd. `autocad-addin`), hoặc việc không phải phát triển add-in
  Revit (script lẻ, tài liệu, repo khác mở cùng phiên…) → **không áp quy trình**: làm việc bình thường theo yêu cầu, không gọi
  tool `hawee-flow`, không đòi cổng / tier / suite, không gợi ý `/task-start`, `/spec`… .
- Tool `hawee-flow` trả "không phải dự án add-in Revit" / "Không thấy hawee.project.yaml" → dừng quy trình, báo người một dòng,
  rồi làm tiếp như bình thường (không tự tạo `hawee.project.yaml` để lách).
- Người gõ lệnh của plugin (`/spec`, `/plan`…) ở repo ngoài phạm vi → báo plugin chỉ dùng cho add-in Revit, không làm theo lệnh.

Quy trình nằm trong tool MCP `hawee-flow`, không nằm trong tài liệu. **Không đọc** `core/process/*.md`, `general.md`,
mẫu spec/plan của BIM_KB_Base trừ khi tool trả về đúng mục cần đọc.

1. Đầu phiên / khi không chắc: `flow_next` (task trống = task gần nhất). Làm đúng các dòng nó trả, dừng ở dòng "Dừng".
2. Task mới: `task_open` (tạo folder, spec/plan dựng sẵn theo tier, context.md). Bug: `task_open bug=true` rồi `/bug`.
3. Rule: `rule_for` (theo từ khoá / file sắp sửa), `rule_get <ID>` khi cần chi tiết. KB / known-issue: `kb_search`.

Ba luật nền:
- **Agent làm** (khảo sát, hỏi, viết spec/plan, code, test, tổng hợp) · **máy kiểm** (tool, lint, hook) · **người quyết ở cổng**.
- Tới cổng thì **dừng**, trình bằng chứng. Không tự ghi cổng (`gate_record` chỉ khi người đã nói rõ quyết định; hook hỏi lại).
- Không tự phát minh business rule (P-001): thiếu căn cứ → `UNKNOWN – NEED HUMAN DECISION: <câu hỏi> – người quyết định: <vai>` và dừng.

Kiểm trong Revit **chỉ** qua suite + `test_run` (`/revit-test`, `/verify`): runner tự mở Revit và bản sao model. Không gọi tool
Revit trực tiếp, không nhờ người mở Revit; thiếu tool hawee-flow → báo `/mcp` và dừng.

Không commit / push khi chủ task chưa nói rõ cho lần này (G4). Không sửa `protected_paths`. Không nới test hay thêm
`known_failures` để qua vòng. Mọi khẳng định hiện trạng có `file:dòng` hoặc lệnh đã chạy.
Kết quả tool là bản tóm tắt; cần chi tiết thì gọi lại với tham số chi tiết thay vì mở file lớn.
