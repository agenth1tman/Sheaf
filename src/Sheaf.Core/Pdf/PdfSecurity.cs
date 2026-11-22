using PdfSharp.Pdf;

namespace Sheaf.Core.Pdf;

public static class PdfSecurity
{
    /// <summary>
    /// Encrypts with AES-128; the same password opens the file and lifts every restriction.
    /// AES-256 (PDF 2.0) would be stronger, but Windows' own PDF engine and older readers can't open it.
    /// </summary>
    public static void Protect(PdfDocument document, string password)
    {
        document.SecuritySettings.UserPassword = password;
        document.SecuritySettings.OwnerPassword = password;
        document.SecurityHandler.SetEncryptionToV4UsingAES(encryptMetadata: true);
    }

    /// <summary>Saves the document without encryption. It must have been opened with its password.</summary>
    public static void RemoveProtection(PdfDocument document) =>
        document.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
}
