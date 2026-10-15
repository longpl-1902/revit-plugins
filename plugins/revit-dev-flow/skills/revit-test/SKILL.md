---
name: revit-test
description: Chạy một hoặc nhiều suite test Revit bằng runner hawee-flow (không vòng sửa); trả kết quả gọn. Chỉ repo add-in Revit.
argument-hint: "<suite> [<suite> …] [--purpose task|baseline|adhoc]"
disable-model-invocation: true
context: fork
model: sonnet
---

# /revit-test $ARGUMENTS

> Chỉ dùng khi phát triển add-in Revit (repo có `hawee.project.yaml`, `profiles` có `revit-addin`). Repo / việc khác → báo người plugin này không áp dụng, không làm theo.

Mỗi suite (tuần tự; suite trước lỗi vẫn chạy suite sau): `test_run suite=<tên> purpose=<…>` (mặc định adhoc; `baseline` khi lấy baseline ở bước 4a).
Revit phải đang đóng; không có tool `test_run` = hawee-flow chưa nối → báo người gõ `/mcp`, dừng (không nhờ người mở Revit / chạy tay).
Runner tự làm bước 0–10 (môi trường, Revit, bản sao model, step có chặn tool cấm, tracking + điểm kiểm, đối chiếu log, chấm, evidence, dọn dẹp).

Kết quả tool từng step: `get_detail kind=steps` (file `data\steps\NN-<tool>.json`). Sau mỗi suite: luật "cần agent" (ảnh, cú pháp máy không đọc được, hoặc bộ chấm C# lệch `rule-eval.ps1` — dòng `rule-eval: lệch …`) → `get_detail kind=rule`, xem ảnh / dữ liệu, `record_verdict`. Không tự sửa code ở lệnh này (vòng sửa thuộc `/verify`).
Trả lời: bảng suite · kết luận test-summary · số PASS / FAIL / cần người · đường dẫn report.md. Không dán dữ liệu thô.
Chưa có baseline → nhắc người xem kết quả và chép `runs\<run>\data\*` sang `baseline\` nếu đúng.
