namespace Modbot.Evidence.Options;

/// <summary>
/// Credentials and addressing for any S3-compatible endpoint (design section 4.1).
/// </summary>
/// <remarks>
/// <para>
/// The field names match the variables Railway injects — <c>BUCKET</c>, <c>ACCESS_KEY_ID</c>,
/// <c>SECRET_ACCESS_KEY</c>, <c>REGION</c>, <c>ENDPOINT</c> — because a one-click template that
/// makes the operator retype five values already sitting in the environment is a needless way to
/// lose people in a wizard. Design section 16.1 is emphatic about the limit of that convenience:
/// the environment may <em>pre-fill</em> the wizard once, and after that the database decides.
/// Re-reading the environment on every boot would let a variable edit silently repoint the store
/// at a different bucket, which is precisely the trap section 8 exists to catch.
/// </para>
/// <para>
/// Nothing here is provider-specific. Wasabi, Cloudflare R2, MinIO and AWS all fit the same five
/// fields plus <see cref="UsePathStyle"/>.
/// </para>
/// </remarks>
public sealed record S3EvidenceOptions
{
    /// <summary>The bucket. Railway calls this <c>BUCKET</c>.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// The service endpoint, with scheme — <c>https://…</c>. Railway calls this <c>ENDPOINT</c>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Railway calls this <c>ACCESS_KEY_ID</c>.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>Railway calls this <c>SECRET_ACCESS_KEY</c>. Stored encrypted by the caller.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>
    /// SigV4 signing region. Railway calls this <c>REGION</c>; MinIO and R2 accept anything and
    /// conventionally use <c>us-east-1</c> and <c>auto</c> respectively.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Path-style (<c>https://endpoint/bucket/key</c>) rather than virtual-hosted-style
    /// (<c>https://bucket.endpoint/key</c>).
    /// </summary>
    /// <remarks>
    /// A setting rather than an assumption because providers genuinely differ and one provider
    /// differs from itself: Railway issues virtual-hosted-style on new buckets and path-style on
    /// older ones, and its Credentials tab is the only place that says which. MinIO defaults to
    /// path-style.
    /// </remarks>
    public bool UsePathStyle { get; set; }

    /// <summary>
    /// An optional key prefix, so one bucket can hold more than one deployment.
    /// </summary>
    /// <remarks>
    /// Leading and trailing slashes are normalised away. This is a prefix on Modbot's own keys and
    /// never carries anything a user supplied; design section 5.1's hex-only rule is unaffected.
    /// </remarks>
    public string? Prefix { get; set; }
}
