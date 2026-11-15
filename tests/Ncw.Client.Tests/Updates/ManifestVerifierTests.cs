using System.Text;
using Ncw.Client.Updates;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Updates;

/// <summary>发布清单的 Ed25519 分离签名验证(docs/design/04 §6):先验签再信内容;任何一步不对都拒绝。</summary>
public class ManifestVerifierTests
{
    private static readonly TestSigningKey Key = new("test-1");

    private static ManifestVerification Verify(byte[] bytes, string? signature, ReleaseKeyRing? ring = null, UpdateChannel channel = UpdateChannel.Stable) =>
        ManifestVerifier.Verify(bytes, signature, ring ?? Key.Ring, channel);

    private static (byte[] Bytes, string Sig) Make(ReleaseManifest? manifest = null, TestSigningKey? key = null) =>
        UpdateKit.Signed(key ?? Key, manifest ?? UpdateKit.Manifest("3.0.1"));

    [Fact]
    public void A_correctly_signed_manifest_passes_and_carries_the_policy()
    {
        var grace = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var (bytes, sig) = Make(UpdateKit.Manifest("3.0.1", mandatory: true, minimum: "3.0.1", grace: grace, notes: "## fixes",
            artifacts: UpdateKit.Artifact("NextCoWork-3.0.1-stable-full.nupkg", sha256: "ab")));

        var result = Verify(bytes, sig);

        Assert.True(result.IsValid);
        Assert.Equal("3.0.1", result.Manifest!.Version);
        Assert.Equal(new UpdateRelease("3.0.1", true, "3.0.1", grace), result.Manifest.ToRelease());
        Assert.Equal("## fixes", result.Manifest.Notes);
        Assert.Equal("NextCoWork-3.0.1-stable-full.nupkg", Assert.Single(result.Manifest.Artifacts).FileName);
    }

