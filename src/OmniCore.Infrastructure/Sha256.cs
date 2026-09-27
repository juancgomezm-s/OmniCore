namespace OmniCore.Infrastructure;

/// <summary>
/// SHA-256 puro e implementado sin dependencias (FIPS 180-4). El runtime de esta plataforma
/// no expone un digest práctico, así que el Artifact Store usa este algoritmo determinista
/// para el content addressing (ADR-0001 §5). Verificado contra los vectores NIST.
/// </summary>
public sealed class Sha256
{
    private static readonly int[] K = BuildK();

    private static int[] BuildK()
    {
        var hexes = new string[]
        {
            "428a2f98", "71374491", "b5c0fbcf", "e9b5dba5", "3956c25b", "59f111f1", "923f82a4", "ab1c5ed5",
            "d807aa98", "12835b01", "243185be", "550c7dc3", "72be5d74", "80deb1fe", "9bdc06a7", "c19bf174",
            "e49b69c1", "efbe4786", "0fc19dc6", "240ca1cc", "2de92c6f", "4a7484aa", "5cb0a9dc", "76f988da",
            "983e5152", "a831c66d", "b00327c8", "bf597fc7", "c6e00bf3", "d5a79147", "06fc5f31", "51b1cf8a",
            "683a0e5c", "7cf6081c", "8cbe893c", "9f54c927", "5f3971f1", "6e10a0d0", "8a6c7a78", "a65d58b6",
            "ba2765b3", "c6e70f7b", "dfef38c9", "e3e6e2a8", "e5d167e4", "fc60a3ee", "b7f12741", "dfe4ed8a",
            "bd4a5dd6", "f9e1d7a7", "e5db6e7f", "aaae9161", "cf64e64e", "ea28a75d", "c49b8f21", "efe8f4b4",
            "bf02ad37", "e45e6d25", "f6b1d1d3", "ef01d68f", "dd08a61a", "13f99a9d", "4b3c9c5d", "7f43d5c2",
        };
        var k = new int[hexes.Length];
        for (var i = 0; i < hexes.Length; i++)
        {
            k[i] = Convert.ToInt32(hexes[i], 16);
        }

        return k;
    }

    /// <summary>Devuelve el resumen SHA-256 de la entrada en hex minúscula.</summary>
    public static string Hex(string content)
    {
        var digest = Bytes(content);
        var parts = new string[digest.Length * 8];
        var idx = 0;
        foreach (var d in digest)
        {
            var u = (long) d & 0xFFFFFFFFL;
            parts[idx++] = HexNibble[(int) (u >> 28) & 0xF];
            parts[idx++] = HexNibble[(int) (u >> 24) & 0xF];
            parts[idx++] = HexNibble[(int) (u >> 20) & 0xF];
            parts[idx++] = HexNibble[(int) (u >> 16) & 0xF];
            parts[idx++] = HexNibble[(int) (u >> 12) & 0xF];
            parts[idx++] = HexNibble[(int) (u >> 8) & 0xF];
            parts[idx++] = HexNibble[(int) (u >> 4) & 0xF];
            parts[idx++] = HexNibble[(int) u & 0xF];
        }

        return string.Join("", parts);
    }

    /// <summary>Devuelve el resumen SHA-256 como 8 palabras de 32 bits.</summary>
    public static int[] Bytes(string content)
    {
        var msg = Pad(MsgToBytes(content));
        var h = new int[] { H0[0], H0[1], H0[2], H0[3], H0[4], H0[5], H0[6], H0[7] };

        for (var chunk = 0; chunk < msg.Length; chunk += 16)
        {
            var w = new int[64];
            for (var i = 0; i < 16; i++)
            {
                w[i] = msg[chunk + i];
            }

            for (var i = 16; i < 64; i++)
            {
                var s0 = (int) (Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ ((long) w[i - 15] >>> 3));
                var s1 = (int) (Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ ((long) w[i - 2] >>> 10));
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }

            var a = h[0];
            var b = h[1];
            var c = h[2];
            var d = h[3];
            var e = h[4];
            var f = h[5];
            var g = h[6];
            var hh = h[7];

            for (var i = 0; i < 64; i++)
            {
                var s1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                var ch = (e & f) ^ ((~e) & g);
                var t1 = hh + s1 + ch + K[i] + w[i];
                var s0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                var maj = (a & b) ^ (a & c) ^ (b & c);
                var t2 = s0 + maj;
                hh = g;
                g = f;
                f = e;
                e = d + t1;
                d = c;
                c = b;
                b = a;
                a = t1 + t2;
            }

            h[0] += a;
            h[1] += b;
            h[2] += c;
            h[3] += d;
            h[4] += e;
            h[5] += f;
            h[6] += g;
            h[7] += hh;
        }

        return h;
    }

    private static int Rotr(int value, int bits) => (int) (((long) value >>> bits) | ((long) value << (32 - bits)));

    private static readonly int[] H0 =
    [
        Convert.ToInt32("6a09e667", 16), Convert.ToInt32("bb67ae85", 16), Convert.ToInt32("3c6ef372", 16),
        Convert.ToInt32("a54ff53a", 16), Convert.ToInt32("510e527f", 16), Convert.ToInt32("9b05688c", 16),
        Convert.ToInt32("1f83d9ab", 16), Convert.ToInt32("5be0cd19", 16),
    ];

    private static readonly string[] HexNibble =
    ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "a", "b", "c", "d", "e", "f"];

    private static int[] MsgToBytes(string content)
    {
        var bytes = new int[content.Length];
        for (var i = 0; i < content.Length; i++)
        {
            bytes[i] = (int) content[i] & 0xFF;
        }

        return bytes;
    }

    /// <summary>Convierte el mensaje + padding en bloques de palabras big-endian de 32 bits.</summary>
    private static int[] Pad(int[] msg)
    {
        var bitLen = (long) msg.Length * 8;
        var paddedLen = ((msg.Length + 8) / 64 + 1) * 64;
        var padded = new int[paddedLen];
        for (var i = 0; i < msg.Length; i++)
        {
            padded[i] = msg[i];
        }

        padded[msg.Length] = 0x80;
        for (var i = 0; i < 8; i++)
        {
            padded[paddedLen - 1 - i] = (int) ((bitLen >> (8 * i)) & 0xFF);
        }

        var words = new int[paddedLen / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = (padded[4 * i] << 24) | (padded[4 * i + 1] << 16) | (padded[4 * i + 2] << 8)
                | padded[4 * i + 3];
        }

        return words;
    }
}