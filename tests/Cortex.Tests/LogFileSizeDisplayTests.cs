using Cortex.Models;
using Xunit;

namespace Cortex.Tests;

public class LogFileSizeDisplayTests
{
    [Fact]
    public void UnknownControllerSizeIsNotShownAsEmptyOrFourGiB()
    {
        var file = new LogFile { IsControllerFile = true, FileSizeBytes = uint.MaxValue };
        Assert.Equal("(size unavailable)", file.FileSizeMbDisplay);
        file.FileSizeBytes = 0;
        Assert.Contains("0", file.FileSizeMbDisplay);
        Assert.DoesNotContain("unavailable", file.FileSizeMbDisplay);
    }

    [Fact]
    public void LocalFilesRetainNumericSizes()
    {
        var file = new LogFile { FileSizeBytes = uint.MaxValue };
        Assert.DoesNotContain("unavailable", file.FileSizeMbDisplay);
    }

    [Fact]
    public void SizeAndSourceChangesNotifyDisplay()
    {
        var file = new LogFile();
        int notifications = 0;
        file.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LogFile.FileSizeMbDisplay)) ++notifications;
        };
        file.FileSizeBytes = uint.MaxValue;
        file.IsControllerFile = true;
        Assert.Equal(2, notifications);
        Assert.Equal("(size unavailable)", file.FileSizeMbDisplay);
    }
}
