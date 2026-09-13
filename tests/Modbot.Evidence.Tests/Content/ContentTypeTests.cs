using Modbot.Evidence.Content;
using Modbot.Evidence.Tests.Fakes;

namespace Modbot.Evidence.Tests.Content;

/// <summary>
/// Design section 10.1: the type is decided by inspecting the leading bytes, never by the client's
/// header and never by the filename, and the allowlist is closed.
/// </summary>
public class ContentTypeTests
{
    [Fact]
    public void EveryAllowedFormatIsRecognisedFromItsBytes()
    {
        Assert.Equal(EvidenceContentType.Png, EvidenceContentType.Sniff(SampleMedia.Png()).ContentType);
        Assert.Equal(EvidenceContentType.Jpeg, EvidenceContentType.Sniff(SampleMedia.Jpeg()).ContentType);
        Assert.Equal(EvidenceContentType.Gif, EvidenceContentType.Sniff(SampleMedia.Gif()).ContentType);
        Assert.Equal(EvidenceContentType.Webp, EvidenceContentType.Sniff(SampleMedia.Webp()).ContentType);
        Assert.Equal(EvidenceContentType.Mp4, EvidenceContentType.Sniff(SampleMedia.Mp4()).ContentType);
        Assert.Equal(EvidenceContentType.Webm, EvidenceContentType.Sniff(SampleMedia.Webm()).ContentType);
    }

    /// <summary>
    /// The single most common way an "image upload" becomes stored XSS. The filename is irrelevant
    /// because the filename is never consulted.
    /// </summary>
    [Fact]
    public void AnSvgIsRefusedAndSaidToBeAnSvg()
    {
        var verdict = EvidenceContentType.Sniff(SampleMedia.Svg());

        Assert.False(verdict.Accepted);
        Assert.Equal(ContentRejection.ScriptableMarkup, verdict.Rejection);
        Assert.Contains("SVG", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlIsRefused()
    {
        var verdict = EvidenceContentType.Sniff(SampleMedia.Html());

        Assert.False(verdict.Accepted);
        Assert.Equal(ContentRejection.ScriptableMarkup, verdict.Rejection);
    }

    /// <summary>
    /// An <c>ftyp</c> box is not evidence of an MP4 — HEIC and AVIF are ISO base media too, and
    /// neither is on the list.
    /// </summary>
    [Fact]
    public void AnIsoMediaFileThatIsNotMp4IsRefused()
    {
        var verdict = EvidenceContentType.Sniff(SampleMedia.Avif());

        Assert.False(verdict.Accepted);
        Assert.Equal(ContentRejection.NotAllowed, verdict.Rejection);
    }

    [Fact]
    public void MatroskaWithoutTheWebmDoctypeIsRefused()
        => Assert.False(EvidenceContentType.Sniff(SampleMedia.Matroska()).Accepted);

    [Fact]
    public void UnrecognisedBytesAreRefused()
    {
        var verdict = EvidenceContentType.Sniff(SampleMedia.Noise());

        Assert.False(verdict.Accepted);
        Assert.Equal(ContentRejection.NotAllowed, verdict.Rejection);
    }

    [Fact]
    public void AnEmptyFileIsRefused()
    {
        var verdict = EvidenceContentType.Sniff([]);

        Assert.False(verdict.Accepted);
        Assert.Equal(ContentRejection.Empty, verdict.Rejection);
    }

    /// <summary>
    /// A file truncated in transit must not be mistaken for a format nobody recognises — the
    /// operator's next step is different in each case.
    /// </summary>
    [Fact]
    public void AShortFileIsReportedAsTruncatedRatherThanUnrecognised()
    {
        var verdict = EvidenceContentType.Sniff([0x00, 0x01, 0x02]);

        Assert.False(verdict.Accepted);
        Assert.Equal(ContentRejection.Truncated, verdict.Rejection);
    }

    /// <summary>
    /// Leading whitespace and a BOM are ordinary in a file that is trying to look like something
    /// else, so neither may hide markup from the check.
    /// </summary>
    [Fact]
    public void MarkupBehindABomAndWhitespaceIsStillMarkup()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF, (byte)'\n', (byte)' '];
        var svg = SampleMedia.Svg();
        var disguised = new byte[bom.Length + svg.Length];

        bom.CopyTo(disguised, 0);
        svg.CopyTo(disguised, bom.Length);

        Assert.Equal(ContentRejection.ScriptableMarkup, EvidenceContentType.Sniff(disguised).Rejection);
    }

    [Fact]
    public void TheDeclaredTypeCheckAcceptsOnlyTheAllowlist()
    {
        Assert.True(EvidenceContentType.IsPlausibleDeclaredType("image/png"));
        Assert.True(EvidenceContentType.IsPlausibleDeclaredType("video/mp4; codecs=avc1"));
        Assert.False(EvidenceContentType.IsPlausibleDeclaredType("image/svg+xml"));
        Assert.False(EvidenceContentType.IsPlausibleDeclaredType("text/html"));
        Assert.False(EvidenceContentType.IsPlausibleDeclaredType(null));
    }

    [Fact]
    public void TheAllowlistIsExactlyTheSixFormats()
        => Assert.Equal(
            ["image/png", "image/jpeg", "image/webp", "image/gif", "video/mp4", "video/webm"],
            EvidenceContentType.Allowed);
}
