using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HunterPdf.Core;

static class SigningIntegrationProof
{
    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "signing-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var helper = Path.Combine(root, "App.Advanced", "worker.py");
        var location = new AdvancedEngineLocation(python, helper);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "interactive-features.pdf");
        var source = Path.Combine(work, "unsigned-signature-field-source.pdf");
        var pfx = Path.Combine(work, "temporary-signer.pfx");
        var certificate = Path.Combine(work, "test-public-certificate.der");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }
        async Task Rejected<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { checks.Add(message); return; }
            throw new Exception(message);
        }

        try
        {
            File.Copy(fixture, source);
            var fixtureHash = RecoveryStore.Fingerprint(fixture);
            var sourceHash = RecoveryStore.Fingerprint(source);
            var sourceBytes = File.ReadAllBytes(source);
            CreateDisposablePfx(python, pfx, certificate, password);

            var store = new RecoveryStore(Path.Combine(work, "recovery"));
            using var session = await DocumentSession.OpenAsync(worker, store, source);
            Check(session.SignatureCount >= 1, "Selected source includes an empty signature field");
            var revision = session.Revision;
            var dirty = session.IsDirty;
            var lastSave = session.LastSavePath;
            var canUndo = session.CanUndo;
            var canRedo = session.CanRedo;

            var destination = Path.Combine(work, "signed-copy.pdf");
            var result = await session.CreateSignedCopyAsync(location, destination, pfx, password, revision);
            Check(File.Exists(destination) && RecoveryStore.Fingerprint(destination).Equals(result.Sha256, StringComparison.OrdinalIgnoreCase),
                "Signed copy publishes the exact SHA-256-validated candidate");
            Check(result.IntegrityValid && result.CryptographicallyValid && result.SignatureCount >= 1,
                "Sidecar integrity validation and native signature-count checks agree");
            Check(!result.CertificateTrustEstablished && !result.RevocationEstablished && !result.TrustedTimestamp,
                "Returned signature status does not claim certificate trust, revocation, or trusted time");
            Check(session.Revision == revision && session.IsDirty == dirty && session.LastSavePath == lastSave &&
                  session.CanUndo == canUndo && session.CanRedo == canRedo,
                "Signing export leaves revision, dirty, undo, redo, and save state unchanged");
            Check(RecoveryStore.Fingerprint(source) == sourceHash && RecoveryStore.Fingerprint(fixture) == fixtureHash,
                "Signing preserves both selected source bytes and the frozen reference PDF");
            Check(!Directory.EnumerateDirectories(work, ".signed-copy.pdf.signing-*").Any(),
                "Private signing staging directory is removed after publication");

            using (var signedSession = await DocumentSession.OpenAsync(worker,
                       new RecoveryStore(Path.Combine(work, "signed-recovery")), destination))
                Check(signedSession.PageCount == session.PageCount && signedSession.SignatureCount >= 1,
                    "Native PDFium reopens the signed output with the same pages and a signature");
            IndependentlyVerify(python, destination, certificate);
            checks.Add("Independent pyHanko verification validates the final signed file and whole-file byte range");

            var existing = Path.Combine(work, "existing-output.pdf");
            var sentinel = System.Text.Encoding.UTF8.GetBytes("preserve existing output");
            File.WriteAllBytes(existing, sentinel);
            await Rejected<IOException>(() => session.CreateSignedCopyAsync(location, existing, pfx, password, revision),
                "Existing signed-copy destination is refused");
            Check(File.ReadAllBytes(existing).SequenceEqual(sentinel), "Existing destination contents remain unchanged");

            var badPasswordOutput = Path.Combine(work, "bad-password-output.pdf");
            var wrongPassword = password + "wrong";
            try
            {
                await session.CreateSignedCopyAsync(location, badPasswordOutput, pfx, wrongPassword, revision);
                throw new Exception("Wrong PFX password was accepted.");
            }
            catch (InvalidOperationException error)
            {
                Check(!error.Message.Contains(password, StringComparison.Ordinal) && !error.Message.Contains(wrongPassword, StringComparison.Ordinal),
                    "Wrong-password refusal does not expose credential material");
            }
            Check(!File.Exists(badPasswordOutput), "Wrong password publishes no signed output");

            var tamperedOutput = Path.Combine(work, "tampered-candidate-output.pdf");
            session.SigningProbe = (stage, _, candidate) =>
            {
                if (stage == SigningStage.BeforePublish) File.AppendAllText(candidate, "tampered");
            };
            try
            {
                await Rejected<IOException>(
                    () => session.CreateSignedCopyAsync(location, tamperedOutput, pfx, password, revision),
                    "A candidate changed after validation is rejected");
            }
            finally { session.SigningProbe = null; }
            Check(!File.Exists(tamperedOutput), "Tampered candidate is never published");

            var driftedSourceOutput = Path.Combine(work, "drifted-source-output.pdf");
            session.SigningProbe = (stage, _, _) =>
            {
                if (stage == SigningStage.BeforePublish) File.AppendAllText(source, "changed");
            };
            try
            {
                await Rejected<DocumentConflictException>(
                    () => session.CreateSignedCopyAsync(location, driftedSourceOutput, pfx, password, revision),
                    "A selected source changed during signing and is rejected");
            }
            finally
            {
                session.SigningProbe = null;
                File.WriteAllBytes(source, sourceBytes);
            }
            Check(!File.Exists(driftedSourceOutput) && RecoveryStore.Fingerprint(source) == sourceHash,
                "Source drift publishes nothing and the disposable source is restored byte-for-byte");

            var racingOutput = Path.Combine(work, "racing-output.pdf");
            var raceSentinel = System.Text.Encoding.UTF8.GetBytes("competing signed output");
            session.SigningProbe = (stage, _, _) =>
            {
                if (stage == SigningStage.BeforePublish) File.WriteAllBytes(racingOutput, raceSentinel);
            };
            try
            {
                await Rejected<IOException>(
                    () => session.CreateSignedCopyAsync(location, racingOutput, pfx, password, revision),
                    "A destination created during signing is not overwritten");
            }
            finally { session.SigningProbe = null; }
            Check(File.ReadAllBytes(racingOutput).SequenceEqual(raceSentinel),
                "Create-only publication preserves a competing destination");

            var staleOutput = Path.Combine(work, "stale-output.pdf");
            var stagingBefore = Directory.EnumerateDirectories(work, ".stale-output.pdf.signing-*", SearchOption.TopDirectoryOnly).Count();
            await Rejected<InvalidOperationException>(() => session.CreateSignedCopyAsync(location, staleOutput, pfx, password, revision - 1),
                "Stale document revision is rejected before signing");
            Check(!File.Exists(staleOutput) &&
                  Directory.EnumerateDirectories(work, ".stale-output.pdf.signing-*", SearchOption.TopDirectoryOnly).Count() == stagingBefore,
                "Stale revision creates no candidate or staging directory");

            var canceledOutput = Path.Combine(work, "canceled-output.pdf");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Rejected<OperationCanceledException>(
                    () => session.CreateSignedCopyAsync(location, canceledOutput, pfx, password, revision, canceled.Token),
                    "Pre-canceled signing never starts or publishes a candidate");
            }
            Check(!File.Exists(canceledOutput) && session.Revision == revision && !session.IsDirty,
                "Cancellation leaves the output and session unchanged");

            var lateCanceledOutput = Path.Combine(work, "late-canceled-output.pdf");
            var cancelReachedPublish = false;
            using (var canceled = new CancellationTokenSource())
            {
                session.SigningProbe = (stage, _, _) =>
                {
                    if (stage == SigningStage.BeforePublish)
                    {
                        cancelReachedPublish = true;
                        canceled.Cancel();
                    }
                };
                try
                {
                    await Rejected<OperationCanceledException>(
                        () => session.CreateSignedCopyAsync(location, lateCanceledOutput, pfx, password, revision, canceled.Token),
                        "Cancellation after candidate validation prevents final publication");
                }
                finally { session.SigningProbe = null; }
            }
            Check(cancelReachedPublish && !File.Exists(lateCanceledOutput),
                "Post-validation cancellation is observed before the create-only move");
            Check(RecoveryStore.Fingerprint(source) == sourceHash, "All refusal and cancellation paths preserve selected source bytes");

            File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} signing integration checks. Evidence: {work}");
        }
        finally
        {
            // The disposable private key is removed even when any assertion fails.
            try { if (File.Exists(pfx)) File.Delete(pfx); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void CreateDisposablePfx(string python, string pfx, string certificate, string password)
    {
        const string script = """
            import datetime, sys
            from pathlib import Path
            from cryptography import x509
            from cryptography.hazmat.primitives import hashes, serialization
            from cryptography.hazmat.primitives.asymmetric import rsa
            from cryptography.x509.oid import NameOID
            from cryptography.hazmat.primitives.serialization import pkcs12
            password = sys.stdin.readline().rstrip("\n")
            key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
            now = datetime.datetime.now(datetime.timezone.utc)
            subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Disposable signing integration test")])
            cert = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key())
                    .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(days=1))
                    .not_valid_after(now + datetime.timedelta(days=30))
                    .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
                    .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=True, key_encipherment=False,
                        data_encipherment=False, key_agreement=False, key_cert_sign=True, crl_sign=True,
                        encipher_only=False, decipher_only=False), critical=True).sign(key, hashes.SHA256()))
            Path(sys.argv[1]).write_bytes(pkcs12.serialize_key_and_certificates(
                b"temporary signing proof", key, cert, None,
                serialization.BestAvailableEncryption(password.encode("utf-8"))))
            Path(sys.argv[2]).write_bytes(cert.public_bytes(serialization.Encoding.DER))
            """;
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(pfx);
        start.ArgumentList.Add(certificate);
        using var process = Process.Start(start) ?? throw new Exception("Disposable signing credential generation could not start.");
        process.StandardInput.WriteLine(password);
        process.StandardInput.Close();
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Disposable signing credential generation failed.");
    }

    static void IndependentlyVerify(string python, string pdf, string certificate)
    {
        const string script = """
            import sys
            from pathlib import Path
            from asn1crypto import x509
            from pyhanko.pdf_utils.reader import PdfFileReader
            from pyhanko.sign.validation import validate_pdf_signature
            from pyhanko_certvalidator import ValidationContext
            pdf, certificate = Path(sys.argv[1]), Path(sys.argv[2])
            cert = x509.Certificate.load(certificate.read_bytes())
            with pdf.open("rb") as stream:
                reader = PdfFileReader(stream, strict=True)
                assert not reader.encrypted and len(reader.embedded_signatures) == 1
                status = validate_pdf_signature(reader.embedded_signatures[0],
                    signer_validation_context=ValidationContext(trust_roots=[cert], allow_fetching=False, revocation_mode="soft-fail"))
                byte_range = list(reader.embedded_signatures[0].sig_object["/ByteRange"])
                assert status.intact and status.valid
                assert len(byte_range) == 4 and byte_range[0] == 0
                assert byte_range[2] + byte_range[3] == pdf.stat().st_size
            """;
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(pdf);
        start.ArgumentList.Add(certificate);
        using var process = Process.Start(start) ?? throw new Exception("Independent signature verification could not start.");
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Independent signature verification failed.");
    }
}
