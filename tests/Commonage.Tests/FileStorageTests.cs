using Commonage.Web.Media;

namespace Commonage.Tests;

public class FileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "commonage-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_file_can_be_deleted_while_it_is_being_read()
    {
        var storage = new LocalFileStorage(_root);
        await storage.SaveAsync("originals/abc", new MemoryStream([1, 2, 3]));

        using (var reader = storage.OpenRead("originals/abc"))
        {
            storage.Delete("originals/abc");
            Assert.Equal(1, reader.ReadByte());
        }

        Assert.Throws<FileNotFoundException>(() => storage.OpenRead("originals/abc"));
    }

    [Fact]
    public void Keys_cannot_point_outside_the_storage_directory()
    {
        var storage = new LocalFileStorage(_root);

        Assert.Throws<ArgumentException>(() => storage.OpenRead("../secrets.txt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
