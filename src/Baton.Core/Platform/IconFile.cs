namespace Baton.Core.Platform;

/// <summary>Reads the frames of a .ico file (the directory at its start), to pick the one drawn for a given size.</summary>
public static class IconFile
{
    /// <summary>The frame of exactly <paramref name="size"/> pixels, else the smallest larger one, else the largest; null if unreadable.</summary>
    public static (int Size, byte[] Data)? Frame(byte[] ico, int size)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1)
            return null;
        var frames = new List<(int Size, int Offset, int Length)>();
        for (var i = 0; i < BitConverter.ToUInt16(ico, 4); i++)
        {
            var entry = 6 + 16 * i;
            if (entry + 16 > ico.Length)
                return null;
            var width = ico[entry] == 0 ? 256 : ico[entry];
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            if (offset < 0 || length <= 0 || (long)offset + length > ico.Length)
                return null;
            frames.Add((width, offset, length));
        }
        if (frames.Count == 0)
            return null;
        var best = frames.Where(f => f.Size >= size).OrderBy(f => f.Size).Cast<(int Size, int Offset, int Length)?>().FirstOrDefault()
            ?? frames.MaxBy(f => f.Size);
        return (best.Size, ico.AsSpan(best.Offset, best.Length).ToArray());
    }
}
