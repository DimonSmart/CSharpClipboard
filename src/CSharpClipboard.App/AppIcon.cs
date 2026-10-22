using Avalonia.Controls;

namespace CSharpClipboard.App;

internal static class AppIcon
{
    public static WindowIcon Create()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)32); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(40 + 4096 + 128); writer.Write(22);
        writer.Write(40); writer.Write(32); writer.Write(64); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(4096); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
        {
            var page = x is >= 8 and <= 23 && y is >= 5 and <= 25;
            var line = page && x is >= 11 and <= 20 && (y == 10 || y == 15 || y == 20);
            writer.Write(line ? 0xFF2354BAu : page ? 0xFFF3F6FFu : 0xFF2354BAu);
        }
        writer.Write(new byte[128]); writer.Flush();
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
