namespace OmniCore.Infrastructure;

/// <summary>
/// SHA-256 puro e implementado sin dependencias (FIPS 180-4), verificado por tests contra
/// los vectores NIST y contra una implementación independiente (tests/OmniCore.Tests/Sha256Tests.cs).
/// El Artifact Store usa este algoritmo determinista para el content addressing (ADR-0001 §5):
/// el digest es del texto en bytes UTF-8, exactamente los bytes que <c>File.WriteAllText</c>
/// escribe en el blob, de modo que la dirección del artifact ES el sha256 de su contenido.
/// <para>
/// Nota de corrección (M4): la versión anterior operaba los words como <c>long</c> con
/// extensión de signo, por lo que todo word con el bit alto puesto rotaba mal y el digest
/// no era SHA-256 real (fallaba incluso el vector NIST de "abc"); además hasheaba chars
/// truncados a 8 bits en lugar de bytes UTF-8. La aritmética ahora es <c>uint</c> y el
/// mensaje se codifica en UTF-8. Los artifacts escritos antes de esta corrección tienen
/// direcciones calculadas con el digest defectuoso: no se pierden (el sweep del GC nunca
/// borra referenciados), pero una lectura con el digest correcto los reporta como corruptos.
/// </para>
/// </summary>
public sealed class Sha256
{
    private static readonly uint[] K = BuildK();

    private static uint[] BuildK()
    {
        var hexes = new string[]
        {
            "428a2f98", "71374491", "b5c0fbcf", "e9b5dba5", "3956c25b", "59f111f1", "923f82a4", "ab1c5ed5",
            "d807aa98", "12835b01", "243185be", "550c7dc3", "72be5d74", "80deb1fe", "9bdc06a7", "c19bf174",
            "e49b69c1", "efbe4786", "0fc19dc6", "240ca1cc", "2de92c6f", "4a7484aa", "5cb0a9dc", "76f988da",
            "983e5152", "a831c66d", "b00327c8", "bf597fc7", "c6e00bf3", "d5a79147", "06ca6351", "14292967",
            "27b70a85", "2e1b2138", "4d2c6dfc", "53380d13", "650a7354", "766a0abb", "81c2c92e", "92722c85",
            "a2bfe8a1", "a81a664b", "c24b8b70", "c76c51a3", "d192e819", "d6990624", "f40e3585", "106aa070",
            "19a4c116", "1e376c08", "2748774c", "34b0bcb5", "391c0cb3", "4ed8aa4a", "5b9cca4f", "682e6ff3",
            "748f82ee", "78a5636f", "84c87814", "8cc70208", "90befffa", "a4506ceb", "bef9a3f7", "c67178f2",
        };
        var k = new uint[hexes.Length];
        for (var i = 0; i < hexes.Length; i++)
        {
            k[i] = Convert.ToUInt32(hexes[i], 16);
        }

        return k;
    }

    /// <summary>Devuelve el resumen SHA-256 de la entrada en hex minúscula.</summary>
    public static string Hex(string content)
    {
        var digest = Words(content);
        var parts = new string[digest.Length * 8];
        var idx = 0;
        foreach (var w in digest)
        {
            parts[idx++] = HexNibble[(int) (w >> 28) & 0xF];
            parts[idx++] = HexNibble[(int) (w >> 24) & 0xF];
            parts[idx++] = HexNibble[(int) (w >> 20) & 0xF];
            parts[idx++] = HexNibble[(int) (w >> 16) & 0xF];
            parts[idx++] = HexNibble[(int) (w >> 12) & 0xF];
            parts[idx++] = HexNibble[(int) (w >> 8) & 0xF];
            parts[idx++] = HexNibble[(int) (w >> 4) & 0xF];
            parts[idx++] = HexNibble[(int) w & 0xF];
        }

        return string.Join("", parts);
    }

    /// <summary>Devuelve el resumen SHA-256 como 8 palabras de 32 bits (big-endian del digest).</summary>
    public static int[] Bytes(string content)
    {
        var words = Words(content);
        var ints = new int[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            ints[i] = unchecked((int) words[i]);
        }

        return ints;
    }

    private static uint[] Words(string content)
    {
        var msg = System.Text.Encoding.UTF8.GetBytes(content ?? "");
        var w = Pad(msg);
        var h = new uint[] { H0[0], H0[1], H0[2], H0[3], H0[4], H0[5], H0[6], H0[7] };

        for (var chunk = 0; chunk < w.Length; chunk += 16)
        {
            var x = new uint[64];
            for (var i = 0; i < 16; i++)
            {
                x[i] = w[chunk + i];
            }

            for (var i = 16; i < 64; i++)
            {
                var s0 = Rotr(x[i - 15], 7) ^ Rotr(x[i - 15], 18) ^ (x[i - 15] >> 3);
                var s1 = Rotr(x[i - 2], 17) ^ Rotr(x[i - 2], 19) ^ (x[i - 2] >> 10);
                x[i] = x[i - 16] + s0 + x[i - 7] + s1;
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
                var ch = (e & f) ^ (~e & g);
                var t1 = hh + s1 + ch + K[i] + x[i];
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

    /// <summary>Rotación de 32 bits: los shifts de uint son lógicos (sin signo), sin extensión.</summary>
    private static uint Rotr(uint value, int bits) => (value >> bits) | (value << (32 - bits));

    private static readonly uint[] H0 =
    [
        Convert.ToUInt32("6a09e667", 16), Convert.ToUInt32("bb67ae85", 16), Convert.ToUInt32("3c6ef372", 16),
        Convert.ToUInt32("a54ff53a", 16), Convert.ToUInt32("510e527f", 16), Convert.ToUInt32("9b05688c", 16),
        Convert.ToUInt32("1f83d9ab", 16), Convert.ToUInt32("5be0cd19", 16),
    ];

    private static readonly string[] HexNibble =
    ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "a", "b", "c", "d", "e", "f"];

    /// <summary>Convierte el mensaje (bytes UTF-8) + padding FIPS en palabras big-endian de 32 bits.</summary>
    private static uint[] Pad(byte[] msg)
    {
        var bitLen = (long) msg.Length * 8;
        var paddedLen = ((msg.Length + 8) / 64 + 1) * 64;
        var padded = new byte[paddedLen];
        for (var i = 0; i < msg.Length; i++)
        {
            padded[i] = msg[i];
        }

        padded[msg.Length] = 0x80;
        for (var i = 0; i < 8; i++)
        {
            padded[paddedLen - 1 - i] = (byte) ((bitLen >> (8 * i)) & 0xFF);
        }

        var words = new uint[paddedLen / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = ((uint) padded[4 * i] << 24) | ((uint) padded[4 * i + 1] << 16)
                | ((uint) padded[4 * i + 2] << 8) | padded[4 * i + 3];
        }

        return words;
    }
}
