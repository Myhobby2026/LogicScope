using System.Globalization;
using System.IO.Compression;
using LogicScope.Core.Models;
using LogicScope.Export;
using Xunit;

namespace LogicScope.Tests;

public sealed class ExportFormatTests
{
    [Fact]
    public async Task NativeLgsRoundTripsSamplesAndRejectsCorruption()
    {
        await InTempDirectory(async directory =>
        {
            var path = Path.Combine(directory, "capture.lgs");
            var samples = new ushort[] { 0x0000, 0x1234, 0xFFFF, 0x8001 };
            await LgsFile.SaveAsync(path, new SampleWindow(0, 2_000_000, samples));
            var loaded = await LgsFile.LoadAsync(path);
            Assert.Equal(2_000_000u, loaded.SampleRateHz);
            Assert.Equal(16, loaded.ChannelCount);
            Assert.Equal(samples, loaded.Samples);

            var bytes = await File.ReadAllBytesAsync(path);
            bytes[36] ^= 0x20; // stored CRC-32 field
            await File.WriteAllBytesAsync(path, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => LgsFile.LoadAsync(path));
        });
    }

    [Fact]
    public async Task SigrokSrIsZipWithV2MetadataAndPackedLogic()
    {
        await InTempDirectory(async directory =>
        {
            var path = Path.Combine(directory, "capture.sr");
            await SigrokSrExport.WriteAsync(path,
                new SampleWindow(0, 2_000_000, new ushort[] { 1, 0x8001 }));
            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "version");
            Assert.Contains(archive.Entries, entry => entry.FullName == "metadata");
            Assert.Contains(archive.Entries, entry => entry.FullName == "logic-1");
            using var reader = new StreamReader(archive.GetEntry("version")!.Open());
            Assert.Equal("2", (await reader.ReadToEndAsync()).Trim());
            using var metadata = new StreamReader(archive.GetEntry("metadata")!.Open());
            var text = await metadata.ReadToEndAsync();
            Assert.Contains("unitsize=2", text, StringComparison.Ordinal);
            Assert.Contains("samplerate=2 MHz", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task VcdCsvAndSaleaeBinaryContainExpectedData()
    {
        await InTempDirectory(async directory =>
        {
            var window = new SampleWindow(0, 1_000_000,
                new ushort[] { 0, 1, 1, 0x8001 });
            var vcdPath = Path.Combine(directory, "capture.vcd");
            VcdExport.Write(vcdPath, window);
            var vcd = await File.ReadAllTextAsync(vcdPath);
            Assert.Contains("$timescale 1 ns $end", vcd, StringComparison.Ordinal);
            Assert.Contains("$var wire 1 c15 D15 $end", vcd, StringComparison.Ordinal);
            Assert.Contains("#1000", vcd, StringComparison.Ordinal);

            var csvPath = Path.Combine(directory, "capture.csv");
            await CsvExport.WriteSamplesAsync(csvPath, window);
            var csv = await File.ReadAllLinesAsync(csvPath);
            Assert.Equal("sample_index,time_seconds,D0,D1,D2,D3,D4,D5,D6,D7,D8,D9,D10,D11,D12,D13,D14,D15", csv[0]);
            var finalRow = csv[4].Split(',');
            Assert.Equal("3", finalRow[0]);
            Assert.InRange(double.Parse(finalRow[1], CultureInfo.InvariantCulture), 2.999e-6, 3.001e-6);
            Assert.Equal("1", finalRow[2]);
            Assert.Equal("1", finalRow[17]);

            var binaryPath = Path.Combine(directory, "capture.bin");
            await SaleaeRawBinaryExport.WriteAsync(binaryPath, window);
            Assert.Equal(new byte[] { 0, 0, 1, 0, 1, 0, 1, 128 },
                await File.ReadAllBytesAsync(binaryPath));
            Assert.True(File.Exists(binaryPath + ".import.txt"));
        });
    }

    private static async Task InTempDirectory(Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LogicScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
