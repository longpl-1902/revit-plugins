---
name: review
description: Bước 9 — review đối kháng diff trước G5 bằng agent phiên riêng; ghi review-<ngày>.md.
argument-hint: "[task] [base_ref]"
disable-model-invocation: true
---

# /review $ARGUMENTS

1. `review_packet` (diff so nhánh tích hợp + checklist G5 + AC + plan §5/§5.6/§7/§13).
2. Giao agent `adversarial-reviewer` với đúng đường dẫn gói đó (không kèm ngữ cảnh phiên này).
3. Ghi kết quả vào `<task>/review-<ngày>.md`: bảng `| Mức | file:dòng | Phát hiện | Cách tái hiện/kiểm |` + đề xuất quyết định
   (PASS: không CRITICAL/HIGH · CONCERNS: còn MEDIUM/LOW hoặc HIGH có issue nối tiếp · FAIL: CRITICAL/HIGH mở → về bước 5, lệch lớn → G2/G1).
4. MR > ~400 dòng code → đề xuất tách theo bước plan; refactor tách khỏi đổi hành vi.
5. `gate_submit G5`. **DỪNG** — Tech Lead review + merge, ghi `/gate G5 … review_record=<file>`.
