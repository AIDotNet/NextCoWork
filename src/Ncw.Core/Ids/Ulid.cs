using System.Security.Cryptography;

namespace Ncw.Core.Ids;

/// <summary>
/// ULID(Crockford base32:10 位时间 + 16 位随机),与 2.x 的 <c>shared/util/id.ts</c> 相同的格式,
/// 3.0 新建的记录和 2.x 留下的记录可以混排。同一毫秒内随机部分递增,保证单调。
/// </summary>
public static class Ulid
{
    private const string Encoding = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly object Gate = new();
    private static long _lastTime = -1;
    private static readonly byte[] LastRandom = new byte[16];

    public static string New(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        Span<char> chars = stackalloc char[26];
        lock (Gate)
        {
            if (now == _lastTime)
            {
                Bump(LastRandom);
            }
            else
            {
                _lastTime = now;
                Span<byte> bytes = stackalloc byte[16];
                RandomNumberGenerator.Fill(bytes);
                for (var i = 0; i < 16; i++)
                {
                    LastRandom[i] = (byte)(bytes[i] % 32);
                }
            }

            var time = now;
            for (var i = 9; i >= 0; i--)
            {
                chars[i] = Encoding[(int)(time % 32)];
                time /= 32;
            }

            for (var i = 0; i < 16; i++)
            {
                chars[10 + i] = Encoding[LastRandom[i]];
            }
        }

        return new string(chars);
    }

    /// <summary>带前缀的可读 id(<c>ws_01J8…</c>),与 2.x 的 prefixedId 相同。</summary>
    public static string Prefixed(string prefix, TimeProvider? clock = null) => $"{prefix}_{New(clock)}";

    private static void Bump(byte[] digits)
    {
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            if (digits[i] < 31)
            {
                digits[i]++;
                return;
            }

            digits[i] = 0;
        }
    }
}
