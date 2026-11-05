using ClipboardManager.Application.Services.FileSearch;

namespace ClipboardManager.Application.Tests;

public sealed class FileSearchQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("好")]
    public void BlankOrTooShort_IsEmpty(string? selection) =>
        Assert.Equal(FileSearchKind.Empty, FileSearchQuery.Parse(selection).Kind);

    [Theory]
    [InlineData(@"D:\资料\报价单V3.xlsx", @"D:\资料\报价单V3.xlsx")]
    [InlineData(@"“D:\资料\报价单V3.xlsx”。", @"D:\资料\报价单V3.xlsx")]
    [InlineData(@"\\nas\share\合同.pdf", @"\\nas\share\合同.pdf")]
    [InlineData("C:/Users/me/Desktop", "C:/Users/me/Desktop")]
    public void AbsolutePath_IsDirect(string selection, string expected)
    {
        var request = FileSearchQuery.Parse(selection);
        Assert.Equal(FileSearchKind.DirectPath, request.Kind);
        Assert.Equal(expected, request.DirectPath);
        Assert.Equal(string.Empty, request.IndexQuery);
    }

    [Theory]
    [InlineData("报价单V3.xlsx", "报价单V3", "xlsx")]
    [InlineData("看下 报价单V3.xlsx 谢谢", "报价单V3", "xlsx")]
    [InlineData("《年度总结-2026.docx》", "年度总结-2026", "docx")]
    [InlineData("发你了：design_final.v2.PSD。", "design_final.v2", "PSD")]
    public void FileName_IsExtracted(string selection, string stem, string extension)
    {
        var request = FileSearchQuery.Parse(selection);
        Assert.Equal(FileSearchKind.FileName, request.Kind);
        Assert.Equal(stem, request.Stem);
        Assert.Equal(extension, request.Extension);
        Assert.Equal($"\"{stem}\" *.{extension}", request.IndexQuery);
    }

    [Theory]
    [InlineData("版本 3.5 发布")]
    [InlineData("访问 example.com 看看")]
    public void NumbersAndDomains_AreNotFileNames(string selection) =>
        Assert.Equal(FileSearchKind.Keywords, FileSearchQuery.Parse(selection).Kind);

    [Fact]
    public void Keywords_AreQuotedSoOperatorsStayLiteral()
    {
        var request = FileSearchQuery.Parse("季度 报价|合同 !草稿");
        Assert.Equal(FileSearchKind.Keywords, request.Kind);
        Assert.Equal(["季度", "报价", "合同", "草稿"], request.Keywords);
        Assert.Equal("\"季度\" \"报价\" \"合同\" \"草稿\"", request.IndexQuery);
        Assert.DoesNotContain('|', request.IndexQuery);
    }

    [Fact]
    public void Keywords_AreCapped()
    {
        var request = FileSearchQuery.Parse("a1 b2 c3 d4 e5 f6 g7 h8");
        Assert.Equal(FileSearchQuery.MaxKeywords, request.Keywords.Count);
    }

    [Fact]
    public void LongProse_IsRejected()
    {
        var request = FileSearchQuery.Parse(new string('字', FileSearchQuery.MaxKeywordSelectionLength + 1));
        Assert.Equal(FileSearchKind.Empty, request.Kind);
        Assert.NotNull(request.Problem);
    }
}

public sealed class FileSearchRankerTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);

    private static FileSearchHit Hit(string path, int ageDays = 100, bool folder = false) =>
        new(path, folder, 10, Now.AddDays(-ageDays));

    [Fact]
    public void ExactName_BeatsSubstringAndNumberedCopy()
    {
        var request = FileSearchQuery.Parse("报价单V3.xlsx");
        var ranked = FileSearchRanker.Rank(
        [
            Hit(@"D:\旧\报价单V3-草稿.xlsx", ageDays: 0),
            Hit(@"D:\Downloads\报价单V3(1).xlsx", ageDays: 2),
            Hit(@"D:\资料\报价单V3.xlsx", ageDays: 40),
        ], request, Now);
        Assert.Equal(@"D:\资料\报价单V3.xlsx", ranked[0].FullPath);
        Assert.Equal(@"D:\Downloads\报价单V3(1).xlsx", ranked[1].FullPath);
    }

    [Fact]
    public void RecycleBin_IsHidden_AndNoise_IsDemoted()
    {
        var request = FileSearchQuery.Parse("logo.png");
        var ranked = FileSearchRanker.Rank(
        [
            Hit(@"C:\$RECYCLE.BIN\S-1-5\logo.png", ageDays: 0),
            Hit(@"D:\app\node_modules\pkg\logo.png", ageDays: 0),
            Hit(@"D:\设计\logo.png", ageDays: 60),
        ], request, Now);
        Assert.Equal(2, ranked.Count);
        Assert.Equal(@"D:\设计\logo.png", ranked[0].FullPath);
    }

    [Fact]
    public void Keywords_PreferMatchesInFileName()
    {
        var request = FileSearchQuery.Parse("季度 报价");
        var ranked = FileSearchRanker.Rank(
        [
            Hit(@"D:\季度\报价\readme.txt", ageDays: 0),
            Hit(@"D:\资料\季度报价.xlsx", ageDays: 90),
        ], request, Now);
        Assert.Equal(@"D:\资料\季度报价.xlsx", ranked[0].FullPath);
    }

    [Fact]
    public void Duplicates_AreRemoved_AndResultsCapped()
    {
        var request = FileSearchQuery.Parse("a.txt");
        var hits = Enumerable.Range(0, 40).Select(i => Hit($@"D:\f{i}\a.txt")).Append(Hit(@"d:\F0\A.TXT"));
        Assert.Equal(FileSearchRanker.MaxResults, FileSearchRanker.Rank(hits, request, Now).Count);
        Assert.Single(FileSearchRanker.Rank([Hit(@"D:\f\a.txt"), Hit(@"d:\F\A.TXT")], request, Now));
    }
}
