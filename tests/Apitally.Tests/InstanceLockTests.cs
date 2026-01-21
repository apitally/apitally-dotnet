namespace Apitally.Tests;

using Xunit;

public class InstanceLockTests : IDisposable
{
    private readonly string _tempDir;

    public InstanceLockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"apitally_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CreatesNewUUID()
    {
        var clientId = Guid.NewGuid().ToString();
        var env = "test";

        using var instanceLock = InstanceLock.Create(clientId, env, _tempDir);
        Assert.NotEqual(Guid.Empty, instanceLock.InstanceUuid);

        var hash = GetAppEnvHash(clientId, env);
        var lockFile = Path.Combine(_tempDir, $"instance_{hash}_0.lock");
        Assert.True(File.Exists(lockFile));
    }

    [Fact]
    public void ReusesExistingUUID()
    {
        var clientId = Guid.NewGuid().ToString();
        var env = "test";

        Guid firstUuid;
        using (var lock1 = InstanceLock.Create(clientId, env, _tempDir))
        {
            firstUuid = lock1.InstanceUuid;
        }
        using var lock2 = InstanceLock.Create(clientId, env, _tempDir);
        Assert.Equal(firstUuid, lock2.InstanceUuid);
    }

    [Fact]
    public void DifferentEnvsGetDifferentUUIDs()
    {
        var clientId = Guid.NewGuid().ToString();

        using var lock1 = InstanceLock.Create(clientId, "env1", _tempDir);
        using var lock2 = InstanceLock.Create(clientId, "env2", _tempDir);
        Assert.NotEqual(lock1.InstanceUuid, lock2.InstanceUuid);
    }

    [Fact]
    public void MultipleSlots()
    {
        var clientId = Guid.NewGuid().ToString();
        var env = "test";

        using var lock1 = InstanceLock.Create(clientId, env, _tempDir);
        using var lock2 = InstanceLock.Create(clientId, env, _tempDir);
        using var lock3 = InstanceLock.Create(clientId, env, _tempDir);

        Assert.NotEqual(lock1.InstanceUuid, lock2.InstanceUuid);
        Assert.NotEqual(lock2.InstanceUuid, lock3.InstanceUuid);
        Assert.NotEqual(lock1.InstanceUuid, lock3.InstanceUuid);

        var hash = GetAppEnvHash(clientId, env);
        Assert.True(File.Exists(Path.Combine(_tempDir, $"instance_{hash}_0.lock")));
        Assert.True(File.Exists(Path.Combine(_tempDir, $"instance_{hash}_1.lock")));
        Assert.True(File.Exists(Path.Combine(_tempDir, $"instance_{hash}_2.lock")));
    }

    [Fact]
    public void OverwritesOldUUID()
    {
        var clientId = Guid.NewGuid().ToString();
        var env = "test";
        var hash = GetAppEnvHash(clientId, env);

        var oldUuid = "550e8400-e29b-41d4-a716-446655440000";
        var lockFile = Path.Combine(_tempDir, $"instance_{hash}_0.lock");
        File.WriteAllText(lockFile, oldUuid);
        var oldTime = DateTime.UtcNow.AddHours(-25);
        File.SetLastWriteTimeUtc(lockFile, oldTime);

        using var instanceLock = InstanceLock.Create(clientId, env, _tempDir);
        Assert.NotEqual(Guid.Parse(oldUuid), instanceLock.InstanceUuid);
        Assert.NotEqual(Guid.Empty, instanceLock.InstanceUuid);
    }

    [Fact]
    public void OverwritesInvalidUUID()
    {
        var clientId = Guid.NewGuid().ToString();
        var env = "test";
        var hash = GetAppEnvHash(clientId, env);

        var lockFile = Path.Combine(_tempDir, $"instance_{hash}_0.lock");
        File.WriteAllText(lockFile, "not-a-valid-uuid");

        Guid uuid;
        using (var instanceLock = InstanceLock.Create(clientId, env, _tempDir))
        {
            uuid = instanceLock.InstanceUuid;
            Assert.NotEqual(Guid.Empty, uuid);
        }
        var content = File.ReadAllText(lockFile).Trim();
        Assert.Equal(uuid.ToString(), content);
    }

    private static string GetAppEnvHash(string clientId, string env)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{clientId}:{env}")
        );
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}
