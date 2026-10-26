using HunterPdf.Core;

static class PdfOpenErrorProof
{
    internal static async Task RunAsync(string root, string worker)
    {
        var encrypted = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "password-aes256.pdf");
        var malformed = Path.Combine(root, "fixtures", "corpus-v1", "corpus", "malformed.pdf");
        var recovery = Path.Combine(root, "artifacts", "pdf-open-error-proof-" + Guid.NewGuid().ToString("N"));
        var encryptedHash = RecoveryStore.Fingerprint(encrypted);
        var malformedHash = RecoveryStore.Fingerprint(malformed);
        using (var client = new WorkerClient(worker))
        {
            client.Start();
            async Task<Response> Open(string path, string? password)
            {
                var id = Guid.NewGuid();
                return await client.SendAsync(Wire.Create(id, 0, "Open", new { Path = path, Password = password }));
            }

            var missingPassword = await Open(encrypted, null);
            if (missingPassword.Success || missingPassword.ErrorCode != WireErrorCodes.PasswordRequired)
                throw new Exception("PDFium's native password error was not sent as the password-required discriminator.");
            var incorrectPassword = await Open(encrypted, "definitely-incorrect");
            if (incorrectPassword.Success || incorrectPassword.ErrorCode != WireErrorCodes.PasswordRequired)
                throw new Exception("An incorrect encrypted-PDF password did not retain the password-required discriminator.");
            var malformedResponse = await Open(malformed, null);
            if (malformedResponse.Success || malformedResponse.ErrorCode is not null ||
                string.IsNullOrWhiteSpace(malformedResponse.Error) ||
                malformedResponse.Error.Contains("password", StringComparison.OrdinalIgnoreCase))
                throw new Exception("A malformed PDF was misclassified as password-required or returned no informative open error.");
        }

        try
        {
            using var _ = await DocumentSession.OpenAsync(worker,
                new RecoveryStore(Path.Combine(recovery, "password")), encrypted);
            throw new Exception("Opening an encrypted PDF without a password unexpectedly succeeded.");
        }
        catch (DocumentPasswordRequiredException) { }

        try
        {
            using var _ = await DocumentSession.OpenAsync(worker,
                new RecoveryStore(Path.Combine(recovery, "malformed")), malformed);
            throw new Exception("Opening a malformed PDF unexpectedly succeeded.");
        }
        catch (DocumentPasswordRequiredException)
        {
            throw new Exception("The core mapped a malformed PDF open failure to a password-required exception.");
        }
        catch (InvalidOperationException error) when (!string.IsNullOrWhiteSpace(error.Message)) { }

        if (RecoveryStore.Fingerprint(encrypted) != encryptedHash || RecoveryStore.Fingerprint(malformed) != malformedHash)
            throw new Exception("An open-error proof modified a frozen source PDF.");
        Directory.CreateDirectory(recovery);
        File.WriteAllText(Path.Combine(recovery, "results.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            PasswordMissingTyped = true, WrongPasswordTyped = true, MalformedGeneric = true,
            SessionPasswordMapping = true, SessionMalformedMapping = true,
            EncryptedSourceSHA256 = encryptedHash, MalformedSourceSHA256 = malformedHash
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS PDF open error classification: native password errors are typed; malformed input remains informative and generic; frozen sources are unchanged.");
    }
}
