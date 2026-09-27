namespace Lumen.Storage.Tests;

/// <summary>Writes a throwaway generic credential to the current user's Credential Manager and removes it.</summary>
public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void WritesReadsAndDeletesAGenericCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialStore();
        var name = $"Lumen/Test/{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Read(name));

            store.Write(name, "sk-or-v1-test-value");
            Assert.Equal("sk-or-v1-test-value", store.Read(name));

            store.Write(name, "replaced");
            Assert.Equal("replaced", store.Read(name));
        }
        finally
        {
            Assert.True(store.Delete(name));
        }

        Assert.Null(store.Read(name));
        Assert.False(store.Delete(name));
    }
}
