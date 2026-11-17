using System.Buffers.Binary;

namespace RemoteScreen;

sealed record ScreenFrame(int Width, int Height, byte[][] Tiles)
{
    public const int TileSize = 256;
    const int HeaderLength = 6;
    const int TileHeaderLength = 6;

    public int PacketSize(ScreenFrame? baseline)
    {
        int size = HeaderLength;
        for (int index = 0; index < Tiles.Length; index++)
        {
            if (Changed(baseline, index))
            {
                size += TileHeaderLength + Tiles[index].Length;
            }
        }
        return size;
    }

    public void WritePacket(ScreenFrame? baseline, Span<byte> packet)
    {
        // All integers are little-endian: width, height, tile count, then (index, length, zlib RGBA).
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[2..], (ushort)Height);
        int offset = HeaderLength;
        ushort count = 0;
        for (int index = 0; index < Tiles.Length; index++)
        {
            if (!Changed(baseline, index))
            {
                continue;
            }

            byte[] tile = Tiles[index];
            BinaryPrimitives.WriteUInt16LittleEndian(packet[offset..], (ushort)index);
            BinaryPrimitives.WriteUInt32LittleEndian(packet[(offset + 2)..], (uint)tile.Length);
            tile.CopyTo(packet[(offset + TileHeaderLength)..]);
            offset += TileHeaderLength + tile.Length;
            count++;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(packet[4..], count);
    }

    bool Changed(ScreenFrame? baseline, int index) =>
        baseline is null || baseline.Width != Width || baseline.Height != Height ||
        !ReferenceEquals(Tiles[index], baseline.Tiles[index]);
}
