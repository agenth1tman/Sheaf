using PdfSharp.Pdf;
using Sheaf.Core.Pdf;
using Sheaf.Core.Rendering;

namespace Sheaf.Core.Tests;

public class SecurityTests
{
    const string Password = "s3cret pass";

    [Fact]
    public async Task ProtectedFileNeedsItsPasswordAndUnlocks()
    {
        using var temp = new TempFolder();
        string source = TestPdfs.WithPages(temp.File("a.pdf"), 2);

        using (PdfDocument document = PdfFiles.OpenForModify(source))
        {
            PdfSecurity.Protect(document, Password);
            PdfFiles.Save(document, temp.File("locked.pdf"));
        }

        var refused = Assert.Throws<PasswordException>(() => PdfFiles.OpenForImport(temp.File("locked.pdf")));
        Assert.Contains("password-protected", refused.Message);
        var wrong = Assert.Throws<PasswordException>(() => PdfFiles.OpenForModify(temp.File("locked.pdf"), "nope"));
        Assert.Contains("doesn’t unlock", wrong.Message);
        await Assert.ThrowsAsync<PasswordException>(() => PdfPreview.OpenAsync(temp.File("locked.pdf")));

        using (PdfDocument document = PdfFiles.OpenForModify(temp.File("locked.pdf"), Password))
        {
            PdfSecurity.RemoveProtection(document);
            PdfFiles.Save(document, temp.File("open.pdf"));
        }

        using PdfDocument unlocked = PdfFiles.OpenForImport(temp.File("open.pdf"));
        Assert.Equal(2, unlocked.PageCount);
        using var preview = await PdfPreview.OpenAsync(temp.File("locked.pdf"), Password);
        Assert.Equal(2, preview.PageCount);
    }
}
