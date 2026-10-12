---
name: revit-test
description: Chạy một hoặc nhiều suite test Revit bằng runner hawee-flow (không vòng sửa); trả kết quả gọn.
argument-hint: "<suite> [<suite> …] [--purpose task|baseline|adhoc]"
disable-model-invocation: true
context: fork
model: sonnet
---

# /revit-test $ARGUMENTS

Mỗi suite (tuần tự; suite trước lỗi vẫn chạy suite sau): `test_run suite=<tên> purpose=<…>` (mặc định adhoc; `baseline` khi lấy baseline ở bước 4a).
Runner tự làm bước 0–10 (môi trường, Revit, bản sao model, step có chặn tool cấm, tracking + điểm kiểm, đối chiếu log, chấm, evidence, dọn dẹp).

Sau mỗi suite: luật "cần agent" → `get_detail kind=rule`, xem ảnh, `record_verdict`. Không tự sửa code ở lệnh này (vòng sửa thuộc `/verify`).
Trả lời: bảng suite · kết luận test-summary · số PASS / FAIL / cần người · đường dẫn report.md. Không dán dữ liệu thô.
Chưa có baseline → nhắc người xem kết quả và chép `runs\<run>\data\*` sang `baseline\` nếu đúng.
