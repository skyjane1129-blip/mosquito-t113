namespace Mosquito.Client.Core;

public sealed record PhotoExportProgress(int Done, int Total, string Current);

public sealed record PhotoExportFailure(CloudCaptureRecord Record, string Error);

public sealed record PhotoExportResult(string Folder, int Photos, int Annotated, string CsvPath, IReadOnlyList<PhotoExportFailure> Failures)
{
    public string Summary =>
        $"已导出 {Photos} 张原图" + (Annotated > 0 ? $"、{Annotated} 张蚊卵标注图" : "") + $"，并附 CSV；" +
        (Failures.Count == 0 ? "全部成功。" : $"{Failures.Count} 条失败：{string.Join("；", Failures.Take(3).Select(f => $"{f.Record.PhotoName} {f.Error}"))}{(Failures.Count > 3 ? "…" : "")}");
}

// Batch export of selected records into one folder: <PhotoName>.jpg for each photo, <...>_蚊卵标注.jpg when a
// server-side analysis exists, plus a CSV of the same records. One record failing (expired link, network)
// is reported and the others continue. Records with a local photo are copied without touching the cloud.
public static class PhotoExporter
{
    public static async Task<PhotoExportResult> ExportAsync(
        IReadOnlyList<CloudCaptureRecord> records,
        string folder,
        Func<Guid, CancellationToken, Task<CloudCaptureDetail>>? fetchDetail,
        Func<string, CancellationToken, Task<byte[]>> download,
        IProgress<PhotoExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var photos = 0; var annotated = 0;
        var failures = new List<PhotoExportFailure>();
        var exported = new List<CloudCaptureRecord>();
        for (var index = 0; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records[index];
            progress?.Report(new PhotoExportProgress(index, records.Count, record.PhotoName));
            try
            {
                var current = record;
                if (record.LocalPhotoPath is not null)
                {
                    File.Copy(record.LocalPhotoPath, Path.Combine(folder, record.PhotoName), true);
                    photos++;
                }
                else
                {
                    if (fetchDetail is null) throw new InvalidOperationException("需要登录云端才能下载照片。");
                    var detail = await fetchDetail(record.Id, cancellationToken);
                    current = detail.Capture;
                    if (string.IsNullOrWhiteSpace(detail.PhotoDownloadUrl)) throw new InvalidOperationException("云端没有这条记录的照片。");
                    await File.WriteAllBytesAsync(Path.Combine(folder, current.PhotoName), await download(detail.PhotoDownloadUrl, cancellationToken), cancellationToken);
                    photos++;
                    if (!string.IsNullOrWhiteSpace(detail.AnnotatedDownloadUrl))
                    {
                        await File.WriteAllBytesAsync(Path.Combine(folder, current.AnnotatedPhotoName), await download(detail.AnnotatedDownloadUrl, cancellationToken), cancellationToken);
                        annotated++;
                    }
                }
                exported.Add(current);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(new PhotoExportFailure(record, ex.Message)); }
        }
        progress?.Report(new PhotoExportProgress(records.Count, records.Count, "写入 CSV"));
        var csvPath = Path.Combine(folder, $"采集记录_{DateTimeOffset.UtcNow.ToOffset(BeijingTime.Offset):yyyyMMdd_HHmmss}.csv");
        await CsvExporter.ExportAsync(csvPath, exported, cancellationToken);
        return new PhotoExportResult(folder, photos, annotated, csvPath, failures);
    }
}
