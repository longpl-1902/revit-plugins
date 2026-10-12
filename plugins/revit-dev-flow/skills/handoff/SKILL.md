---
name: handoff
description: Bước 10 — đóng task: handoff có bằng chứng, known-issue, case vào suite, bài học lên KB.
argument-hint: "[task]"
disable-model-invocation: true
---

# /handoff $ARGUMENTS

1. `handoff_draft` → điền các ô `<…>`: §2 việc + file + bằng chứng (commit / lệnh + kết quả), §3 không làm tròn ("chưa kiểm trên Revit" nếu chưa),
   §4 câu mở có người quyết + hạn, **phát hiện tốn công**, **cố ý chưa làm**; §5 3–5 bước, lệnh đầu tiên; §6 bản đồ code `<file> <hash cũ> → <hash mới>`.
2. Known-issue mới (mẫu Base `known-issue.md`): Loại, Phát hiện bởi, Phòng ngừa (rule / lint / hook / analyzer), **Test hồi quy**, Lặp, KB.
   `ki_report` để thấy KI thiếu trường / quá hạn.
3. Case mới vào suite (nếu task thêm kịch bản Revit): đề xuất `rules.proposed.md`, không sửa luật đã duyệt.
4. Bẫy đúng cho dự án khác → `kb/inbox/<ngày>-agent-<chủ đề>.md`; lỗi lặp ≥ 2 lần hoặc mức high → đề xuất rule `draft` (không tự nâng trạng thái).
5. `check_all scope=full` (doc-lint gồm handoff-chain / handoff-map). Trả lời 5 dòng: trạng thái, đã / chưa kiểm, câu mở (ai), bước tiếp, đường dẫn handoff.
   Phát hành: G6 do QA + BIM Manager (`/gate G6 … release=<bản>`); không phát hành → người đặt done.
