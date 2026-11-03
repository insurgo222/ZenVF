using ZenVF.Models;

namespace ZenVF.Tests;

public class CpuDescriptorTests
{
    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", "9800X3D")]
    [InlineData("AMD Ryzen 9 9950X3D2 16-Core Processor", "9950X3D2")]
    [InlineData("AMD Ryzen 9 9955HX3D 16-Core Processor", "9955HX3D")]
    [InlineData("AMD Ryzen 9 7945HX3D with Radeon Graphics", "7945HX3D")]
    [InlineData("AMD Ryzen 9 7945HX with Radeon Graphics", "7945HX")]
    [InlineData("AMD Ryzen 5 7500F 6-Core Processor", "7500F")]
    [InlineData("AMD Ryzen 7 8700GE w/ Radeon 780M Graphics", "8700GE")]
    [InlineData("AMD Ryzen 7 8845HS w/ Radeon 780M Graphics", "8845HS")]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", "370")]
    [InlineData("AMD Ryzen AI 7 PRO 360 w/ Radeon 880M", "360")]
    public void ExtractsTheModelToken(string name, string expected)
    {
        Assert.Equal(expected, CpuDescriptor.ExtractModelToken(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Intel(R) Core(TM) i9-14900K")]
    public void UnknownNamesHaveNoToken(string name)
    {
        Assert.Null(CpuDescriptor.ExtractModelToken(name));
    }

    [Theory]
    [InlineData(0x19, 0x61, true, false)]  // Raphael / Dragon Range
    [InlineData(0x19, 0x75, true, false)]  // Hawk Point
    [InlineData(0x1A, 0x44, false, true)]  // Granite Ridge
    [InlineData(0x1A, 0x24, false, true)]  // Strix Point
    public void KnownZen4And5ModelsMap(byte family, byte model, bool zen4, bool zen5)
    {
        var mapped = CpuDescriptor.MapArchitecture(family, model);

        Assert.Equal(zen4, mapped.Zen4);
        Assert.Equal(zen5, mapped.Zen5);
    }

    [Theory]
    [InlineData(0x19, 0x21)] // Zen 3 Vermeer shares family 19h
    [InlineData(0x19, 0x50)] // Zen 3 Cezanne
    [InlineData(0x1A, 0x70)] // Strix Halo: not verified
    [InlineData(0x17, 0x71)] // Zen 2
    public void UnknownModelsAreNotZen4Or5(byte family, byte model)
    {
        var mapped = CpuDescriptor.MapArchitecture(family, model);

        Assert.False(mapped.Zen4);
        Assert.False(mapped.Zen5);
    }

    [Fact]
    public void OnlyAmdZen4Or5IsSupported()
    {
        Assert.True(Cpu(isAmd: true, zen5: true).IsSupported);
        Assert.False(Cpu(isAmd: true).IsSupported);
        Assert.False(Cpu(isAmd: false, zen5: true).IsSupported);
    }

    [Fact]
    public void ThermalLimits()
    {
        Assert.Equal(89.0, Cpu(isAmd: true, zen4: true, name: "AMD Ryzen 7 7800X3D").TjMaxC);
        Assert.Equal(95.0, Cpu(isAmd: true, zen5: true, name: "AMD Ryzen 7 9800X3D").TjMaxC);
        Assert.Equal(105.0, Cpu(isAmd: true, zen5: true).AbortTemperatureC);
    }

    [Fact]
    public void CeilingOffsetRoundTrips()
    {
        CpuDescriptor cpu = new() { Name = "x", Vendor = "AuthenticAMD", FMaxCeilingOffsetMHz = 25 };

        Assert.Equal(5250u, cpu.ToCeilingFMax(5225));
        Assert.Equal(5225, cpu.ToActualFMax(5250));
    }

    private static CpuDescriptor Cpu(bool isAmd, bool zen4 = false, bool zen5 = false, string name = "AMD Ryzen") =>
        new() { Name = name, Vendor = isAmd ? "AuthenticAMD" : "GenuineIntel", IsAmd = isAmd, IsZen4 = zen4, IsZen5 = zen5 };
}
