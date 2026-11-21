using Xunit;

namespace Wavvon.Sdk.Tests;

public sealed class VoiceTests
{
    [Theory]
    [InlineData(new byte[] { 0xf8 }, 20_000)]
    [InlineData(new byte[] { 0xf9 }, 40_000)]
    [InlineData(new byte[] { 0x08 }, 20_000)]
    [InlineData(new byte[] { 0x18 }, 60_000)]
    [InlineData(new byte[] { 0x0b, 0x03 }, 60_000)]
    public void OpusDurationReadsTheTocByte(byte[] packet, int us) =>
        Assert.Equal(us, VoiceCrypto.OpusDurationUs(packet));

    [Fact]
    public void OpusDurationRefusesWhatItCannotRead()
    {
        Assert.Throws<FormatException>(() => VoiceCrypto.OpusDurationUs([]));
        Assert.Throws<FormatException>(() => VoiceCrypto.OpusDurationUs([0x0b]));
        Assert.Throws<FormatException>(() => VoiceCrypto.OpusDurationUs([0x0b, 0x00]));
    }
}
