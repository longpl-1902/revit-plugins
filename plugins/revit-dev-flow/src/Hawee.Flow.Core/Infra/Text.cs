using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hawee.Flow.Core.Infra;

public static class Text
{
    public static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string Read(string path) => File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
    public static string[] Lines(string path) => Read(path).Split('\n');

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n"), Utf8NoBom);
    }

    /// <summary>Cắt danh sách dòng: giữ tối đa n dòng, thêm dòng "… còn k dòng".</summary>
    public static IEnumerable<string> Cap(IEnumerable<string> lines, int max)
    {
        var list = lines as IList<string> ?? lines.ToList();
        foreach (var l in list.Take(max)) yield return l;
        if (list.Count > max) yield return $"… còn {list.Count - max} dòng (xem file / get_detail)";
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Bỏ dấu tiếng Việt để so khớp từ khoá.</summary>
    public static string Fold(string s)
    {
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var c in norm)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'đ' => 'd', 'Đ' => 'D', _ => c });
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>Từ khoá truy vấn — xem <see cref="Terms.Query"/>.</summary>
    public static List<string> Keywords(string s) => Terms.Query(s);

    /// <summary>Thoát ký tự '|' trong ô bảng markdown.</summary>
    public static string Cell(string? s) => (s ?? "").Replace("\n", " ").Replace("|", "\\|").Trim();

    public static string Slug(string s)
    {
        var f = Regex.Replace(Fold(s), @"[^a-z0-9]+", "-").Trim('-');
        return f.Length == 0 ? "task" : f;
    }
}
