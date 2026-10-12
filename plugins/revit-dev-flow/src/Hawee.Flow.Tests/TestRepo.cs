using Hawee.Flow.Core.Infra;

namespace Hawee.Flow.Tests;

/// <summary>Repo dự án giả trong thư mục tạm: hawee.project.yaml + BIM_KB_Base thu nhỏ (mẫu, rule) — đủ cho test.</summary>
public sealed class TestRepo : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hawee-flow-test-" + Guid.NewGuid().ToString("N")[..8]);
    public FixedClock Clock { get; } = new(new DateTime(2026, 10, 12, 9, 0, 0));
    public FakeRunner Runner { get; } = new();

    public TestRepo(string? extraConfig = null)
    {
        Write("hawee.project.yaml", $$"""
            project: demo-addin
            ki_prefix: KI-DM
            base: { path: BIM_KB_Base, version: "0.8", mode: in-repo }
            profiles: [revit-addin]
            paths:
              tasks: docs/tasks
              code_maps: docs/architecture/code-map
            gates:
              G1: [BA, TechLead]
              G2: { approver: TechLead, skip_if: "chỉ sửa Core" }
              G3: QA
              G4: owner-of-task
              G5: TechLead
              G6: [QA, BIMManager]
            kb_lint:
              ki_files: [docs/known-issues.md]
            {{extraConfig ?? ""}}
            """);
        Write("BIM_KB_Base/VERSION", "0.8\n");
        Write("BIM_KB_Base/core/templates/task-status.yaml", """
            # status.yaml — mẫu thu nhỏ cho test
            task: <issueId>-<slug>
            issue: "#<issueId>"                      # issue tracker
            topic: <slug>                            # chủ đề
            cr_of: ""
            change_requests: []
            type: feature                            # feature | bug
            tier: T2                                 # tier
            owner: <người làm>
            agent: <client agent>
            created: 2026-01-01
            state: context                           # context | spec | g1
            spec: nháp
            plan: nháp
            baseline: ""
            gates:                                   # một dòng một cổng
              G1: { decision: "", by: "", date: "", evidence: "", decision_log: "" }     # evidence
              G2: { decision: "", by: "", date: "", evidence: "", coder_confirmed: false }
              G3: { decision: "", by: "", date: "", evidence: "", conditional_on: "" }
              G4: { decision: "", by: "", date: "", commits: [] }
              G5: { decision: "", by: "", date: "", review_record: "" }
              G6: { decision: "", by: "", date: "", release: "" }
            loops: { build_test: 0, host_test: 0 }   # vòng sửa
            suites: []
            suite_hashes: {}
            autonomy: ask
            deviations: 0
            open_questions: []
            """);
        Write("BIM_KB_Base/core/templates/spec.md", """
            # Spec — <tên tính năng / thay đổi>

            - Chủ đề: `<slug>` · Phiên bản: v1 · Issue: #<số> · Ngày: <YYYY-MM-DD> · Tier: T2 · Trạng thái: nháp | chờ G1 | chốt
            - Thay cho: không có | <link>
            - Bị thay bởi: — | <link>

            > Cách làm: `core/process/spec-plan.md` §2.

            ## 0. Tóm tắt

            3–5 dòng.

            ## 1. Bài toán

            Ai dùng.

            ## 2. Hiện trạng (đã khảo sát)

            | Hạng mục | Thực tế | Bằng chứng |
            |---|---|---|

            ## 3. Phạm vi

            Trong / ngoài.

            ## 12. Giả định

            | # | Giả định | Đã kiểm chứng? (bằng gì) | Nếu sai thì |
            |---|---|---|---|

            ## 13. UNKNOWN – NEED HUMAN DECISION

            | # | Câu hỏi | Phương án đề xuất | Người quyết định | Trả lời |
            |---|---|---|---|---|

            ## 14. Bản ghi trao đổi & quyết định

            | # | Gói | Câu hỏi (tóm tắt) | Phương án đề xuất | Trả lời / quyết định | Ai | Ngày | Áp vào mục |
            |---|---|---|---|---|---|---|---|
            | Q1 | 1 | | | | | | §3 |

            ## 15. Lịch sử duyệt và thay đổi sau chốt

            | Ngày | Ai | Việc | Phát hiện | Đã sửa |
            |---|---|---|---|---|
            """);
        Write("BIM_KB_Base/core/templates/plan.md", """
            # Plan — <tên>

            - Chủ đề: `<slug>` · Phiên bản: v1 · Spec: <link> v<N> (qua G1 ngày <YYYY-MM-DD>) · Issue: #<số> · Ngày: <YYYY-MM-DD> · Tier: T2 · Trạng thái: nháp | chờ G2

            ## 0. Tóm tắt

            Tóm tắt.

            ## 1. Đầu vào đã chốt

            ### 1.1. Spec

            Link.

            ### 1.2. Quyết định kỹ thuật đã chốt cùng DEV / Tech Lead

            | # | Câu hỏi | Phương án đề xuất | Chốt | Ai | Ngày | Áp vào mục |
            |---|---|---|---|---|---|---|
            | T1 | chọn phương án A hay B | A | | | | §2 |

            ### 1.3. Baseline (đo trước khi chốt plan)

            | Hạng mục | Giá trị đo được | Lệnh / nguồn |
            |---|---|---|

            ## 2. Đánh giá tổng thể

            ### 2.1. Phương án

            A, B.

            ## 3. Phạm vi ảnh hưởng

            ### 3.1. Thay đổi trực tiếp

            File.

            ### 3.2. Caller bị ảnh hưởng

            Caller.

            ## 5. Thiết kế kỹ thuật

            ### 5.1. Luồng sau khi sửa

            Luồng.

            ### 5.2. Kiểu & hàm mới / đổi

            Hàm.

            ### 5.6. Bất biến — không được đổi

            | # | Bất biến | Vì sao | Test / case canh (bước #) |
            |---|---|---|---|

            ## 6. Bước code

            ### Tóm tắt

            Bảng.

            ## 7. Ma trận AC → bước → test

            | AC | Bước # | Unit test | Case suite / tool READ | Kiểm tay (ai) |
            |---|---|---|---|---|
            | AC1 | | | | |

            ## 13. Lệch so với plan (ghi trong lúc làm)

            | Ngày | Lệch gì | Vì sao | Mức | Cần duyệt lại (G1/G2)? |
            |---|---|---|---|---|
            """);
        Write("BIM_KB_Base/core/templates/decision-log.md", """
            # Nhật ký quyết định — <dự án>

            | ID | Ngày | Ai (vai) | Việc / tài liệu | Câu hỏi | Quyết định | Hệ quả / áp vào | Nguồn (link) | Thay cho |
            |---|---|---|---|---|---|---|---|---|
            | DEC-2026-001 | 2026-01-01 | <tên> (Tech Lead) | spec <task> | Q1 … | B — … | spec §3 | <link> | — |

            ## Câu hỏi đang treo (CHẶN / UNKNOWN chưa trả lời)

            | ID câu | Từ ngày | Tài liệu | Câu hỏi (tóm tắt) | Người quyết định | Mặc định nếu không trả lời |
            |---|---|---|---|---|---|
            """);
        Write("BIM_KB_Base/core/rules/general.md", """
            # Rule chung

            ## GEN-IDEMP-001 — Automation chạy lại không tạo trùng · `high`

            - **Khi:** lệnh tạo sheet, view.
            - **Phải:** tìm theo khoá nghiệp vụ trước khi tạo;
              đã có thì cập nhật.

            ## GEN-DOC-001 — Spec, plan nối chuỗi theo chủ đề · `high` · `draft`

            - **Phải:** mỗi chủ đề một spec hiện hành.
            """);
        Write("BIM_KB_Base/profiles/revit-addin/rules/revit-api.md", """
            # Rule Revit

            ## REVIT-THREAD-001 — Gọi Revit API chỉ từ ngữ cảnh hợp lệ · `blocker`

            - **Khi:** code chạy từ thread nền, MCP tool.
            - **Phải:** đưa về thread Revit qua ExternalEvent.

            ## REVIT-TXN-001 — Transaction có tên, có rollback · `medium`

            - **Phải:** transaction có tên.
            """);
        Write("docs/known-issues.md", """
            # Known issues

            ### KI-DM-007 — Keyplan crop sai khi view template khoá scale
            - **Trạng thái:** OPEN · **Mức:** high · **Loại:** geometry
            """);
    }

    public ProjectContext Ctx() => ProjectContext.Open(Root, Clock);

    public string Write(string rel, string content)
    {
        var p = Path.Combine(Root, rel);
        Text.Write(p, content);
        return p;
    }

    public string Read(string rel) => Text.Read(Path.Combine(Root, rel));

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* bỏ qua */ }
    }
}

/// <summary>Thay tiến trình con: trả kết quả đặt trước theo tên script.</summary>
public sealed class FakeRunner : IProcRunner
{
    public List<(string File, List<string> Args)> Calls { get; } = new();
    public Dictionary<string, ProcResult> ByScript { get; } = new();
    public ProcResult Default { get; set; } = new(0, "", false);

    public ProcResult Run(string file, IEnumerable<string> args, string cwd, TimeSpan? timeout = null, IDictionary<string, string>? env = null)
    {
        var a = args.ToList();
        Calls.Add((file, a));
        foreach (var (key, res) in ByScript)
            if (a.Any(x => x.EndsWith(key, StringComparison.OrdinalIgnoreCase)) || file.EndsWith(key)) return res;
        return Default;
    }
}
