---
name: process
description: Luồng làm việc BIM_KB_Base (HAWEE) cho repo add-in Revit có hawee.project.yaml — bước 1 → 10, cổng G1–G6, tier T0–T3. Dùng khi bắt đầu hoặc tiếp tục bất kỳ task/issue/bug nào trong repo đó, hoặc khi không rõ bước tiếp theo.
user-invocable: false
---

# Luồng HAWEE — bản ngắn

Quy trình nằm trong tool MCP `hawee-flow`, không nằm trong tài liệu. **Không đọc** `core/process/*.md`, `general.md`,
mẫu spec/plan của BIM_KB_Base trừ khi tool trả về đúng mục cần đọc.

1. Đầu phiên / khi không chắc: `flow_next` (task trống = task gần nhất). Làm đúng các dòng nó trả, dừng ở dòng "Dừng".
2. Task mới: `task_open` (tạo folder, spec/plan dựng sẵn theo tier, context.md). Bug: `task_open bug=true` rồi `/bug`.
3. Rule: `rule_for` (theo từ khoá / file sắp sửa), `rule_get <ID>` khi cần chi tiết. KB / known-issue: `kb_search`.

Ba luật nền:
- **Agent làm** (khảo sát, hỏi, viết spec/plan, code, test, tổng hợp) · **máy kiểm** (tool, lint, hook) · **người quyết ở cổng**.
- Tới cổng thì **dừng**, trình bằng chứng. Không tự ghi cổng (`gate_record` chỉ khi người đã nói rõ quyết định; hook hỏi lại).
- Không tự phát minh business rule (P-001): thiếu căn cứ → `UNKNOWN – NEED HUMAN DECISION: <câu hỏi> – người quyết định: <vai>` và dừng.

Không commit / push khi chủ task chưa nói rõ cho lần này (G4). Không sửa `protected_paths`. Không nới test hay thêm
`known_failures` để qua vòng. Mọi khẳng định hiện trạng có `file:dòng` hoặc lệnh đã chạy.
Kết quả tool là bản tóm tắt; cần chi tiết thì gọi lại với tham số chi tiết thay vì mở file lớn.
