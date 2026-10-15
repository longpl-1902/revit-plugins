---
name: spec
description: Bước 3a–3c — spec làm cùng người qua gói câu hỏi; dừng ở mỗi gói và ở G1. Chỉ repo add-in Revit.
argument-hint: "[task] | trả lời gói: Q1: B, Q2: mặc định"
disable-model-invocation: true
---

# /spec $ARGUMENTS

> Chỉ dùng khi phát triển add-in Revit (repo có `hawee.project.yaml`, `profiles` có `revit-addin`). Repo / việc khác → báo người plugin này không áp dụng, không làm theo.

Gọi `flow_next brief=true` trước. Spec đã dựng sẵn theo tier (`spec.md`); chỉ điền nội dung, không chép lại mẫu.
Luôn: mọi khẳng định hiện trạng có `file:dòng`/log; không hỏi điều tự tìm được hoặc đã có trong decision log / §14
(`kb_search`, grep `docs/decisions/decision-log.md`); không viết "cách làm" vào spec.

**3a — khảo sát rồi hỏi**
1. Đọc `context.md`, issue, bản đồ code; đi luồng từ điểm vào tới chỗ ghi dữ liệu. Ghi spec §2 (bảng hiện trạng có bằng chứng, dùng lại được, KI/quyết định cũ).
2. Viết `questions/pack-1.yaml` — mẫu: [references/pack-format.md](references/pack-format.md). Có Q0 (tier + chủ đề, `sets: tier`). Gói ≤ giới hạn tier.
3. `pack_render` → gửi nguyên văn bản ngắn nó trả cho người. **DỪNG.**

**Có trả lời** → `pack_apply` (answers = nguyên văn người gõ, by = tên + vai). Đọc lại cho người phần "tôi hiểu là …".

**3b — kịch bản & AC**: §3 phạm vi, §4 K1–K9 + kịch bản bắt buộc của profile (trong context.md), §5 AC dạng EARS có "Đo bằng"
(chạy trong Revit: số đo + nguồn kỳ vọng; chưa đo được → ghi cần gì), §6 dữ liệu + điểm kiểm, §7 contract tool MCP, §8 phi chức năng có số.
Gói 2 chỉ hỏi điểm chưa chắc → `pack_render`. **DỪNG.** Trả lời → `pack_apply`.

**3c — chốt**: §9 rule (`rule_for`), §10–§12, §13 chỉ còn CHẶN. `doc_check spec` tới khi không còn CRITICAL/HIGH và lint sạch.
Rà độc lập theo tier: T1 = `doc_check` đủ; T2 = agent `independent-reviewer`; T3 = 2 lượt, một lượt ngoài phân hệ. Ghi kết quả §15.
`gate_submit G1` (máy kiểm câu CHẶN, doc_check). **DỪNG** — trình G1: 5 dòng tóm tắt, phát hiện còn lại, câu CHẶN + người quyết.
Người duyệt dùng `/gate G1 …`. Sau G1 → `/plan` (tự gọi `/test-design` trước 4a khi có AC đo bằng suite). AC chạy trong Revit ghi cột Đo bằng là `suite …` để máy nhận ra.

Không tự chọn khi yêu cầu mâu thuẫn code/rule/spec cũ — nêu mâu thuẫn, hỏi. Tối đa 3 gói; người nói "đủ rồi" → câu mở thành giả định (pack_apply tự làm), CHẶN vẫn dừng.
CR (`status.yaml.cr_of`): gói 1 có câu mức CR (nhỏ/lớn) + bảng mục spec đổi trước → sau; nhỏ: sửa spec gốc, Phiên bản +1, §15; lớn: spec mới `Thay cho`.
