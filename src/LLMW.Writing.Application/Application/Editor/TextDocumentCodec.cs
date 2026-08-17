using System.Text;
using LLMW.Writing.Contracts.Ipc;

namespace LLMW.Writing.Application.Editor;

public sealed record TextDecodeResult(string LogicalText, bool HadUtf8Bom, bool HadCarriageReturn);

public static class TextDocumentCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static EditorResult<TextDecodeResult> TryDecode(ReadOnlySpan<byte> bytes)
    {
        var slice = bytes;
        var hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (hadBom)
        {
            slice = bytes[3..];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(slice);
        }
        catch (DecoderFallbackException)
        {
            return EditorResult<TextDecodeResult>.Fail(IpcErrorCodes.EditorEncodingUnsupported);
        }

        var hadCr = text.Contains('\r');
        var logical = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        return EditorResult<TextDecodeResult>.Ok(new TextDecodeResult(logical, hadBom, hadCr));
    }

    public static byte[] EncodeUtf8NoBomLf(string logicalText)
    {
        ArgumentNullException.ThrowIfNull(logicalText);
        var lf = logicalText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        return Utf8NoBom.GetBytes(lf);
    }
}
