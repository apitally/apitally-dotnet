namespace Apitally;

using System.Security.Cryptography;
using System.Text;

sealed class InstanceLock : IDisposable
{
    private const int MaxSlots = 100;
    private const int MaxLockAgeSeconds = 24 * 60 * 60;

    public Guid InstanceUuid { get; }
    private readonly FileStream? _lockFile;

    private InstanceLock(Guid uuid, FileStream? lockFile)
    {
        InstanceUuid = uuid;
        _lockFile = lockFile;
    }

    public static InstanceLock Create(string clientId, string env) =>
        Create(clientId, env, Path.Combine(Path.GetTempPath(), "apitally"));

    internal static InstanceLock Create(string clientId, string env, string lockDir)
    {
        try
        {
            Directory.CreateDirectory(lockDir);
        }
        catch
        {
            return new InstanceLock(Guid.NewGuid(), null);
        }

        var appEnvHash = GetAppEnvHash(clientId, env);

        for (var slot = 0; slot < MaxSlots; slot++)
        {
            var lockPath = Path.Combine(lockDir, $"instance_{appEnvHash}_{slot}.lock");
            FileStream? file = null;
            try
            {
                file = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );

                var lastWriteTime = File.GetLastWriteTimeUtc(lockPath);
                var tooOld = (DateTime.UtcNow - lastWriteTime).TotalSeconds > MaxLockAgeSeconds;

                using var reader = new StreamReader(file, leaveOpen: true);
                var existingUuid = reader.ReadToEnd().Trim();

                if (Guid.TryParse(existingUuid, out var uuid) && !tooOld)
                {
                    return new InstanceLock(uuid, file);
                }

                var newUuid = Guid.NewGuid();
                file.SetLength(0);
                using var writer = new StreamWriter(file, leaveOpen: true);
                writer.Write(newUuid.ToString());
                writer.Flush();

                return new InstanceLock(newUuid, file);
            }
            catch
            {
                file?.Dispose();
                continue;
            }
        }

        return new InstanceLock(Guid.NewGuid(), null);
    }

    private static string GetAppEnvHash(string clientId, string env)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId}:{env}"));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    public void Dispose() => _lockFile?.Dispose();
}