    [Fact]
    public void A_single_changed_byte_is_a_signature_failure()
    {
        var (bytes, sig) = Make();
        bytes[bytes.Length / 2] ^= 0x01;
        var result = Verify(bytes, sig);
        Assert.Equal(UpdateFailureKind.SignatureInvalid, result.Failure);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void Flipping_mandatory_or_the_version_after_signing_is_detected()
    {
        var (bytes, sig) = Make(UpdateKit.Manifest("3.0.1"));
        var text = Encoding.UTF8.GetString(bytes).Replace("\"mandatory\": false", "\"mandatory\": true", StringComparison.Ordinal);
        Assert.Equal(UpdateFailureKind.SignatureInvalid, Verify(Encoding.UTF8.GetBytes(text), sig).Failure);
        text = Encoding.UTF8.GetString(bytes).Replace("3.0.1", "9.9.9", StringComparison.Ordinal);
        Assert.Equal(UpdateFailureKind.SignatureInvalid, Verify(Encoding.UTF8.GetBytes(text), sig).Failure);
    }

    [Fact]
    public void A_signature_from_another_key_is_rejected_even_when_the_key_id_matches_ours()
    {
        var attacker = new TestSigningKey("test-1"); // 同一个 kid,别的密钥
        var (bytes, sig) = Make(key: attacker);
        Assert.Equal(UpdateFailureKind.SignatureInvalid, Verify(bytes, sig).Failure);
    }

    [Fact]
    public void A_key_id_we_do_not_trust_is_reported_as_unknown()
    {
        var other = new TestSigningKey("rotated-out");
        var (bytes, sig) = Make(UpdateKit.Manifest("3.0.1", keyId: "rotated-out"), other);
        Assert.Equal(UpdateFailureKind.UnknownSigningKey, Verify(bytes, sig).Failure);
    }

    [Fact]
    public void Claiming_a_trusted_key_id_but_signing_with_an_untrusted_key_fails()
    {
        var attacker = new TestSigningKey("x");
        var (bytes, sig) = Make(UpdateKit.Manifest("3.0.1", keyId: "test-1"), attacker);
        Assert.Equal(UpdateFailureKind.SignatureInvalid, Verify(bytes, sig).Failure);
    }

    [Fact]
    public void Key_rotation_old_and_new_keys_can_both_verify_and_dropping_one_revokes_it()
    {
        var oldKey = new TestSigningKey("k1");
        var newKey = new TestSigningKey("k2");
        var both = new ReleaseKeyRing(new Dictionary<string, byte[]> { ["k1"] = oldKey.PublicKey, ["k2"] = newKey.PublicKey });
        var onlyNew = newKey.Ring;

        var (oldBytes, oldSig) = UpdateKit.Signed(oldKey, UpdateKit.Manifest("3.0.1", keyId: "k1"));
        var (newBytes, newSig) = UpdateKit.Signed(newKey, UpdateKit.Manifest("3.0.2", keyId: "k2"));

        Assert.True(Verify(oldBytes, oldSig, both).IsValid);
        Assert.True(Verify(newBytes, newSig, both).IsValid);
        Assert.True(Verify(newBytes, newSig, onlyNew).IsValid);
        Assert.Equal(UpdateFailureKind.UnknownSigningKey, Verify(oldBytes, oldSig, onlyNew).Failure);
    }

    [Fact]
    public void A_manifest_without_a_declared_key_id_is_tried_against_every_trusted_key()
    {
        var newKey = new TestSigningKey("k2");
        var both = new ReleaseKeyRing(new Dictionary<string, byte[]> { ["k1"] = Key.PublicKey, ["k2"] = newKey.PublicKey });
        var (bytes, sig) = UpdateKit.Signed(newKey, UpdateKit.Manifest("3.0.1", keyId: null));
        Assert.True(Verify(bytes, sig, both).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void A_missing_signature_is_rejected(string? signature) =>
        Assert.Equal(UpdateFailureKind.SignatureMissing, Verify(Make().Bytes, signature).Failure);

    [Theory]
    [InlineData("not base64 at all!!")]
    [InlineData("AAAA")]                                  // 合法 base64,但不是 64 字节
    public void A_malformed_signature_is_rejected(string signature) =>
        Assert.Equal(UpdateFailureKind.SignatureInvalid, Verify(Make().Bytes, signature).Failure);

    [Fact]
    public void A_build_without_any_trusted_key_refuses_instead_of_skipping_verification()
    {
        var (bytes, sig) = Make();
        var result = Verify(bytes, sig, ReleaseKeyRing.Empty);
        Assert.Equal(UpdateFailureKind.NoTrustedKeys, result.Failure);
    }

    [Fact]
    public void A_validly_signed_manifest_for_another_channel_is_rejected()
    {
        var (bytes, sig) = Make(UpdateKit.Manifest("3.0.1", channel: "beta"));
        Assert.Equal(UpdateFailureKind.ManifestRejected, Verify(bytes, sig, channel: UpdateChannel.Stable).Failure);
        Assert.True(Verify(bytes, sig, channel: UpdateChannel.Beta).IsValid);
    }

    [Fact]
    public void A_manifest_that_says_it_is_unsigned_is_rejected_even_with_a_valid_signature()
    {
        var (bytes, sig) = Make(UpdateKit.Manifest("3.0.1", status: "pending"));
        Assert.Equal(UpdateFailureKind.ManifestUnsigned, Verify(bytes, sig).Failure);
    }

    [Theory]
    [InlineData("OtherProduct", 1)]
    [InlineData("NextCoWork", 2)]
    public void Wrong_product_or_unknown_schema_is_rejected(string product, int schema)
    {
        var (bytes, sig) = Make(UpdateKit.Manifest("3.0.1", product: product, schema: schema));
        Assert.Equal(UpdateFailureKind.ManifestRejected, Verify(bytes, sig).Failure);
    }

    [Fact]
    public void A_self_contradicting_policy_is_rejected()
    {
        // 最低版本高于这个发布本身:装完仍低于最低版本,用户会被永远阻断
        var (bytes, sig) = Make(UpdateKit.Manifest("3.0.1", mandatory: true, minimum: "3.5.0"));
        Assert.Equal(UpdateFailureKind.ManifestRejected, Verify(bytes, sig).Failure);
    }

    [Fact]
    public void Garbage_that_happens_to_carry_a_valid_signature_is_rejected_not_thrown()
    {
        var bytes = Encoding.UTF8.GetBytes("this is not json");
        var result = Verify(bytes, Convert.ToBase64String(Key.Sign(bytes)));
        Assert.Equal(UpdateFailureKind.ManifestRejected, result.Failure);
    }

    [Fact]
    public void Garbage_with_a_wrong_signature_never_throws()
    {
        var result = Verify([1, 2, 3], Convert.ToBase64String(new byte[64]));
        Assert.Equal(UpdateFailureKind.SignatureInvalid, result.Failure);
    }

    [Theory]
    [InlineData("k1:" , false)]
    [InlineData(":AAAA", false)]
    [InlineData("k1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Key_ring_text_must_be_well_formed(string? text, bool ok) => Assert.Equal(ok, ReleaseKeyRing.TryParse(text, out _));

    [Fact]
    public void Key_ring_text_round_trips_several_keys_and_rejects_a_bad_one_wholesale()
    {
        var a = new TestSigningKey("a");
        var b = new TestSigningKey("b");
        var text = $"a:{Convert.ToBase64String(a.PublicKey)}, b:{Convert.ToBase64String(b.PublicKey)}";
        Assert.True(ReleaseKeyRing.TryParse(text, out var ring));
        Assert.Equal(["a", "b"], ring.KeyIds.Order());
        Assert.False(ReleaseKeyRing.TryParse(text + ",c:AAAA", out _)); // 一项坏了,整份作废
    }

    [Fact]
    public void A_key_that_is_not_32_bytes_cannot_enter_the_ring() =>
        Assert.Throws<ArgumentException>(() => new ReleaseKeyRing(new Dictionary<string, byte[]> { ["x"] = new byte[31] }));
}
