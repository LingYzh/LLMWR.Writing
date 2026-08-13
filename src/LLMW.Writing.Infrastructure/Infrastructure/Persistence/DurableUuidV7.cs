using System.Security.Cryptography;

namespace LLMW.Writing.Infrastructure.Persistence;

internal static class DurableUuidV7
{
    public static Guid Create()
    {
        Span<byte> uuidBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(uuidBytes);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var index = 5; index >= 0; index--)
        {
            uuidBytes[index] = (byte)timestamp;
            timestamp >>= 8;
        }

        uuidBytes[6] = (byte)((uuidBytes[6] & 0x0f) | 0x70);
        uuidBytes[8] = (byte)((uuidBytes[8] & 0x3f) | 0x80);
        return new Guid(uuidBytes, bigEndian: true);
    }
}
