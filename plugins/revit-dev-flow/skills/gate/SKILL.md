---
name: gate
description: Người duyệt ghi quyết định cổng G1–G6 cho task (status.yaml + decision log) và chuyển bước. Chỉ repo add-in Revit.
argument-hint: <G1..G6> <PASS|CONCERNS|FAIL|WAIVED> [task] [lý do / bằng chứng]
disable-model-invocation: true
---

# /gate $ARGUMENTS

> Chỉ dùng khi phát triển add-in Revit (repo có `hawee.project.yaml`, `profiles` có `revit-addin`). Repo / việc khác → báo người plugin này không áp dụng, không làm theo.

Người vừa gõ lệnh này là người duyệt. Gọi `gate_record` với:
- `gate`, `decision` lấy từ tham số; `task` nếu có (trống = task gần nhất);
- `by` = tên + vai người duyệt (hỏi nếu chưa biết; vai theo `gates` của hawee.project.yaml);
- `evidence` = link issue / MR / chat hoặc file đã xem; `reason` bắt buộc khi WAIVED;
- G2: hỏi "người sẽ code đã xác nhận đủ để code chưa?" → `coder_confirmed`; G3 có điều kiện → `conditional_on: <KI>`;
  G4 → `commits`; G5 → `review_record`; G6 → `release`.

Tool từ chối (thiếu lý do, còn câu CHẶN, G2 chưa xác nhận…) → báo nguyên văn, không thử cách khác.
Xong → trả một dòng kết quả của tool rồi `flow_next` cho bước kế.
