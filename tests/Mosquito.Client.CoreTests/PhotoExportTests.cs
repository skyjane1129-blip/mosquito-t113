using Mosquito.Client.Core;

internal static class PhotoExportTests
{
    public static async Task RunAsync(Action<bool, string> assert, string root)
    {
        var captured = new DateTimeOffset(2026, 9, 16, 3, 18, 50, TimeSpan.Zero);
        CloudCaptureRecord Record(int i, string device) => new(Guid.NewGuid(), "MOSQUITO-T113-DEV", "Complete", "BOARD_4G",
            captured.AddMinutes(i), true, 10, 10, null, null, null) { DeviceId = device };
        var localPhoto = Path.Combine(root, "local-photo.jpg");
        await File.WriteAllBytesAsync(localPhoto, [1, 2, 3]);
        var local = Record(0, "MQ-SH-009") with { LocalPhotoPath = localPhoto };
        var analysed = Record(1, "MQ-SH-001") with { Analysis = new EggAnalysis("Completed", "r1", 0.25, captured) { EggCount = 3 } };
        var plain = Record(2, "MQ-SH-002");
        var missing = Record(3, "MQ-SH-003");
        var downloads = new List<string>();
        Task<CloudCaptureDetail> Detail(Guid id, CancellationToken _)
        {
            if (id == analysed.Id) return Task.FromResult(new CloudCaptureDetail(analysed, "http://x/photo-a", null) { AnnotatedDownloadUrl = "http://x/annotated-a" });
            if (id == plain.Id) return Task.FromResult(new CloudCaptureDetail(plain, "http://x/photo-b", null));
            throw new HttpRequestException("404 not found");
        }
        Task<byte[]> Download(string url, CancellationToken _) { downloads.Add(url); return Task.FromResult(new byte[] { 0xFF, 0xD8, (byte)url.Length }); }
        var folder = Path.Combine(root, "export");
        var progress = new List<PhotoExportProgress>();
        var result = await PhotoExporter.ExportAsync([local, analysed, plain, missing], folder, Detail, Download, new Progress<PhotoExportProgress>(progress.Add), default);
        await Task.Delay(50); // Progress<T> posts asynchronously
        assert(result.Photos == 3 && result.Annotated == 1 && result.Failures.Count == 1 && result.Failures[0].Record.Id == missing.Id,
            $"export counts photos, annotated and failures ({result.Photos}/{result.Annotated}/{result.Failures.Count})");
        assert(File.Exists(Path.Combine(folder, "MQ-SH-009_20260916_111850.jpg")) && (await File.ReadAllBytesAsync(Path.Combine(folder, "MQ-SH-009_20260916_111850.jpg"))).SequenceEqual(new byte[] { 1, 2, 3 }),
            "local photo copied under its photo name without a cloud call");
        assert(File.Exists(Path.Combine(folder, "MQ-SH-001_20260916_111950.jpg")) && File.Exists(Path.Combine(folder, "MQ-SH-001_20260916_111950_蚊卵标注.jpg")),
            "analysed record exports the original and the annotated picture");
        assert(File.Exists(Path.Combine(folder, "MQ-SH-002_20260916_112050.jpg")) && !File.Exists(Path.Combine(folder, "MQ-SH-002_20260916_112050_蚊卵标注.jpg")),
            "record without analysis exports only the original");
        assert(downloads.Count == 3 && !downloads.Any(u => u.Contains("MQ-SH-009")), "only cloud records are downloaded");
        var csv = await File.ReadAllLinesAsync(result.CsvPath);
        assert(csv.Length == 4 && csv.Skip(1).All(l => l.Contains("_20260916_")) && !csv.Any(l => l.Contains("MQ-SH-003")),
            "CSV lists the exported records only, photo name first");
        assert(result.Summary.Contains("3 张原图") && result.Summary.Contains("1 张蚊卵标注图") && result.Summary.Contains("1 条失败"), $"summary text ({result.Summary})");

        var cancelled = false;
        try
        {
            using var cts = new CancellationTokenSource();
            await PhotoExporter.ExportAsync([plain, analysed], Path.Combine(root, "export-cancel"), (id, t) => { cts.Cancel(); return Detail(id, t); }, Download, null, cts.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        assert(cancelled, "cancellation stops the export");
    }
}
