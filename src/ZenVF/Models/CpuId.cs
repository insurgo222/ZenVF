using System.Runtime.Intrinsics.X86;

namespace ZenVF.Models;

internal static class CpuId
{
    public readonly record struct CpuIdResult(string Vendor, string Brand, byte Family, byte Model, byte Stepping);

    public static CpuIdResult Read()
    {
        try
        {
            if (!X86Base.IsSupported)
            {
                return new CpuIdResult(string.Empty, string.Empty, 0, 0, 0);
            }

            (int eax, int ebx, int ecx, int edx) = X86Base.CpuId(0, 0);
            Span<byte> vendorBytes = stackalloc byte[12];
            BitConverter.TryWriteBytes(vendorBytes[..4], ebx);
            BitConverter.TryWriteBytes(vendorBytes[4..8], edx);
            BitConverter.TryWriteBytes(vendorBytes[8..12], ecx);
            string vendor = System.Text.Encoding.ASCII.GetString(vendorBytes).Trim('\0', ' ');

            (eax, _, _, _) = X86Base.CpuId(1, 0);
            byte baseFamily = (byte)((eax >> 8) & 0xF);
            byte baseModel = (byte)((eax >> 4) & 0xF);
            byte family = baseFamily == 0xF ? (byte)(baseFamily + ((eax >> 20) & 0xFF)) : baseFamily;
            byte model = baseFamily is 0x6 or 0xF ? (byte)(baseModel | (((eax >> 16) & 0xF) << 4)) : baseModel;
            byte stepping = (byte)(eax & 0xF);

            Span<byte> brandBytes = stackalloc byte[48];
            for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                (eax, ebx, ecx, edx) = X86Base.CpuId((int)leaf, 0);
                int offset = (int)(leaf - 0x80000002) * 16;
                BitConverter.TryWriteBytes(brandBytes.Slice(offset, 4), eax);
                BitConverter.TryWriteBytes(brandBytes.Slice(offset + 4, 4), ebx);
                BitConverter.TryWriteBytes(brandBytes.Slice(offset + 8, 4), ecx);
                BitConverter.TryWriteBytes(brandBytes.Slice(offset + 12, 4), edx);
            }

            string brand = System.Text.Encoding.ASCII.GetString(brandBytes).Trim('\0', ' ');
            return new CpuIdResult(vendor, brand, family, model, stepping);
        }
        catch
        {
            return new CpuIdResult(string.Empty, string.Empty, 0, 0, 0);
        }
    }
}
