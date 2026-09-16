namespace Mosquito.Cloud.Api;

public sealed class AuthOptions
{
    public string Username { get; set; } = "demo";
    public string Password { get; set; } = "change-me";
    public string SigningKey { get; set; } = "development-only-change-this-signing-key";
    public int TokenMinutes { get; set; } = 480;
}

public sealed class DeviceAuthOptions
{
    public string ApiKey { get; set; } = "development-device-key-change-me";
}

public sealed class RepositoryOptions
{
    public string Provider { get; set; } = "InMemory";
    public string ConnectionString { get; set; } = string.Empty;
}

// Server-side mosquito/egg detection (ai/analyze.py + the trained YOLO weights). Paths are relative to the
// content root. PythonPath empty = use ai/.venv/Scripts/python.exe when present, else "python" from PATH.
public sealed class AnalysisOptions
{
    public bool Enabled { get; set; } = true;
    public string PythonPath { get; set; } = string.Empty;
    public string ScriptPath { get; set; } = "ai/analyze.py";
    // Combined pipeline (2026-09-16): mosquitoes = YOLO11m detector + YOLO11s-cls classifier, eggs = YOLO26s r3
    // on 224 px tiles. ModelPath only has to exist (the script resolves the three weight files itself).
    public string ModelPath { get; set; } = "ai/models/mosquito_egg_r3.pt";
    public string ModelVersion { get; set; } = "combined_20260916";
    // Egg confidence threshold (0.35 when cup-wall scratches get counted as eggs).
    public double Confidence { get; set; } = 0.25;
    // Tile edge for the egg pass, expressed at RefWidth pixels of image width (224 for the 3264-wide board camera).
    public int EggTile { get; set; } = 224;
    public int RefWidth { get; set; } = 3264;
    // Mosquito classifier threshold (raise to 0.8-0.9 for fewer false alarms) and candidate threshold.
    public double MosquitoClassifierThreshold { get; set; } = 0.7;
    public double MosquitoCandidateConfidence { get; set; } = 0.15;
    public int TimeoutSeconds { get; set; } = 600;
}

public sealed class StorageOptions
{
    public string Provider { get; set; } = "Local";
    public string LocalRoot { get; set; } = "local-storage";
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:5080";
    public string TokenSigningKey { get; set; } = "development-only-storage-signing-key";
    public int UploadUrlMinutes { get; set; } = 15;
    public int DownloadUrlMinutes { get; set; } = 5;
    public string Region { get; set; } = "cn-hangzhou";
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string EcsRamRoleName { get; set; } = string.Empty;
}
