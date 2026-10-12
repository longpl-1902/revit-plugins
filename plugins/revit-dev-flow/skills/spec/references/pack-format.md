# questions/pack-N.yaml — gói câu hỏi cho pack_render / pack_apply

```yaml
doc: spec                 # spec | plan | bug
round: 1                  # số gói
answerers: "BA: Q1–Q3 · Tech Lead: Q0, Q4"
read_before: "issue #42591, KeyplanService.cs:120-180, KI-SD-035"   # đã đọc gì trước khi hỏi
questions:
  - id: Q0                # Q<n>, duy nhất trong task
    q: "Tier T2, chủ đề keyplan-crop (mới) — đồng ý?"
    kind: phạm vi         # nghiệp vụ | phạm vi | hành vi | kỹ thuật
    seen: "đụng 2 project + bản đôi R2022; không đổi contract"   # bằng chứng: file:dòng / issue / log — bắt buộc
    why: "tiers.md: >1 project là T2"                              # vì sao code không trả lời được
    options: { A: "T2", B: "T3 — nếu tính cả đổi DocumentSetConfig" }
    recommend: A
    because: "không đổi cấu hình dùng chung"
    default: A            # hoặc CHẶN (không có mặc định → dừng ở cổng)
    decider: TechLead
    applies: "status tier"   # mục spec/plan nhận quyết định, vd. "§3, §5"
    sets: tier            # tuỳ chọn: tier | autonomy | topic — tool tự ghi status.yaml
    values: { A: T2, B: T3 }
extras:                   # "đề xuất thêm" — người trả lời D1: giữ / bỏ
  - { id: D1, seen: "hai phiên Revit cùng mở", propose: "ngoài phạm vi, ghi §3" }
```

- Mỗi câu một ý; giới hạn số câu theo tier (T1 ≤ 3, T2/T3 ≤ 10); tối đa 3 gói.
- Plan bước 4a: `doc: plan`; có câu chọn phương án (≥ 2), thiết kế, và câu mức tự chủ
  `{ sets: autonomy, options: { A: "ask — dừng ở G3 chờ người", B: "night — tự quyết + tự sửa lỗi chặn task, báo cáo để review" }, values: { A: ask, B: night }, default: A }`.
- Trả lời người: `Q1: B`, `Q2: mặc định`, `Q3: xem lại — …`, `Q4: <câu tự do>`, `D1: bỏ`.
