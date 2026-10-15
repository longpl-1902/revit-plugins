---
name: dod
description: Bước 8 — Definition of Done bằng máy, dod-report.md, commit message nháp; dừng chờ G4. Chỉ repo add-in Revit.
argument-hint: "[task]"
disable-model-invocation: true
---

# /dod $ARGUMENTS

> Chỉ dùng khi phát triển add-in Revit (repo có `hawee.project.yaml`, `profiles` có `revit-addin`). Repo / việc khác → báo người plugin này không áp dụng, không làm theo.

1. `dod_report` (chạy check_all full + kiểm test hồi quy cho bug, MCP registry, bản đồ code; ghi dod-report.md; state commit).
2. FAIL do diff của task → sửa, chạy lại (≤ 5 vòng tổng ở bước 6). Hết vòng → dừng, báo lỗi nguyên văn + giả thuyết + việc đã thử.
3. Điền phần "MCP:" của commit nháp (tool đã đụng hoặc "không"); rà các dòng "Người kiểm" — dòng nào agent có bằng chứng thì ghi bằng chứng bên cạnh, còn lại để người tick.
4. **DỪNG** — trả lời người: dòng kết quả, mục FAIL (nếu có), commit message nháp, danh sách người kiểm.
   Chỉ commit / push khi chủ task nói rõ "commit" / "push" cho **lần này** (hook hỏi lại mỗi lần). Commit theo Conventional Commits, MR vào nhánh tích hợp, một MR một việc.
