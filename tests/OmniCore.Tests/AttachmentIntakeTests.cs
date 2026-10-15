using System.Text;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using OmniCore.Client;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class AttachmentIntakeTests
{
    [Fact]
    public void Attachment_manifest_encodes_a_deserialized_interface_collection()
    {
        var reference = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('a', 64)), 4,
            "text/plain", ArtifactKind.Other, Sensitivity.Sensitive) { FileName = "note.txt" };
        IReadOnlyList<ArtifactRef> references = new List<ArtifactRef> { reference };

        var encoded = AttachmentReferences.Encode(references);
        var decoded = AttachmentReferences.Decode(encoded);

        Assert.Equal(reference, Assert.Single(decoded));
    }

    [Fact]
    public void Pdf_and_docx_extractions_are_versioned_CAS_snapshots_linked_from_the_original()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-document-snapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var artifacts = new FileArtifactStore(root);
            var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(), artifacts);
            var pdf = server.ImportUserAttachment(CreatePdf("PDF extracted fixture"), "fixture.pdf");
            var docx = server.ImportUserAttachment(CreateDocx("DOCX extracted fixture"), "fixture.docx");
            Assert.Equal("omnicore.pdfpig-content-order/0.1.16-v1", pdf.ExtractorId);
            Assert.Equal("omnicore.docx-ooxml-text/1", docx.ExtractorId);
            Assert.Equal("PDF extracted fixture", artifacts.GetText(Assert.IsType<ArtifactRef>(pdf.ExtractedTextSnapshot).Hash));
            Assert.Contains("DOCX extracted fixture", artifacts.GetText(Assert.IsType<ArtifactRef>(docx.ExtractedTextSnapshot).Hash));
            Assert.Equal("PDF extracted fixture", Assert.IsType<DocumentBlock>(AttachmentContentMaterializer.Read(pdf, artifacts)).ExtractedText);
            Assert.Contains("DOCX extracted fixture", Assert.IsType<DocumentBlock>(AttachmentContentMaterializer.Read(docx, artifacts)).ExtractedText);

            using (var store = new SqliteEventStore(Path.Combine(root, "journal.db")))
            {
                var session = SessionId.New();
                var run = TestRun.Open(store, session);
                new EventStream(store, EventCodecs.Create(), session).Append(new UserInputReceived(
                    run.RunId, "document snapshots", null, Attachments: [pdf, docx]));
                var decoded = store.ReadFrom(session, 1).Select(EventCodecs.Create().Decode)
                    .OfType<UserInputReceived>().Single();
                Assert.Equal(pdf.ExtractedTextSnapshot!.Hash, decoded.Attachments![0].ExtractedTextSnapshot!.Hash);
                Assert.Equal(docx.ExtractedTextSnapshot!.Hash, decoded.Attachments[1].ExtractedTextSnapshot!.Hash);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateDocx(string text)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>"
                + System.Security.SecurityElement.Escape(text) + "</w:t></w:r></w:p></w:body></w:document>");
        }
        return output.ToArray();
    }

    private static byte[] CreatePdf(string text)
    {
        var content = "BT /F1 12 Tf 40 200 Td (" + text + ") Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + Encoding.ASCII.GetByteCount(content) + " >>\nstream\n" + content + "\nendstream",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    [Fact]
    public void Sqlite_input_with_imported_binary_attachment_can_be_read_back_with_its_file_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-attachment-sqlite-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var previousDirectory = Environment.CurrentDirectory;
        var previousData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var previousConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            var journal = Path.Combine(root, "journal.db");
            using var store = new SqliteEventStore(journal);
            var artifacts = new FileArtifactStore(Path.Combine(root, "artifacts"));
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), artifacts);
            byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 0, 1, 0, 0, 0, 1];
            var imported = server.ImportUserAttachment(png, "fixture.png");
            var attachmentManifest = AttachmentReferences.Encode(new[] { imported });
            var command = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"binary attachment fixture\",\"mode\":\"plan\",\"attachments\":"
                + attachmentManifest + "}");

            var ack = ((ITrustedUserActionClient)server).SendUserAction(command, CancellationToken.None);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
            var session = Assert.IsType<SessionId>(server.LastSessionId());

            // ReadFrom is intentionally outside the Host's durable-outcome safety catch: preserve
            // the concrete SQLite/envelope exception so metadata serialization failures are not
            // disguised as an ambiguous command result.
            var events = store.ReadFrom(session, 1);
            var decodedInput = events.Select(EventCodecs.Create().Decode).OfType<UserInputReceived>().Single();
            var storedArtifactRef = events.SelectMany(evt => evt.ArtifactRefs)
                .Single(reference => reference.Hash == imported.Hash);
            Assert.Equal("fixture.png", storedArtifactRef.FileName);
            Assert.Single(decodedInput.Attachments!);
            Assert.Equal("fixture.png", decodedInput.Attachments![0].FileName);
            Assert.Equal(png.Length, decodedInput.Attachments[0].Size);
            Assert.NotNull(artifacts.GetBytes(decodedInput.Attachments[0].Hash, png.Length));
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", previousConfig);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Rejects_secret_bearing_document_before_raw_bytes_enter_CAS_but_accepts_binary_image_without_OCR()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-attachment-intake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var artifacts = new FileArtifactStore(root);
            var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(), artifacts);
            var secret = Encoding.UTF8.GetBytes("Authorization: Bearer abcdEFGHijklMNOP1234\nprivate notes");
            Assert.Throws<InvalidDataException>(() => server.ImportUserAttachment(secret, "notes.txt"));
            var hash = ContentHash.Sha256(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(secret)).ToLowerInvariant());
            Assert.Null(artifacts.GetBytes(hash, secret.Length));

            byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 0, 1, 0, 0, 0, 1];
            var image = server.ImportUserAttachment(png, "picture.png");
            Assert.Equal("image/png", image.MediaType);
            Assert.Equal(png.Length, image.Size);
            Assert.NotNull(artifacts.GetBytes(image.Hash, image.Size));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
