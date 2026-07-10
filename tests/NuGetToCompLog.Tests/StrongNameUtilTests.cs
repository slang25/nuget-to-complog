using System.Security.Cryptography;
using NuGetToCompLog.Services;
using Xunit;

namespace NuGetToCompLog.Tests;

public class StrongNameUtilTests
{
    [Fact]
    public void DerivesPublicKeyBlobFromPrivateCspBlob()
    {
        using var rsa = new RSACryptoServiceProvider(1024);
        var snk = rsa.ExportCspBlob(includePrivateParameters: true);

        var publicKeyBlob = StrongNameUtil.TryDerivePublicKeyBlob(snk);

        Assert.NotNull(publicKeyBlob);

        // 12-byte strong-name header + PUBLICKEYBLOB(8) + RSAPUBKEY(12) + modulus(128)
        Assert.Equal(12 + 20 + 128, publicKeyBlob.Length);

        // Header: CALG_RSA_SIGN, CALG_SHA1, blob length
        Assert.Equal(0x2400, BitConverter.ToInt32(publicKeyBlob, 0));
        Assert.Equal(0x8004, BitConverter.ToInt32(publicKeyBlob, 4));
        Assert.Equal(publicKeyBlob.Length - 12, BitConverter.ToInt32(publicKeyBlob, 8));

        // PUBLICKEYBLOB with 'RSA1' magic and the same bit length
        Assert.Equal(0x06, publicKeyBlob[12]);
        Assert.Equal(0x31415352u, BitConverter.ToUInt32(publicKeyBlob, 20)); // 'RSA1'
        Assert.Equal(1024, BitConverter.ToInt32(publicKeyBlob, 24));

        // Exponent and modulus must match the CSP blob (same little-endian layout, offsets 16/20)
        Assert.Equal(snk.AsSpan(16, 4).ToArray(), publicKeyBlob.AsSpan(28, 4).ToArray());
        Assert.Equal(snk.AsSpan(20, 128).ToArray(), publicKeyBlob.AsSpan(32, 128).ToArray());
    }

    [Fact]
    public void PassesThroughExistingPublicKeyBlob()
    {
        using var rsa = new RSACryptoServiceProvider(1024);
        var snk = rsa.ExportCspBlob(includePrivateParameters: true);
        var publicKeyBlob = StrongNameUtil.TryDerivePublicKeyBlob(snk)!;

        Assert.Equal(publicKeyBlob, StrongNameUtil.TryDerivePublicKeyBlob(publicKeyBlob));
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.Null(StrongNameUtil.TryDerivePublicKeyBlob([1, 2, 3, 4]));
        Assert.Null(StrongNameUtil.TryDerivePublicKeyBlob(new byte[64]));
    }
}
