---
name: task-start
description: Bước 2 — mở task từ issue, gom ngữ cảnh bằng máy, đề xuất tier rồi dừng chờ người xác nhận. Chỉ repo add-in Revit.
argument-hint: <issueId> <slug> [--bug] [--tier T0|T1|T2|T3] [--cr <task gốc>]
disable-model-invocation: true
---

# /task-start $ARGUMENTS

> Chỉ dùng khi phát triển add-in Revit (repo có `hawee.project.yaml`, `profiles` có `revit-addin`). Repo / việc khác → báo người plugin này không áp dụng, không làm theo.

1. Đọc issue qua MCP issue tracker nếu có (Redmine), không có thì hỏi người dán nội dung + AC + đính kèm.
2. `task_open` với issue, slug, title (từ issue), tier (`--tier`, mặc định T2), bug, cr_of, keywords = tên phân hệ / lệnh / tool trong issue.
3. Đọc `context.md` vừa sinh. Chỉ đọc source cho câu context.md chưa trả lời; ghi tóm tắt issue vào spec §1 (đánh dấu *theo issue*).
4. Điền mục "Pre-flight còn thiếu" của context.md: version Revit đích, tính năng/màn hình, model thử, có đụng MCP, nhiều Revit đang chạy (PID) — có bằng chứng hoặc `chưa — cần <ai/gì>`.
5. Đề xuất tier theo bảng cuối context.md: liệt kê điều kiện thoả / không thoả. Nghi ngờ → tier cao hơn. Khác `--tier` → nói rõ.
6. **DỪNG** — trả lời người ≤ 6 dòng: tóm tắt, câu Q0 *"Tier đề xuất T<n> vì …; chủ đề `<slug>` (dùng lại / mới); đồng ý? (mặc định: …)"*, điều còn thiếu để làm spec. Bước sau: `/spec` (bug: `/bug`).

Không đọc toàn bộ source khi context.md đã chỉ bản đồ code / chuỗi chủ đề. Không hỏi điều tự tìm được.
