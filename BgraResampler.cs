namespace RemoteScreen;

sealed unsafe class BgraResampler
{
    readonly int _sourceWidth;
    readonly int _sourceHeight;
    readonly int _width;
    readonly int _height;
    readonly Sample[] _columns;
    readonly Sample[] _rows;

    public BgraResampler(int sourceWidth, int sourceHeight, int width, int height)
    {
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _width = width;
        _height = height;
        _columns = CreateSamples(sourceWidth, width);
        _rows = CreateSamples(sourceHeight, height);
    }

    public void Copy(nint data, int pitch, Span<uint> target)
    {
        bool sameSize = _sourceWidth == _width && _sourceHeight == _height;
        bool exactHalf = _sourceWidth == _width * 2 && _sourceHeight == _height * 2;
        for (int row = 0; row < _height; row++)
        {
            Span<uint> output = target.Slice(row * _width, _width);
            if (sameSize)
            {
                new ReadOnlySpan<uint>((byte*)data + row * pitch, _width).CopyTo(output);
                continue;
            }

            if (exactHalf)
            {
                uint* first = (uint*)((byte*)data + 2 * row * pitch);
                uint* second = (uint*)((byte*)first + pitch);
                for (int column = 0; column < _width; column++)
                {
                    int source = column * 2;
                    output[column] = Average(first[source], first[source + 1], second[source], second[source + 1]);
                }
                continue;
            }

            Sample vertical = _rows[row];
            uint* top = (uint*)((byte*)data + vertical.First * pitch);
            uint* bottom = (uint*)((byte*)data + vertical.Second * pitch);
            for (int column = 0; column < _width; column++)
            {
                Sample horizontal = _columns[column];
                uint upper = Interpolate(top[horizontal.First], top[horizontal.Second], horizontal.Weight);
                uint lower = Interpolate(bottom[horizontal.First], bottom[horizontal.Second], horizontal.Weight);
                output[column] = Interpolate(upper, lower, vertical.Weight);
            }
        }
    }

    static Sample[] CreateSamples(int source, int target)
    {
        Sample[] samples = new Sample[target];
        for (int index = 0; index < target; index++)
        {
            // Map pixel centers to the source and keep the fractional offset on an 8-bit scale.
            int position = (int)((2L * index + 1) * source * 128 / target) - 128;
            int first = Math.Clamp(position >> 8, 0, source - 1);
            samples[index] = new Sample(first, Math.Min(first + 1, source - 1), (uint)(position & 255));
        }
        return samples;
    }

    static uint Average(uint a, uint b, uint c, uint d)
    {
        // The two 16-bit lanes let us average independent colour components without carries between them.
        const uint lanes = 0x00ff00ff;
        const uint round = 0x00020002;
        uint low = (a & lanes) + (b & lanes) + (c & lanes) + (d & lanes) + round;
        uint high = ((a >> 8) & lanes) + ((b >> 8) & lanes) + ((c >> 8) & lanes) + ((d >> 8) & lanes) + round;
        return ((low >> 2) & lanes) | (((high >> 2) & lanes) << 8);
    }

    static uint Interpolate(uint a, uint b, uint weight)
    {
        const uint lanes = 0x00ff00ff;
        const uint round = 0x00800080;
        uint complement = 256 - weight;
        uint low = (a & lanes) * complement + (b & lanes) * weight + round;
        uint high = ((a >> 8) & lanes) * complement + ((b >> 8) & lanes) * weight + round;
        return ((low >> 8) & lanes) | (((high >> 8) & lanes) << 8);
    }

    readonly record struct Sample(int First, int Second, uint Weight);
}
