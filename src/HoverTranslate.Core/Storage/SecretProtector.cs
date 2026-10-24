using System.Runtime.InteropServices;
using System.Text;

namespace HoverTranslate.Core.Storage;

/// <summary>
/// Шифрование секретов (ключ API) средствами Windows DPAPI в области «текущий пользователь»: зашифрованное значение
/// расшифровывается только этой учётной записью на этом компьютере. В базе лежит base64 шифртекста, открытого вида на диске нет.
/// </summary>
public static partial class SecretProtector
{
    // Примесь: другая программа того же пользователя с тем же вызовом DPAPI без неё этот шифртекст не расшифрует.
    private static readonly byte[] Entropy = "HoverTranslate.Secret.v1"u8.ToArray();

    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(in DataBlob dataIn, nint description, in DataBlob entropy, nint reserved,
        nint prompt, uint flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(in DataBlob dataIn, nint description, in DataBlob entropy, nint reserved,
        nint prompt, uint flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    /// <summary>Шифрует строку; результат — base64.</summary>
    public static string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        return Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plain), protect: true));
    }

    /// <summary>Расшифровывает результат <see cref="Protect"/>; null, если значение испорчено или зашифровано другим пользователем.</summary>
    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedValue), protect: false));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static unsafe byte[] Transform(byte[] input, bool protect)
    {
        fixed (byte* inputPtr = input)
        fixed (byte* entropyPtr = Entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (nint)inputPtr };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropyPtr };
            bool ok = protect
                ? CryptProtectData(inBlob, 0, entropyBlob, 0, 0, CryptProtectUiForbidden, out var result)
                : CryptUnprotectData(inBlob, 0, entropyBlob, 0, 0, CryptProtectUiForbidden, out result);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var output = new byte[result.Size];
                Marshal.Copy(result.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                LocalFree(result.Data);
            }
        }
    }

    /// <summary>Ошибка DPAPI (код Win32 в <see cref="Exception.HResult"/> не используется — только в сообщении).</summary>
    public sealed class CryptographicException(int win32Error)
        : Exception($"DPAPI: ошибка Windows {win32Error}");
}
